namespace ProofShift.Domain;

public enum MigrationNodeType
{
    Source,
    Target,
    Archive,
    Derived,
    Aggregate
}

public sealed record ArtifactSelector
{
    public string Kind { get; }
    public DomainDictionary<string> Properties { get; }
    public DomainList<string> IdentityFields { get; }

    public ArtifactSelector(
        string kind,
        IEnumerable<KeyValuePair<string, string>>? properties = null,
        IEnumerable<string>? identityFields = null)
    {
        Kind = DomainGuard.Required(kind, nameof(kind));
        Properties = new DomainDictionary<string>(properties ?? Array.Empty<KeyValuePair<string, string>>());
        IdentityFields = new DomainList<string>((identityFields ?? Array.Empty<string>())
            .Select(field => DomainGuard.Required(field, nameof(identityFields))));
        if (IdentityFields.Distinct(StringComparer.Ordinal).Count() != IdentityFields.Count)
        {
            throw new ArgumentException("Selector identity fields must be unique.", nameof(identityFields));
        }
    }
}

public sealed record MigrationNode
{
    public MigrationNodeId Id { get; }
    public string Name { get; }
    public MigrationNodeType Type { get; }
    public string SemanticType { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public ArtifactSelector Selector { get; }

    public MigrationNode(
        MigrationNodeId id,
        string name,
        MigrationNodeType type,
        string semanticType,
        SystemId systemId,
        StorageEndpointId endpointId,
        ArtifactSelector selector)
    {
        Id = DomainGuard.Required(id, nameof(id));
        Name = DomainGuard.Required(name, nameof(name));
        Type = type;
        SemanticType = DomainGuard.Required(semanticType, nameof(semanticType));
        SystemId = DomainGuard.Required(systemId, nameof(systemId));
        EndpointId = DomainGuard.Required(endpointId, nameof(endpointId));
        Selector = DomainGuard.NotNull(selector, nameof(selector));
    }
}

public enum TransformationStepType
{
    Copy,
    Rename,
    Trim,
    NormalizeString,
    NormalizeDate,
    CodeMap,
    Concatenate,
    Split,
    Lookup,
    Calculate,
    Archive,
    Exclude
}

public sealed record TransformationStep
{
    public TransformationStepType Type { get; }
    public string Version { get; }
    public DomainDictionary<string> Parameters { get; }

    public TransformationStep(
        TransformationStepType type,
        string version,
        IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        Type = type;
        Version = DomainGuard.Required(version, nameof(version));
        Parameters = new DomainDictionary<string>(parameters ?? Array.Empty<KeyValuePair<string, string>>());
    }
}

public enum MigrationOperationType
{
    Copy,
    Map,
    Transform,
    Split,
    Merge,
    Aggregate,
    Derive,
    Archive,
    Exclude,
    Relationship
}

public sealed record MigrationOperation
{
    public MigrationOperationType Type { get; }
    public DomainList<TransformationStep> Steps { get; }
    public DomainList<TransformationFieldDefinition> Fields { get; }
    public DomainDictionary<string> Parameters { get; }
    public bool IsDestructive { get; }

    public MigrationOperation(
        MigrationOperationType type,
        IEnumerable<TransformationStep>? steps = null,
        bool isDestructive = false,
        IEnumerable<TransformationFieldDefinition>? fields = null,
        IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        Type = type;
        Steps = new DomainList<TransformationStep>(steps ?? Array.Empty<TransformationStep>());
        Fields = new DomainList<TransformationFieldDefinition>(fields ?? Array.Empty<TransformationFieldDefinition>());
        Parameters = new DomainDictionary<string>(parameters ?? Array.Empty<KeyValuePair<string, string>>());
        IsDestructive = isDestructive ||
            type == MigrationOperationType.Exclude ||
            Steps.Any(step => step.Type == TransformationStepType.Exclude) ||
            Fields.Any(field => field.Pipeline.Any(step => step.Type == TransformationStepType.Exclude));
    }
}

public sealed record TransformationFieldDefinition
{
    public string Target { get; }
    public string? Source { get; }
    public DomainList<TransformationStep> Pipeline { get; }

    public TransformationFieldDefinition(
        string target,
        string? source = null,
        IEnumerable<TransformationStep>? pipeline = null)
    {
        Target = DomainGuard.Required(target, nameof(target));
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        Pipeline = new DomainList<TransformationStep>(pipeline ?? Array.Empty<TransformationStep>());
    }
}

public enum RecoveryMode
{
    Reverse,
    Restore,
    Compensate,
    Irreversible
}

public sealed record RecoveryDefinition
{
    public RecoveryMode Mode { get; }
    public string? Strategy { get; }
    public bool RequiresSnapshot { get; }
    public string? Justification { get; }

    public RecoveryDefinition(
        RecoveryMode mode,
        string? strategy = null,
        bool requiresSnapshot = false,
        string? justification = null)
    {
        if (mode == RecoveryMode.Restore && !requiresSnapshot)
        {
            throw new ArgumentException("Restore recovery must declare that a snapshot is required.", nameof(requiresSnapshot));
        }

        if (mode == RecoveryMode.Irreversible && string.IsNullOrWhiteSpace(justification))
        {
            throw new ArgumentException("Irreversible recovery requires a justification.", nameof(justification));
        }

        if (mode == RecoveryMode.Compensate && string.IsNullOrWhiteSpace(strategy))
        {
            throw new ArgumentException("Compensating recovery requires a strategy.", nameof(strategy));
        }

        Mode = mode;
        Strategy = string.IsNullOrWhiteSpace(strategy) ? null : strategy.Trim();
        RequiresSnapshot = requiresSnapshot;
        Justification = string.IsNullOrWhiteSpace(justification) ? null : justification.Trim();
    }
}

public sealed record MigrationEdge
{
    public MigrationEdgeId Id { get; }
    public string Name { get; }
    public DomainList<MigrationNodeId> Sources { get; }
    public DomainList<MigrationNodeId> Targets { get; }
    public MigrationOperation Operation { get; }
    public RecoveryDefinition? Recovery { get; }
    public string Version { get; }

    public MigrationEdge(
        MigrationEdgeId id,
        string name,
        IEnumerable<MigrationNodeId> sources,
        IEnumerable<MigrationNodeId> targets,
        MigrationOperation operation,
        string version,
        RecoveryDefinition? recovery = null)
    {
        Operation = DomainGuard.NotNull(operation, nameof(operation));
        if (operation.IsDestructive && recovery is null)
        {
            throw new ArgumentException("A destructive migration operation requires explicit recovery metadata.", nameof(recovery));
        }

        Id = DomainGuard.Required(id, nameof(id));
        Name = DomainGuard.Required(name, nameof(name));
        Sources = new DomainList<MigrationNodeId>(sources.Select(source => DomainGuard.Required(source, nameof(sources))));
        Targets = new DomainList<MigrationNodeId>(targets.Select(target => DomainGuard.Required(target, nameof(target))));
        Recovery = recovery;
        Version = DomainGuard.Required(version, nameof(version));
    }

    public MigrationEdge(
        MigrationEdgeId id,
        MigrationNodeId from,
        MigrationNodeId to,
        MigrationOperation operation,
        string version,
        RecoveryDefinition? recovery = null)
        : this(id, id.Value.ToString("N"), [from], [to], operation, version, recovery)
    {
    }
}

public enum MigrationGraphIssueCode
{
    DuplicateNodeId,
    DuplicateEdgeId,
    EdgeHasNoSource,
    EdgeHasNoTarget,
    DanglingFromNode,
    DanglingToNode
}

public sealed record MigrationGraphIssue
{
    public MigrationGraphIssueCode Code { get; }
    public string Message { get; }
    public MigrationEdgeId? EdgeId { get; }
    public MigrationNodeId? NodeId { get; }

    public MigrationGraphIssue(
        MigrationGraphIssueCode code,
        string message,
        MigrationEdgeId? edgeId = null,
        MigrationNodeId? nodeId = null)
    {
        Code = code;
        Message = DomainGuard.Required(message, nameof(message));
        EdgeId = edgeId is { } edge ? DomainGuard.Required(edge, nameof(edgeId)) : null;
        NodeId = nodeId is { } node ? DomainGuard.Required(node, nameof(nodeId)) : null;
    }
}

public sealed record MigrationGraph
{
    public MigrationGraphId Id { get; }
    public DomainList<MigrationNode> Nodes { get; }
    public DomainList<MigrationEdge> Edges { get; }
    public string GraphHash { get; }
    public string? GraphCanonicalizationVersion { get; }

    public MigrationGraph(
        MigrationGraphId id,
        IEnumerable<MigrationNode> nodes,
        IEnumerable<MigrationEdge> edges,
        string graphHash,
        string? graphCanonicalizationVersion = null)
    {
        Id = DomainGuard.Required(id, nameof(id));
        Nodes = new DomainList<MigrationNode>(nodes);
        Edges = new DomainList<MigrationEdge>(edges);
        GraphHash = DomainGuard.Required(graphHash, nameof(graphHash));
        GraphCanonicalizationVersion = string.IsNullOrWhiteSpace(graphCanonicalizationVersion)
            ? null
            : graphCanonicalizationVersion.Trim();
    }

    public DomainList<MigrationGraphIssue> Validate()
    {
        var issues = new List<MigrationGraphIssue>();
        var nodeIds = new HashSet<MigrationNodeId>();
        foreach (var node in Nodes)
        {
            if (!nodeIds.Add(node.Id))
            {
                issues.Add(new MigrationGraphIssue(
                    MigrationGraphIssueCode.DuplicateNodeId,
                    $"Migration node identifier '{node.Id}' occurs more than once.",
                    nodeId: node.Id));
            }
        }

        var edgeIds = new HashSet<MigrationEdgeId>();
        foreach (var edge in Edges)
        {
            if (!edgeIds.Add(edge.Id))
            {
                issues.Add(new MigrationGraphIssue(
                    MigrationGraphIssueCode.DuplicateEdgeId,
                    $"Migration edge identifier '{edge.Id}' occurs more than once.",
                    edgeId: edge.Id));
            }

            if (edge.Sources.Count == 0)
            {
                issues.Add(new MigrationGraphIssue(
                    MigrationGraphIssueCode.EdgeHasNoSource,
                    $"Migration edge '{edge.Name}' has no source nodes.",
                    edgeId: edge.Id));
            }

            if (edge.Targets.Count == 0 && edge.Operation.Type != MigrationOperationType.Exclude)
            {
                issues.Add(new MigrationGraphIssue(
                    MigrationGraphIssueCode.EdgeHasNoTarget,
                    $"Migration edge '{edge.Name}' has no target nodes.",
                    edgeId: edge.Id));
            }

            foreach (var source in edge.Sources)
            {
                if (!nodeIds.Contains(source))
                {
                    issues.Add(new MigrationGraphIssue(
                        MigrationGraphIssueCode.DanglingFromNode,
                        $"Migration edge '{edge.Name}' references missing source node '{source}'.",
                        edgeId: edge.Id,
                        nodeId: source));
                }
            }

            foreach (var target in edge.Targets)
            {
                if (!nodeIds.Contains(target))
                {
                    issues.Add(new MigrationGraphIssue(
                        MigrationGraphIssueCode.DanglingToNode,
                        $"Migration edge '{edge.Name}' references missing target node '{target}'.",
                        edgeId: edge.Id,
                        nodeId: target));
                }
            }
        }

        return new DomainList<MigrationGraphIssue>(issues);
    }
}

public enum VerificationScope
{
    Attribute,
    Entity,
    Relationship,
    Timeline,
    Aggregate,
    Accounting,
    Recovery
}

public sealed record VerificationPolicy
{
    public DomainList<VerificationScope> RequiredScopes { get; }

    public VerificationPolicy(IEnumerable<VerificationScope> requiredScopes)
    {
        RequiredScopes = new DomainList<VerificationScope>(requiredScopes.Distinct());
    }
}

public sealed record RecoveryPolicy
{
    public bool RequireRecoveryForDestructiveOperations { get; }
    public bool AllowIrreversibleOperations { get; }

    public RecoveryPolicy(
        bool requireRecoveryForDestructiveOperations = true,
        bool allowIrreversibleOperations = false)
    {
        RequireRecoveryForDestructiveOperations = requireRecoveryForDestructiveOperations;
        AllowIrreversibleOperations = allowIrreversibleOperations;
    }
}

public sealed record MigrationPlan
{
    public MigrationPlanId Id { get; }
    public int Version { get; }
    public ProjectId ProjectId { get; }
    public SnapshotId SourceSnapshotId { get; }
    public MigrationGraph Graph { get; }
    public VerificationPolicy Verification { get; }
    public RecoveryPolicy Recovery { get; }
    public string ConfigurationHash { get; }
    public DateTimeOffset CreatedAt { get; }

    public MigrationPlan(
        MigrationPlanId id,
        int version,
        ProjectId projectId,
        SnapshotId sourceSnapshotId,
        MigrationGraph graph,
        VerificationPolicy verification,
        RecoveryPolicy recovery,
        string configurationHash,
        DateTimeOffset createdAt)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Plan version must be positive.");
        }

        Id = DomainGuard.Required(id, nameof(id));
        Version = version;
        ProjectId = DomainGuard.Required(projectId, nameof(projectId));
        SourceSnapshotId = DomainGuard.Required(sourceSnapshotId, nameof(sourceSnapshotId));
        Graph = DomainGuard.NotNull(graph, nameof(graph));
        Verification = DomainGuard.NotNull(verification, nameof(verification));
        Recovery = DomainGuard.NotNull(recovery, nameof(recovery));
        ConfigurationHash = DomainGuard.Required(configurationHash, nameof(configurationHash));
        CreatedAt = createdAt;
    }
}

public enum ArtifactDisposition
{
    Migrated,
    Transformed,
    Derived,
    Archived,
    Excluded,
    Superseded,
    Failed,
    Unaccounted
}

public sealed record ArtifactDispositionRecord
{
    public ArtifactReference Source { get; }
    public MigrationNodeId? SourceNodeId { get; }
    public ArtifactDisposition Disposition { get; }
    public DomainList<ArtifactReference> Targets { get; }
    public DomainList<MigrationNodeId> TargetNodeIds { get; }
    public string? Reason { get; }

    public ArtifactDispositionRecord(
        ArtifactReference source,
        ArtifactDisposition disposition,
        IEnumerable<ArtifactReference>? targets = null,
        string? reason = null,
        MigrationNodeId? sourceNodeId = null,
        IEnumerable<MigrationNodeId>? targetNodeIds = null)
    {
        Source = DomainGuard.NotNull(source, nameof(source));
        SourceNodeId = sourceNodeId is { } nodeId ? DomainGuard.Required(nodeId, nameof(sourceNodeId)) : null;
        Targets = new DomainList<ArtifactReference>(targets ?? Array.Empty<ArtifactReference>());
        TargetNodeIds = new DomainList<MigrationNodeId>(targetNodeIds ?? []);
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Disposition = disposition;

        if (TargetNodeIds.Count > 0 && TargetNodeIds.Count != Targets.Count)
            throw new ArgumentException("Target graph-node scope must align with target artifact references.", nameof(targetNodeIds));

        if (disposition is ArtifactDisposition.Migrated or ArtifactDisposition.Transformed or ArtifactDisposition.Derived or ArtifactDisposition.Archived
            && Targets.Count == 0)
        {
            throw new ArgumentException($"Disposition '{disposition}' requires at least one target artifact.", nameof(targets));
        }

        if (disposition is ArtifactDisposition.Excluded or ArtifactDisposition.Superseded or ArtifactDisposition.Failed or ArtifactDisposition.Unaccounted
            && Reason is null)
        {
            throw new ArgumentException($"Disposition '{disposition}' requires an explanation.", nameof(reason));
        }

        if (disposition == ArtifactDisposition.Unaccounted && Targets.Count > 0)
        {
            throw new ArgumentException("An unaccounted source artifact cannot have target artifacts.", nameof(targets));
        }
    }
}

public enum ArtifactAccountingIssueCode
{
    MissingDisposition,
    UnexpectedDisposition,
    DuplicateDisposition,
    UnaccountedArtifact,
    MissingNodeScope
}

public sealed record ArtifactAccountingIssue
{
    public ArtifactAccountingIssueCode Code { get; }
    public ArtifactId ArtifactId { get; }
    public MigrationNodeId? NodeId { get; }
    public string Message { get; }

    public ArtifactAccountingIssue(ArtifactAccountingIssueCode code, ArtifactId artifactId, string message, MigrationNodeId? nodeId = null)
    {
        Code = code;
        ArtifactId = DomainGuard.Required(artifactId, nameof(artifactId));
        Message = DomainGuard.Required(message, nameof(message));
        NodeId = nodeId;
    }
}

public sealed record ArtifactDispositionLedger
{
    public DomainList<ArtifactDispositionRecord> Records { get; }

    public ArtifactDispositionLedger(IEnumerable<ArtifactDispositionRecord> records) =>
        Records = new DomainList<ArtifactDispositionRecord>(records);

    public DomainList<ArtifactAccountingIssue> ValidateCoverage(IEnumerable<ArtifactReference> sourceArtifacts)
    {
        ArgumentNullException.ThrowIfNull(sourceArtifacts);
        var expectedIds = sourceArtifacts.Select(artifact => artifact.Id).ToHashSet();
        var groupedRecords = Records.GroupBy(record => record.Source.Id).ToArray();
        var issues = new List<ArtifactAccountingIssue>();

        foreach (var group in groupedRecords)
        {
            if (!expectedIds.Contains(group.Key))
            {
                issues.Add(new ArtifactAccountingIssue(
                    ArtifactAccountingIssueCode.UnexpectedDisposition,
                    group.Key,
                    $"Disposition references unexpected source artifact '{group.Key}'."));
            }

            if (group.Count() > 1)
            {
                issues.Add(new ArtifactAccountingIssue(
                    ArtifactAccountingIssueCode.DuplicateDisposition,
                    group.Key,
                    $"Source artifact '{group.Key}' has more than one disposition."));
            }

            if (group.Any(record => record.Disposition == ArtifactDisposition.Unaccounted))
            {
                issues.Add(new ArtifactAccountingIssue(
                    ArtifactAccountingIssueCode.UnaccountedArtifact,
                    group.Key,
                    $"Source artifact '{group.Key}' is unaccounted."));
            }
        }

        var recordedIds = groupedRecords.Select(group => group.Key).ToHashSet();
        foreach (var missingId in expectedIds.Except(recordedIds))
        {
            issues.Add(new ArtifactAccountingIssue(
                ArtifactAccountingIssueCode.MissingDisposition,
                missingId,
                $"Source artifact '{missingId}' has no disposition."));
        }

        return new DomainList<ArtifactAccountingIssue>(issues);
    }

    public DomainList<ArtifactAccountingIssue> ValidateScopedCoverage(IEnumerable<GraphArtifactReference> sourceArtifacts)
    {
        ArgumentNullException.ThrowIfNull(sourceArtifacts);
        var expected = sourceArtifacts.Select(item => (item.NodeId, item.Artifact.Id)).ToHashSet();
        var scopedRecords = new List<(MigrationNodeId NodeId, ArtifactDispositionRecord Record)>();
        var issues = new List<ArtifactAccountingIssue>();
        foreach (var record in Records)
        {
            if (record.SourceNodeId is not { } nodeId)
            {
                issues.Add(new ArtifactAccountingIssue(ArtifactAccountingIssueCode.MissingNodeScope, record.Source.Id,
                    "Verification disposition must identify its source graph node."));
                continue;
            }
            scopedRecords.Add((nodeId, record));
        }

        foreach (var group in scopedRecords.GroupBy(item => (item.NodeId, item.Record.Source.Id)))
        {
            if (!expected.Contains(group.Key))
                issues.Add(new ArtifactAccountingIssue(ArtifactAccountingIssueCode.UnexpectedDisposition, group.Key.Id,
                    "Disposition references an unexpected graph-scoped source artifact.", group.Key.NodeId));
            if (group.Count() > 1)
                issues.Add(new ArtifactAccountingIssue(ArtifactAccountingIssueCode.DuplicateDisposition, group.Key.Id,
                    "Graph-scoped source artifact has more than one final disposition.", group.Key.NodeId));
            if (group.Any(item => item.Record.Disposition == ArtifactDisposition.Unaccounted))
                issues.Add(new ArtifactAccountingIssue(ArtifactAccountingIssueCode.UnaccountedArtifact, group.Key.Id,
                    "Graph-scoped source artifact is unaccounted.", group.Key.NodeId));
        }

        var recorded = scopedRecords.Select(item => (item.NodeId, item.Record.Source.Id)).ToHashSet();
        foreach (var missing in expected.Except(recorded))
            issues.Add(new ArtifactAccountingIssue(ArtifactAccountingIssueCode.MissingDisposition, missing.Id,
                "Graph-scoped source artifact has no disposition.", missing.NodeId));
        return new DomainList<ArtifactAccountingIssue>(issues);
    }
}

public sealed record LineageRecord
{
    public LineageBasis Basis { get; }
    public ArtifactReference Target { get; }
    public MigrationNodeId? TargetNodeId { get; }
    public DomainList<ArtifactReference> Sources { get; }
    public DomainList<MigrationNodeId> SourceNodeIds { get; }
    public DomainList<MigrationEdgeId> Path { get; }
    public string PlanHash { get; }

    public LineageRecord(
        ArtifactReference target,
        IEnumerable<ArtifactReference> sources,
        IEnumerable<MigrationEdgeId> path,
        string planHash,
        MigrationNodeId? targetNodeId = null,
        IEnumerable<MigrationNodeId>? sourceNodeIds = null,
        LineageBasis basis = LineageBasis.ExecutionObserved)
    {
        Basis = basis;
        Target = DomainGuard.NotNull(target, nameof(target));
        TargetNodeId = targetNodeId is { } nodeId ? DomainGuard.Required(nodeId, nameof(targetNodeId)) : null;
        Sources = new DomainList<ArtifactReference>(sources);
        SourceNodeIds = new DomainList<MigrationNodeId>(sourceNodeIds ?? []);
        Path = new DomainList<MigrationEdgeId>(path);
        PlanHash = DomainGuard.Required(planHash, nameof(planHash));

        if (Sources.Count == 0)
        {
            throw new ArgumentException("Target lineage must reference at least one source artifact.", nameof(sources));
        }

        if (Path.Count == 0)
        {
            throw new ArgumentException("Target lineage must reference at least one migration edge.", nameof(path));
        }

        if (SourceNodeIds.Count > 0 && SourceNodeIds.Count != Sources.Count)
            throw new ArgumentException("Source graph-node scope must align with source artifact references.", nameof(sourceNodeIds));
    }
}

public enum LineageBasis
{
    ExecutionObserved,
    GraphDerivedExpected
}

public enum LineageCoverageIssueCode
{
    MissingLineage,
    UnexpectedLineage,
    DuplicateLineage
}

public sealed record LineageCoverageIssue
{
    public LineageCoverageIssueCode Code { get; }
    public ArtifactId ArtifactId { get; }
    public MigrationNodeId? NodeId { get; }
    public string Message { get; }

    public LineageCoverageIssue(LineageCoverageIssueCode code, ArtifactId artifactId, string message, MigrationNodeId? nodeId = null)
    {
        Code = code;
        ArtifactId = DomainGuard.Required(artifactId, nameof(artifactId));
        Message = DomainGuard.Required(message, nameof(message));
        NodeId = nodeId;
    }
}

public sealed record LineageLedger
{
    public DomainList<LineageRecord> Records { get; }

    public LineageLedger(IEnumerable<LineageRecord> records) => Records = new DomainList<LineageRecord>(records);

    public DomainList<LineageCoverageIssue> ValidateCoverage(IEnumerable<ArtifactReference> targetArtifacts)
    {
        ArgumentNullException.ThrowIfNull(targetArtifacts);
        var expectedIds = targetArtifacts.Select(artifact => artifact.Id).ToHashSet();
        var groupedRecords = Records.GroupBy(record => record.Target.Id).ToArray();
        var issues = new List<LineageCoverageIssue>();

        foreach (var group in groupedRecords)
        {
            if (!expectedIds.Contains(group.Key))
            {
                issues.Add(new LineageCoverageIssue(
                    LineageCoverageIssueCode.UnexpectedLineage,
                    group.Key,
                    $"Lineage references unexpected target artifact '{group.Key}'."));
            }

            if (group.Count() > 1)
            {
                issues.Add(new LineageCoverageIssue(
                    LineageCoverageIssueCode.DuplicateLineage,
                    group.Key,
                    $"Target artifact '{group.Key}' has more than one lineage record."));
            }
        }

        var recordedIds = groupedRecords.Select(group => group.Key).ToHashSet();
        foreach (var missingId in expectedIds.Except(recordedIds))
        {
            issues.Add(new LineageCoverageIssue(
                LineageCoverageIssueCode.MissingLineage,
                missingId,
                $"Target artifact '{missingId}' has no lineage record."));
        }

        return new DomainList<LineageCoverageIssue>(issues);
    }

    public DomainList<LineageCoverageIssue> ValidateScopedCoverage(IEnumerable<GraphArtifactReference> targetArtifacts)
    {
        ArgumentNullException.ThrowIfNull(targetArtifacts);
        var expected = targetArtifacts.Select(item => (item.NodeId, item.Artifact.Id)).ToHashSet();
        var scopedRecords = new List<(MigrationNodeId NodeId, LineageRecord Record)>();
        var issues = new List<LineageCoverageIssue>();
        foreach (var record in Records)
        {
            if (record.TargetNodeId is not { } nodeId)
            {
                issues.Add(new LineageCoverageIssue(LineageCoverageIssueCode.MissingLineage, record.Target.Id,
                    "Verification lineage must identify its target graph node."));
                continue;
            }
            scopedRecords.Add((nodeId, record));
        }

        foreach (var group in scopedRecords.GroupBy(item => (item.NodeId, item.Record.Target.Id)))
        {
            if (!expected.Contains(group.Key))
                issues.Add(new LineageCoverageIssue(LineageCoverageIssueCode.UnexpectedLineage, group.Key.Id,
                    "Lineage references an unexpected graph-scoped target artifact.", group.Key.NodeId));
            if (group.Count() > 1)
                issues.Add(new LineageCoverageIssue(LineageCoverageIssueCode.DuplicateLineage, group.Key.Id,
                    "Graph-scoped target artifact has duplicate lineage records.", group.Key.NodeId));
        }

        var recorded = scopedRecords.Select(item => (item.NodeId, item.Record.Target.Id)).ToHashSet();
        foreach (var missing in expected.Except(recorded))
            issues.Add(new LineageCoverageIssue(LineageCoverageIssueCode.MissingLineage, missing.Id,
                "Graph-scoped target artifact has no lineage record.", missing.NodeId));
        return new DomainList<LineageCoverageIssue>(issues);
    }
}
