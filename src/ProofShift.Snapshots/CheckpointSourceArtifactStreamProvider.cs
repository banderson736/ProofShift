using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

public sealed class CheckpointSourceArtifactStreamProvider : ISourceArtifactStreamProvider
{
    private readonly ILoadedCheckpoint _checkpoint;
    private readonly Dictionary<string, CheckpointEndpoint> _endpoints;

    public CheckpointSourceArtifactStreamProvider(ILoadedCheckpoint checkpoint)
    {
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        _endpoints = checkpoint.Manifest.Endpoints.ToDictionary(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal);
    }

    public string? CheckpointId => _checkpoint.Manifest.Id.Value.ToString("N");
    public string? CheckpointSourceFingerprint => _checkpoint.Manifest.SourceFingerprint;
    public string? CheckpointManifestHash => _checkpoint.Manifest.ManifestHash;
    public string? CheckpointConfigurationHash => _checkpoint.Manifest.ConfigurationHash;
    public string? CheckpointGraphHash => _checkpoint.Manifest.GraphHash;
    public IReadOnlyCollection<string>? CheckpointSourceNodeKeys => _endpoints.Keys.ToArray();

    public Task<string> ValidateAsync(string sourceNodeKey, ConnectorContext context, ArtifactSelector selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_endpoints.TryGetValue(sourceNodeKey, out var endpoint))
            throw new SnapshotStoreException(SnapshotIssueCodes.IncompleteSourceCoverage, "Checkpoint does not contain every compiled source node.");
        if (endpoint.SystemId.Value != context.SystemKey || endpoint.EndpointId.Value != context.EndpointKey ||
            endpoint.Connector != context.Connector || endpoint.SelectorHash != SnapshotFingerprints.SelectorHash(selector))
            throw new SnapshotStoreException(SnapshotIssueCodes.SelectorMismatch, "Checkpoint source identity or selector does not match the compiled configuration.");
        return Task.FromResult(endpoint.ConnectorVersion);
    }

    public IAsyncEnumerable<RecordEnvelope> ReadAsync(string sourceNodeKey, ConnectorContext context,
        ArtifactSelector selector, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _checkpoint.ReadAsync(sourceNodeKey, selector, cancellationToken);
    }

    public ValueTask<Stream> OpenBinaryReadAsync(string sourceNodeKey, ConnectorContext context, ArtifactSelector selector,
        ArtifactReference artifact, BinaryReferenceValue binaryReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _checkpoint.OpenBinaryReadAsync(sourceNodeKey, artifact, binaryReference, cancellationToken);
    }
}
