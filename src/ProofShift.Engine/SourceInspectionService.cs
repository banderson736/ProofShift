using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Graph;

namespace ProofShift.Engine;

public sealed record NodeInspectionResult
{
    public MigrationNodeId NodeId { get; }
    public string NodeKey { get; }
    public string SystemKey { get; }
    public string EndpointKey { get; }
    public ConnectorId Connector { get; }
    public ArtifactSelector Selector { get; }
    public SourceInspection Inspection { get; }

    public NodeInspectionResult(
        MigrationNode node,
        string systemKey,
        string endpointKey,
        ConnectorId connector,
        SourceInspection inspection)
    {
        NodeId = node.Id;
        NodeKey = node.Name;
        SystemKey = systemKey;
        EndpointKey = endpointKey;
        Connector = connector;
        Selector = node.Selector;
        Inspection = inspection;
    }
}

public sealed record SourceInspectionReport
{
    public string ProjectName { get; }
    public DomainList<NodeInspectionResult> Sources { get; }
    public bool IsValid => Sources.Count > 0 && Sources.All(source => source.Inspection.Status == SourceInspectionStatus.Valid);

    public SourceInspectionReport(string projectName, IEnumerable<NodeInspectionResult> sources)
    {
        ProjectName = projectName;
        Sources = new DomainList<NodeInspectionResult>(sources.OrderBy(source => source.NodeKey, StringComparer.Ordinal));
    }
}

public sealed class RuntimeConnectorContextFactory
{
    private readonly IEnvironmentVariableProvider _environment;

    public RuntimeConnectorContextFactory(IEnvironmentVariableProvider? environment = null) =>
        _environment = environment ?? new ProcessEnvironmentVariableProvider();

    public ConnectorContext Create(
        LoadedProjectConfiguration configuration,
        MigrationNode node)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(node);

        var system = configuration.Systems.FirstOrDefault(candidate => candidate.Id == node.SystemId)
            ?? throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Graph node references an unavailable system.");
        var endpoint = system.StorageEndpoints.FirstOrDefault(candidate => candidate.Id == node.EndpointId)
            ?? throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Graph node references an unavailable storage endpoint.");
        var runtimeSettings = endpoint.Configuration.Select(pair =>
            new KeyValuePair<string, RuntimeSetting>(pair.Key, ResolveSetting(pair.Value)));
        return new ConnectorContext(
            system.Id.Value,
            endpoint.Id.Value,
            endpoint.Connector,
            node.Name,
            node.SemanticType,
            new RuntimeConfiguration(runtimeSettings));
    }

    private RuntimeSetting ResolveSetting(string identity)
    {
        if (identity.StartsWith("secret:", StringComparison.Ordinal))
        {
            return ResolveEnvironment(identity["secret:".Length..], isSecret: true);
        }

        if (identity.StartsWith("env:", StringComparison.Ordinal))
        {
            return ResolveEnvironment(identity["env:".Length..], isSecret: false);
        }

        return RuntimeSetting.FromRuntimeValue(identity);
    }

    private RuntimeSetting ResolveEnvironment(string name, bool isSecret)
    {
        var value = _environment.GetValue(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ConnectorConfigurationException(
                ConnectorIssueCodes.MissingConfiguration,
                "Required runtime configuration is unavailable.");
        }

        return RuntimeSetting.FromRuntimeValue(value, isSecret);
    }
}

public sealed class SourceInspectionService
{
    private readonly ConnectorRegistry _registry;
    private readonly RuntimeConnectorContextFactory _contextFactory;

    public SourceInspectionService(ConnectorRegistry registry, RuntimeConnectorContextFactory? contextFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _contextFactory = contextFactory ?? new RuntimeConnectorContextFactory();
    }

    public async Task<SourceInspectionReport> InspectAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(graph);
        var results = new List<NodeInspectionResult>();

        foreach (var node in graph.Nodes.Where(node => node.Type == MigrationNodeType.Source).OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectorId = FindConnector(configuration, node);
            var connector = TryResolveConnector(connectorId, node);
            if (connector is null)
            {
                results.Add(FailedResult(node, connectorId, ConnectorIssueCodes.UnknownConnector, "Configured connector is not registered."));
                continue;
            }

            ConnectorContext context;
            try
            {
                context = _contextFactory.Create(configuration, node);
            }
            catch (ConnectorConfigurationException exception)
            {
                results.Add(FailedResult(node, connectorId, exception.Code, exception.Message));
                continue;
            }

            try
            {
                var inspection = await connector.InspectAsync(context, node.Selector, cancellationToken).ConfigureAwait(false);
                results.Add(new NodeInspectionResult(node, node.SystemId.Value, node.EndpointId.Value, connectorId, inspection));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ConnectorConfigurationException exception)
            {
                results.Add(FailedResult(node, connectorId, exception.Code, exception.Message));
            }
            catch
            {
                results.Add(FailedResult(node, connectorId, ConnectorIssueCodes.SourceConnectionFailed, "Source inspection failed."));
            }
        }

        return new SourceInspectionReport(configuration.Root.Project?.Name ?? configuration.Root.Project?.Id ?? "Unnamed project", results);
    }

    private static ConnectorId FindConnector(LoadedProjectConfiguration configuration, MigrationNode node)
    {
        var system = configuration.Systems.FirstOrDefault(candidate => candidate.Id == node.SystemId);
        var endpoint = system?.StorageEndpoints.FirstOrDefault(candidate => candidate.Id == node.EndpointId);
        return endpoint?.Connector ?? new ConnectorId("unknown");
    }

    private ISourceConnector? TryResolveConnector(ConnectorId connectorId, MigrationNode node)
    {
        try
        {
            return _registry.Resolve(connectorId);
        }
        catch (ConnectorResolutionException)
        {
            return null;
        }
    }

    private static NodeInspectionResult FailedResult(MigrationNode node, ConnectorId connectorId, string code, string message)
    {
        var issue = new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, node.Name);
        var inspection = new SourceInspection(SourceInspectionStatus.Failed, issues: [issue]);
        return new NodeInspectionResult(node, node.SystemId.Value, node.EndpointId.Value, connectorId, inspection);
    }
}
