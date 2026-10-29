using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Reporting;

public sealed record PersistedExternalVerificationRun(
    string Format,
    string ProjectId,
    string VerificationRunId,
    string ObservationId,
    string ObservationRunId,
    string ConfigurationHash,
    string GraphHash,
    string CheckpointId,
    string CheckpointManifestHash,
    string SourceFingerprint,
    string TargetFingerprint,
    long SourceArtifactCount,
    long TargetArtifactCount,
    string RuleSetFingerprint,
    string RuntimeFingerprint,
    string EvidenceFingerprint,
    string Outcome,
    int RulesExecuted,
    int FailedRules,
    int WarningRules,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    VerificationLedgerStoreReceipt Ledger,
    string? IntegrityHash);

public sealed class FileSystemExternalVerificationRunStore
{
    public const string FormatVersion = "proofshift-external-verification-run-v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly string _root;

    public FileSystemExternalVerificationRunStore(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        _root = Path.GetFullPath(Path.Combine(projectDirectory, ".proofshift", "external-verifications"));
    }

    public async Task SaveAsync(string projectId, ExternalVerificationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        var runId = result.Run.Id.Value;
        var directory = Path.Combine(_root, runId.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(directory, "manifest.json");
        var temporaryPath = Path.Combine(directory, $"manifest-{Guid.NewGuid():N}.partial");
        var run = result.Run;
        var document = new PersistedExternalVerificationRun(FormatVersion, projectId,
            run.Id.Value.ToString("D", CultureInfo.InvariantCulture), run.ObservationId,
            run.ObservationRunId.Value.ToString("D", CultureInfo.InvariantCulture), run.ConfigurationHash,
            run.GraphHash, run.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture),
            run.CheckpointManifestHash, run.SourceFingerprint, run.TargetFingerprint, run.SourceArtifactCount,
            run.TargetArtifactCount, run.RuleSetFingerprint, run.RuntimeFingerprint, run.EvidenceFingerprint,
            run.Outcome.ToString(), run.RulesExecuted, run.FailedRules, run.WarningRules, run.StartedAt,
            run.CompletedAt, result.Ledger, null);
        var integrityHash = Hash(JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document with { IntegrityHash = integrityHash }, JsonOptions);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<PersistedExternalVerificationRun> ReadAsync(RunId verificationRunId,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_root, verificationRunId.Value.ToString("N", CultureInfo.InvariantCulture));
        var path = Path.GetFullPath(Path.Combine(directory, "manifest.json"));
        var relative = Path.GetRelativePath(_root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException("External Verification run path is outside its configured store.");

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<PersistedExternalVerificationRun>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("External Verification run manifest is empty.");
        if (document.Format != FormatVersion || document.IntegrityHash is null ||
            !Guid.TryParseExact(document.VerificationRunId, "D", out var parsedRunId) ||
            parsedRunId != verificationRunId.Value ||
            !Guid.TryParseExact(document.ObservationRunId, "D", out _) ||
            !Guid.TryParseExact(document.CheckpointId, "D", out _) ||
            document.Ledger.StoreId != verificationRunId.Value)
            throw new InvalidDataException("External Verification run manifest has invalid identity or format.");

        var expected = Hash(JsonSerializer.SerializeToUtf8Bytes(document with { IntegrityHash = null }, JsonOptions));
        if (!string.Equals(document.IntegrityHash, expected, StringComparison.Ordinal))
            throw new InvalidDataException("External Verification run manifest integrity verification failed.");
        return document;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed record PersistedVerificationEvidenceReference(
    string EvidenceId,
    string RuleId,
    string? Code,
    string Result,
    string Severity);

public sealed record PersistedEvaluationScope(
    string SourceNodeKey,
    string Id,
    string Type,
    string Fingerprint,
    long ArtifactCount);

public sealed record PersistedVerificationReport
{
    public const string FormatVersion = "proofshift-persisted-verification-report-v1";
    public string Format { get; }
    public string ProjectId { get; }
    public string ProjectName { get; }
    public string AssuranceScope { get; }
    public string ClaimBoundary { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public string CheckpointId { get; }
    public string CheckpointManifestHash { get; }
    public string CheckpointSemanticFingerprint { get; }
    public string SourceFingerprint { get; }
    public string ObservationId { get; }
    public string ObservationRunId { get; }
    public string TargetFingerprint { get; }
    public string RuleSetFingerprint { get; }
    public IReadOnlyDictionary<string, string> ProviderVersions { get; }
    public string VerificationRunId { get; }
    public string VerificationOutcome { get; }
    public int VerificationFailureCount { get; }
    public string EvidenceFingerprint { get; }
    public long SourceArtifactCount { get; }
    public long AccountedSourceCount { get; }
    public long SourceAccountingGapCount { get; }
    public string SourceAccountingStatus => SourceAccountingGapCount == 0 ? "COMPLETE" : "GAPS";
    public long TargetArtifactCount { get; }
    public long LineagedTargetCount { get; }
    public long TargetLineageGapCount { get; }
    public string TargetLineageStatus => TargetLineageGapCount == 0 ? "COMPLETE" : "GAPS";
    public IReadOnlyDictionary<string, long> FailedFindingGroups { get; }
    public IReadOnlyList<PersistedVerificationEvidenceReference> EvidenceReferences { get; }
    public string LineageBasis { get; }
    public bool RecoveryAssessed { get; }
    public bool QualificationAvailable { get; }
    public IReadOnlyList<PersistedEvaluationScope> EvaluationScopes { get; }
    public string EvaluationScopeClaimBoundary { get; }

    internal PersistedVerificationReport(string projectId, string projectName,
        PersistedExternalVerificationRun run, EvidenceStoreManifest evidenceManifest,
        SourceCheckpoint checkpoint, VerificationLedgerSummary ledger,
        IReadOnlyDictionary<string, long> findings,
        IReadOnlyList<PersistedVerificationEvidenceReference> references)
    {
        Format = FormatVersion;
        ProjectId = Required(projectId, nameof(projectId));
        ProjectName = Required(projectName, nameof(projectName));
        AssuranceScope = "external-target-verification";
        ClaimBoundary = "ProofShift independently verified the configured graph-derived expected state against the observed external target using the configured Verification rules. This does not prove that ProofShift executed the migration, does not establish observed vendor execution lineage, and does not include Recovery qualification.";
        ConfigurationHash = run.ConfigurationHash;
        GraphHash = run.GraphHash;
        CheckpointId = run.CheckpointId;
        CheckpointManifestHash = run.CheckpointManifestHash;
        CheckpointSemanticFingerprint = CheckpointSemanticComparison.Fingerprint(checkpoint);
        SourceFingerprint = run.SourceFingerprint;
        ObservationId = run.ObservationId;
        ObservationRunId = run.ObservationRunId;
        TargetFingerprint = run.TargetFingerprint;
        RuleSetFingerprint = run.RuleSetFingerprint;
        ProviderVersions = ReadOnly(evidenceManifest.VerificationContext?.ProviderVersions);
        VerificationRunId = run.VerificationRunId;
        VerificationOutcome = run.Outcome;
        VerificationFailureCount = run.FailedRules;
        EvidenceFingerprint = run.EvidenceFingerprint;
        SourceArtifactCount = ledger.SourceCount;
        SourceAccountingGapCount = Math.Max(ledger.UnaccountedDispositionCount,
            Math.Max(0, ledger.SourceCount - ledger.DispositionCount));
        AccountedSourceCount = Math.Max(0, SourceArtifactCount - SourceAccountingGapCount);
        TargetArtifactCount = ledger.TargetCount;
        LineagedTargetCount = ledger.LineageCount;
        TargetLineageGapCount = Math.Max(0, ledger.TargetCount - ledger.LineageCount);
        FailedFindingGroups = ReadOnly(findings);
        EvidenceReferences = references;
        LineageBasis = "graph-derived expected lineage";
        RecoveryAssessed = false;
        QualificationAvailable = false;
        EvaluationScopes = checkpoint.Endpoints
            .Where(endpoint => endpoint.EvaluationScopeId is not null &&
                endpoint.EvaluationScopeType is not null &&
                endpoint.EvaluationScopeFingerprint is not null)
            .OrderBy(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal)
            .Select(endpoint => new PersistedEvaluationScope(endpoint.SourceNodeKey,
                endpoint.EvaluationScopeId!, endpoint.EvaluationScopeType!,
                endpoint.EvaluationScopeFingerprint!, endpoint.ArtifactCount))
            .ToArray();
        EvaluationScopeClaimBoundary = EvaluationScopes.Count == 0
            ? "Results are limited to configured source selectors; no claim is made about artifacts outside them."
            : EvaluationScopes.Count == checkpoint.Endpoints.Count
                ? "Results are limited to the declared evaluation scopes; no result proves completeness outside them."
                : "Results are limited to each source's configured selector; only listed sources have explicit evaluation scopes, and no result proves completeness outside those selectors.";
    }

    private static System.Collections.ObjectModel.ReadOnlyDictionary<string, string> ReadOnly(
        IReadOnlyDictionary<string, string>? values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            (values ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static System.Collections.ObjectModel.ReadOnlyDictionary<string, long> ReadOnly(
        IReadOnlyDictionary<string, long> values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", name) : value;
}

public static class PersistedVerificationReportBuilder
{
    public static async Task<PersistedVerificationReport> BuildAsync(
        string expectedProjectId,
        string projectName,
        PersistedExternalVerificationRun run,
        EvidenceStoreManifest evidenceManifest,
        IAsyncEnumerable<JsonElement> evidenceRecords,
        SourceCheckpoint checkpoint,
        VerificationLedgerSummary ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(evidenceManifest);
        ArgumentNullException.ThrowIfNull(evidenceRecords);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(ledger);
        var context = evidenceManifest.VerificationContext
            ?? throw new InvalidDataException("External Verification Evidence has no persisted context.");
        if (run.ProjectId != expectedProjectId || string.IsNullOrWhiteSpace(run.ProjectId))
            throw new InvalidDataException("Persisted external Verification belongs to a different project.");
        if (evidenceManifest.Format != EvidenceFormat.StoreFormatVersion ||
            evidenceManifest.CanonicalizationVersion != EvidenceFormat.CanonicalizationVersion ||
            evidenceManifest.RunId != run.VerificationRunId ||
            evidenceManifest.Fingerprint != run.EvidenceFingerprint ||
            context.ProjectionRunId is not null || context.ProjectionFingerprint is not null ||
            context.ObservationId != run.ObservationId ||
            context.ObservationRunId != run.ObservationRunId ||
            context.ConfigurationHash != run.ConfigurationHash ||
            context.GraphHash != run.GraphHash ||
            context.CheckpointId != run.CheckpointId ||
            context.CheckpointManifestHash != run.CheckpointManifestHash ||
            context.SourceFingerprint != run.SourceFingerprint ||
            context.TargetFingerprint != run.TargetFingerprint ||
            context.RuleSetFingerprint != run.RuleSetFingerprint ||
            context.VerificationLedgerFingerprint != run.Ledger.Fingerprint ||
            checkpoint.Status != CheckpointStatus.Complete ||
            checkpoint.Id.Value.ToString("D", CultureInfo.InvariantCulture) != run.CheckpointId ||
            checkpoint.ManifestHash != run.CheckpointManifestHash ||
            checkpoint.SourceFingerprint != run.SourceFingerprint ||
            checkpoint.ConfigurationHash != run.ConfigurationHash ||
            checkpoint.GraphHash != run.GraphHash ||
            ledger.Fingerprint != run.Ledger.Fingerprint ||
            ledger.SourceCount != run.SourceArtifactCount ||
            ledger.TargetCount != run.TargetArtifactCount)
            throw new InvalidDataException("Persisted external Verification bindings do not match Evidence, checkpoint, ledger, or project.");

        var findings = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var references = new List<PersistedVerificationEvidenceReference>();
        var failedRuleIds = new HashSet<string>(StringComparer.Ordinal);
        var summaryRecords = 0;
        long count = 0;
        await foreach (var record in evidenceRecords.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.TryGetProperty("runId", out var recordRunId) ||
                !string.Equals(recordRunId.GetString(), run.VerificationRunId, StringComparison.Ordinal))
                throw new InvalidDataException("Persisted Evidence record belongs to a different Verification run.");
            count++;
            if (!record.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<EvidenceResult>(result.GetString(), ignoreCase: true, out var evidenceResult))
                throw new InvalidDataException("Persisted Evidence contains an unsupported result.");
            var code = ReadOptionalString(record, "code");
            if (code == "ExternalVerificationSummary")
            {
                summaryRecords++;
                var expectedSummaryResult = run.Outcome switch
                {
                    nameof(VerificationOutcome.Failed) => EvidenceResult.Fail,
                    nameof(VerificationOutcome.PassedWithWarnings) => EvidenceResult.Warning,
                    nameof(VerificationOutcome.Passed) => EvidenceResult.Pass,
                    _ => throw new InvalidDataException("Persisted external Verification has an unsupported outcome.")
                };
                if (evidenceResult != expectedSummaryResult)
                    throw new InvalidDataException("Persisted Verification summary does not match its run outcome.");
                continue;
            }
            if (evidenceResult != EvidenceResult.Fail) continue;

            var ruleId = ReadString(record, "ruleId");
            failedRuleIds.Add(ruleId);
            var key = code is null ? ruleId : $"{ruleId}:{code}";
            findings[key] = findings.GetValueOrDefault(key) + 1;
            references.Add(new PersistedVerificationEvidenceReference(
                ReadString(record, "id"), ruleId, code, "Fail",
                ReadString(record, "severity")));
        }
        if (count != evidenceManifest.RecordCount || summaryRecords != 1 ||
            failedRuleIds.Count != run.FailedRules ||
            !Enum.TryParse<VerificationOutcome>(run.Outcome, ignoreCase: false, out _))
            throw new InvalidDataException("Persisted Evidence record count, summary, or failed-rule total does not match its run manifest.");

        return new PersistedVerificationReport(expectedProjectId, projectName, run, evidenceManifest,
            checkpoint, ledger, findings, references);
    }

    private static string ReadString(JsonElement record, string propertyName)
    {
        if (!record.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Persisted Evidence is missing required '{propertyName}' metadata.");
        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement record, string propertyName) =>
        record.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record PersistedVerificationFindingDifference(string Finding, long Count);

public sealed record PersistedVerificationRunComparison
{
    public string ProjectId { get; }
    public string BeforeVerificationRunId { get; }
    public string AfterVerificationRunId { get; }
    public bool ConfigurationHashChanged { get; }
    public bool GraphHashChanged { get; }
    public bool SourceFingerprintChanged { get; }
    public bool CheckpointSemanticsChanged { get; }
    public bool TargetFingerprintChanged { get; }
    public bool RuleSetFingerprintChanged { get; }
    public bool ProviderVersionsChanged { get; }
    public bool VerificationOutcomeChanged { get; }
    public bool EvidenceFingerprintChanged { get; }
    public bool SourceAccountingGapsChanged { get; }
    public bool TargetLineageGapsChanged { get; }
    public string RecoveryComparison { get; }
    public IReadOnlyList<PersistedVerificationFindingDifference> FindingsResolved { get; }
    public IReadOnlyList<PersistedVerificationFindingDifference> FindingsIntroduced { get; }

    private PersistedVerificationRunComparison(PersistedVerificationReport before,
        PersistedVerificationReport after, IReadOnlyList<PersistedVerificationFindingDifference> resolved,
        IReadOnlyList<PersistedVerificationFindingDifference> introduced)
    {
        ProjectId = before.ProjectId;
        RecoveryComparison = "not applicable to verification-only comparison";
        BeforeVerificationRunId = before.VerificationRunId;
        AfterVerificationRunId = after.VerificationRunId;
        ConfigurationHashChanged = before.ConfigurationHash != after.ConfigurationHash;
        GraphHashChanged = before.GraphHash != after.GraphHash;
        SourceFingerprintChanged = before.SourceFingerprint != after.SourceFingerprint;
        CheckpointSemanticsChanged = before.CheckpointSemanticFingerprint != after.CheckpointSemanticFingerprint;
        TargetFingerprintChanged = before.TargetFingerprint != after.TargetFingerprint;
        RuleSetFingerprintChanged = before.RuleSetFingerprint != after.RuleSetFingerprint;
        ProviderVersionsChanged = !DictionaryEquals(before.ProviderVersions, after.ProviderVersions);
        VerificationOutcomeChanged = before.VerificationOutcome != after.VerificationOutcome;
        EvidenceFingerprintChanged = before.EvidenceFingerprint != after.EvidenceFingerprint;
        SourceAccountingGapsChanged = before.SourceAccountingGapCount != after.SourceAccountingGapCount;
        TargetLineageGapsChanged = before.TargetLineageGapCount != after.TargetLineageGapCount;
        FindingsResolved = resolved;
        FindingsIntroduced = introduced;
    }

    public static PersistedVerificationRunComparison Create(PersistedVerificationReport before,
        PersistedVerificationReport after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!string.Equals(before.ProjectId, after.ProjectId, StringComparison.Ordinal))
            throw new InvalidDataException("Verification-only comparisons require the same project identity.");

        var keys = before.FailedFindingGroups.Keys.Union(after.FailedFindingGroups.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var resolved = new List<PersistedVerificationFindingDifference>();
        var introduced = new List<PersistedVerificationFindingDifference>();
        foreach (var key in keys)
        {
            var beforeCount = before.FailedFindingGroups.GetValueOrDefault(key);
            var afterCount = after.FailedFindingGroups.GetValueOrDefault(key);
            if (beforeCount > afterCount) resolved.Add(new(key, beforeCount - afterCount));
            if (afterCount > beforeCount) introduced.Add(new(key, afterCount - beforeCount));
        }
        return new PersistedVerificationRunComparison(before, after, resolved, introduced);
    }

    private static bool DictionaryEquals(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) => left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}
