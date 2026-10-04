using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Files;

public sealed class FilesystemShadowTargetConnector : IShadowTargetRecoveryConnector
{
    private readonly HashSet<string> _preparedRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _prepareLock = new();

    public ConnectorId Id { get; } = new("files");
    public string Version => "0.1.0";

    public Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        cancellationToken.ThrowIfCancellationRequested();
        var root = ConnectorPathUtilities.ResolveRoot(context.ConnectorContext);
        var key = $"{root}|{context.RunId.Value:N}";
        lock (_prepareLock)
        {
            if (_preparedRuns.Contains(key))
            {
                return Task.CompletedTask;
            }

            var relativeRunPath = context.RunId.Value.ToString("N", CultureInfo.InvariantCulture);
            if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativeRunPath, out var runRoot) || Directory.Exists(runRoot) || File.Exists(runRoot))
            {
                throw new ProjectionConnectorException("Filesystem shadow run path is invalid or already exists.");
            }

            Directory.CreateDirectory(runRoot);
            if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativeRunPath, out var resolvedRunRoot) ||
                !string.Equals(runRoot, resolvedRunRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new ProjectionConnectorException("Filesystem shadow run directory escaped the configured root.");
            }

            var internalDirectory = Path.Combine(runRoot, ".proofshift-internal");
            Directory.CreateDirectory(internalDirectory);
            using (var identityConnection = new SqliteConnection($"Data Source={Path.Combine(internalDirectory, "identities.sqlite")};Mode=ReadWriteCreate;Cache=Private;Pooling=False"))
            {
                identityConnection.Open();
                using var identityCommand = identityConnection.CreateCommand();
                identityCommand.CommandText = "CREATE TABLE identities (node_key TEXT NOT NULL, identity_hash BLOB NOT NULL, logical_path TEXT NOT NULL, storage_path TEXT NOT NULL, PRIMARY KEY (node_key, identity_hash), UNIQUE (node_key, storage_path))";
                identityCommand.ExecuteNonQuery();
            }

            _preparedRuns.Add(key);
        }

        return Task.CompletedTask;
    }

    public async Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runRoot = ResolveRunRoot(request.Context);
        var pathField = request.Selector.Properties.GetValueOrDefault("pathField") ??
            (request.Selector.IdentityFields.Count == 0 ? "relativePath" : request.Selector.IdentityFields[0]);
        if (!request.Record.Values.TryGetValue(pathField, out var pathNode) || pathNode is not StringValue pathValue)
        {
            throw new ProjectionConnectorException("Filesystem target path field must exist and be a string.");
        }

        if (request.Selector.IdentityFields.Count != 1 || !string.Equals(request.Selector.IdentityFields[0], pathField, StringComparison.Ordinal) ||
            request.NodeKey.Contains('/') || request.NodeKey.Contains('\\') || request.NodeKey is "." or "..")
        {
            throw new ProjectionConnectorException("Filesystem shadow targets require one path identity field and a single-segment graph node key.");
        }

        var logicalPath = NormalizeTargetPath(pathValue.Value);
        var relativePath = $"{request.NodeKey}/{logicalPath}";
        if (!ConnectorPathUtilities.TryResolveContainedPath(runRoot, relativePath, out var fullPath))
        {
            throw new ProjectionConnectorException("Filesystem target path escapes the isolated shadow run directory.");
        }

        await ReserveIdentityAsync(runRoot, request.NodeKey, request.Record.Artifact.Identity, logicalPath, relativePath, cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(fullPath) ?? runRoot;
        Directory.CreateDirectory(directory);
        if (!ConnectorPathUtilities.TryResolveContainedPath(runRoot, relativePath, out var recheckedPath) ||
            !ConnectorPathUtilities.IsContained(runRoot, recheckedPath))
        {
            throw new ProjectionConnectorException("Filesystem target path escaped the isolated shadow run directory.");
        }

        var binary = request.Record.Values.FirstOrDefault(pair => pair.Value is BinaryReferenceValue);
        var temporaryPath = Path.Combine(directory, $".proofshift-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (binary.Value is BinaryReferenceValue binaryReference)
                {
                    if (request.OpenBinaryReadAsync is null)
                    {
                        throw new ProjectionConnectorException("Filesystem target requires a streaming source binary resolver.");
                    }

                    await using var input = await request.OpenBinaryReadAsync(binary.Key, cancellationToken).ConfigureAwait(false);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long totalLength = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, count);
                        totalLength = checked(totalLength + count);
                    }

                    var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    if (totalLength != binaryReference.ContentLength || !string.Equals(actualHash, binaryReference.Sha256, StringComparison.Ordinal))
                    {
                        throw new ProjectionConnectorException("Projected binary content did not match its source length and SHA-256.");
                    }
                }
                else
                {
                    var json = Encoding.UTF8.GetBytes(string.Join("\n", request.Record.Values
                        .Where(pair => pair.Key != pathField)
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}={pair.Value}")));
                    await output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!ConnectorPathUtilities.TryResolveContainedPath(runRoot, relativePath, out recheckedPath) ||
                !string.Equals(fullPath, recheckedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new ProjectionConnectorException("Filesystem target path changed during projection.");
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (ProjectionConnectorException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (IOException)
        {
            TryDelete(temporaryPath);
            throw new ProjectionConnectorException("Filesystem target path already exists or could not be materialized.");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw new ProjectionConnectorException("Filesystem shadow artifact write failed.");
        }
    }

    public Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolveRunRoot(context);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ReadRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runRoot = ResolveRunRoot(request.Context);
        var indexPath = Path.Combine(runRoot, ".proofshift-internal", "identities.sqlite");
        await using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Cache=Private;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT storage_path, logical_path FROM identities WHERE node_key = $node ORDER BY storage_path COLLATE BINARY";
        command.Parameters.AddWithValue("$node", request.Context.ConnectorContext.NodeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storagePath = reader.GetString(0);
            var logicalPath = reader.GetString(1);
            if (!ConnectorPathUtilities.TryResolveContainedPath(runRoot, storagePath, out var fullPath))
            {
                throw new ProjectionConnectorException("Filesystem read-back encountered a path outside the isolated shadow run directory.");
            }

            var info = new FileInfo(fullPath);
            var (length, sha256) = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var values = new SortedDictionary<string, ValueNode>(StringComparer.Ordinal)
            {
                ["relativePath"] = new StringValue(logicalPath),
                ["size"] = new IntegerValue(length),
                ["sha256"] = new StringValue(sha256),
                ["content"] = new BinaryReferenceValue(EncodeReference(storagePath), length, sha256)
            };
            var pathField = request.Selector.Properties.GetValueOrDefault("pathField") ?? request.Selector.IdentityFields[0];
            values[pathField] = new StringValue(logicalPath);
            var identityFields = request.Selector.IdentityFields.Count == 0 ? ["relativePath"] : request.Selector.IdentityFields.ToArray();
            var identity = CreateIdentity(values, identityFields);
            var connectorContext = request.Context.ConnectorContext;
            var artifact = new ArtifactReference(
                new ArtifactId(ProjectionIdentityHash(connectorContext.SystemKey, connectorContext.EndpointKey,
                    StableArtifactIdentity.ArtifactTypeFor(request.Selector), identity)),
                new SystemId(connectorContext.SystemKey),
                new StorageEndpointId(connectorContext.EndpointKey),
                StableArtifactIdentity.ArtifactTypeFor(request.Selector),
                identity);
            yield return new RecordEnvelope(artifact, connectorContext.SemanticType, values,
                new ProvenanceMetadata(Id, new StorageEndpointId(connectorContext.EndpointKey), logicalPath,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), sha256,
                    [new KeyValuePair<string, string>("observationKind", "projection-readback")]));
        }
    }

    public async Task<ShadowRecoveryCheckpoint> CaptureRecoveryCheckpointAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        var context = request.Targets[0].Context;
        var root = ConnectorPathUtilities.ResolveRoot(context.ConnectorContext);
        var runRoot = ResolveRunRoot(context);
        var checkpointRelative = $".proofshift-recovery/{request.CheckpointId.Value:N}/contents";
        if (!ConnectorPathUtilities.TryResolveContainedPath(root, checkpointRelative, out var checkpointContents))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "Filesystem recovery path is outside the configured shadow root.");
        var checkpointDirectory = Path.GetDirectoryName(checkpointContents)!;
        if (Directory.Exists(checkpointDirectory) || File.Exists(checkpointDirectory))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "Filesystem recovery checkpoint ID already exists.");

        try
        {
            Directory.CreateDirectory(checkpointDirectory);
            await CopyDirectoryAsync(runRoot, checkpointContents, cancellationToken).ConfigureAwait(false);
            var contentHash = await HashDirectoryAsync(checkpointContents, cancellationToken).ConfigureAwait(false);
            return new ShadowRecoveryCheckpoint(request.CheckpointId, request.ShadowRunId, request.SystemId,
                request.EndpointId, Id, Version, request.GraphHash, request.BaselineFingerprint,
                request.TargetArtifactCount, checkpointRelative, contentHash, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(checkpointDirectory);
            throw;
        }
        catch (ShadowRecoveryConnectorException)
        {
            TryDeleteDirectory(checkpointDirectory);
            throw;
        }
        catch
        {
            TryDeleteDirectory(checkpointDirectory);
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "Filesystem shadow recovery checkpoint could not be captured.");
        }
    }

    public async Task ValidateRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        ValidateCheckpointBinding(request, checkpoint);
        var root = ConnectorPathUtilities.ResolveRoot(request.Targets[0].Context.ConnectorContext);
        if (!ConnectorPathUtilities.TryResolveContainedPath(root, checkpoint.Reference, out var contents) ||
            !Directory.Exists(contents))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "Filesystem shadow recovery checkpoint is unavailable.");
        var actualHash = await HashDirectoryAsync(contents, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, checkpoint.ContentSha256, StringComparison.Ordinal))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointIntegrityFailure, "Filesystem shadow recovery checkpoint failed integrity validation.");
    }

    public async Task<ShadowRecoveryMutation> ApplyControlledMutationAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        var runRoot = ResolveRunRoot(request.Targets[0].Context);
        foreach (var target in request.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeDirectory = Path.Combine(runRoot, target.Context.ConnectorContext.NodeKey);
            if (!Directory.Exists(nodeDirectory)) continue;
            foreach (var file in EnumerateFilesSafely(nodeDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoReparsePoint(runRoot, file);
                await using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(new byte[] { 0x58 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return new ShadowRecoveryMutation(1, "Appended one controlled byte to one isolated shadow file.");
            }
        }

        throw RecoveryFailure(ShadowRecoveryIssueCodes.RehearsalMutationFailed, "Filesystem recovery rehearsal found no shadow file to mutate.");
    }

    public async Task RestoreRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await ValidateRecoveryCheckpointAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var context = request.Targets[0].Context;
        var root = ConnectorPathUtilities.ResolveRoot(context.ConnectorContext);
        var runRoot = ResolveRunRoot(context);
        if (!ConnectorPathUtilities.IsContained(root, runRoot) || string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(runRoot)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "Filesystem recovery target is not an isolated child of its configured root.");
        var checkpointContents = Path.GetFullPath(Path.Combine(root, checkpoint.Reference.Replace('/', Path.DirectorySeparatorChar)));
        Directory.Delete(runRoot, recursive: true);
        try
        {
            await CopyDirectoryAsync(checkpointContents, runRoot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw RecoveryFailure(ShadowRecoveryIssueCodes.RehearsalMutationFailed, "Filesystem shadow recovery restore failed.");
        }
    }

    private static string ResolveRunRoot(ShadowTargetContext context)
    {
        var root = ConnectorPathUtilities.ResolveRoot(context.ConnectorContext);
        var relative = context.RunId.Value.ToString("N", CultureInfo.InvariantCulture);
        if (!ConnectorPathUtilities.TryResolveContainedPath(root, relative, out var runRoot) || !Directory.Exists(runRoot))
        {
            throw new ProjectionConnectorException("Filesystem shadow run directory is unavailable.");
        }

        return runRoot;
    }

    private static string NormalizeTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new ProjectionConnectorException("Filesystem target path must be relative.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..") ||
            normalized.StartsWith('/'))
        {
            throw new ProjectionConnectorException("Filesystem target path contains a traversal segment.");
        }

        return normalized;
    }

    private static string CreateIdentity(SortedDictionary<string, ValueNode> values, IEnumerable<string> fields)
    {
        var parts = new List<string>();
        foreach (var field in fields)
        {
            if (!values.TryGetValue(field, out var value) || value is not StringValue text)
            {
                throw new ProjectionConnectorException("Filesystem target identity field is missing or non-string.");
            }

            var canonical = $"string:{text.Value.Normalize(NormalizationForm.FormC)}";
            parts.Add($"{field.Length.ToString(CultureInfo.InvariantCulture)}:{field}={canonical.Length.ToString(CultureInfo.InvariantCulture)}:{canonical}");
        }

        return string.Join('|', parts);
    }

    private static string ProjectionIdentityHash(string system, string endpoint, string artifactType, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(new[] { system, endpoint, artifactType, identity }
            .Select(part => $"{part.Length.ToString(CultureInfo.InvariantCulture)}:{part}")))))
            .ToLowerInvariant();

    private static string EncodeReference(string relativePath)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"shadow-file-v1:{encoded}";
    }

    private static async Task<(long Length, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, count);
            length = checked(length + count);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private void ValidateRecoveryRequest(ShadowRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConnectorId != Id || request.Targets.Any(target => target.Context.ConnectorContext.Connector != Id ||
            target.Context.Role != SystemRole.ShadowTarget || target.Context.RunId != request.ShadowRunId))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "Filesystem recovery request does not match this shadow connector/run.");
    }

    private void ValidateCheckpointBinding(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Id != request.CheckpointId || checkpoint.ShadowRunId != request.ShadowRunId ||
            checkpoint.SystemId != request.SystemId || checkpoint.EndpointId != request.EndpointId ||
            checkpoint.ConnectorId != Id || checkpoint.ConnectorVersion != Version || checkpoint.GraphHash != request.GraphHash ||
            checkpoint.BaselineFingerprint != request.BaselineFingerprint || checkpoint.TargetArtifactCount != request.TargetArtifactCount)
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "Filesystem recovery checkpoint binding does not match the requested shadow state.");
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoint(source, source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoint(source, file);
            var target = Path.Combine(destination, Path.GetFileName(file));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoint(source, directory);
            await CopyDirectoryAsync(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> HashDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateFilesSafely(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoint(directory, file);
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            var result = await HashFileAsync(file, cancellationToken).ConfigureAwait(false);
            Append(hash, relative);
            Append(hash, result.Length.ToString(CultureInfo.InvariantCulture));
            Append(hash, result.Sha256);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> size = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        hash.AppendData(size);
        hash.AppendData(bytes);
    }

    private static void EnsureNoReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(path);
        while (ConnectorPathUtilities.IsContained(root, current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "Filesystem recovery refuses to traverse a symbolic link.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(current),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directory)
    {
        EnsureNoReparsePoint(directory, directory);
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            EnsureNoReparsePoint(directory, file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            EnsureNoReparsePoint(directory, child);
            foreach (var file in EnumerateFilesSafely(child)) yield return file;
        }
    }

    private static ShadowRecoveryConnectorException RecoveryFailure(string code, string message) => new(code, message);

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch { }
    }

    private static async Task ReserveIdentityAsync(string runRoot, string nodeKey, string identity, string logicalPath,
        string storagePath, CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(runRoot, ".proofshift-internal", "identities.sqlite");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadWrite;Cache=Private;Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO identities (node_key, identity_hash, logical_path, storage_path) VALUES ($node, $identityHash, $logicalPath, $storagePath)";
            command.Parameters.AddWithValue("$node", nodeKey);
            command.Parameters.AddWithValue("$identityHash", SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            command.Parameters.AddWithValue("$logicalPath", logicalPath);
            command.Parameters.AddWithValue("$storagePath", storagePath);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new ProjectionConnectorException("Duplicate filesystem shadow artifact identity.");
        }
        catch
        {
            throw new ProjectionConnectorException("Filesystem shadow artifact identity could not be reserved.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}