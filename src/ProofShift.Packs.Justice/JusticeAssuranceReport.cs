using System.Collections.ObjectModel;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Packs.Justice;

public sealed record JusticeAssuranceReport
{
    public const string FormatVersion = "proofshift-justice-assurance-report-v1";
    public const string ScopeBoundary = "Configured migration invariants only; this report does not validate law, jurisdiction, court rules, sentencing, charge validity, filing sufficiency, or records retention.";

    public string Format { get; }
    public string Project { get; }
    public string Scope { get; }
    public string ClaimBoundary { get; }
    public string VerificationOutcome { get; }
    public string RecoveryAssessmentOutcome { get; }
    public string RecoveryRehearsalOutcome { get; }
    public string Qualification { get; }
    public int VerificationFailureCount { get; }
    public int UnaccountedSourceCount { get; }
    public int UnexplainedTargetCount { get; }
    public IReadOnlyDictionary<string, int> FindingsByRuleAndCode { get; }
    public IReadOnlyList<string> Concepts { get; }
    public IReadOnlyList<string> RuleTypes { get; }

    private JusticeAssuranceReport(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        Format = FormatVersion;
        Project = string.IsNullOrWhiteSpace(project) ? throw new ArgumentException("Project name is required.", nameof(project)) : project.Trim();
        Scope = "Representative case-management migration assurance";
        ClaimBoundary = ScopeBoundary;
        VerificationOutcome = verification.Run.Outcome.ToString();
        RecoveryAssessmentOutcome = recovery.Assessment.Outcome.ToString();
        RecoveryRehearsalOutcome = recovery.Rehearsal.Outcome.ToString();
        Qualification = recovery.Qualification.Status.ToString().ToUpperInvariant();
        var failures = verification.Findings.Where(finding => finding.Result == ProofShift.Domain.EvidenceResult.Fail).ToArray();
        VerificationFailureCount = failures.Length;
        UnaccountedSourceCount = failures.Count(finding => finding.Code == "UnaccountedArtifact");
        UnexplainedTargetCount = failures.Count(finding => finding.Code is "MissingLineage" or "UnexpectedTarget");
        FindingsByRuleAndCode = new ReadOnlyDictionary<string, int>(failures
            .GroupBy(finding => $"{finding.RuleId.Value}:{finding.Code}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
        var pack = new JusticePack();
        Concepts = pack.Metadata.Concepts.Select(concept => concept.SemanticType).Order(StringComparer.Ordinal).ToArray();
        RuleTypes = pack.RuleFactories.Select(factory => factory.Type).Order(StringComparer.Ordinal).ToArray();
    }

    public static JusticeAssuranceReport Create(string project, VerificationResult verification, RecoveryRunResult recovery)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(recovery);
        return new JusticeAssuranceReport(project, verification, recovery);
    }
}
