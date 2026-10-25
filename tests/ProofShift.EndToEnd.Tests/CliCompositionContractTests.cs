using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class CliCompositionContractTests
{
    [Fact]
    public async Task VersionAndConfigurationCommandsPreserveOutputContracts()
    {
        var root = CreateDirectory("cli-configuration-contract");
        try
        {
            var version = await RunAsync(root, "--version");
            Assert.Equal(0, version.ExitCode);
            Assert.Equal("ProofShift 0.1.0-preview.1", version.Output.Trim());
            Assert.Empty(version.Error);

            var structuredVersion = await RunAsync(root, "version", "--json");
            Assert.Equal(0, structuredVersion.ExitCode);
            using (var json = JsonDocument.Parse(structuredVersion.Output))
            {
                Assert.Equal("0.1.0-preview.1", json.RootElement.GetProperty("productVersion").GetString());
                Assert.DoesNotContain("0.11D", structuredVersion.Output, StringComparison.Ordinal);
            }

            var project = Path.Combine(root, "project");
            var initialized = await RunAsync(root, "init", project);
            Assert.Equal(0, initialized.ExitCode);
            var configuration = Path.Combine(project, "proofshift.yaml");

            var validation = await RunAsync(root, "validate", configuration, "--json");
            Assert.Equal(0, validation.ExitCode);
            using (var json = JsonDocument.Parse(validation.Output))
            {
                Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
                Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("issues").ValueKind);
            }

            var humanValidation = await RunAsync(root, "validate", configuration);
            Assert.Equal(0, humanValidation.ExitCode);
            Assert.Contains("ProofShift Configuration Validation", humanValidation.Output, StringComparison.Ordinal);
            Assert.Contains("RESULT:", humanValidation.Output, StringComparison.Ordinal);
            Assert.Empty(humanValidation.Error);

            var malformed = await RunAsync(root, "validate");
            Assert.Equal(2, malformed.ExitCode);
            Assert.Empty(malformed.Output);
            Assert.Contains("Usage: proofshift validate", malformed.Error, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MigrationCommandsKeepJsonHumanAndErrorDestinations()
    {
        var root = CreateDirectory("cli-migration-contract");
        try
        {
            var project = Path.Combine(root, "project");
            Assert.Equal(0, (await RunAsync(root, "init", project)).ExitCode);
            var configuration = Path.Combine(project, "proofshift.yaml");
            await File.AppendAllTextAsync(Path.Combine(project, "data", "source.csv"), "1;sample\n",
                TestContext.Current.CancellationToken);

            var plan = await RunAsync(root, "plan", configuration, "--json");
            Assert.Equal(0, plan.ExitCode);
            using (var json = JsonDocument.Parse(plan.Output))
            {
                Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
                Assert.Equal(1, json.RootElement.GetProperty("graphVersion").GetInt32());
                Assert.True(json.RootElement.GetProperty("nodes").GetInt32() > 0);
            }

            var humanPlan = await RunAsync(root, "plan", configuration);
            Assert.Equal(0, humanPlan.ExitCode);
            Assert.Contains("ProofShift Migration Graph", humanPlan.Output, StringComparison.Ordinal);
            Assert.Contains("RESULT:", humanPlan.Output, StringComparison.Ordinal);

            var inspection = await RunAsync(project, "inspect", configuration, "--json");
            Assert.True(inspection.ExitCode == 0, inspection.Output + inspection.Error);
            using (var json = JsonDocument.Parse(inspection.Output))
            {
                Assert.True(json.RootElement.GetProperty("valid").GetBoolean());
                Assert.Contains(json.RootElement.GetProperty("sources").EnumerateArray(),
                    source => source.GetProperty("connector").GetString() == "csv");
            }

            var snapshot = await RunAsync(project, "snapshot", configuration, "--json");
            Assert.Equal(0, snapshot.ExitCode);
            using (var json = JsonDocument.Parse(snapshot.Output))
            {
                Assert.Equal("complete", json.RootElement.GetProperty("status").GetString());
                Assert.True(json.RootElement.GetProperty("capturedSourceNodes").GetInt32() > 0);
                Assert.True(json.RootElement.GetProperty("capturedArtifacts").GetInt64() > 0);
            }

            var failedProject = await RunAsync(project, "project", configuration, "--checkpoint", "missing-checkpoint");
            Assert.Equal(1, failedProject.ExitCode);
            Assert.Empty(failedProject.Output);
            Assert.Equal("Shadow projection could not be completed.", failedProject.Error.Trim());

            var malformedVerify = await RunAsync(root, "verify", configuration, "--checkpoint", "not-a-guid",
                "--projection", "not-a-guid");
            Assert.Equal(2, malformedVerify.ExitCode);
            Assert.Empty(malformedVerify.Output);
            Assert.Equal("Checkpoint and projection identifiers must be GUIDs.", malformedVerify.Error.Trim());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AssuranceCommandsPreserveArgumentErrorsAndDemoCatalogOutput()
    {
        var root = CreateDirectory("cli-assurance-contract");
        try
        {
            var project = Path.Combine(root, "project");
            Assert.Equal(0, (await RunAsync(root, "init", project)).ExitCode);
            var configuration = Path.Combine(project, "proofshift.yaml");

            var dryRun = await RunAsync(root, "dry-run", Path.Combine(root, "missing.yaml"), "--json");
            Assert.Equal(1, dryRun.ExitCode);
            Assert.Empty(dryRun.Output);
            Assert.Equal("Configuration validation failed; dry run was not started.", dryRun.Error.Trim());

            var evidence = await RunAsync(root, "evidence", configuration, "--run", "not-a-guid");
            Assert.Equal(2, evidence.ExitCode);
            Assert.Empty(evidence.Output);
            Assert.Equal("Verification run identifier must be a GUID.", evidence.Error.Trim());

            var recovery = await RunAsync(root, "recovery", configuration, "--run", "not-a-guid");
            Assert.Equal(2, recovery.ExitCode);
            Assert.Empty(recovery.Output);
            Assert.Equal("Dry-run identifier must be a GUID.", recovery.Error.Trim());

            var report = await RunAsync(root, "report", configuration, "--run", "not-a-guid");
            Assert.Equal(2, report.ExitCode);
            Assert.Empty(report.Output);
            Assert.Equal("Dry-run identifier must be a GUID.", report.Error.Trim());

            var compare = await RunAsync(root, "compare", configuration, "--before", "not-a-guid",
                "--after", Guid.NewGuid().ToString("D"));
            Assert.Equal(2, compare.ExitCode);
            Assert.Empty(compare.Output);
            Assert.Equal("Dry-run identifiers must be GUIDs.", compare.Error.Trim());

            var missingRun = await RunAsync(root, "verify", configuration);
            Assert.Equal(2, missingRun.ExitCode);
            Assert.Empty(missingRun.Output);
            Assert.Contains("Usage: proofshift validate", missingRun.Error, StringComparison.Ordinal);

            var repositoryRoot = FindRepositoryRoot();
            var demoList = await RunAsync(repositoryRoot, "demo", "list", "--json");
            Assert.Equal(0, demoList.ExitCode);
            using var scenarios = JsonDocument.Parse(demoList.Output);
            Assert.NotEmpty(scenarios.RootElement.EnumerateArray());
            Assert.Contains(scenarios.RootElement.EnumerateArray(),
                scenario => scenario.GetProperty("id").GetString() == "pension");
            Assert.Empty(demoList.Error);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateDirectory(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $".{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ProofShift.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("ProofShift repository root was not found.");
    }

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
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
