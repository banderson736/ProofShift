using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

internal sealed record WorkspaceBaselineComparisonRecord(
    string Format,
    Guid BaselineId,
    Guid CandidateRunId,
    string BaselineRunManifestIdentityFingerprint,
    string CandidateRunManifestIdentityFingerprint,
    string BaselineEvidencePackageIdentity,
    string CandidateEvidenceIdentity,
    string PreRunSemanticDriftIdentityFingerprint,
    string OutcomeClassification,
    string DetailedComparisonIdentityFingerprint,
    WorkspaceRunComparison DetailedComparison,
    string IntegrityHash);

internal static class WorkspaceBaselineComparisonService
{
    private const string Format = "proofshift-baseline-outcome-comparison-v1";
    private const string ComparisonDirectory = ".proofshift/comparisons";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<WorkspaceBaselineComparisonRecord?> CompareAndPersistAsync(string workspaceDirectory,
        WorkspaceRunManifest candidate, CancellationToken cancellationToken)
    {
        if (candidate.BaselineId is not { } baselineId ||
            candidate.State != "Completed" || string.IsNullOrWhiteSpace(candidate.ManifestHash))
            return null;
        if (candidate.PreRunSemanticDriftIdentityFingerprint is not { } driftFingerprint)
            throw new InvalidDataException("Baseline-bound run is missing its pre-run drift identity.");

        var baselinePlan = await WorkspaceBaselineService.ReadPlanAsync(workspaceDirectory,
            baselineId.ToString("D"), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The run's bound baseline is no longer available.", baselineId.ToString("D"));
        var baseline = baselinePlan.Baseline;
        if (candidate.BaselineRunId != baseline.RunId ||
            candidate.BaselineRunManifestIdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            candidate.BaselinePlanFingerprint != baseline.EffectivePlanFingerprint ||
            candidate.BaselineEvidencePackageIdentity != baseline.EvidencePackageIdentity)
            throw new InvalidDataException("The candidate run's baseline binding no longer matches the validated baseline.");

        var comparison = await WorkspaceRunComparisonService.CompareBaselineAsync(workspaceDirectory, baselineId,
            candidate.RunId.ToString("D"), cancellationToken).ConfigureAwait(false);
        var classification = Classify(baseline.Disposition, candidate, comparison);
        var comparisonIdentity = Hash(JsonSerializer.SerializeToUtf8Bytes(comparison, JsonOptions));
        var record = new WorkspaceBaselineComparisonRecord(Format, baselineId, candidate.RunId,
            baseline.RunManifestIdentityFingerprint, candidate.IdentityFingerprint,
            baseline.EvidencePackageIdentity,
            candidate.EvidenceManifestIntegrityHash ?? candidate.VerificationEvidenceFingerprint
                ?? throw new InvalidDataException("Candidate run has no authoritative Evidence identity."),
            driftFingerprint, classification, comparisonIdentity, comparison, string.Empty);
        record = record with { IntegrityHash = IntegrityHash(record) };

        var root = Path.GetFullPath(workspaceDirectory);
        var directory = ResolveContained(root, ComparisonDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(root, directory);
        var path = Path.Combine(directory, $"{candidate.RunId:D}.json");
        EnsureNoReparsePoints(root, path);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return record;
    }

    public static async Task<WorkspaceBaselineComparisonRecord?> ReadAsync(string workspaceDirectory,
        Guid candidateRunId, WorkspaceRunManifest run, CancellationToken cancellationToken)
    {
        if (run.BaselineId is null || candidateRunId != run.RunId)
            return null;
        var root = Path.GetFullPath(workspaceDirectory);
        var baselinePlan = await WorkspaceBaselineService.ReadPlanAsync(root, run.BaselineId.Value.ToString("D"),
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The run's bound baseline is no longer available.",
                run.BaselineId.Value.ToString("D"));
        var baseline = baselinePlan.Baseline;
        if (run.BaselineRunId != baseline.RunId ||
            run.BaselineRunManifestIdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            run.BaselinePlanFingerprint != baseline.EffectivePlanFingerprint ||
            run.BaselineEvidencePackageIdentity != baseline.EvidencePackageIdentity)
            throw new InvalidDataException("Run baseline binding no longer matches its retained baseline.");
        var path = ResolveContained(root, Path.Combine(ComparisonDirectory, $"{candidateRunId:D}.json"));
        EnsureNoReparsePoints(root, path);
        if (!File.Exists(path))
            return null;
        var record = JsonSerializer.Deserialize<WorkspaceBaselineComparisonRecord>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Baseline outcome comparison artifact is empty.");
        if (record.Format != Format || record.BaselineId != run.BaselineId ||
            record.CandidateRunId != candidateRunId ||
            record.CandidateRunManifestIdentityFingerprint != run.IdentityFingerprint ||
            record.BaselineRunManifestIdentityFingerprint != run.BaselineRunManifestIdentityFingerprint ||
            record.BaselineRunManifestIdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            record.BaselineEvidencePackageIdentity != baseline.EvidencePackageIdentity ||
            record.DetailedComparison.BeforeRunId != baseline.RunId ||
            record.CandidateEvidenceIdentity != (run.EvidenceManifestIntegrityHash ??
                run.VerificationEvidenceFingerprint) ||
            record.PreRunSemanticDriftIdentityFingerprint != run.PreRunSemanticDriftIdentityFingerprint ||
            record.DetailedComparison.BeforeDisposition != baseline.Disposition ||
            record.DetailedComparison.AfterDisposition != run.Disposition ||
            record.OutcomeClassification is not ("Equivalent" or "ChangedStillQualified" or "Improved" or
                "Regressed" or "StillNotQualified" or "NotComparable") ||
            record.OutcomeClassification != Classify(baseline.Disposition, run, record.DetailedComparison) ||
            record.DetailedComparisonIdentityFingerprint !=
                Hash(JsonSerializer.SerializeToUtf8Bytes(record.DetailedComparison, JsonOptions)) ||
            record.IntegrityHash != IntegrityHash(record))
            throw new InvalidDataException("Baseline outcome comparison identity or integrity is invalid.");
        return record;
    }

    internal static byte[] Serialize(WorkspaceBaselineComparisonRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

    internal static WorkspaceBaselineComparisonRecord ValidatePackagedRecord(ReadOnlySpan<byte> bytes,
        WorkspaceRunManifest run, WorkspaceBaselineRecord baseline)
    {
        var record = JsonSerializer.Deserialize<WorkspaceBaselineComparisonRecord>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Packaged baseline outcome comparison is empty.");
        if (record.Format != Format || record.BaselineId != baseline.BaselineId ||
            record.CandidateRunId != run.RunId ||
            record.BaselineRunManifestIdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            record.CandidateRunManifestIdentityFingerprint != run.IdentityFingerprint ||
            record.BaselineEvidencePackageIdentity != baseline.EvidencePackageIdentity ||
            record.DetailedComparison.BeforeRunId != baseline.RunId ||
            record.DetailedComparison.AfterRunId != run.RunId ||
            record.CandidateEvidenceIdentity != (run.EvidenceManifestIntegrityHash ??
                run.VerificationEvidenceFingerprint) ||
            record.PreRunSemanticDriftIdentityFingerprint != run.PreRunSemanticDriftIdentityFingerprint ||
            record.DetailedComparison.BeforeDisposition != baseline.Disposition ||
            record.DetailedComparison.AfterDisposition != run.Disposition ||
            record.OutcomeClassification is not ("Equivalent" or "ChangedStillQualified" or "Improved" or
                "Regressed" or "StillNotQualified" or "NotComparable") ||
            record.OutcomeClassification != Classify(baseline.Disposition, run, record.DetailedComparison) ||
            record.DetailedComparisonIdentityFingerprint !=
                Hash(JsonSerializer.SerializeToUtf8Bytes(record.DetailedComparison, JsonOptions)) ||
            record.IntegrityHash != IntegrityHash(record))
            throw new InvalidDataException("Packaged baseline comparison does not match the candidate/baseline identities.");
        return record;
    }

    internal static string Classify(string baselineDisposition, WorkspaceRunManifest candidate,
        WorkspaceRunComparison comparison)
    {
        if (candidate.State != "Completed" || candidate.Disposition is null)
            return "NotComparable";
        if (candidate.PreRunSemanticDriftStatus == WorkspaceSemanticDriftStatus.NotComparable.ToString())
            return "NotComparable";
        if (baselineDisposition == "NOT QUALIFIED" && candidate.Disposition == "QUALIFIED")
            return "Improved";
        if (baselineDisposition == "QUALIFIED" && candidate.Disposition == "NOT QUALIFIED")
            return "Regressed";
        if (baselineDisposition == "NOT QUALIFIED" && candidate.Disposition == "NOT QUALIFIED")
            return "StillNotQualified";
        if (baselineDisposition != "QUALIFIED" || candidate.Disposition != "QUALIFIED")
            return "NotComparable";

        var equivalentIgnoringExecutionAncestry =
            !comparison.DispositionChanged &&
            !comparison.ProductVersionChanged &&
            !comparison.ConfigurationChanged &&
            !comparison.GraphChanged &&
            !comparison.PlanChanged &&
            !comparison.DiscoveryChanged &&
            !comparison.SourcePopulationChanged &&
            !comparison.ProjectionChanged &&
            !comparison.VerificationOutcomeChanged &&
            !comparison.Findings.Any(item => item.Category != "Persisting") &&
            !comparison.ExpectedExceptions.Any(item => item.Category != "Persisting") &&
            comparison.ReconciliationFailuresAdded.Count == 0 &&
            comparison.ReconciliationFailuresResolved.Count == 0;
        return equivalentIgnoringExecutionAncestry ? "Equivalent" : "ChangedStillQualified";
    }

    private static string IntegrityHash(WorkspaceBaselineComparisonRecord record) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(record with { IntegrityHash = string.Empty }, JsonOptions));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ProofShift.Connectors.Abstractions.ConnectorPathUtilities.IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Baseline comparison artifact path is outside the workspace.");
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Baseline comparison artifact path is outside the workspace.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Baseline comparison path traverses a reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Baseline comparison path traverses a reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
