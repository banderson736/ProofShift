using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProofShift.Reporting;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class DemoScenarioCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] TestSourceConnectors = ["csv"];
    private static readonly string[] TestTargetConnectors = ["files"];

    [Fact]
    public async Task DemoListAndDescribeAreDeterministicAndUseAcceptedExpectations()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "zeta", "zeta-fast", expectedFindings: 7);
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3);

            var firstList = await RunAsync(root, "demo", "list", "--json");
            var secondList = await RunAsync(root, "demo", "list", "--json");
            Assert.True(firstList.ExitCode == 0, $"stdout: {firstList.Output} stderr: {firstList.Error}");
            Assert.Equal(firstList.Output, secondList.Output);
            using var list = JsonDocument.Parse(firstList.Output);
            Assert.Equal("alpha", list.RootElement[0].GetProperty("id").GetString());
            Assert.Equal("zeta", list.RootElement[1].GetProperty("id").GetString());
            Assert.Equal("Test service", list.RootElement[0].GetProperty("requiredServices")[0].GetString());

            var described = await RunAsync(root, "demo", "describe", "alpha", "--json");
            Assert.True(described.ExitCode == 0, $"stdout: {described.Output} stderr: {described.Error}");
            using var description = JsonDocument.Parse(described.Output);
            var acceptedOutcome = description.RootElement.GetProperty("acceptedOutcome");
            Assert.Equal("NOT QUALIFIED", acceptedOutcome.GetProperty("defectiveQualification").GetString());
            Assert.Equal(3, acceptedOutcome.GetProperty("expectedFindings").GetInt32());
            Assert.Equal("QUALIFIED", acceptedOutcome.GetProperty("correctedQualification").GetString());
            Assert.Equal("Configured synthetic demo invariants only.", description.RootElement.GetProperty("claimBoundary").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DemoHelpListsLifecycleCommands()
    {
        var root = CreateRoot();
        try
        {
            var help = await RunAsync(root, "demo", "help");
            Assert.Equal(0, help.ExitCode);
            Assert.Contains("demo list", help.Error, StringComparison.Ordinal);
            Assert.Contains("demo describe", help.Error, StringComparison.Ordinal);
            Assert.Contains("demo preflight", help.Error, StringComparison.Ordinal);
            Assert.Contains("demo run", help.Error, StringComparison.Ordinal);
            Assert.Contains("demo reset", help.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliVersionReportsCurrentMilestone()
    {
        var root = CreateRoot();
        try
        {
            var version = await RunAsync(root, "--version");
            Assert.Equal(0, version.ExitCode);
            Assert.Equal("ProofShift PS-0.10G", version.Output.Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FixtureOwnedScenarioCanRunWithoutProjectConfiguration()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [Stage("packs", ["packs", "list", "--json"], 0, "results/packs.json")]);
            var manifestPath = Path.Combine(root, "scenarios", "demos", "alpha", "scenario.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
            manifest!["projectPath"] = null;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), TestContext.Current.CancellationToken);

            var described = await RunAsync(root, "demo", "describe", "alpha", "--json");
            Assert.Equal(0, described.ExitCode);
            using var description = JsonDocument.Parse(described.Output);
            Assert.Equal(JsonValueKind.Null, description.RootElement.GetProperty("projectPath").ValueKind);

            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 0, $"stdout: {run.Output} stderr: {run.Error}");
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "demos", "alpha", "project")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedTestModuleRunnerExecutesBoundedMtpClass()
    {
        var root = CreateRoot();
        var moduleDirectory = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(moduleDirectory);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(moduleDirectory, Path.GetFileName(file)), overwrite: true);
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [Stage("report-tests", ["fixtures/ProofShift.EndToEnd.Tests.dll", "-class",
                    "ProofShift.EndToEnd.Tests.PensionReportAggregationTests", "-parallelMode", "none"], 0,
                    runner: "dotnet-test-module", outputFile: "logs/report-tests.log")]);

            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 0, $"stdout: {run.Output} stderr: {run.Error}");
            using var result = JsonDocument.Parse(run.Output);
            Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
            Assert.True(result.RootElement.GetProperty("stages")[0]
                .GetProperty("fixtureProcessElapsedMilliseconds").GetInt64() > 0);
            Assert.True(result.RootElement.GetProperty("stages")[0]
                .GetProperty("fixtureProcessPeakWorkingSetBytes").GetInt64() > 0);
            Assert.True(File.Exists(Path.Combine(root, ".proofshift", "demos", "alpha", "logs", "report-tests.log")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreflightReportsMissingFixturesBeforeRunningStages()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                requiredFiles: ["fixtures/absent.csv"]);

            var result = await RunAsync(root, "demo", "preflight", "alpha", "--json");
            Assert.True(result.ExitCode == 1, $"stdout: {result.Output} stderr: {result.Error}");
            using var preflight = JsonDocument.Parse(result.Output);
            Assert.False(preflight.RootElement.GetProperty("ready").GetBoolean());
            Assert.Contains(preflight.RootElement.GetProperty("checks").EnumerateArray(), check =>
                check.GetProperty("code").GetString() == "DEMO_FILE_MISSING");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreflightReportsOccupiedRequiredPorts()
    {
        var root = CreateRoot();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3, requiredPorts: [occupiedPort]);

            var result = await RunAsync(root, "demo", "preflight", "alpha", "--json");
            Assert.True(result.ExitCode == 1, $"stdout: {result.Output} stderr: {result.Error}");
            using var preflight = JsonDocument.Parse(result.Output);
            Assert.Contains(preflight.RootElement.GetProperty("checks").EnumerateArray(), check =>
                check.GetProperty("code").GetString() == "DEMO_PORT_OCCUPIED");
        }
        finally
        {
            listener.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunRecognizesExpectedDefectiveExitAndResetDeletesOnlyOwnedWorkspace()
    {
        var root = CreateRoot();
        try
        {
            var projectDirectory = Path.Combine(root, "scenarios", "projects", "alpha");
            Directory.CreateDirectory(projectDirectory);
            var initialized = await RunAsync(root, "init", projectDirectory);
            Assert.Equal(0, initialized.ExitCode);
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [
                    Stage("isolated-plan", ["plan", "{{project}}", "--json"], 0,
                        resultFile: "results/plan.json",
                        assertions: [new DemoTestAssertion("/valid", "boolean", "true")]),
                    Stage("packs", ["packs", "list", "--json"], 0,
                        resultFile: "results/packs.json",
                        assertions: [new DemoTestAssertion("/0/id", "string", "proofshift.fhir")]),
                    Stage("expected-defect", ["validate", "missing-project.yaml", "--json"], 1,
                        resultFile: "results/defective.json",
                        assertions: [new DemoTestAssertion("/valid", "boolean", "false")])
                    ], writeProjectFile: false, relativeProjectPath: "scenarios/projects/alpha/proofshift.yaml");
            var unrelatedWorkspace = Path.Combine(root, ".proofshift", "demos", "unrelated");
            Directory.CreateDirectory(unrelatedWorkspace);
            var unrelatedFile = Path.Combine(unrelatedWorkspace, "keep.txt");
            await File.WriteAllTextAsync(unrelatedFile, "not owned by alpha", TestContext.Current.CancellationToken);

            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 0, $"stdout: {run.Output} stderr: {run.Error}");
            using var result = JsonDocument.Parse(run.Output);
            Assert.Equal("completed", result.RootElement.GetProperty("status").GetString());
            Assert.Equal("completed", result.RootElement.GetProperty("stages")[0].GetProperty("status").GetString());
            Assert.Equal("success", result.RootElement.GetProperty("stages")[0].GetProperty("expectedOutcome").GetString());
            Assert.Equal("completed", result.RootElement.GetProperty("stages")[1].GetProperty("status").GetString());
            Assert.Equal("expected-state-observed", result.RootElement.GetProperty("stages")[2].GetProperty("status").GetString());
            Assert.Equal("expected-defective-verification", result.RootElement.GetProperty("stages")[2].GetProperty("expectedOutcome").GetString());
            Assert.True(File.Exists(Path.Combine(root, ".proofshift", "demos", "alpha", "project", "proofshift.yaml")));
            Assert.False(Directory.Exists(Path.Combine(projectDirectory, ".proofshift")));
            Assert.True(File.Exists(Path.Combine(root, ".proofshift", "demos", "alpha", "results", "packs.json")));

            var rerunWithoutReset = await RunAsync(root, "demo", "run", "alpha");
            Assert.Equal(1, rerunWithoutReset.ExitCode);
            Assert.Contains("demo reset alpha", rerunWithoutReset.Error, StringComparison.Ordinal);

            var reset = await RunAsync(root, "demo", "reset", "alpha", "--json");
            Assert.Equal(0, reset.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "demos", "alpha")));
            Assert.True(File.Exists(unrelatedFile));

            var repeatedRun = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(repeatedRun.ExitCode == 0, $"stdout: {repeatedRun.Output} stderr: {repeatedRun.Error}");
            using var repeatedResult = JsonDocument.Parse(repeatedRun.Output);
            Assert.Equal(result.RootElement.GetProperty("status").GetString(), repeatedResult.RootElement.GetProperty("status").GetString());
            Assert.Equal(result.RootElement.GetProperty("workspace").GetString(), repeatedResult.RootElement.GetProperty("workspace").GetString());
            Assert.Equal(result.RootElement.GetProperty("stages").GetRawText(), repeatedResult.RootElement.GetProperty("stages").GetRawText());

            var unownedWorkspace = Path.Combine(root, ".proofshift", "demos", "untrusted");
            Directory.CreateDirectory(unownedWorkspace);
            var unownedFile = Path.Combine(unownedWorkspace, "keep.txt");
            await File.WriteAllTextAsync(unownedFile, "must not delete", TestContext.Current.CancellationToken);
            WriteScenario(root, "untrusted", "untrusted-fast", expectedFindings: 1);
            var refused = await RunAsync(root, "demo", "reset", "untrusted");
            Assert.Equal(2, refused.ExitCode);
            Assert.True(File.Exists(unownedFile));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunFailureLeavesMarkerOwnedWorkspaceForExplicitReset()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [Stage("unexpected-defect", ["validate", "missing-project.yaml"], expectedExitCode: 0,
                    resultFile: "results/failed.json",
                    resultBindings: [new DemoTestBinding("failedId", "/id", "string")])]);
            var manifestPath = Path.Combine(root, "scenarios", "demos", "alpha", "scenario.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
            manifest!["reportPaths"] = new JsonArray("{{workspace}}/reports/{{demo.failedId}}.json");
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), TestContext.Current.CancellationToken);

            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 1, $"stdout: {run.Output} stderr: {run.Error}");
            using var result = JsonDocument.Parse(run.Output);
            Assert.Equal("failed", result.RootElement.GetProperty("status").GetString());
            Assert.Contains("Expected exit code 0, observed 1",
                result.RootElement.GetProperty("stages")[0].GetProperty("diagnostic").GetString(), StringComparison.Ordinal);
            Assert.Empty(result.RootElement.GetProperty("reports").EnumerateArray());
            Assert.True(File.Exists(Path.Combine(root, ".proofshift", "demos", "alpha", ".proofshift-demo-owned.json")));

            var reset = await RunAsync(root, "demo", "reset", "alpha");
            Assert.Equal(0, reset.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "demos", "alpha")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestRejectsExpectedDefectiveExitWithoutStructuredAssertion()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [Stage("unasserted-defect", ["validate", "missing-project.yaml", "--json"],
                    expectedExitCode: 1, expectedOutcome: "expected-defective-verification")]);

            var list = await RunAsync(root, "demo", "list");
            Assert.Equal(2, list.ExitCode);
            Assert.Contains("persist structured results and assert them", list.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunResolvesStructuredResultBindingsInLaterStages()
    {
        var root = CreateRoot();
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                runStages: [
                    Stage("list-packs", ["packs", "list", "--json"], 0,
                        resultFile: "results/packs.json",
                        resultBindings: [new DemoTestBinding("packId", "/0/id", "string")]),
                    Stage("describe-bound-pack", ["packs", "describe", "{{demo.packId}}", "--json"], 0,
                        resultFile: "results/pack.json",
                        assertions: [new DemoTestAssertion("/id", "string", "proofshift.fhir")])
                ]);

            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 0, $"stdout: {run.Output} stderr: {run.Error}");
            using var result = JsonDocument.Parse(run.Output);
            Assert.Equal("proofshift.fhir", result.RootElement.GetProperty("stages")[0]
                .GetProperty("resultBindings").GetProperty("packId").GetString());
            Assert.Equal("completed", result.RootElement.GetProperty("stages")[1].GetProperty("status").GetString());

            var reset = await RunAsync(root, "demo", "reset", "alpha");
            Assert.Equal(0, reset.ExitCode);
            var humanRun = await RunAsync(root, "demo", "run", "alpha");
            Assert.True(humanRun.ExitCode == 0, $"stdout: {humanRun.Output} stderr: {humanRun.Error}");
            Assert.Contains("Pack Id: proofshift.fhir", humanRun.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DemoDisplayMetadataDoesNotChangeReferencedProjectGraphHash()
    {
        var root = CreateRoot();
        try
        {
            var projectDirectory = Path.Combine(root, "scenarios", "projects", "alpha");
            Directory.CreateDirectory(projectDirectory);
            var initialized = await RunAsync(root, "init", projectDirectory);
            Assert.Equal(0, initialized.ExitCode);
            var projectPath = Path.Combine(projectDirectory, "proofshift.yaml");

            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3,
                writeProjectFile: false, relativeProjectPath: "scenarios/projects/alpha/proofshift.yaml");
            var firstPlan = await RunAsync(root, "plan", projectPath, "--json");
            Assert.True(firstPlan.ExitCode == 0, $"stdout: {firstPlan.Output} stderr: {firstPlan.Error}");
            using var first = JsonDocument.Parse(firstPlan.Output);
            var firstGraphHash = first.RootElement.GetProperty("graphHash").GetString();

            var manifestPath = Path.Combine(root, "scenarios", "demos", "alpha", "scenario.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
            manifest!["displayName"] = "Renamed presentation-only label";
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), TestContext.Current.CancellationToken);

            var secondPlan = await RunAsync(root, "plan", projectPath, "--json");
            Assert.True(secondPlan.ExitCode == 0, $"stdout: {secondPlan.Output} stderr: {secondPlan.Error}");
            using var second = JsonDocument.Parse(secondPlan.Output);
            Assert.Equal(firstGraphHash, second.RootElement.GetProperty("graphHash").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ResetRefusesSymlinkInsideWorkspaceWhenHostAllowsSymlinkCreation()
    {
        var root = CreateRoot();
        var outside = Path.Combine(Path.GetTempPath(), $"proofshift-demo-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(outsideFile, "outside scenario scope", TestContext.Current.CancellationToken);
        string? linkPath = null;
        try
        {
            WriteScenario(root, "alpha", "alpha-fast", expectedFindings: 3);
            var run = await RunAsync(root, "demo", "run", "alpha", "--json");
            Assert.True(run.ExitCode == 0, $"stdout: {run.Output} stderr: {run.Error}");

            linkPath = Path.Combine(root, ".proofshift", "demos", "alpha", "outside-link");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Assert.Skip("Host does not permit symlink creation for demo-reset containment coverage.");
            }

            var reset = await RunAsync(root, "demo", "reset", "alpha");
            Assert.Equal(2, reset.ExitCode);
            Assert.True(File.Exists(outsideFile));
            Assert.True(Directory.Exists(Path.Combine(root, ".proofshift", "demos", "alpha")));
        }
        finally
        {
            if (linkPath is not null && Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-demo-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Directory.CreateDirectory(Path.Combine(root, "fixtures"));
        Directory.CreateDirectory(Path.Combine(root, "scenarios", "demos"));
        Directory.CreateDirectory(Path.Combine(root, "scenarios", "projects"));
        File.WriteAllText(Path.Combine(root, "fixtures", "source.csv"), "id\n1\n");
        return root;
    }

    private static void WriteScenario(string root, string id, string acceptedScenarioId, int expectedFindings,
        IReadOnlyList<string>? requiredFiles = null, IReadOnlyList<int>? requiredPorts = null,
        IReadOnlyList<object>? runStages = null, bool writeProjectFile = true, string? relativeProjectPath = null)
    {
        var projectPath = relativeProjectPath ?? $"scenarios/projects/{id}.yaml";
        if (writeProjectFile)
            File.WriteAllText(Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar)), "proofshift: 1\n");
        var manifestDirectory = Path.Combine(root, "scenarios", "demos", id);
        Directory.CreateDirectory(manifestDirectory);
        var claimBoundary = "Configured synthetic demo invariants only.";
        var manifest = new
        {
            manifestVersion = 1,
            id,
            displayName = $"{id} demo",
            domain = "Synthetic test domain",
            pack = new { id = "proofshift.test", version = "1.0.0" },
            description = "A deterministic demo contract test.",
            claimBoundary,
            approximateRuntime = "under 1 minute",
            requirements = new
            {
                docker = false,
                minimumDotNetSdk = "1.0.0",
                minimumFreeDiskBytes = 0,
                requiredPorts = requiredPorts ?? Array.Empty<int>(),
                requiredFiles = requiredFiles ?? ["fixtures/source.csv"]
            },
            sourceSystems = new[] { "synthetic-source" },
            targetSystems = new[] { "synthetic-target" },
            requiredServices = new[] { "Test service" },
            artifactScale = new { sourceArtifacts = 1, targetArtifacts = 1, unit = "records", notes = "test fixture" },
            acceptanceReference = new { indexFile = "docs/PS0_10F_SCENARIO_INDEX.json", scenarioId = acceptedScenarioId },
            projectPath,
            fixtureSetupSteps = new[] { "Use deterministic synthetic input." },
            defectiveTargetPreparation = "Prepare the declared defective state.",
            correctedTargetPreparation = "Prepare the declared corrected state.",
            runStages = runStages ?? [Stage("packs", ["packs", "list", "--json"], 0, "results/packs.json")],
            resetStages = Array.Empty<object>(),
            reportPaths = new[] { "{{workspace}}/reports/summary.json" },
            resetScope = "scenario-owned-only"
        };
        File.WriteAllText(Path.Combine(manifestDirectory, "scenario.json"), JsonSerializer.Serialize(manifest, JsonOptions));

        var indexPath = Path.Combine(root, "docs", "PS0_10F_SCENARIO_INDEX.json");
        var entries = File.Exists(indexPath)
            ? JsonDocument.Parse(File.ReadAllText(indexPath)).RootElement.GetProperty("scenarios").EnumerateArray()
                .Select(element => JsonSerializer.Deserialize<JsonElement>(element.GetRawText())).ToList()
            : [];
        entries.Add(JsonSerializer.SerializeToElement(new
        {
            scenarioId = acceptedScenarioId,
            packId = "proofshift.test",
            packVersion = "1.0.0",
            sourceArtifacts = 1,
            correctedTargetArtifacts = 1,
            seededDefects = expectedFindings,
            expectedFindings,
            correctedFailures = 0,
            unaccountedSources = 0,
            unexplainedTargets = 0,
            recoveryAssessment = "PASSED",
            recoveryRehearsal = "PASSED",
            correctedQualification = "QUALIFIED",
            falseReverseDeclarations = 0,
            claimBoundary,
            measuredTestSeconds = 1.0,
            runtimeNote = "test only",
            sourceConnectors = TestSourceConnectors,
            targetConnectors = TestTargetConnectors
        }));
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new
        {
            format = "proofshift-ps010f-scenario-index-v1",
            scenarios = entries
        }, JsonOptions));
    }

    private static object Stage(string id, string[] arguments, int expectedExitCode,
        string? resultFile = null, DemoTestAssertion[]? assertions = null, string? expectedOutcome = null,
        DemoTestBinding[]? resultBindings = null, string runner = "proofshift-cli", string? outputFile = null) => new
    {
        id,
        name = id,
        phase = "Verification",
        workingDirectory = "repository",
        arguments,
        expectedOutcome = expectedOutcome ?? (expectedExitCode == 0 ? "success" : "expected-defective-verification"),
        expectedExitCode,
        timeoutSeconds = 60,
        resultFile,
        runner,
        outputFile,
        assertions = (assertions ?? []).Select(assertion => new
        {
            pointer = assertion.Pointer,
            valueType = assertion.ValueType,
            expected = assertion.Expected
        }).ToArray(),
        resultBindings = (resultBindings ?? []).Select(binding => new
        {
            name = binding.Name,
            pointer = binding.Pointer,
            valueType = binding.ValueType
        }).ToArray(),
        environment = new Dictionary<string, string>()
    };

    private static async Task<CliResult> RunAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        start.Environment["PROOFSHIFT_TARGET_CONNECTION"] = "test-only-unresolved-connection-placeholder";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record DemoTestAssertion(string Pointer, string ValueType, string Expected);
    private sealed record DemoTestBinding(string Name, string Pointer, string ValueType);
    private sealed record CliResult(int ExitCode, string Output, string Error);
}

internal static class DemoScenarioResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static bool IsRunningDemo => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROOFSHIFT_DEMO_WORKSPACE"));

    public static string CreateFixtureRoot(string temporaryName)
    {
        var workspace = Environment.GetEnvironmentVariable("PROOFSHIFT_DEMO_WORKSPACE");
        return string.IsNullOrWhiteSpace(workspace)
            ? Path.Combine(Path.GetTempPath(), $"{temporaryName}-{Guid.NewGuid():N}")
            : Path.Combine(Path.GetFullPath(workspace), "fixture");
    }

    public static void CleanupFixtureRoot(string path, bool deleteOnDispose)
    {
        if (!Directory.Exists(path)) return;
        if (deleteOnDispose)
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var name = Path.GetFileName(entry);
            if (name is ".proofshift" or "assurance-report.json" or "assurance-report.txt" ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    public static async Task WriteIfRequestedAsync(object result, CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable("PROOFSHIFT_DEMO_RESULT_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions), cancellationToken);
        if (result is DemoAssuranceReport report)
            await File.WriteAllTextAsync(Path.ChangeExtension(path, ".txt"), report.ToHumanReadable(), cancellationToken);
    }
}