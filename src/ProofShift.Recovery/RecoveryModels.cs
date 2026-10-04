using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Evidence;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Verification;

namespace ProofShift.Recovery;

public static class RecoveryFingerprintVersions
{
    public const string Policy = "proofshift-recovery-policy-v1";
    public const string Assessment = "proofshift-recovery-assessment-v1";
    public const string Plan = "proofshift-recovery-plan-v1";
    public const string Rehearsal = "proofshift-recovery-rehearsal-v1";
    public const string DryRun = "proofshift-dry-run-fingerprint-v1";
    public const string Evidence = "proofshift-recovery-evidence-v1";
}

public enum RecoveryAssessmentState
{
    Building,
    Complete,
    Failed,
    Cancelled
}

public enum RecoveryAssessmentOutcome
{
    Passed,
    Failed,
    Error,
    Cancelled
}

public enum RecoveryCheckResult
{
    Pass,
    Fail,
    NotApplicable
}

public enum RecoveryRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum RecoveryValidationMode
{
    ExactRestoration,
    SemanticCompensation
}

public enum RecoveryRehearsalState
{
    Building,
    Complete,
    Failed,
    Cancelled
}

public enum RecoveryRehearsalOutcome
{
    Passed,
    Failed,
    NotRequired,
    Cancelled
}

public enum DryRunQualificationStatus
{
    Qualified,
    NotQualified,
    Error,
    Cancelled
}

public sealed record RecoveryExecutionBinding
{
    public RunId VerificationRunId { get; }
    public bool VerificationComplete { get; }
    public bool VerificationPassed { get; }
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
    public string EvidenceFingerprint { get; }

    public RecoveryExecutionBinding(VerificationRunRecord verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        VerificationRunId = verification.Id;
        VerificationComplete = verification.State == VerificationRunState.Complete;
        VerificationPassed = verification.Outcome is VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings;
        ConfigurationHash = verification.ConfigurationHash;
        GraphHash = verification.GraphHash;
        CheckpointId = verification.CheckpointId;
        CheckpointManifestHash = verification.CheckpointManifestHash;
        SourceFingerprint = verification.SourceFingerprint;
        ProjectionRunId = verification.ProjectionRunId;
        ProjectionManifestHash = verification.ProjectionManifestHash;
        ProjectionFingerprint = verification.ProjectionFingerprint;
        ProjectionSourceCount = verification.ProjectionSourceCount;
        ProjectionTargetCount = verification.ProjectionTargetCount;
        RuleSetFingerprint = verification.RuleSetFingerprint;
        EvidenceFingerprint = verification.EvidenceFingerprint;
    }
}

public sealed record RecoveryAssessmentIssue
{
    public string Code { get; }
    public string Message { get; }
    public MigrationEdgeId? EdgeId { get; }
    public ArtifactId? ArtifactId { get; }
    public MigrationNodeId? GraphNodeId { get; }

    public RecoveryAssessmentIssue(string code, string message, MigrationEdgeId? edgeId = null,
        ArtifactId? artifactId = null, MigrationNodeId? graphNodeId = null)
    {
        Code = Required(code, nameof(code));
        Message = Required(message, nameof(message));
        EdgeId = edgeId;
        ArtifactId = artifactId;
        GraphNodeId = graphNodeId;
        if ((artifactId is null) != (graphNodeId is null))
            throw new ArgumentException("Artifact recovery issues require both an artifact ID and graph-node scope.");
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record RecoveryEdgeAssessment
{
    public MigrationEdgeId EdgeId { get; }
    public string EdgeName { get; }
    public MigrationOperationType Operation { get; }
    public bool IsDestructive { get; }
    public DomainList<string> AffectedSemanticTypes { get; }
    public RecoveryMode? ConfiguredMode { get; }
    public string? Strategy { get; }
    public RecoveryCheckResult Result { get; }
    public bool CapabilityAvailable { get; }
    public bool CapabilityValidated { get; }
    public bool IsLossy { get; }
    public RecoveryRiskLevel Risk { get; }
    public RecoveryValidationMode ValidationMode { get; }
    public long AffectedSourceArtifacts { get; }
    public long AffectedTargetArtifacts { get; }
    public DomainList<GraphArtifactReference> Sources { get; }
    public DomainList<GraphArtifactReference> Targets { get; }
    public DomainList<RecoveryAssessmentIssue> Issues { get; }

    public RecoveryEdgeAssessment(MigrationEdgeId edgeId, string edgeName, MigrationOperationType operation,
        bool isDestructive, IEnumerable<string> affectedSemanticTypes,
        RecoveryMode? configuredMode, string? strategy, RecoveryCheckResult result, bool capabilityAvailable,
        bool capabilityValidated, bool isLossy, RecoveryRiskLevel risk, RecoveryValidationMode validationMode,
        long affectedSourceArtifacts, long affectedTargetArtifacts,
        IEnumerable<GraphArtifactReference> sources, IEnumerable<GraphArtifactReference> targets,
        IEnumerable<RecoveryAssessmentIssue> issues)
    {
        EdgeId = edgeId;
        EdgeName = Required(edgeName, nameof(edgeName));
        Operation = operation;
        IsDestructive = isDestructive;
        AffectedSemanticTypes = new DomainList<string>(affectedSemanticTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        ConfiguredMode = configuredMode;
        Strategy = string.IsNullOrWhiteSpace(strategy) ? null : strategy.Trim();
        Result = result;
        CapabilityAvailable = capabilityAvailable;
        CapabilityValidated = capabilityValidated;
        IsLossy = isLossy;
        Risk = risk;
        ValidationMode = validationMode;
        ArgumentOutOfRangeException.ThrowIfNegative(affectedSourceArtifacts);
        ArgumentOutOfRangeException.ThrowIfNegative(affectedTargetArtifacts);
        AffectedSourceArtifacts = affectedSourceArtifacts;
        AffectedTargetArtifacts = affectedTargetArtifacts;
        Sources = new DomainList<GraphArtifactReference>(sources);
        Targets = new DomainList<GraphArtifactReference>(targets);
        Issues = new DomainList<RecoveryAssessmentIssue>(issues);
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record RecoveryArtifactCoverage
{
    public GraphArtifactReference Target { get; }
    public string SemanticType { get; }
    public DomainList<MigrationEdgeId> EdgePath { get; }
    public DomainList<RecoveryMode> Modes { get; }
    public bool Covered { get; }
    public bool Recoverable { get; }
    public bool ApprovedIrreversible { get; }
    public string Reason { get; }

    public RecoveryArtifactCoverage(GraphArtifactReference target, string semanticType,
        IEnumerable<MigrationEdgeId> edgePath, IEnumerable<RecoveryMode> modes, bool covered,
        bool recoverable, bool approvedIrreversible, string reason)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        SemanticType = string.IsNullOrWhiteSpace(semanticType) ? throw new ArgumentException("Semantic type is required.", nameof(semanticType)) : semanticType.Trim();
        EdgePath = new DomainList<MigrationEdgeId>(edgePath);
        Modes = new DomainList<RecoveryMode>(modes.Distinct().Order());
        Covered = covered;
        Recoverable = recoverable;
        ApprovedIrreversible = approvedIrreversible;
        Reason = string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException("Reason is required.", nameof(reason)) : reason.Trim();
    }
}

public sealed record RecoverySemanticTypeCoverage(string SemanticType, long AffectedArtifacts,
    long RecoverableArtifacts, decimal? RecoverablePercentage);

public sealed record RecoveryCoverageSummary
{
    public long ExecutedEdges { get; }
    public long ReverseEdges { get; }
    public long RestoreEdges { get; }
    public long CompensateEdges { get; }
    public long IrreversibleEdges { get; }
    public long ValidatedEdges { get; }
    public long FailedEdges { get; }
    public long AffectedArtifacts { get; }
    public long RecoverableArtifacts { get; }
    public long IrrecoverableArtifacts { get; }
    public long UnknownArtifacts { get; }
    public decimal? RecoverablePercentage { get; }
    public IReadOnlyDictionary<string, RecoverySemanticTypeCoverage> BySemanticType { get; }

    public RecoveryCoverageSummary(IEnumerable<RecoveryEdgeAssessment> edges,
        IEnumerable<RecoveryArtifactCoverage> artifacts)
    {
        var edgeArray = edges.ToArray();
        var artifactArray = artifacts.ToArray();
        ExecutedEdges = edgeArray.LongLength;
        ReverseEdges = edgeArray.LongCount(edge => edge.ConfiguredMode == RecoveryMode.Reverse);
        RestoreEdges = edgeArray.LongCount(edge => edge.ConfiguredMode == RecoveryMode.Restore);
        CompensateEdges = edgeArray.LongCount(edge => edge.ConfiguredMode == RecoveryMode.Compensate);
        IrreversibleEdges = edgeArray.LongCount(edge => edge.ConfiguredMode == RecoveryMode.Irreversible);
        ValidatedEdges = edgeArray.LongCount(edge => edge.Result is RecoveryCheckResult.Pass or RecoveryCheckResult.NotApplicable);
        FailedEdges = edgeArray.LongCount(edge => edge.Result == RecoveryCheckResult.Fail);
        AffectedArtifacts = artifactArray.LongLength;
        RecoverableArtifacts = artifactArray.LongCount(artifact => artifact.Recoverable);
        IrrecoverableArtifacts = artifactArray.LongCount(artifact => !artifact.Recoverable);
        UnknownArtifacts = artifactArray.LongCount(artifact => !artifact.Covered);
        RecoverablePercentage = AffectedArtifacts == 0 ? null : decimal.Round(100m * RecoverableArtifacts / AffectedArtifacts, 4);
        BySemanticType = new System.Collections.ObjectModel.ReadOnlyDictionary<string, RecoverySemanticTypeCoverage>(
            artifactArray.GroupBy(artifact => artifact.SemanticType, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group =>
                {
                    var affected = group.LongCount();
                    var recoverable = group.LongCount(artifact => artifact.Recoverable);
                    return new RecoverySemanticTypeCoverage(group.Key, affected, recoverable,
                        affected == 0 ? null : decimal.Round(100m * recoverable / affected, 4));
                }, StringComparer.Ordinal));
    }
}

public sealed record RecoveryAssessment
{
    public static string FingerprintVersion => RecoveryFingerprintVersions.Assessment;
    public RecoveryAssessmentId Id { get; }
    public RecoveryAssessmentState State { get; }
    public RecoveryAssessmentOutcome Outcome { get; }
    public RecoveryExecutionBinding Binding { get; }
    public string PolicyFingerprint { get; }
    public string Fingerprint { get; }
    public RecoveryCoverageSummary Coverage { get; }
    public DomainList<RecoveryEdgeAssessment> Edges { get; }
    public DomainList<RecoveryArtifactCoverage> Artifacts { get; }
    public DomainList<RecoveryAssessmentIssue> Issues { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }

    public RecoveryAssessment(RecoveryAssessmentId id, RecoveryAssessmentState state, RecoveryAssessmentOutcome outcome,
        RecoveryExecutionBinding binding, string policyFingerprint, string fingerprint,
        IEnumerable<RecoveryEdgeAssessment> edges, IEnumerable<RecoveryArtifactCoverage> artifacts,
        IEnumerable<RecoveryAssessmentIssue> issues, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        Id = id;
        State = state;
        Outcome = outcome;
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        PolicyFingerprint = Hash(policyFingerprint, nameof(policyFingerprint));
        Fingerprint = Hash(fingerprint, nameof(fingerprint));
        Edges = new DomainList<RecoveryEdgeAssessment>(edges.OrderBy(edge => edge.EdgeId.Value));
        Artifacts = new DomainList<RecoveryArtifactCoverage>(artifacts.OrderBy(item => item.Target.NodeId.Value).ThenBy(item => item.Target.Artifact.Id.Value, StringComparer.Ordinal));
        Issues = new DomainList<RecoveryAssessmentIssue>(issues);
        Coverage = new RecoveryCoverageSummary(Edges, Artifacts);
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    private static string Hash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record RecoveryPlanStep
{
    public int Sequence { get; }
    public MigrationEdgeId EdgeId { get; }
    public string EdgeName { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public string Operation { get; }
    public RecoveryMode? Mode { get; }
    public string Strategy { get; }
    public DomainList<GraphArtifactReference> Scope { get; }
    public string Preconditions { get; }
    public string ExpectedOutcome { get; }
    public string ValidationMethod { get; }

    public RecoveryPlanStep(int sequence, MigrationEdge edge, SystemId systemId, StorageEndpointId endpointId,
        string strategy, IEnumerable<GraphArtifactReference> scope, string preconditions, string expectedOutcome,
        string validationMethod)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        Sequence = sequence;
        EdgeId = edge.Id;
        EdgeName = edge.Name;
        SystemId = systemId;
        EndpointId = endpointId;
        Operation = edge.Operation.Type.ToString();
        Mode = edge.Recovery?.Mode;
        Strategy = string.IsNullOrWhiteSpace(strategy) ? "reverse" : strategy.Trim();
        Scope = new DomainList<GraphArtifactReference>(scope);
        Preconditions = Required(preconditions, nameof(preconditions));
        ExpectedOutcome = Required(expectedOutcome, nameof(expectedOutcome));
        ValidationMethod = Required(validationMethod, nameof(validationMethod));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record RecoveryPlan
{
    public static string FingerprintVersion => RecoveryFingerprintVersions.Plan;
    public RecoveryPlanId Id { get; }
    public RunId VerificationRunId { get; }
    public CheckpointId SourceCheckpointId { get; }
    public DomainList<RecoveryCheckpointId> TargetRecoveryCheckpointIds { get; }
    public string PolicyFingerprint { get; }
    public string Fingerprint { get; }
    public DomainList<string> Preconditions { get; }
    public DomainList<RecoveryPlanStep> Steps { get; }
    public DomainList<RecoveryAssessmentIssue> IrreversibleRisks { get; }
    public DomainList<EvidenceReference> EvidenceReferences { get; }

    public RecoveryPlan(RecoveryPlanId id, RunId verificationRunId, CheckpointId sourceCheckpointId,
        IEnumerable<RecoveryCheckpointId> targetRecoveryCheckpointIds, string policyFingerprint,
        string fingerprint, IEnumerable<string> preconditions, IEnumerable<RecoveryPlanStep> steps,
        IEnumerable<RecoveryAssessmentIssue> irreversibleRisks, IEnumerable<EvidenceReference> evidenceReferences)
    {
        Id = id;
        VerificationRunId = verificationRunId;
        SourceCheckpointId = sourceCheckpointId;
        TargetRecoveryCheckpointIds = new DomainList<RecoveryCheckpointId>(targetRecoveryCheckpointIds);
        PolicyFingerprint = RequiredHash(policyFingerprint, nameof(policyFingerprint));
        Fingerprint = RequiredHash(fingerprint, nameof(fingerprint));
        Preconditions = new DomainList<string>(preconditions);
        Steps = new DomainList<RecoveryPlanStep>(steps.OrderBy(step => step.Sequence));
        IrreversibleRisks = new DomainList<RecoveryAssessmentIssue>(irreversibleRisks);
        EvidenceReferences = new DomainList<EvidenceReference>(evidenceReferences);
    }

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record RecoveryRehearsal
{
    public static string FingerprintVersion => RecoveryFingerprintVersions.Rehearsal;
    public const string Environment = "Shadow";
    public RecoveryRehearsalId Id { get; }
    public RecoveryRehearsalState State { get; }
    public RecoveryRehearsalOutcome Outcome { get; }
    public RunId VerificationRunId { get; }
    public string BaselineFingerprint { get; }
    public string? RestoredFingerprint { get; }
    public string? ShadowCleanupFingerprint { get; }
    public string Fingerprint { get; }
    public long MutatedArtifacts { get; }
    public DomainList<ShadowRecoveryCheckpoint> Checkpoints { get; }
    public DomainList<RecoveryAssessmentIssue> Issues { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }

    public RecoveryRehearsal(RecoveryRehearsalId id, RecoveryRehearsalState state,
        RecoveryRehearsalOutcome outcome, RunId verificationRunId, string baselineFingerprint,
        string? restoredFingerprint, string? shadowCleanupFingerprint, string fingerprint, long mutatedArtifacts,
        IEnumerable<ShadowRecoveryCheckpoint> checkpoints, IEnumerable<RecoveryAssessmentIssue> issues,
        DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        Id = id;
        State = state;
        Outcome = outcome;
        VerificationRunId = verificationRunId;
        BaselineFingerprint = RequiredHash(baselineFingerprint, nameof(baselineFingerprint));
        RestoredFingerprint = restoredFingerprint is null ? null : RequiredHash(restoredFingerprint, nameof(restoredFingerprint));
        ShadowCleanupFingerprint = shadowCleanupFingerprint is null ? null : RequiredHash(shadowCleanupFingerprint, nameof(shadowCleanupFingerprint));
        Fingerprint = RequiredHash(fingerprint, nameof(fingerprint));
        ArgumentOutOfRangeException.ThrowIfNegative(mutatedArtifacts);
        MutatedArtifacts = mutatedArtifacts;
        Checkpoints = new DomainList<ShadowRecoveryCheckpoint>(checkpoints.OrderBy(checkpoint => checkpoint.SystemId.Value, StringComparer.Ordinal)
            .ThenBy(checkpoint => checkpoint.EndpointId.Value, StringComparer.Ordinal));
        Issues = new DomainList<RecoveryAssessmentIssue>(issues);
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record DryRunQualification
{
    public static string FingerprintVersion => RecoveryFingerprintVersions.DryRun;
    public DryRunId Id { get; }
    public DryRunQualificationStatus Status { get; }
    public string DryRunFingerprint { get; }
    public string PolicyFingerprint { get; }
    public string RecoveryAssessmentFingerprint { get; }
    public string RecoveryPlanFingerprint { get; }
    public string RecoveryRehearsalFingerprint { get; }
    public string CheckpointFingerprint { get; }
    public string ProjectionFingerprint { get; }
    public string RuleSetFingerprint { get; }
    public string VerificationEvidenceFingerprint { get; }
    public string RecoveryEvidenceFingerprint { get; }
    public DomainList<RecoveryAssessmentIssue> Reasons { get; }

    public DryRunQualification(DryRunId id, DryRunQualificationStatus status, string dryRunFingerprint,
        string policyFingerprint, string recoveryAssessmentFingerprint, string recoveryPlanFingerprint,
        string recoveryRehearsalFingerprint, RecoveryExecutionBinding binding, string recoveryEvidenceFingerprint,
        IEnumerable<RecoveryAssessmentIssue> reasons)
    {
        Id = id;
        Status = status;
        DryRunFingerprint = Hash(dryRunFingerprint, nameof(dryRunFingerprint));
        PolicyFingerprint = Hash(policyFingerprint, nameof(policyFingerprint));
        RecoveryAssessmentFingerprint = Hash(recoveryAssessmentFingerprint, nameof(recoveryAssessmentFingerprint));
        RecoveryPlanFingerprint = Hash(recoveryPlanFingerprint, nameof(recoveryPlanFingerprint));
        RecoveryRehearsalFingerprint = Hash(recoveryRehearsalFingerprint, nameof(recoveryRehearsalFingerprint));
        CheckpointFingerprint = binding.SourceFingerprint;
        ProjectionFingerprint = binding.ProjectionFingerprint;
        RuleSetFingerprint = binding.RuleSetFingerprint;
        VerificationEvidenceFingerprint = binding.EvidenceFingerprint;
        RecoveryEvidenceFingerprint = Hash(recoveryEvidenceFingerprint, nameof(recoveryEvidenceFingerprint));
        Reasons = new DomainList<RecoveryAssessmentIssue>(reasons);
    }

    private static string Hash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }
}

public sealed record RecoveryEvidenceGraph
{
    public static string FormatVersion => RecoveryFingerprintVersions.Evidence;
    public EvidenceGraph Graph { get; }
    public string VerificationEvidenceFingerprint { get; }
    public string Fingerprint { get; }

    public RecoveryEvidenceGraph(EvidenceGraph graph, string verificationEvidenceFingerprint)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        VerificationEvidenceFingerprint = RequiredHash(verificationEvidenceFingerprint, nameof(verificationEvidenceFingerprint));
        Fingerprint = Hash($"{FormatVersion}\n{Graph.Fingerprint}\n{VerificationEvidenceFingerprint}");
    }

    private static string RequiredHash(string value, string parameterName)
    {
        var hash = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Hash is required.", parameterName) : value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        return hash;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record RecoveryRunResult(
    DryRunId Id,
    RecoveryAssessment Assessment,
    RecoveryPlan Plan,
    RecoveryRehearsal Rehearsal,
    RecoveryEvidenceGraph EvidenceGraph,
    DryRunQualification Qualification);
