using System.Text;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

public sealed partial class FileSystemSnapshotStore
{
    private sealed class LoadedCheckpoint(string directory, SourceCheckpoint manifest) : ILoadedCheckpoint
    {
        private readonly Dictionary<string, CheckpointEndpoint> _endpoints = manifest.Endpoints
            .ToDictionary(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal);

        public SourceCheckpoint Manifest => manifest;

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(string sourceNodeKey, ArtifactSelector selector,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!_endpoints.TryGetValue(sourceNodeKey, out var endpoint))
                throw new SnapshotStoreException(SnapshotIssueCodes.MissingReplayArtifact, "Required source node is absent from this checkpoint.");
            if (SnapshotFingerprints.SelectorHash(selector) != endpoint.SelectorHash)
                throw new SnapshotStoreException(SnapshotIssueCodes.SelectorMismatch, "Checkpoint selector differs from the compiled graph.");

            var accumulator = new SnapshotFingerprints.MultisetAccumulator();
            long count = 0;
            await foreach (var (record, fingerprint) in ReadNodeAsync(directory, endpoint, cancellationToken).ConfigureAwait(false))
            {
                if (SnapshotFingerprints.RecordFingerprint(record) != fingerprint)
                    throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Materialized artifact fingerprint mismatch.");
                accumulator.Add(fingerprint);
                count++;
            }
            if (count != endpoint.ArtifactCount || accumulator.Finish(sourceNodeKey) != endpoint.SourceFingerprint)
                throw new SnapshotStoreException(SnapshotIssueCodes.SourceFingerprintMismatch, "Materialized source set does not match the checkpoint manifest.");

            await foreach (var (record, _) in ReadNodeAsync(directory, endpoint, cancellationToken).ConfigureAwait(false))
                yield return record;
        }

        public async ValueTask<Stream> OpenBinaryReadAsync(string sourceNodeKey, ArtifactReference artifact,
            BinaryReferenceValue binaryReference, CancellationToken cancellationToken)
        {
            if (!_endpoints.ContainsKey(sourceNodeKey) || !binaryReference.Reference.StartsWith("checkpoint-binary-v1:", StringComparison.Ordinal))
                throw new SnapshotStoreException(SnapshotIssueCodes.MissingReplayArtifact, "Snapshot binary reference is invalid.");
            var hash = binaryReference.Reference["checkpoint-binary-v1:".Length..];
            if (hash != binaryReference.Sha256 || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot binary reference hash is invalid.");
            var path = ResolveContainedPath(directory, $"blobs/{hash}.blob");
            var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (actual.Length != binaryReference.ContentLength || actual.Hash != hash)
                throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Snapshot binary blob failed integrity validation.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async IAsyncEnumerable<(RecordEnvelope Record, string Fingerprint)> ReadNodeAsync(
        string directory,
        CheckpointEndpoint endpoint,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = ResolveContainedPath(directory, endpoint.SegmentReference);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
            BufferSize, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) throw new SnapshotStoreException(SnapshotIssueCodes.IntegrityFailure, "Checkpoint segment contains an empty line.");
            yield return SnapshotRecordCodec.Decode(Encoding.UTF8.GetBytes(line));
        }
    }
}
