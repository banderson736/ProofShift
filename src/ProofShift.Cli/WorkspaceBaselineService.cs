using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Cli;

internal sealed record WorkspaceBaselineRecord
{
    public const string CurrentFormat = "proofshift-evaluation-baseline-v1";

    public string Format { get; init; } = CurrentFormat;
    public required Guid BaselineId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset PromotedAt { get; init; }
    public Guid? PreviousActiveBaselineId { get; init; }
    public Guid? PromotionComparisonBaselineId { get; init; }
    public string? PromotionComparisonOutcomeClassification { get; init; }
    public required Guid RunId { get; init; }
    public string? RunManifestRelativePath { get; init; }
    public required string RunManifestIdentityFingerprint { get; init; }
    public required string RunManifestHash { get; init; }
    public required string EffectivePlanFingerprint { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string GraphHash { get; init; }
    public required IReadOnlyList<string> DiscoveryFingerprints { get; init; }
    public string? CheckpointId { get; init; }
    public string? CheckpointManifestHash { get; init; }
    public string? CheckpointSourceFingerprint { get; init; }
    public string? CheckpointStoreScope { get; init; }
    public bool? ReplayedCheckpoint { get; init; }
    public required string VerificationRunId { get; init; }
    public required string VerificationOutcome { get; init; }
    public required string VerificationEvidenceFingerprint { get; init; }
    public required string VerificationLedgerFingerprint { get; init; }
    public required string ReconciliationFingerprint { get; init; }
    public required string EvidencePackageIdentity { get; init; }
    public required string EvidencePackageRelativePath { get; init; }
    public required string Disposition { get; init; }
    public required string ProductVersion { get; init; }
    public string? SourceRevision { get; init; }
    public string? SourceTree { get; init; }
    public required string RecordHash { get; init; }
}

internal sealed record WorkspaceBaselinePromotionResult(
    WorkspaceBaselineRecord Baseline,
    bool Created,
    bool AlreadyActive);

internal sealed class WorkspaceBaselineAlreadyPromotedException(string message) : InvalidOperationException(message);

internal sealed record WorkspaceBaselineCatalogEntry(
    Guid BaselineId,
    string RecordHash,
    DateTimeOffset PromotedAt,
    Guid? PreviousActiveBaselineId);

internal sealed record WorkspaceBaselineCatalog
{
    public const string CurrentFormat = "proofshift-evaluation-baseline-catalog-v1";

    public string Format { get; init; } = CurrentFormat;
    public required Guid WorkspaceId { get; init; }
    public Guid? ActiveBaselineId { get; init; }
    public required IReadOnlyList<WorkspaceBaselineCatalogEntry> History { get; init; }
    public required string IntegrityHash { get; init; }
}

internal sealed record WorkspaceBaselinePlan(WorkspaceBaselineRecord Baseline, WorkspaceEvaluationPlan Plan,
    bool SemanticallyComparable);

internal static class WorkspaceBaselineService
{
    private const string BaselineDirectory = ".proofshift/baselines";
    private const string CatalogFileName = "catalog.json";
    private const string LockFileName = "catalog.lock";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<WorkspaceBaselinePromotionResult> PromoteAsync(string workspaceDirectory,
        string runReference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runReference, out var runId) || runId == Guid.Empty)
            throw new ArgumentException("Run reference must be a non-empty run GUID.", nameof(runReference));

        var (root, workspace) = await ReadWorkspaceAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var runDirectory = ResolveContained(root,
            Path.Combine(workspace.Paths.Runs, runId.ToString("D")));
        EnsureNoReparsePoints(root, runDirectory);
        if (!Directory.Exists(runDirectory))
            throw new FileNotFoundException("The selected workspace run does not exist.", runDirectory);
        var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        ValidatePromotionRun(workspace.WorkspaceId, runId, run);

        var evidenceDirectory = ResolveContained(root, workspace.Paths.Evidence);
        var packagePath = Path.Combine(evidenceDirectory, $"{runId:D}.zip");
        EnsureNoReparsePoints(root, packagePath);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("A final evidence package for the selected run is required before promotion.", packagePath);
        var package = await WorkspaceEvidencePackageService.ValidateAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        ValidatePackageBinding(run, package);

        var baselineDirectory = ResolveContained(root, BaselineDirectory);
        Directory.CreateDirectory(baselineDirectory);
        EnsureNoReparsePoints(root, baselineDirectory);
        var lockPath = Path.Combine(baselineDirectory, LockFileName);
        EnsureNoReparsePoints(root, lockPath);
        await using var catalogLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.WriteThrough);

        var catalog = await ReadCatalogAsync(root, workspace, cancellationToken).ConfigureAwait(false);
        var history = await ReadHistoryAsync(root, workspace, catalog, cancellationToken).ConfigureAwait(false);
        var priorPromotion = history.SingleOrDefault(item => item.RunId == runId);
        if (priorPromotion is not null)
        {
            if (catalog.ActiveBaselineId == priorPromotion.BaselineId)
                return new(priorPromotion, Created: false, AlreadyActive: true);
            throw new WorkspaceBaselineAlreadyPromotedException(
                "This run is already represented by a historical baseline. Historical baseline reactivation is not supported; no history was changed.");
        }

        run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        ValidatePromotionRun(workspace.WorkspaceId, runId, run);
        package = await WorkspaceEvidencePackageService.ValidateAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        ValidatePackageBinding(run, package);
        var priorComparison = run.BaselineId is null
            ? null
            : await WorkspaceBaselineComparisonService.ReadAsync(root, run.RunId, run, cancellationToken)
                .ConfigureAwait(false);
        var baselineId = Guid.NewGuid();
        var promotedAt = DateTimeOffset.UtcNow;
        var baseline = new WorkspaceBaselineRecord
        {
            BaselineId = baselineId,
            WorkspaceId = workspace.WorkspaceId,
            PromotedAt = promotedAt,
            PreviousActiveBaselineId = catalog.ActiveBaselineId,
            PromotionComparisonBaselineId = priorComparison?.BaselineId,
            PromotionComparisonOutcomeClassification = priorComparison?.OutcomeClassification,
            RunId = run.RunId,
            RunManifestRelativePath = Normalize(Path.GetRelativePath(root,
                Path.Combine(runDirectory, "run.json"))),
            RunManifestIdentityFingerprint = run.IdentityFingerprint,
            RunManifestHash = run.ManifestHash!,
            EffectivePlanFingerprint = run.PlanFingerprint!,
            ConfigurationHash = run.ConfigurationHash!,
            GraphHash = run.GraphHash!,
            DiscoveryFingerprints = run.DiscoveryFingerprints.Order(StringComparer.Ordinal).ToArray(),
            CheckpointId = run.CheckpointId,
            CheckpointManifestHash = run.CheckpointManifestHash,
            CheckpointSourceFingerprint = run.CheckpointSourceFingerprint,
            CheckpointStoreScope = run.CheckpointStoreScope,
            ReplayedCheckpoint = run.ReplayedCheckpoint,
            VerificationRunId = run.VerificationRunId!,
            VerificationOutcome = run.VerificationOutcome!,
            VerificationEvidenceFingerprint = run.VerificationEvidenceFingerprint!,
            VerificationLedgerFingerprint = run.VerificationLedgerFingerprint!,
            ReconciliationFingerprint = ComputeReconciliationFingerprint(run),
            EvidencePackageIdentity = package.PackageIdentity!,
            EvidencePackageRelativePath = Normalize(Path.GetRelativePath(root, packagePath)),
            Disposition = run.Disposition!,
            ProductVersion = run.ProductVersion,
            SourceRevision = run.SourceRevision,
            SourceTree = run.SourceTree,
            RecordHash = string.Empty
        };
        baseline = baseline with { RecordHash = ComputeBaselineHash(baseline) };

        var baselinePath = GetBaselinePath(baselineDirectory, baselineId);
        EnsureNoReparsePoints(root, baselinePath);
        await WriteNewBaselineAsync(baselinePath, baseline, cancellationToken).ConfigureAwait(false);

        var catalogHistory = catalog.History.Append(new WorkspaceBaselineCatalogEntry(
            baselineId, baseline.RecordHash, promotedAt, catalog.ActiveBaselineId)).ToArray();
        var updatedCatalog = new WorkspaceBaselineCatalog
        {
            WorkspaceId = workspace.WorkspaceId,
            ActiveBaselineId = baselineId,
            History = catalogHistory,
            IntegrityHash = string.Empty
        };
        updatedCatalog = updatedCatalog with { IntegrityHash = ComputeCatalogHash(updatedCatalog) };
        await WriteCatalogAsync(root, baselineDirectory, updatedCatalog, cancellationToken).ConfigureAwait(false);
        return new(baseline, Created: true, AlreadyActive: false);
    }

    public static async Task<IReadOnlyList<WorkspaceBaselineRecord>> ListAsync(string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var (root, workspace) = await ReadWorkspaceAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var catalog = await ReadCatalogAsync(root, workspace, cancellationToken).ConfigureAwait(false);
        return await ReadHistoryAsync(root, workspace, catalog, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkspaceBaselineRecord?> ActiveAsync(string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var (root, workspace) = await ReadWorkspaceAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var catalog = await ReadCatalogAsync(root, workspace, cancellationToken).ConfigureAwait(false);
        if (catalog.ActiveBaselineId is not { } activeId)
            return null;
        var history = await ReadHistoryAsync(root, workspace, catalog, cancellationToken).ConfigureAwait(false);
        return history.Single(item => item.BaselineId == activeId);
    }

    public static async Task<WorkspaceBaselinePlan?> ReadPlanAsync(string workspaceDirectory,
        string baselineReference, CancellationToken cancellationToken)
    {
        WorkspaceBaselineRecord? baseline;
        if (baselineReference == "active")
            baseline = await ActiveAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        else
            baseline = await ShowAsync(workspaceDirectory, baselineReference, cancellationToken).ConfigureAwait(false);
        if (baseline is null)
            return null;

        var root = Path.GetFullPath(workspaceDirectory);
        var packagePath = ResolveContained(root, baseline.EvidencePackageRelativePath);
        EnsureNoReparsePoints(root, packagePath);
        var plan = await WorkspaceEvidencePackageService.ReadValidatedEffectivePlanAsync(packagePath,
            cancellationToken).ConfigureAwait(false);
        if (plan.WorkspaceId != baseline.WorkspaceId ||
            plan.Fingerprint != baseline.EffectivePlanFingerprint)
            throw new InvalidDataException("Promoted baseline effective plan does not match its stored identities.");
        if (plan.SemanticModelVersion != WorkspaceEvaluationPlan.CurrentSemanticModelVersion)
            return new WorkspaceBaselinePlan(baseline, plan, SemanticallyComparable: false);
        if (plan.ConfigurationFingerprint != baseline.ConfigurationHash)
            throw new InvalidDataException("Promoted baseline effective plan does not match its configuration identity.");
        return new WorkspaceBaselinePlan(baseline, plan, SemanticallyComparable: true);
    }

    public static async Task<WorkspaceBaselineRecord> ShowAsync(string workspaceDirectory,
        string baselineReference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(baselineReference, out var baselineId) || baselineId == Guid.Empty)
            throw new ArgumentException("Baseline reference must be a non-empty baseline GUID.", nameof(baselineReference));
        var (root, workspace) = await ReadWorkspaceAsync(workspaceDirectory, cancellationToken).ConfigureAwait(false);
        var catalog = await ReadCatalogAsync(root, workspace, cancellationToken).ConfigureAwait(false);
        var history = await ReadHistoryAsync(root, workspace, catalog, cancellationToken).ConfigureAwait(false);
        return history.SingleOrDefault(item => item.BaselineId == baselineId)
            ?? throw new FileNotFoundException("The selected baseline does not exist in this workspace.", baselineId.ToString("D"));
    }

    internal static string GetBaselineRecordPath(string workspaceDirectory, Guid baselineId) =>
        GetBaselinePath(ResolveContained(Path.GetFullPath(workspaceDirectory), BaselineDirectory), baselineId);

    internal static byte[] SerializeBaselineRecord(WorkspaceBaselineRecord baseline) =>
        JsonSerializer.SerializeToUtf8Bytes(baseline, JsonOptions);

    internal static WorkspaceBaselineRecord ValidatePackagedBaselineRecord(ReadOnlySpan<byte> bytes,
        WorkspaceRunManifest candidate)
    {
        var baseline = JsonSerializer.Deserialize<WorkspaceBaselineRecord>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Packaged baseline reference is empty.");
        if (baseline.Format != WorkspaceBaselineRecord.CurrentFormat ||
            baseline.BaselineId != candidate.BaselineId ||
            baseline.WorkspaceId != candidate.WorkspaceId ||
            baseline.RunId != candidate.BaselineRunId ||
            baseline.RunManifestIdentityFingerprint != candidate.BaselineRunManifestIdentityFingerprint ||
            baseline.EffectivePlanFingerprint != candidate.BaselinePlanFingerprint ||
            baseline.EvidencePackageIdentity != candidate.BaselineEvidencePackageIdentity ||
            !string.Equals(ComputeBaselineHash(baseline), baseline.RecordHash, StringComparison.Ordinal))
            throw new InvalidDataException("Packaged baseline reference does not match the candidate run or its integrity identity.");
        return baseline;
    }

    private static async Task<(string Root, EvaluationWorkspaceManifest Workspace)> ReadWorkspaceAsync(
        string workspaceDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before baseline operations.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        return (root, workspace);
    }

    private static void ValidatePromotionRun(Guid workspaceId, Guid runId, WorkspaceRunManifest run)
    {
        if (run.WorkspaceId != workspaceId || run.RunId != runId)
            throw new InvalidDataException("Run manifest identity does not match the selected workspace and run.");
        if (run.State != "Completed" || run.Disposition is not ("QUALIFIED" or "NOT QUALIFIED"))
            throw new InvalidOperationException("Only a completed QUALIFIED or NOT QUALIFIED run can be promoted.");
        if (string.IsNullOrWhiteSpace(run.ManifestHash) ||
            string.IsNullOrWhiteSpace(run.PlanFingerprint) ||
            string.IsNullOrWhiteSpace(run.ConfigurationHash) ||
            string.IsNullOrWhiteSpace(run.GraphHash) ||
            string.IsNullOrWhiteSpace(run.VerificationRunId) ||
            string.IsNullOrWhiteSpace(run.VerificationOutcome) ||
            string.IsNullOrWhiteSpace(run.VerificationEvidenceFingerprint) ||
            string.IsNullOrWhiteSpace(run.VerificationLedgerFingerprint))
            throw new InvalidDataException("Completed run is missing required baseline identity.");
    }

    private static void ValidatePackageBinding(WorkspaceRunManifest run,
        WorkspaceEvidencePackageValidation package)
    {
        if (!package.Valid)
            throw new InvalidDataException("The selected run evidence package failed offline integrity validation: " +
                string.Join("; ", package.Failures));
        if (package.PackageType != "final" || package.ExecutionState != "Completed" ||
            package.RunId != run.RunId || package.Disposition != run.Disposition ||
            package.RunManifestIdentityFingerprint != run.IdentityFingerprint ||
            package.RunManifestHash != run.ManifestHash ||
            package.PlanFingerprint != run.PlanFingerprint ||
            package.ProductVersion != run.ProductVersion ||
            string.IsNullOrWhiteSpace(package.PackageIdentity))
            throw new InvalidDataException("Final evidence package does not bind to the selected completed run.");
    }

    private static async Task<WorkspaceBaselineCatalog> ReadCatalogAsync(string root,
        EvaluationWorkspaceManifest workspace,
        CancellationToken cancellationToken)
    {
        var directory = ResolveContained(root, BaselineDirectory);
        EnsureNoReparsePoints(root, directory);
        var path = Path.Combine(directory, CatalogFileName);
        EnsureNoReparsePoints(root, path);
        if (!File.Exists(path))
            return new WorkspaceBaselineCatalog
            {
                WorkspaceId = workspace.WorkspaceId,
                ActiveBaselineId = null,
                History = [],
                IntegrityHash = string.Empty
            };

        var catalog = JsonSerializer.Deserialize<WorkspaceBaselineCatalog>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Baseline catalog is empty.");
        if (catalog.Format != WorkspaceBaselineCatalog.CurrentFormat ||
            catalog.WorkspaceId != workspace.WorkspaceId || catalog.History is null ||
            string.IsNullOrWhiteSpace(catalog.IntegrityHash) ||
            !string.Equals(ComputeCatalogHash(catalog), catalog.IntegrityHash, StringComparison.Ordinal))
            throw new InvalidDataException("Baseline catalog format, workspace binding, or integrity hash is invalid.");

        var ids = new HashSet<Guid>();
        Guid? previousActive = null;
        foreach (var entry in catalog.History)
        {
            if (entry.BaselineId == Guid.Empty || !ids.Add(entry.BaselineId) ||
                entry.PreviousActiveBaselineId != previousActive ||
                string.IsNullOrWhiteSpace(entry.RecordHash))
                throw new InvalidDataException("Baseline catalog history is malformed or inconsistent.");
            previousActive = entry.BaselineId;
        }
        if (catalog.ActiveBaselineId != previousActive)
            throw new InvalidDataException("Active baseline does not match the latest retained promotion.");
        return catalog;
    }

    private static async Task<IReadOnlyList<WorkspaceBaselineRecord>> ReadHistoryAsync(string root,
        EvaluationWorkspaceManifest workspace, WorkspaceBaselineCatalog catalog,
        CancellationToken cancellationToken)
    {
        var baselineDirectory = ResolveContained(root, BaselineDirectory);
        var result = new List<WorkspaceBaselineRecord>(catalog.History.Count);
        foreach (var entry in catalog.History)
        {
            var path = GetBaselinePath(baselineDirectory, entry.BaselineId);
            EnsureNoReparsePoints(root, path);
            var baseline = JsonSerializer.Deserialize<WorkspaceBaselineRecord>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Baseline record is empty.");
            ValidateBaselineRecord(baseline, entry, workspace.WorkspaceId);
            await ValidateReferencesAsync(root, workspace, baseline, cancellationToken).ConfigureAwait(false);
            result.Add(baseline);
        }
        return result;
    }

    private static void ValidateBaselineRecord(WorkspaceBaselineRecord baseline,
        WorkspaceBaselineCatalogEntry entry, Guid workspaceId)
    {
        if (baseline.Format != WorkspaceBaselineRecord.CurrentFormat ||
            baseline.WorkspaceId != workspaceId || baseline.BaselineId != entry.BaselineId ||
            baseline.PromotedAt != entry.PromotedAt ||
            baseline.PreviousActiveBaselineId != entry.PreviousActiveBaselineId ||
            baseline.RecordHash != entry.RecordHash ||
            !string.Equals(ComputeBaselineHash(baseline), baseline.RecordHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Baseline record {entry.BaselineId:D} failed integrity or catalog binding validation.");
    }

    private static async Task ValidateReferencesAsync(string root, EvaluationWorkspaceManifest workspace,
        WorkspaceBaselineRecord baseline,
        CancellationToken cancellationToken)
    {
        var runManifestRelativePath = baseline.RunManifestRelativePath ??
            Normalize(Path.Combine(workspace.Paths.Runs, baseline.RunId.ToString("D"), "run.json"));
        var runManifestPath = ResolveContained(root, runManifestRelativePath);
        EnsureNoReparsePoints(root, runManifestPath);
        if (!File.Exists(runManifestPath) ||
            !string.Equals(Path.GetFileName(runManifestPath), "run.json", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("A promoted baseline's run manifest is missing or invalid.", runManifestPath);
        var runDirectory = Path.GetDirectoryName(runManifestPath)
            ?? throw new InvalidDataException("A promoted baseline run manifest has no containing directory.");
        var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        ValidatePromotionRun(baseline.WorkspaceId, baseline.RunId, run);
        if (run.IdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            run.ManifestHash != baseline.RunManifestHash ||
            run.PlanFingerprint != baseline.EffectivePlanFingerprint ||
            run.ConfigurationHash != baseline.ConfigurationHash ||
            run.GraphHash != baseline.GraphHash ||
            !run.DiscoveryFingerprints.Order(StringComparer.Ordinal).SequenceEqual(
                baseline.DiscoveryFingerprints, StringComparer.Ordinal) ||
            run.CheckpointId != baseline.CheckpointId ||
            run.CheckpointManifestHash != baseline.CheckpointManifestHash ||
            run.CheckpointSourceFingerprint != baseline.CheckpointSourceFingerprint ||
            run.CheckpointStoreScope != baseline.CheckpointStoreScope ||
            run.ReplayedCheckpoint != baseline.ReplayedCheckpoint ||
            run.VerificationRunId != baseline.VerificationRunId ||
            run.VerificationOutcome != baseline.VerificationOutcome ||
            run.VerificationEvidenceFingerprint != baseline.VerificationEvidenceFingerprint ||
            run.VerificationLedgerFingerprint != baseline.VerificationLedgerFingerprint ||
            ComputeReconciliationFingerprint(run) != baseline.ReconciliationFingerprint ||
            run.Disposition != baseline.Disposition ||
            run.ProductVersion != baseline.ProductVersion ||
            run.SourceRevision != baseline.SourceRevision ||
            run.SourceTree != baseline.SourceTree)
            throw new InvalidDataException($"Baseline {baseline.BaselineId:D} no longer matches its authoritative run.");

        var packagePath = ResolveContained(root, baseline.EvidencePackageRelativePath);
        EnsureNoReparsePoints(root, packagePath);
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("A promoted baseline's evidence package is missing.", packagePath);
        var package = await WorkspaceEvidencePackageService.ValidateAsync(packagePath, cancellationToken)
            .ConfigureAwait(false);
        ValidatePackageBinding(run, package);
        if (package.PackageIdentity != baseline.EvidencePackageIdentity)
            throw new InvalidDataException($"Baseline {baseline.BaselineId:D} evidence package identity has changed.");
    }

    private static async Task WriteNewBaselineAsync(string path, WorkspaceBaselineRecord baseline,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(baseline, JsonOptions);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCatalogAsync(string root, string directory, WorkspaceBaselineCatalog catalog,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, CatalogFileName);
        EnsureNoReparsePoints(root, destination);
        var temporary = Path.Combine(directory, $"catalog-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions), cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string GetBaselinePath(string directory, Guid baselineId) =>
        Path.Combine(directory, $"{baselineId:D}.json");

    private static string ResolveContained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("Baseline reference must be workspace-relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ConnectorPathUtilities.IsContained(root, path))
            throw new InvalidDataException("Baseline reference resolves outside the workspace.");
        return path;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        if (!ConnectorPathUtilities.IsContained(fullRoot, current))
            throw new InvalidDataException("Baseline path resolves outside the workspace.");
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Baseline storage and references cannot traverse reparse points.");
            }
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                return;
            current = Directory.GetParent(current)?.FullName
                ?? throw new InvalidDataException("Baseline path has no workspace parent.");
        }
    }

    private static string ComputeReconciliationFingerprint(WorkspaceRunManifest run)
    {
        var identity = new
        {
            RuleIds = run.ReconciliationRuleIds.Order(StringComparer.Ordinal).ToArray(),
            FailedRuleIds = run.ReconciliationFailedRuleIds.Order(StringComparer.Ordinal).ToArray()
        };
        return Hash(JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions));
    }

    private static string ComputeBaselineHash(WorkspaceBaselineRecord baseline) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(baseline with { RecordHash = string.Empty }, JsonOptions));

    private static string ComputeCatalogHash(WorkspaceBaselineCatalog catalog) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(catalog with { IntegrityHash = string.Empty }, JsonOptions));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
