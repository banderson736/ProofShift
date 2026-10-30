using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal sealed record WorkspacePreflightCheck(
    string Id,
    string Category,
    string Status,
    string Message,
    string? Component,
    string? Remediation,
    string? Capability = null);

internal sealed record WorkspacePlanScopePredicate(string Field, string Operator);

internal sealed record WorkspacePreflightResult(
    Guid? WorkspaceId,
    bool Completed,
    string Status,
    bool ConnectivityAttempted,
    IReadOnlyList<WorkspacePreflightCheck> Checks);

internal sealed record WorkspacePlanNode(
    string Key,
    string Type,
    string System,
    string Endpoint,
    string Connector,
    string SemanticType,
    string SelectorKind,
    string SelectorFingerprint,
    IReadOnlyList<string> IdentityFields,
    string? ScopeId,
    string? ScopeType,
    string? ScopeFingerprint,
    IReadOnlyList<WorkspacePlanScopePredicate> ScopePredicates);

internal sealed record WorkspacePlanField(
    string? Source,
    string Target,
    IReadOnlyList<string> Transformations,
    string SemanticFingerprint);

internal sealed record WorkspacePlanEdge(
    string Key,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Targets,
    string Operation,
    IReadOnlyList<WorkspacePlanField> Fields,
    string? RecoveryMode,
    string SemanticFingerprint);

internal sealed record WorkspacePlanSystem(
    string System,
    string Endpoint,
    string Role,
    string Connector,
    string ConnectorVersion,
    IReadOnlyList<string> Capabilities,
    string ConfigurationFingerprint);

internal sealed record WorkspacePlanPack(string Id, string Version);

internal sealed record WorkspacePlanRule(string Id, string Type, string Version, string Severity,
    string SemanticFingerprint);

internal sealed record WorkspacePlanExpectedException(
    string Id,
    string RuleId,
    string FindingCode,
    string TargetNode,
    string ArtifactIdSha256,
    string EvaluationScopeId);

internal sealed record WorkspaceEvaluationPlan(
    Guid WorkspaceId,
    string ConfigurationFingerprint,
    string ProjectId,
    string ProjectName,
    string ConfigurationFile,
    string ConfigurationVersion,
    string VerificationRuleSetFingerprint,
    string ExpectedExceptionsFingerprint,
    string GraphHash,
    string GraphCanonicalizationVersion,
    string Readiness,
    string CheckpointOperation,
    string CheckpointOutput,
    string ReplayBehavior,
    string ProjectionStrategy,
    bool RequireRecoveryForDestructiveOperations,
    bool AllowIrreversibleOperations,
    string EvidenceOutput,
    string RunOutput,
    string CleanupImplications,
    string ExpectedExceptionSemantics,
    IReadOnlyList<string> ReconciliationRules,
    IReadOnlyList<string> IntentionallyOmittedCapabilities,
    IReadOnlyList<WorkspacePlanPack> Packs,
    IReadOnlyList<WorkspacePlanSystem> Systems,
    IReadOnlyList<WorkspacePlanNode> Nodes,
    IReadOnlyList<WorkspacePlanEdge> Edges,
    IReadOnlyList<WorkspacePlanRule> Rules,
    IReadOnlyList<WorkspacePlanExpectedException> ExpectedExceptions,
    IReadOnlyList<WorkspacePreflightCheck> PreflightChecks,
    string Fingerprint)
{
    public const string CurrentSemanticModelVersion = "proofshift-effective-plan-semantic-v2";
    public string? SemanticModelVersion { get; init; }
}

internal sealed record WorkspacePlanResult(
    bool Completed,
    string Status,
    WorkspacePreflightResult Preflight,
    WorkspaceEvaluationPlan? Plan)
{
    public WorkspaceSemanticDrift? SemanticDrift { get; init; }
}

internal static class WorkspaceEvaluationService
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<WorkspacePreflightResult> PreflightAsync(
        string workspaceDirectory,
        bool connect,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(workspaceDirectory, connect, cancellationToken).ConfigureAwait(false);
        return analysis.Preflight;
    }

    public static async Task<WorkspacePlanResult> PlanAsync(
        string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(workspaceDirectory, connect: false, cancellationToken).ConfigureAwait(false);
        return CreatePlan(analysis);
    }

    internal static WorkspacePlanResult CreatePlan(WorkspaceAnalysis analysis)
    {
        if (analysis.Manifest is null || analysis.Configuration is null || analysis.Graph is null ||
            analysis.RuleConfiguration is null || analysis.RuleSet is null)
            return new(analysis.Preflight.Completed, "BLOCKED", analysis.Preflight, null);

        var configuration = analysis.Configuration;
        var graph = analysis.Graph;
        var endpointByNode = graph.Nodes.ToDictionary(
            node => node.Name,
            node => configuration.Systems.Single(system => system.Id == node.SystemId)
                .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId),
            StringComparer.Ordinal);
        var nodeById = graph.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var systems = graph.Nodes
            .Select(node =>
            {
                var system = configuration.Systems.Single(item => item.Id == node.SystemId);
                var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
                var capability = analysis.Connectors!.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value);
                return new WorkspacePlanSystem(
                    system.Id.Value,
                    endpoint.Id.Value,
                    system.Role.ToString(),
                    endpoint.Connector.Value,
                    capability?.Version ?? "unknown",
                    CapabilityNames(capability),
                    FingerprintSettings(endpoint.Configuration));
            })
            .Distinct()
            .OrderBy(item => item.System, StringComparer.Ordinal)
            .ThenBy(item => item.Endpoint, StringComparer.Ordinal)
            .ToArray();
        var nodes = graph.Nodes.OrderBy(node => node.Name, StringComparer.Ordinal)
            .Select(node => new WorkspacePlanNode(
                node.Name,
                node.Type.ToString(),
                node.SystemId.Value,
                node.EndpointId.Value,
                endpointByNode[node.Name].Connector.Value,
                node.SemanticType,
                node.Selector.Kind,
                FingerprintSettings(node.Selector.Properties),
                node.Selector.IdentityFields.Order(StringComparer.Ordinal).ToArray(),
                node.Selector.EvaluationScope?.Id,
                node.Selector.EvaluationScope?.Type.ToString(),
                node.Selector.EvaluationScope?.Fingerprint,
                node.Selector.EvaluationScope?.Predicates
                    .OrderBy(predicate => predicate.Field, StringComparer.Ordinal)
                    .ThenBy(predicate => EvaluationPredicate.OperatorName(predicate.Operator), StringComparer.Ordinal)
                    .Select(predicate => new WorkspacePlanScopePredicate(
                        predicate.Field, EvaluationPredicate.OperatorName(predicate.Operator))).ToArray() ?? []))
            .ToArray();
        var edges = graph.Edges.OrderBy(edge => edge.Name, StringComparer.Ordinal)
            .Select(edge => new WorkspacePlanEdge(
                edge.Name,
                edge.Sources.Select(id => nodeById[id]).Order(StringComparer.Ordinal).ToArray(),
                edge.Targets.Select(id => nodeById[id]).Order(StringComparer.Ordinal).ToArray(),
                edge.Operation.Type.ToString(),
                edge.Operation.Fields.OrderBy(field => field.Target, StringComparer.Ordinal)
                    .Select(field => new WorkspacePlanField(
                        field.Source,
                        field.Target,
                        field.Pipeline.Select(step => step.Type.ToString()).ToArray(),
                        FingerprintField(field)))
                    .ToArray(),
                edge.Recovery?.Mode.ToString(),
                FingerprintEdge(edge)))
            .ToArray();
        var rules = analysis.RuleConfiguration.OrderBy(rule => rule.Id.Value, StringComparer.Ordinal)
            .Select(rule => new WorkspacePlanRule(rule.Id.Value, rule.Type, rule.Version, rule.Severity.ToString(),
                FingerprintRule(analysis.Configuration, rule.Id.Value)))
            .ToArray();
        var packs = configuration.Root.Packs
            .Select(pack => new WorkspacePlanPack(pack.Id ?? "unspecified", pack.Version ?? "unspecified"))
            .OrderBy(pack => pack.Id, StringComparer.Ordinal)
            .ThenBy(pack => pack.Version, StringComparer.Ordinal)
            .ToArray();
        var expectedExceptionsFingerprint = ExpectedExceptionsFingerprint(analysis.RuleSet.ExpectedExceptions);
        var expectedExceptions = analysis.RuleSet.ExpectedExceptions
            .Select(annotation => new WorkspacePlanExpectedException(annotation.Id, annotation.RuleId.Value,
                annotation.FindingCode, annotation.TargetNode, annotation.ArtifactIdSha256,
                annotation.EvaluationScopeId))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var reconciliationRules = analysis.RuleConfiguration.Where(rule =>
            rule.Type.Contains("reconciliation", StringComparison.OrdinalIgnoreCase)).ToArray();
        var omitted = new[]
        {
            "No checkpoint ID or projection fingerprint is assigned before execution.",
            "Run manifests, evidence packages, run comparison, and guided cleanup are not produced by preflight or plan."
        };
        var readiness = analysis.Preflight.Status;
        const string checkpointOperation = "Capture a complete source checkpoint for configured source nodes before projection or Verification.";
        const string checkpointOutput = ".proofshift/checkpoints/ beneath the project configuration directory (the existing checkpoint store default).";
        const string replayBehavior = "Post-capture projection and Verification consume the complete checkpoint; they do not fall back to live source reads.";
        var projectionStrategy = systems.Any(system => system.Role == SystemRole.ShadowTarget.ToString())
            ? "Project only to explicitly configured isolated shadow-target endpoints."
            : "No shadow-target projection is configured; target evaluation remains read-only.";
        var expectedExceptionSemantics = $"{analysis.RuleSet.ExpectedExceptions.Count} expected-exception annotations are listed in configuration; annotations never waive findings.";
        var fingerprintInput = new PlanFingerprintInput(
            analysis.Manifest.WorkspaceId,
            WorkspaceEvaluationPlan.CurrentSemanticModelVersion,
            configuration.ConfigurationHash,
            configuration.Root.Project?.Id ?? string.Empty,
            configuration.Root.Project?.Name ?? string.Empty,
            configuration.Root.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            packs,
            analysis.RuleSet.Fingerprint,
            expectedExceptionsFingerprint,
            graph.GraphHash,
            graph.GraphCanonicalizationVersion ?? string.Empty,
            systems,
            nodes,
            edges,
            rules,
            expectedExceptions,
            analysis.RuleConfiguration.ExpectedExceptions.Count,
            analysis.RecoveryPolicy!.RequireRecoveryForDestructiveOperations,
            analysis.RecoveryPolicy.AllowIrreversibleOperations,
            checkpointOperation,
            checkpointOutput,
            replayBehavior,
            projectionStrategy,
            reconciliationRules.Select(rule => rule.Id.Value).Order(StringComparer.Ordinal).ToArray(),
            omitted);
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fingerprintInput, PlanJsonOptions)))
            .ToLowerInvariant();
        var plan = new WorkspaceEvaluationPlan(
            analysis.Manifest.WorkspaceId,
            configuration.ConfigurationHash,
            configuration.Root.Project?.Id ?? string.Empty,
            configuration.Root.Project?.Name ?? string.Empty,
            analysis.Manifest.ProjectConfiguration,
            configuration.Root.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            analysis.RuleSet.Fingerprint,
            expectedExceptionsFingerprint,
            graph.GraphHash,
            graph.GraphCanonicalizationVersion ?? string.Empty,
            readiness,
            checkpointOperation,
            checkpointOutput,
            replayBehavior,
            projectionStrategy,
            analysis.RecoveryPolicy.RequireRecoveryForDestructiveOperations,
            analysis.RecoveryPolicy.AllowIrreversibleOperations,
            analysis.Manifest.Paths.Evidence,
            analysis.Manifest.Paths.Runs,
            "No cleanup is performed. Retain evidence explicitly; cleanup remains separate, explicit, and run-scoped.",
            expectedExceptionSemantics,
            reconciliationRules.Select(rule => rule.Id.Value).Order(StringComparer.Ordinal).ToArray(),
            omitted,
            packs,
            systems,
            nodes,
            edges,
            rules,
            expectedExceptions,
            analysis.Preflight.Checks,
            fingerprint)
        {
            SemanticModelVersion = WorkspaceEvaluationPlan.CurrentSemanticModelVersion
        };
        return new(true, analysis.Preflight.Status, analysis.Preflight, plan);
    }

    internal static async Task<WorkspaceAnalysis> AnalyzeAsync(
        string workspaceDirectory,
        bool connect,
        CancellationToken cancellationToken,
        bool replayCheckpoint = false)
    {
        var checks = new List<WorkspacePreflightCheck>();
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(workspaceDirectory).ConfigureAwait(false);
        if (!validation.Valid)
        {
            if (validation.Issues.Count == 0)
                checks.Add(Blocked("PSPREF001", "workspace", "Workspace structure is invalid.", null,
                    "Run workspace validate and correct its reported issues."));
            else
                checks.AddRange(validation.Issues.Select(issue =>
                    Blocked(issue.Code, "workspace", issue.Message, null, "Run workspace validate and correct its reported issues.")));
            return WorkspaceAnalysis.Failed(validation.WorkspaceId, connect, checks);
        }

        checks.Add(Ready("PSPREF001", "workspace", "Workspace manifest, referenced files, paths, and static configuration are valid.", null));
        EvaluationWorkspaceManifest? manifest;
        try
        {
            manifest = await EvaluationWorkspaceManifestStore.ReadAsync(workspaceDirectory).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            checks.Add(Blocked("PSPREF002", "workspace", $"Workspace manifest could not be read ({exception.GetType().Name}).", null,
                "Run workspace validate and restore the manifest."));
            return WorkspaceAnalysis.Failed(validation.WorkspaceId, connect, checks);
        }

        if (manifest is null)
            return WorkspaceAnalysis.Failed(validation.WorkspaceId, connect,
                [Blocked("PSPREF002", "workspace", "Workspace manifest is empty.", null, "Restore a valid workspace manifest.")]);

        var root = Path.GetFullPath(workspaceDirectory);
        var configurationPath = Path.GetFullPath(Path.Combine(root,
            manifest.ProjectConfiguration.Replace('/', Path.DirectorySeparatorChar)));
        ConfigurationLoadResult loaded;
        try
        {
            loaded = await new ConfigurationLoader(requireEnvironmentValues: false)
                .LoadAsync(configurationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            checks.Add(Blocked("PSPREF003", "configuration", $"Project configuration could not be loaded ({exception.GetType().Name}).", null,
                "Run workspace validate and correct the local configuration."));
            return WorkspaceAnalysis.Failed(manifest.WorkspaceId, connect, checks);
        }

        if (!loaded.IsValid || loaded.Configuration is null)
        {
            checks.Add(Blocked("PSPREF003", "configuration", "Project configuration is invalid.", null,
                "Run workspace validate and correct the local configuration."));
            return WorkspaceAnalysis.Failed(manifest.WorkspaceId, connect, checks);
        }

        var configuration = loaded.Configuration;
        var compilation = MigrationGraphCompiler.Compile(configuration);
        if (!compilation.IsValid || compilation.Graph is null)
        {
            checks.Add(Blocked("PSPREF004", "graph", "Migration graph compilation failed.", null,
                "Correct the graph using the reported workspace validation diagnostics."));
            return WorkspaceAnalysis.Failed(manifest.WorkspaceId, connect, checks);
        }

        checks.Add(Ready("PSPREF003", "configuration", "Effective project configuration and migration graph compiled successfully.",
            configuration.Root.Project?.Id));

        var unapproved = StrictAuthoringValidation.UnapprovedMappings(configuration).ToArray();
        if (unapproved.Length > 0)
            checks.Add(Blocked("PSAUTHOR001", "mappings", $"{unapproved.Length} required mapping(s) are not explicitly approved.",
                configuration.MigrationGraphConfiguration?.RelativePath,
                "Review generated mappings and set approval to confirmed only after human review."));
        else
            checks.Add(Ready("PSPREF004", "mappings", "No unapproved required mappings were found.", null));

        var connectors = CliComposition.Connectors();
        var ruleConfiguration = VerificationRuleConfigurationLoader.Load(configuration);
        var ruleRegistry = CliComposition.RulesFor(configuration, ruleConfiguration);
        var ruleSet = ruleRegistry.Resolve(ruleConfiguration);
        var ruleIssues = RuleReferenceValidator.Validate(ruleConfiguration, ruleRegistry, compilation.Graph);
        if (ruleIssues.Any(issue => issue.Severity == ValidationSeverity.Error))
            checks.Add(Blocked("PSPREF005", "rules", "Verification rules contain invalid types, options, or graph references.",
                configuration.Root.VerificationRulesFile, "Correct the verification rule definitions and references."));
        else
            checks.Add(Ready("PSPREF005", "rules", "Verification rule types, options, and graph references are valid.",
                configuration.Root.VerificationRulesFile));
        var reconciliationRules = ruleConfiguration.Where(rule =>
            rule.Type.Contains("reconciliation", StringComparison.OrdinalIgnoreCase)).ToArray();
        checks.Add(reconciliationRules.Length > 0
            ? Ready("PSPREF014", "reconciliation",
                $"{reconciliationRules.Length} configured reconciliation rule(s) resolve against installed providers and graph references.",
                configuration.Root.VerificationRulesFile)
            : NotChecked("PSPREF014", "reconciliation", "No reconciliation rule is configured.", null,
                "Add a supported reconciliation rule if the evaluation requires one."));

        ValidateExpectedExceptions(ruleConfiguration, compilation.Graph, checks);
        var recoveryPolicy = RecoveryPolicyConfigurationLoader.Load(configuration);
        checks.Add(Ready("PSPREF007", "recovery", "Recovery policy loaded; destructive-operation recovery requirements remain configured.",
            configuration.Root.RecoveryPolicyFile));

        ValidateCapabilities(configuration, compilation.Graph, connectors, checks, replayCheckpoint);
        ValidateCredentials(configuration, compilation.Graph, connectors, checks, replayCheckpoint);
        await ValidateDiscoveryAsync(root, configurationPath, manifest, configuration, compilation.Graph, connectors, connect,
            checks, replayCheckpoint, cancellationToken)
            .ConfigureAwait(false);
        ValidateOutputPaths(manifest, checks);
        if (connect)
            await CheckConnectivityAsync(configuration, compilation.Graph, connectors, configurationPath, checks,
                replayCheckpoint, cancellationToken)
                .ConfigureAwait(false);
        else
            checks.Add(NotChecked("PSPREF010", "connectivity",
                "Live endpoint connectivity and credential validity were not checked. Rerun with --connect to perform read-only connector inspection.",
                null, "Use --connect only when network or local endpoint access is intended."));

        var status = checks.Any(check => check.Status == "BLOCKED") ? "BLOCKED" : "READY";
        var result = new WorkspacePreflightResult(manifest.WorkspaceId, true, status, connect, SortChecks(checks));
        return new WorkspaceAnalysis(manifest, configuration, compilation.Graph, ruleConfiguration, ruleSet, recoveryPolicy,
            connectors, result, root, configurationPath);
    }

    private static void ValidateCapabilities(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ConnectorCatalog connectors,
        List<WorkspacePreflightCheck> checks,
        bool replayCheckpoint)
    {
        foreach (var node in graph.Nodes.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            var descriptor = connectors.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value);
            if (descriptor is null)
            {
                checks.Add(Blocked("PSPREF008", "capability", "Configured connector is not installed.", node.Name,
                    "Install a product build that includes the configured connector."));
                continue;
            }

            if (node.Type == MigrationNodeType.Source && replayCheckpoint)
            {
                checks.Add(Ready("PSPREF008", "capability",
                    "The complete checkpoint supplies source artifacts; live source-read and capture capabilities are not required.",
                    node.Name));
                continue;
            }

            var required = node.Type switch
            {
                MigrationNodeType.Source => new[] { (descriptor.Discovery, "discovery"), (descriptor.SourceRead, "sourceRead"),
                    (descriptor.CheckpointCapture, "checkpointCapture") },
                MigrationNodeType.Target or MigrationNodeType.Archive when system.Role == SystemRole.ShadowTarget =>
                    new[] { (descriptor.TargetObservation, "targetObservation"), (descriptor.ShadowWrite, "shadowWrite") },
                MigrationNodeType.Target or MigrationNodeType.Archive =>
                    new[] { (descriptor.TargetObservation, "targetObservation") },
                _ => []
            };
            foreach (var capability in required)
                checks.Add(new WorkspacePreflightCheck(
                    "PSPREF008",
                    "capability",
                    capability.Item1 ? "READY" : "BLOCKED",
                    capability.Item1
                        ? $"Connector '{descriptor.Id}' supports this required operation."
                        : $"Connector '{descriptor.Id}' does not support this required operation.",
                    node.Name,
                    capability.Item1 ? null : "Choose a supported connector or revise the declared evaluation workflow.",
                    capability.Item2));
        }
    }

    private static void ValidateCredentials(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ConnectorCatalog connectors,
        List<WorkspacePreflightCheck> checks,
        bool replayCheckpoint)
    {
        var seen = new HashSet<(string System, string Endpoint)>();
        foreach (var node in graph.Nodes.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            if (!seen.Add((system.Id.Value, node.EndpointId.Value))) continue;
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            var descriptor = connectors.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value);
            var schema = descriptor is null
                ? null
                : connectors.ConfigurationSchemas.SingleOrDefault(item => item.ConnectorId == descriptor.Id);
            var endpointSettings = endpoint.Configuration.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var missingRequiredProperties = schema?.RequiredEndpointProperties
                .Where(property => !endpointSettings.ContainsKey(property))
                .Order(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (missingRequiredProperties.Length > 0)
            {
                checks.Add(Blocked("PSPREF006", "credentials",
                    "Required connector configuration is missing; no endpoint values were included in the report.",
                    $"{system.Id.Value}/{endpoint.Id.Value}",
                    "Provide the required endpoint configuration and rerun preflight."));
                continue;
            }

            if (replayCheckpoint && node.Type == MigrationNodeType.Source)
            {
                checks.Add(Ready("PSPREF006", "credentials",
                    "Source credentials are not required when execution replays a complete checkpoint.",
                    $"{system.Id.Value}/{endpoint.Id.Value}"));
                continue;
            }

            var missingReferences = new List<string>();
            foreach (var setting in endpoint.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var reference = setting.Value.StartsWith("secret:", StringComparison.Ordinal)
                    ? setting.Value["secret:".Length..]
                    : setting.Value.StartsWith("env:", StringComparison.Ordinal)
                        ? setting.Value["env:".Length..]
                        : null;
                if (reference is null) continue;
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(reference)))
                    missingReferences.Add(reference);
            }
            if (missingReferences.Count > 0)
                checks.Add(Blocked("PSPREF006", "credentials",
                    $"{missingReferences.Count} configured credential or environment reference(s) are unavailable; values and reference names were not included in the report.",
                    $"{system.Id.Value}/{endpoint.Id.Value}",
                    "Provide the referenced values in the process environment or secret provider, then rerun preflight."));
            else
                checks.Add(NotChecked("PSPREF006", "credentials",
                    "No declared environment or secret reference is missing; provider credential requirements and validity are not fully verified without endpoint inspection.",
                    $"{system.Id.Value}/{endpoint.Id.Value}",
                    "Use --connect for explicit read-only authentication and endpoint checks."));
        }
    }

    private static void ValidateExpectedExceptions(
        VerificationRuleConfiguration rules,
        MigrationGraph graph,
        List<WorkspacePreflightCheck> checks)
    {
        var issues = new List<string>();
        var ruleIds = rules.Select(rule => rule.Id).ToHashSet();
        var nodes = graph.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        foreach (var annotation in rules.ExpectedExceptions)
        {
            if (!ruleIds.Contains(annotation.RuleId))
                issues.Add("an annotation references an unknown rule");
            if (!nodes.TryGetValue(annotation.TargetNode, out var target) || target.Type != MigrationNodeType.Target)
                issues.Add("an annotation references an unknown target node");
        }

        if (issues.Count > 0)
            checks.Add(Blocked("PSPREF009", "expected-exceptions",
                $"Expected-exception validation failed: {issues.Distinct(StringComparer.Ordinal).Count()} reference error(s).",
                null, "Correct rule and target-node references. Expected exceptions never waive findings."));
        else if (rules.ExpectedExceptions.Count > 0)
            checks.Add(Warning("PSPREF009", "expected-exceptions",
                $"{rules.ExpectedExceptions.Count} expected-exception annotation(s) are valid, auditable, and do not waive findings.",
                null, "Review each annotation independently; failed findings remain failures."));
        else
            checks.Add(Ready("PSPREF009", "expected-exceptions",
                $"{rules.ExpectedExceptions.Count} expected-exception annotation(s) are structurally valid and do not waive findings.",
                null));
    }

    private static async Task ValidateDiscoveryAsync(
        string root,
        string configurationPath,
        EvaluationWorkspaceManifest manifest,
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ConnectorCatalog connectors,
        bool connect,
        List<WorkspacePreflightCheck> checks,
        bool replayCheckpoint,
        CancellationToken cancellationToken)
    {
        var discoveryRoot = Path.GetFullPath(Path.Combine(root, manifest.Paths.Discovery.Replace('/', Path.DirectorySeparatorChar)));
        string[] artifactDirectories;
        try
        {
            artifactDirectories = Directory.EnumerateDirectories(discoveryRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            checks.Add(Blocked("PSPREF011", "discovery",
                $"Workspace discovery artifacts could not be enumerated ({exception.GetType().Name}).",
                manifest.Paths.Discovery, "Restore access to the workspace discovery directory and rerun preflight."));
            return;
        }
        var configuredEndpoints = configuration.Systems.SelectMany(system => system.StorageEndpoints
            .Select(endpoint => (System: system, Endpoint: endpoint))).ToArray();
        if (artifactDirectories.Length == 0)
        {
            checks.Add(NotChecked("PSPREF011", "discovery",
                "No persisted discovery artifacts are present; preflight did not generate any.",
                manifest.Paths.Discovery, "Run read-only discovery explicitly, then place the artifact under this workspace path."));
            return;
        }

        var artifacts = new List<(string Directory, PhysicalDiscoveryArtifact Artifact, string EndpointKey)>();
        foreach (var directory in artifactDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var artifact = await PhysicalDiscoveryStore.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                var endpoint = configuredEndpoints.SingleOrDefault(item =>
                    item.System.Id.Value == artifact.SystemId && item.Endpoint.Id.Value == artifact.EndpointId);
                if (endpoint.System is null || endpoint.Endpoint is null || endpoint.Endpoint.Connector.Value != artifact.ConnectorId)
                {
                    checks.Add(Blocked("PSPREF011", "discovery",
                        "A persisted discovery artifact does not match a configured system, endpoint, or connector.",
                        Path.GetRelativePath(root, directory),
                        "Regenerate discovery explicitly for the current configuration."));
                    continue;
                }

                artifacts.Add((directory, artifact, $"{artifact.SystemId}/{artifact.EndpointId}"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or InvalidDataException or ArgumentException or
                System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or
                InvalidOperationException)
            {
                checks.Add(Blocked("PSPREF011", "discovery",
                    $"A persisted discovery artifact could not be validated or compared ({exception.GetType().Name}); no connector configuration values were included.",
                    Path.GetRelativePath(root, directory), connect
                        ? "Check endpoint access and validate or deliberately regenerate the persisted artifact."
                        : "Restore a valid discovery artifact or remove it deliberately."));
            }
        }

        var latestDirectories = artifacts
            .GroupBy(item => item.EndpointKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(item => item.Artifact.ObservedAt)
                    .ThenBy(item => item.Directory, StringComparer.Ordinal).First().Directory,
                StringComparer.Ordinal);
        foreach (var item in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, artifact, endpointKey) = item;
            if (!connect)
            {
                checks.Add(Ready("PSPREF011", "discovery",
                    $"Persisted discovery integrity and endpoint binding are valid ({artifact.Fingerprint}).",
                    Path.GetRelativePath(root, directory)));
                continue;
            }

            try
            {
                var nodes = graph.Nodes.Where(node => node.SystemId.Value == artifact.SystemId &&
                    node.EndpointId.Value == artifact.EndpointId).OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
                var descriptor = connectors.Capabilities.SingleOrDefault(capability => capability.Id == artifact.ConnectorId);
                if (nodes.Length == 0 || descriptor is null || !descriptor.Discovery)
                {
                    checks.Add(NotChecked("PSPREF011", "discovery",
                        "Current discovery cannot be compared because no configured graph node or discovery capability applies.",
                        Path.GetRelativePath(root, directory), null));
                    continue;
                }

                if (!string.Equals(latestDirectories[endpointKey], directory, StringComparison.Ordinal))
                {
                    checks.Add(Ready("PSPREF011", "discovery",
                        "Historical persisted discovery integrity and endpoint binding are valid; live comparison uses the latest artifact.",
                        Path.GetRelativePath(root, directory)));
                    continue;
                }

                if (replayCheckpoint && nodes.All(node => node.Type == MigrationNodeType.Source))
                {
                    checks.Add(Ready("PSPREF011", "discovery",
                        "Persisted discovery integrity is valid; live source discovery was skipped for checkpoint replay.",
                        Path.GetRelativePath(root, directory)));
                    continue;
                }

                var connector = connectors.Reader(artifact.ConnectorId) as IPhysicalDiscoveryConnector;
                if (connector is null)
                {
                    checks.Add(Blocked("PSPREF011", "discovery", "Connector capability metadata and implementation disagree.",
                        Path.GetRelativePath(root, directory), "Contact the product maintainer."));
                    continue;
                }
                var context = new RuntimeConnectorContextFactory(
                    baseDirectory: Path.GetDirectoryName(configurationPath)!).Create(configuration, nodes[0]);
                var current = await connector.DiscoverAsync(context, nodes.Select(node => node.Selector).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                var drift = PhysicalDiscoveryDiff.Compare(artifact, current);
                checks.Add(drift.Count == 0
                    ? Ready("PSPREF011", "discovery", "Persisted discovery matches a fresh read-only physical discovery.", Path.GetRelativePath(root, directory))
                    : Blocked("PSPREF011", "discovery", $"Persisted discovery has {drift.Count} physical drift difference(s).",
                        Path.GetRelativePath(root, directory), "Review drift and explicitly regenerate discovery if appropriate."));
            }
            catch (ConnectorConfigurationException exception)
            {
                checks.Add(Blocked("PSPREF011", "discovery", ConnectivityFailureMessage(exception.Code, SourceInspectionStatus.Failed),
                    Path.GetRelativePath(root, directory), "Correct connector configuration or provide the required credential references."));
            }
            catch (ConnectorReadException exception)
            {
                checks.Add(Blocked("PSPREF011", "discovery", ConnectivityFailureMessage(exception.Code, SourceInspectionStatus.Failed),
                    Path.GetRelativePath(root, directory), "Check endpoint availability, read permissions, and selector configuration."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or InvalidDataException or ArgumentException or
                System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or
                InvalidOperationException)
            {
                checks.Add(Blocked("PSPREF011", "discovery",
                    $"A persisted discovery artifact could not be validated or compared ({exception.GetType().Name}); no connector configuration values were included.",
                    Path.GetRelativePath(root, directory), connect
                        ? "Check endpoint access and validate or deliberately regenerate the persisted artifact."
                        : "Restore a valid discovery artifact or remove it deliberately."));
            }
        }
    }

    private static async Task CheckConnectivityAsync(
        LoadedProjectConfiguration configuration,
        MigrationGraph graph,
        ConnectorCatalog connectors,
        string configurationPath,
        List<WorkspacePreflightCheck> checks,
        bool replayCheckpoint,
        CancellationToken cancellationToken)
    {
        var contextFactory = new RuntimeConnectorContextFactory(
            baseDirectory: Path.GetDirectoryName(configurationPath)!);
        foreach (var node in graph.Nodes.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Type == MigrationNodeType.Source && replayCheckpoint)
            {
                checks.Add(Ready("PSPREF012", "connectivity",
                    "Live source inspection was skipped because execution is bound to a complete checkpoint.", node.Name));
                continue;
            }
            var system = configuration.Systems.Single(item => item.Id == node.SystemId);
            var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
            if (connectors.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value) is not { } descriptor)
                continue;
            try
            {
                var connector = connectors.Reader(endpoint.Connector.Value);
                var context = contextFactory.Create(configuration, node);
                var inspection = await connector.InspectAsync(context, node.Selector, cancellationToken).ConfigureAwait(false);
                var issueCode = inspection.Issues.FirstOrDefault(issue => issue.Severity == ConnectorIssueSeverity.Error)?.Code;
                var check = inspection.Status == SourceInspectionStatus.Valid
                    ? Ready("PSPREF012", "connectivity", $"Read-only inspection succeeded with connector '{descriptor.Id} {descriptor.Version}'.", node.Name)
                    : Blocked("PSPREF012", "connectivity", ConnectivityFailureMessage(issueCode, inspection.Status),
                        node.Name, "Check endpoint availability, read permissions, credentials, and selector configuration.");
                checks.Add(check);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ConnectorConfigurationException exception)
            {
                checks.Add(Blocked("PSPREF012", "connectivity", ConnectivityFailureMessage(exception.Code, SourceInspectionStatus.Failed),
                    node.Name, "Provide valid endpoint configuration and required credential references."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                System.Data.Common.DbException or System.Net.Sockets.SocketException or TimeoutException or
                ConnectorReadException or ArgumentException or InvalidOperationException)
            {
                checks.Add(Blocked("PSPREF012", "connectivity",
                    $"Read-only inspection failed ({exception.GetType().Name}); no connector configuration values were included.",
                    node.Name, "Check endpoint availability, read permissions, credentials, and selector configuration."));
            }
        }
    }

    private static string ConnectivityFailureMessage(string? code, SourceInspectionStatus status) => code switch
    {
        ConnectorIssueCodes.RemoteAuthenticationFailed => "Connector authentication failed.",
        ConnectorIssueCodes.RemoteAuthorizationFailed => "Read access was denied by the endpoint.",
        ConnectorIssueCodes.RemoteTransientFailure => "The endpoint was unavailable or returned a transient connectivity failure.",
        ConnectorIssueCodes.MissingConfiguration => "Required endpoint configuration or credential reference is unavailable.",
        ConnectorIssueCodes.UnknownConnector => "Configured connector is not installed.",
        ConnectorIssueCodes.SourceConnectionFailed => "Endpoint connection failed; authentication and reachability could not be distinguished by the connector.",
        _ when status == SourceInspectionStatus.Invalid => "Endpoint was reached, but the configured selector or schema is invalid.",
        _ => "Read-only endpoint inspection failed; no connector configuration values were included."
    };

    private static string ExpectedExceptionsFingerprint(IEnumerable<ExpectedExceptionAnnotation> annotations)
    {
        var identity = annotations.OrderBy(annotation => annotation.Id, StringComparer.Ordinal)
            .Select(annotation => new
            {
                annotation.Id,
                RuleId = annotation.RuleId.Value,
                annotation.FindingCode,
                annotation.TargetNode,
                annotation.ArtifactIdSha256,
                annotation.EvaluationScopeId,
                annotation.Reason,
                annotation.Owner
            })
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, PlanJsonOptions)))
            .ToLowerInvariant();
    }

    private static string FingerprintSettings(IReadOnlyDictionary<string, string> settings)
    {
        var identity = settings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { pair.Key, pair.Value }).ToArray();
        return Hash(JsonSerializer.SerializeToUtf8Bytes(identity, PlanJsonOptions));
    }

    private static string FingerprintField(TransformationFieldDefinition field)
    {
        var identity = new
        {
            field.Source,
            field.Target,
            Pipeline = field.Pipeline.Select(step => new
            {
                Type = step.Type.ToString(),
                step.Version,
                Parameters = step.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, pair.Value }).ToArray()
            }).ToArray()
        };
        return Hash(JsonSerializer.SerializeToUtf8Bytes(identity, PlanJsonOptions));
    }

    private static string FingerprintEdge(MigrationEdge edge)
    {
        var identity = new
        {
            edge.Name,
            Sources = edge.Sources.Select(source => source.Value.ToString()).Order(StringComparer.Ordinal).ToArray(),
            Targets = edge.Targets.Select(target => target.Value.ToString()).Order(StringComparer.Ordinal).ToArray(),
            Operation = edge.Operation.Type.ToString(),
            Parameters = edge.Operation.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, pair.Value }).ToArray(),
            Steps = edge.Operation.Steps.Select(step => new
            {
                Type = step.Type.ToString(),
                step.Version,
                Parameters = step.Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { pair.Key, pair.Value }).ToArray()
            }).ToArray(),
            edge.Operation.IsDestructive,
            Recovery = edge.Recovery is null ? null : new
            {
                Mode = edge.Recovery.Mode.ToString(),
                edge.Recovery.Strategy,
                edge.Recovery.RequiresSnapshot,
                edge.Recovery.Justification
            },
            Fields = edge.Operation.Fields.OrderBy(field => field.Target, StringComparer.Ordinal)
                .Select(field => new { field.Target, Fingerprint = FingerprintField(field) }).ToArray()
        };
        return Hash(JsonSerializer.SerializeToUtf8Bytes(identity, PlanJsonOptions));
    }

    private static string FingerprintRule(LoadedProjectConfiguration configuration, string ruleId)
    {
        var ruleFile = configuration.ReferencedFiles.SingleOrDefault(file =>
            file.Kind == ConfigurationFileKind.VerificationRules);
        if (ruleFile?.Document is not ConfigurationMappingNode root ||
            !root.Values.TryGetValue("rules", out var rulesNode) ||
            rulesNode is not ConfigurationMappingNode rules ||
            !rules.Values.TryGetValue(ruleId, out var ruleNode))
            throw new InvalidDataException($"Verification rule '{ruleId}' could not be bound to its configuration identity.");
        return Hash(Encoding.UTF8.GetBytes(CanonicalDocumentNode(ruleNode)));
    }

    private static string CanonicalDocumentNode(ConfigurationDocumentNode node) => node switch
    {
        ConfigurationScalarNode scalar => JsonSerializer.Serialize(new { kind = scalar.Kind.ToString(), scalar.Value }),
        ConfigurationSequenceNode sequence => "[" + string.Join(",", sequence.Values.Select(CanonicalDocumentNode)) + "]",
        ConfigurationMappingNode mapping => "{" + string.Join(",", mapping.Values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalDocumentNode(pair.Value))) + "}",
        _ => throw new InvalidDataException("Unsupported configuration node in semantic fingerprint.")
    };

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateOutputPaths(EvaluationWorkspaceManifest manifest, List<WorkspacePreflightCheck> checks)
    {
        checks.Add(NotChecked("PSPREF013", "output",
            "Output directories exist and are safely contained; effective write permissions are not probed because preflight does not create files.",
            $"{manifest.Paths.Runs}, {manifest.Paths.Evidence}",
            "Confirm the evaluator account can write to the run and evidence directories before execution."));
    }

    private static List<string> CapabilityNames(ConnectorCapabilityDescriptor? descriptor)
    {
        if (descriptor is null) return [];
        var values = new List<string>();
        if (descriptor.Discovery) values.Add("discovery");
        if (descriptor.SourceRead) values.Add("sourceRead");
        if (descriptor.CheckpointCapture) values.Add("checkpointCapture");
        if (descriptor.TargetObservation) values.Add("targetObservation");
        if (descriptor.ShadowWrite) values.Add("shadowWrite");
        if (descriptor.BinaryStreaming) values.Add("binaryStreaming");
        if (descriptor.SchemaInspection) values.Add("schemaInspection");
        if (descriptor.OfflineReplay) values.Add("offlineReplay");
        return values;
    }

    private static WorkspacePreflightCheck Ready(string id, string category, string message, string? component) =>
        new(id, category, "READY", message, component, null);

    private static WorkspacePreflightCheck Blocked(string id, string category, string message, string? component, string? remediation) =>
        new(id, category, "BLOCKED", message, component, remediation);

    private static WorkspacePreflightCheck NotChecked(string id, string category, string message, string? component, string? remediation) =>
        new(id, category, "NOT_CHECKED", message, component, remediation);

    private static WorkspacePreflightCheck Warning(string id, string category, string message, string? component, string? remediation) =>
        new(id, category, "WARNING", message, component, remediation);

    private static WorkspacePreflightCheck[] SortChecks(IEnumerable<WorkspacePreflightCheck> checks) =>
        checks.OrderBy(check => check.Category, StringComparer.Ordinal)
            .ThenBy(check => check.Id, StringComparer.Ordinal)
            .ThenBy(check => check.Component ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(check => check.Status, StringComparer.Ordinal)
            .ToArray();

    private sealed record PlanFingerprintInput(
        Guid WorkspaceId,
        string SemanticModelVersion,
        string ConfigurationFingerprint,
        string ProjectId,
        string ProjectName,
        string ConfigurationVersion,
        IReadOnlyList<WorkspacePlanPack> Packs,
        string VerificationRuleSetFingerprint,
        string ExpectedExceptionsFingerprint,
        string GraphHash,
        string GraphCanonicalizationVersion,
        IReadOnlyList<WorkspacePlanSystem> Systems,
        IReadOnlyList<WorkspacePlanNode> Nodes,
        IReadOnlyList<WorkspacePlanEdge> Edges,
        IReadOnlyList<WorkspacePlanRule> Rules,
        IReadOnlyList<WorkspacePlanExpectedException> ExpectedExceptions,
        int ExpectedExceptionCount,
        bool RequireRecoveryForDestructiveOperations,
        bool AllowIrreversibleOperations,
        string CheckpointOperation,
        string CheckpointOutput,
        string ReplayBehavior,
        string ProjectionStrategy,
        IReadOnlyList<string> ReconciliationRules,
        IReadOnlyList<string> IntentionallyOmittedCapabilities);

    internal sealed record WorkspaceAnalysis(
        EvaluationWorkspaceManifest? Manifest,
        LoadedProjectConfiguration? Configuration,
        MigrationGraph? Graph,
        VerificationRuleConfiguration? RuleConfiguration,
        VerificationRuleSet? RuleSet,
        EffectiveRecoveryPolicy? RecoveryPolicy,
        ConnectorCatalog? Connectors,
        WorkspacePreflightResult Preflight,
        string Root,
        string ConfigurationPath)
    {
        public static WorkspaceAnalysis Failed(Guid? workspaceId, bool connect, IEnumerable<WorkspacePreflightCheck> checks)
        {
            var ordered = SortChecks(checks);
            return new(null, null, null, null, null, null, null,
                new WorkspacePreflightResult(workspaceId, true, "BLOCKED", connect, ordered), string.Empty, string.Empty);
        }
    }
}
