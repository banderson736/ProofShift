using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class ReaderTargetObserver(ISourceConnector reader) : ITargetObserver
{
    public ConnectorId Id => reader.Id;
    public string Version => reader.Version;

    public async IAsyncEnumerable<RecordEnvelope> ObserveAsync(TargetObservationRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var record in reader.ReadAsync(request.Context.ConnectorContext, request.Selector, request.Options,
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var identity = GraphTargetIdentity.Create(record, request.Selector);
            var artifact = new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(
                record.Artifact.SystemId.Value, record.Artifact.EndpointId.Value, record.Artifact.ArtifactType, identity)),
                record.Artifact.SystemId, record.Artifact.EndpointId, record.Artifact.ArtifactType, identity);
            yield return new RecordEnvelope(artifact, record.SemanticType, record.Values, record.Provenance,
                record.Relationships, record.Temporal);
        }
    }
}