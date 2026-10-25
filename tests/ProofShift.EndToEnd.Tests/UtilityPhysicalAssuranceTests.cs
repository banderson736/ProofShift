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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UtilityExternalAndShadowScenarioPreservesExactResults()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"proofshift-utility-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var context = new DemoScenarioExecutionContext(workspace,
                Path.Combine(workspace, "fixture", "results", "assurance-report.json"),
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
            var report = execution.Data.Deserialize<DemoAssuranceReport>(JsonOptions)
                ?? throw new InvalidDataException("The Utility scenario returned an invalid report.");

            Assert.Equal("proofshift.utility", report.PackId);
            Assert.Equal("0.10.0", report.PackVersion);
            Assert.Equal(14, report.SeededDefectCount);
            Assert.Equal(10_200, report.CheckpointArtifactCount);
            Assert.Equal("FAILED", report.DefectiveExternalObservation.VerificationOutcome);
            Assert.Equal(26, report.DefectiveExternalObservation.VerificationFailureCount);
            Assert.Equal(26, report.DefectiveExternalObservation.EvidenceReferences.Count);
            Assert.Equal("PASSED", report.CorrectedExternalObservation.VerificationOutcome);
            Assert.Equal(10_200, report.CorrectedExternalObservation.TargetArtifactCount);
            Assert.Equal("PASSED", report.CorrectedShadow.VerificationOutcome);
            Assert.Equal(0, report.CorrectedShadow.VerificationFailureCount);
            Assert.Equal(10_200, report.CorrectedShadow.AccountedSourceCount);
            Assert.Equal(10_200, report.CorrectedShadow.LineagedTargetCount);
            Assert.Equal(0, report.CorrectedShadow.UnaccountedSourceCount);
            Assert.Equal(0, report.CorrectedShadow.UnexplainedTargetCount);
            Assert.Equal("PASSED", report.CorrectedShadow.RecoveryOutcome);
            Assert.Equal("PASSED", report.CorrectedShadow.RecoveryRehearsalOutcome);
            Assert.Equal("QUALIFIED", report.CorrectedShadow.Qualification);

            var fixtureRoot = Path.Combine(workspace, "fixture");
            var recoveryId = new DryRunId(Guid.Parse(report.CorrectedShadow.DryRunId));
            var persistedReport = await PersistedAssuranceReportTestHelper.BuildAsync(fixtureRoot,
                Path.Combine(fixtureRoot, "projected-corrected"), "Synthetic Utility Assurance",
                new UtilityPack(), recoveryId, TestContext.Current.CancellationToken);
            Assert.Equal(report.PackId, persistedReport.PackId);
            Assert.Equal(report.ClaimBoundary, persistedReport.ClaimBoundary);
            Assert.Equal("QUALIFIED", persistedReport.Qualification);
            Assert.Empty(persistedReport.FindingsByRuleAndCode);
            PersistedAssuranceReportTestHelper.AssertNoExpectedOrActualProperties(
                JsonSerializer.Serialize(persistedReport));
            await PersistedAssuranceReportTestHelper.AssertCliReportAndComparisonAsync(fixtureRoot,
                Path.Combine(fixtureRoot, "projected-corrected"), "Synthetic Utility Assurance",
                report.PackId, report.PackVersion, report.ClaimBoundary, recoveryId,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
