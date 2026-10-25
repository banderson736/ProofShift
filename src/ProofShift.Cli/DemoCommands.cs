using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ProofShift.Cli;

internal static class DemoCommands
{
    private const string WorkspaceMarkerName = ".proofshift-demo-owned.json";
    private const string WorkspaceResultName = "demo-result.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteUsage();
            return args.Count == 0 ? 2 : 0;
        }

        var command = args[0].ToLowerInvariant();
        if (!TryReadJsonFlag(args.Skip(1).ToArray(), command is "list", out var commandArgs, out var jsonOutput))
        {
            WriteUsage();
            return 2;
        }

        try
        {
            var catalog = LoadCatalog();
            return command switch
            {
                "list" when commandArgs.Length == 0 => List(catalog, jsonOutput),
                "describe" when commandArgs.Length == 1 => Describe(catalog, commandArgs[0], jsonOutput),
                "preflight" when commandArgs.Length == 1 => await PreflightAsync(catalog, commandArgs[0], jsonOutput).ConfigureAwait(false),
                "run" when commandArgs.Length == 1 => await RunScenarioAsync(catalog, commandArgs[0], jsonOutput).ConfigureAwait(false),
                "reset" when commandArgs.Length == 1 => await ResetScenarioAsync(catalog, commandArgs[0], jsonOutput).ConfigureAwait(false),
                _ => UsageError()
            };
        }
        catch (DemoManifestException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Demo operation cancelled; scenario-owned resources may remain. Run demo reset after inspecting the workspace.");
            return 130;
        }
        catch
        {
            Console.Error.WriteLine("Demo operation failed. Inspect the scenario workspace before retrying or resetting it.");
            return 1;
        }
    }

    private static bool TryReadJsonFlag(string[] args, bool allowNoArguments, out string[] commandArgs, out bool jsonOutput)
    {
        jsonOutput = args.Length > 0 && StringComparer.OrdinalIgnoreCase.Equals(args[^1], "--json");
        commandArgs = jsonOutput ? args[..^1] : args;
        return commandArgs.All(argument => !argument.StartsWith("--", StringComparison.Ordinal)) &&
            (allowNoArguments || commandArgs.Length > 0);
    }

    private static int UsageError()
    {
        WriteUsage();
        return 2;
    }

    private static DemoScenarioCatalog LoadCatalog()
    {
        var currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (Directory.Exists(Path.Combine(currentDirectory, "scenarios", "demos")))
            return DemoScenarioCatalog.Load(currentDirectory);

        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (Directory.Exists(Path.Combine(applicationDirectory, "scenarios", "demos")))
            return DemoScenarioCatalog.Load(applicationDirectory);
        return DemoScenarioCatalog.Load(currentDirectory);
    }

    private static int List(DemoScenarioCatalog catalog, bool jsonOutput)
    {
        var scenarios = catalog.Scenarios.Select(entry => new DemoListItem(entry.Manifest.Id,
            entry.Manifest.DisplayName, entry.Manifest.Domain, entry.Manifest.Description,
            entry.Manifest.ApproximateRuntime, entry.Manifest.Requirements.Docker,
            entry.Manifest.RequiredServices)).ToArray();
        if (jsonOutput)
        {
            WriteJson(scenarios);
            return 0;
        }

        Console.WriteLine("ProofShift Evaluation Scenarios");
        if (scenarios.Length == 0)
        {
            Console.WriteLine("No scenarios are installed in this evaluation package.");
            return 0;
        }

        foreach (var scenario in scenarios)
            Console.WriteLine($"{scenario.Id,-28} {scenario.Domain,-22} {scenario.ApproximateRuntime,-18} Docker: {(scenario.DockerRequired ? "required" : "not required")}  Services: {(scenario.RequiredServices.Count == 0 ? "none" : string.Join(", ", scenario.RequiredServices))}  {scenario.DisplayName}");
        return 0;
    }

    private static int Describe(DemoScenarioCatalog catalog, string scenarioId, bool jsonOutput)
    {
        var entry = catalog.Find(scenarioId);
        var accepted = catalog.ReadAcceptanceReference(entry);
        var output = new DemoScenarioDescription(entry.Manifest.Id, entry.Manifest.DisplayName, entry.Manifest.Domain,
            entry.Manifest.Description, entry.Manifest.ClaimBoundary, entry.Manifest.Pack.Id, entry.Manifest.Pack.Version,
            entry.Manifest.ApproximateRuntime, entry.Manifest.SourceSystems, entry.Manifest.TargetSystems,
            entry.Manifest.ArtifactScale, entry.Manifest.Requirements, entry.Manifest.RequiredServices, entry.Manifest.ProjectPath,
            entry.Manifest.FixtureSetupSteps, entry.Manifest.DefectiveTargetPreparation,
            entry.Manifest.CorrectedTargetPreparation, DescribeAcceptedOutcome(accepted),
            entry.Manifest.ReportPaths, entry.Manifest.ResetScope, entry.Manifest.ResetStages.Select(stage => stage.Name).ToArray());

        if (jsonOutput)
        {
            WriteJson(output);
            return 0;
        }

        Console.WriteLine(output.DisplayName);
        Console.WriteLine($"ID: {output.Id}");
        Console.WriteLine($"Domain: {output.Domain}");
        Console.WriteLine($"Pack: {output.PackId} {output.PackVersion}");
        Console.WriteLine($"What it proves: {output.Description}");
        Console.WriteLine($"Claim boundary: {output.ClaimBoundary}");
        Console.WriteLine($"Approximate local runtime: {output.ApproximateRuntime}");
        Console.WriteLine($"Docker: {(output.Requirements.Docker ? "required" : "not required")}");
        Console.WriteLine($"Required services: {(output.RequiredServices.Count == 0 ? "none" : string.Join(", ", output.RequiredServices))}");
        Console.WriteLine($"Source / target artifacts: {output.ArtifactScale.SourceArtifacts} / {output.ArtifactScale.TargetArtifacts} {output.ArtifactScale.Unit}");
        Console.WriteLine($"Expected defective state: {output.AcceptedOutcome.DefectiveQualification}, {output.AcceptedOutcome.SeededDefects} seeded defects / {output.AcceptedOutcome.ExpectedFindings} findings");
        Console.WriteLine($"Expected corrected state: {output.AcceptedOutcome.CorrectedFailures} failures, {output.AcceptedOutcome.UnaccountedSources} unaccounted, {output.AcceptedOutcome.UnexplainedTargets} unexplained, Recovery {output.AcceptedOutcome.RecoveryAssessment}, {output.AcceptedOutcome.CorrectedQualification}");
        if (output.AcceptedOutcome.FalseReverseDeclarations is { } falseReverseCount)
            Console.WriteLine($"Recovery proof: {falseReverseCount} false-Reverse declarations are independently rejected even when Verification passes.");
        Console.WriteLine($"Required ports: {(output.Requirements.RequiredPorts.Count == 0 ? "none" : string.Join(", ", output.Requirements.RequiredPorts))}");
        Console.WriteLine($"Project: {output.ProjectPath ?? "constructed by the scenario fixture"}");
        foreach (var report in output.ReportPaths) Console.WriteLine($"Report: {report}");
        return 0;
    }

    private static async Task<int> PreflightAsync(DemoScenarioCatalog catalog, string scenarioId, bool jsonOutput)
    {
        var entry = catalog.Find(scenarioId);
        var result = await RunPreflightAsync(catalog, entry, CancellationToken.None).ConfigureAwait(false);
        WritePreflight(result, jsonOutput);
        return result.Ready ? 0 : 1;
    }

    private static async Task<int> RunScenarioAsync(DemoScenarioCatalog catalog, string scenarioId, bool jsonOutput)
    {
        var entry = catalog.Find(scenarioId);
        var preflight = await RunPreflightAsync(catalog, entry, CancellationToken.None).ConfigureAwait(false);
        if (!preflight.Ready)
        {
            if (jsonOutput) WriteJson(new DemoRunResult(entry.Manifest.Id, "preflight-failed", string.Empty, [], [], preflight.Checks));
            else WritePreflight(preflight, jsonOutput: false);
            return 1;
        }

        var accepted = catalog.ReadAcceptanceReference(entry);
        var workspace = GetWorkspacePath(catalog.ContentRoot, entry.Manifest.Id);
        if (File.Exists(workspace) || Directory.Exists(workspace))
        {
            Console.Error.WriteLine($"A scenario workspace already exists. Review it, then run 'proofshift demo reset {entry.Manifest.Id}' before retrying.");
            return 1;
        }

        EnsureSafeManagedDirectory(catalog.ContentRoot, workspace, create: true);
        var marker = new DemoWorkspaceMarker(1, entry.Manifest.Id, workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, WorkspaceMarkerName),
            JsonSerializer.Serialize(marker, JsonOptions), new UTF8Encoding(false)).ConfigureAwait(false);
        var projectPath = entry.Manifest.ProjectPath is null ? null : MaterializeProject(catalog, entry, workspace);

        var stageResults = new List<DemoStageResult>();
        var resultBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stage in entry.Manifest.RunStages)
        {
            var result = await RunStageAsync(catalog, entry, stage, workspace, projectPath, accepted,
                resultBindings, CancellationToken.None).ConfigureAwait(false);
            stageResults.Add(result);
            foreach (var binding in result.ResultBindings)
                resultBindings[$"{{{{demo.{binding.Key}}}}}"] = binding.Value;
            if (result.Status == "failed") break;
        }

        var completed = stageResults.Count == entry.Manifest.RunStages.Count && stageResults.All(stage => stage.Status != "failed");
        var output = new DemoRunResult(entry.Manifest.Id, completed ? "completed" : "failed", workspace,
            stageResults, completed ? ResolveReportPaths(catalog, entry, workspace, accepted, resultBindings) : [], preflight.Checks);
        await File.WriteAllTextAsync(Path.Combine(workspace, WorkspaceResultName),
            JsonSerializer.Serialize(output, JsonOptions), new UTF8Encoding(false)).ConfigureAwait(false);
        WriteRun(output, jsonOutput);
        return completed ? 0 : 1;
    }

    private static async Task<int> ResetScenarioAsync(DemoScenarioCatalog catalog, string scenarioId, bool jsonOutput)
    {
        var entry = catalog.Find(scenarioId);
        var workspace = GetWorkspacePath(catalog.ContentRoot, entry.Manifest.Id);
        if (!Directory.Exists(workspace))
        {
            var missing = new DemoResetResult(entry.Manifest.Id, "already-reset", workspace);
            WriteReset(missing, jsonOutput);
            return 0;
        }

        EnsureSafeManagedDirectory(catalog.ContentRoot, workspace, create: false);
        EnsureTreeHasNoReparsePoints(workspace);
        var markerPath = Path.Combine(workspace, WorkspaceMarkerName);
        if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new DemoManifestException("Refusing reset: scenario ownership marker is missing or unsafe.");
        var marker = JsonSerializer.Deserialize<DemoWorkspaceMarker>(await File.ReadAllTextAsync(markerPath).ConfigureAwait(false), JsonOptions);
        if (marker is null || marker.FormatVersion != 1 || marker.ScenarioId != entry.Manifest.Id ||
            !PathEquals(marker.WorkspacePath, workspace))
            throw new DemoManifestException("Refusing reset: scenario ownership marker does not match this workspace.");

        AcceptedDemoScenario? accepted = entry.Manifest.ResetStages.Count == 0
            ? null
            : catalog.ReadAcceptanceReference(entry);
        var projectPath = entry.Manifest.ProjectPath is null ? null : ResolveWorkspaceProjectPath(catalog, entry, workspace);
        foreach (var stage in entry.Manifest.ResetStages)
        {
            var result = await RunStageAsync(catalog, entry, stage, workspace, projectPath, accepted,
                new Dictionary<string, string>(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);
            if (result.Status == "failed")
            {
                Console.Error.WriteLine($"Reset stage '{stage.Name}' did not complete as expected; scenario workspace was retained.");
                return 1;
            }
        }

        EnsureTreeHasNoReparsePoints(workspace);
        Directory.Delete(workspace, recursive: true);
        var reset = new DemoResetResult(entry.Manifest.Id, "reset", workspace);
        WriteReset(reset, jsonOutput);
        return 0;
    }

    private static async Task<DemoPreflightResult> RunPreflightAsync(DemoScenarioCatalog catalog,
        DemoScenarioEntry entry, CancellationToken cancellationToken)
    {
        var checks = new List<DemoPreflightCheck>();
        try
        {
            _ = catalog.ReadAcceptanceReference(entry);
            checks.Add(new DemoPreflightCheck("DEMO_ACCEPTANCE_REFERENCE_READY", true, "Accepted scenario expectations are available."));
        }
        catch (DemoManifestException exception)
        {
            checks.Add(new DemoPreflightCheck("DEMO_ACCEPTANCE_REFERENCE_INVALID", false, exception.Message));
        }

        var dotnet = await RunProbeAsync("dotnet", ["--version"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (!dotnet.Success || !Version.TryParse(dotnet.Output.Trim(), out var actualVersion) ||
            !Version.TryParse(entry.Manifest.Requirements.MinimumDotNetSdk, out var minimumVersion))
        {
            checks.Add(new DemoPreflightCheck("DEMO_DOTNET_UNAVAILABLE", false,
                $".NET SDK {entry.Manifest.Requirements.MinimumDotNetSdk} or newer could not be verified."));
        }
        else
        {
            checks.Add(actualVersion >= minimumVersion
                ? new DemoPreflightCheck("DEMO_DOTNET_READY", true, $".NET SDK {actualVersion} is available.")
                : new DemoPreflightCheck("DEMO_DOTNET_TOO_OLD", false,
                    $".NET SDK {entry.Manifest.Requirements.MinimumDotNetSdk} or newer is required; found {actualVersion}."));
        }

        if (entry.Manifest.Requirements.Docker)
        {
            var docker = await RunProbeAsync("docker", ["info", "--format", "{{.ServerVersion}}"],
                TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            checks.Add(docker.Success
                ? new DemoPreflightCheck("DEMO_DOCKER_READY", true, "Docker is available.")
                : new DemoPreflightCheck("DEMO_DOCKER_UNAVAILABLE", false,
                    "Docker is required and did not respond. Start a Docker-compatible runtime, then retry preflight."));
        }

        var driveRoot = Path.GetPathRoot(catalog.ContentRoot);
        if (driveRoot is null)
        {
            checks.Add(new DemoPreflightCheck("DEMO_DISK_UNKNOWN", false, "The scenario content volume could not be identified."));
        }
        else
        {
            var availableBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
            var minimumBytes = entry.Manifest.Requirements.MinimumFreeDiskBytes;
            checks.Add(availableBytes >= minimumBytes
                ? new DemoPreflightCheck("DEMO_DISK_READY", true, $"Available disk space is {availableBytes} bytes; required {minimumBytes}.")
                : new DemoPreflightCheck("DEMO_DISK_LOW", false, $"At least {minimumBytes} bytes of free disk space are required; found {availableBytes}."));
        }

        foreach (var port in entry.Manifest.Requirements.RequiredPorts.Order())
        {
            var available = IsPortAvailable(port);
            checks.Add(available
                ? new DemoPreflightCheck("DEMO_PORT_READY", true, $"Required port {port} is available.")
                : new DemoPreflightCheck("DEMO_PORT_OCCUPIED", false, $"Required port {port} is occupied; stop its owner and retry."));
        }

        var requiredFiles = entry.Manifest.Requirements.RequiredFiles.ToList();
        if (entry.Manifest.ProjectPath is { } projectPath) requiredFiles.Add(projectPath);
        if (entry.Manifest.RunStages.Concat(entry.Manifest.ResetStages)
            .Any(stage => stage.Runner == "demo-runner"))
        {
            var runnerPath = ResolveDemoRunnerAssembly(catalog.ContentRoot);
            checks.Add(runnerPath is null
                ? new DemoPreflightCheck("DEMO_RUNNER_MISSING", false,
                    "The ProofShift DemoRunner has not been built or packaged. Build the DemoRunner project, then retry preflight.")
                : new DemoPreflightCheck("DEMO_RUNNER_READY", true,
                    "The non-test ProofShift DemoRunner is available."));
        }
        requiredFiles.Add(entry.Manifest.AcceptanceReference.IndexFile);
        foreach (var relativeFile in requiredFiles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                var filePath = DemoScenarioPaths.EnsureNoReparsePoints(catalog.ContentRoot, relativeFile, "Required file");
                checks.Add(File.Exists(filePath)
                    ? new DemoPreflightCheck("DEMO_FILE_READY", true, $"Required file is present: {relativeFile}.")
                    : new DemoPreflightCheck("DEMO_FILE_MISSING", false, $"Required file is missing: {relativeFile}."));
            }
            catch (DemoManifestException exception)
            {
                checks.Add(new DemoPreflightCheck("DEMO_FILE_UNSAFE", false, exception.Message));
            }
        }

        return new DemoPreflightResult(entry.Manifest.Id, checks.All(check => check.Passed), checks);
    }

    private static async Task<DemoStageResult> RunStageAsync(DemoScenarioCatalog catalog, DemoScenarioEntry entry,
        DemoStageManifest stage, string workspace, string? projectPath, AcceptedDemoScenario? accepted,
        IReadOnlyDictionary<string, string> priorBindings, CancellationToken cancellationToken)
    {
        var scenarioDirectory = entry.Directory;
        var tokens = CreateTokens(catalog, entry, workspace, projectPath, accepted, priorBindings);
        var workingDirectory = stage.WorkingDirectory switch
        {
            "repository" => catalog.ContentRoot,
            "scenario" => scenarioDirectory,
            "workspace" => workspace,
            _ when stage.WorkingDirectory.StartsWith("workspace/", StringComparison.Ordinal) =>
                DemoScenarioPaths.ResolveRelative(workspace, stage.WorkingDirectory["workspace/".Length..], "Stage working directory"),
            _ => throw new DemoManifestException($"Stage '{stage.Id}' has an invalid working directory.")
        };

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        var runnerArgumentStart = 0;
        if (stage.Runner == "proofshift-cli")
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        else if (stage.Runner == "demo-runner")
        {
            var runnerAssembly = ResolveDemoRunnerAssembly(catalog.ContentRoot)
                ?? throw new DemoManifestException("The ProofShift DemoRunner has not been built or packaged.");
            start.ArgumentList.Add(runnerAssembly);
        }
        else
            throw new DemoManifestException($"Stage '{stage.Id}' has an unsupported runner.");
        foreach (var argument in stage.Arguments.Skip(runnerArgumentStart)) start.ArgumentList.Add(ExpandTokens(argument, tokens));
        foreach (var pair in stage.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            start.Environment[pair.Key] = ExpandTokens(pair.Value, tokens);
        start.Environment["PROOFSHIFT_DEMO_SCENARIO_ID"] = entry.Manifest.Id;
        start.Environment["PROOFSHIFT_DEMO_WORKSPACE"] = workspace;
        start.Environment["PROOFSHIFT_DEMO_REPOSITORY_ROOT"] = catalog.ContentRoot;
        start.Environment["PROOFSHIFT_DEMO_CLI_PATH"] = Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll");

        var resultPath = stage.ResultFile is null
            ? null
            : ResolveWorkspaceOutputPath(workspace, ExpandTokens(stage.ResultFile, tokens), $"Stage '{stage.Id}' result path");
        if (resultPath is not null) start.Environment["PROOFSHIFT_DEMO_RESULT_FILE"] = resultPath;
        var outputRelativePath = stage.OutputFile ?? (stage.Runner == "proofshift-cli" ? stage.ResultFile : null);
        var outputPath = outputRelativePath is null
            ? null
            : ResolveWorkspaceOutputPath(workspace, ExpandTokens(outputRelativePath, tokens), $"Stage '{stage.Id}' output path");
        FileStream? outputStream = null;
        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        using var process = new Process { StartInfo = start };
        var stageStopwatch = Stopwatch.StartNew();
        using var samplingCancellation = new CancellationTokenSource();
        Task<long?>? fixturePeakWorkingSetTask = null;
        Task<string?>? stderrTask = null;
        try
        {
            if (!process.Start()) return FailedStage(stage, resultPath, outputPath, "Stage process could not be started.");
            if (stage.Runner == "demo-runner")
                fixturePeakWorkingSetTask = MeasureFixturePeakWorkingSetAsync(process, samplingCancellation.Token);
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream ?? Stream.Null, cancellationToken);
            stderrTask = stage.Runner == "demo-runner"
                ? ReadSafeDemoRunnerDiagnosticAsync(process.StandardError, cancellationToken)
                : DrainStandardErrorAsync(process.StandardError.BaseStream, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(stage.TimeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                var diagnostic = $"Stage timed out after {stage.TimeoutSeconds} seconds.";
                if (stderrTask?.Result is { } runnerDiagnostic) diagnostic += $" {runnerDiagnostic}";
                return FailedStage(stage, resultPath, outputPath, diagnostic);
            }
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch
        {
            if (process.StartInfo is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            return FailedStage(stage, resultPath, outputPath, "Stage process could not complete.");
        }
        finally
        {
            samplingCancellation.Cancel();
            if (outputStream is not null) await outputStream.DisposeAsync().ConfigureAwait(false);
        }

        if (process.ExitCode != stage.ExpectedExitCode)
        {
            var diagnostic = $"Expected exit code {stage.ExpectedExitCode}, observed {process.ExitCode}.";
            if (stderrTask?.Result is { } runnerDiagnostic) diagnostic += $" {runnerDiagnostic}";
            return FailedStage(stage, resultPath, outputPath, diagnostic, process.ExitCode);
        }

        if (stage.Assertions.Count > 0)
        {
            var assertionFailure = VerifyAssertions(resultPath!, stage.Assertions, tokens);
            if (assertionFailure is not null) return FailedStage(stage, resultPath, outputPath, assertionFailure, process.ExitCode);
        }

        var bindings = stage.ResultBindings.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) :
            ReadResultBindings(resultPath!, stage.ResultBindings);
        var status = stage.ExpectedOutcome == "success" ? "completed" : "expected-state-observed";
        long? fixtureProcessElapsedMilliseconds = null;
        long? fixtureProcessPeakWorkingSetBytes = null;
        if (stage.Runner == "demo-runner")
        {
            stageStopwatch.Stop();
            fixtureProcessElapsedMilliseconds = stageStopwatch.ElapsedMilliseconds;
            if (fixturePeakWorkingSetTask is not null)
                fixtureProcessPeakWorkingSetBytes = await fixturePeakWorkingSetTask.ConfigureAwait(false);
        }
        return new DemoStageResult(stage.Id, stage.Name, stage.Phase, stage.ExpectedOutcome,
            stage.ExpectedExitCode, process.ExitCode, status, resultPath, outputPath, bindings, null,
            fixtureProcessElapsedMilliseconds, fixtureProcessPeakWorkingSetBytes);
    }

    private static async Task<string?> ReadSafeDemoRunnerDiagnosticAsync(StreamReader error,
        CancellationToken cancellationToken)
    {
        const string prefix = "Demo scenario failed unexpectedly (";
        const string suffix = "). Exception details were withheld to protect configured connection values.";
        string? diagnostic = null;
        while (await error.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (diagnostic is null && line.StartsWith(prefix, StringComparison.Ordinal) &&
                line.EndsWith(suffix, StringComparison.Ordinal))
                diagnostic = line;
        }
        return diagnostic;
    }

    private static async Task<string?> DrainStandardErrorAsync(Stream error,
        CancellationToken cancellationToken)
    {
        await error.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static async Task<long?> MeasureFixturePeakWorkingSetAsync(Process process, CancellationToken cancellationToken)
    {
        long peakWorkingSetBytes = 0;
        using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (process.HasExited) break;
                try
                {
                    process.Refresh();
                    peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    break;
                }
                if (!await interval.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) { }
        return peakWorkingSetBytes > 0 ? peakWorkingSetBytes : null;
    }

    private static string ResolveWorkspaceOutputPath(string workspace, string path, string description)
    {
        var resultPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : DemoScenarioPaths.ResolveRelative(workspace, path, description);
        if (!DemoScenarioPaths.IsWithin(workspace, resultPath)) throw new DemoManifestException($"{description} escapes the scenario workspace.");
        DemoScenarioPaths.EnsureNoReparsePoints(workspace, Path.GetRelativePath(workspace, resultPath), description);
        return resultPath;
    }

    private static string? ResolveDemoRunnerAssembly(string contentRoot)
    {
        var candidates = new[]
        {
            "demo/ProofShift.DemoRunner.dll",
            "src/ProofShift.DemoRunner/bin/Debug/net10.0/ProofShift.DemoRunner.dll",
            "src/ProofShift.DemoRunner/bin/Release/net10.0/ProofShift.DemoRunner.dll"
        };
        foreach (var relativePath in candidates)
        {
            var resolvedPath = DemoScenarioPaths.EnsureNoReparsePoints(contentRoot, relativePath, "DemoRunner assembly");
            if (File.Exists(resolvedPath)) return resolvedPath;
        }
        return null;
    }

    private static string? VerifyAssertions(string resultPath, IReadOnlyList<DemoJsonAssertion> assertions,
        IReadOnlyDictionary<string, string> tokens)
    {
        if (!File.Exists(resultPath)) return "Stage result JSON file is missing.";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            foreach (var assertion in assertions)
            {
                var actual = ReadJsonPointer(document.RootElement, assertion.Pointer);
                var expected = ExpandTokens(assertion.Expected, tokens);
                var actualValue = assertion.ValueType switch
                {
                    "string" when actual.ValueKind == JsonValueKind.String => actual.GetString(),
                    "number" when actual.ValueKind == JsonValueKind.Number => actual.GetRawText(),
                    "boolean" when actual.ValueKind is JsonValueKind.True or JsonValueKind.False => actual.GetBoolean() ? "true" : "false",
                    _ => null
                };
                if (!StringComparer.Ordinal.Equals(actualValue, expected))
                    return $"Result assertion failed at '{assertion.Pointer}'.";
            }
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return "Stage result JSON could not be validated.";
        }
    }

    private static JsonElement ReadJsonPointer(JsonElement current, string pointer)
    {
        foreach (var rawToken in pointer.Split('/').Skip(1))
        {
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
                current = property;
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, NumberStyles.None,
                         CultureInfo.InvariantCulture, out var index) && index >= 0 && index < current.GetArrayLength())
                current = current[index];
            else
                throw new KeyNotFoundException();
        }
        return current;
    }

    private static Dictionary<string, string> CreateTokens(DemoScenarioCatalog catalog,
        DemoScenarioEntry entry, string workspace, string? projectPath, AcceptedDemoScenario? accepted,
        IReadOnlyDictionary<string, string>? resultBindings = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{repository}}"] = catalog.ContentRoot,
            ["{{scenarioDirectory}}"] = entry.Directory,
            ["{{workspace}}"] = workspace,
        };
        if (projectPath is not null) values["{{project}}"] = projectPath;
        if (accepted is not null)
        {
            values["{{acceptance.scenarioId}}"] = accepted.ScenarioId;
            values["{{acceptance.sourceArtifacts}}"] = accepted.SourceArtifacts.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.targetArtifacts}}"] = accepted.CorrectedTargetArtifacts.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.seededDefects}}"] = accepted.SeededDefects.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.expectedFindings}}"] = accepted.ExpectedFindings.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.correctedFailures}}"] = accepted.CorrectedFailures.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.unaccountedSources}}"] = accepted.UnaccountedSources.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.unexplainedTargets}}"] = accepted.UnexplainedTargets.ToString(CultureInfo.InvariantCulture);
            values["{{acceptance.qualification}}"] = accepted.CorrectedQualification;
        }
        if (resultBindings is not null)
            foreach (var binding in resultBindings) values[binding.Key] = binding.Value;
        return values;
    }

    private static Dictionary<string, string> ReadResultBindings(string resultPath, IReadOnlyList<DemoResultBinding> bindings)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var element = ReadJsonPointer(document.RootElement, binding.Pointer);
            var value = binding.ValueType switch
            {
                "string" when element.ValueKind == JsonValueKind.String => element.GetString(),
                "number" when element.ValueKind == JsonValueKind.Number => element.GetRawText(),
                "boolean" when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean() ? "true" : "false",
                _ => null
            };
            if (value is null) throw new DemoManifestException($"Stage result binding '{binding.Name}' did not resolve to its declared JSON type.");
            values.Add(binding.Name, value);
        }
        return values;
    }

    private static string ExpandTokens(string value, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var token in tokens.OrderByDescending(pair => pair.Key.Length))
            value = value.Replace(token.Key, token.Value, StringComparison.Ordinal);
        if (value.Contains("{{", StringComparison.Ordinal) || value.Contains("}}", StringComparison.Ordinal))
            throw new DemoManifestException("Demo stage contains an unresolved or unsupported metadata token.");
        return value;
    }

    private static DemoStageResult FailedStage(DemoStageManifest stage, string? resultFile, string? outputFile,
        string diagnostic, int? actualExitCode = null) => new(stage.Id, stage.Name, stage.Phase, stage.ExpectedOutcome,
        stage.ExpectedExitCode, actualExitCode, "failed", resultFile, outputFile,
        new Dictionary<string, string>(StringComparer.Ordinal), diagnostic);

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<DemoProbeResult> RunProbeAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return new DemoProbeResult(false, string.Empty);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return new DemoProbeResult(process.ExitCode == 0, output);
        }
        catch
        {
            return new DemoProbeResult(false, string.Empty);
        }
    }

    private static string GetWorkspacePath(string contentRoot, string scenarioId)
    {
        var managedRoot = Path.Combine(Path.GetFullPath(contentRoot), ".proofshift", "demos");
        var workspace = Path.GetFullPath(Path.Combine(managedRoot, scenarioId));
        if (!DemoScenarioPaths.IsWithin(managedRoot, workspace))
            throw new DemoManifestException("Scenario workspace escaped the managed demo root.");
        return workspace;
    }

    private static string MaterializeProject(DemoScenarioCatalog catalog, DemoScenarioEntry entry, string workspace)
    {
        var projectRelativePath = entry.Manifest.ProjectPath
            ?? throw new DemoManifestException("Project materialization requires projectPath.");
        var sourceProjectPath = DemoScenarioPaths.EnsureNoReparsePoints(catalog.ContentRoot, projectRelativePath, "Project path");
        var sourceDirectory = Path.GetDirectoryName(sourceProjectPath)
            ?? throw new DemoManifestException("Project configuration directory is unavailable.");
        EnsureTreeHasNoReparsePoints(sourceDirectory);
        var destinationDirectory = Path.Combine(workspace, "project");
        CopyProjectDirectory(sourceDirectory, destinationDirectory);
        var relativeProjectPath = Path.GetRelativePath(sourceDirectory, sourceProjectPath);
        var isolatedProjectPath = Path.Combine(destinationDirectory, relativeProjectPath);
        if (!File.Exists(isolatedProjectPath))
            throw new DemoManifestException("Project configuration was not materialized in the scenario-owned workspace.");
        return isolatedProjectPath;
    }

    private static string? ResolveWorkspaceProjectPath(DemoScenarioCatalog catalog, DemoScenarioEntry entry, string workspace)
    {
        if (entry.Manifest.ProjectPath is not { } projectRelativePath) return null;
        var sourceProjectPath = DemoScenarioPaths.EnsureNoReparsePoints(catalog.ContentRoot, projectRelativePath, "Project path");
        var sourceDirectory = Path.GetDirectoryName(sourceProjectPath)
            ?? throw new DemoManifestException("Project configuration directory is unavailable.");
        return Path.Combine(workspace, "project", Path.GetRelativePath(sourceDirectory, sourceProjectPath));
    }

    private static void CopyProjectDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (name is ".proofshift" or ".git" or "bin" or "obj") continue;
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new DemoManifestException("Project isolation refuses symbolic links and reparse points.");
                CopyProjectDirectory(entry, Path.Combine(destinationDirectory, name));
            }
            else
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new DemoManifestException("Project isolation refuses symbolic links and reparse points.");
                File.Copy(entry, Path.Combine(destinationDirectory, name), overwrite: false);
            }
        }
    }

    private static void EnsureSafeManagedDirectory(string contentRoot, string workspace, bool create)
    {
        var fullRoot = Path.GetFullPath(contentRoot);
        var proofshiftRoot = Path.Combine(fullRoot, ".proofshift");
        var demosRoot = Path.Combine(proofshiftRoot, "demos");
        foreach (var path in new[] { proofshiftRoot, demosRoot, workspace })
        {
            if (File.Exists(path)) throw new DemoManifestException($"Refusing to use scenario path occupied by a file: '{path}'.");
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new DemoManifestException($"Refusing to use scenario path that is a symbolic link or reparse point: '{path}'.");
        }
        if (create)
        {
            Directory.CreateDirectory(demosRoot);
            Directory.CreateDirectory(workspace);
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new DemoManifestException("Refusing operation because the inspected tree root is a symbolic link or reparse point.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new DemoManifestException("Refusing operation because the inspected tree contains a symbolic link or reparse point.");
            if (Directory.Exists(entry)) EnsureTreeHasNoReparsePoints(entry);
        }
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string[] ResolveReportPaths(DemoScenarioCatalog catalog, DemoScenarioEntry entry, string workspace,
        AcceptedDemoScenario accepted, IReadOnlyDictionary<string, string> resultBindings)
    {
        var projectPath = ResolveWorkspaceProjectPath(catalog, entry, workspace);
        var tokens = CreateTokens(catalog, entry, workspace, projectPath, accepted, resultBindings);
        return entry.Manifest.ReportPaths.Select(path =>
        {
            var expandedPath = ExpandTokens(path, tokens);
            if (Path.IsPathRooted(expandedPath))
            {
                if (!DemoScenarioPaths.IsWithin(workspace, expandedPath))
                    throw new DemoManifestException("Report path escapes the scenario workspace.");
                return Path.GetFullPath(expandedPath);
            }
            return DemoScenarioPaths.ResolveRelative(workspace, expandedPath, "Report path");
        }).ToArray();
    }

    private static DemoAcceptedOutcome DescribeAcceptedOutcome(AcceptedDemoScenario accepted) =>
        new(accepted.SeededDefects > 0 ? "NOT QUALIFIED" : accepted.CorrectedQualification,
            accepted.SeededDefects, accepted.ExpectedFindings, accepted.CorrectedFailures,
            accepted.UnaccountedSources, accepted.UnexplainedTargets, accepted.RecoveryAssessment,
            accepted.RecoveryRehearsal, accepted.CorrectedQualification, accepted.FalseReverseDeclarations);

    private static void WritePreflight(DemoPreflightResult result, bool jsonOutput)
    {
        if (jsonOutput) WriteJson(result);
        else
        {
            Console.WriteLine($"Preflight: {(result.Ready ? "READY" : "BLOCKED")}");
            foreach (var check in result.Checks) Console.WriteLine($"{check.Code}: {check.Message}");
        }
    }

    private static void WriteRun(DemoRunResult result, bool jsonOutput)
    {
        if (jsonOutput) WriteJson(result);
        else
        {
            Console.WriteLine($"Scenario: {result.ScenarioId}");
            foreach (var stage in result.Stages)
            {
                var outcome = stage.Status == "failed" ? "FAILED" : stage.ExpectedOutcome switch
                {
                    "expected-defective-verification" => "EXPECTED DEFECTS DETECTED",
                    "expected-unsafe-recovery" => "UNSAFE RECOVERY DECLARATION REJECTED",
                    _ => "COMPLETED"
                };
                Console.WriteLine($"{stage.Phase}: {stage.Name} - {outcome}");
                if (stage.Diagnostic is not null) Console.WriteLine($"  {stage.Diagnostic}");
                if (stage.ResultFile is not null) Console.WriteLine($"  Machine output: {stage.ResultFile}");
                if (stage.OutputFile is not null && stage.OutputFile != stage.ResultFile)
                    Console.WriteLine($"  Stage log: {stage.OutputFile}");
                if (stage.ResultFile is not null)
                {
                    var humanReportPath = Path.ChangeExtension(stage.ResultFile, ".txt");
                    if (File.Exists(humanReportPath)) Console.WriteLine($"  Human report: {humanReportPath}");
                }
                if (stage.FixtureProcessElapsedMilliseconds is { } elapsedMilliseconds)
                    Console.WriteLine($"  Fixture process elapsed: {elapsedMilliseconds} ms");
                if (stage.FixtureProcessPeakWorkingSetBytes is { } peakWorkingSetBytes)
                    Console.WriteLine($"  Fixture process peak working set: {peakWorkingSetBytes} bytes (container memory excluded)");
                foreach (var fact in stage.ResultBindings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    Console.WriteLine($"  {HumanizeBindingName(fact.Key)}: {fact.Value}");
            }
            Console.WriteLine($"Demo run: {result.Status.ToUpperInvariant()}");
            Console.WriteLine($"Scenario workspace: {result.Workspace}");
            foreach (var check in result.PreflightChecks.Where(check => !check.Passed))
                Console.WriteLine($"Preflight {check.Code}: {check.Message}");
            foreach (var report in result.Reports) Console.WriteLine($"Report: {report}");
        }
    }

    private static void WriteReset(DemoResetResult result, bool jsonOutput)
    {
        if (jsonOutput) WriteJson(result);
        else Console.WriteLine($"Scenario {result.ScenarioId}: {result.Status}. Workspace: {result.Workspace}");
    }

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static string HumanizeBindingName(string name)
    {
        var label = new StringBuilder(name.Length + 8);
        foreach (var character in name)
        {
            if (character is '_' or '-')
            {
                if (label.Length > 0 && label[label.Length - 1] != ' ') label.Append(' ');
                continue;
            }
            if (char.IsUpper(character) && label.Length > 0 && label[label.Length - 1] != ' ') label.Append(' ');
            label.Append(character);
        }
        if (label.Length > 0) label[0] = char.ToUpperInvariant(label[0]);
        return label.ToString();
    }

    private static void WriteUsage() => Console.Error.WriteLine(
        "Usage: proofshift demo list [--json] | demo describe <id> [--json] | demo preflight <id> [--json] | demo run <id> [--json] | demo reset <id> [--json]");

    private sealed record DemoProbeResult(bool Success, string Output);
    private sealed record DemoWorkspaceMarker(int FormatVersion, string ScenarioId, string WorkspacePath);
    private sealed record DemoResetResult(string ScenarioId, string Status, string Workspace);
    private sealed record DemoListItem(string Id, string DisplayName, string Domain, string Description,
        string ApproximateRuntime, bool DockerRequired, IReadOnlyList<string> RequiredServices);
    private sealed record DemoAcceptedOutcome(string DefectiveQualification, int SeededDefects, int ExpectedFindings,
        int CorrectedFailures, int UnaccountedSources, int UnexplainedTargets, string RecoveryAssessment,
        string RecoveryRehearsal, string CorrectedQualification, int? FalseReverseDeclarations);
    private sealed record DemoScenarioDescription(string Id, string DisplayName, string Domain,
        string Description, string ClaimBoundary, string PackId, string PackVersion, string ApproximateRuntime,
        IReadOnlyList<string> SourceSystems, IReadOnlyList<string> TargetSystems, DemoArtifactScale ArtifactScale,
        DemoScenarioRequirements Requirements, IReadOnlyList<string> RequiredServices,
        string? ProjectPath, IReadOnlyList<string> FixtureSetupSteps,
        string DefectiveTargetPreparation, string CorrectedTargetPreparation, DemoAcceptedOutcome AcceptedOutcome,
        IReadOnlyList<string> ReportPaths, string ResetScope, IReadOnlyList<string> ResetStages);
}