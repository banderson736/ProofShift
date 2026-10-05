using System.Diagnostics;
using System.Text.Json;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class AuthoringCliTests
{
    [Theory]
    [InlineData("generic")]
    [InlineData("pension")]
    public async Task InitCreatesCredentialFreeStrictlyValidProjectAndRefusesOverwrite(string template)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-authoring-{Guid.NewGuid():N}");
        try
        {
            var init = await RunAsync("init", root, "--template", template);
            Assert.True(init.ExitCode == 0, init.Error);
            var path = Path.Combine(root, "proofshift.yaml");
            var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var validation = await RunAsync("validate", path, "--strict", "--json");
            Assert.True(validation.ExitCode == 0, validation.Output + validation.Error);
            using var document = JsonDocument.Parse(validation.Output);
            Assert.True(document.RootElement.GetProperty("valid").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("issues").EnumerateArray());
            var target = await File.ReadAllTextAsync(Path.Combine(root, "systems", "target.yaml"), TestContext.Current.CancellationToken);
            Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", target, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", target, StringComparison.OrdinalIgnoreCase);
            var discoveryPath = Path.Combine(root, "discovery", "source");
            var discovered = await RunAsync("discover", path, "--system", "source", "--endpoint", "records", "--output", discoveryPath, "--json");
            Assert.True(discovered.ExitCode == 0, discovered.Error);
            using var physical = JsonDocument.Parse(discovered.Output);
            Assert.Equal("csv", physical.RootElement.GetProperty("connectorId").GetString());
            Assert.True(File.Exists(Path.Combine(discoveryPath, "manifest.json")));
            var second = await RunAsync("init", root);
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("PSAUTHOR011", second.Error, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuleCatalogAndSchemaAreDeterministicAndUnknownRulesFailClosed()
    {
        var first = await RunAsync("rules", "schema");
        var second = await RunAsync("rules", "schema");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.Output, second.Output);
        using var schema = JsonDocument.Parse(first.Output);
        Assert.Equal(2, schema.RootElement.GetProperty("properties").GetProperty("version").GetProperty("const").GetInt32());
        var description = await RunAsync("rules", "describe", "pension-contribution-total", "--json");
        using var descriptor = JsonDocument.Parse(description.Output);
        var tolerance = descriptor.RootElement.GetProperty("options").EnumerateArray()
            .Single(option => option.GetProperty("name").GetString() == "tolerance");
        Assert.Equal(0.01m, tolerance.GetProperty("schema").GetProperty("default").GetDecimal());
        var unknown = await RunAsync("rules", "describe", "not-installed");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("PSRULE008", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EffectiveProjectRuleAndMappingExplainUseCompiledConfigurationAndNeverResolveSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-explain-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, (await RunAsync("init", root)).ExitCode);
            var path = Path.Combine(root, "proofshift.yaml");
            var project = await RunAsync("explain", path, "project", "--json");
            Assert.Equal(0, project.ExitCode);
            using var projectJson = JsonDocument.Parse(project.Output);
            Assert.Equal(64, projectJson.RootElement.GetProperty("configurationHash").GetString()!.Length);
            Assert.Equal(64, projectJson.RootElement.GetProperty("graphHash").GetString()!.Length);
            var rule = await RunAsync("explain", path, "rule", "attributes", "--json");
            Assert.Equal(0, rule.ExitCode);
            Assert.Contains("attribute-comparison", rule.Output, StringComparison.Ordinal);
            var mapping = await RunAsync("explain", path, "mapping", "record-copy", "--json");
            Assert.Equal(0, mapping.ExitCode);
            Assert.Contains("source-records", mapping.Output, StringComparison.Ordinal);
            Assert.Contains("target-records", mapping.Output, StringComparison.Ordinal);
            var effective = await RunAsync("config", "show", path, "--effective", "--json");
            Assert.Equal(0, effective.ExitCode);
            Assert.Contains("PROOFSHIFT_TARGET_CONNECTION", effective.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", effective.Output, StringComparison.OrdinalIgnoreCase);
            using var effectiveJson = JsonDocument.Parse(effective.Output);
            Assert.Equal(projectJson.RootElement.GetProperty("configurationHash").GetString(),
                effectiveJson.RootElement.GetProperty("configurationHash").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScaffoldAndReviewedCsvImportCompileNormallyWhileStrictRejectsSuggestions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-scaffold-cli-{Guid.NewGuid():N}");
        try
        {
            var context = new ConnectorContext("logical", "records", new ConnectorId("postgres"), "discovery", "Physical.Uninterpreted", new RuntimeConfiguration([]));
            var source = PhysicalDiscovery.Create(context, "postgres", "0.1.0",
                [new("public", "records", "table", [new("id", "integer", false, 1)], [new("pk", true, ["id"])], [])]);
            var target = PhysicalDiscovery.Create(context, "postgres", "0.1.0", source.Objects);
            var sourcePath = Path.Combine(root, "source-discovery");
            var targetPath = Path.Combine(root, "target-discovery");
            await PhysicalDiscoveryStore.SaveAsync(sourcePath, source, TestContext.Current.CancellationToken);
            await PhysicalDiscoveryStore.SaveAsync(targetPath, target, TestContext.Current.CancellationToken);
            var generated = Path.Combine(root, "suggested");
            var scaffold = await RunAsync("scaffold", "--source-discovery", sourcePath, "--target-discovery", targetPath, "--output", generated);
            Assert.True(scaffold.ExitCode == 0, scaffold.Error);
            var strict = await RunAsync("validate", Path.Combine(generated, "proofshift.yaml"), "--strict", "--json");
            Assert.Equal(1, strict.ExitCode);
            Assert.Contains("PSAUTHOR001", strict.Output, StringComparison.Ordinal);
            var csv = Path.Combine(root, "reviewed.csv");
            await File.WriteAllTextAsync(csv, "Source Entity,Source Field,Target Entity,Target Field,Transformation,Required,Notes,Review Status\npublic.records,id,public.records,id,copy,true,Reviewed,confirmed\n", TestContext.Current.CancellationToken);
            var reviewed = Path.Combine(root, "confirmed");
            var import = await RunAsync("mapping", "import", "--csv", csv, "--source-discovery", sourcePath, "--target-discovery", targetPath, "--output", reviewed);
            Assert.True(import.ExitCode == 0, import.Error);
            var valid = await RunAsync("validate", Path.Combine(reviewed, "proofshift.yaml"), "--strict", "--json");
            Assert.True(valid.ExitCode == 0, valid.Output + valid.Error);
            Assert.Contains("\"valid\": true", valid.Output, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }
}