using System.Text.Json;
using ProofShift.DemoScenarios;
using ProofShift.Domain;
using ProofShift.Packs.Utility;
using ProofShift.Reporting;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class UtilityPhysicalAssuranceTests
{
    [Fact]
    public async Task UtilityExternalAndShadowScenarioPreservesExactResults()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"proofshift-utility-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var context = new DemoScenarioExecutionContext(workspace,
                Path.Combine(workspace, "fixture", "results", "assurance-report.json"),
                DemoScenarioTestPaths.FindRepositoryRoot(),
                TestContext.Current.CancellationToken, PreserveArtifacts: true);
            DemoScenarioExecutionResult execution;
            try
            {
                execution = await new UtilityDemoScenario().ExecuteAsync(context);
            }
            catch (DemoScenarioUnavailableException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(exception.Message);
            }
            var report = execution.Data;
            var defectiveObservation = report.GetProperty("defectiveExternalObservation");
            var correctedObservation = report.GetProperty("correctedExternalObservation");
            var correctedShadow = report.GetProperty("correctedShadow");

            Assert.Equal("proofshift.utility", report.GetProperty("packId").GetString());
            Assert.Equal("0.10.0", report.GetProperty("packVersion").GetString());
            Assert.Equal(14, report.GetProperty("seededDefectCount").GetInt32());
            Assert.Equal(10_200, report.GetProperty("checkpointArtifactCount").GetInt64());
            Assert.Equal("FAILED", defectiveObservation.GetProperty("verificationOutcome").GetString());
            Assert.Equal(26, defectiveObservation.GetProperty("verificationFailureCount").GetInt32());
            Assert.Equal(26, defectiveObservation.GetProperty("evidenceReferences").GetArrayLength());
            Assert.Equal("PASSED", correctedObservation.GetProperty("verificationOutcome").GetString());
            Assert.Equal(10_200, correctedObservation.GetProperty("targetArtifactCount").GetInt64());
            Assert.Equal("PASSED", correctedShadow.GetProperty("verificationOutcome").GetString());
            Assert.Equal(0, correctedShadow.GetProperty("verificationFailureCount").GetInt32());
            Assert.Equal(10_200, correctedShadow.GetProperty("accountedSourceCount").GetInt64());
            Assert.Equal(10_200, correctedShadow.GetProperty("lineagedTargetCount").GetInt64());
            Assert.Equal(0, correctedShadow.GetProperty("unaccountedSourceCount").GetInt64());
            Assert.Equal(0, correctedShadow.GetProperty("unexplainedTargetCount").GetInt64());
            Assert.Equal("PASSED", correctedShadow.GetProperty("recoveryOutcome").GetString());
            Assert.Equal("PASSED", correctedShadow.GetProperty("recoveryRehearsalOutcome").GetString());
            Assert.Equal("QUALIFIED", correctedShadow.GetProperty("qualification").GetString());

            var fixtureRoot = Path.Combine(workspace, "fixture");
            var recoveryId = new DryRunId(Guid.Parse(correctedShadow.GetProperty("dryRunId").GetString()!));
            var persistedReport = await PersistedAssuranceReportTestHelper.BuildAsync(fixtureRoot,
                Path.Combine(fixtureRoot, "projected-corrected"), "Synthetic Utility Assurance",
                new UtilityPack(), recoveryId, TestContext.Current.CancellationToken);
            var packId = report.GetProperty("packId").GetString()!;
            var packVersion = report.GetProperty("packVersion").GetString()!;
            var claimBoundary = report.GetProperty("claimBoundary").GetString()!;
            Assert.Equal(packId, persistedReport.PackId);
            Assert.Equal(claimBoundary, persistedReport.ClaimBoundary);
            Assert.Equal("QUALIFIED", persistedReport.Qualification);
            Assert.Empty(persistedReport.FindingsByRuleAndCode);
            PersistedAssuranceReportTestHelper.AssertNoExpectedOrActualProperties(
                JsonSerializer.Serialize(persistedReport));
            await PersistedAssuranceReportTestHelper.AssertCliReportAndComparisonAsync(fixtureRoot,
                Path.Combine(fixtureRoot, "projected-corrected"), "Synthetic Utility Assurance",
                packId, packVersion, claimBoundary, recoveryId,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
