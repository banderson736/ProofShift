using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Files;

public sealed class FilesystemSourceConnector : ISourceConnector, ISourceBinaryContentResolver
{
    public ConnectorId Id { get; } = new("files");
    public string Version => "0.1.0";

    public Task<SourceInspection> InspectAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        if (!string.Equals(selector.Kind, "file-pattern", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Invalid(context, ConnectorIssueCodes.UnsupportedSelector,
                "Filesystem connector supports file-pattern selectors only."));
        }

        if (!TryGetPattern(selector, out var pattern))
        {
            return Task.FromResult(Invalid(context, ConnectorIssueCodes.InvalidPhysicalIdentifier,
                "File pattern is required and must remain relative to the endpoint root."));
        }

        if (!HasSupportedIdentity(selector))
        {
            return Task.FromResult(Invalid(context, ConnectorIssueCodes.IdentityFieldNotFound,
                "Filesystem artifact identity is the endpoint-relative path."));
        }

        try
        {
            var root = ConnectorPathUtilities.ResolveRoot(context);
            var matcher = CreatePatternMatcher(pattern);
            long fileCount = 0;
            long byteCount = 0;
            foreach (var relativePath in EnumerateMatchingFiles(root, matcher, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
                {
                    return Task.FromResult(Invalid(context, ConnectorIssueCodes.PathOutsideRoot,
                        "A matched file resolves outside the configured source root."));
                }

                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    continue;
                }

                fileCount++;
                byteCount = checked(byteCount + info.Length);
            }

            if (fileCount == 0)
            {
                return Task.FromResult(Invalid(context, ConnectorIssueCodes.SourceObjectNotFound,
                    "File pattern did not match any source files."));
            }

            return Task.FromResult(new SourceInspection(
                SourceInspectionStatus.Valid,
                identityFields: ["relativePath"],
                estimatedRecords: fileCount,
                physicalObject: pattern,
                files: fileCount,
                bytes: byteCount,
                issues: []));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectorConfigurationException exception)
        {
            return Task.FromResult(Invalid(context, exception.Code, exception.Message));
        }
        catch
        {
            return Task.FromResult(new SourceInspection(SourceInspectionStatus.Failed, issues:
            [
                new ConnectorIssue(ConnectorIssueCodes.SourceReadFailed, ConnectorIssueSeverity.Error,
                    "Filesystem inspection failed.", context.NodeKey)
            ]));
        }
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Partition.Count > 0)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PartitioningUnsupported,
                "Partitioned source reads are not implemented yet.");
        }

        if (!string.Equals(selector.Kind, "file-pattern", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector,
                "Filesystem connector supports file-pattern selectors only.");
        }

        if (!TryGetPattern(selector, out var pattern))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier,
                "File pattern is required and must remain relative to the endpoint root.");
        }

        if (!HasSupportedIdentity(selector))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound,
                "Filesystem artifact identity is the endpoint-relative path.");
        }

        string root;
        try
        {
            root = ConnectorPathUtilities.ResolveRoot(context);
        }
        catch (ConnectorConfigurationException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }

        var matcher = CreatePatternMatcher(pattern);
        foreach (var relativePath in EnumerateMatchingFiles(root, matcher, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath))
            {
                throw new ConnectorReadException(ConnectorIssueCodes.PathOutsideRoot,
                    "A matched file resolves outside the configured source root.");
            }

            var file = await ReadFileAsync(context, root, relativePath, fullPath, cancellationToken).ConfigureAwait(false);
            yield return file;
        }
    }

    private async Task<RecordEnvelope> ReadFileAsync(
        ConnectorContext context,
        string root,
        string relativePath,
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Matched source file is no longer available.");
            }

            var hash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var normalizedPath = ConnectorPathUtilities.NormalizeRelativePath(relativePath);
            var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal)
            {
                ["relativePath"] = new StringValue(normalizedPath),
                ["size"] = new IntegerValue(info.Length),
                ["modifiedAt"] = new InstantValue(modifiedAt),
                ["sha256"] = new StringValue(hash),
                ["content"] = new BinaryReferenceValue(EncodeFileReference(normalizedPath), info.Length, hash)
            };
            var artifact = new ArtifactReference(
                new ArtifactId(ArtifactIdentity.Create(context.SystemKey, context.EndpointKey, "file", normalizedPath)),
                new SystemId(context.SystemKey),
                new StorageEndpointId(context.EndpointKey),
                "file",
                normalizedPath);
            var provenance = new ProvenanceMetadata(
                Id,
                new StorageEndpointId(context.EndpointKey),
                normalizedPath,
                DateTimeOffset.UtcNow,
                hash,
                [new KeyValuePair<string, string>("observationKind", "read")]);
            return new RecordEnvelope(
                artifact,
                context.SemanticType,
                values,
                provenance,
                temporal: new TemporalMetadata(recordedAt: modifiedAt));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ConnectorReadException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Source file could not be inspected or hashed.");
        }
    }

    public ValueTask<Stream> OpenBinaryReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ArtifactReference artifact,
        BinaryReferenceValue binaryReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(binaryReference);
        cancellationToken.ThrowIfCancellationRequested();
        if (artifact.SystemId.Value != context.SystemKey || artifact.EndpointId.Value != context.EndpointKey ||
            !TryDecodeFileReference(binaryReference.Reference, out var relativePath) ||
            !string.Equals(relativePath, artifact.Identity, StringComparison.Ordinal))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound,
                "Binary reference does not belong to the supplied source artifact.");
        }

        var root = ConnectorPathUtilities.ResolveRoot(context);
        if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var fullPath) || !File.Exists(fullPath))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PathOutsideRoot,
                "Binary content reference is outside the endpoint root or unavailable.");
        }

        try
        {
            Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(stream);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed,
                "Referenced file content could not be opened.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateMatchingFiles(string root, Regex matcher, CancellationToken cancellationToken)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false
        };
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", enumerationOptions).GetEnumerator();
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Filesystem enumeration failed.");
        }

        using (enumerator)
        {
        while (TryMoveNext(enumerator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = enumerator.Current;
            var relative = ConnectorPathUtilities.NormalizeRelativePath(Path.GetRelativePath(root, path));
            if (matcher.IsMatch(relative))
            {
                yield return relative;
            }
        }
        }
    }

    private static bool TryMoveNext(IEnumerator<string> enumerator)
    {
        try
        {
            return enumerator.MoveNext();
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed,
                "Filesystem enumeration failed.");
        }
    }

    private static bool TryGetPattern(ArtifactSelector selector, out string pattern)
    {
        if (!selector.Properties.TryGetValue("pattern", out pattern!) || string.IsNullOrWhiteSpace(pattern) ||
            Path.IsPathRooted(pattern) || pattern.Replace('\\', '/').Split('/').Any(segment => segment == ".."))
        {
            pattern = string.Empty;
            return false;
        }

        return true;
    }

    private static bool HasSupportedIdentity(ArtifactSelector selector) =>
        selector.IdentityFields.Count == 0 ||
        selector.IdentityFields.Count == 1 &&
        string.Equals(selector.IdentityFields[0], "relativePath", StringComparison.Ordinal);

    private static string EncodeFileReference(string relativePath)
    {
        var normalized = ConnectorPathUtilities.NormalizeRelativePath(relativePath);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"file-v1:{encoded}";
    }

    private static bool TryDecodeFileReference(string reference, out string relativePath)
    {
        const string prefix = "file-v1:";
        relativePath = string.Empty;
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encoded = reference[prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            relativePath = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return !string.IsNullOrWhiteSpace(relativePath) && !Path.IsPathRooted(relativePath) &&
                !relativePath.Split('/').Any(segment => segment == "..");
        }
        catch
        {
            return false;
        }
    }

    private static Regex CreatePatternMatcher(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized)
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static SourceInspection Invalid(ConnectorContext context, string code, string message) =>
        new(SourceInspectionStatus.Invalid, issues:
        [
            new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey)
        ]);
}
