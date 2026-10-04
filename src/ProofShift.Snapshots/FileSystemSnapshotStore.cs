using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

public sealed partial class FileSystemSnapshotStore : IMaterializedSnapshotStore
{
    internal const int BufferSize = 64 * 1024;
    private static readonly JsonSerializerOptions StateOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _root;

    public FileSystemSnapshotStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.GetFullPath(rootPath);
    }

    public async Task<ICheckpointWriteSession> CreateAsync(CheckpointId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.Value == Guid.Empty) throw new ArgumentException("Checkpoint ID must not be empty.", nameof(id));
        Directory.CreateDirectory(_root);
        RestrictDirectory(_root);
        var directory = Path.Combine(_root, id.Value.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint ID already exists in this store.");
        Directory.CreateDirectory(Path.Combine(directory, "nodes"));
        Directory.CreateDirectory(Path.Combine(directory, "blobs"));
        Directory.CreateDirectory(Path.Combine(directory, ".temporary"));
        RestrictDirectory(directory);
        await WriteStateAsync(directory, id, CheckpointStatus.Creating, null, cancellationToken).ConfigureAwait(false);
        return new FileSystemSnapshotWriteSession(id, directory);
    }

    public async Task<ILoadedCheckpoint> OpenCompleteAsync(string checkpointIdOrPath, CancellationToken cancellationToken)
    {
        var directory = Guid.TryParse(checkpointIdOrPath, out var checkpointId) && checkpointId != Guid.Empty
            ? ResolveCheckpointDirectory(checkpointIdOrPath)
            : Path.GetFullPath(checkpointIdOrPath);
        var state = await ReadStateAsync(directory, cancellationToken).ConfigureAwait(false);
        if (state.Status != "complete")
            throw new SnapshotStoreException(SnapshotIssueCodes.CheckpointNotComplete, "Only complete checkpoints can be replayed.");
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint manifest is missing or unreadable.");
        }
        var manifest = SnapshotManifestCodec.Decode(bytes);
        if (state.CheckpointId != manifest.Id.Value.ToString("N", CultureInfo.InvariantCulture))
            throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint state and manifest identities differ.");
        foreach (var endpoint in manifest.Endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveContainedPath(directory, endpoint.SegmentReference);
            var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (actual.Length != endpoint.SegmentLength || actual.Hash != endpoint.SegmentSha256)
                throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint segment failed length/SHA-256 validation.");

            var accumulator = new SnapshotFingerprints.MultisetAccumulator();
            long recordCount = 0;
            await foreach (var (record, fingerprint) in ReadNodeAsync(directory, endpoint, cancellationToken).ConfigureAwait(false))
            {
                if (SnapshotFingerprints.RecordFingerprint(record) != fingerprint)
                    throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint record fingerprint validation failed.");
                accumulator.Add(fingerprint);
                recordCount++;
                foreach (var value in record.Values.Values.OfType<BinaryReferenceValue>())
                {
                    if (!value.Reference.StartsWith("checkpoint-binary-v1:", StringComparison.Ordinal))
                        throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint record contains an external binary reference.");
                    var binaryHash = value.Reference["checkpoint-binary-v1:".Length..];
                    if (binaryHash != value.Sha256 || binaryHash.Length != 64 || binaryHash.Any(character => !Uri.IsHexDigit(character)))
                        throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint record contains an invalid binary digest.");
                    var blobPath = ResolveContainedPath(directory, $"blobs/{binaryHash}.blob");
                    var blob = await HashFileAsync(blobPath, cancellationToken).ConfigureAwait(false);
                    if (blob.Length != value.ContentLength || blob.Hash != binaryHash)
                        throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint binary blob failed length/SHA-256 validation.");
                }
            }

            if (recordCount != endpoint.ArtifactCount || accumulator.Finish(endpoint.SourceNodeKey) != endpoint.SourceFingerprint)
                throw new SnapshotStoreException(SnapshotIssueCodes.SourceFingerprintMismatch, "Checkpoint source set does not match its manifest.");
        }

        if (SnapshotFingerprints.AggregateSourceFingerprint(manifest.GraphHash, manifest.Endpoints) != manifest.SourceFingerprint)
            throw new SnapshotStoreException(SnapshotIssueCodes.SourceFingerprintMismatch, "Aggregate checkpoint source fingerprint does not match its manifest.");
        return new LoadedCheckpoint(directory, manifest);
    }

    public Task DeleteAsync(string checkpointIdOrPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = ResolveCheckpointDirectory(checkpointIdOrPath);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private string ResolveCheckpointDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint ID or path is required.");
        var candidate = Guid.TryParse(value, out var id) && id != Guid.Empty
            ? Path.Combine(_root, id.ToString("N", CultureInfo.InvariantCulture))
            : Path.GetFullPath(value);
        candidate = Path.GetFullPath(candidate);
        if (!ConnectorPathUtilities.IsContained(_root, candidate) ||
            string.Equals(Path.TrimEndingDirectorySeparator(_root), Path.TrimEndingDirectorySeparator(candidate), PathComparison))
            throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint path must be a child of the configured snapshot root.");
        return candidate;
    }

    internal static string ResolveContainedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
            throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint reference is not a safe relative path.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return ConnectorPathUtilities.IsContained(root, path)
            ? path
            : throw new SnapshotStoreException(SnapshotIssueCodes.SnapshotPathInvalid, "Checkpoint reference escapes its checkpoint directory.");
    }

    internal static async Task<(long Length, string Hash)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static async Task WriteStateAsync(string directory, CheckpointId id, CheckpointStatus status,
        string? failureCode, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateDocument(id.Value.ToString("N", CultureInfo.InvariantCulture),
            status.ToString().ToLowerInvariant(), failureCode), StateOptions);
        var temporary = Path.Combine(directory, ".temporary", $"state-{Guid.NewGuid():N}.partial");
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        RestrictFile(temporary);
        File.Move(temporary, Path.Combine(directory, "state.json"), overwrite: true);
    }

    private static async Task<StateDocument> ReadStateAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "state.json"), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<StateDocument>(bytes, StateOptions)
                ?? throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint state marker is empty.");
        }
        catch (SnapshotStoreException) { throw; }
        catch { throw new SnapshotStoreException(SnapshotIssueCodes.CheckpointNotComplete, "Checkpoint state marker is unavailable."); }
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record StateDocument(string CheckpointId, string Status, string? FailureCode);
}
