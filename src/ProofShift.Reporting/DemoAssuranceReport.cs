using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Reporting;

public sealed record DemoAssuranceReport
{
    public const string FormatVersion = "proofshift-demo-assurance-report-v1";

    public string Format { get; }
    public string PackId { get; }
    public string PackVersion { get; }
    public string PackDisplayName { get; }
    public string ClaimBoundary { get; }
    public long CheckpointArtifactCount { get; }
    public string CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public int SeededDefectCount { get; }
    public DemoExternalObservation CorrectedExternalObservation { get; }
    public DemoExternalObservation DefectiveExternalObservation { get; }
    public DemoCorrectedShadowResult CorrectedShadow { get; }

    private DemoAssuranceReport(IDomainPack pack, string claimBoundary, long checkpointArtifactCount,
        string checkpointId, string checkpointManifestHash, int seededDefectCount, ExternalVerificationResult correctedExternal,
        ExternalVerificationResult defectiveExternal, VerificationResult shadowVerification,
        RecoveryRunResult shadowRecovery)
    {
        Format = FormatVersion;
        ArgumentNullException.ThrowIfNull(pack);
        if (string.IsNullOrWhiteSpace(claimBoundary)) throw new ArgumentException("A claim boundary is required.", nameof(claimBoundary));
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointArtifactCount);
        ArgumentOutOfRangeException.ThrowIfNegative(seededDefectCount);
        if (string.IsNullOrWhiteSpace(checkpointId)) throw new ArgumentException("Checkpoint ID is required.", nameof(checkpointId));
        if (string.IsNullOrWhiteSpace(checkpointManifestHash)) throw new ArgumentException("Checkpoint manifest hash is required.", nameof(checkpointManifestHash));
        ArgumentNullException.ThrowIfNull(correctedExternal);
        ArgumentNullException.ThrowIfNull(defectiveExternal);
        ArgumentNullException.ThrowIfNull(shadowVerification);
        ArgumentNullException.ThrowIfNull(shadowRecovery);

        if (!shadowVerification.Run.Runtime.DomainPackVersions.TryGetValue(pack.Id, out var runtimePackVersion) ||
            !StringComparer.Ordinal.Equals(runtimePackVersion, pack.Version))
            throw new InvalidOperationException("Report pack metadata does not match the Verification runtime fingerprint.");
        PackId = pack.Id;
        PackVersion = pack.Version;
        PackDisplayName = pack.Metadata.DisplayName;
        ClaimBoundary = claimBoundary.Trim();
        CheckpointArtifactCount = checkpointArtifactCount;
        CheckpointId = checkpointId;
        CheckpointManifestHash = checkpointManifestHash;
        SeededDefectCount = seededDefectCount;
        CorrectedExternalObservation = DemoExternalObservation.Create(correctedExternal);
        DefectiveExternalObservation = DemoExternalObservation.Create(defectiveExternal);
        CorrectedShadow = DemoCorrectedShadowResult.Create(shadowVerification, shadowRecovery);
    }

    public static DemoAssuranceReport Create(IDomainPack pack, string claimBoundary,
        long checkpointArtifactCount, string checkpointId, string checkpointManifestHash, int seededDefectCount,
        ExternalVerificationResult correctedExternal, ExternalVerificationResult defectiveExternal,
        VerificationResult shadowVerification, RecoveryRunResult shadowRecovery) =>
        new(pack, claimBoundary, checkpointArtifactCount, checkpointId, checkpointManifestHash, seededDefectCount,
            correctedExternal, defectiveExternal, shadowVerification, shadowRecovery);

    public string ToHumanReadable()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"ProofShift Assurance Report: {PackDisplayName}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Pack: {PackId} {PackVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Checkpoint artifacts: {CheckpointArtifactCount}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Seeded defects: {SeededDefectCount}");
        text.AppendLine();
        text.AppendLine("EXTERNAL TARGET OBSERVATIONS (READ ONLY)");
        AppendObservation(text, "Corrected external target", CorrectedExternalObservation);
        AppendObservation(text, "Defective external target", DefectiveExternalObservation);
        text.AppendLine();
        text.AppendLine("CORRECTED SHADOW PROJECTION AND RECOVERY");
        text.AppendLine(CultureInfo.InvariantCulture, $"Verification: {CorrectedShadow.VerificationOutcome}; {CorrectedShadow.VerificationFailureCount} failures");
        text.AppendLine(CultureInfo.InvariantCulture, $"Source accounting: {CorrectedShadow.AccountedSourceCount}/{CorrectedShadow.SourceArtifactCount} accounted; {CorrectedShadow.UnaccountedSourceCount} gaps");
        text.AppendLine(CultureInfo.InvariantCulture, $"Target lineage: {CorrectedShadow.LineagedTargetCount}/{CorrectedShadow.TargetArtifactCount} explained; {CorrectedShadow.UnexplainedTargetCount} gaps");
        text.AppendLine(CultureInfo.InvariantCulture, $"Recovery assessment: {CorrectedShadow.RecoveryOutcome}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Recovery rehearsal: {CorrectedShadow.RecoveryRehearsalOutcome}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Qualification: {CorrectedShadow.Qualification}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Failed Recovery edges: {CorrectedShadow.FailedRecoveryEdges}; false-Reverse declarations: {CorrectedShadow.FalseReversibleTransformations}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Verification run: {CorrectedShadow.VerificationRunId}; Evidence fingerprint: {CorrectedShadow.VerificationEvidenceFingerprint}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Recovery Evidence fingerprint: {CorrectedShadow.RecoveryEvidenceFingerprint}");
        text.AppendLine();
        text.AppendLine("DEFECTIVE TARGET EVIDENCE REFERENCES");
        if (DefectiveExternalObservation.EvidenceReferences.Count == 0)
        {
            text.AppendLine("None.");
        }
        else
        {
            foreach (var reference in DefectiveExternalObservation.EvidenceReferences)
                text.AppendLine(CultureInfo.InvariantCulture, $"{reference.EvidenceId} | {reference.RuleId} | {reference.Code} | {reference.Severity}");
        }
            text.AppendLine(CultureInfo.InvariantCulture, $"Run-level Verification summary Evidence: {DefectiveExternalObservation.SummaryEvidenceId ?? "not present"}");
        text.AppendLine();
        text.AppendLine("CLAIM BOUNDARY");
        text.AppendLine(ClaimBoundary);
        return text.ToString();
    }

    private static void AppendObservation(StringBuilder text, string label, DemoExternalObservation observation)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {observation.VerificationOutcome}; {observation.VerificationFailureCount} failed findings; {observation.TargetArtifactCount} target artifacts");
        text.AppendLine(CultureInfo.InvariantCulture, $"Verification run: {observation.VerificationRunId}; Evidence fingerprint: {observation.EvidenceFingerprint}");
        foreach (var (finding, count) in observation.FindingsByRuleAndCode)
            text.AppendLine(CultureInfo.InvariantCulture, $"  {finding}: {count}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Run-level Verification summary Evidence: {observation.SummaryEvidenceId ?? "not present"}");
    }
}

public sealed record DemoExternalObservation(string VerificationRunId, string TargetFingerprint,
    string VerificationOutcome, long TargetArtifactCount, int VerificationFailureCount,
    string EvidenceFingerprint, string? SummaryEvidenceId, IReadOnlyDictionary<string, int> FindingsByRuleAndCode,
    IReadOnlyList<AssuranceEvidenceReference> EvidenceReferences)
{
    internal static DemoExternalObservation Create(ExternalVerificationResult result)
    {
        var failures = result.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        var counts = new ReadOnlyDictionary<string, int>(failures
            .GroupBy(finding => $"{finding.RuleId.Value}:{finding.Code}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        var summaryEvidenceId = result.EvidenceGraph.Records.FirstOrDefault(DemoReportEvidence.IsSummaryRecord)?
            .Id.Value.ToString("D");
        var references = result.EvidenceGraph.Records.Where(record => record.Result == EvidenceResult.Fail &&
            !DemoReportEvidence.IsSummaryRecord(record))
            .OrderBy(record => record.Id.Value)
            .Select(record => new AssuranceEvidenceReference(record.Id.Value.ToString("D"), record.RuleId.Value,
                record.Code ?? "UnclassifiedFailure", record.Severity.ToString())).ToArray();
        return new DemoExternalObservation(result.Run.Id.Value.ToString("D"), result.Run.TargetFingerprint,
            result.Run.Outcome.ToString().ToUpperInvariant(), result.Run.TargetArtifactCount, failures.Length,
            result.Run.EvidenceFingerprint, summaryEvidenceId, counts, references);
    }
}

public sealed record DemoCorrectedShadowResult(string VerificationRunId, string DryRunId, string VerificationOutcome,
    long SourceArtifactCount, long TargetArtifactCount, long AccountedSourceCount, long LineagedTargetCount,
    int VerificationFailureCount, int UnaccountedSourceCount, int UnexplainedTargetCount,
    string VerificationEvidenceFingerprint, string? SummaryEvidenceId, IReadOnlyList<AssuranceEvidenceReference> EvidenceReferences,
    string RecoveryOutcome, string RecoveryRehearsalOutcome, string Qualification,
    long FailedRecoveryEdges, long FalseReversibleTransformations, string RecoveryEvidenceFingerprint)
{
    internal static DemoCorrectedShadowResult Create(VerificationResult verification, RecoveryRunResult recovery)
    {
        var summaryEvidenceId = verification.EvidenceGraph.Records.FirstOrDefault(DemoReportEvidence.IsSummaryRecord)?
            .Id.Value.ToString("D");
        var failures = verification.EvidenceGraph.Records.Where(record => record.Result == EvidenceResult.Fail &&
            !DemoReportEvidence.IsSummaryRecord(record))
            .OrderBy(record => record.Id.Value)
            .Select(record => new AssuranceEvidenceReference(record.Id.Value.ToString("D"), record.RuleId.Value,
                record.Code ?? "UnclassifiedFailure", record.Severity.ToString())).ToArray();
        var verificationFailures = verification.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        return new DemoCorrectedShadowResult(verification.Run.Id.Value.ToString("D"),
            recovery.Qualification.Id.Value.ToString("D"), verification.Run.Outcome.ToString().ToUpperInvariant(), verification.Run.ProjectionSourceCount,
            verification.Run.ProjectionTargetCount, verification.Ledger.DispositionCount,
            verification.Ledger.LineageCount, verificationFailures.Length,
            verificationFailures.Count(finding => finding.Code == "UnaccountedArtifact"),
            verificationFailures.Count(finding => finding.Code is "MissingLineage" or "UnexpectedTarget"),
            verification.Run.EvidenceFingerprint, summaryEvidenceId, failures,
            recovery.Assessment.Outcome.ToString().ToUpperInvariant(), recovery.Rehearsal.Outcome.ToString().ToUpperInvariant(),
            recovery.Qualification.Status.ToString().ToUpperInvariant(), recovery.Assessment.Coverage.FailedEdges,
            recovery.Assessment.Edges.LongCount(edge => edge.Issues.Any(issue => issue.Code == RecoveryIssueCodes.InvalidReverse)),
            recovery.EvidenceGraph.Fingerprint);
    }

}

internal static class DemoReportEvidence
{
    public static bool IsSummaryRecord(EvidenceRecord record) =>
        record.Type == EvidenceType.Decision && record.Code is "VerificationSummary" or "ExternalVerificationSummary";
}
