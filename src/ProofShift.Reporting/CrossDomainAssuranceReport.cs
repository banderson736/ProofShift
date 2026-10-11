using System.Collections.ObjectModel;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Reporting;

public sealed record CrossDomainAssuranceReport
{
    public const string FormatVersion = "proofshift-cross-domain-assurance-report-v1";

    public string Format { get; }
    public string Project { get; }
    public string PackId { get; }
    public string PackVersion { get; }
    public string PackDisplayName { get; }
    public string ClaimBoundary { get; }
    public string VerificationOutcome { get; }
    public string RecoveryOutcome { get; }
    public string RecoveryRehearsalOutcome { get; }
    public string Qualification { get; }
    public IReadOnlyDictionary<string, string> RuntimePackVersions { get; }
    public IReadOnlyDictionary<string, string> RuleVersions { get; }
    public int VerificationFailureCount { get; }
    public int UnaccountedSourceCount { get; }
    public int UnexplainedTargetCount { get; }
    public IReadOnlyDictionary<string, int> FindingsByRuleAndCode { get; }
    public IReadOnlyList<AssuranceConceptSummary> Concepts { get; }
    public IReadOnlyList<AssuranceRuleSummary> Rules { get; }

    private CrossDomainAssuranceReport(string project, IDomainPack pack, string claimBoundary,
        VerificationResult verification, RecoveryRunResult recovery)
    {
        Format = FormatVersion;
        Project = string.IsNullOrWhiteSpace(project) ? throw new ArgumentException("Project name is required.", nameof(project)) : project.Trim();
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(recovery);
        PackId = pack.Id;
        PackVersion = pack.Version;
        PackDisplayName = pack.Metadata.DisplayName;
        var runtimePackVersions = new SortedDictionary<string, string>(
            verification.Run.Runtime.DomainPackVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        if (!runtimePackVersions.TryGetValue(pack.Id, out var runtimePackVersion) || runtimePackVersion != pack.Version)
            throw new InvalidOperationException("Report pack metadata does not match the Verification runtime fingerprint.");
        RuntimePackVersions = new ReadOnlyDictionary<string, string>(runtimePackVersions);
        RuleVersions = new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(
            verification.Run.Runtime.RuleVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
        ClaimBoundary = string.IsNullOrWhiteSpace(claimBoundary)
            ? throw new ArgumentException("A report claim boundary is required.", nameof(claimBoundary))
            : claimBoundary.Trim();
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
        Concepts = pack.Metadata.Concepts.OrderBy(concept => concept.SemanticType, StringComparer.Ordinal)
            .Select(concept => new AssuranceConceptSummary(concept.SemanticType, concept.DisplayName, concept.Description)).ToArray();
        Rules = pack.Metadata.RuleProviders.SelectMany(provider => provider.Rules)
            .OrderBy(rule => rule.Type, StringComparer.Ordinal)
            .Select(rule => new AssuranceRuleSummary(rule.Type, rule.Version, rule.Description)).ToArray();
    }

    public static CrossDomainAssuranceReport Create(string project, IDomainPack pack, string claimBoundary,
        VerificationResult verification, RecoveryRunResult recovery) =>
        new(project, pack, claimBoundary, verification, recovery);
}

public sealed record AssuranceConceptSummary(string SemanticType, string DisplayName, string Description);

public sealed record AssuranceRuleSummary(string Type, string Version, string Description);
