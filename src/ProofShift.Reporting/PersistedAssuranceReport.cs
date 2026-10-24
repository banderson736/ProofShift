using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Packs.Abstractions;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Reporting;

public sealed record PersistedAssuranceReport
{
    public const string FormatVersion = "proofshift-persisted-assurance-report-v1";

    public string Format { get; }
    public string ProjectId { get; }
    public string ProjectName { get; }
    public string DryRunId { get; }
    public string PackId { get; }
    public string PackVersion { get; }
    public string PackDisplayName { get; }
    public string ClaimBoundary { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public string CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public string CheckpointSemanticFingerprint { get; }
    public string SourceFingerprint { get; }
    public string RecoveryProjectionRunId { get; }
    public string ProjectionManifestHash { get; }
    public string? ProjectionRunId { get; }
    public string? ProjectionFingerprint { get; }
    public string? ObservationId { get; }
    public string? ObservationRunId { get; }
    public string? TargetFingerprint { get; }
    public string RuleSetFingerprint { get; }
    public string VerificationRunId { get; }
    public string VerificationOutcome { get; }
    public string VerificationEvidenceFingerprint { get; }
    public string VerificationLedgerFingerprint { get; }
    public string RecoveryAssessmentOutcome { get; }
    public string RecoveryRehearsalOutcome { get; }
    public string Qualification { get; }
    public string RecoveryPolicyFingerprint { get; }
    public string RecoveryAssessmentFingerprint { get; }
    public string RecoveryPlanFingerprint { get; }
    public string RecoveryRehearsalFingerprint { get; }
    public string RecoveryEvidenceFingerprint { get; }
    public string DryRunFingerprint { get; }
    public long VerificationFailureCount { get; }
    public long WarningCount { get; }
    public long BusinessDiscrepancyCount { get; }
    public IReadOnlyDictionary<string, string> RuleVersions { get; }
    public long SourceArtifactCount { get; }
    public long AccountedSourceCount { get; }
    public long SourceAccountingGapCount { get; }
    public string SourceAccountingStatus { get; }
    public long TargetArtifactCount { get; }
    public long LineagedTargetCount { get; }
    public long TargetLineageGapCount { get; }
    public string TargetLineageStatus { get; }
    public long FailedRecoveryEdges { get; }
    public long FalseReversibleEdges { get; }
    public long AffectedRecoveryArtifacts { get; }
    public long RecoverableArtifacts { get; }
    public long IrrecoverableArtifacts { get; }
    public long UnknownRecoveryArtifacts { get; }
    public decimal? RecoveryCoveragePercentage { get; }
    public IReadOnlyList<string> RecoveryReasons { get; }
    public IReadOnlyDictionary<string, long> FindingsByRuleAndCode { get; }
    public IReadOnlyList<AssuranceEvidenceReference> EvidenceReferences { get; }
    public IReadOnlyList<AssuranceConceptSummary> Concepts { get; }
    public IReadOnlyList<AssuranceRuleSummary> Rules { get; }

    internal PersistedAssuranceReport(string projectId, string projectName, DryRunId dryRunId,
        IDomainPack pack, EvidenceStoreManifest manifest, RecoveryArtifactSummary recovery,
        SourceCheckpoint checkpoint, VerificationLedgerSummary ledger, IReadOnlyDictionary<string, string> ruleVersions,
        string verificationOutcome, long verificationFailures, long warnings, long businessDiscrepancies,
        IReadOnlyDictionary<string, long> findings,
        IReadOnlyList<AssuranceEvidenceReference> references)
    {
        Format = FormatVersion;
        ProjectId = Required(projectId, nameof(projectId));
        ProjectName = Required(projectName, nameof(projectName));
        DryRunId = dryRunId.Value.ToString("D", CultureInfo.InvariantCulture);
        PackId = pack.Id;
        PackVersion = pack.Version;
        PackDisplayName = pack.Metadata.DisplayName;
        ClaimBoundary = pack.Metadata.ClaimBoundary;
        ConfigurationHash = recovery.ConfigurationHash;
        GraphHash = recovery.GraphHash;
        CheckpointId = recovery.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture);
        CheckpointManifestHash = recovery.CheckpointManifestHash;
        CheckpointSemanticFingerprint = CheckpointSemanticComparison.Fingerprint(checkpoint);
        SourceFingerprint = recovery.SourceFingerprint;
        RecoveryProjectionRunId = recovery.ProjectionRunId.Value.ToString("D", CultureInfo.InvariantCulture);
        ProjectionManifestHash = recovery.ProjectionManifestHash;
        ProjectionRunId = manifest.VerificationContext?.ProjectionRunId;
        ProjectionFingerprint = manifest.VerificationContext?.ProjectionFingerprint;
        ObservationId = manifest.VerificationContext?.ObservationId;
        ObservationRunId = manifest.VerificationContext?.ObservationRunId;
        TargetFingerprint = manifest.VerificationContext?.TargetFingerprint;
        RuleSetFingerprint = recovery.RuleSetFingerprint;
        VerificationRunId = manifest.RunId;
        VerificationOutcome = verificationOutcome;
        VerificationEvidenceFingerprint = manifest.Fingerprint;
        VerificationLedgerFingerprint = recovery.VerificationLedger.Fingerprint;
        RecoveryAssessmentOutcome = recovery.AssessmentOutcome.ToString();
        RecoveryRehearsalOutcome = recovery.RehearsalOutcome;
        Qualification = recovery.Status switch
        {
            DryRunQualificationStatus.Qualified => "QUALIFIED",
            DryRunQualificationStatus.NotQualified => "NOT QUALIFIED",
            DryRunQualificationStatus.Error => "ERROR",
            DryRunQualificationStatus.Cancelled => "CANCELLED",
            _ => throw new ArgumentOutOfRangeException(nameof(recovery))
        };
        RecoveryPolicyFingerprint = recovery.PolicyFingerprint;
        RecoveryAssessmentFingerprint = recovery.AssessmentFingerprint;
        RecoveryPlanFingerprint = recovery.PlanFingerprint;
        RecoveryRehearsalFingerprint = recovery.RehearsalFingerprint;
        RecoveryEvidenceFingerprint = recovery.RecoveryEvidenceFingerprint;
        DryRunFingerprint = recovery.DryRunFingerprint;
        VerificationFailureCount = verificationFailures;
        WarningCount = warnings;
        BusinessDiscrepancyCount = businessDiscrepancies;
        RuleVersions = ReadOnly(ruleVersions);
        SourceArtifactCount = ledger.SourceCount;
        SourceAccountingGapCount = Math.Max(
            findings.Where(pair => IsSourceAccountingCode(pair.Key)).Sum(pair => pair.Value),
            Math.Max(ledger.UnaccountedDispositionCount, ledger.SourceCount - ledger.DispositionCount));
        AccountedSourceCount = Math.Max(0, SourceArtifactCount - SourceAccountingGapCount);
        SourceAccountingStatus = SourceAccountingGapCount == 0 ? "COMPLETE" : "GAPS";
        TargetArtifactCount = ledger.TargetCount;
        LineagedTargetCount = ledger.LineageCount;
        TargetLineageGapCount = Math.Max(
            findings.Where(pair => IsTargetLineageCode(pair.Key)).Sum(pair => pair.Value),
            Math.Max(0, ledger.TargetCount - ledger.LineageCount));
        TargetLineageStatus = TargetLineageGapCount == 0 ? "COMPLETE" : "GAPS";
        FailedRecoveryEdges = recovery.FailedEdges;
        FalseReversibleEdges = recovery.FalseReversibleEdges;
        AffectedRecoveryArtifacts = recovery.AffectedArtifacts;
        RecoverableArtifacts = recovery.RecoverableArtifacts;
        IrrecoverableArtifacts = recovery.IrrecoverableArtifacts;
        UnknownRecoveryArtifacts = recovery.UnknownArtifacts;
        RecoveryCoveragePercentage = recovery.RecoverablePercentage;
        RecoveryReasons = recovery.Reasons.Order(StringComparer.Ordinal).ToArray();
        FindingsByRuleAndCode = ReadOnly(findings);
        EvidenceReferences = references;
        Concepts = pack.Metadata.Concepts
            .Select(concept => new AssuranceConceptSummary(concept.SemanticType, concept.DisplayName, concept.Description))
            .ToArray();
        Rules = pack.Metadata.RuleProviders.SelectMany(provider => provider.Rules)
            .Select(rule => new AssuranceRuleSummary(rule.Type, rule.Version, rule.Description))
            .OrderBy(rule => rule.Type, StringComparer.Ordinal).ToArray();
    }

    private static bool IsSourceAccountingCode(string key) =>
        key.EndsWith(":UnaccountedSource", StringComparison.Ordinal) ||
        key.EndsWith(":UnaccountedArtifact", StringComparison.Ordinal) ||
        key.EndsWith(":DuplicateDisposition", StringComparison.Ordinal) ||
        key.EndsWith(":MissingDisposition", StringComparison.Ordinal);

    private static bool IsTargetLineageCode(string key) =>
        key.EndsWith(":MissingLineage", StringComparison.Ordinal) ||
        key.EndsWith(":UnexpectedTarget", StringComparison.Ordinal);

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();

    private static ReadOnlyDictionary<string, TValue> ReadOnly<TValue>(IEnumerable<KeyValuePair<string, TValue>> values) =>
        new ReadOnlyDictionary<string, TValue>(
            new SortedDictionary<string, TValue>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal));
}

public static class PersistedAssuranceReportBuilder
{
    public static async Task<PersistedAssuranceReport> BuildAsync(string projectId, string projectName,
        DryRunId dryRunId, IDomainPack pack, EvidenceStoreManifest manifest,
        IAsyncEnumerable<JsonElement> records, RecoveryArtifactSummary recovery,
        SourceCheckpoint checkpoint, VerificationLedgerSummary ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(ledger);
        ValidateBindings(projectId, dryRunId, pack, manifest, recovery, checkpoint, ledger);

        var ruleVersions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var findings = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var failuresByCode = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var references = new List<AssuranceEvidenceReference>();
        long recordCount = 0;
        long failures = 0;
        long warnings = 0;
        string? verificationOutcome = null;
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordCount++;
            var recordId = RequiredText(record, "id");
            if (!Guid.TryParseExact(recordId, "D", out _))
                throw new InvalidDataException("Persisted Evidence contains an invalid record identifier.");
            var runId = RequiredText(record, "runId");
            if (!string.Equals(runId, manifest.RunId, StringComparison.Ordinal))
                throw new InvalidDataException("Persisted Evidence contains a record from another Verification run.");
            var ruleId = RequiredText(record, "ruleId");
            var ruleVersion = RequiredText(record, "ruleVersion");
            if (ruleVersions.TryGetValue(ruleId, out var existingVersion) && existingVersion != ruleVersion)
                throw new InvalidDataException("Persisted Evidence contains conflicting versions for one rule.");
            ruleVersions[ruleId] = ruleVersion;
            var result = RequiredText(record, "result");
            if (!Enum.TryParse<EvidenceResult>(result, ignoreCase: true, out var evidenceResult))
                throw new InvalidDataException("Persisted Evidence contains an unsupported result.");
            var code = OptionalText(record, "code") ?? "UnclassifiedFailure";
            if (code is "VerificationSummary" or "ExternalVerificationSummary")
            {
                if (verificationOutcome is not null)
                    throw new InvalidDataException("Persisted Evidence contains multiple Verification summary records.");
                verificationOutcome = evidenceResult switch
                {
                    EvidenceResult.Fail => "FAILED",
                    EvidenceResult.Warning => "PASSED WITH WARNINGS",
                    EvidenceResult.Pass => "PASSED",
                    _ => throw new InvalidDataException("Persisted Evidence summary has an unsupported result.")
                };
                continue;
            }
            if (evidenceResult == EvidenceResult.Warning) warnings++;
            if (evidenceResult != EvidenceResult.Fail) continue;

            failures++;
            failuresByCode[code] = failuresByCode.GetValueOrDefault(code) + 1;
            var key = $"{ruleId}:{code}";
            findings[key] = findings.GetValueOrDefault(key) + 1;
            references.Add(new AssuranceEvidenceReference(recordId, ruleId, code,
                OptionalText(record, "severity") ?? "Unspecified"));
        }

        if (recordCount != manifest.RecordCount)
            throw new InvalidDataException("Persisted Evidence record count does not match its manifest.");
        if (verificationOutcome is null)
            throw new InvalidDataException("Persisted Evidence has no Verification summary record.");

        references.Sort((left, right) => StringComparer.Ordinal.Compare(left.EvidenceId, right.EvidenceId));
        var businessDiscrepancies = pack.Metadata.BusinessDiscrepancyAggregation.Aggregate(
            failuresByCode, recovery.FalseReversibleEdges);
        return new PersistedAssuranceReport(projectId, projectName, dryRunId, pack, manifest, recovery,
            checkpoint, ledger, ruleVersions, verificationOutcome, failures, warnings, businessDiscrepancies,
            findings, references);
    }

    private static void ValidateBindings(string projectId, DryRunId dryRunId, IDomainPack pack,
        EvidenceStoreManifest manifest, RecoveryArtifactSummary recovery, SourceCheckpoint checkpoint,
        VerificationLedgerSummary ledger)
    {
        if (recovery.Id != dryRunId)
            throw new InvalidDataException("Recovery artifacts do not belong to the requested dry run.");
        if (checkpoint.Id != recovery.CheckpointId ||
            checkpoint.ManifestHash != recovery.CheckpointManifestHash ||
            checkpoint.SourceFingerprint != recovery.SourceFingerprint ||
            checkpoint.ProjectId != projectId ||
            checkpoint.ConfigurationHash != recovery.ConfigurationHash ||
            checkpoint.GraphHash != recovery.GraphHash)
            throw new InvalidDataException("Persisted checkpoint manifest does not match the Recovery assessment or Verification context.");
        if (manifest.Format != EvidenceFormat.StoreFormatVersion || !manifest.Complete ||
            !Guid.TryParseExact(manifest.RunId, "D", out var runId) ||
            runId != recovery.VerificationRunId.Value ||
            manifest.Fingerprint != recovery.VerificationEvidenceFingerprint)
            throw new InvalidDataException("Persisted Evidence does not match the Recovery assessment.");

        var context = manifest.VerificationContext
            ?? throw new InvalidDataException("Persisted Evidence has no Verification context binding.");
        var packProviders = context.ProviderVersions?
            .Where(pair => pair.Key != "proofshift.verification.generic")
            .ToArray() ?? [];
        if (!packProviders.Any(provider => provider.Key == pack.Id && provider.Value == pack.Version))
            throw new InvalidDataException("Persisted Verification context does not identify the exact configured domain pack and version.");
        if (context.ConfigurationHash != recovery.ConfigurationHash ||
            context.GraphHash != recovery.GraphHash ||
            context.CheckpointId != recovery.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture) ||
            context.CheckpointManifestHash != recovery.CheckpointManifestHash ||
            context.SourceFingerprint != recovery.SourceFingerprint ||
            context.RuleSetFingerprint != recovery.RuleSetFingerprint ||
            context.VerificationLedgerFingerprint != recovery.VerificationLedger.Fingerprint ||
            context.VerificationLedgerFingerprint != ledger.Fingerprint)
            throw new InvalidDataException("Persisted Evidence context does not match the Recovery assessment or ledger.");

        if (recovery.VerificationLedger.Fingerprint != ledger.Fingerprint ||
            recovery.VerificationLedger.SourceCount != ledger.SourceCount ||
            recovery.VerificationLedger.TargetCount != ledger.TargetCount ||
            recovery.VerificationLedger.DispositionCount != ledger.DispositionCount ||
            recovery.VerificationLedger.LineageCount != ledger.LineageCount ||
            recovery.VerificationLedger.JournalEntryCount != ledger.JournalEntryCount)
            throw new InvalidDataException("Persisted Verification ledger does not match its Recovery receipt.");

        if (context.ProjectionRunId is { } projectionRunId &&
            (!Guid.TryParseExact(projectionRunId, "D", out var parsedProjectionRunId) ||
             parsedProjectionRunId != recovery.ProjectionRunId.Value ||
             context.ProjectionFingerprint != recovery.ProjectionFingerprint))
            throw new InvalidDataException("Persisted Projection context does not match the Recovery assessment.");
        if (context.ProjectionRunId is null &&
            (string.IsNullOrWhiteSpace(context.ObservationId) ||
             string.IsNullOrWhiteSpace(context.ObservationRunId) ||
             string.IsNullOrWhiteSpace(context.TargetFingerprint)))
            throw new InvalidDataException("Persisted external Verification context has incomplete Observation identity.");
        if (context.ProjectionRunId is not null &&
            (context.ObservationId is not null || context.ObservationRunId is not null || context.TargetFingerprint is not null))
            throw new InvalidDataException("Persisted Verification context ambiguously combines Projection and Observation identities.");

        if (!recovery.VerificationLedger.Fingerprint.Equals(context.VerificationLedgerFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException($"Pack '{pack.Id}' report context is not bound to its persisted Verification ledger.");
    }

    private static string RequiredText(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Persisted Evidence is missing '{name}'.");

    private static string? OptionalText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

public sealed record PersistedAssuranceRunComparison
{
    public const string FormatVersion = "proofshift-persisted-assurance-comparison-v1";

    public string Format { get; }
    public string BeforeDryRunId { get; }
    public string AfterDryRunId { get; }
    public string BeforeQualification { get; }
    public string AfterQualification { get; }
    public bool QualificationChanged { get; }
    public bool ConfigurationHashChanged { get; }
    public bool GraphHashChanged { get; }
    public bool SourceFingerprintChanged { get; }
    public bool CheckpointSemanticsChanged { get; }
    public bool ProjectionSemanticsChanged { get; }
    public bool ObservationSemanticsChanged { get; }
    public bool RuleSetFingerprintChanged { get; }
    public bool VerificationEvidenceFingerprintChanged { get; }
    public bool DryRunFingerprintChanged { get; }
    public bool RecoveryPolicyFingerprintChanged { get; }
    public bool RecoveryEvidenceFingerprintChanged { get; }
    public bool RecoveryReadinessChanged { get; }
    public bool SourceAccountingGapsChanged { get; }
    public bool TargetLineageGapsChanged { get; }
    public IReadOnlyDictionary<string, long> FindingsResolved { get; }
    public IReadOnlyDictionary<string, long> FindingsIntroduced { get; }
    public IReadOnlyList<string> DifferenceAttribution { get; }

    private PersistedAssuranceRunComparison(PersistedAssuranceReport before, PersistedAssuranceReport after,
        IReadOnlyDictionary<string, long> resolved, IReadOnlyDictionary<string, long> introduced)
    {
        Format = FormatVersion;
        BeforeDryRunId = before.DryRunId;
        AfterDryRunId = after.DryRunId;
        BeforeQualification = before.Qualification;
        AfterQualification = after.Qualification;
        QualificationChanged = before.Qualification != after.Qualification;
        ConfigurationHashChanged = before.ConfigurationHash != after.ConfigurationHash;
        GraphHashChanged = before.GraphHash != after.GraphHash;
        SourceFingerprintChanged = before.SourceFingerprint != after.SourceFingerprint;
        CheckpointSemanticsChanged = before.CheckpointSemanticFingerprint != after.CheckpointSemanticFingerprint;
        ProjectionSemanticsChanged = before.ProjectionFingerprint != after.ProjectionFingerprint;
        ObservationSemanticsChanged = before.TargetFingerprint != after.TargetFingerprint;
        RuleSetFingerprintChanged = before.RuleSetFingerprint != after.RuleSetFingerprint;
        VerificationEvidenceFingerprintChanged =
            before.VerificationEvidenceFingerprint != after.VerificationEvidenceFingerprint;
        DryRunFingerprintChanged = before.DryRunFingerprint != after.DryRunFingerprint;
        RecoveryPolicyFingerprintChanged = before.RecoveryPolicyFingerprint != after.RecoveryPolicyFingerprint;
        RecoveryEvidenceFingerprintChanged = before.RecoveryEvidenceFingerprint != after.RecoveryEvidenceFingerprint;
        RecoveryReadinessChanged = before.RecoveryAssessmentOutcome != after.RecoveryAssessmentOutcome ||
            before.RecoveryRehearsalOutcome != after.RecoveryRehearsalOutcome ||
            before.Qualification != after.Qualification ||
            before.FailedRecoveryEdges != after.FailedRecoveryEdges ||
            before.FalseReversibleEdges != after.FalseReversibleEdges ||
            before.AffectedRecoveryArtifacts != after.AffectedRecoveryArtifacts ||
            before.RecoverableArtifacts != after.RecoverableArtifacts ||
            before.IrrecoverableArtifacts != after.IrrecoverableArtifacts ||
            before.UnknownRecoveryArtifacts != after.UnknownRecoveryArtifacts;
        SourceAccountingGapsChanged = before.SourceAccountingGapCount != after.SourceAccountingGapCount;
        TargetLineageGapsChanged = before.TargetLineageGapCount != after.TargetLineageGapCount;
        FindingsResolved = resolved;
        FindingsIntroduced = introduced;
        DifferenceAttribution = AttributeDifferences(this, resolved, introduced);
    }

    public static PersistedAssuranceRunComparison Create(PersistedAssuranceReport before,
        PersistedAssuranceReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.ProjectId != after.ProjectId || before.PackId != after.PackId ||
            before.PackVersion != after.PackVersion)
            throw new InvalidOperationException("Assurance runs must belong to the same project and exact domain pack.");
        var resolved = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var introduced = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in before.FindingsByRuleAndCode.Keys.Union(after.FindingsByRuleAndCode.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            var beforeCount = before.FindingsByRuleAndCode.GetValueOrDefault(key);
            var afterCount = after.FindingsByRuleAndCode.GetValueOrDefault(key);
            if (beforeCount > afterCount) resolved[key] = beforeCount - afterCount;
            else if (afterCount > beforeCount) introduced[key] = afterCount - beforeCount;
        }
        return new PersistedAssuranceRunComparison(before, after, resolved, introduced);
    }

    private static List<string> AttributeDifferences(PersistedAssuranceRunComparison comparison,
        IReadOnlyDictionary<string, long> resolved, IReadOnlyDictionary<string, long> introduced)
    {
        var attribution = new List<string>();
        if (comparison.ConfigurationHashChanged) attribution.Add("Project configuration changed.");
        if (comparison.GraphHashChanged) attribution.Add("Migration graph changed.");
        if (comparison.SourceFingerprintChanged) attribution.Add("Source checkpoint fingerprint changed.");
        if (comparison.CheckpointSemanticsChanged) attribution.Add("Checkpoint source or consistency semantics changed.");
        if (comparison.ProjectionSemanticsChanged) attribution.Add("Projected target state changed.");
        if (comparison.ObservationSemanticsChanged) attribution.Add("Observed target state changed.");
        if (comparison.RuleSetFingerprintChanged) attribution.Add("Verification rule set changed.");
        if (comparison.VerificationEvidenceFingerprintChanged) attribution.Add("Verification findings or evidence changed.");
        if (comparison.DryRunFingerprintChanged) attribution.Add("Dry-run fingerprint changed.");
        if (comparison.RecoveryPolicyFingerprintChanged) attribution.Add("Recovery policy changed.");
        if (comparison.RecoveryEvidenceFingerprintChanged || comparison.RecoveryReadinessChanged)
            attribution.Add("Recovery assessment, rehearsal, or readiness changed.");
        if (comparison.SourceAccountingGapsChanged) attribution.Add("Source-accounting gaps changed.");
        if (comparison.TargetLineageGapsChanged) attribution.Add("Target-lineage gaps changed.");
        if (resolved.Count > 0) attribution.Add("One or more findings were resolved.");
        if (introduced.Count > 0) attribution.Add("One or more findings were introduced.");
        if (attribution.Count == 0) attribution.Add("No persisted assurance differences.");
        return attribution;
    }
}

public sealed record CheckpointSemanticDifference(bool SourceFingerprintChanged, bool CheckpointSemanticsChanged);

public static class CheckpointSemanticComparison
{
    public static CheckpointSemanticDifference Compare(SourceCheckpoint before, SourceCheckpoint after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return new CheckpointSemanticDifference(
            before.SourceFingerprint != after.SourceFingerprint,
            Fingerprint(before) != Fingerprint(after));
    }

    internal static string Fingerprint(SourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var canonical = new StringBuilder();
        Append("proofshift-checkpoint-semantic-v1");
        Append(checkpoint.MaximumEndpointSkew.Ticks.ToString(CultureInfo.InvariantCulture));
        Append(checkpoint.CrossSystemAtomic.ToString(CultureInfo.InvariantCulture));
        Append(checkpoint.Replayable.ToString(CultureInfo.InvariantCulture));
        Append(checkpoint.Guarantee.ToString());
        Append(checkpoint.ArtifactCount.ToString(CultureInfo.InvariantCulture));
        Append(checkpoint.SourceFingerprint ?? string.Empty);
        Append(checkpoint.Endpoints.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var endpoint in checkpoint.Endpoints.OrderBy(item => item.SourceNodeKey, StringComparer.Ordinal))
        {
            Append(endpoint.SourceNodeKey);
            Append(endpoint.SystemId.Value);
            Append(endpoint.EndpointId.Value);
            Append(endpoint.Connector.Value);
            Append(endpoint.ConnectorVersion);
            Append(endpoint.SelectorHash);
            Append(endpoint.IdentityFields.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var identityField in endpoint.IdentityFields) Append(identityField);
            Append(endpoint.SourceConsistency.ToString());
            Append(endpoint.RequestedConsistencyStrategy);
            Append(endpoint.EffectiveConsistencyStrategy);
            Append(endpoint.ConsistencyDowngrade ?? string.Empty);
            Append(endpoint.Guarantee.ToString());
            Append(endpoint.Replayable.ToString(CultureInfo.InvariantCulture));
            Append(endpoint.ArtifactCount.ToString(CultureInfo.InvariantCulture));
            Append(endpoint.SourceFingerprint);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

        void Append(string value) => canonical
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }
}
