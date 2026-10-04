using System.Text.Json;
using System.Globalization;
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
using ProofShift.Snapshots;
using ProofShift.Domain;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            WriteUsage();
            return 2;
        }

        var jsonOutput = false;
        string? checkpoint = null;
        string? projection = null;
        string? verificationRun = null;
        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
            }
            else if (string.Equals(args[index], "--checkpoint", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                checkpoint = args[++index];
            }
            else if (string.Equals(args[index], "--projection", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                projection = args[++index];
            }
            else if (string.Equals(args[index], "--run", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                verificationRun = args[++index];
            }
            else
            {
                WriteUsage();
                return 2;
            }
        }

        if (string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            return await ValidateConfigurationAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "plan", StringComparison.OrdinalIgnoreCase))
        {
            return await CompilePlanAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
        {
            return await InspectSourcesAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "project", StringComparison.OrdinalIgnoreCase))
        {
            return await ProjectAsync(args[1], jsonOutput, checkpoint).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "snapshot", StringComparison.OrdinalIgnoreCase) && checkpoint is null)
        {
            return await SnapshotAsync(args[1], jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase) && checkpoint is not null && projection is not null)
        {
            return await VerifyAsync(args[1], checkpoint, projection, jsonOutput).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "evidence", StringComparison.OrdinalIgnoreCase) && verificationRun is not null)
        {
            return await ReadEvidenceAsync(args[1], verificationRun, jsonOutput).ConfigureAwait(false);
        }

        WriteUsage();
        return 2;
    }

    private static async Task<int> ValidateConfigurationAsync(string path, bool jsonOutput)
    {
        ConfigurationLoadResult result;
        try
        {
            result = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Configuration validation could not be completed.");
            return 1;
        }

        if (jsonOutput)
        {
            WriteJson(result);
        }
        else
        {
            WriteHumanReadable(result);
        }

        return result.IsValid ? 0 : 1;
    }

    private static async Task<int> CompilePlanAsync(string path, bool jsonOutput)
    {
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                var output = CreatePlanOutput(
                    valid: false,
                    graphVersion: null,
                    graphHash: null,
                    summary: null,
                    loaded.Issues.Select(ToJsonIssue));
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
                }
                else
                {
                    WritePlanHuman(null, output);
                }

                return 1;
            }

            var result = MigrationGraphCompiler.Compile(loaded.Configuration);
            var planOutput = CreatePlanOutput(
                result.IsValid,
                result.GraphVersion,
                result.GraphHash,
                result.Summary,
                result.Issues.Select(ToJsonIssue));
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(planOutput, JsonOptions));
            }
            else
            {
                WritePlanHuman(loaded.Configuration.Root.Project?.Name, planOutput);
            }

            return result.IsValid ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Migration graph compilation could not be completed.");
            return 1;
        }
    }

    private static async Task<int> InspectSourcesAsync(string path, bool jsonOutput)
    {
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                var invalidConfiguration = new InspectionOutput(
                    false,
                    null,
                    [],
                    loaded.Issues.Select(ToJsonIssue));
                WriteInspection(invalidConfiguration, jsonOutput);
                return 1;
            }

            var compilation = MigrationGraphCompiler.Compile(loaded.Configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                var invalidGraph = new InspectionOutput(
                    false,
                    loaded.Configuration.Root.Project?.Name,
                    [],
                    compilation.Issues.Select(ToJsonIssue));
                WriteInspection(invalidGraph, jsonOutput);
                return 1;
            }

            var registry = new ConnectorRegistry(
            [
                new PostgresSourceConnector(),
                new SqlServerSourceConnector(),
                new FilesystemSourceConnector(),
                new CsvSourceConnector()
            ]);
            var report = await new SourceInspectionService(registry)
                .InspectAsync(loaded.Configuration, compilation.Graph)
                .ConfigureAwait(false);
            var sources = report.Sources.Select(source => new SourceNodeInspectionOutput(
                source.NodeKey,
                source.SystemKey,
                source.EndpointKey,
                source.Connector.Value,
                source.Selector.Kind,
                source.Inspection.Status.ToString().ToLowerInvariant(),
                source.Inspection.PhysicalObject,
                source.Inspection.IdentityFields,
                source.Inspection.PrimaryKeyFields,
                source.Inspection.Columns.Select(column => new SourceColumnOutput(
                    column.Name,
                    column.DataType,
                    column.IsNullable,
                    column.Ordinal)).ToArray(),
                source.Inspection.EstimatedRecords,
                source.Inspection.Files,
                source.Inspection.Bytes,
                source.Inspection.Issues.Select(issue => new ValidationIssueOutput(
                    issue.Code,
                    issue.Severity.ToString().ToLowerInvariant(),
                    issue.Location,
                    null,
                    issue.Message))));
            var output = new InspectionOutput(report.IsValid, report.ProjectName, sources, []);
            WriteInspection(output, jsonOutput);
            return report.IsValid ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Source inspection could not be completed.");
            return 1;
        }
    }

    private static async Task<int> SnapshotAsync(string path, bool jsonOutput)
    {
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                Console.Error.WriteLine("Configuration validation failed; checkpoint capture was not started.");
                return 1;
            }

            var compilation = MigrationGraphCompiler.Compile(loaded.Configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                Console.Error.WriteLine("Migration graph validation failed; checkpoint capture was not started.");
                return 1;
            }

            var registry = new ConnectorRegistry(
            [
                new PostgresSourceConnector(),
                new SqlServerSourceConnector(),
                new FilesystemSourceConnector(),
                new CsvSourceConnector()
            ]);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var storePath = Path.Combine(projectDirectory, ".proofshift", "checkpoints");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            SnapshotCaptureResult result;
            try
            {
                result = await new SnapshotCaptureService(registry, new FileSystemSnapshotStore(storePath))
                    .CaptureAsync(loaded.Configuration, compilation.Graph, cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            var output = new SnapshotOutput(
                result.Id.Value.ToString("N", CultureInfo.InvariantCulture),
                result.Status.ToString().ToLowerInvariant(),
                result.Checkpoint?.ManifestHash,
                result.Checkpoint?.SourceFingerprint,
                result.Checkpoint?.CrossSystemAtomic,
                result.Checkpoint?.CaptureWindow.TotalMilliseconds,
                result.ExpectedSourceNodes,
                result.CapturedSourceNodes,
                result.CapturedArtifacts,
                result.CapturedBytes,
                result.FailureCode,
                result.Status == CheckpointStatus.Complete
                    ? Path.Combine(storePath, result.Id.Value.ToString("N", CultureInfo.InvariantCulture))
                    : null);
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            }
            else
            {
                Console.WriteLine("ProofShift Source Checkpoint");
                Console.WriteLine($"Checkpoint: {output.CheckpointId}");
                Console.WriteLine($"Status: {output.Status.ToUpperInvariant()}");
                Console.WriteLine($"Source Nodes: {output.CapturedSourceNodes}/{output.ExpectedSourceNodes}");
                Console.WriteLine($"Artifacts: {output.CapturedArtifacts}");
                Console.WriteLine($"Materialized Bytes: {output.CapturedBytes}");
                if (output.CheckpointPath is not null) Console.WriteLine($"Path: {output.CheckpointPath}");
                if (output.FailureCode is not null) Console.WriteLine($"Failure: {output.FailureCode}");
                if (output.CrossSystemAtomic is false) Console.WriteLine("Consistency: endpoint guarantees only; cross-system atomicity is not claimed.");
            }

            return result.Status == CheckpointStatus.Complete ? 0 : result.Status == CheckpointStatus.Cancelled ? 130 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Source checkpoint could not be completed.");
            return 1;
        }
    }

    private static async Task<int> ProjectAsync(string path, bool jsonOutput, string? checkpointPath)
    {
        ProjectionRun? run = null;
        try
        {
            var loaded = await new ConfigurationLoader().LoadAsync(path).ConfigureAwait(false);
            if (loaded.Configuration is null)
            {
                Console.Error.WriteLine("Configuration validation failed; projection was not started.");
                return 1;
            }

            var compilation = MigrationGraphCompiler.Compile(loaded.Configuration);
            if (!compilation.IsValid || compilation.Graph is null)
            {
                Console.Error.WriteLine("Migration graph validation failed; projection was not started.");
                return 1;
            }

            var sourceConnectors = new ConnectorRegistry(
            [
                new PostgresSourceConnector(),
                new SqlServerSourceConnector(),
                new FilesystemSourceConnector(),
                new CsvSourceConnector()
            ]);
            var targetConnectors = new ShadowTargetConnectorRegistry(
            [
                new PostgresShadowTargetConnector(),
                new FilesystemShadowTargetConnector()
            ]);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
            var store = new FileSystemSnapshotStore(Path.Combine(projectDirectory, ".proofshift", "checkpoints"));
            await using var loadedCheckpoint = checkpointPath is null
                ? null
                : await store.OpenCompleteAsync(checkpointPath, CancellationToken.None).ConfigureAwait(false);
            var sourceProvider = loadedCheckpoint is null ? null : new CheckpointSourceArtifactStreamProvider(loadedCheckpoint);
            var service = new ShadowProjectionService(sourceConnectors, targetConnectors, sourceProvider: sourceProvider);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                run = await service.ProjectAsync(loaded.Configuration, compilation.Graph, projectDirectory, cancellation.Token).ConfigureAwait(false);
                await ProjectionRunManifestStore.WriteAsync(projectDirectory, run, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(ToProjectionOutput(run), JsonOptions));
            }
            else
            {
                WriteProjectionHuman(loaded.Configuration.Root.Project?.Name, run);
            }

            return run.Status == ProjectionStatus.Succeeded ? 0 : run.Status == ProjectionStatus.Cancelled ? 130 : 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch
        {
            if (jsonOutput && run is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(ToProjectionOutput(run), JsonOptions));
            }
            else
            {
                Console.Error.WriteLine("Shadow projection could not be completed.");
            }

            return 1;
        }
    }

    private static async Task<int> VerifyAsync(string path, string checkpointId, string projectionRunId, bool jsonOutput)
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
            var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider(), new PensionPack()]);
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
            if (jsonOutput) Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
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

    private static async Task<int> ReadEvidenceAsync(string path, string verificationRunId, bool jsonOutput)
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

            await using var stream = await store.OpenReadAsync(new RunId(runGuid), CancellationToken.None).ConfigureAwait(false);
            if (jsonOutput)
            {
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(document.RootElement, JsonOptions));
            }
            else
            {
                using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
                Console.WriteLine("ProofShift Evidence Graph");
                Console.WriteLine($"Run: {runGuid:D}");
                Console.WriteLine($"Fingerprint: {document.RootElement.GetProperty("fingerprint").GetString()}");
                Console.WriteLine($"Records: {document.RootElement.GetProperty("records").GetArrayLength()}");
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

    private static void WriteProjectionHuman(string? projectName, ProjectionRun run)
    {
        Console.WriteLine("ProofShift Shadow Projection");
        Console.WriteLine();
        Console.WriteLine($"Project: {projectName ?? run.ProjectId}");
        Console.WriteLine($"Run: {run.Id.Value:D}");
        Console.WriteLine($"Status: {run.Status.ToString().ToUpperInvariant()}");
        Console.WriteLine($"Configuration Hash: {run.ConfigurationHash}");
        Console.WriteLine($"Migration Graph Hash: {run.GraphHash}");
        if (run.CheckpointId is not null)
        {
            Console.WriteLine($"Source Checkpoint: {run.CheckpointId}");
            Console.WriteLine($"Checkpoint Source Fingerprint: {run.CheckpointSourceFingerprint}");
            Console.WriteLine($"Checkpoint Manifest Hash: {run.CheckpointManifestHash}");
        }
        Console.WriteLine();
        Console.WriteLine("Connectors");
        foreach (var connector in run.ConnectorVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{connector.Key}: {connector.Value}");
        }

        Console.WriteLine("Shadow Destinations");
        foreach (var destination in run.ShadowDestinations)
        {
            Console.WriteLine(destination);
        }

        Console.WriteLine();
        Console.WriteLine("Projection Journal");
        Console.WriteLine($"Source Artifacts: {run.SourceArtifactCount}");
        Console.WriteLine($"Target Artifacts: {run.TargetArtifactCount}");
        Console.WriteLine($"Failures: {run.FailureCount}");
        if (run.FailureCode is not null)
        {
            Console.WriteLine($"Failure Code: {run.FailureCode}");
        }
        Console.WriteLine($"Journal: {run.JournalPath}");
        if (run.Fingerprint is not null)
        {
            Console.WriteLine($"Projection Fingerprint ({run.FingerprintVersion}): {run.Fingerprint}");
        }

        Console.WriteLine();
        Console.WriteLine(run.Status == ProjectionStatus.Succeeded ? "RESULT: PROJECTED" : $"RESULT: {run.Status.ToString().ToUpperInvariant()}");
    }

    private static ProjectionOutput ToProjectionOutput(ProjectionRun run) => new(
        run.Id.Value.ToString("D", CultureInfo.InvariantCulture),
        run.Type.ToString().ToLowerInvariant(),
        run.Status.ToString().ToLowerInvariant(),
        run.ProjectId,
        run.ConfigurationHash,
        run.GraphHash,
        run.StartedAt,
        run.CompletedAt,
        run.ConnectorVersions,
        run.ShadowDestinations,
        run.SourceArtifactCount,
        run.TargetArtifactCount,
        run.FailureCount,
        run.FailureCode,
        run.Fingerprint,
        run.FingerprintVersion,
        run.JournalPath,
        run.CheckpointId,
        run.CheckpointSourceFingerprint,
        run.CheckpointManifestHash);

    private static void WriteInspection(InspectionOutput output, bool jsonOutput)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            return;
        }

        Console.WriteLine("ProofShift Source Inspection");
        Console.WriteLine();
        if (output.Project is not null)
        {
            Console.WriteLine($"Project: {output.Project}");
            Console.WriteLine();
        }

        Console.WriteLine("Sources");
        Console.WriteLine();
        foreach (var source in output.Sources)
        {
            Console.WriteLine(source.NodeKey);
            Console.WriteLine($"System: {source.System}");
            Console.WriteLine($"Endpoint: {source.Endpoint}");
            Console.WriteLine($"Connector: {source.Connector}");
            Console.WriteLine($"Selector: {source.SelectorKind}");
            if (source.PhysicalObject is not null)
            {
                Console.WriteLine($"Physical Object: {source.PhysicalObject}");
            }

            Console.WriteLine($"Status: {source.Status.ToUpperInvariant()}");
            if (source.IdentityFields.Count > 0)
            {
                Console.WriteLine($"Identity: {string.Join(", ", source.IdentityFields)}");
            }

            Console.WriteLine($"Columns: {source.Columns.Count}");
            if (source.EstimatedRecords is not null)
            {
                Console.WriteLine($"Estimated Records: {source.EstimatedRecords}");
            }

            if (source.Files is not null)
            {
                Console.WriteLine($"Files: {source.Files}");
            }

            if (source.Bytes is not null)
            {
                Console.WriteLine($"Bytes: {source.Bytes}");
            }

            foreach (var issue in source.Issues)
            {
                Console.WriteLine($"{issue.Severity.ToUpperInvariant()} {issue.Code}: {issue.Message}");
            }

            Console.WriteLine();
        }

        foreach (var issue in output.Issues)
        {
            Console.WriteLine($"{issue.Severity.ToUpperInvariant()} {issue.Code}");
            if (issue.File is not null)
            {
                Console.WriteLine(issue.File);
            }

            if (issue.Path is not null)
            {
                Console.WriteLine($"Path: {issue.Path}");
            }

            Console.WriteLine(issue.Message);
            Console.WriteLine();
        }

        Console.WriteLine("OVERALL RESULT:");
        Console.WriteLine(output.Valid ? "VALID" : "INVALID");
    }

    private static void WriteJson(ConfigurationLoadResult result)
    {
        var issues = result.Issues.Select(issue => new ValidationIssueOutput(
            issue.Code,
            issue.Severity.ToString().ToLowerInvariant(),
            issue.File,
            issue.Path,
            issue.Message));
        var output = new ValidationOutput(
            result.IsValid,
            result.ConfigurationVersion,
            result.Configuration?.CanonicalizationVersion,
            result.Configuration?.ConfigurationHash,
            issues);
        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void WriteHumanReadable(ConfigurationLoadResult result)
    {
        Console.WriteLine("ProofShift Configuration Validation");
        Console.WriteLine();

        if (result.Configuration is { } configuration)
        {
            Console.WriteLine($"Project: {configuration.Root.Project?.Name}");
            Console.WriteLine();
            Console.WriteLine($"Configuration Version: {configuration.Root.Version}");
            Console.WriteLine($"Canonicalization Version: {configuration.CanonicalizationVersion}");
            Console.WriteLine();
            Console.WriteLine($"Systems: {configuration.Systems.Count}");
            Console.WriteLine($"Storage Endpoints: {configuration.Systems.Sum(system => system.StorageEndpoints.Count)}");
            Console.WriteLine($"Migration Graph: {configuration.Root.MigrationGraphFile}");
            Console.WriteLine($"Verification Rules: {configuration.Root.VerificationRulesFile}");
            Console.WriteLine($"Recovery Policy: {configuration.Root.RecoveryPolicyFile}");
            Console.WriteLine();
            Console.WriteLine($"Configuration Hash: {configuration.ConfigurationHash}");
            Console.WriteLine();
        }
        else if (result.ConfigurationVersion is not null)
        {
            Console.WriteLine($"Configuration Version: {result.ConfigurationVersion}");
            Console.WriteLine();
        }

        foreach (var issue in result.Issues)
        {
            var severity = issue.Severity.ToString().ToUpperInvariant();
            Console.WriteLine($"{severity} {issue.Code}");
            if (issue.File is not null)
            {
                Console.WriteLine(issue.File);
            }

            if (issue.Path is not null)
            {
                Console.WriteLine($"Path: {issue.Path}");
            }

            Console.WriteLine(issue.Message);
            Console.WriteLine();
        }

        Console.WriteLine("RESULT:");
        Console.WriteLine(result.IsValid ? "VALID" : "INVALID");
    }

    private static PlanValidationOutput CreatePlanOutput(
        bool valid,
        int? graphVersion,
        string? graphHash,
        GraphCompilationSummary? summary,
        IEnumerable<ValidationIssueOutput> issues) =>
        new(
            valid,
            graphVersion,
            GraphCanonicalizer.FormatVersion,
            graphHash,
            summary?.Nodes ?? 0,
            summary?.Edges ?? 0,
            summary?.SourceNodes ?? 0,
            summary?.TargetNodes ?? 0,
            new SourceCoverageOutput(summary?.SourceAccounted ?? 0, summary?.SourceNodes ?? 0),
            new TargetProvenanceOutput(summary?.TargetConnected ?? 0, summary?.TargetNodes ?? 0),
            summary?.RecoveryDefinitions ?? 0,
            summary?.OperationCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, int>(StringComparer.Ordinal),
            issues);

    private static void WritePlanHuman(string? projectName, PlanValidationOutput output)
    {
        Console.WriteLine("ProofShift Migration Graph");
        Console.WriteLine();
        if (projectName is not null)
        {
            Console.WriteLine($"Project: {projectName}");
            Console.WriteLine();
        }

        Console.WriteLine($"Graph Version: {output.GraphVersion?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN"}");
        Console.WriteLine($"Canonicalization Version: {output.CanonicalizationVersion}");
        Console.WriteLine($"Nodes: {output.Nodes}");
        Console.WriteLine($"Source Nodes: {output.SourceNodes}");
        Console.WriteLine($"Target Nodes: {output.TargetNodes}");
        Console.WriteLine($"Edges: {output.Edges}");
        Console.WriteLine("Operations:");
        foreach (var (operation, count) in output.OperationCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {operation}: {count}");
        }

        Console.WriteLine($"Source Coverage: {output.SourceCoverage.Accounted} / {output.SourceCoverage.Total}");
        Console.WriteLine($"Target Provenance: {output.TargetProvenance.Connected} / {output.TargetProvenance.Total}");
        Console.WriteLine($"Recovery Definitions: {output.RecoveryDefinitions} / {output.Edges}");
        if (output.GraphHash is not null)
        {
            Console.WriteLine($"Graph Hash: {output.GraphHash}");
        }

        Console.WriteLine();
        foreach (var issue in output.Issues)
        {
            Console.WriteLine($"{issue.Severity.ToUpperInvariant()} {issue.Code}");
            if (issue.File is not null)
            {
                Console.WriteLine(issue.File);
            }

            if (issue.Path is not null)
            {
                Console.WriteLine($"Path: {issue.Path}");
            }

            Console.WriteLine(issue.Message);
            Console.WriteLine();
        }

        Console.WriteLine("RESULT:");
        Console.WriteLine(output.Valid ? "VALID" : "INVALID");
    }

    private static ValidationIssueOutput ToJsonIssue(ConfigurationValidationIssue issue) => new(
        issue.Code,
        issue.Severity.ToString().ToLowerInvariant(),
        issue.File,
        issue.Path,
        issue.Message);

    private static ValidationIssueOutput ToJsonIssue(GraphValidationIssue issue) => new(
        issue.Code,
        issue.Severity.ToString().ToLowerInvariant(),
        issue.File,
        issue.Path,
        issue.Message);

    private static void WriteUsage() =>
        Console.Error.WriteLine("Usage: proofshift validate <config> [--json] | proofshift plan <config> [--json] | proofshift inspect <config> [--json] | proofshift snapshot <config> [--json] | proofshift project <config> [--checkpoint <id-or-path>] [--json] | proofshift verify <config> --checkpoint <id> --projection <run-id> [--json] | proofshift evidence <config> --run <verification-run-id> [--json]");

    private sealed record SnapshotOutput(
        string CheckpointId,
        string Status,
        string? ManifestHash,
        string? SourceFingerprint,
        bool? CrossSystemAtomic,
        double? CaptureWindowMilliseconds,
        int ExpectedSourceNodes,
        int CapturedSourceNodes,
        long CapturedArtifacts,
        long CapturedBytes,
        string? FailureCode,
        string? CheckpointPath);

    private sealed record ValidationOutput(
        bool Valid,
        int? ConfigurationVersion,
        string? CanonicalizationVersion,
        string? ConfigurationHash,
        IEnumerable<ValidationIssueOutput> Issues);

    private sealed record ValidationIssueOutput(
        string Code,
        string Severity,
        string? File,
        string? Path,
        string Message);

    private sealed record SourceCoverageOutput(int Accounted, int Total);

    private sealed record TargetProvenanceOutput(int Connected, int Total);

    private sealed record PlanValidationOutput(
        bool Valid,
        int? GraphVersion,
        string CanonicalizationVersion,
        string? GraphHash,
        int Nodes,
        int Edges,
        int SourceNodes,
        int TargetNodes,
        SourceCoverageOutput SourceCoverage,
        TargetProvenanceOutput TargetProvenance,
        int RecoveryDefinitions,
        IReadOnlyDictionary<string, int> OperationCounts,
        IEnumerable<ValidationIssueOutput> Issues);

    private sealed record ProjectionOutput(
        string RunId,
        string RunType,
        string Status,
        string ProjectId,
        string ConfigurationHash,
        string MigrationGraphHash,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        IReadOnlyDictionary<string, string> ConnectorVersions,
        IReadOnlyCollection<string> ShadowDestinations,
        long SourceArtifactCount,
        long TargetArtifactCount,
        long FailureCount,
        string? FailureCode,
        string? ProjectionFingerprint,
        string ProjectionFingerprintVersion,
        string ProjectionJournal,
        string? CheckpointId,
        string? CheckpointSourceFingerprint,
        string? CheckpointManifestHash);

    private sealed record VerificationOutput(
        string RunId,
        string Outcome,
        string ConfigurationHash,
        string GraphHash,
        string CheckpointId,
        string ProjectionRunId,
        string ProjectionManifestHash,
        string RuleSetFingerprint,
        string EvidenceFingerprint,
        int Findings,
        int FailedRules,
        int WarningRules,
        string EvidencePath);

    private sealed record SourceColumnOutput(string Name, string DataType, bool Nullable, int? Ordinal);

    private sealed record SourceNodeInspectionOutput(
        string NodeKey,
        string System,
        string Endpoint,
        string Connector,
        string SelectorKind,
        string Status,
        string? PhysicalObject,
        IReadOnlyList<string> IdentityFields,
        IReadOnlyList<string> PrimaryKeyFields,
        IReadOnlyList<SourceColumnOutput> Columns,
        long? EstimatedRecords,
        long? Files,
        long? Bytes,
        IEnumerable<ValidationIssueOutput> Issues);

    private sealed record InspectionOutput(
        bool Valid,
        string? Project,
        IEnumerable<SourceNodeInspectionOutput> Sources,
        IEnumerable<ValidationIssueOutput> Issues);
}
