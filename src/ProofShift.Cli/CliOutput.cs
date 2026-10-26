using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Recovery;

namespace ProofShift.Cli;

internal static class CliOutput
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static ValidationIssueOutput ToJsonIssue(ProofShift.Configuration.ConfigurationValidationIssue issue) => new(
        issue.Code, issue.Severity.ToString().ToLowerInvariant(), issue.File, issue.Path, issue.Message);

    internal static ValidationIssueOutput ToJsonIssue(ProofShift.Graph.GraphValidationIssue issue) => new(
        issue.Code, issue.Severity.ToString().ToLowerInvariant(), issue.File, issue.Path, issue.Message);
}

internal sealed record VersionInformation(
    string ProductVersion,
    string SourceRevision,
    string SourceTree,
    string TargetFramework);

internal sealed record SnapshotOutput(
    string CheckpointId,
    string Status,
    string? ManifestHash,
    string? SourceFingerprint,
    bool? CrossSystemAtomic,
    double? CaptureWindowMilliseconds,
    CheckpointConsistencyOutput[] EndpointConsistency,
    int ExpectedSourceNodes,
    int CapturedSourceNodes,
    long CapturedArtifacts,
    long CapturedBytes,
    string? FailureCode,
    string? CheckpointPath);

internal sealed record CheckpointConsistencyOutput(string Endpoint, string RequestedStrategy,
    string EffectiveStrategy, string Guarantee, string? Downgrade);

internal sealed record ValidationOutput(
    bool Valid,
    int? ConfigurationVersion,
    string? CanonicalizationVersion,
    string? ConfigurationHash,
    IEnumerable<ValidationIssueOutput> Issues);

internal sealed record ValidationIssueOutput(
    string Code,
    string Severity,
    string? File,
    string? Path,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Column = null);

internal sealed record SourceCoverageOutput(int Accounted, int Total);

internal sealed record TargetProvenanceOutput(int Connected, int Total);

internal sealed record PlanValidationOutput(
    bool Valid,
    int? GraphVersion,
    string CanonicalizationVersion,
    string? GraphHash,
    int Nodes,
    int Edges,
    int SourceNodes,
    int TargetNodes,
    SourceCoverageOutput SourceCoverage,
    TargetProvenanceOutput TargetProvenance,
    int RecoveryDefinitions,
    IReadOnlyDictionary<string, int> OperationCounts,
    IEnumerable<ValidationIssueOutput> Issues);

internal sealed record ProjectionOutput(
    string RunId,
    string RunType,
    string Status,
    string ProjectId,
    string ConfigurationHash,
    string MigrationGraphHash,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyDictionary<string, string> ConnectorVersions,
    IReadOnlyCollection<string> ShadowDestinations,
    long SourceArtifactCount,
    long TargetArtifactCount,
    long FailureCount,
    string? FailureCode,
    string? ProjectionFingerprint,
    string ProjectionFingerprintVersion,
    string ProjectionJournal,
    string? CheckpointId,
    string? CheckpointSourceFingerprint,
    string? CheckpointManifestHash);

internal sealed record VerificationOutput(
    string RunId,
    string Outcome,
    string ConfigurationHash,
    string GraphHash,
    string CheckpointId,
    string ProjectionRunId,
    string ProjectionManifestHash,
    string RuleSetFingerprint,
    string EvidenceFingerprint,
    int Findings,
    int FailedRules,
    int WarningRules,
    string EvidencePath);

internal sealed record ExternalVerificationOutput(
    string AssuranceScope,
    string VerificationRunId,
    string ObservationId,
    string ObservationRunId,
    string CheckpointId,
    string ConfigurationHash,
    string GraphHash,
    string SourceFingerprint,
    string TargetFingerprint,
    string RuleSetFingerprint,
    string VerificationOutcome,
    int VerificationFailureCount,
    long SourceArtifactCount,
    long TargetArtifactCount,
    long AccountedSourceCount,
    long UnaccountedSourceCount,
    long LineagedTargetCount,
    long UnexplainedTargetCount,
    string EvidenceFingerprint,
    string LineageBasis,
    bool RecoveryAssessed,
    bool QualificationAvailable,
    string EvidencePath);

internal sealed record DryRunOutput(
    string? Project,
    string Outcome,
    string? DryRunId,
    string? CheckpointStatus,
    string? CheckpointId,
    string? CheckpointManifestHash,
    string? SourceFingerprint,
    bool? CrossSystemAtomic,
    CheckpointConsistencyOutput[] EndpointConsistency,
    string? ProjectionRunId,
    string? ProjectionStatus,
    string? ProjectionFingerprint,
    string? ProjectionManifestHash,
    string? VerificationRunId,
    string? VerificationOutcome,
    int? VerificationFailures,
    int? UnaccountedSources,
    int? UnexplainedTargets,
    string? RuleSetFingerprint,
    string? VerificationEvidenceFingerprint,
    string? RecoveryAssessmentFingerprint,
    string? RecoveryAssessmentFingerprintVersion,
    string? RecoveryPolicyFingerprint,
    string? RecoveryPolicyFingerprintVersion,
    string? RecoveryPlanFingerprint,
    string? RecoveryPlanFingerprintVersion,
    string? RehearsalOutcome,
    string? RecoveryRehearsalFingerprint,
    string? RecoveryRehearsalFingerprintVersion,
    string? RecoveryBaselineFingerprint,
    string? RecoveryRestoredFingerprint,
    string? ShadowCleanupFingerprint,
    long? EdgesAssessed,
    long? ReverseEdges,
    long? RestoreEdges,
    long? CompensateEdges,
    long? IrreversibleEdges,
    long? AffectedArtifacts,
    long? RecoverableArtifacts,
    long? FailedEdges,
    IReadOnlyCollection<RecoverySemanticTypeCoverage> RecoveryBySemanticType,
    long? IrrecoverableArtifacts,
    long? UnknownArtifacts,
    decimal? RecoverablePercentage,
    string? RecoveryEvidenceFingerprint,
    string? RecoveryEvidenceFormatVersion,
    string? DryRunFingerprint,
    string? DryRunFingerprintVersion,
    string? RecoveryArtifactDirectory,
    IReadOnlyCollection<string> Reasons,
    string? FailureCode);

internal sealed record RecoverySummaryOutput(
    string DryRunId,
    string Status,
    string DryRunFingerprint,
    string PolicyFingerprint,
    string PolicyFingerprintVersion,
    string AssessmentFingerprint,
    string PlanFingerprint,
    string RehearsalFingerprint,
    string AssessmentFingerprintVersion,
    string PlanFingerprintVersion,
    string RehearsalFingerprintVersion,
    string VerificationEvidenceFingerprint,
    string RecoveryEvidenceFingerprint,
    string RecoveryEvidenceFormatVersion,
    string DryRunFingerprintVersion,
    long ExecutedEdges,
    long ReverseEdges,
    long RestoreEdges,
    long CompensateEdges,
    long IrreversibleEdges,
    long AffectedArtifacts,
    long RecoverableArtifacts,
    long IrrecoverableArtifacts,
    long UnknownArtifacts,
    decimal? RecoverablePercentage,
    long FailedEdges,
    string RehearsalOutcome,
    IReadOnlyCollection<RecoverySemanticTypeCoverage> BySemanticType,
    IReadOnlyList<string> Reasons);

internal sealed record SourceColumnOutput(string Name, string DataType, bool Nullable, int? Ordinal);

internal sealed record SourceNodeInspectionOutput(
    string NodeKey,
    string System,
    string Endpoint,
    string Connector,
    string SelectorKind,
    string Status,
    string? PhysicalObject,
    IReadOnlyList<string> IdentityFields,
    IReadOnlyList<string> PrimaryKeyFields,
    IReadOnlyList<SourceColumnOutput> Columns,
    long? EstimatedRecords,
    long? Files,
    long? Bytes,
    IEnumerable<ValidationIssueOutput> Issues);

internal sealed record InspectionOutput(
    bool Valid,
    string? Project,
    IEnumerable<SourceNodeInspectionOutput> Sources,
    IEnumerable<ValidationIssueOutput> Issues);
