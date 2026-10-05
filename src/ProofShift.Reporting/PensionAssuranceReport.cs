using System.Globalization;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Recovery;

namespace ProofShift.Reporting;

public sealed record PensionAssuranceReport
{
    public const string FormatVersion = "proofshift-pension-assurance-report-v1";
    public string Format { get; }
    public string Project { get; }
    public string DryRunId { get; }
    public string Qualification { get; }
    public string VerificationOutcome { get; }
    public string RehearsalOutcome { get; }
    public string VerificationRunId { get; }
    public string VerificationEvidenceFingerprint { get; }
    public string RecoveryAssessmentFingerprint { get; }
    public string RecoveryEvidenceFingerprint { get; }
    public string DryRunFingerprint { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public string SourceFingerprint { get; }
    public string ProjectionFingerprint { get; }
    public string RuleSetFingerprint { get; }
    public string RecoveryPolicyFingerprint { get; }
    public long VerificationFailureCount { get; }
    public long WarningCount { get; }
    public long UnaccountedSources { get; }
    public long UnexplainedTargets { get; }
    public long FailedRecoveryEdges { get; }
    public long AffectedRecoveryArtifacts { get; }
    public long RecoverableArtifacts { get; }
    public long UnknownRecoveryArtifacts { get; }
    public long IrrecoverableArtifacts { get; }
    public long FalseReversibleTransformations { get; }
    public decimal? RecoveryCoveragePercentage { get; }
    public IReadOnlyCollection<RecoverySemanticTypeCoverage> RecoveryBySemanticType { get; }
    public long BusinessDiscrepancyCount { get; }
    public IReadOnlyDictionary<string, long> DefectCounts { get; }
    public IReadOnlyDictionary<string, long> EvidenceFailureCounts { get; }
    public IReadOnlyList<PensionAssuranceSection> Sections { get; }
    public IReadOnlyList<PensionAssuranceIssue> Exceptions { get; }
    public IReadOnlyList<string> RecoveryReasons { get; }

    internal PensionAssuranceReport(string project, DryRunId dryRunId, RecoveryArtifactSummary recovery,
        string verificationOutcome, long verificationFailureCount, long warningCount, long unaccountedSources,
        long unexplainedTargets, IReadOnlyDictionary<string, long> defectCounts,
        IReadOnlyDictionary<string, long> evidenceFailureCounts, IReadOnlyList<PensionAssuranceSection> sections,
        IReadOnlyList<PensionAssuranceIssue> exceptions)
    {
        Format = FormatVersion;
        Project = project;
        DryRunId = dryRunId.Value.ToString("D", CultureInfo.InvariantCulture);
        Qualification = recovery.Status switch
        {
            DryRunQualificationStatus.Qualified => "QUALIFIED",
            DryRunQualificationStatus.NotQualified => "NOT QUALIFIED",
            DryRunQualificationStatus.Error => "ERROR",
            DryRunQualificationStatus.Cancelled => "CANCELLED",
            _ => throw new ArgumentOutOfRangeException(nameof(recovery))
        };
        VerificationOutcome = verificationOutcome;
        RehearsalOutcome = recovery.RehearsalOutcome;
        VerificationRunId = recovery.VerificationRunId.Value.ToString("D", CultureInfo.InvariantCulture);
        VerificationEvidenceFingerprint = recovery.VerificationEvidenceFingerprint;
        RecoveryAssessmentFingerprint = recovery.AssessmentFingerprint;
        RecoveryEvidenceFingerprint = recovery.RecoveryEvidenceFingerprint;
        DryRunFingerprint = recovery.DryRunFingerprint;
        ConfigurationHash = recovery.ConfigurationHash;
        GraphHash = recovery.GraphHash;
        SourceFingerprint = recovery.SourceFingerprint;
        ProjectionFingerprint = recovery.ProjectionFingerprint;
        RuleSetFingerprint = recovery.RuleSetFingerprint;
        RecoveryPolicyFingerprint = recovery.PolicyFingerprint;
        VerificationFailureCount = verificationFailureCount;
        WarningCount = warningCount;
        UnaccountedSources = unaccountedSources;
        UnexplainedTargets = unexplainedTargets;
        FailedRecoveryEdges = recovery.FailedEdges;
        AffectedRecoveryArtifacts = recovery.AffectedArtifacts;
        RecoverableArtifacts = recovery.RecoverableArtifacts;
        UnknownRecoveryArtifacts = recovery.UnknownArtifacts;
        IrrecoverableArtifacts = recovery.IrrecoverableArtifacts;
        FalseReversibleTransformations = recovery.FalseReversibleEdges;
        RecoveryCoveragePercentage = recovery.RecoverablePercentage;
        RecoveryBySemanticType = recovery.BySemanticType;
        DefectCounts = defectCounts;
        EvidenceFailureCounts = evidenceFailureCounts;
        BusinessDiscrepancyCount = defectCounts.Values.Sum();
        Sections = sections;
        Exceptions = exceptions;
        RecoveryReasons = recovery.Reasons;
    }
}

public sealed record PensionAssuranceSection(string Name, long PassedFindings, long FailedFindings, long WarningFindings);

public sealed record PensionAssuranceIssue(string EvidenceId, string Code, string RuleId,
    string RuleVersion, string Severity, string Explanation, object? Expected, object? Actual);

public sealed record PensionAssuranceRunComparison
{
    public string BeforeDryRunId { get; }
    public string AfterDryRunId { get; }
    public string BeforeQualification { get; }
    public string AfterQualification { get; }
    public bool EvidenceFingerprintChanged { get; }
    public bool RecoveryEvidenceFingerprintChanged { get; }
    public bool DryRunFingerprintChanged { get; }
    public bool ConfigurationHashChanged { get; }
    public bool GraphHashChanged { get; }
    public bool RuleSetFingerprintChanged { get; }
    public bool RecoveryPolicyFingerprintChanged { get; }
    public bool QualificationChanged { get; }
    public bool SourceFingerprintChanged { get; }
    public bool ProjectionFingerprintChanged { get; }
    public IReadOnlyDictionary<string, long> DefectsResolved { get; }
    public IReadOnlyDictionary<string, long> DefectsIntroduced { get; }
    public IReadOnlyList<string> DifferenceAttribution { get; }

    internal PensionAssuranceRunComparison(PensionAssuranceReport before, PensionAssuranceReport after,
        IReadOnlyDictionary<string, long> resolved, IReadOnlyDictionary<string, long> introduced,
        IReadOnlyList<string> attribution)
    {
        BeforeDryRunId = before.DryRunId;
        AfterDryRunId = after.DryRunId;
        BeforeQualification = before.Qualification;
        AfterQualification = after.Qualification;
        EvidenceFingerprintChanged = before.VerificationEvidenceFingerprint != after.VerificationEvidenceFingerprint;
        RecoveryEvidenceFingerprintChanged = before.RecoveryEvidenceFingerprint != after.RecoveryEvidenceFingerprint;
        DryRunFingerprintChanged = before.DryRunFingerprint != after.DryRunFingerprint;
        ConfigurationHashChanged = before.ConfigurationHash != after.ConfigurationHash;
        GraphHashChanged = before.GraphHash != after.GraphHash;
        RuleSetFingerprintChanged = before.RuleSetFingerprint != after.RuleSetFingerprint;
        RecoveryPolicyFingerprintChanged = before.RecoveryPolicyFingerprint != after.RecoveryPolicyFingerprint;
        QualificationChanged = before.Qualification != after.Qualification;
        SourceFingerprintChanged = before.SourceFingerprint != after.SourceFingerprint;
        ProjectionFingerprintChanged = before.ProjectionFingerprint != after.ProjectionFingerprint;
        DefectsResolved = resolved;
        DefectsIntroduced = introduced;
        DifferenceAttribution = attribution;
    }
}

public static class PensionAssuranceReportBuilder
{
    private static readonly (string Name, string[] Codes)[] BusinessDefectCategories =
    [
        ("missingMembers", ["MissingMember"]),
        ("duplicateMembers", ["DuplicateMember"]),
        ("wrongMemberStatuses", ["WrongMemberStatus"]),
        ("missingEmploymentPeriods", ["MissingEmploymentPeriod"]),
        ("incorrectEmploymentDates", ["IncorrectEmploymentDate", "EmploymentStateTransitionMismatch"]),
        ("incorrectServiceCreditTotals", ["IncorrectServiceCreditTotal"]),
        ("missingContributions", ["MissingContribution"]),
        ("duplicateContributions", ["DuplicateContribution"]),
        ("incorrectContributionAmounts", ["IncorrectContributionAmount", "ContributionSemanticMismatch"]),
        ("benefitPaymentDiscrepancies", ["MissingBenefitPayment", "DuplicateBenefitPayment", "BenefitPaymentAmountMismatch", "BenefitPaymentSemanticMismatch"]),
        ("brokenBeneficiaryRelationships", ["BrokenBeneficiaryRelationship"]),
        ("wrongMemberBeneficiaries", ["WrongMemberBeneficiary"]),
        ("incorrectRetirementElectionMappings", ["RetirementElectionMappingMismatch"]),
        ("incorrectCodeTransformations", ["CodeTransformationMismatch"]),
        ("missingDocuments", ["MissingDocument"]),
        ("wrongMemberDocuments", ["WrongMemberDocument"]),
        ("missingHistoricalExports", ["MissingHistoricalExport"]),
        ("falseReversibleTransformations", [])
    ];

    private static readonly (string Name, string[] Codes)[] SectionCodes =
    [
        ("Member Conversion", ["MissingMember", "DuplicateMember", "WrongMemberStatus"]),
        ("Historical Employment", ["MissingEmploymentPeriod", "IncorrectEmploymentDate", "EmploymentStateTransitionMismatch", "EmploymentTimelineGap", "OverlappingEmploymentPeriods", "InvalidEmploymentPeriod"]),
        ("Financial Reconciliation", ["MissingContribution", "DuplicateContribution", "IncorrectContributionAmount", "ContributionSemanticMismatch", "ContributionPeriodTotalMismatch"]),
        ("Beneficiary Relationships", ["BrokenBeneficiaryRelationship", "WrongMemberBeneficiary", "BeneficiarySemanticsMismatch", "DuplicateBeneficiaryRelationship"]),
        ("Retirement Elections", ["RetirementElectionMappingMismatch"]),
        ("Benefit Payments", ["MissingBenefitPayment", "DuplicateBenefitPayment", "BenefitPaymentAmountMismatch", "BenefitPaymentSemanticMismatch", "BenefitPaymentTotalMismatch"]),
        ("Document Migration", ["MissingDocument", "WrongMemberDocument", "DocumentContentMismatch", "MissingHistoricalExport"]),
        ("Source Accounting", ["UnaccountedSource", "DuplicateDisposition", "SourceDisposition", "MemberAccounted"]),
        ("Target Lineage", ["MissingLineage", "TargetLineage", "UnexpectedTarget", "TargetPresence"]),
        ("Transformation", ["CodeTransformationMismatch"])
    ];

    public static PensionAssuranceReport Build(string projectName, DryRunId dryRunId, JsonElement storedEvidence,
        RecoveryArtifactSummary recovery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(recovery);
        if (storedEvidence.ValueKind != JsonValueKind.Object ||
            storedEvidence.GetProperty("format").GetString() != EvidenceFormat.StoreFormatVersion)
            throw new InvalidDataException("Stored verification evidence has an unsupported format.");
        if (!Guid.TryParseExact(storedEvidence.GetProperty("runId").GetString(), "D", out var evidenceRunId) ||
            evidenceRunId != recovery.VerificationRunId.Value)
            throw new InvalidDataException("Verification evidence run does not match the recovery assessment.");
        if (!string.Equals(storedEvidence.GetProperty("fingerprint").GetString(),
            recovery.VerificationEvidenceFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Verification Evidence fingerprint does not match the recovery assessment.");

        var records = storedEvidence.GetProperty("records").EnumerateArray().ToArray();
        var failures = records.Where(record => Is(record, "result", "Fail")).ToArray();
        var warnings = records.LongCount(record => Is(record, "result", "Warning"));
        var evidenceCounts = failures.GroupBy(record => Text(record, "code"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
        var businessCounts = CountBusinessDiscrepancies(evidenceCounts, recovery.FalseReversibleEdges);
        var sections = SectionCodes.Select(section => new PensionAssuranceSection(section.Name,
            records.LongCount(record => section.Codes.Contains(Text(record, "code"), StringComparer.Ordinal) && Is(record, "result", "Pass")),
            records.LongCount(record => section.Codes.Contains(Text(record, "code"), StringComparer.Ordinal) && Is(record, "result", "Fail")),
            records.LongCount(record => section.Codes.Contains(Text(record, "code"), StringComparer.Ordinal) && Is(record, "result", "Warning")))).ToArray();
        var exceptions = failures.Select(ToIssue).ToArray();
        var unaccounted = failures.LongCount(record => Text(record, "code") is "UnaccountedSource" or "DuplicateDisposition");
        var unexplained = failures.LongCount(record => Text(record, "code") == "UnexpectedTarget");
        var outcome = failures.Length == 0 ? warnings == 0 ? "PASSED" : "PASSED WITH WARNINGS" : "FAILED";
        return new PensionAssuranceReport(projectName, dryRunId, recovery, outcome, failures.LongLength, warnings,
            unaccounted, unexplained, businessCounts, evidenceCounts, sections, exceptions);
    }

    public static async Task<PensionAssuranceReport> BuildAsync(string projectName, DryRunId dryRunId,
        EvidenceStoreManifest manifest, IAsyncEnumerable<JsonElement> records, RecoveryArtifactSummary recovery,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(recovery);
        if (manifest.Format != EvidenceFormat.StoreFormatVersion || !manifest.Complete ||
            !Guid.TryParseExact(manifest.RunId, "D", out var runId) || runId != recovery.VerificationRunId.Value ||
            manifest.Fingerprint != recovery.VerificationEvidenceFingerprint)
            throw new InvalidDataException("Stored verification evidence does not match the recovery assessment.");

        var failureDetails = new List<PensionAssuranceIssue>();
        var failureCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var sectionCounts = SectionCodes.Select(_ => new long[3]).ToArray();
        long failures = 0;
        long warnings = 0;
        long unaccounted = 0;
        long unexplained = 0;
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = Text(record, "code");
            var result = Text(record, "result");
            if (string.Equals(result, "Fail", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
                failureCounts[code] = failureCounts.GetValueOrDefault(code) + 1;
                failureDetails.Add(ToIssue(record));
                if (code is "UnaccountedSource" or "DuplicateDisposition") unaccounted++;
                if (code == "UnexpectedTarget") unexplained++;
            }
            else if (string.Equals(result, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                warnings++;
            }

            for (var index = 0; index < SectionCodes.Length; index++)
            {
                if (!SectionCodes[index].Codes.Contains(code, StringComparer.Ordinal)) continue;
                if (string.Equals(result, "Pass", StringComparison.OrdinalIgnoreCase)) sectionCounts[index][0]++;
                else if (string.Equals(result, "Fail", StringComparison.OrdinalIgnoreCase)) sectionCounts[index][1]++;
                else if (string.Equals(result, "Warning", StringComparison.OrdinalIgnoreCase)) sectionCounts[index][2]++;
            }
        }

        var sections = SectionCodes.Select((section, index) => new PensionAssuranceSection(section.Name,
            sectionCounts[index][0], sectionCounts[index][1], sectionCounts[index][2])).ToArray();
        var outcome = failures == 0 ? warnings == 0 ? "PASSED" : "PASSED WITH WARNINGS" : "FAILED";
        var businessCounts = CountBusinessDiscrepancies(failureCounts, recovery.FalseReversibleEdges);
        return new PensionAssuranceReport(projectName, dryRunId, recovery, outcome, failures, warnings,
            unaccounted, unexplained, businessCounts, failureCounts, sections, failureDetails);
    }

    public static IReadOnlyDictionary<string, long> CountBusinessDiscrepancies(
        IEnumerable<JsonElement> failedEvidenceRecords, long falseReversibleEdges)
    {
        ArgumentNullException.ThrowIfNull(failedEvidenceRecords);
        ArgumentOutOfRangeException.ThrowIfNegative(falseReversibleEdges);
        var evidenceCounts = failedEvidenceRecords.GroupBy(record => Text(record, "code"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
        return CountBusinessDiscrepancies(evidenceCounts, falseReversibleEdges);
    }

    private static SortedDictionary<string, long> CountBusinessDiscrepancies(
        IReadOnlyDictionary<string, long> evidenceCounts, long falseReversibleEdges)
    {
        var businessCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var category in BusinessDefectCategories)
            businessCounts[category.Name] = category.Codes.Sum(code => evidenceCounts.GetValueOrDefault(code));
        businessCounts["falseReversibleTransformations"] = falseReversibleEdges;
        return businessCounts;
    }

    public static PensionAssuranceRunComparison Compare(PensionAssuranceReport before, PensionAssuranceReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var keys = before.DefectCounts.Keys.Union(after.DefectCounts.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var resolved = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var introduced = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var previous = before.DefectCounts.GetValueOrDefault(key);
            var current = after.DefectCounts.GetValueOrDefault(key);
            if (previous > current) resolved[key] = previous - current;
            if (current > previous) introduced[key] = current - previous;
        }
        var attribution = new List<string>();
        if (before.SourceFingerprint != after.SourceFingerprint) attribution.Add("source data/checkpoint");
        if (before.GraphHash != after.GraphHash) attribution.Add("migration graph");
        if (before.RuleSetFingerprint != after.RuleSetFingerprint) attribution.Add("verification rule set");
        if (before.RecoveryPolicyFingerprint != after.RecoveryPolicyFingerprint) attribution.Add("recovery configuration");
        if (before.ConfigurationHash != after.ConfigurationHash && attribution.Count == 0) attribution.Add("other configuration");
        if (before.ProjectionFingerprint != after.ProjectionFingerprint && !attribution.Contains("source data/checkpoint", StringComparer.Ordinal) &&
            !attribution.Contains("migration graph", StringComparer.Ordinal)) attribution.Add("transformation or target projection");
        return new PensionAssuranceRunComparison(before, after, resolved, introduced, attribution);
    }

    private static bool Is(JsonElement element, string property, string expected) =>
        string.Equals(Text(element, property), expected, StringComparison.OrdinalIgnoreCase);

    private static PensionAssuranceIssue ToIssue(JsonElement record) => new(Text(record, "id"), Text(record, "code"),
        Text(record, "ruleId"), Text(record, "ruleVersion"), Text(record, "severity"), Text(record, "explanation"),
        record.TryGetProperty("expected", out var expected) ? SafeValue(expected) : null,
        record.TryGetProperty("actual", out var actual) ? SafeValue(actual) : null);

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static object? SafeValue(JsonElement value)
    {
        var kind = Text(value, "kind");
        if (kind == "object" && value.TryGetProperty("properties", out var properties))
        {
            var safe = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateArray())
            {
                var name = Text(property, "name");
                if (name is "groupFingerprint" or "count" or "total" or "tolerance" or "difference" or "sourceCount" or "targetCount" or "sourceTotal" or "targetTotal")
                    safe[name] = SafeValue(property.GetProperty("value"));
            }
            return safe;
        }
        if (value.TryGetProperty("value", out var raw) && raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString() ?? string.Empty;
            return text.StartsWith("sha256:", StringComparison.Ordinal) || text is "present" or "missing" or "materialized" or "excluded" or "journal-backed" or "unexplained"
                ? text
                : "[redacted]";
        }
        if (value.TryGetProperty("value", out raw) && raw.ValueKind == JsonValueKind.Number) return raw.GetRawText();
        return null;
    }
}
