namespace ProofShift.Cli;

internal sealed record WorkspaceFindingDelta(
    string Category,
    string RuleId,
    string? BeforeCode,
    string? AfterCode,
    string TargetIdentity,
    int Count);

internal sealed record WorkspaceExpectedExceptionDelta(string Category, string AnnotationIdentity, int Count);

internal sealed record WorkspaceRunComparison(
    string Format,
    Guid BeforeRunId,
    Guid AfterRunId,
    string? BeforeDisposition,
    string? AfterDisposition,
    bool DispositionChanged,
    bool ProductVersionChanged,
    bool ExecutionModeChanged,
    bool ConfigurationChanged,
    bool GraphChanged,
    bool PlanChanged,
    bool DiscoveryChanged,
    bool CheckpointIdentityChanged,
    bool SourcePopulationChanged,
    bool ProjectionChanged,
    bool VerificationOutcomeChanged,
    IReadOnlyList<string> DiscoveryAdded,
    IReadOnlyList<string> DiscoveryRemoved,
    IReadOnlyList<WorkspaceFindingDelta> Findings,
    IReadOnlyList<string> ReconciliationFailuresAdded,
    IReadOnlyList<string> ReconciliationFailuresResolved,
    IReadOnlyList<WorkspaceExpectedExceptionDelta> ExpectedExceptions,
    bool MaterializedStateFindingAdded,
    bool MaterializedStateFindingResolved,
    bool SameDispositionImpliesEquivalent,
    bool SemanticallyEquivalent);

internal static class WorkspaceRunComparisonService
{
    private const string FormatVersion = "proofshift-workspace-run-comparison-v1";

    public static async Task<WorkspaceRunComparison> CompareAsync(string workspaceDirectory,
        string beforeReference, string afterReference, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before comparing runs.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        var before = await LoadAsync(root, workspace, beforeReference, cancellationToken).ConfigureAwait(false);
        var after = await LoadAsync(root, workspace, afterReference, cancellationToken).ConfigureAwait(false);
        if (before.Run.WorkspaceId != after.Run.WorkspaceId)
            throw new InvalidOperationException("Workspace runs must belong to the same workspace.");

        return Compare(before, after);
    }

    public static async Task<WorkspaceRunComparison> CompareBaselineAsync(string workspaceDirectory,
        Guid baselineId, string candidateRunReference, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before comparing runs.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        var baselinePlan = await WorkspaceBaselineService.ReadPlanAsync(root, baselineId.ToString("D"),
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The selected baseline does not exist.", baselineId.ToString("D"));
        var baseline = baselinePlan.Baseline;
        var manifestRelativePath = baseline.RunManifestRelativePath ??
            Path.Combine(workspace.Paths.Runs, baseline.RunId.ToString("D"), "run.json")
                .Replace(Path.DirectorySeparatorChar, '/');
        var manifestPath = ResolveContained(root, manifestRelativePath);
        EnsureNoReparsePoints(root, manifestPath);
        var baselineRunDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Baseline run directory is unavailable.");
        var beforeRun = await WorkspaceRunManifestStore.ReadAsync(baselineRunDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (beforeRun.WorkspaceId != workspace.WorkspaceId || beforeRun.RunId != baseline.RunId ||
            beforeRun.IdentityFingerprint != baseline.RunManifestIdentityFingerprint ||
            beforeRun.ManifestHash != baseline.RunManifestHash ||
            beforeRun.Disposition != baseline.Disposition)
            throw new InvalidDataException("Baseline run no longer matches its retained identity.");
        var beforeEvidence = await WorkspaceEvaluationSummaryService.ReadEvidenceAsync(baselineRunDirectory,
            beforeRun, cancellationToken).ConfigureAwait(false);
        var after = await LoadAsync(root, workspace, candidateRunReference, cancellationToken).ConfigureAwait(false);
        return Compare(new RunEvidenceSnapshot(beforeRun, beforeEvidence), after);
    }

    internal static WorkspaceRunComparison ComparePackagedRuns(WorkspaceRunManifest beforeRun,
        WorkspaceEvaluationEvidence beforeEvidence, WorkspaceRunManifest afterRun,
        WorkspaceEvaluationEvidence afterEvidence) =>
        Compare(new RunEvidenceSnapshot(beforeRun, beforeEvidence), new RunEvidenceSnapshot(afterRun, afterEvidence));

    private static WorkspaceRunComparison Compare(RunEvidenceSnapshot before, RunEvidenceSnapshot after)
    {
        if (before.Run.WorkspaceId != after.Run.WorkspaceId)
            throw new InvalidOperationException("Workspace runs must belong to the same workspace.");
        var findingDeltas = CompareFindings(before.Evidence.Findings, after.Evidence.Findings);
        var expectedDeltas = CompareExpectedExceptions(before.Evidence.ExpectedExceptions, after.Evidence.ExpectedExceptions);
        var addedDiscovery = after.Run.DiscoveryFingerprints.Except(before.Run.DiscoveryFingerprints, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var removedDiscovery = before.Run.DiscoveryFingerprints.Except(after.Run.DiscoveryFingerprints, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var reconciliationAdded = after.Run.ReconciliationFailedRuleIds.Except(
            before.Run.ReconciliationFailedRuleIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var reconciliationResolved = before.Run.ReconciliationFailedRuleIds.Except(
            after.Run.ReconciliationFailedRuleIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var materializedBefore = HasMaterializedStateFinding(before.Evidence.Findings);
        var materializedAfter = HasMaterializedStateFinding(after.Evidence.Findings);

        var dispositionChanged = before.Run.Disposition != after.Run.Disposition;
        var productVersionChanged = before.Run.ProductVersion != after.Run.ProductVersion;
        var executionModeChanged = before.Run.ReplayedCheckpoint != after.Run.ReplayedCheckpoint;
        var configurationChanged = before.Run.ConfigurationHash != after.Run.ConfigurationHash;
        var graphChanged = before.Run.GraphHash != after.Run.GraphHash;
        var planChanged = before.Run.PlanFingerprint != after.Run.PlanFingerprint;
        var discoveryChanged = addedDiscovery.Length > 0 || removedDiscovery.Length > 0;
        var checkpointIdentityChanged = before.Run.CheckpointId != after.Run.CheckpointId ||
            before.Run.CheckpointManifestHash != after.Run.CheckpointManifestHash;
        var sourceChanged = before.Run.CheckpointSourceFingerprint != after.Run.CheckpointSourceFingerprint;
        var projectionChanged = before.Run.ProjectionFingerprint != after.Run.ProjectionFingerprint;
        var verificationChanged = before.Run.VerificationOutcome != after.Run.VerificationOutcome;
        var findingsChanged = findingDeltas.Any(item => item.Category != "Persisting");
        var expectedChanged = expectedDeltas.Any(item => item.Category != "Persisting");
        var reconciliationChanged = reconciliationAdded.Length > 0 || reconciliationResolved.Length > 0;
        var semanticallyEquivalent = !dispositionChanged && !productVersionChanged && !executionModeChanged &&
            !configurationChanged && !graphChanged && !planChanged && !discoveryChanged &&
            !sourceChanged && !projectionChanged && !verificationChanged && !findingsChanged &&
            !expectedChanged && !reconciliationChanged;

        return new WorkspaceRunComparison(FormatVersion, before.Run.RunId, after.Run.RunId,
            before.Run.Disposition, after.Run.Disposition, dispositionChanged, productVersionChanged,
            executionModeChanged, configurationChanged, graphChanged, planChanged, discoveryChanged,
            checkpointIdentityChanged, sourceChanged, projectionChanged, verificationChanged,
            addedDiscovery, removedDiscovery, findingDeltas, reconciliationAdded, reconciliationResolved,
            expectedDeltas, !materializedBefore && materializedAfter, materializedBefore && !materializedAfter,
            SameDispositionImpliesEquivalent: false, semanticallyEquivalent);
    }

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Baseline run manifest is outside the workspace.");
        return fullPath;
    }

    private static async Task<RunEvidenceSnapshot> LoadAsync(string root, EvaluationWorkspaceManifest workspace,
        string runReference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runReference, out var runId) || runId == Guid.Empty)
            throw new ArgumentException("Run reference must be a non-empty run GUID.", nameof(runReference));
        var runsRoot = Path.GetFullPath(Path.Combine(root,
            workspace.Paths.Runs.Replace('/', Path.DirectorySeparatorChar)));
        var runDirectory = Path.GetFullPath(Path.Combine(runsRoot, runId.ToString("D")));
        if (!IsContained(runsRoot, runDirectory))
            throw new InvalidDataException("Selected run is outside the workspace run directory.");
        EnsureNoReparsePoints(root, runDirectory);
        if (!Directory.Exists(runDirectory))
            throw new FileNotFoundException("Selected workspace run does not exist.", runDirectory);
        var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        if (run.RunId != runId || run.WorkspaceId != workspace.WorkspaceId)
            throw new InvalidDataException("Run manifest identity does not match the selected workspace.");
        var evidence = await WorkspaceEvaluationSummaryService.ReadEvidenceAsync(runDirectory, run, cancellationToken)
            .ConfigureAwait(false);
        return new RunEvidenceSnapshot(run, evidence);
    }

    private static WorkspaceFindingDelta[] CompareFindings(
        IReadOnlyList<WorkspaceEvaluationFinding> before, IReadOnlyList<WorkspaceEvaluationFinding> after)
    {
        var remainingBefore = before.GroupBy(ExactFindingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var remainingAfter = after.GroupBy(ExactFindingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var deltas = new List<WorkspaceFindingDelta>();

        foreach (var key in remainingBefore.Keys.Intersect(remainingAfter.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var beforeItems = remainingBefore[key];
            var afterItems = remainingAfter[key];
            var count = Math.Min(beforeItems.Count, afterItems.Count);
            if (count > 0)
            {
                var sample = beforeItems[0];
                deltas.Add(new("Persisting", sample.RuleId, sample.Code, sample.Code,
                    sample.TargetIdentity, count));
                beforeItems.RemoveRange(0, count);
                afterItems.RemoveRange(0, count);
            }
        }

        var beforeRemaining = remainingBefore.Values.SelectMany(items => items).ToList();
        var afterRemaining = remainingAfter.Values.SelectMany(items => items).ToList();
        var beforeGroups = beforeRemaining.GroupBy(FindingComparisonGroup, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.ValueFingerprint, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var afterGroups = afterRemaining.GroupBy(FindingComparisonGroup, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.ValueFingerprint, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        foreach (var key in beforeGroups.Keys.Intersect(afterGroups.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var oldItems = beforeGroups[key];
            var newItems = afterGroups[key];
            while (oldItems.Count > 0 && newItems.Count > 0)
            {
                var oldItem = oldItems[0];
                var newItem = newItems[0];
                deltas.Add(new("Changed", oldItem.RuleId, oldItem.Code, newItem.Code,
                    oldItem.TargetIdentity, 1));
                oldItems.RemoveAt(0);
                newItems.RemoveAt(0);
            }
        }

        beforeRemaining = beforeGroups.Values.SelectMany(items => items).ToList();
        afterRemaining = afterGroups.Values.SelectMany(items => items).ToList();
        foreach (var group in beforeRemaining.GroupBy(ExactFindingKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var sample = group.First();
            deltas.Add(new("Resolved", sample.RuleId, sample.Code, null, sample.TargetIdentity, group.Count()));
        }
        foreach (var group in afterRemaining.GroupBy(ExactFindingKey, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var sample = group.First();
            deltas.Add(new("Added", sample.RuleId, null, sample.Code, sample.TargetIdentity, group.Count()));
        }

        return deltas.OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.RuleId, StringComparer.Ordinal)
            .ThenBy(item => item.BeforeCode, StringComparer.Ordinal)
            .ThenBy(item => item.AfterCode, StringComparer.Ordinal)
            .ThenBy(item => item.TargetIdentity, StringComparer.Ordinal).ToArray();
    }

    private static WorkspaceExpectedExceptionDelta[] CompareExpectedExceptions(
        IReadOnlyList<WorkspaceExpectedException> before, IReadOnlyList<WorkspaceExpectedException> after)
    {
        var left = before.GroupBy(ExpectedExceptionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var right = after.GroupBy(ExpectedExceptionKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var result = new List<WorkspaceExpectedExceptionDelta>();
        foreach (var key in left.Keys.Intersect(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldItems = left[key];
            var newItems = right[key];
            var common = Math.Min(oldItems.Count, newItems.Count);
            var identity = oldItems[0].AnnotationIdentity;
            if (common > 0) result.Add(new("Persisting", identity, common));
            oldItems.RemoveRange(0, common);
            newItems.RemoveRange(0, common);
        }

        var remainingBefore = left.Values.SelectMany(items => items).ToList();
        var remainingAfter = right.Values.SelectMany(items => items).ToList();
        var beforeByIdentity = remainingBefore.GroupBy(item => item.AnnotationIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var afterByIdentity = remainingAfter.GroupBy(item => item.AnnotationIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var identity in beforeByIdentity.Keys.Intersect(afterByIdentity.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var oldItems = beforeByIdentity[identity];
            var newItems = afterByIdentity[identity];
            var changed = Math.Min(oldItems.Count, newItems.Count);
            if (changed > 0) result.Add(new("Changed", identity, changed));
            oldItems.RemoveRange(0, changed);
            newItems.RemoveRange(0, changed);
        }
        foreach (var group in beforeByIdentity.Values.SelectMany(items => items)
                     .GroupBy(item => item.AnnotationIdentity, StringComparer.Ordinal))
            result.Add(new("Removed", group.Key, group.Count()));
        foreach (var group in afterByIdentity.Values.SelectMany(items => items)
                     .GroupBy(item => item.AnnotationIdentity, StringComparer.Ordinal))
            result.Add(new("Added", group.Key, group.Count()));

        return result.OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.AnnotationIdentity, StringComparer.Ordinal).ToArray();
    }

    private static string ExactFindingKey(WorkspaceEvaluationFinding finding) =>
        string.Join("|", finding.RuleId, finding.Code ?? string.Empty, finding.Severity,
            finding.TargetIdentity, finding.ValueFingerprint);

    private static string FindingComparisonGroup(WorkspaceEvaluationFinding finding) =>
        string.Join("|", finding.RuleId, finding.TargetIdentity);

    private static string ExpectedExceptionKey(WorkspaceExpectedException annotation) =>
        string.Join("|", annotation.AnnotationIdentity, annotation.Result, annotation.RuleId ?? string.Empty,
            annotation.Code ?? string.Empty, annotation.TargetIdentity, annotation.AnnotationFingerprint);

    private static bool HasMaterializedStateFinding(IEnumerable<WorkspaceEvaluationFinding> findings) =>
        findings.Any(item => item.RuleId.Contains("materialized-state", StringComparison.OrdinalIgnoreCase) ||
            item.Code?.Contains("MaterializedTargetChanged", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison);
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Run path is outside the workspace.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Workspace path traverses a symbolic link or reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace path traverses a symbolic link or reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record RunEvidenceSnapshot(WorkspaceRunManifest Run, WorkspaceEvaluationEvidence Evidence);
}
