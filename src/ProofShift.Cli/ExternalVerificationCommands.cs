using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Reporting;
using ProofShift.Snapshots;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class ExternalVerificationCommands
{
    internal static async Task<int> VerifyAsync(string configurationPath, string checkpointIdOrPath,
        string observationId, bool jsonOutput)
    {
        if (string.IsNullOrWhiteSpace(checkpointIdOrPath) || string.IsNullOrWhiteSpace(observationId))
        {
            Console.Error.WriteLine("External Verification requires a non-empty checkpoint and observation label.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(configurationPath).ConfigureAwait(false);
            if (!loaded.IsValid || loaded.Configuration is null)
            {
                Console.Error.WriteLine("Configuration validation failed; external Verification was not started.");
                return 1;
            }
            var configuration = loaded.Configuration;
            var compilation = MigrationGraphCompiler.Compile(configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                Console.Error.WriteLine("Migration graph validation failed; external Verification was not started.");
                return 1;
            }
            if (string.IsNullOrWhiteSpace(configuration.Root.Project?.Id))
            {
                Console.Error.WriteLine("External Verification requires a configured project identity.");
                return 1;
            }
            var graph = compilation.Graph;
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var checkpointStore = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
            await using var checkpoint = await checkpointStore.OpenCompleteAsync(checkpointIdOrPath,
                cancellation.Token).ConfigureAwait(false);
            ValidateCheckpointBinding(configuration, graph, checkpoint.Manifest);

            var connectorCatalog = CliComposition.Connectors();
            var contextFactory = new RuntimeConnectorContextFactory();
            var observationRunId = new RunId(Guid.NewGuid());
            var targetNodes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
            if (targetNodes.Length == 0)
                throw new InvalidDataException("External Verification requires at least one configured target graph node.");

            var endpoints = new List<ExternalTargetEndpoint>(targetNodes.Length);
            var runtimes = new List<VerificationTargetRuntime>(targetNodes.Length);
            foreach (var node in targetNodes)
            {
                var system = configuration.Systems.Single(item => item.Id == node.SystemId);
                if (system.Role != SystemRole.Target)
                    throw new InvalidDataException($"External target node '{node.Name}' must use system role 'target'.");
                var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
                var capability = connectorCatalog.Capabilities.SingleOrDefault(item => item.Id == endpoint.Connector.Value);
                if (capability is null || !capability.TargetObservation)
                    throw new InvalidDataException($"Connector '{endpoint.Connector.Value}' for target node '{node.Name}' does not advertise target-observation capability.");
                var observer = connectorCatalog.Observer(endpoint.Connector.Value);
                var targetContext = new TargetObservationContext(contextFactory.Create(configuration, node),
                    observationRunId, SystemRole.Target);
                endpoints.Add(new ExternalTargetEndpoint(node.Name, system.Id, endpoint.Id, observer.Id, observer.Version));
                runtimes.Add(new VerificationTargetRuntime(node.Name, observer, targetContext));
            }

            var rules = VerificationRuleConfigurationLoader.Load(configuration);
            var ruleSet = CliComposition.RulesFor(configuration, rules).Resolve(rules);
            var observation = new ExternalMigrationObservation(observationRunId, observationId,
                configuration.ConfigurationHash, graph.GraphHash, checkpoint.Manifest.Id,
                checkpoint.Manifest.ManifestHash!, checkpoint.Manifest.SourceFingerprint!, endpoints,
                DateTimeOffset.UtcNow);
            var temporaryDirectory = Path.Combine(projectDirectory, ".proofshift", "temporary");
            var result = await new VerificationService(checkpointStore).VerifyExternalTargetAsync(
                configuration, graph, observation, ruleSet, projectDirectory, temporaryDirectory,
                VersionCommands.GetProductVersion(), runtimes, cancellation.Token).ConfigureAwait(false);

            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
            var evidenceReceipt = await evidenceStore.SaveAsync(result.EvidenceGraph, cancellation.Token).ConfigureAwait(false);
            var runStore = new FileSystemExternalVerificationRunStore(projectDirectory);
            await runStore.SaveAsync(configuration.Root.Project.Id, result, cancellation.Token).ConfigureAwait(false);
            var output = await CreateOutputAsync(projectDirectory, result, evidenceReceipt.RelativePath,
                checkpoint.Manifest,
                cancellation.Token).ConfigureAwait(false);
            WriteResult(output, jsonOutput);
            return result.Run.Outcome is VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (IsSafeExternalVerificationFailure(exception))
        {
            Console.Error.WriteLine($"External target Verification failed ({FailureCode(exception)}). Check the configuration, checkpoint bindings, target observer capability, and secret references; resolved credentials are never displayed.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> ReadReportAsync(string configurationPath, string verificationRunId,
        bool jsonOutput)
    {
        if (!Guid.TryParse(verificationRunId, out var parsedRunId))
        {
            Console.Error.WriteLine("Verification run identifier must be a GUID.");
            return 2;
        }
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(configurationPath).ConfigureAwait(false);
            if (!loaded.IsValid || loaded.Configuration?.Root.Project?.Id is not { Length: > 0 } projectId)
                throw new InvalidDataException("Report configuration must contain a valid project identity.");
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var report = await LoadReportAsync(projectDirectory, loaded.Configuration, projectId,
                new RunId(parsedRunId), CancellationToken.None).ConfigureAwait(false);
            WriteReport(report, jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted Verification report failed integrity or binding validation ({FailureCode(exception)}).");
            return 1;
        }
    }

    internal static async Task<int> CompareAsync(string configurationPath, string beforeRunId,
        string afterRunId, bool jsonOutput)
    {
        if (!Guid.TryParse(beforeRunId, out var beforeGuid) || !Guid.TryParse(afterRunId, out var afterGuid))
        {
            Console.Error.WriteLine("Verification run identifiers must be GUIDs.");
            return 2;
        }
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(configurationPath).ConfigureAwait(false);
            if (!loaded.IsValid || loaded.Configuration?.Root.Project?.Id is not { Length: > 0 } projectId)
                throw new InvalidDataException("Comparison configuration must contain a valid project identity.");
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var before = await LoadReportAsync(projectDirectory, loaded.Configuration, projectId,
                new RunId(beforeGuid), CancellationToken.None).ConfigureAwait(false);
            var after = await LoadReportAsync(projectDirectory, loaded.Configuration, projectId,
                new RunId(afterGuid), CancellationToken.None).ConfigureAwait(false);
            var comparison = PersistedVerificationRunComparison.Create(before, after);
            WriteComparison(comparison, jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted Verification comparison failed integrity or binding validation ({FailureCode(exception)}).");
            return 1;
        }
    }

    private static async Task<PersistedVerificationReport> LoadReportAsync(string projectDirectory,
        LoadedProjectConfiguration configuration, string projectId, RunId runId,
        CancellationToken cancellationToken)
    {
        var runStore = new FileSystemExternalVerificationRunStore(projectDirectory);
        var run = await runStore.ReadAsync(runId, cancellationToken).ConfigureAwait(false);
        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
        var evidenceManifest = await evidenceStore.ReadManifestAsync(runId, cancellationToken).ConfigureAwait(false);
        var snapshotStore = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
        await using var checkpoint = await snapshotStore.OpenCompleteAsync(run.CheckpointId, cancellationToken).ConfigureAwait(false);
        var ledgerStore = await SqliteVerificationLedgerStore.OpenAsync(projectDirectory, run.Ledger,
            cancellationToken).ConfigureAwait(false);
        await using (ledgerStore.ConfigureAwait(false))
        {
            var ledger = await ledgerStore.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            return await PersistedVerificationReportBuilder.BuildAsync(projectId,
                configuration.Root.Project?.Name ?? Path.GetFileName(projectDirectory), run, evidenceManifest,
                evidenceStore.ReadRecordsAsync(runId, cancellationToken), checkpoint.Manifest, ledger,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ExternalVerificationOutput> CreateOutputAsync(string projectDirectory,
        ExternalVerificationResult result, string evidencePath, SourceCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var ledgerStore = await SqliteVerificationLedgerStore.OpenAsync(projectDirectory, result.Ledger,
            cancellationToken).ConfigureAwait(false);
        await using (ledgerStore.ConfigureAwait(false))
        {
            var ledger = await ledgerStore.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            var sourceGap = Math.Max(ledger.UnaccountedDispositionCount,
                Math.Max(0, ledger.SourceCount - ledger.DispositionCount));
            var targetGap = Math.Max(0, ledger.TargetCount - ledger.LineageCount);
            return new ExternalVerificationOutput(
                "external-target-verification",
                result.Run.Id.Value.ToString("D", CultureInfo.InvariantCulture),
                result.Run.ObservationId,
                result.Run.ObservationRunId.Value.ToString("D", CultureInfo.InvariantCulture),
                result.Run.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture),
                result.Run.ConfigurationHash,
                result.Run.GraphHash,
                result.Run.SourceFingerprint,
                result.Run.TargetFingerprint,
                result.Run.RuleSetFingerprint,
                result.Run.Outcome.ToString().ToLowerInvariant(),
                result.Run.FailedRules,
                ledger.SourceCount,
                ledger.TargetCount,
                Math.Max(0, ledger.SourceCount - sourceGap),
                sourceGap,
                ledger.LineageCount,
                targetGap,
                result.Run.EvidenceFingerprint,
                "graph-derived expected lineage",
                RecoveryAssessed: false,
                QualificationAvailable: false,
                evidencePath,
                checkpoint.Endpoints
                    .Where(endpoint => endpoint.EvaluationScopeId is not null &&
                        endpoint.EvaluationScopeType is not null &&
                        endpoint.EvaluationScopeFingerprint is not null)
                    .OrderBy(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal)
                    .Select(endpoint => new EvaluationScopeOutput(endpoint.SourceNodeKey,
                        endpoint.EvaluationScopeId!, endpoint.EvaluationScopeType!,
                        endpoint.EvaluationScopeFingerprint!, endpoint.ArtifactCount))
                    .ToArray(),
                ScopeClaimBoundary(checkpoint.Endpoints));
        }
    }

    private static string ScopeClaimBoundary(DomainList<CheckpointEndpoint> endpoints)
    {
        var scoped = endpoints.Count(endpoint => endpoint.EvaluationScopeId is not null &&
            endpoint.EvaluationScopeType is not null && endpoint.EvaluationScopeFingerprint is not null);
        if (scoped == 0)
            return "Results are limited to configured source selectors; no claim is made about artifacts outside them.";
        if (scoped == endpoints.Count)
            return "Results are limited to the declared evaluation scopes; no result proves completeness outside them.";
        return "Results are limited to each source's configured selector; only listed sources have explicit evaluation scopes, and no result proves completeness outside those selectors.";
    }

    private static void ValidateCheckpointBinding(LoadedProjectConfiguration configuration,
        MigrationGraph graph, SourceCheckpoint checkpoint)
    {
        if (checkpoint.Status != CheckpointStatus.Complete || !checkpoint.Replayable ||
            checkpoint.ConfigurationHash != configuration.ConfigurationHash ||
            checkpoint.GraphHash != graph.GraphHash)
            throw new InvalidDataException("Checkpoint is incomplete or does not match the current project configuration and graph.");

        var sourceNodes = graph.Nodes.Where(node => node.Type == MigrationNodeType.Source)
            .ToDictionary(node => node.Name, StringComparer.Ordinal);
        if (!checkpoint.Endpoints.Select(endpoint => endpoint.SourceNodeKey).Order(StringComparer.Ordinal)
            .SequenceEqual(sourceNodes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Checkpoint source-node coverage does not match the configured graph.");
        foreach (var endpoint in checkpoint.Endpoints)
        {
            var node = sourceNodes[endpoint.SourceNodeKey];
            if (endpoint.SystemId != node.SystemId || endpoint.EndpointId != node.EndpointId ||
                endpoint.SelectorHash != SnapshotFingerprints.SelectorHash(node.Selector) ||
                !endpoint.IdentityFields.SequenceEqual(node.Selector.IdentityFields, StringComparer.Ordinal) ||
                endpoint.EvaluationScopeId != node.Selector.EvaluationScope?.Id ||
                endpoint.EvaluationScopeType != (node.Selector.EvaluationScope is { } scope
                    ? scope.Type switch
                    {
                        EvaluationScopeType.FullDeclaredSource => "full-declared-source",
                        EvaluationScopeType.BoundedEvaluation => "bounded-evaluation",
                        EvaluationScopeType.CustomerPreparedSubset => "customer-prepared-subset",
                        _ => throw new InvalidDataException("Configured evaluation scope type is unsupported.")
                    }
                    : null) ||
                endpoint.EvaluationScopeFingerprint != node.Selector.EvaluationScope?.Fingerprint)
                throw new InvalidDataException("Checkpoint source selector binding does not match the configured graph.");
        }
    }

    private static void WriteResult(ExternalVerificationOutput output, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, CliOutput.JsonOptions));
            return;
        }
        Console.WriteLine("ProofShift External Target Verification");
        Console.WriteLine("Assurance Scope: EXTERNAL TARGET VERIFICATION");
        Console.WriteLine($"Verification Run: {output.VerificationRunId}");
        Console.WriteLine($"Observation: {output.ObservationId}");
        Console.WriteLine($"Observation Run: {output.ObservationRunId}");
        Console.WriteLine($"Checkpoint: {output.CheckpointId}");
        Console.WriteLine($"Outcome: {output.VerificationOutcome.ToUpperInvariant()}");
        Console.WriteLine($"Failures: {output.VerificationFailureCount}");
        Console.WriteLine($"Source Accounting: {output.AccountedSourceCount}/{output.SourceArtifactCount} accounted; {output.UnaccountedSourceCount} gaps");
        foreach (var scope in output.EvaluationScopes)
            Console.WriteLine($"Evaluation Scope: {scope.SourceNodeKey} / {scope.Id} ({scope.Type}); {scope.ArtifactCount} artifacts; sha256:{scope.Fingerprint}");
        Console.WriteLine($"Scope claim boundary: {output.EvaluationScopeClaimBoundary}");
        Console.WriteLine($"Target Lineage: {output.LineagedTargetCount}/{output.TargetArtifactCount} lineaged; {output.UnexplainedTargetCount} gaps");
        Console.WriteLine($"Target Fingerprint: {output.TargetFingerprint}");
        Console.WriteLine($"Evidence Fingerprint: {output.EvidenceFingerprint}");
        Console.WriteLine($"Evidence: {output.EvidencePath}");
        Console.WriteLine("Recovery: NOT ASSESSED");
        Console.WriteLine("Qualification: NOT AVAILABLE");
        Console.WriteLine("Vendor execution lineage: NOT OBSERVED");
        Console.WriteLine("Lineage basis: graph-derived expected lineage");
    }

    private static void WriteReport(PersistedVerificationReport report, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, CliOutput.JsonOptions));
            return;
        }
        Console.WriteLine("ProofShift Persisted External Verification Report");
        Console.WriteLine($"Project: {report.ProjectName} ({report.ProjectId})");
        Console.WriteLine($"Assurance Scope: {report.AssuranceScope}");
        Console.WriteLine($"Verification Run: {report.VerificationRunId}");
        Console.WriteLine($"Observation: {report.ObservationId} ({report.ObservationRunId})");
        Console.WriteLine($"Outcome / Failures: {report.VerificationOutcome} / {report.VerificationFailureCount}");
        Console.WriteLine($"Source Accounting: {report.AccountedSourceCount}/{report.SourceArtifactCount} ({report.SourceAccountingStatus})");
        foreach (var scope in report.EvaluationScopes)
            Console.WriteLine($"Evaluation Scope: {scope.SourceNodeKey} / {scope.Id} ({scope.Type}); {scope.ArtifactCount} artifacts; sha256:{scope.Fingerprint}");
        Console.WriteLine($"Scope claim boundary: {report.EvaluationScopeClaimBoundary}");
        Console.WriteLine($"Target Lineage: {report.LineagedTargetCount}/{report.TargetArtifactCount} ({report.TargetLineageStatus})");
        Console.WriteLine($"Evidence Fingerprint: {report.EvidenceFingerprint}");
        Console.WriteLine($"Lineage basis: {report.LineageBasis}");
        Console.WriteLine("Recovery: NOT ASSESSED");
        Console.WriteLine("Qualification: NOT AVAILABLE");
        Console.WriteLine(report.ClaimBoundary);
        foreach (var finding in report.FailedFindingGroups)
            Console.WriteLine($"Finding {finding.Key}: {finding.Value.ToString(CultureInfo.InvariantCulture)}");
        if (report.FailedFindingGroups.Count == 0) Console.WriteLine("Findings: none");
    }

    private static void WriteComparison(PersistedVerificationRunComparison comparison, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(comparison, CliOutput.JsonOptions));
            return;
        }
        Console.WriteLine("ProofShift Verification-Only Comparison");
        Console.WriteLine($"Project: {comparison.ProjectId}");
        Console.WriteLine($"Before / After: {comparison.BeforeVerificationRunId} / {comparison.AfterVerificationRunId}");
        Console.WriteLine($"Configuration / Graph Changed: {comparison.ConfigurationHashChanged} / {comparison.GraphHashChanged}");
        Console.WriteLine($"Checkpoint Semantics / Source Changed: {comparison.CheckpointSemanticsChanged} / {comparison.SourceFingerprintChanged}");
        Console.WriteLine($"Target / Rules Changed: {comparison.TargetFingerprintChanged} / {comparison.RuleSetFingerprintChanged}");
        Console.WriteLine($"Outcome / Evidence Changed: {comparison.VerificationOutcomeChanged} / {comparison.EvidenceFingerprintChanged}");
        Console.WriteLine($"Source Accounting / Target Lineage Gaps Changed: {comparison.SourceAccountingGapsChanged} / {comparison.TargetLineageGapsChanged}");
        Console.WriteLine($"Recovery: {comparison.RecoveryComparison}");
        foreach (var item in comparison.FindingsResolved) Console.WriteLine($"Resolved {item.Finding}: {item.Count}");
        foreach (var item in comparison.FindingsIntroduced) Console.WriteLine($"Introduced {item.Finding}: {item.Count}");
    }

    private static bool IsPersistedReportFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            FormatException or InvalidDataException or JsonException or SnapshotStoreException or DbException;

    private static bool IsSafeExternalVerificationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            InvalidDataException or SnapshotStoreException or ConnectorResolutionException or ConnectorConfigurationException or
            ConnectorReadException or VerificationRuleException or DbException;

    private static string FailureCode(Exception exception) => exception switch
    {
        SnapshotStoreException snapshot => snapshot.Code,
        ConnectorResolutionException connector => connector.Code,
        ConnectorConfigurationException connector => connector.Code,
        ConnectorReadException connector => connector.Code,
        VerificationRuleException rule => rule.Code,
        _ => exception.GetType().Name
    };
}
