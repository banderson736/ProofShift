using System.Text.Json;
using System.Globalization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Projection;

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
        if (args.Length is < 2 or > 3)
        {
            WriteUsage();
            return 2;
        }

        var jsonOutput = args.Length == 3 && string.Equals(args[2], "--json", StringComparison.OrdinalIgnoreCase);
        if (args.Length == 3 && !jsonOutput)
        {
            WriteUsage();
            return 2;
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
            return await ProjectAsync(args[1], jsonOutput).ConfigureAwait(false);
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

    private static async Task<int> ProjectAsync(string path, bool jsonOutput)
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
            var service = new ShadowProjectionService(sourceConnectors, targetConnectors);
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                ?? throw new InvalidOperationException("Project configuration directory is unavailable.");
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

    private static void WriteProjectionHuman(string? projectName, ProjectionRun run)
    {
        Console.WriteLine("ProofShift Shadow Projection");
        Console.WriteLine();
        Console.WriteLine($"Project: {projectName ?? run.ProjectId}");
        Console.WriteLine($"Run: {run.Id.Value:D}");
        Console.WriteLine($"Status: {run.Status.ToString().ToUpperInvariant()}");
        Console.WriteLine($"Configuration Hash: {run.ConfigurationHash}");
        Console.WriteLine($"Migration Graph Hash: {run.GraphHash}");
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
        run.JournalPath);

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
        Console.Error.WriteLine("Usage: proofshift validate <config> [--json] | proofshift plan <config> [--json] | proofshift inspect <config> [--json] | proofshift project <config> [--json]");

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
        string ProjectionJournal);

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
