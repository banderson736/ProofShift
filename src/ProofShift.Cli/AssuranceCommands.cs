using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Data.Common;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Pension;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Reporting;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class AssuranceCommands
{
    internal static async Task<int> VerifyAsync(string path, string checkpointId, string projectionRunId, bool jsonOutput)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (!Guid.TryParse(checkpointId, out var checkpointGuid) || !Guid.TryParse(projectionRunId, out var projectionGuid))
            {
                Console.Error.WriteLine("Checkpoint and projection identifiers must be GUIDs.");
                return 2;
            }

            var loaded = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                Console.Error.WriteLine("Configuration validation failed; verification was not started.");
                return 1;
            }

            var compilation = MigrationGraphCompiler.Compile(loaded.Configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                Console.Error.WriteLine("Migration graph validation failed; verification was not started.");
                return 1;
            }

            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var projection = await ProjectionRunManifestStore.ReadAsync(projectDirectory, new RunId(projectionGuid),
                cancellation.Token).ConfigureAwait(false);
            if (projection.CheckpointId is null || !Guid.TryParse(projection.CheckpointId, out var manifestCheckpoint) ||
                manifestCheckpoint != checkpointGuid || projection.CheckpointManifestHash is null ||
                projection.CheckpointSourceFingerprint is null || projection.ProjectionFingerprint is null)
            {
                Console.Error.WriteLine("Projection manifest does not match the requested complete checkpoint binding.");
                return 1;
            }

            var binding = new ProjectionVerificationBinding(projection.RunId, projection.ConfigurationHash, projection.GraphHash,
                new CheckpointId(checkpointGuid), projection.CheckpointManifestHash, projection.CheckpointSourceFingerprint,
                projection.ProjectionFingerprint, projection.ProjectionFingerprintVersion, projection.SourceArtifactCount,
                projection.TargetArtifactCount,
                projection.Status.ToString().ToLowerInvariant(), projection.ManifestHash, projection.JournalPath,
                projection.ConnectorVersions);
            var targetConnectors = new ShadowTargetConnectorRegistry(
            [
                new PostgresShadowTargetConnector(),
                new FilesystemShadowTargetConnector()
            ]);
            var contextFactory = new RuntimeConnectorContextFactory();
            var targetRuntimes = compilation.Graph.Nodes
                .Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                .OrderBy(node => node.Name, StringComparer.Ordinal)
                .Select(node =>
                {
                    var system = loaded.Configuration.Systems.Single(item => item.Id == node.SystemId);
                    var endpoint = system.StorageEndpoints.Single(item => item.Id == node.EndpointId);
                    var connector = targetConnectors.Resolve(endpoint.Connector);
                    return new VerificationTargetRuntime(node.Name, connector,
                        new ShadowTargetContext(contextFactory.Create(loaded.Configuration, node), projection.RunId, system.Role));
                }).ToArray();
            var registry = CliComposition.RulesFor(loaded.Configuration, VerificationRuleConfigurationLoader.Load(loaded.Configuration));
            var ruleSet = registry.Resolve(VerificationRuleConfigurationLoader.Load(loaded.Configuration));
            var snapshotStore = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
            var verificationService = new VerificationService(snapshotStore);
            var result = await verificationService.VerifyAsync(loaded.Configuration, compilation.Graph, binding, ruleSet,
                projectDirectory, Path.Combine(projectDirectory, ".proofshift", "temporary"), "0.1.0",
                targetRuntimes, cancellation.Token).ConfigureAwait(false);
            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
            var receipt = await evidenceStore.SaveAsync(result.EvidenceGraph, cancellation.Token).ConfigureAwait(false);
            var output = new VerificationOutput(result.Run.Id.Value.ToString("D", CultureInfo.InvariantCulture),
                result.Run.Outcome.ToString().ToLowerInvariant(), result.Run.ConfigurationHash, result.Run.GraphHash,
                result.Run.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture), result.Run.ProjectionRunId.Value.ToString("D", CultureInfo.InvariantCulture),
                result.Run.ProjectionManifestHash, result.Run.RuleSetFingerprint, result.Run.EvidenceFingerprint, result.Findings.Count,
                result.Run.FailedRules, result.Run.WarningRules, receipt.RelativePath);
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(output, CliOutput.JsonOptions));
            else
            {
                Console.WriteLine("ProofShift Semantic Verification");
                Console.WriteLine($"Run: {output.RunId}");
                Console.WriteLine($"Outcome: {output.Outcome.ToUpperInvariant()}");
                Console.WriteLine($"Findings: {output.Findings}");
                Console.WriteLine($"Failed Rules: {output.FailedRules}");
                Console.WriteLine($"Warning Rules: {output.WarningRules}");
                Console.WriteLine($"Evidence Fingerprint: {output.EvidenceFingerprint}");
                Console.WriteLine($"Evidence: {output.EvidencePath}");
            }

            return result.Run.Outcome is VerificationOutcome.Passed or VerificationOutcome.PassedWithWarnings ? 0 : 1;
        }
        catch (OperationCanceledException) { return 130; }
        catch (VerificationRuleException exception)
        {
            Console.Error.WriteLine($"Verification failed ({exception.Code}).");
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("Semantic verification could not be completed.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }


    internal static async Task<int> DryRunAsync(string path, bool jsonOutput)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(path, cancellation.Token).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                Console.Error.WriteLine("Configuration validation failed; dry run was not started.");
                return 1;
            }
            var compilation = MigrationGraphCompiler.Compile(loaded.Configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                Console.Error.WriteLine("Migration graph validation failed; dry run was not started.");
                return 1;
            }

            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var sourceConnectors = new ConnectorRegistry(
            [
                new PostgresSourceConnector(),
                new SqlServerSourceConnector(),
                new FilesystemSourceConnector(),
                new CsvSourceConnector()
            ]);
            var shadowConnectors = new ShadowTargetConnectorRegistry(
            [
                new PostgresShadowTargetConnector(),
                new FilesystemShadowTargetConnector()
            ]);
            var snapshotStore = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
            var verificationService = new VerificationService(snapshotStore);
            var recoveryService = new RecoveryService(snapshotStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()]));
            var orchestrator = new DryRunOrchestrator(sourceConnectors, shadowConnectors, snapshotStore,
                verificationService, recoveryService);
            var ruleRegistry = CliComposition.RulesFor(loaded.Configuration, VerificationRuleConfigurationLoader.Load(loaded.Configuration));
            var rules = ruleRegistry.Resolve(VerificationRuleConfigurationLoader.Load(loaded.Configuration));
            var policy = RecoveryPolicyConfigurationLoader.Load(loaded.Configuration);
            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
            var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(projectDirectory, ".proofshift", "recovery"));
            var result = await orchestrator.RunAsync(loaded.Configuration, compilation.Graph, rules, policy,
                projectDirectory, Path.Combine(projectDirectory, ".proofshift", "temporary"), "0.1.0",
                evidenceStore, recoveryStore, cancellation.Token).ConfigureAwait(false);
            var output = ToDryRunOutput(loaded.Configuration.Root.Project?.Name, result);
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(output, CliOutput.JsonOptions));
            else WriteDryRunHuman(output);
            return result.Outcome == DryRunExecutionOutcome.Qualified ? 0 :
                result.Outcome == DryRunExecutionOutcome.Cancelled ? 130 : 1;
        }
        catch (OperationCanceledException) { return 130; }
        catch (RecoveryException exception)
        {
            Console.Error.WriteLine($"Dry-run recovery analysis failed ({exception.Code}).");
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("Dry run could not be completed.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }


    private static DryRunOutput ToDryRunOutput(string? projectName, DryRunExecutionResult result)
    {
        var recovery = result.Recovery;
        return new DryRunOutput(projectName, result.Outcome.ToString().ToLowerInvariant(),
            recovery?.Qualification.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            result.Checkpoint?.Status.ToString().ToLowerInvariant(),
            result.Checkpoint?.Id.Value.ToString("N", CultureInfo.InvariantCulture),
            result.Checkpoint?.Checkpoint?.ManifestHash, result.Checkpoint?.Checkpoint?.SourceFingerprint,
            result.Checkpoint?.Checkpoint?.CrossSystemAtomic,
            result.Checkpoint?.Checkpoint?.Endpoints.Select(endpoint => new CheckpointConsistencyOutput(
                endpoint.SourceNodeKey, endpoint.RequestedConsistencyStrategy, endpoint.EffectiveConsistencyStrategy,
                endpoint.SourceConsistency.ToString().ToLowerInvariant(), endpoint.ConsistencyDowngrade)).ToArray() ?? [],
            result.Projection?.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            result.Projection?.Status.ToString().ToLowerInvariant(), result.Projection?.Fingerprint,
            recovery?.Assessment.Binding.ProjectionManifestHash,
            result.Verification?.Run.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            result.Verification?.Run.Outcome.ToString().ToLowerInvariant(),
            result.Verification?.Run.FailedRules,
            result.Verification?.Findings.Count(finding => finding.Code == "UnaccountedSource"),
            result.Verification?.Findings.Count(finding => finding.Code == "UnexpectedTarget"),
            result.Verification?.Run.RuleSetFingerprint, result.Verification?.Run.EvidenceFingerprint,
            recovery?.Assessment.Fingerprint, RecoveryAssessment.FingerprintVersion,
            recovery?.Assessment.PolicyFingerprint, EffectiveRecoveryPolicy.FingerprintVersion,
            recovery?.Plan.Fingerprint, RecoveryPlan.FingerprintVersion,
            recovery?.Rehearsal.Outcome.ToString().ToLowerInvariant(), recovery?.Rehearsal.Fingerprint,
            RecoveryRehearsal.FingerprintVersion, recovery?.Rehearsal.BaselineFingerprint,
            recovery?.Rehearsal.RestoredFingerprint, recovery?.Rehearsal.ShadowCleanupFingerprint,
            recovery?.Assessment.Coverage.ExecutedEdges, recovery?.Assessment.Coverage.ReverseEdges,
            recovery?.Assessment.Coverage.RestoreEdges, recovery?.Assessment.Coverage.CompensateEdges,
            recovery?.Assessment.Coverage.IrreversibleEdges, recovery?.Assessment.Coverage.AffectedArtifacts,
            recovery?.Assessment.Coverage.RecoverableArtifacts, recovery?.Assessment.Coverage.FailedEdges,
            recovery?.Assessment.Coverage.BySemanticType.Values.ToArray() ?? [],
            recovery?.Assessment.Coverage.IrrecoverableArtifacts,
            recovery?.Assessment.Coverage.UnknownArtifacts,
            recovery?.Assessment.Coverage.RecoverablePercentage,
            recovery?.EvidenceGraph.Fingerprint, RecoveryEvidenceGraph.FormatVersion,
            recovery?.Qualification.DryRunFingerprint, DryRunQualification.FingerprintVersion,
            result.RecoveryArtifacts?.RelativeDirectory,
            recovery?.Qualification.Reasons.Select(reason => $"{reason.Code}: {reason.Message}").ToArray() ?? [],
            result.FailureCode);
    }


    private static void WriteDryRunHuman(DryRunOutput output)
    {
        Console.WriteLine("ProofShift Dry Run");
        Console.WriteLine();
        Console.WriteLine($"Project: {output.Project ?? "unnamed"}");
        if (output.DryRunId is not null) Console.WriteLine($"Dry Run: {output.DryRunId}");
        Console.WriteLine();
        Console.WriteLine("SOURCE CHECKPOINT");
        Console.WriteLine($"Status: {output.CheckpointStatus?.ToUpperInvariant() ?? "NOT CREATED"}");
        if (output.CheckpointId is not null) Console.WriteLine($"Checkpoint: {output.CheckpointId}");
        if (output.CrossSystemAtomic is false) Console.WriteLine("Cross-System Atomic: NO");
        foreach (var endpoint in output.EndpointConsistency)
        {
            Console.WriteLine($"{endpoint.Endpoint}: requested={endpoint.RequestedStrategy}; effective={endpoint.EffectiveStrategy}; guarantee={endpoint.Guarantee}");
            if (endpoint.Downgrade is not null) Console.WriteLine($"  Downgrade: {endpoint.Downgrade}");
        }
        Console.WriteLine();
        Console.WriteLine("PROJECTION");
        Console.WriteLine($"Status: {output.ProjectionStatus?.ToUpperInvariant() ?? "NOT CREATED"}");
        if (output.ProjectionFingerprint is not null) Console.WriteLine($"Projection Fingerprint: {output.ProjectionFingerprint}");
        Console.WriteLine();
        Console.WriteLine("VERIFICATION");
        Console.WriteLine($"Status: {output.VerificationOutcome?.ToUpperInvariant() ?? "NOT COMPLETED"}");
        Console.WriteLine($"Verification Failures: {output.VerificationFailures ?? 0}");
        Console.WriteLine($"Unaccounted Source Artifacts: {output.UnaccountedSources ?? 0}");
        Console.WriteLine($"Unexplained Target Artifacts: {output.UnexplainedTargets ?? 0}");
        if (output.VerificationEvidenceFingerprint is not null) Console.WriteLine($"Evidence Fingerprint: {output.VerificationEvidenceFingerprint}");
        Console.WriteLine();
        Console.WriteLine("RECOVERY");
        Console.WriteLine($"Edges Assessed: {output.EdgesAssessed?.ToString(CultureInfo.InvariantCulture) ?? "0"}");
        Console.WriteLine($"Reverse / Restore / Compensate / Irreversible: {output.ReverseEdges ?? 0} / {output.RestoreEdges ?? 0} / {output.CompensateEdges ?? 0} / {output.IrreversibleEdges ?? 0}");
        Console.WriteLine($"Recovery Coverage: {output.RecoverableArtifacts?.ToString(CultureInfo.InvariantCulture) ?? "0"}/{output.AffectedArtifacts?.ToString(CultureInfo.InvariantCulture) ?? "0"} artifacts");
        Console.WriteLine($"Irrecoverable / Unknown Artifacts: {output.IrrecoverableArtifacts ?? 0} / {output.UnknownArtifacts ?? 0}");
        if (output.RecoverablePercentage is { } percentage) Console.WriteLine($"Recovery Coverage Percentage: {percentage.ToString(CultureInfo.InvariantCulture)}%");
        Console.WriteLine($"Rehearsal: {output.RehearsalOutcome?.ToUpperInvariant() ?? "NOT RUN"}");
        if (output.RecoveryPolicyFingerprint is not null) Console.WriteLine($"Recovery Policy Fingerprint ({output.RecoveryPolicyFingerprintVersion}): {output.RecoveryPolicyFingerprint}");
        if (output.RecoveryAssessmentFingerprint is not null) Console.WriteLine($"Recovery Assessment Fingerprint ({output.RecoveryAssessmentFingerprintVersion}): {output.RecoveryAssessmentFingerprint}");
        if (output.RecoveryBaselineFingerprint is not null) Console.WriteLine($"Rehearsal Baseline: {output.RecoveryBaselineFingerprint}");
        if (output.RecoveryRestoredFingerprint is not null) Console.WriteLine($"Recovery Result: {output.RecoveryRestoredFingerprint}");
        if (output.ShadowCleanupFingerprint is not null) Console.WriteLine($"Shadow Cleanup: {output.ShadowCleanupFingerprint}");
        foreach (var coverage in output.RecoveryBySemanticType)
            Console.WriteLine($"{coverage.SemanticType}: {coverage.RecoverableArtifacts}/{coverage.AffectedArtifacts} recoverable");
        if (output.DryRunFingerprint is not null) Console.WriteLine($"Dry Run Fingerprint: {output.DryRunFingerprint}");
        foreach (var reason in output.Reasons) Console.WriteLine($"Finding: {reason}");
        if (output.FailureCode is not null) Console.WriteLine($"Failure Code: {output.FailureCode}");
        Console.WriteLine();
        Console.WriteLine($"RESULT: {(output.Outcome == "qualified" ? "QUALIFIED DRY RUN" : "DRY RUN NOT QUALIFIED")}");
    }


    internal static async Task<int> ReadAssuranceReportAsync(string configurationPath, string dryRunId, bool jsonOutput)
    {
        if (!Guid.TryParse(dryRunId, out var parsedDryRunId))
        {
            Console.Error.WriteLine("Dry-run identifier must be a GUID.");
            return 2;
        }
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(configurationPath).ConfigureAwait(false);
            if (!loaded.IsValid || loaded.Configuration is null)
                throw new InvalidDataException("Report configuration is invalid.");
            var configuration = loaded.Configuration;
            var compilation = MigrationGraphCompiler.Compile(configuration);
            if (!compilation.IsValid || compilation.Graph is null)
                throw new InvalidDataException("Report migration graph is invalid.");
            var pack = ResolveConfiguredReportPack(configuration);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var report = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(parsedDryRunId),
                configuration.Root.Project?.Name ?? Path.GetFileName(projectDirectory), pack,
                configuration.Root.Project?.Id, configuration.ConfigurationHash, compilation.Graph.GraphHash)
                .ConfigureAwait(false);
            WriteAssuranceReport(report, jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted assurance report failed: {exception.Message}");
            return 1;
        }
    }


    internal static async Task<int> ReadStandaloneAssuranceReportAsync(string projectDirectory, string dryRunId, bool jsonOutput)
    {
        if (!Guid.TryParse(dryRunId, out var parsedDryRunId)) return 2;
        try
        {
            var report = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(parsedDryRunId),
                Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory))),
                expectedPack: null, expectedProjectId: null, expectedConfigurationHash: null,
                expectedGraphHash: null).ConfigureAwait(false);
            WriteAssuranceReport(report, jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted assurance artifacts failed validation: {exception.Message}");
            return 1;
        }
    }


    internal static async Task<int> CompareAssuranceRunsAsync(string configurationPath, string beforeDryRunId,
        string afterDryRunId, bool jsonOutput)
    {
        if (!Guid.TryParse(beforeDryRunId, out var beforeId) || !Guid.TryParse(afterDryRunId, out var afterId))
        {
            Console.Error.WriteLine("Dry-run identifiers must be GUIDs.");
            return 2;
        }
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(configurationPath).ConfigureAwait(false);
            if (!loaded.IsValid || loaded.Configuration is null)
                throw new InvalidDataException("Comparison configuration is invalid.");
            var configuration = loaded.Configuration;
            var compilation = MigrationGraphCompiler.Compile(configuration);
            if (!compilation.IsValid || compilation.Graph is null)
                throw new InvalidDataException("Comparison migration graph is invalid.");
            var pack = ResolveConfiguredReportPack(configuration);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(configurationPath))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var projectName = configuration.Root.Project?.Name ?? Path.GetFileName(projectDirectory);
            var expectedProjectId = configuration.Root.Project?.Id;
            var before = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(beforeId),
                projectName, pack, expectedProjectId, expectedConfigurationHash: null,
                expectedGraphHash: null).ConfigureAwait(false);
            var after = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(afterId),
                projectName, pack, expectedProjectId, expectedConfigurationHash: null,
                expectedGraphHash: null).ConfigureAwait(false);
            WriteAssuranceComparison(PersistedAssuranceRunComparison.Create(before, after), jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted assurance comparison failed: {exception.Message}");
            return 1;
        }
    }


    internal static async Task<int> CompareStandaloneAssuranceRunsAsync(string projectDirectory,
        string beforeDryRunId, string afterDryRunId, bool jsonOutput)
    {
        if (!Guid.TryParse(beforeDryRunId, out var beforeId) || !Guid.TryParse(afterDryRunId, out var afterId)) return 2;
        try
        {
            var projectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)));
            var before = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(beforeId),
                projectName, expectedPack: null, expectedProjectId: null, expectedConfigurationHash: null,
                expectedGraphHash: null).ConfigureAwait(false);
            var after = await LoadPersistedAssuranceReportAsync(projectDirectory, new DryRunId(afterId),
                projectName, expectedPack: null, expectedProjectId: null, expectedConfigurationHash: null,
                expectedGraphHash: null).ConfigureAwait(false);
            WriteAssuranceComparison(PersistedAssuranceRunComparison.Create(before, after), jsonOutput);
            return 0;
        }
        catch (Exception exception) when (IsPersistedReportFailure(exception))
        {
            Console.Error.WriteLine($"Persisted assurance comparison failed: {exception.Message}");
            return 1;
        }
    }


    private static async Task<PersistedAssuranceReport> LoadPersistedAssuranceReportAsync(string projectDirectory,
        DryRunId dryRunId, string projectName, IDomainPack? expectedPack, string? expectedProjectId,
        string? expectedConfigurationHash, string? expectedGraphHash)
    {
        var cancellationToken = CancellationToken.None;
        var recoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(projectDirectory, ".proofshift", "recovery"));
        var recovery = await recoveryStore.ReadSummaryAsync(dryRunId, cancellationToken).ConfigureAwait(false);
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
        var checkpoint = await checkpointStore.ReadManifestAsync(
            recovery.CheckpointId.Value.ToString("D", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        var projection = await ProjectionRunManifestStore.ReadAsync(projectDirectory, recovery.ProjectionRunId,
            cancellationToken).ConfigureAwait(false);
        if (projection.Status != ProjectionStatus.Succeeded)
            throw new InvalidDataException("Persisted Projection run did not succeed.");
        if (projection.ManifestHash != recovery.ProjectionManifestHash)
            throw new InvalidDataException("Persisted Projection manifest hash does not match Recovery.");
        if ((expectedProjectId is not null && projection.ProjectId != expectedProjectId) ||
            string.IsNullOrWhiteSpace(projection.ProjectId))
            throw new InvalidDataException("Persisted Projection project identity does not match the selected project.");
        if (projection.ConfigurationHash != recovery.ConfigurationHash)
            throw new InvalidDataException("Persisted Projection configuration hash does not match Recovery.");
        if (projection.GraphHash != recovery.GraphHash)
            throw new InvalidDataException("Persisted Projection graph hash does not match Recovery.");
        if (projection.CheckpointId != recovery.CheckpointId.Value.ToString("N", CultureInfo.InvariantCulture))
            throw new InvalidDataException("Persisted Projection checkpoint identity does not match Recovery.");
        if (projection.CheckpointManifestHash != recovery.CheckpointManifestHash)
            throw new InvalidDataException("Persisted Projection checkpoint manifest hash does not match Recovery.");
        if (projection.CheckpointSourceFingerprint != recovery.SourceFingerprint)
            throw new InvalidDataException("Persisted Projection source fingerprint does not match Recovery.");
        if (projection.ProjectionFingerprint != recovery.ProjectionFingerprint)
            throw new InvalidDataException("Persisted Projection fingerprint does not match Recovery.");
        if ((expectedConfigurationHash is not null && projection.ConfigurationHash != expectedConfigurationHash) ||
            (expectedGraphHash is not null && projection.GraphHash != expectedGraphHash))
            throw new InvalidDataException("Current configuration or graph does not match the selected dry run.");

        var evidenceStore = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
        if (!await evidenceStore.VerifyIntegrityAsync(recovery.VerificationRunId, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Verification evidence integrity verification failed.");
        var manifest = await evidenceStore.ReadManifestAsync(recovery.VerificationRunId, cancellationToken).ConfigureAwait(false);
        var pack = expectedPack ?? ResolvePersistedReportPack(manifest);
        var ledgerStore = await SqliteVerificationLedgerStore.OpenAsync(projectDirectory, recovery.VerificationLedger,
            cancellationToken).ConfigureAwait(false);
        await using (ledgerStore.ConfigureAwait(false))
        {
            var ledger = await ledgerStore.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            return await PersistedAssuranceReportBuilder.BuildAsync(projection.ProjectId, projectName, dryRunId,
                pack, manifest, evidenceStore.ReadRecordsAsync(recovery.VerificationRunId, cancellationToken),
                recovery, checkpoint, ledger, cancellationToken).ConfigureAwait(false);
        }

    }


    private static IDomainPack ResolveConfiguredReportPack(LoadedProjectConfiguration configuration)
    {
        if (configuration.Root.Packs.Count != 1)
            throw new InvalidDataException("Persisted reports require exactly one configured domain pack.");
        return CliComposition.Packs().ResolvePack(configuration.Root.Packs.Single());
    }


    private static IDomainPack ResolvePersistedReportPack(EvidenceStoreManifest manifest)
    {
        var providers = manifest.VerificationContext?.ProviderVersions
            ?.Where(pair => pair.Key != "proofshift.verification.generic").ToArray() ?? [];
        if (providers.Length != 1)
            throw new InvalidDataException("Persisted report context must identify exactly one domain pack.");
        return CliComposition.Packs().ResolvePack(new PackConfigurationDto(providers[0].Key, providers[0].Value));
    }


    private static bool IsPersistedReportFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            FormatException or InvalidDataException or VerificationRuleException or ProjectionManifestException or
            RecoveryException or SnapshotStoreException or JsonException or DbException;


    private static void WriteAssuranceReport(PersistedAssuranceReport report, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, CliOutput.JsonOptions));
            return;
        }
        Console.WriteLine("ProofShift Domain-Neutral Migration Assurance");
        Console.WriteLine();
        Console.WriteLine("EXECUTIVE SUMMARY");
        Console.WriteLine($"Project: {report.ProjectName} ({report.ProjectId})");
        Console.WriteLine($"Domain Pack: {report.PackDisplayName} ({report.PackId}@{report.PackVersion})");
        Console.WriteLine($"Claim Boundary: {report.ClaimBoundary}");
        Console.WriteLine($"Qualification: {report.Qualification}");
        Console.WriteLine($"Verification: {report.VerificationOutcome}; {report.VerificationFailureCount} failed evidence records; {report.WarningCount} warnings");
        Console.WriteLine($"Business Discrepancies: {report.BusinessDiscrepancyCount}");
        Console.WriteLine();
        Console.WriteLine("MIGRATION SCOPE");
        Console.WriteLine($"Configuration: {report.ConfigurationHash}");
        Console.WriteLine($"Migration Graph: {report.GraphHash}");
        Console.WriteLine($"Source Checkpoint: {report.CheckpointId}; {report.CheckpointManifestHash}");
        Console.WriteLine($"Source Fingerprint: {report.SourceFingerprint}");
        if (report.ProjectionRunId is not null)
            Console.WriteLine($"Projection: {report.ProjectionRunId}; {report.ProjectionFingerprint}");
        Console.WriteLine($"Recovery Projection Binding: {report.RecoveryProjectionRunId}; manifest {report.ProjectionManifestHash}");
        if (report.ObservationId is not null)
            Console.WriteLine($"Target Observation: {report.ObservationId}; {report.ObservationRunId}; {report.TargetFingerprint}");
        Console.WriteLine($"Rule Set: {report.RuleSetFingerprint}");
        Console.WriteLine($"Verification Ledger: {report.VerificationLedgerFingerprint}");
        Console.WriteLine($"Source Accounting: {report.SourceAccountingStatus} ({report.AccountedSourceCount}/{report.SourceArtifactCount}, {report.SourceAccountingGapCount} gaps)");
        Console.WriteLine($"Target Lineage: {report.TargetLineageStatus} ({report.LineagedTargetCount}/{report.TargetArtifactCount}, {report.TargetLineageGapCount} gaps)");
        Console.WriteLine();
        Console.WriteLine("FINDINGS");
        foreach (var (key, count) in report.FindingsByRuleAndCode)
            Console.WriteLine($"{key}: {count.ToString(CultureInfo.InvariantCulture)}");
        if (report.FindingsByRuleAndCode.Count == 0) Console.WriteLine("None.");
        Console.WriteLine();
        Console.WriteLine("RECOVERY READINESS");
        Console.WriteLine($"Assessment / Rehearsal: {report.RecoveryAssessmentOutcome} / {report.RecoveryRehearsalOutcome}");
        Console.WriteLine($"Assessment / Plan / Rehearsal Fingerprints: {report.RecoveryAssessmentFingerprint} / {report.RecoveryPlanFingerprint} / {report.RecoveryRehearsalFingerprint}");
        Console.WriteLine($"Recoverable Artifacts: {report.RecoverableArtifacts}/{report.AffectedRecoveryArtifacts}");
        Console.WriteLine($"Irrecoverable / Unknown Artifacts: {report.IrrecoverableArtifacts} / {report.UnknownRecoveryArtifacts}");
        Console.WriteLine($"Failed / False-Reversible Edges: {report.FailedRecoveryEdges} / {report.FalseReversibleEdges}");
        if (report.RecoveryCoveragePercentage is { } percentage)
            Console.WriteLine($"Recovery Coverage: {percentage.ToString(CultureInfo.InvariantCulture)}%");
        foreach (var reason in report.RecoveryReasons) Console.WriteLine($"Recovery Finding: {reason}");
        Console.WriteLine();
        Console.WriteLine($"VERIFICATION EVIDENCE: {report.VerificationEvidenceFingerprint}");
        Console.WriteLine($"RECOVERY EVIDENCE: {report.RecoveryEvidenceFingerprint}");
        Console.WriteLine($"DRY-RUN FINGERPRINT: {report.DryRunFingerprint}");
        Console.WriteLine($"RESULT: {(report.Qualification == "QUALIFIED" ? "QUALIFIED DRY RUN" : "NOT QUALIFIED")}");
    }


    private static void WriteAssuranceComparison(PersistedAssuranceRunComparison comparison, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(comparison, CliOutput.JsonOptions));
            return;
        }
        Console.WriteLine("ProofShift Domain-Neutral Assurance Comparison");
        Console.WriteLine($"Before: {comparison.BeforeQualification} ({comparison.BeforeDryRunId})");
        Console.WriteLine($"After: {comparison.AfterQualification} ({comparison.AfterDryRunId})");
        Console.WriteLine($"Configuration / Graph Changed: {comparison.ConfigurationHashChanged} / {comparison.GraphHashChanged}");
        Console.WriteLine($"Source Fingerprint / Checkpoint Semantics Changed: {comparison.SourceFingerprintChanged} / {comparison.CheckpointSemanticsChanged}");
        Console.WriteLine($"Projection / Observation Semantics Changed: {comparison.ProjectionSemanticsChanged} / {comparison.ObservationSemanticsChanged}");
        Console.WriteLine($"Rules / Verification Evidence Changed: {comparison.RuleSetFingerprintChanged} / {comparison.VerificationEvidenceFingerprintChanged}");
        Console.WriteLine($"Recovery Policy / Evidence / Readiness Changed: {comparison.RecoveryPolicyFingerprintChanged} / {comparison.RecoveryEvidenceFingerprintChanged} / {comparison.RecoveryReadinessChanged}");
        Console.WriteLine($"Dry-Run Fingerprint Changed: {comparison.DryRunFingerprintChanged}");
        Console.WriteLine($"Source Accounting / Target Lineage Gaps Changed: {comparison.SourceAccountingGapsChanged} / {comparison.TargetLineageGapsChanged}");
        Console.WriteLine($"Qualification Changed: {comparison.QualificationChanged}");
        foreach (var attribution in comparison.DifferenceAttribution) Console.WriteLine($"Difference Attribution: {attribution}");
        foreach (var (finding, count) in comparison.FindingsResolved) Console.WriteLine($"Resolved {finding}: {count}");
        foreach (var (finding, count) in comparison.FindingsIntroduced) Console.WriteLine($"Introduced {finding}: {count}");
    }


    internal static async Task<int> ReadRecoveryAsync(string path, string dryRunId, bool jsonOutput)
    {
        try
        {
            if (!Guid.TryParse(dryRunId, out var runGuid))
            {
                Console.Error.WriteLine("Dry-run identifier must be a GUID.");
                return 2;
            }
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var store = new FileSystemRecoveryArtifactStore(Path.Combine(projectDirectory, ".proofshift", "recovery"));
            var summary = await store.ReadSummaryAsync(new DryRunId(runGuid), CancellationToken.None).ConfigureAwait(false);
            var output = new RecoverySummaryOutput(summary.Id.Value.ToString("D", CultureInfo.InvariantCulture),
                summary.Status.ToString().ToLowerInvariant(), summary.DryRunFingerprint, summary.PolicyFingerprint,
                EffectiveRecoveryPolicy.FingerprintVersion,
                summary.AssessmentFingerprint, summary.PlanFingerprint, summary.RehearsalFingerprint,
                RecoveryAssessment.FingerprintVersion, RecoveryPlan.FingerprintVersion, RecoveryRehearsal.FingerprintVersion,
                summary.VerificationEvidenceFingerprint, summary.RecoveryEvidenceFingerprint,
                RecoveryEvidenceGraph.FormatVersion, DryRunQualification.FingerprintVersion, summary.ExecutedEdges,
                summary.ReverseEdges, summary.RestoreEdges, summary.CompensateEdges, summary.IrreversibleEdges,
                summary.AffectedArtifacts, summary.RecoverableArtifacts, summary.IrrecoverableArtifacts,
                summary.UnknownArtifacts, summary.RecoverablePercentage, summary.FailedEdges,
                summary.RehearsalOutcome.ToLowerInvariant(), summary.BySemanticType, summary.Reasons);
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(output, CliOutput.JsonOptions));
            else
            {
                Console.WriteLine("ProofShift Recovery Readiness");
                Console.WriteLine($"Dry Run: {summary.Id.Value:D}");
                Console.WriteLine($"Qualification: {summary.Status.ToString().ToUpperInvariant()}");
                Console.WriteLine($"Recovery Edges: {summary.ExecutedEdges}");
                Console.WriteLine($"Reverse / Restore / Compensate / Irreversible: {summary.ReverseEdges} / {summary.RestoreEdges} / {summary.CompensateEdges} / {summary.IrreversibleEdges}");
                Console.WriteLine($"Recoverable Artifacts: {summary.RecoverableArtifacts}/{summary.AffectedArtifacts}");
                Console.WriteLine($"Irrecoverable / Unknown Artifacts: {summary.IrrecoverableArtifacts} / {summary.UnknownArtifacts}");
                if (summary.RecoverablePercentage is { } percentage) Console.WriteLine($"Recovery Coverage: {percentage.ToString(CultureInfo.InvariantCulture)}%");
                Console.WriteLine($"Failed Edges: {summary.FailedEdges}");
                Console.WriteLine($"Rehearsal: {summary.RehearsalOutcome.ToUpperInvariant()}");
                Console.WriteLine($"Dry Run Fingerprint: {summary.DryRunFingerprint}");
                Console.WriteLine("Integrity: VERIFIED");
                foreach (var coverage in summary.BySemanticType)
                    Console.WriteLine($"{coverage.SemanticType}: {coverage.RecoverableArtifacts}/{coverage.AffectedArtifacts} recoverable");
                foreach (var reason in summary.Reasons) Console.WriteLine($"Finding: {reason}");
            }
            return summary.Status == DryRunQualificationStatus.Qualified ? 0 : 1;
        }
        catch
        {
            Console.Error.WriteLine("Recovery artifacts could not be read or failed integrity verification.");
            return 1;
        }
    }


    internal static async Task<int> ReadEvidenceAsync(string path, string verificationRunId, bool jsonOutput)
    {
        try
        {
            if (!Guid.TryParse(verificationRunId, out var runGuid))
            {
                Console.Error.WriteLine("Verification run identifier must be a GUID.");
                return 2;
            }

            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var store = new FileSystemEvidenceStore(Path.Combine(projectDirectory, ".proofshift", "verifications"));
            if (!await store.VerifyIntegrityAsync(new RunId(runGuid), CancellationToken.None).ConfigureAwait(false))
            {
                Console.Error.WriteLine("Evidence integrity verification failed.");
                return 1;
            }

            var runId = new RunId(runGuid);
            var manifest = await store.ReadManifestAsync(runId, CancellationToken.None).ConfigureAwait(false);
            if (jsonOutput)
            {
                await using var output = Console.OpenStandardOutput();
                await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WritePropertyName("manifest");
                JsonSerializer.Serialize(writer, manifest, CliOutput.JsonOptions);
                writer.WritePropertyName("records");
                writer.WriteStartArray();
                await foreach (var record in store.ReadRecordsAsync(runId, CancellationToken.None).ConfigureAwait(false))
                    record.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("ProofShift Evidence Graph");
                Console.WriteLine($"Run: {runGuid:D}");
                Console.WriteLine($"Fingerprint: {manifest.Fingerprint}");
                Console.WriteLine($"Records: {manifest.RecordCount.ToString(CultureInfo.InvariantCulture)}");
                Console.WriteLine("Integrity: VERIFIED");
            }
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Evidence graph could not be read.");
            return 1;
        }
    }


}
