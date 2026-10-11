using System.Collections.ObjectModel;
using ProofShift.Domain;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Packs.Hcm;

public sealed record HcmAssuranceReport
{
    public const string FormatVersion = "proofshift-hcm-assurance-report-v1";
    public const string ClaimBoundary = "Configured migration invariants only; this report does not validate payroll tax, tax withholding, benefits or labor-law compliance, accounting certification, or complete payroll-engine correctness.";

    public string Format { get; }
    public string Project { get; }
    public string Scope { get; }
    public string ClaimBoundaryText { get; }
    public string VerificationOutcome { get; }
    public string RecoveryOutcome { get; }
    public string RecoveryRehearsalOutcome { get; }
    public string Qualification { get; }
    public int VerificationFailureCount { get; }
    public int UnaccountedSourceCount { get; }
    public int UnexplainedTargetCount { get; }
    public IReadOnlyDictionary<string, int> FindingsByRuleAndCode { get; }
    public IReadOnlyList<string> Concepts { get; }
    public IReadOnlyList<string> RuleTypes { get; }

    private HcmAssuranceReport(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        Format = FormatVersion;
        Project = string.IsNullOrWhiteSpace(project) ? throw new ArgumentException("Project name is required.", nameof(project)) : project.Trim();
        Scope = "Representative ERP/HCM migration assurance";
        ClaimBoundaryText = ClaimBoundary;
        VerificationOutcome = verification.Run.Outcome.ToString();
        RecoveryOutcome = recovery.Assessment.Outcome.ToString();
        RecoveryRehearsalOutcome = recovery.Rehearsal.Outcome.ToString();
        Qualification = recovery.Qualification.Status.ToString().ToUpperInvariant();
        var failures = verification.Findings.Where(finding => finding.Result == EvidenceResult.Fail).ToArray();
        VerificationFailureCount = failures.Length;
        UnaccountedSourceCount = failures.Count(finding => finding.Code == "UnaccountedArtifact");
        UnexplainedTargetCount = failures.Count(finding => finding.Code is "MissingLineage" or "UnexpectedTarget");
        FindingsByRuleAndCode = new ReadOnlyDictionary<string, int>(failures
            .GroupBy(finding => $"{finding.RuleId.Value}:{finding.Code}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        var pack = new HcmPack();
        Concepts = pack.Metadata.Concepts.Select(concept => concept.SemanticType).Order(StringComparer.Ordinal).ToArray();
        RuleTypes = pack.RuleFactories.Select(factory => factory.Type).Order(StringComparer.Ordinal).ToArray();
    }

    public static HcmAssuranceReport Create(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(recovery);
        return new HcmAssuranceReport(project, verification, recovery);
    }
}
