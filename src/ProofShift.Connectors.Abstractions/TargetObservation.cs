using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public sealed record TargetObservationContext
{
    public ConnectorContext ConnectorContext { get; }
    public RunId RunId { get; }
    public SystemRole Role { get; }

    public TargetObservationContext(ConnectorContext connectorContext, RunId runId, SystemRole role)
    {
        ConnectorContext = connectorContext ?? throw new ArgumentNullException(nameof(connectorContext));
        if (runId.Value == Guid.Empty) throw new ArgumentException("Observation requires a run identity.", nameof(runId));
        if (role is not (SystemRole.Target or SystemRole.ShadowTarget))
            throw new ArgumentException("Observation requires a target or isolated shadow-target system.", nameof(role));
        RunId = runId;
        Role = role;
    }
}

public sealed record TargetObservationRequest(TargetObservationContext Context, ArtifactSelector Selector, ReadOptions Options);

public interface ITargetObserver
{
    ConnectorId Id { get; }
    string Version { get; }
    IAsyncEnumerable<RecordEnvelope> ObserveAsync(TargetObservationRequest request, CancellationToken cancellationToken);
}

public sealed class ShadowReadObserverAdapter(IShadowTargetConnector connector) : ITargetObserver
{
    public ConnectorId Id => connector.Id;
    public string Version => connector.Version;
    public IAsyncEnumerable<RecordEnvelope> ObserveAsync(TargetObservationRequest request, CancellationToken cancellationToken)
    {
        if (request.Context.Role != SystemRole.ShadowTarget)
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "Shadow read adapter may only observe an isolated shadow target.");
        return connector.ReadAsync(new ReadRequest(new ShadowTargetContext(request.Context.ConnectorContext,
            request.Context.RunId, request.Context.Role), request.Selector, request.Options), cancellationToken);
    }
}