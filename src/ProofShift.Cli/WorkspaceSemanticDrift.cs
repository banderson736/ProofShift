using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Connectors.Abstractions;
using ProofShift.Engine;

namespace ProofShift.Cli;

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceSemanticDriftStatus>))]
internal enum WorkspaceSemanticDriftStatus
{
    NoBaseline,
    NoSemanticChange,
    SemanticChangeDetected,
    ReviewRequired,
    NotComparable,
    Blocking
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceSemanticDriftClassification>))]
internal enum WorkspaceSemanticDriftClassification
{
    Informational,
    ReviewRequired,
    NotChecked,
    Blocking
}

internal sealed record WorkspaceSemanticDriftChange(
    string Category,
    string Kind,
    string Subject,
    WorkspaceSemanticDriftClassification Classification,
    string Detail);

internal sealed record WorkspaceSemanticDrift(
    Guid? BaselineId,
    Guid? BaselineRunId,
    string? BaselinePlanFingerprint,
    string? CurrentPlanFingerprint,
    WorkspaceSemanticDriftStatus Status,
    bool ReviewRequired,
    IReadOnlyList<WorkspaceSemanticDriftChange> Changes,
    IReadOnlyList<string> BlockingConditions);

internal static class WorkspaceSemanticDriftService
{
    public static async Task<WorkspaceSemanticDrift> CompareAsync(string workspaceDirectory,
        WorkspacePlanResult planResult, string baselineReference, CancellationToken cancellationToken)
    {
        var current = planResult.Plan;
        if (current is null)
        {
            var change = new WorkspaceSemanticDriftChange("plan", "invalid", "current",
                WorkspaceSemanticDriftClassification.Blocking,
                "The current effective plan could not be generated; baseline comparison is not possible.");
            return Result(null, null, WorkspaceSemanticDriftStatus.Blocking, [change]);
        }
        if (baselineReference == "none")
            return Result(null, current, WorkspaceSemanticDriftStatus.NoBaseline, []);

        var baseline = await WorkspaceBaselineService.ReadPlanAsync(workspaceDirectory, baselineReference,
            cancellationToken).ConfigureAwait(false);
        if (baseline is null)
            return Result(null, current, WorkspaceSemanticDriftStatus.NoBaseline, []);
        if (!baseline.SemanticallyComparable)
        {
            var change = new WorkspaceSemanticDriftChange("baseline-plan", "schema-not-supported",
                "historical-effective-plan", WorkspaceSemanticDriftClassification.NotChecked,
                "The validated historical plan predates the current semantic comparison model; no drift is inferred.");
            return Result(baseline, current, WorkspaceSemanticDriftStatus.NotComparable, [change]);
        }

        var changes = ComparePlans(baseline.Plan, current);
        changes.AddRange(await CompareDiscoveryAsync(workspaceDirectory, baseline.Baseline, cancellationToken)
            .ConfigureAwait(false));
        changes = Sort(changes);
        var blocking = changes.Where(change =>
                change.Classification == WorkspaceSemanticDriftClassification.Blocking)
            .Select(change => $"{change.Category}: {change.Detail}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var reviewRequired = changes.Any(change =>
            change.Classification is WorkspaceSemanticDriftClassification.ReviewRequired or
                WorkspaceSemanticDriftClassification.NotChecked or
                WorkspaceSemanticDriftClassification.Blocking);
        var status = blocking.Length > 0
            ? WorkspaceSemanticDriftStatus.Blocking
            : changes.Any(change => change.Classification == WorkspaceSemanticDriftClassification.NotChecked)
                ? WorkspaceSemanticDriftStatus.NotComparable
                : reviewRequired
                    ? WorkspaceSemanticDriftStatus.ReviewRequired
                    : changes.Any(change => change.Classification == WorkspaceSemanticDriftClassification.Informational)
                        ? WorkspaceSemanticDriftStatus.SemanticChangeDetected
                        : WorkspaceSemanticDriftStatus.NoSemanticChange;
        return new WorkspaceSemanticDrift(baseline.Baseline.BaselineId, baseline.Baseline.RunId,
            baseline.Baseline.EffectivePlanFingerprint, current.Fingerprint, status, reviewRequired,
            changes, blocking);

        static WorkspaceSemanticDrift Result(WorkspaceBaselinePlan? baselinePlan,
            WorkspaceEvaluationPlan? currentPlan, WorkspaceSemanticDriftStatus status,
            IReadOnlyList<WorkspaceSemanticDriftChange> changes)
        {
            var blocking = changes.Where(change =>
                    change.Classification == WorkspaceSemanticDriftClassification.Blocking)
                .Select(change => $"{change.Category}: {change.Detail}")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new WorkspaceSemanticDrift(baselinePlan?.Baseline.BaselineId,
                baselinePlan?.Baseline.RunId, baselinePlan?.Baseline.EffectivePlanFingerprint,
                currentPlan?.Fingerprint, status,
                blocking.Length > 0 || status == WorkspaceSemanticDriftStatus.NotComparable,
                changes, blocking);
        }
    }

    internal static List<WorkspaceSemanticDriftChange> ComparePlans(WorkspaceEvaluationPlan baseline,
        WorkspaceEvaluationPlan current)
    {
        var changes = new List<WorkspaceSemanticDriftChange>();
        if (baseline.WorkspaceId != current.WorkspaceId)
            Add("configuration", "workspace-changed", "workspace", "The plans belong to different workspaces.");
        if (baseline.ConfigurationFingerprint != current.ConfigurationFingerprint)
            Add("configuration", "changed", "project", "Effective configuration identity changed; endpoint values are redacted.");
        if (baseline.ProjectId != current.ProjectId || baseline.ConfigurationVersion != current.ConfigurationVersion)
            Add("configuration", "changed", "project", "Project identity or configuration format changed.");

        CompareSets("pack", baseline.Packs.Select(PackKey), current.Packs.Select(PackKey),
            "Configured domain pack identity changed.", changes);
        CompareSystems(baseline.Systems, current.Systems, changes);
        var graphChangeCount = changes.Count;
        CompareNodes(baseline.Nodes, current.Nodes, changes);
        CompareEdges(baseline.Edges, current.Edges, changes);
        if (baseline.GraphHash != current.GraphHash && changes.Count == graphChangeCount)
            changes.Add(new WorkspaceSemanticDriftChange("graph", "semantics-changed", "graph",
                WorkspaceSemanticDriftClassification.ReviewRequired,
                "Migration graph semantics changed in a detail not separately represented; configured values are redacted."));
        CompareRules(baseline.Rules, current.Rules, changes);
        var expectedExceptionChangeCount = changes.Count;
        CompareExpectedExceptions(baseline.ExpectedExceptions, current.ExpectedExceptions, changes);
        if (baseline.ExpectedExceptionsFingerprint != current.ExpectedExceptionsFingerprint &&
            changes.Count == expectedExceptionChangeCount)
            changes.Add(new WorkspaceSemanticDriftChange("expected-exception", "annotation-metadata-changed",
                "annotations", WorkspaceSemanticDriftClassification.ReviewRequired,
                "Expected-exception owner or reason metadata changed; annotation values are redacted and do not waive findings."));
        CompareReconciliation(baseline, current, changes);

        if (baseline.RequireRecoveryForDestructiveOperations != current.RequireRecoveryForDestructiveOperations ||
            baseline.AllowIrreversibleOperations != current.AllowIrreversibleOperations ||
            baseline.CheckpointOperation != current.CheckpointOperation ||
            baseline.ReplayBehavior != current.ReplayBehavior ||
            baseline.ProjectionStrategy != current.ProjectionStrategy)
            Add("evaluation-options", "changed", "execution-policy",
                "Checkpoint, Projection, replay, or Recovery policy changed.");

        foreach (var check in current.PreflightChecks.Where(check =>
                     check.Status == "BLOCKED" && check.Category == "capability"))
            changes.Add(new WorkspaceSemanticDriftChange("capability", "unsupported",
                check.Component ?? check.Id, WorkspaceSemanticDriftClassification.Blocking,
                "A required connector capability is unavailable in the current evaluation."));
        return changes;

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareSystems(IReadOnlyList<WorkspacePlanSystem> baseline,
        IReadOnlyList<WorkspacePlanSystem> current, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldSystems = baseline.ToDictionary(SystemKey, StringComparer.Ordinal);
        var newSystems = current.ToDictionary(SystemKey, StringComparer.Ordinal);
        foreach (var key in oldSystems.Keys.Union(newSystems.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldSystems.TryGetValue(key, out var oldSystem))
            {
                Add("configuration", "added", key, "System endpoint was added.");
                continue;
            }
            if (!newSystems.TryGetValue(key, out var newSystem))
            {
                Add("configuration", "removed", key, "System endpoint was removed.");
                continue;
            }
            if (oldSystem.Role != newSystem.Role || oldSystem.Connector != newSystem.Connector ||
                oldSystem.ConnectorVersion != newSystem.ConnectorVersion)
                Add("configuration", "changed", key, "Endpoint role, connector, or connector version changed.");
            if (oldSystem.ConfigurationFingerprint != newSystem.ConfigurationFingerprint)
                Add("configuration-binding", "changed", key,
                    "Endpoint configuration binding changed; configuration values are redacted.");
            var removedCapabilities = oldSystem.Capabilities.Except(newSystem.Capabilities, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var addedCapabilities = newSystem.Capabilities.Except(oldSystem.Capabilities, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            foreach (var capability in removedCapabilities)
            {
                var required = newSystem.Role switch
                {
                    "Source" => capability is "sourceRead" or "checkpointCapture" or "discovery",
                    "Target" => capability == "targetObservation",
                    "ShadowTarget" => capability is "targetObservation" or "shadowWrite",
                    _ => false
                };
                changes.Add(new WorkspaceSemanticDriftChange("capability",
                    required ? "required-capability-removed" : "capability-removed", $"{key}/{capability}",
                    required ? WorkspaceSemanticDriftClassification.Blocking :
                        WorkspaceSemanticDriftClassification.ReviewRequired,
                    required
                        ? "A capability required by the configured endpoint is no longer advertised."
                        : "Connector capability descriptor changed."));
            }
            foreach (var capability in addedCapabilities)
                Add("capability", "capability-added", $"{key}/{capability}",
                    "Connector capability descriptor changed.");
        }

        static string SystemKey(WorkspacePlanSystem system) => $"{system.System}/{system.Endpoint}";
        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareNodes(IReadOnlyList<WorkspacePlanNode> baseline,
        IReadOnlyList<WorkspacePlanNode> current, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldNodes = baseline.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var newNodes = current.ToDictionary(node => node.Key, StringComparer.Ordinal);
        foreach (var key in oldNodes.Keys.Union(newNodes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldNodes.TryGetValue(key, out var oldNode))
            {
                Add("scope", "declared-node-added", key, "A declared graph node and its scope were added.");
                continue;
            }
            if (!newNodes.TryGetValue(key, out var newNode))
            {
                Add("scope", "declared-node-removed", key, "A declared graph node and its scope were removed.");
                continue;
            }
            if (oldNode.ScopeId != newNode.ScopeId || oldNode.ScopeType != newNode.ScopeType ||
                oldNode.ScopeFingerprint != newNode.ScopeFingerprint ||
                !oldNode.ScopePredicates.SequenceEqual(newNode.ScopePredicates))
                Add("scope", "changed", key,
                    "Declared evaluation scope changed; future runtime population is not known before execution.");
            if (oldNode.Type != newNode.Type || oldNode.System != newNode.System ||
                oldNode.Endpoint != newNode.Endpoint || oldNode.Connector != newNode.Connector ||
                oldNode.SemanticType != newNode.SemanticType || oldNode.SelectorKind != newNode.SelectorKind ||
                oldNode.SelectorFingerprint != newNode.SelectorFingerprint ||
                !oldNode.IdentityFields.SequenceEqual(newNode.IdentityFields))
                Add("graph", "node-changed", key, "Graph node, artifact class, selector, or identity changed.");
        }

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareEdges(IReadOnlyList<WorkspacePlanEdge> baseline,
        IReadOnlyList<WorkspacePlanEdge> current, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldEdges = baseline.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        var newEdges = current.ToDictionary(edge => edge.Key, StringComparer.Ordinal);
        foreach (var key in oldEdges.Keys.Union(newEdges.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldEdges.TryGetValue(key, out var oldEdge))
            {
                Add("mapping", "added", key, "Mapping edge was added.");
                continue;
            }
            if (!newEdges.TryGetValue(key, out var newEdge))
            {
                Add("mapping", "removed", key, "Mapping edge was removed.");
                continue;
            }
            if (oldEdge.SemanticFingerprint == newEdge.SemanticFingerprint)
                continue;

            var changeCount = changes.Count;
            var oldFields = oldEdge.Fields.ToDictionary(field => field.Target, StringComparer.Ordinal);
            var newFields = newEdge.Fields.ToDictionary(field => field.Target, StringComparer.Ordinal);
            foreach (var target in oldFields.Keys.Union(newFields.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (!oldFields.TryGetValue(target, out var oldField))
                    Add("mapping", "field-added", $"{key}/{target}", "Mapped target field was added.");
                else if (!newFields.TryGetValue(target, out var newField))
                    Add("mapping", "field-removed", $"{key}/{target}", "Mapped target field was removed.");
                else if (oldField.SemanticFingerprint != newField.SemanticFingerprint)
                    Add("mapping", "field-changed", $"{key}/{target}",
                        $"Mapping changed ({oldField.Source ?? "(constant)"} -> {target}); transformation parameters are redacted.");
            }
            if (!oldEdge.Sources.SequenceEqual(newEdge.Sources) ||
                !oldEdge.Targets.SequenceEqual(newEdge.Targets))
                Add("mapping", "fan-out-changed", key, "Mapping edge source/target fan-out changed.");
            if (oldEdge.Operation != newEdge.Operation || oldEdge.RecoveryMode != newEdge.RecoveryMode)
                Add("mapping", "operation-changed", key,
                    "Mapping operation or Recovery mode changed; operation parameters are redacted.");
            if (changes.Count == changeCount)
                Add("mapping", "semantics-changed", key,
                    "Mapping operation or Recovery semantics changed; details are redacted.");
        }

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareRules(IReadOnlyList<WorkspacePlanRule> baseline,
        IReadOnlyList<WorkspacePlanRule> current, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldRules = baseline.Where(rule => !rule.Type.Contains("reconciliation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var newRules = current.Where(rule => !rule.Type.Contains("reconciliation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        foreach (var id in oldRules.Keys.Union(newRules.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldRules.TryGetValue(id, out var oldRule))
                Add("verification-rule", "added", id, "Verification rule was added.");
            else if (!newRules.TryGetValue(id, out var newRule))
                Add("verification-rule", "removed", id, "Verification rule was removed.");
            else if (oldRule.SemanticFingerprint != newRule.SemanticFingerprint)
                Add("verification-rule", "changed", id,
                    $"Verification rule identity or semantics changed (type {newRule.Type}); configured values are redacted.");
        }

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareExpectedExceptions(IReadOnlyList<WorkspacePlanExpectedException> baseline,
        IReadOnlyList<WorkspacePlanExpectedException> current, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldExceptions = baseline.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var newExceptions = current.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var id in oldExceptions.Keys.Union(newExceptions.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldExceptions.TryGetValue(id, out var oldException))
                Add("expected-exception", "added", id, "Expected-exception annotation was added; it does not waive findings.");
            else if (!newExceptions.TryGetValue(id, out var newException))
                Add("expected-exception", "removed", id, "Expected-exception annotation was removed.");
            else if (oldException != newException)
                Add("expected-exception", "changed", id,
                    "Expected-exception identity or matching semantics changed; this does not waive findings.");
        }

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareReconciliation(WorkspaceEvaluationPlan baseline, WorkspaceEvaluationPlan current,
        List<WorkspaceSemanticDriftChange> changes)
    {
        var oldRules = baseline.Rules.Where(rule => baseline.ReconciliationRules.Contains(rule.Id, StringComparer.Ordinal))
            .ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        var newRules = current.Rules.Where(rule => current.ReconciliationRules.Contains(rule.Id, StringComparer.Ordinal))
            .ToDictionary(rule => rule.Id, StringComparer.Ordinal);
        foreach (var id in oldRules.Keys.Union(newRules.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!oldRules.TryGetValue(id, out var oldRule))
                Add("reconciliation", "added", id, "Reconciliation definition was added.");
            else if (!newRules.TryGetValue(id, out var newRule))
                Add("reconciliation", "removed", id, "Reconciliation definition was removed.");
            else if (oldRule.SemanticFingerprint != newRule.SemanticFingerprint)
                Add("reconciliation", "changed", id,
                    "Reconciliation definition changed; no observed run values are part of this comparison.");
        }

        void Add(string category, string kind, string subject, string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange(category, kind, subject,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static void CompareSets(string category, IEnumerable<string> before, IEnumerable<string> after,
        string detail, List<WorkspaceSemanticDriftChange> changes)
    {
        var oldValues = before.ToHashSet(StringComparer.Ordinal);
        var newValues = after.ToHashSet(StringComparer.Ordinal);
        foreach (var value in oldValues.Except(newValues, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            changes.Add(new WorkspaceSemanticDriftChange(category, "removed", value,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
        foreach (var value in newValues.Except(oldValues, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            changes.Add(new WorkspaceSemanticDriftChange(category, "added", value,
                WorkspaceSemanticDriftClassification.ReviewRequired, detail));
    }

    private static async Task<List<WorkspaceSemanticDriftChange>> CompareDiscoveryAsync(string workspaceDirectory,
        WorkspaceBaselineRecord baseline, CancellationToken cancellationToken)
    {
        var changes = new List<WorkspaceSemanticDriftChange>();
        if (baseline.DiscoveryFingerprints.Count == 0)
            return changes;
        try
        {
            var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(workspaceDirectory)
                .ConfigureAwait(false) ?? throw new InvalidDataException("Workspace manifest is unavailable.");
            var discoveryRoot = Path.GetFullPath(Path.Combine(workspaceDirectory,
                workspace.Paths.Discovery.Replace('/', Path.DirectorySeparatorChar)));
            var artifacts = new List<PhysicalDiscoveryArtifact>();
            foreach (var directory in Directory.EnumerateDirectories(discoveryRoot, "*",
                         SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(directory, "manifest.json")))
                    continue;
                artifacts.Add(await PhysicalDiscoveryStore.ReadAsync(directory, cancellationToken)
                    .ConfigureAwait(false));
            }
            var availableFingerprints = artifacts.Select(artifact => artifact.Fingerprint)
                .ToHashSet(StringComparer.Ordinal);
            if (baseline.DiscoveryFingerprints.Any(fingerprint => !availableFingerprints.Contains(fingerprint)))
            {
                NotChecked("One or more historical discovery artifacts are unavailable; schema equivalence is not assumed.");
                return changes;
            }
            var baselineArtifacts = artifacts.Where(artifact =>
                    baseline.DiscoveryFingerprints.Contains(artifact.Fingerprint, StringComparer.Ordinal) &&
                    artifact.ObservedAt <= baseline.PromotedAt)
                .GroupBy(EndpointKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(artifact => artifact.ObservedAt).First())
                .ToDictionary(EndpointKey, StringComparer.Ordinal);
            var currentArtifacts = artifacts.GroupBy(EndpointKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(artifact => artifact.ObservedAt).First())
                .ToDictionary(EndpointKey, StringComparer.Ordinal);
            if (baselineArtifacts.Count == 0)
            {
                NotChecked("Historical discovery artifacts are not available for a trustworthy schema comparison.");
                return changes;
            }

            foreach (var (endpoint, oldArtifact) in baselineArtifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!currentArtifacts.TryGetValue(endpoint, out var newArtifact))
                {
                    NotChecked($"Current persisted discovery is missing for {endpoint}; no live discovery was generated.");
                    continue;
                }
                if (newArtifact.ObservedAt <= baseline.PromotedAt)
                {
                    NotChecked($"Current persisted discovery for {endpoint} is not newer than its baseline; no live discovery was generated.");
                    continue;
                }
                var drift = PhysicalDiscoveryDiff.Compare(oldArtifact, newArtifact);
                foreach (var item in drift)
                    changes.Add(new WorkspaceSemanticDriftChange("discovery", item.Kind,
                        $"{endpoint}/{item.ObjectName}{(item.Field is null ? string.Empty : "." + item.Field)}",
                        WorkspaceSemanticDriftClassification.ReviewRequired,
                        "Persisted physical discovery differs; current discovery is not regenerated by planning."));
            }
            foreach (var endpoint in currentArtifacts.Keys.Except(baselineArtifacts.Keys, StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
                NotChecked($"Current persisted discovery for {endpoint} has no historical baseline artifact.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        {
            NotChecked($"Persisted discovery comparison is unavailable ({exception.GetType().Name}); no live discovery was generated.");
        }
        return changes;

        void NotChecked(string detail) =>
            changes.Add(new WorkspaceSemanticDriftChange("discovery", "not-checked", "persisted-discovery",
                WorkspaceSemanticDriftClassification.NotChecked, detail));

        static string EndpointKey(PhysicalDiscoveryArtifact artifact) =>
            $"{artifact.SystemId}/{artifact.EndpointId}";
    }

    private static string PackKey(WorkspacePlanPack pack) => $"{pack.Id}@{pack.Version}";

    private static List<WorkspaceSemanticDriftChange> Sort(IEnumerable<WorkspaceSemanticDriftChange> changes) =>
        changes.OrderBy(change => change.Category, StringComparer.Ordinal)
            .ThenBy(change => change.Subject, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Classification)
            .ToList();
}
