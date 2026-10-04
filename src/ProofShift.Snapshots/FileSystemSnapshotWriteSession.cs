using System.Security.Cryptography;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

internal sealed class FileSystemSnapshotWriteSession : ICheckpointWriteSession
{
    private readonly CheckpointId _id;
    private readonly string _directory;
    private readonly Dictionary<string, SegmentWriter> _writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckpointSegment> _segments = new(StringComparer.Ordinal);
    private bool _finalized;

    public CheckpointId Id => _id;

    public FileSystemSnapshotWriteSession(CheckpointId id, string directory)
    {
        _id = id;
        _directory = directory;
    }

    public Task BeginSourceNodeAsync(string sourceNodeKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        if (_writers.ContainsKey(sourceNodeKey) || _segments.ContainsKey(sourceNodeKey))
            throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed, "Source node was started more than once.");
        var hash = SnapshotFingerprints.Hash(sourceNodeKey);
        var relative = $"nodes/{hash}.ndjson";
        var partial = Path.Combine(_directory, "nodes", $"{hash}.ndjson.partial");
        var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            FileSystemSnapshotStore.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        FileSystemSnapshotStore.RestrictFile(partial);
        _writers.Add(sourceNodeKey, new SegmentWriter(partial, relative, stream));
        return Task.CompletedTask;
    }

    public async Task<long> WriteRecordAsync(string sourceNodeKey, RecordEnvelope record,
        Func<string, CancellationToken, ValueTask<Stream>>? openBinaryReadAsync, CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (!_writers.TryGetValue(sourceNodeKey, out var writer))
            throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed, "Source node writer is unavailable.");
        var sourceFingerprint = SnapshotFingerprints.RecordFingerprint(record);
        var values = new List<KeyValuePair<string, ValueNode>>(record.Values.Count);
        long binaryBytes = 0;
        foreach (var pair in record.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value is not BinaryReferenceValue binary)
            {
                values.Add(pair);
                continue;
            }
            if (openBinaryReadAsync is null)
                throw new SnapshotStoreException(SnapshotIssueCodes.MissingReplayArtifact, "Binary source value has no stream resolver.");
            var source = await openBinaryReadAsync(pair.Key, cancellationToken).ConfigureAwait(false);
            await CopyBinaryAsync(binary, source, cancellationToken).ConfigureAwait(false);
            binaryBytes = checked(binaryBytes + binary.ContentLength);
            values.Add(new KeyValuePair<string, ValueNode>(pair.Key,
                new BinaryReferenceValue($"checkpoint-binary-v1:{binary.Sha256}", binary.ContentLength, binary.Sha256)));
        }

        var metadata = record.Provenance.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        metadata["snapshotMaterialized"] = "true";
        var provenance = new ProvenanceMetadata(record.Provenance.Connector, record.Provenance.Endpoint,
            record.Provenance.Location, record.Provenance.ObservedAt, record.Provenance.SourceHash, metadata);
        var captured = new RecordEnvelope(record.Artifact, record.SemanticType, values, provenance, record.Relationships, record.Temporal);
        var line = SnapshotRecordCodec.Encode(captured, _id, sourceNodeKey, sourceFingerprint);
        await writer.Stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await writer.Stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        writer.Hash.AppendData(line);
        writer.Hash.AppendData("\n"u8);
        writer.Length = checked(writer.Length + line.LongLength + 1);
        return checked(line.LongLength + 1 + binaryBytes);
    }

    public async Task<CheckpointSegment> CompleteSourceNodeAsync(string sourceNodeKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        if (!_writers.Remove(sourceNodeKey, out var writer))
            throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed, "Source node was not started.");
        await writer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await writer.Stream.DisposeAsync().ConfigureAwait(false);
        var hash = Convert.ToHexString(writer.Hash.GetHashAndReset()).ToLowerInvariant();
        writer.Hash.Dispose();
        var target = FileSystemSnapshotStore.ResolveContainedPath(_directory, writer.RelativePath);
        File.Move(writer.PartialPath, target, overwrite: false);
        FileSystemSnapshotStore.RestrictFile(target);
        var segment = new CheckpointSegment(writer.RelativePath, hash, writer.Length);
        _segments.Add(sourceNodeKey, segment);
        return segment;
    }

    public async Task<string> FinalizeAsync(SourceCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        if (checkpoint.Id != _id || checkpoint.Status != CheckpointStatus.Complete || checkpoint.Endpoints.Count != _segments.Count ||
            checkpoint.Endpoints.Any(endpoint => !_segments.TryGetValue(endpoint.SourceNodeKey, out var segment) ||
                segment.Reference != endpoint.SegmentReference || segment.Sha256 != endpoint.SegmentSha256 || segment.Length != endpoint.SegmentLength))
            throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed, "Final manifest does not match all completed source segments.");

        var (bytes, manifestHash) = SnapshotManifestCodec.Encode(checkpoint);
        var temporary = Path.Combine(_directory, ".temporary", $"manifest-{Guid.NewGuid():N}.partial");
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        FileSystemSnapshotStore.RestrictFile(temporary);
        File.Move(temporary, Path.Combine(_directory, "manifest.json"), overwrite: false);
        FileSystemSnapshotStore.RestrictFile(Path.Combine(_directory, "manifest.json"));
        await FileSystemSnapshotStore.WriteStateAsync(_directory, _id, CheckpointStatus.Complete, null, cancellationToken).ConfigureAwait(false);
        _finalized = true;
        return manifestHash;
    }

    public async Task MarkIncompleteAsync(CheckpointStatus status, string failureCode, CancellationToken cancellationToken)
    {
        if (status is not (CheckpointStatus.Failed or CheckpointStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status));
        foreach (var writer in _writers.Values)
        {
            await writer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await writer.Stream.DisposeAsync().ConfigureAwait(false);
            writer.Hash.Dispose();
        }
        _writers.Clear();
        await FileSystemSnapshotStore.WriteStateAsync(_directory, _id, status, failureCode, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var writer in _writers.Values)
        {
            await writer.Stream.DisposeAsync().ConfigureAwait(false);
            writer.Hash.Dispose();
        }
        _writers.Clear();
    }

    private async Task CopyBinaryAsync(BinaryReferenceValue binary, Stream source, CancellationToken cancellationToken)
    {
        await using (source.ConfigureAwait(false))
        {
            var destination = FileSystemSnapshotStore.ResolveContainedPath(_directory, $"blobs/{binary.Sha256}.blob");
            if (File.Exists(destination))
            {
                var existing = await FileSystemSnapshotStore.HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
                if (existing.Length != binary.ContentLength || existing.Hash != binary.Sha256)
                    throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Existing snapshot blob is corrupt.");
                return;
            }

            var temporary = Path.Combine(_directory, ".temporary", $"blob-{Guid.NewGuid():N}.partial");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                FileSystemSnapshotStore.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                FileSystemSnapshotStore.RestrictFile(temporary);
                var buffer = new byte[FileSystemSnapshotStore.BufferSize];
                long length = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    length = checked(length + read);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (length != binary.ContentLength || actualHash != binary.Sha256)
                    throw new SnapshotStoreException(SnapshotIssueCodes.ArtifactChangedDuringCapture, "Source binary changed during materialization.");
            }
            File.Move(temporary, destination, overwrite: false);
            FileSystemSnapshotStore.RestrictFile(destination);
        }
    }

    private void EnsureOpen()
    {
        if (_finalized) throw new SnapshotStoreException(SnapshotIssueCodes.CaptureFailed, "Checkpoint session is finalized.");
    }

    private sealed class SegmentWriter(string partialPath, string relativePath, FileStream stream)
    {
        public string PartialPath { get; } = partialPath;
        public string RelativePath { get; } = relativePath;
        public FileStream Stream { get; } = stream;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Length { get; set; }
    }
}
