using System.Text.Json;
using ProofShift.DemoScenarios;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class PensionExternalCorpusVerificationTests
{
    [Fact]
    public Task CommittedReviewedPensionConfigurationPreservesAcceptedGraphAndRuleMeaning() =>
        PensionDemoScenario.ValidateCommittedReviewedConfigurationAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task FastDefectiveAndCorrectedDatasetsRunThroughVerificationServiceWithExactBusinessCounts()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"proofshift-pension-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var resultPath = Path.Combine(workspace, "integrated-assurance", "demo-summary.json");
            var context = new DemoScenarioExecutionContext(workspace, resultPath,
                TestContext.Current.CancellationToken, PreserveArtifacts: true);
            DemoScenarioExecutionResult execution;
            try
            {
                execution = await new PensionDemoScenario().ExecuteAsync(context);
            }
            catch (DemoScenarioUnavailableException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(exception.Message);
            }

            var summary = execution.Data;
            Assert.Equal("fast", summary.GetProperty("benchmarkScale").GetString());
            Assert.Equal(8_495, summary.GetProperty("checkpointArtifacts").GetInt64());
            Assert.Equal(8_595, summary.GetProperty("correctedProjectionTargets").GetInt64());
            Assert.Equal(149, summary.GetProperty("defectiveBusinessDiscrepancies").GetInt64());
            Assert.Equal(0, summary.GetProperty("correctedBusinessDiscrepancies").GetInt64());
            Assert.Equal(0, summary.GetProperty("unaccountedSources").GetInt64());
            Assert.Equal(0, summary.GetProperty("unexplainedTargets").GetInt64());
            Assert.Equal("NOT QUALIFIED", summary.GetProperty("defectiveQualification").GetString());
            Assert.Equal("QUALIFIED", summary.GetProperty("correctedQualification").GetString());
            Assert.Equal("PASSED", summary.GetProperty("falseReverseVerificationOutcome").GetString());
            Assert.Equal("NOT QUALIFIED", summary.GetProperty("falseReverseQualification").GetString());
            Assert.Equal(2, summary.GetProperty("falseReverseRejectedEdges").GetInt32());
            Assert.Equal(2, summary.GetProperty("falseReverseBusinessDiscrepancies").GetInt64());
            Assert.Equal(0, summary.GetProperty("falseReverseVerificationFailures").GetInt64());
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
