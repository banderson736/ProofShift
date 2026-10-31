namespace ProofShift.Cli;

internal sealed record WorkspacePackageComparisonResult(
    string ComparisonMode,
    string BaselinePackageIdentity,
    string CandidatePackageIdentity,
    Guid BaselineRunId,
    Guid CandidateRunId,
    string? BaselineDisposition,
    string? CandidateDisposition,
    string? SemanticDriftStatus,
    string? ReviewDecision,
    Guid? ReviewRecordId,
    string? OutcomeClassification,
    bool? BaselineGovernanceBindingValid,
    bool ReplayAncestryChanged,
    IReadOnlyList<WorkspaceSemanticDriftChange>? SemanticDriftChanges,
    WorkspaceRunComparison DetailedComparison,
    bool MaterializedStateFindingAdded,
    bool MaterializedStateFindingResolved);

internal static class WorkspacePackageComparisonService
{
    public static async Task<WorkspacePackageComparisonResult> CompareAsync(string baselinePackagePath,
        string candidatePackagePath, CancellationToken cancellationToken)
    {
        var baseline = await WorkspaceEvidencePackageService.ReadValidatedSnapshotAsync(baselinePackagePath,
            cancellationToken).ConfigureAwait(false);
        var candidate = await WorkspaceEvidencePackageService.ReadValidatedSnapshotAsync(candidatePackagePath,
            cancellationToken).ConfigureAwait(false);
        var comparison = WorkspaceRunComparisonService.ComparePackagedRuns(baseline.Run, baseline.Evidence,
            candidate.Run, candidate.Evidence);

        if (candidate.Baseline is null)
            return Create("AdHoc", baseline, candidate, comparison, null);

        var declared = candidate.Baseline;
        if (declared.RunId != baseline.Run.RunId ||
            declared.EvidencePackageIdentity != baseline.Manifest.PackageIdentity ||
            declared.RunManifestIdentityFingerprint != baseline.Run.IdentityFingerprint ||
            declared.RunManifestHash != baseline.Run.ManifestHash ||
            candidate.Run.BaselineId != declared.BaselineId ||
            candidate.Run.BaselineRunId != baseline.Run.RunId ||
            candidate.Run.BaselineRunManifestIdentityFingerprint != baseline.Run.IdentityFingerprint ||
            candidate.Run.BaselineEvidencePackageIdentity != baseline.Manifest.PackageIdentity)
            throw new InvalidDataException("Supplied baseline package does not match the candidate's declared baseline identity.");

        var storedComparison = candidate.BaselineComparison
            ?? throw new InvalidDataException("Baseline-governed candidate package has no persisted post-run comparison.");
        if (!JsonEquals(storedComparison.DetailedComparison, comparison))
            throw new InvalidDataException("Packaged post-run comparison does not match the supplied baseline/candidate Evidence pair.");
        var outcome = candidate.Run.PreRunSemanticDriftStatus == WorkspaceSemanticDriftStatus.NotComparable.ToString()
            ? "NotComparable"
            : storedComparison.OutcomeClassification;
        return Create("BaselineGoverned", baseline, candidate, comparison, outcome);
    }

    private static WorkspacePackageComparisonResult Create(string mode,
        WorkspaceEvidencePackageSnapshot baseline, WorkspaceEvidencePackageSnapshot candidate,
        WorkspaceRunComparison comparison, string? outcome) =>
        new(mode, baseline.Manifest.PackageIdentity, candidate.Manifest.PackageIdentity, baseline.Run.RunId,
            candidate.Run.RunId, baseline.Run.Disposition, candidate.Run.Disposition,
            candidate.SemanticDrift?.Drift.Status.ToString() ?? candidate.Run.PreRunSemanticDriftStatus,
            candidate.Review?.Decision ?? candidate.Run.ReviewDecision, candidate.Review?.ReviewId,
            outcome, mode == "BaselineGoverned" ? true : null, comparison.ExecutionModeChanged,
            candidate.SemanticDrift?.Drift.Changes, comparison,
            comparison.MaterializedStateFindingAdded, comparison.MaterializedStateFindingResolved);

    private static bool JsonEquals<T>(T left, T right)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(left, options)),
            System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(right, options)));
    }
}
