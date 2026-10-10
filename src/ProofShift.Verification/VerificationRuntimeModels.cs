using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Snapshots;

namespace ProofShift.Verification;

internal enum VerificationRuleExecutionMode
{
    GlobalRuleReference,
    PartitionLocalExperimental
}

public static class VerificationIssueCodes
{
    public const string ContextMismatch = "PSVERIFY001";
    public const string UnknownRuleType = "PSVERIFY002";
    public const string RuleExecutionFailure = "PSVERIFY003";
    public const string CheckpointMismatch = "PSVERIFY004";
    public const string ProjectionMismatch = "PSVERIFY005";
    public const string MissingTarget = "PSVERIFY006";
    public const string UnexpectedTarget = "PSVERIFY007";
    public const string DuplicateTarget = "PSVERIFY008";
    public const string MissingLineage = "PSVERIFY009";
    public const string DuplicateDisposition = "PSVERIFY010";
    public const string UnaccountedSource = "PSVERIFY011";
    public const string EvidenceIntegrityFailure = "PSVERIFY012";
    public const string AttributeMismatch = "PSVERIFY013";
    public const string WorkspaceFailure = "PSVERIFY014";
}

public enum VerificationRunState
{
    Building,
    Complete,
    Failed,
    Cancelled
}

public enum VerificationOutcome
{
    Passed,
    PassedWithWarnings,
    Failed,
    Error,
    Cancelled
}

public sealed record ProjectionVerificationBinding
{
    public RunId ProjectionRunId { get; }
    public string ProjectionStatus { get; }
    public string ProjectionManifestHash { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public CheckpointId CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public string SourceFingerprint { get; }
    public string ProjectionFingerprint { get; }
    public string ProjectionFingerprintVersion { get; }
    public long ProjectionSourceCount { get; }
    public long ProjectionTargetCount { get; }
    public string JournalPath { get; }
    public IReadOnlyDictionary<string, string> ConnectorVersions { get; }

    public ProjectionVerificationBinding(
        RunId projectionRunId,
        string configurationHash,
        string graphHash,
        CheckpointId checkpointId,
        string checkpointManifestHash,
        string sourceFingerprint,
        string projectionFingerprint,
        string projectionFingerprintVersion,
        long projectionSourceCount,
        long projectionTargetCount,
        string projectionStatus,
        string projectionManifestHash,
        string journalPath,
        IEnumerable<KeyValuePair<string, string>> connectorVersions)
    {
        ProjectionRunId = projectionRunId;
        ProjectionStatus = Required(projectionStatus, nameof(projectionStatus)).ToLowerInvariant();
        ProjectionManifestHash = RequiredHash(projectionManifestHash, nameof(projectionManifestHash));
        ConfigurationHash = RequiredHash(configurationHash, nameof(configurationHash));
        GraphHash = RequiredHash(graphHash, nameof(graphHash));
        CheckpointId = checkpointId;
        CheckpointManifestHash = RequiredHash(checkpointManifestHash, nameof(checkpointManifestHash));
        SourceFingerprint = RequiredHash(sourceFingerprint, nameof(sourceFingerprint));
        ProjectionFingerprint = RequiredHash(projectionFingerprint, nameof(projectionFingerprint));
        ProjectionFingerprintVersion = Required(projectionFingerprintVersion, nameof(projectionFingerprintVersion));
        ArgumentOutOfRangeException.ThrowIfNegative(projectionSourceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(projectionTargetCount);
        ProjectionSourceCount = projectionSourceCount;
        ProjectionTargetCount = projectionTargetCount;
        JournalPath = Required(journalPath, nameof(journalPath));
        ConnectorVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(connectorVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = Required(value, parameterName).ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record ExternalTargetEndpoint
{
    public string NodeKey { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public ConnectorId ConnectorId { get; }
    public string ConnectorVersion { get; }

    public ExternalTargetEndpoint(string nodeKey, SystemId systemId, StorageEndpointId endpointId,
        ConnectorId connectorId, string connectorVersion)
    {
        NodeKey = Required(nodeKey, nameof(nodeKey));
        SystemId = systemId;
        EndpointId = endpointId;
        ConnectorId = connectorId;
        ConnectorVersion = Required(connectorVersion, nameof(connectorVersion));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record ExternalMigrationObservation
{
    public RunId ObservationRunId { get; }
    public string ObservationId { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public CheckpointId CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public string SourceFingerprint { get; }
    public DomainList<ExternalTargetEndpoint> Targets { get; }
    public DateTimeOffset ObservedAt { get; }

    public ExternalMigrationObservation(RunId observationRunId, string observationId, string configurationHash,
        string graphHash, CheckpointId checkpointId, string checkpointManifestHash, string sourceFingerprint,
        IEnumerable<ExternalTargetEndpoint> targets, DateTimeOffset observedAt)
    {
        ObservationRunId = observationRunId;
        ObservationId = Required(observationId, nameof(observationId));
        ConfigurationHash = Hash(configurationHash, nameof(configurationHash));
        GraphHash = Hash(graphHash, nameof(graphHash));
        CheckpointId = checkpointId;
        CheckpointManifestHash = Hash(checkpointManifestHash, nameof(checkpointManifestHash));
        SourceFingerprint = Hash(sourceFingerprint, nameof(sourceFingerprint));
        Targets = new DomainList<ExternalTargetEndpoint>(targets.OrderBy(item => item.NodeKey, StringComparer.Ordinal));
        if (Targets.Select(item => item.NodeKey).Distinct(StringComparer.Ordinal).Count() != Targets.Count)
            throw new ArgumentException("External observation target nodes must be unique.", nameof(targets));
        ObservedAt = observedAt;
    }

    private static string Hash(string value, string parameterName)
    {
        var hash = Required(value, parameterName).ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record VerificationRuleDefinition
{
    public RuleId Id { get; }
    public string Type { get; }
    public string Version { get; }
    public EvidenceSeverity Severity { get; }
    public DomainDictionary<string> Options { get; }
    public DomainDictionary<ValueNode> StructuredOptions { get; }
    public bool UsesStructuredOptions { get; }
    public RuleSourceLocation? SourceLocation { get; init; }

    public VerificationRuleDefinition(RuleId id, string type, string version, EvidenceSeverity severity,
        IEnumerable<KeyValuePair<string, string>>? options = null,
        IEnumerable<KeyValuePair<string, ValueNode>>? structuredOptions = null)
    {
        Id = id;
        Type = Required(type, nameof(type));
        Version = Required(version, nameof(version));
        Severity = severity;
        Options = new DomainDictionary<string>(options ?? []);
        StructuredOptions = new DomainDictionary<ValueNode>(structuredOptions ??
            Options.Select(pair => new KeyValuePair<string, ValueNode>(pair.Key, new StringValue(pair.Value))));
        UsesStructuredOptions = structuredOptions is not null;
        if (UsesStructuredOptions && Options.Count > 0)
            throw new ArgumentException("Use either legacy or structured rule options, not both.", nameof(structuredOptions));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record RuleSourceLocation(string File, string Path, int? Line, int? Column);

public sealed record VerificationFinding
{
    public RuleId RuleId { get; }
    public string RuleVersion { get; }
    public EvidenceType Type { get; }
    public EvidenceResult Result { get; }
    public EvidenceSeverity Severity { get; }
    public string Code { get; }
    public string Explanation { get; }
    public DomainList<EvidenceReference> Inputs { get; }
    public EvidenceValue? Expected { get; }
    public EvidenceValue? Actual { get; }
    public string StableKey { get; }

    public VerificationFinding(EvidenceType type, EvidenceResult result, EvidenceSeverity severity,
        string code, string explanation, string stableKey, IEnumerable<EvidenceReference>? inputs = null,
        EvidenceValue? expected = null, EvidenceValue? actual = null, RuleId? ruleId = null, string? ruleVersion = null)
    {
        RuleId = ruleId ?? new RuleId("proofshift.verification.materialized-state");
        RuleVersion = string.IsNullOrWhiteSpace(ruleVersion) ? "1" : ruleVersion.Trim();
        Type = type;
        Result = result;
        Severity = severity;
        Code = Required(code, nameof(code));
        Explanation = Required(explanation, nameof(explanation));
        StableKey = Required(stableKey, nameof(stableKey));
        Inputs = new DomainList<EvidenceReference>(inputs ?? []);
        Expected = expected;
        Actual = actual;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public interface IVerificationRule
{
    RuleId Id { get; }
    string Version { get; }
    VerificationScope Scope { get; }
    IReadOnlyCollection<VerificationOrderingKey> RequiredOrderingKeys => [];
    IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context, CancellationToken cancellationToken);
}

public interface IVerificationPartitionFindingMerger
{
    IReadOnlyCollection<VerificationFinding> MergePartitionFindings(VerificationExecutionContext context,
        IReadOnlyList<IReadOnlyList<VerificationFinding>> partitionFindings);
}

public enum VerificationOrderingRole
{
    Grouping,
    Ordering,
    Lookup
}

public sealed record VerificationOrderingKey(string SemanticType, string Field,
    VerificationOrderingRole Role, bool Descending = false);

public sealed record VerificationJournalEntry
{
    public string Result { get; }
    public string? TargetNode { get; }
    public ArtifactReference? Target { get; }
    public DomainList<VerificationGraphArtifact> Sources { get; }
    public MigrationEdgeId EdgeId { get; }
    public string EdgeName { get; }
    public string EdgeVersion { get; }
    public string? FailureCode { get; }

    public VerificationJournalEntry(string result, string? targetNode, ArtifactReference? target,
        IEnumerable<VerificationGraphArtifact> sources, MigrationEdgeId edgeId, string edgeName, string edgeVersion, string? failureCode)
    {
        Result = Required(result, nameof(result)).ToLowerInvariant();
        TargetNode = string.IsNullOrWhiteSpace(targetNode) ? null : targetNode.Trim();
        Target = target;
        Sources = new DomainList<VerificationGraphArtifact>(sources);
        EdgeId = edgeId;
        EdgeName = Required(edgeName, nameof(edgeName));
        EdgeVersion = Required(edgeVersion, nameof(edgeVersion));
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record VerificationJournalValidationResult(long ProducedEntryCount, bool SourceArtifactsMatch,
    bool ProducedTargetsMatchExpectations, bool ProducedAncestryIsUnique);

public sealed record VerificationSourceFact(string NodeKey, string SemanticType, ArtifactReference Artifact, int ProducedEntries,
    int ExcludedEntries, int FailedEntries, IReadOnlyCollection<ArtifactId> TargetIds,
    IReadOnlyCollection<MigrationEdgeId> EdgeIds);

public sealed record VerificationGraphArtifact(string NodeKey, ArtifactReference Artifact);

public enum VerificationArtifactRole
{
    Source,
    ExpectedTarget,
    ActualTarget
}

public sealed record VerificationArtifactRecord
{
    private readonly DomainList<string>? _declaredFields;
    public string NodeKey { get; }
    public VerificationArtifactRole Role { get; }
    public string SemanticType { get; }
    public ArtifactReference Artifact { get; }
    public DomainDictionary<ValueNode> Values { get; }
    public DomainList<RelationshipReference> Relationships { get; }
    public TemporalMetadata? Temporal { get; }
    public bool HasDeclaredFieldSet => _declaredFields is not null;

    public VerificationArtifactRecord(string nodeKey, VerificationArtifactRole role, string semanticType,
        ArtifactReference artifact, IEnumerable<KeyValuePair<string, ValueNode>> values,
        IEnumerable<RelationshipReference>? relationships = null, TemporalMetadata? temporal = null,
        IEnumerable<string>? declaredFields = null)
    {
        NodeKey = string.IsNullOrWhiteSpace(nodeKey) ? throw new ArgumentException("Node key is required.", nameof(nodeKey)) : nodeKey.Trim();
        Role = role;
        SemanticType = string.IsNullOrWhiteSpace(semanticType) ? throw new ArgumentException("Semantic type is required.", nameof(semanticType)) : semanticType.Trim();
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        Values = new DomainDictionary<ValueNode>(values);
        Relationships = new DomainList<RelationshipReference>(relationships ?? []);
        Temporal = temporal;
        _declaredFields = declaredFields is null ? null : new DomainList<string>(declaredFields);
    }

    public void RequireDeclaredField(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (_declaredFields is not null && !_declaredFields.Contains(field, StringComparer.Ordinal))
            throw new VerificationRuleException("PSRULE008",
                $"Rule accessed field '{field}' outside the declared Verification workset for semantic type '{SemanticType}'.");
    }
}

public sealed record VerificationTargetFact(string NodeKey, string SemanticType, ArtifactReference Artifact, int ActualCount,
    IReadOnlyCollection<VerificationGraphArtifact> Sources, IReadOnlyCollection<MigrationEdgeId> EdgeIds);

public sealed record VerificationAttributeComparison(string NodeKey, string SourceNodeKey, string SemanticType,
    ArtifactReference Source, ArtifactReference ExpectedTarget,
    ArtifactReference ActualTarget, MigrationEdgeId EdgeId, string EdgeName, string Field, string ExpectedFingerprint,
    string ActualFingerprint, bool Matches);

public interface IVerificationWorkspace : IAsyncDisposable
{
    long SourceArtifactCount { get; }
    long ExpectedTargetCount { get; }
    long ActualTargetCount { get; }

    void SetRuleEvaluationPartition(int? partitionIndex) { }

    Task AddSourceArtifactAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken);
    Task<bool> ContainsSourceArtifactAsync(string nodeKey, ArtifactReference artifact, CancellationToken cancellationToken);
    Task AddExpectedTargetAsync(string nodeKey, RecordEnvelope expected, string sourceNodeKey, RecordEnvelope source,
        MigrationEdge edge, CancellationToken cancellationToken);
    Task<bool> ContainsExpectedTargetAsync(string nodeKey, ArtifactReference target, string sourceNodeKey,
        ArtifactReference source, MigrationEdgeId edgeId, CancellationToken cancellationToken);
    Task AddJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken);
    Task<VerificationJournalValidationResult> ValidateJournalEntriesAsync(CancellationToken cancellationToken);
    Task AddTargetObservationAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken);

    IAsyncEnumerable<VerificationSourceFact> ReadSourceFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationSourceFact> ReadGraphDerivedSourceFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsAsync(VerificationArtifactRole role,
        string? nodeKey, string? semanticType, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? orderByFields = null);
    IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsByKeysAsync(VerificationArtifactRole role,
        string? nodeKey, string? semanticType, IReadOnlyCollection<VerificationOrderingKey> orderByKeys,
        CancellationToken cancellationToken) =>
        ReadArtifactRecordsAsync(role, nodeKey, semanticType, cancellationToken,
            orderByKeys.Select(key => key.Field).ToArray());
    async Task<bool> ContainsFieldValueAsync(VerificationArtifactRole role, string nodeKey, string semanticType,
        string field, ValueNode value, CancellationToken cancellationToken)
    {
        await foreach (var record in ReadArtifactRecordsAsync(role, nodeKey, semanticType, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            if (record.Values.TryGetValue(field, out var actual) && Equals(actual, value)) return true;
        return false;
    }
    IAsyncEnumerable<VerificationTargetFact> ReadMaterializedJournalTargetsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadGraphDerivedTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadMissingGraphDerivedTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadActualTargetsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadMissingTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedGraphTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadDuplicateTargetFactsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutLineageAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutGraphDerivedLineageAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationAttributeComparison> ReadAttributeComparisonsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LineageRecord> ReadLineageAsync(string graphHash,
        IReadOnlyDictionary<string, MigrationNodeId> graphNodeIds, CancellationToken cancellationToken);
    IAsyncEnumerable<LineageRecord> ReadGraphDerivedLineageAsync(string graphHash,
        IReadOnlyDictionary<string, MigrationNodeId> graphNodeIds, CancellationToken cancellationToken);
}

public sealed record VerificationExecutionContext
{
    public LoadedProjectConfiguration Configuration { get; }
    public MigrationGraph Graph { get; }
    public ProjectionVerificationBinding? Projection { get; }
    public ExternalMigrationObservation? ExternalObservation { get; }
    public DomainList<EvidenceReference> BindingReferences { get; }
    public RunId VerificationRunId { get; }
    public IVerificationWorkspace Workspace { get; }
    public int? EvaluationPartition { get; init; }
    public int PartitionCount { get; init; } = 1;

    public VerificationExecutionContext ForPartition(int partitionIndex, int partitionCount) => this with
    {
        EvaluationPartition = partitionIndex,
        PartitionCount = partitionCount
    };

    public VerificationExecutionContext(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ProjectionVerificationBinding projection, RunId verificationRunId, IVerificationWorkspace workspace)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        BindingReferences = new DomainList<EvidenceReference>([
            new(checkpointId: projection.CheckpointId), new(projectionRunId: projection.ProjectionRunId)
        ]);
        VerificationRunId = verificationRunId;
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public VerificationExecutionContext(LoadedProjectConfiguration configuration, MigrationGraph graph,
        ExternalMigrationObservation externalObservation, RunId verificationRunId, IVerificationWorkspace workspace)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        ExternalObservation = externalObservation ?? throw new ArgumentNullException(nameof(externalObservation));
        BindingReferences = new DomainList<EvidenceReference>([
            new(checkpointId: externalObservation.CheckpointId)
        ]);
        VerificationRunId = verificationRunId;
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }
}

public sealed record VerificationRunRecord
{
    public RunId Id { get; }
    public VerificationRunState State { get; }
    public VerificationOutcome Outcome { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public CheckpointId CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public string SourceFingerprint { get; }
    public RunId ProjectionRunId { get; }
    public string ProjectionManifestHash { get; }
    public string ProjectionFingerprint { get; }
    public long ProjectionSourceCount { get; }
    public long ProjectionTargetCount { get; }
    public string RuleSetFingerprint { get; }
    public VerificationRuntimeFingerprint Runtime { get; }
    public string EvidenceFingerprint { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }
    public int RulesExecuted { get; }
    public int PassedRules { get; }
    public int FailedRules { get; }
    public int WarningRules { get; }
    public int ExecutionErrorCount { get; }

    public VerificationRunRecord(RunId id, VerificationRunState state, VerificationOutcome outcome,
        ProjectionVerificationBinding binding, string ruleSetFingerprint, VerificationRuntimeFingerprint runtime,
        string evidenceFingerprint, DateTimeOffset startedAt, DateTimeOffset completedAt,
        int rulesExecuted, int passedRules, int failedRules, int warningRules, int executionErrorCount)
    {
        Id = id;
        State = state;
        Outcome = outcome;
        ConfigurationHash = binding.ConfigurationHash;
        GraphHash = binding.GraphHash;
        CheckpointId = binding.CheckpointId;
        CheckpointManifestHash = binding.CheckpointManifestHash;
        SourceFingerprint = binding.SourceFingerprint;
        ProjectionRunId = binding.ProjectionRunId;
        ProjectionManifestHash = binding.ProjectionManifestHash;
        ProjectionFingerprint = binding.ProjectionFingerprint;
        ProjectionSourceCount = binding.ProjectionSourceCount;
        ProjectionTargetCount = binding.ProjectionTargetCount;
        RuleSetFingerprint = RequiredHash(ruleSetFingerprint, nameof(ruleSetFingerprint));
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        EvidenceFingerprint = RequiredHash(evidenceFingerprint, nameof(evidenceFingerprint));
        StartedAt = startedAt;
        CompletedAt = completedAt;
        RulesExecuted = rulesExecuted;
        PassedRules = passedRules;
        FailedRules = failedRules;
        WarningRules = warningRules;
        ExecutionErrorCount = executionErrorCount;
    }

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record VerificationRuntimeFingerprint
{
    public string ProofShiftVersion { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public IReadOnlyDictionary<string, string> ConnectorVersions { get; }
    public IReadOnlyDictionary<string, string> DomainPackVersions { get; }
    public IReadOnlyDictionary<string, string> RuleVersions { get; }
    public string CheckpointFormatVersion { get; }
    public string ProjectionFingerprintVersion { get; }
    public string EvidenceFormatVersion { get; }
    public string Fingerprint { get; }

    public VerificationRuntimeFingerprint(string proofShiftVersion, ProjectionVerificationBinding binding,
        IEnumerable<KeyValuePair<string, string>> domainPackVersions, IEnumerable<KeyValuePair<string, string>> ruleVersions)
    {
        ProofShiftVersion = Required(proofShiftVersion, nameof(proofShiftVersion));
        ConfigurationHash = binding.ConfigurationHash;
        GraphHash = binding.GraphHash;
        ConnectorVersions = ToSorted(binding.ConnectorVersions);
        DomainPackVersions = ToSorted(domainPackVersions);
        RuleVersions = ToSorted(ruleVersions);
        CheckpointFormatVersion = SnapshotFingerprints.MaterializedFormatVersion;
        ProjectionFingerprintVersion = binding.ProjectionFingerprintVersion;
        EvidenceFormatVersion = EvidenceFormat.CanonicalizationVersion;
        Fingerprint = ComputeFingerprint();
    }

    private string ComputeFingerprint()
    {
        var parts = new List<string>
        {
            ProofShiftVersion, ConfigurationHash, GraphHash, CheckpointFormatVersion,
            ProjectionFingerprintVersion, EvidenceFormatVersion
        };
        Add(ConnectorVersions, "connector");
        Add(DomainPackVersions, "pack");
        Add(RuleVersions, "rule");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();

        void Add(IReadOnlyDictionary<string, string> values, string prefix)
        {
            foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                parts.Add(prefix);
                parts.Add(pair.Key);
                parts.Add(pair.Value);
            }
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyDictionary<string, string> ToSorted(IEnumerable<KeyValuePair<string, string>> values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record VerificationResult(VerificationRunRecord Run, Evidence.EvidenceGraph EvidenceGraph,
    IReadOnlyList<VerificationFinding> Findings, VerificationLedgerStoreReceipt Ledger);

public sealed record ExternalVerificationRunRecord(RunId Id, string ObservationId, RunId ObservationRunId,
    string ConfigurationHash, string GraphHash, CheckpointId CheckpointId, string CheckpointManifestHash,
    string SourceFingerprint, string TargetFingerprint, long SourceArtifactCount, long TargetArtifactCount,
    string RuleSetFingerprint, string RuntimeFingerprint, string EvidenceFingerprint, VerificationOutcome Outcome,
    int RulesExecuted, int FailedRules, int WarningRules, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);

public sealed record ExternalVerificationResult(ExternalVerificationRunRecord Run, Evidence.EvidenceGraph EvidenceGraph,
    IReadOnlyList<VerificationFinding> Findings, VerificationLedgerStoreReceipt Ledger);
