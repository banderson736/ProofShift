using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PensionDemoGeneratorCliTests
{
    [Fact]
    public async Task DemoGenerateWritesVersionedCleanAndDefectiveFixturesWithExactSeedCounts()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"proofshift-pension-demo-{Guid.NewGuid():N}");
        var cli = Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll");
        Assert.True(File.Exists(cli), "The CLI assembly should be copied to the test output.");
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(cli);
            start.ArgumentList.Add("demo");
            start.ArgumentList.Add("generate");
            start.ArgumentList.Add(outputDirectory);
            start.ArgumentList.Add("--scale");
            start.ArgumentList.Add("fast");
            start.ArgumentList.Add("--seed");
            start.ArgumentList.Add("20261003");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the ProofShift CLI.");
            var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(process.ExitCode == 0, $"Demo generation failed. stdout: {stdout} stderr: {stderr}");
            using var manifest = JsonDocument.Parse(stdout);
            var root = manifest.RootElement;
            Assert.Equal("proofshift-pension-generator-v1", root.GetProperty("generatorVersion").GetString());
            Assert.Equal("proofshift-pension-target-model-v1", root.GetProperty("targetModelVersion").GetString());
            Assert.Equal("proofshift-pension-defects-v1", root.GetProperty("defectSetVersion").GetString());
            Assert.Equal(100, root.GetProperty("sourceRecords").GetProperty("Member").GetInt64());
            Assert.Equal(5_000, root.GetProperty("sourceRecords").GetProperty("Contribution").GetInt64());
            Assert.Equal(100, root.GetProperty("correctedTargetRecords").GetProperty("Member").GetInt64());
            Assert.Equal(97, root.GetProperty("defectiveTargetRecords").GetProperty("Member").GetInt64());
            Assert.Equal(149, root.GetProperty("defects").GetProperty("total").GetInt32());
            Assert.Equal(2, root.GetProperty("falseReverseDeclarations").GetArrayLength());
            Assert.True(File.Exists(Path.Combine(outputDirectory, "source", "employment.csv")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "target-corrected", "employment.csv")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "target-defective", "employment.csv")));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "demo-manifest.json")));
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }
}
