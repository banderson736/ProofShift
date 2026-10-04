using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public sealed record ShadowTargetContext
{
    public ConnectorContext ConnectorContext { get; }
    public RunId RunId { get; }
    public SystemRole Role { get; }

    public ShadowTargetContext(ConnectorContext connectorContext, RunId runId, SystemRole role)
    {
        ConnectorContext = connectorContext ?? throw new ArgumentNullException(nameof(connectorContext));
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Run identifier must not be empty.", nameof(runId));
        }

        RunId = runId;
        if (role != SystemRole.ShadowTarget)
        {
            throw new ArgumentException("Shadow connectors only accept systems explicitly classified as shadow-target.", nameof(role));
        }

        Role = role;
    }
}

public sealed record ShadowWriteRequest
{
    public ShadowTargetContext Context { get; }
    public ArtifactSelector Selector { get; }
    public RecordEnvelope Record { get; }
    public string NodeKey { get; }
    public Func<string, CancellationToken, ValueTask<Stream>>? OpenBinaryReadAsync { get; }

    public ShadowWriteRequest(
        ShadowTargetContext context,
        ArtifactSelector selector,
        RecordEnvelope record,
        string nodeKey,
        Func<string, CancellationToken, ValueTask<Stream>>? openBinaryReadAsync = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        NodeKey = Required(nodeKey, nameof(nodeKey));
        OpenBinaryReadAsync = openBinaryReadAsync;
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record ReadRequest
{
    public ShadowTargetContext Context { get; }
    public ArtifactSelector Selector { get; }
    public ReadOptions Options { get; }

    public ReadRequest(ShadowTargetContext context, ArtifactSelector selector, ReadOptions? options = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Options = options ?? new ReadOptions();
    }
}

public interface IShadowTargetConnector
{
    ConnectorId Id { get; }
    string Version { get; }

    Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken);
    Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken);
    Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken);
    IAsyncEnumerable<RecordEnvelope> ReadAsync(ReadRequest request, CancellationToken cancellationToken);
}

public sealed class ShadowTargetConnectorRegistry
{
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<ConnectorId, IShadowTargetConnector> _connectors;

    public ShadowTargetConnectorRegistry(IEnumerable<IShadowTargetConnector> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        var map = new Dictionary<ConnectorId, IShadowTargetConnector>();
        foreach (var connector in connectors)
        {
            ArgumentNullException.ThrowIfNull(connector);
            if (!map.TryAdd(connector.Id, connector))
            {
                throw new ArgumentException($"Shadow target connector '{connector.Id.Value}' is registered more than once.", nameof(connectors));
            }
        }

        _connectors = new System.Collections.ObjectModel.ReadOnlyDictionary<ConnectorId, IShadowTargetConnector>(map);
    }

    public IShadowTargetConnector Resolve(ConnectorId connectorId) =>
        _connectors.TryGetValue(connectorId, out var connector)
            ? connector
            : throw new ConnectorResolutionException(ConnectorIssueCodes.UnknownConnector,
                "Configured shadow target connector is not registered.");
}

public sealed class ProjectionConnectorException(string message) : Exception(message);