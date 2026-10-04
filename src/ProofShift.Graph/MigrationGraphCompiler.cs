using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Configuration;
using ProofShift.Domain;

namespace ProofShift.Graph;

public sealed record GraphCompilationSummary
{
    public int Nodes { get; }
    public int SourceNodes { get; }
    public int TargetNodes { get; }
    public int Edges { get; }
    public int SourceAccounted { get; }
    public int TargetConnected { get; }
    public int RecoveryDefinitions { get; }
    public DomainDictionary<int> OperationCounts { get; }

    public GraphCompilationSummary(
        int nodes,
        int sourceNodes,
        int targetNodes,
        int edges,
        int sourceAccounted,
        int targetConnected,
        int recoveryDefinitions,
        IEnumerable<KeyValuePair<string, int>> operationCounts)
    {
        Nodes = nodes;
        SourceNodes = sourceNodes;
        TargetNodes = targetNodes;
        Edges = edges;
        SourceAccounted = sourceAccounted;
        TargetConnected = targetConnected;
        RecoveryDefinitions = recoveryDefinitions;
        OperationCounts = new DomainDictionary<int>(operationCounts);
    }
}

public sealed record GraphCompilationResult
{
    public int? GraphVersion { get; }
    public string GraphCanonicalizationVersion { get; }
    public MigrationGraph? Graph { get; }
    public string? CanonicalGraph { get; }
    public GraphCompilationSummary? Summary { get; }
    public DomainList<GraphValidationIssue> Issues { get; }
    public bool IsValid => Graph is not null && Issues.All(issue => issue.Severity != GraphValidationSeverity.Error);
    public string? GraphHash => Graph?.GraphHash;

    public GraphCompilationResult(
        int? graphVersion,
        MigrationGraph? graph,
        string? canonicalGraph,
        GraphCompilationSummary? summary,
        IEnumerable<GraphValidationIssue> issues)
    {
        GraphVersion = graphVersion;
        GraphCanonicalizationVersion = GraphCanonicalizer.FormatVersion;
        Graph = graph;
        CanonicalGraph = canonicalGraph;
        Summary = summary;
        Issues = new DomainList<GraphValidationIssue>(issues
            .OrderBy(issue => issue.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal));
    }
}

public static class MigrationGraphCompiler
{
    private const int SupportedGraphVersion = 1;

    public static GraphCompilationResult Compile(LoadedProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<GraphValidationIssue>();
        var graphFile = configuration.MigrationGraphConfiguration;
        if (graphFile is null)
        {
            AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                "Root configuration does not reference a migration graph.");
            return Result(null, null, null, issues);
        }

        var parsed = MigrationGraphConfigurationParser.Parse(graphFile);
        issues.AddRange(parsed.Issues);
        if (parsed.Configuration is null)
        {
            return Result(null, null, null, issues);
        }

        var graphConfiguration = parsed.Configuration;
        if (graphConfiguration.Nodes.Count == 0)
        {
            AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                "Migration graph must contain at least one node.", graphFile.RelativePath, "nodes");
        }

        if (graphConfiguration.Edges.Count == 0)
        {
            AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                "Migration graph must contain at least one edge.", graphFile.RelativePath, "edges");
        }

        if (graphConfiguration.Version is null)
        {
            AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                "Migration graph version is required.", graphFile.RelativePath, "version");
        }
        else if (graphConfiguration.Version != SupportedGraphVersion)
        {
            AddIssue(issues, GraphIssueCodes.UnsupportedVersion,
                $"Unsupported migration graph version '{graphConfiguration.Version}'. Supported version is {SupportedGraphVersion}.",
                graphFile.RelativePath,
                "version");
        }

        var projectKey = configuration.Root.Project?.Id ?? string.Empty;
        var nodeDrafts = CompileNodes(configuration, graphFile, graphConfiguration, projectKey, issues);
        var edgeDrafts = CompileEdges(graphFile, graphConfiguration, projectKey, nodeDrafts, issues);
        var reachability = ValidateReachability(graphFile, nodeDrafts, edgeDrafts, issues);
        ValidateCycles(graphFile, nodeDrafts, edgeDrafts, issues);

        var summary = CreateSummary(nodeDrafts.Values, edgeDrafts, reachability);
        if (issues.Any(issue => issue.Severity == GraphValidationSeverity.Error) ||
            graphConfiguration.Version != SupportedGraphVersion)
        {
            return Result(graphConfiguration.Version, null, summary, issues);
        }

        var domainNodes = nodeDrafts.Values.Select(draft => draft.Node!).OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var domainEdges = edgeDrafts.Select(draft => draft.Edge!).OrderBy(edge => edge.Name, StringComparer.Ordinal).ToArray();
        var externalKeys = domainNodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(
            graphConfiguration.Version.Value,
            domainNodes,
            domainEdges,
            externalKeys);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var graphId = new MigrationGraphId(StableGraphIdentifier.Derive(
            projectKey,
            graphFile.RelativePath,
            "graph",
            graphFile.RelativePath));
        var graph = new MigrationGraph(
            graphId,
            domainNodes,
            domainEdges,
            hash,
            GraphCanonicalizer.FormatVersion);

        foreach (var issue in graph.Validate())
        {
            AddDomainValidationIssue(issues, graphFile.RelativePath, issue);
        }

        if (issues.Any(issue => issue.Severity == GraphValidationSeverity.Error))
        {
            return Result(graphConfiguration.Version, null, summary, issues);
        }

        return Result(graphConfiguration.Version, graph, summary, issues, canonical);
    }

    private static Dictionary<string, NodeDraft> CompileNodes(
        LoadedProjectConfiguration configuration,
        ReferencedConfigurationFile graphFile,
        MigrationGraphConfigurationDto graphConfiguration,
        string projectKey,
        ICollection<GraphValidationIssue> issues)
    {
        var nodes = new Dictionary<string, NodeDraft>(StringComparer.Ordinal);
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in graphConfiguration.Nodes.OrderBy(node => node.Key, StringComparer.Ordinal))
        {
            var path = $"nodes.{item.Key}";
            if (!IsGraphKey(item.Key))
            {
                AddIssue(issues, GraphIssueCodes.InvalidGraphDocument, "Graph node key is invalid.", graphFile.RelativePath, "nodes");
                continue;
            }

            if (!nodeKeys.Add(item.Key))
            {
                AddIssue(issues, GraphIssueCodes.DuplicateNodeId,
                    $"Duplicate graph node ID '{item.Key}'.", graphFile.RelativePath, path);
                continue;
            }

            if (!TryParseNodeType(item.Type, out var type))
            {
                AddIssue(issues, GraphIssueCodes.InvalidNodeType,
                    $"Invalid graph node type '{item.Type ?? string.Empty}'.", graphFile.RelativePath, $"{path}.type");
            }

            var systemId = RequiredValue(item.System, GraphIssueCodes.MissingRequiredProperty,
                "Node system is required.", graphFile.RelativePath, $"{path}.system", issues);
            var storageId = RequiredValue(item.Storage, GraphIssueCodes.MissingRequiredProperty,
                "Node storage endpoint is required.", graphFile.RelativePath, $"{path}.storage", issues);
            var semanticType = RequiredValue(item.SemanticType, GraphIssueCodes.MissingRequiredProperty,
                "Node semantic type is required.", graphFile.RelativePath, $"{path}.semanticType", issues);

            SystemDefinition? system = null;
            StorageEndpointDefinition? endpoint = null;
            if (systemId is not null)
            {
                system = configuration.Systems.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id.Value, systemId, StringComparison.Ordinal));
                if (system is null)
                {
                    AddIssue(issues, GraphIssueCodes.UnknownSystem,
                        $"Unknown system '{systemId}'.", graphFile.RelativePath, $"{path}.system");
                }
            }

            if (storageId is not null && system is not null)
            {
                endpoint = system.StorageEndpoints.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id.Value, storageId, StringComparison.Ordinal));
                if (endpoint is null)
                {
                    AddIssue(issues, GraphIssueCodes.UnknownStorageEndpoint,
                        $"Storage endpoint '{storageId}' does not exist in system '{systemId}'.",
                        graphFile.RelativePath,
                        $"{path}.storage");
                }
            }

            var selector = CompileSelector(item.Selector, graphFile.RelativePath, path, issues);
            var nodeId = new MigrationNodeId(StableGraphIdentifier.Derive(
                projectKey,
                graphFile.RelativePath,
                "node",
                item.Key));
            MigrationNode? domainNode = null;
            if (type is not null && system is not null && endpoint is not null && semanticType is not null && selector is not null)
            {
                domainNode = new MigrationNode(nodeId, item.Key, type.Value, semanticType, system.Id, endpoint.Id, selector);
            }

            nodes.Add(item.Key, new NodeDraft(item.Key, nodeId, type, domainNode));
        }

        return nodes;
    }

    private static ArtifactSelector? CompileSelector(
        ArtifactSelectorConfigurationDto? selector,
        string file,
        string nodePath,
        ICollection<GraphValidationIssue> issues)
    {
        if (selector is null || string.IsNullOrWhiteSpace(selector.Kind))
        {
            AddIssue(issues, GraphIssueCodes.InvalidSelector,
                "Node requires a selector with a non-empty kind.", file, $"{nodePath}.selector.kind");
            return null;
        }

        if (selector.Properties.Count == 0 && selector.IdentityFields.Count == 0)
        {
            AddIssue(issues, GraphIssueCodes.InvalidSelector,
                "Selector must define properties or identity fields.", file, $"{nodePath}.selector");
            return null;
        }

        if (selector.IdentityFields.Any(string.IsNullOrWhiteSpace) ||
            selector.IdentityFields.Distinct(StringComparer.Ordinal).Count() != selector.IdentityFields.Count)
        {
            AddIssue(issues, GraphIssueCodes.InvalidSelector,
                "Selector identity fields must be non-empty and unique.", file, $"{nodePath}.selector.identity");
            return null;
        }

        return new ArtifactSelector(selector.Kind, selector.Properties, selector.IdentityFields);
    }

    private static List<EdgeDraft> CompileEdges(
        ReferencedConfigurationFile graphFile,
        MigrationGraphConfigurationDto graphConfiguration,
        string projectKey,
        IReadOnlyDictionary<string, NodeDraft> nodes,
        ICollection<GraphValidationIssue> issues)
    {
        var edges = new List<EdgeDraft>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in graphConfiguration.Edges.OrderBy(edge => edge.Key, StringComparer.Ordinal))
        {
            var path = $"edges.{item.Key}";
            if (!IsGraphKey(item.Key))
            {
                AddIssue(issues, GraphIssueCodes.InvalidGraphDocument, "Graph edge key is invalid.", graphFile.RelativePath, "edges");
                continue;
            }

            if (!edgeKeys.Add(item.Key))
            {
                AddIssue(issues, GraphIssueCodes.DuplicateEdgeId,
                    $"Duplicate graph edge ID '{item.Key}'.", graphFile.RelativePath, path);
                continue;
            }

            if (!TryParseOperationType(item.OperationType, out var operationType))
            {
                AddIssue(issues, GraphIssueCodes.InvalidOperation,
                    $"Invalid migration operation '{item.OperationType ?? string.Empty}'.", graphFile.RelativePath, $"{path}.operation.type");
            }

            ValidateCardinality(item, operationType, graphFile.RelativePath, issues);
            ValidateReferences(item, nodes, graphFile.RelativePath, issues);
            ValidateArchiveTargets(item, operationType, nodes, graphFile.RelativePath, issues);

            var steps = CompileSteps(item.Steps, graphFile.RelativePath, $"{path}.operation.steps", issues);
            var fields = CompileFields(item.Fields, graphFile.RelativePath, $"{path}.operation.fields", issues);
            if (operationType is MigrationOperationType.Map or MigrationOperationType.Transform && fields.Count == 0)
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    $"Operation '{item.OperationType}' requires at least one field mapping.",
                    graphFile.RelativePath,
                    $"{path}.operation.fields");
            }

            var recovery = CompileRecovery(item.Recovery, graphFile.RelativePath, $"{path}.recovery", issues);
            if (item.Recovery is null)
            {
                AddIssue(issues, GraphIssueCodes.MissingRecovery,
                    "Every migration edge requires an explicit recovery definition.",
                    graphFile.RelativePath,
                    $"{path}.recovery");
            }

            var effectiveDestructive = item.IsDestructive ||
                operationType is MigrationOperationType.Merge or MigrationOperationType.Aggregate or MigrationOperationType.Exclude ||
                (operationType == MigrationOperationType.Archive && IsTrue(item.Parameters, "deleteSource")) ||
                IsTrue(item.Parameters, "lossy") ||
                item.Steps.Any(IsDestructiveStep) ||
                item.Fields.Any(field => field.Pipeline.Any(IsDestructiveStep));
            var allReferencesResolve = item.From.All(nodes.ContainsKey) && item.To.All(nodes.ContainsKey);
            MigrationEdge? domainEdge = null;
            if (operationType is not null && recovery is not null && allReferencesResolve &&
                item.From.All(key => nodes[key].Node is not null) && item.To.All(key => nodes[key].Node is not null))
            {
                var operation = new MigrationOperation(
                    operationType.Value,
                    steps,
                    effectiveDestructive,
                    fields,
                    item.Parameters);
                var edgeId = new MigrationEdgeId(StableGraphIdentifier.Derive(
                    projectKey,
                    graphFile.RelativePath,
                    "edge",
                    item.Key));
                var edgeVersion = item.Version ?? (graphConfiguration.Version ?? SupportedGraphVersion)
                    .ToString(CultureInfo.InvariantCulture);
                try
                {
                    domainEdge = new MigrationEdge(
                        edgeId,
                        item.Key,
                        item.From.Select(key => nodes[key].Id),
                        item.To.Select(key => nodes[key].Id),
                        operation,
                        edgeVersion,
                        recovery);
                }
                catch (ArgumentException exception)
                {
                    AddIssue(issues, GraphIssueCodes.InvalidRecovery, exception.Message, graphFile.RelativePath, $"{path}.recovery");
                }
            }

            edges.Add(new EdgeDraft(item.Key, item.From, item.To, operationType, effectiveDestructive, domainEdge, recovery is not null));
        }

        return edges;
    }

    private static List<TransformationStep> CompileSteps(
        IEnumerable<TransformationStepConfigurationDto> definitions,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var steps = new List<TransformationStep>();
        var index = 0;
        foreach (var definition in definitions)
        {
            if (!TryParseStepType(definition.Type, out var type))
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    $"Invalid transformation step '{definition.Type ?? string.Empty}'.", file, $"{path}[{index}].type");
                index++;
                continue;
            }

            var version = string.IsNullOrWhiteSpace(definition.Version) ? "1" : definition.Version.Trim();
            steps.Add(new TransformationStep(type, version, definition.Parameters));
            index++;
        }

        return steps;
    }

    private static List<TransformationFieldDefinition> CompileFields(
        IEnumerable<TransformationFieldConfigurationDto> definitions,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var fields = new List<TransformationFieldDefinition>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!targets.Add(definition.Target))
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    $"Duplicate mapped target field '{definition.Target}'.", file, path);
                continue;
            }

            var pipeline = CompileSteps(definition.Pipeline, file, $"{path}.{definition.Target}.pipeline", issues);
            if (pipeline is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.Source) &&
                !pipeline.Any(step => step.Type == TransformationStepType.Calculate))
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    $"Field '{definition.Target}' requires a source or calculate step.", file, $"{path}.{definition.Target}.source");
            }

            fields.Add(new TransformationFieldDefinition(definition.Target, definition.Source, pipeline));
        }

        return fields;
    }

    private static RecoveryDefinition? CompileRecovery(
        RecoveryConfigurationDto? configuration,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        if (configuration is null)
        {
            return null;
        }

        if (!TryParseRecoveryMode(configuration.Mode, out var mode))
        {
            AddIssue(issues, GraphIssueCodes.InvalidRecovery,
                $"Invalid recovery mode '{configuration.Mode ?? string.Empty}'.", file, $"{path}.mode");
            return null;
        }

        try
        {
            return new RecoveryDefinition(
                mode,
                configuration.Strategy,
                configuration.RequiresSnapshot ?? false,
                configuration.Justification);
        }
        catch (ArgumentException exception)
        {
            AddIssue(issues, GraphIssueCodes.InvalidRecovery, exception.Message, file, path);
            return null;
        }
    }

    private static void ValidateReferences(
        MigrationEdgeConfigurationDto edge,
        IReadOnlyDictionary<string, NodeDraft> nodes,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        foreach (var source in edge.From)
        {
            if (!nodes.ContainsKey(source))
            {
                AddIssue(issues, GraphIssueCodes.UnknownSourceNode,
                    $"Unknown source node '{source}'.", file, $"edges.{edge.Key}.from");
            }
        }

        foreach (var target in edge.To)
        {
            if (!nodes.ContainsKey(target))
            {
                AddIssue(issues, GraphIssueCodes.UnknownTargetNode,
                    $"Unknown target node '{target}'.", file, $"edges.{edge.Key}.to");
            }
        }
    }

    private static void ValidateArchiveTargets(
        MigrationEdgeConfigurationDto edge,
        MigrationOperationType? operationType,
        IReadOnlyDictionary<string, NodeDraft> nodes,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        if (operationType != MigrationOperationType.Archive)
        {
            return;
        }

        foreach (var targetKey in edge.To)
        {
            if (nodes.TryGetValue(targetKey, out var target) && target.Type != MigrationNodeType.Archive)
            {
                AddIssue(issues, GraphIssueCodes.InvalidCardinality,
                    $"Archive operation target '{targetKey}' must be an archive node.",
                    file,
                    $"edges.{edge.Key}.to");
            }
        }
    }

    private static void ValidateCardinality(
        MigrationEdgeConfigurationDto edge,
        MigrationOperationType? operationType,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        if (edge.From.Count == 0)
        {
            AddIssue(issues, GraphIssueCodes.InvalidCardinality,
                "Migration edge requires at least one source node.", file, $"edges.{edge.Key}.from");
        }

        if (edge.From.Distinct(StringComparer.Ordinal).Count() != edge.From.Count ||
            edge.To.Distinct(StringComparer.Ordinal).Count() != edge.To.Count)
        {
            AddIssue(issues, GraphIssueCodes.InvalidCardinality,
                "Source and target node references must be unique within an edge.", file, $"edges.{edge.Key}");
        }

        if (operationType is null)
        {
            return;
        }

        var valid = operationType.Value switch
        {
            MigrationOperationType.Exclude => edge.From.Count > 0 && edge.To.Count == 0,
            MigrationOperationType.Split => edge.From.Count == 1 && edge.To.Count >= 2,
            MigrationOperationType.Merge => edge.From.Count >= 2 && edge.To.Count == 1,
            MigrationOperationType.Aggregate => edge.From.Count >= 1 && edge.To.Count == 1,
            _ => edge.From.Count >= 1 && edge.To.Count >= 1
        };
        if (!valid)
        {
            AddIssue(issues, GraphIssueCodes.InvalidCardinality,
                $"Operation '{edge.OperationType}' does not support {edge.From.Count} source(s) and {edge.To.Count} target(s).",
                file,
                $"edges.{edge.Key}");
        }
    }

    private static ReachabilityResult ValidateReachability(
        ReferencedConfigurationFile graphFile,
        IReadOnlyDictionary<string, NodeDraft> nodes,
        IReadOnlyCollection<EdgeDraft> edges,
        ICollection<GraphValidationIssue> issues)
    {
        var outgoing = nodes.Keys.ToDictionary(key => key, _ => new List<EdgeDraft>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            foreach (var source in edge.Sources.Where(outgoing.ContainsKey))
            {
                outgoing[source].Add(edge);
            }
        }

        var reachableFromSources = new HashSet<string>(StringComparer.Ordinal);
        var accountedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in nodes.Values.Where(node => node.Type == MigrationNodeType.Source).OrderBy(node => node.Key, StringComparer.Ordinal))
        {
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { source.Key };
            pending.Enqueue(source.Key);
            var reachesDisposition = false;
            while (pending.TryDequeue(out var current))
            {
                reachableFromSources.Add(current);
                foreach (var edge in outgoing[current].OrderBy(candidate => candidate.Key, StringComparer.Ordinal))
                {
                    if (edge.OperationType == MigrationOperationType.Exclude)
                    {
                        reachesDisposition = true;
                    }

                    foreach (var targetKey in edge.Targets.Order(StringComparer.Ordinal))
                    {
                        if (!nodes.TryGetValue(targetKey, out var target))
                        {
                            continue;
                        }

                        if (target.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                        {
                            reachesDisposition = true;
                        }

                        if (visited.Add(targetKey))
                        {
                            pending.Enqueue(targetKey);
                        }
                    }
                }
            }

            if (reachesDisposition)
            {
                accountedSources.Add(source.Key);
            }
            else
            {
                AddIssue(issues, GraphIssueCodes.OrphanSource,
                    $"Source node '{source.Key}' has no path to a target, archive, or explicit exclusion.",
                    graphFile.RelativePath,
                    $"nodes.{source.Key}");
            }
        }

        foreach (var target in nodes.Values.Where(node => node.Type == MigrationNodeType.Target).OrderBy(node => node.Key, StringComparer.Ordinal))
        {
            if (!reachableFromSources.Contains(target.Key))
            {
                AddIssue(issues, GraphIssueCodes.OrphanTarget,
                    $"Target node '{target.Key}' has no incoming path from a source node.",
                    graphFile.RelativePath,
                    $"nodes.{target.Key}");
            }
        }

        var connectedTargets = nodes.Values
            .Where(node => node.Type == MigrationNodeType.Target && reachableFromSources.Contains(node.Key))
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new ReachabilityResult(accountedSources, connectedTargets);
    }

    private static void ValidateCycles(
        ReferencedConfigurationFile graphFile,
        IReadOnlyDictionary<string, NodeDraft> nodes,
        IReadOnlyCollection<EdgeDraft> edges,
        ICollection<GraphValidationIssue> issues)
    {
        var adjacency = nodes.Keys.ToDictionary(key => key, _ => new List<(string Target, EdgeDraft Edge)>(), StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            foreach (var source in edge.Sources.Where(adjacency.ContainsKey))
            {
                foreach (var target in edge.Targets.Where(adjacency.ContainsKey))
                {
                    adjacency[source].Add((target, edge));
                }
            }
        }

        foreach (var linked in adjacency.Values)
        {
            linked.Sort((left, right) =>
            {
                var targetOrder = StringComparer.Ordinal.Compare(left.Target, right.Target);
                return targetOrder != 0 ? targetOrder : StringComparer.Ordinal.Compare(left.Edge.Key, right.Edge.Key);
            });
        }

        var nextIndex = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<List<string>>();

        void Visit(string node)
        {
            indexes[node] = nextIndex;
            lowLinks[node] = nextIndex;
            nextIndex++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var link in adjacency[node])
            {
                if (!indexes.TryGetValue(link.Target, out var targetIndex))
                {
                    Visit(link.Target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[link.Target]);
                }
                else if (onStack.Contains(link.Target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], targetIndex);
                }
            }

            if (lowLinks[node] != indexes[node])
            {
                return;
            }

            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (!string.Equals(member, node, StringComparison.Ordinal));

            components.Add(component);
        }

        foreach (var node in nodes.Keys.Order(StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(node))
            {
                Visit(node);
            }
        }

        foreach (var component in components)
        {
            var componentSet = component.ToHashSet(StringComparer.Ordinal);
            var innerEdges = edges.Where(edge => edge.Sources.Any(componentSet.Contains) && edge.Targets.Any(componentSet.Contains)).ToArray();
            var selfCycle = component.Count == 1 && adjacency[component[0]].Any(link =>
                string.Equals(link.Target, component[0], StringComparison.Ordinal));
            if (component.Count == 1 && !selfCycle)
            {
                continue;
            }

            var relationshipOnly = innerEdges.Length > 0 && innerEdges.All(edge => edge.OperationType == MigrationOperationType.Relationship);
            var destructive = innerEdges.Any(edge => edge.IsDestructive);
            var isWarning = relationshipOnly && !destructive;
            var members = component.Order(StringComparer.Ordinal).ToArray();
            var message = isWarning
                ? $"Relationship-only cycle detected among nodes: {string.Join(", ", members)}."
                : $"Execution or destructive cycle detected among nodes: {string.Join(", ", members)}.";
            AddIssue(issues, GraphIssueCodes.CycleDetected, message, graphFile.RelativePath,
                $"nodes.{string.Join(",", members)}", isWarning ? GraphValidationSeverity.Warning : GraphValidationSeverity.Error);
        }
    }

    private static GraphCompilationSummary CreateSummary(
        IEnumerable<NodeDraft> nodeDrafts,
        IEnumerable<EdgeDraft> edgeDrafts,
        ReachabilityResult reachability)
    {
        var nodes = nodeDrafts.ToArray();
        var edges = edgeDrafts.ToArray();
        var sourceNodes = nodes.Where(node => node.Type == MigrationNodeType.Source).Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        var targetNodes = nodes.Where(node => node.Type == MigrationNodeType.Target).Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        var operations = edges
            .Where(edge => edge.OperationType is not null)
            .GroupBy(edge => Kebab(edge.OperationType!.Value.ToString()), StringComparer.Ordinal)
            .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()));
        return new GraphCompilationSummary(
            nodes.Length,
            sourceNodes.Count,
            targetNodes.Count,
            edges.Length,
            reachability.AccountedSources.Count,
            reachability.ConnectedTargets.Count,
            edges.Count(edge => edge.HasRecovery),
            operations);
    }

    private static void AddDomainValidationIssue(
        ICollection<GraphValidationIssue> issues,
        string file,
        MigrationGraphIssue issue)
    {
        var code = issue.Code switch
        {
            MigrationGraphIssueCode.DuplicateNodeId => GraphIssueCodes.DuplicateNodeId,
            MigrationGraphIssueCode.DuplicateEdgeId => GraphIssueCodes.DuplicateEdgeId,
            MigrationGraphIssueCode.EdgeHasNoSource or MigrationGraphIssueCode.EdgeHasNoTarget => GraphIssueCodes.InvalidCardinality,
            MigrationGraphIssueCode.DanglingFromNode => GraphIssueCodes.UnknownSourceNode,
            MigrationGraphIssueCode.DanglingToNode => GraphIssueCodes.UnknownTargetNode,
            _ => GraphIssueCodes.InvalidGraphDocument
        };
        AddIssue(issues, code, issue.Message, file);
    }

    private static bool TryParseNodeType(string? value, out MigrationNodeType? type)
    {
        switch (Normalize(value))
        {
            case "source": type = MigrationNodeType.Source; return true;
            case "target": type = MigrationNodeType.Target; return true;
            case "archive": type = MigrationNodeType.Archive; return true;
            case "derived": type = MigrationNodeType.Derived; return true;
            case "aggregate": type = MigrationNodeType.Aggregate; return true;
            default: type = null; return false;
        }
    }

    private static bool TryParseOperationType(string? value, out MigrationOperationType? type)
    {
        switch (Normalize(value))
        {
            case "copy": type = MigrationOperationType.Copy; return true;
            case "map": type = MigrationOperationType.Map; return true;
            case "transform": type = MigrationOperationType.Transform; return true;
            case "split": type = MigrationOperationType.Split; return true;
            case "merge": type = MigrationOperationType.Merge; return true;
            case "aggregate": type = MigrationOperationType.Aggregate; return true;
            case "derive": type = MigrationOperationType.Derive; return true;
            case "archive": type = MigrationOperationType.Archive; return true;
            case "exclude": type = MigrationOperationType.Exclude; return true;
            case "relationship": type = MigrationOperationType.Relationship; return true;
            default: type = null; return false;
        }
    }

    private static bool TryParseStepType(string? value, out TransformationStepType type)
    {
        switch (Normalize(value))
        {
            case "copy": type = TransformationStepType.Copy; return true;
            case "rename": type = TransformationStepType.Rename; return true;
            case "trim": type = TransformationStepType.Trim; return true;
            case "normalizestring": type = TransformationStepType.NormalizeString; return true;
            case "normalizedate": type = TransformationStepType.NormalizeDate; return true;
            case "codemap": type = TransformationStepType.CodeMap; return true;
            case "concatenate": type = TransformationStepType.Concatenate; return true;
            case "split": type = TransformationStepType.Split; return true;
            case "lookup": type = TransformationStepType.Lookup; return true;
            case "calculate": type = TransformationStepType.Calculate; return true;
            case "archive": type = TransformationStepType.Archive; return true;
            case "exclude": type = TransformationStepType.Exclude; return true;
            default: type = default; return false;
        }
    }

    private static bool TryParseRecoveryMode(string? value, out RecoveryMode mode)
    {
        switch (Normalize(value))
        {
            case "reverse": mode = RecoveryMode.Reverse; return true;
            case "restore": mode = RecoveryMode.Restore; return true;
            case "compensate": mode = RecoveryMode.Compensate; return true;
            case "irreversible": mode = RecoveryMode.Irreversible; return true;
            default: mode = default; return false;
        }
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToLowerInvariant();

    private static string Kebab(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string? RequiredValue(
        string? value,
        string code,
        string message,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        AddIssue(issues, code, message, file, path);
        return null;
    }

    private static bool IsGraphKey(string key) =>
        key.Length > 0 && char.IsAsciiLetterOrDigit(key[0]) &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsTrue(DomainDictionary<string> values, string key) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static bool IsDestructiveStep(TransformationStepConfigurationDto step) =>
        Normalize(step.Type) == "exclude" ||
        (Normalize(step.Type) == "archive" && IsTrue(step.Parameters, "deleteSource")) ||
        IsTrue(step.Parameters, "lossy");

    private static GraphCompilationResult Result(
        int? version,
        MigrationGraph? graph,
        GraphCompilationSummary? summary,
        IEnumerable<GraphValidationIssue> issues,
        string? canonicalGraph = null) =>
        new(version, graph, canonicalGraph, summary, issues);

    private static void AddIssue(
        ICollection<GraphValidationIssue> issues,
        string code,
        string message,
        string? file = null,
        string? path = null,
        GraphValidationSeverity severity = GraphValidationSeverity.Error) =>
        issues.Add(new GraphValidationIssue(code, severity, message, file, path));

    private sealed record NodeDraft(string Key, MigrationNodeId Id, MigrationNodeType? Type, MigrationNode? Node);

    private sealed record EdgeDraft(
        string Key,
        DomainList<string> Sources,
        DomainList<string> Targets,
        MigrationOperationType? OperationType,
        bool IsDestructive,
        MigrationEdge? Edge,
        bool HasRecovery);

    private sealed record ReachabilityResult(
        HashSet<string> AccountedSources,
        HashSet<string> ConnectedTargets);
}
