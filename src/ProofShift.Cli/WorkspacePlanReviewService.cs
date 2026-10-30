using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

internal sealed record WorkspacePlanReviewRecord(
    string Format,
    Guid ReviewId,
    Guid WorkspaceId,
    Guid BaselineId,
    string BaselinePlanFingerprint,
    string CurrentPlanFingerprint,
    string SemanticDriftIdentityFingerprint,
    WorkspaceSemanticDriftStatus DriftStatus,
    string Decision,
    DateTimeOffset ReviewedAt,
    string IntegrityHash);

internal sealed record WorkspacePlanReviewResult(
    WorkspacePlanReviewRecord Review,
    WorkspaceSemanticDrift Drift);

internal static class WorkspacePlanReviewService
{
    private const string Format = "proofshift-workspace-plan-review-v1";
    private const string ReviewDirectory = ".proofshift/reviews";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<WorkspacePlanReviewResult> ReviewAsync(string workspaceDirectory,
        string baselineReference, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var plan = await WorkspaceEvaluationService.PlanAsync(root, cancellationToken).ConfigureAwait(false);
        var drift = await WorkspaceSemanticDriftService.CompareAsync(root, plan, baselineReference,
            cancellationToken).ConfigureAwait(false);
        if (drift.Status is not (WorkspaceSemanticDriftStatus.ReviewRequired or
            WorkspaceSemanticDriftStatus.NotComparable))
            throw new InvalidOperationException("The selected plan does not require semantic review or unavailable-comparison acknowledgement.");
        if (drift.Status == WorkspaceSemanticDriftStatus.Blocking || drift.BaselineId is null ||
            drift.BaselinePlanFingerprint is null || drift.CurrentPlanFingerprint is null)
            throw new InvalidDataException("A blocking or unbound plan comparison cannot be reviewed.");

        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before plan review.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        var reviewId = Guid.NewGuid();
        var decision = drift.Status == WorkspaceSemanticDriftStatus.NotComparable
            ? "ComparisonUnavailableAcknowledged"
            : "Reviewed";
        var record = new WorkspacePlanReviewRecord(Format, reviewId, workspace.WorkspaceId,
            drift.BaselineId.Value, drift.BaselinePlanFingerprint, drift.CurrentPlanFingerprint,
            IdentityFingerprint(drift), drift.Status, decision, DateTimeOffset.UtcNow, string.Empty);
        record = record with { IntegrityHash = IntegrityHash(record) };

        var directory = ResolveContained(root, ReviewDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(root, directory);
        var path = Path.Combine(directory, $"{reviewId:D}.json");
        EnsureNoReparsePoints(root, path);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new(record, drift);
    }

    public static async Task<WorkspacePlanReviewRecord> ValidateForExecutionAsync(string workspaceDirectory,
        string reviewReference, WorkspaceSemanticDrift drift, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reviewReference, out var reviewId) || reviewId == Guid.Empty)
            throw new ArgumentException("Review reference must be a non-empty review GUID.", nameof(reviewReference));
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before review validation.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        var path = ResolveContained(root, Path.Combine(ReviewDirectory, $"{reviewId:D}.json"));
        EnsureNoReparsePoints(root, path);
        var record = JsonSerializer.Deserialize<WorkspacePlanReviewRecord>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Plan review record is empty.");
        if (record.Format != Format || record.ReviewId != reviewId || record.WorkspaceId != workspace.WorkspaceId ||
            record.BaselineId == Guid.Empty || !IsSha256(record.BaselinePlanFingerprint) ||
            !IsSha256(record.CurrentPlanFingerprint) || !IsSha256(record.SemanticDriftIdentityFingerprint) ||
            record.Decision is not ("Reviewed" or "ComparisonUnavailableAcknowledged") ||
            !string.Equals(record.IntegrityHash, IntegrityHash(record), StringComparison.Ordinal))
            throw new InvalidDataException("Plan review record identity or integrity is invalid.");

        var expectedDecision = drift.Status switch
        {
            WorkspaceSemanticDriftStatus.ReviewRequired => "Reviewed",
            WorkspaceSemanticDriftStatus.NotComparable => "ComparisonUnavailableAcknowledged",
            _ => null
        };
        if (expectedDecision is null || record.Decision != expectedDecision ||
            record.BaselineId != drift.BaselineId ||
            record.BaselinePlanFingerprint != drift.BaselinePlanFingerprint ||
            record.CurrentPlanFingerprint != drift.CurrentPlanFingerprint ||
            record.DriftStatus != drift.Status ||
            record.SemanticDriftIdentityFingerprint != IdentityFingerprint(drift))
            throw new InvalidOperationException("Plan review does not authorize the selected baseline and exact current semantic comparison.");
        return record;
    }

    internal static async Task<WorkspacePlanReviewRecord> ReadForPackagingAsync(string workspaceDirectory,
        WorkspaceRunManifest run, CancellationToken cancellationToken)
    {
        if (run.ReviewRecordId is not { } reviewId || run.BaselineId is not { } baselineId ||
            run.PreRunSemanticDriftIdentityFingerprint is not { } driftFingerprint)
            throw new InvalidDataException("Run does not contain a complete review binding.");
        var root = Path.GetFullPath(workspaceDirectory);
        var path = ResolveContained(root, Path.Combine(ReviewDirectory, $"{reviewId:D}.json"));
        EnsureNoReparsePoints(root, path);
        var record = JsonSerializer.Deserialize<WorkspacePlanReviewRecord>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Plan review record is empty.");
        ValidateRecord(record);
        if (record.WorkspaceId != run.WorkspaceId || record.BaselineId != baselineId ||
            record.CurrentPlanFingerprint != run.PlanFingerprint ||
            record.SemanticDriftIdentityFingerprint != driftFingerprint ||
            record.Decision != run.ReviewDecision)
            throw new InvalidDataException("Plan review does not match the run's exact governance binding.");
        return record;
    }

    internal static byte[] SerializeRecord(WorkspacePlanReviewRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

    internal static WorkspacePlanReviewRecord ValidatePackagedReviewRecord(ReadOnlySpan<byte> bytes,
        WorkspaceRunManifest run)
    {
        var record = JsonSerializer.Deserialize<WorkspacePlanReviewRecord>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Packaged plan review record is empty.");
        ValidateRecord(record);
        if (run.ReviewRecordId != record.ReviewId || run.WorkspaceId != record.WorkspaceId ||
            run.BaselineId != record.BaselineId || run.BaselinePlanFingerprint != record.BaselinePlanFingerprint ||
            run.PlanFingerprint != record.CurrentPlanFingerprint ||
            run.PreRunSemanticDriftIdentityFingerprint != record.SemanticDriftIdentityFingerprint ||
            run.ReviewDecision != record.Decision ||
            run.PreRunSemanticDriftStatus != record.DriftStatus.ToString())
            throw new InvalidDataException("Packaged plan review does not match the candidate run.");
        return record;
    }

    public static string IdentityFingerprint(WorkspaceSemanticDrift drift)
    {
        var canonical = new
        {
            drift.BaselineId,
            drift.BaselineRunId,
            drift.BaselinePlanFingerprint,
            drift.CurrentPlanFingerprint,
            status = drift.Status.ToString(),
            drift.ReviewRequired,
            changes = drift.Changes,
            drift.BlockingConditions
        };
        return Hash(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions));
    }

    private static string IntegrityHash(WorkspacePlanReviewRecord record) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(record with { IntegrityHash = string.Empty }, JsonOptions));

    private static void ValidateRecord(WorkspacePlanReviewRecord record)
    {
        if (record.Format != Format || record.ReviewId == Guid.Empty || record.WorkspaceId == Guid.Empty ||
            record.BaselineId == Guid.Empty || !IsSha256(record.BaselinePlanFingerprint) ||
            !IsSha256(record.CurrentPlanFingerprint) || !IsSha256(record.SemanticDriftIdentityFingerprint) ||
            record.Decision is not ("Reviewed" or "ComparisonUnavailableAcknowledged") ||
            !string.Equals(record.IntegrityHash, IntegrityHash(record), StringComparison.Ordinal))
            throw new InvalidDataException("Plan review record identity or integrity is invalid.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ProofShift.Connectors.Abstractions.ConnectorPathUtilities.IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Review artifact path is outside the workspace.");
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Review artifact path is outside the workspace.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Review artifact path traverses a reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Review artifact path traverses a reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
