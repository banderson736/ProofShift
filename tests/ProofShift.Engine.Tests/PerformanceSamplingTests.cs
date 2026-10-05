using System.Diagnostics;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Engine;
using Xunit;

namespace ProofShift.Engine.Tests;

public sealed class PerformanceSamplingTests
{
    [Fact]
    public async Task SamplesExecutingProcessActiveProgressAndSeparateScratchCategories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-sampling-{Guid.NewGuid():N}");
        var working = Path.Combine(root, "working-test");
        Directory.CreateDirectory(working);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(working, "working-set.sqlite"), new byte[17], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(working, "ledger.sqlite"), new byte[23], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(working, "working-set.sqlite-wal"), new byte[31], TestContext.Current.CancellationToken);
            var recorder = new PerformanceRecorder("synthetic sampling");
            using var stage = recorder.StartStage(PerformanceStageKind.Verification, "synthetic ingest");
            stage.AddArtifacts(5);
            var sampler = recorder.StartSampling(root, TimeSpan.FromSeconds(5));
            await sampler.DisposeAsync();
            await sampler.DisposeAsync();
            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "workload-samples.ndjson"), TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            using var sample = JsonDocument.Parse(lines[0]);
            using var process = Process.GetCurrentProcess();
            Assert.Equal(process.Id, sample.RootElement.GetProperty("processId").GetInt32());
            Assert.True(sample.RootElement.GetProperty("workingSetBytes").GetInt64() > 0);
            Assert.Equal(17, sample.RootElement.GetProperty("verificationWorkspaceBytes").GetInt64());
            Assert.Equal(23, sample.RootElement.GetProperty("verificationLedgerBytes").GetInt64());
            Assert.Equal(31, sample.RootElement.GetProperty("sqliteWalBytes").GetInt64());
            Assert.Equal(5, sample.RootElement.GetProperty("activeStages")[0].GetProperty("artifacts").GetInt64());
            Assert.DoesNotContain("working-test", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}