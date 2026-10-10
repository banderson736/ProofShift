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
            Assert.True(sample.RootElement.GetProperty("processCpuTimeMilliseconds").GetDouble() >= 0);
            Assert.Equal(Environment.ProcessorCount, sample.RootElement.GetProperty("logicalProcessorCount").GetInt32());
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

    [Fact]
    public async Task NestedStageReportsExclusiveTimeAndStableParentIdentityWithOverlappingChildren()
    {
        var recorder = new PerformanceRecorder("synthetic nested stages");
        using var parent = recorder.StartStage(PerformanceStageKind.Verification, "parent verification");
        var releaseChildren = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstChild = Task.Run(async () =>
        {
            using var child = recorder.StartStage(PerformanceStageKind.Verification, "first child");
            firstChildStarted.SetResult();
            await releaseChildren.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await firstChildStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var secondChild = Task.Run(async () =>
        {
            using var child = recorder.StartStage(PerformanceStageKind.Verification, "second child");
            secondChildStarted.SetResult();
            await releaseChildren.Task.WaitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await secondChildStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseChildren.SetResult();
        await Task.WhenAll(firstChild, secondChild).WaitAsync(TestContext.Current.CancellationToken);

        parent.Dispose();
        var run = recorder.Complete();
        var parentStage = Assert.Single(run.Stages, stage => stage.Name == "parent verification");
        var parentSequence = Assert.Single(parentStage.Measurements,
            measurement => measurement.Name == "stageSequence").Value;
        var parentInclusive = Assert.Single(parentStage.Measurements,
            measurement => measurement.Name == "inclusiveElapsedMicroseconds").Value;
        var parentExclusive = Assert.Single(parentStage.Measurements,
            measurement => measurement.Name == "exclusiveElapsedMicroseconds").Value;
        Assert.True(parentInclusive > 0);
        Assert.True(parentExclusive >= 0);
        Assert.True(parentExclusive < parentInclusive);

        var children = run.Stages.Where(stage => stage.Name is "first child" or "second child").ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, child =>
        {
            Assert.Equal(parentSequence, Assert.Single(child.Measurements,
                measurement => measurement.Name == "parentStageSequence").Value);
            var inclusive = Assert.Single(child.Measurements,
                measurement => measurement.Name == "inclusiveElapsedMicroseconds").Value;
            var exclusive = Assert.Single(child.Measurements,
                measurement => measurement.Name == "exclusiveElapsedMicroseconds").Value;
            Assert.True(inclusive > 0);
            Assert.InRange(exclusive, 0, inclusive);
        });
    }

    [Fact]
    public void ProcessingRootReconcilesTheStableSummaryDefinitionAndMeasuredStageParentage()
    {
        var recorder = new PerformanceRecorder("processing root reconciliation");
        using (var fixture = recorder.StartStage(PerformanceStageKind.Fixture, "fixture work"))
            Thread.Sleep(5);

        using (recorder.StartStage(PerformanceStageKind.Checkpoint, "checkpoint work"))
        {
            recorder.RecordMeasuredStage(PerformanceStageKind.Verification, "aggregate query counters", 25_000,
                measurements: [new PerformanceMeasurement("queryExecutions", 3, "queries")]);
        }

        using (recorder.StartStage(PerformanceStageKind.Verification, "verification work"))
            Thread.Sleep(5);

        var run = recorder.Complete();
        var root = Assert.Single(run.Stages, stage => stage.Kind == PerformanceStageKind.Processing &&
            stage.Name == "ProofShift processing");
        var fixtureMicroseconds = run.Stages.Where(stage => stage.Kind == PerformanceStageKind.Fixture)
            .Sum(stage => stage.ElapsedMicroseconds);
        var expectedProcessingMicroseconds = Math.Max(0, run.ElapsedMicroseconds - fixtureMicroseconds);
        var rootSequence = Assert.Single(root.Measurements,
            measurement => measurement.Name == "stageSequence").Value;

        Assert.Equal(expectedProcessingMicroseconds, run.ProofShiftProcessingMicroseconds);
        Assert.Equal(expectedProcessingMicroseconds, root.ElapsedMicroseconds);
        Assert.InRange(Assert.Single(root.Measurements,
            measurement => measurement.Name == "unattributedProcessingMicroseconds").Value, 0, 10_000);
        Assert.Equal(fixtureMicroseconds, Assert.Single(root.Measurements,
            measurement => measurement.Name == "excludedFixtureElapsedMicroseconds").Value);

        var checkpointStage = Assert.Single(run.Stages, stage => stage.Name == "checkpoint work");
        var checkpointSequence = Assert.Single(checkpointStage.Measurements,
            measurement => measurement.Name == "stageSequence").Value;
        Assert.Equal(rootSequence, Assert.Single(checkpointStage.Measurements,
            measurement => measurement.Name == "parentStageSequence").Value);
        var aggregateStage = Assert.Single(run.Stages, stage => stage.Name == "aggregate query counters");
        Assert.Equal(checkpointSequence, Assert.Single(aggregateStage.Measurements,
            measurement => measurement.Name == "parentStageSequence").Value);
        Assert.DoesNotContain(aggregateStage.Measurements, measurement =>
            measurement.Name is "stageStartOffsetMicroseconds" or "stageEndOffsetMicroseconds");
        Assert.All(run.Stages.Where(stage => stage.Kind == PerformanceStageKind.Processing &&
                stage.Name.StartsWith("Unscoped processing interval", StringComparison.Ordinal)), stage =>
        {
            Assert.Equal(rootSequence, Assert.Single(stage.Measurements,
                measurement => measurement.Name == "parentStageSequence").Value);
            Assert.True(Assert.Single(stage.Measurements,
                measurement => measurement.Name == "stageEndOffsetMicroseconds").Value >=
                Assert.Single(stage.Measurements,
                    measurement => measurement.Name == "stageStartOffsetMicroseconds").Value);
        });
    }
}