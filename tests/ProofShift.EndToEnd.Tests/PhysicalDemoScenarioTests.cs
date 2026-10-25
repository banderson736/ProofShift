using System.Text.Json;
using ProofShift.DemoScenarios;
using ProofShift.Reporting;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class PhysicalDemoScenarioTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task JusticeScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new JusticeDemoScenario());

        Assert.Equal("proofshift.justice", report.PackId);
        Assert.Equal(15, report.SeededDefectCount);
        Assert.Equal(16_200, report.CheckpointArtifactCount);
        Assert.Equal(24, report.DefectiveExternalObservation.VerificationFailureCount);
        Assert.Equal(24, report.DefectiveExternalObservation.EvidenceReferences.Count);
        Assert.Equal(0, report.CorrectedShadow.VerificationFailureCount);
        Assert.Equal(0, report.CorrectedShadow.UnaccountedSourceCount);
        Assert.Equal(0, report.CorrectedShadow.UnexplainedTargetCount);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryOutcome);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryRehearsalOutcome);
        Assert.Equal("QUALIFIED", report.CorrectedShadow.Qualification);
    }

    [Fact]
    public async Task HcmScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new HcmDemoScenario());

        Assert.Equal("proofshift.hcm", report.PackId);
        Assert.Equal(14, report.SeededDefectCount);
        Assert.Equal(40_575, report.CheckpointArtifactCount);
        Assert.Equal(25, report.DefectiveExternalObservation.VerificationFailureCount);
        Assert.Equal(25, report.DefectiveExternalObservation.EvidenceReferences.Count);
        Assert.Equal(0, report.CorrectedShadow.VerificationFailureCount);
        Assert.Equal(0, report.CorrectedShadow.UnaccountedSourceCount);
        Assert.Equal(0, report.CorrectedShadow.UnexplainedTargetCount);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryOutcome);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryRehearsalOutcome);
        Assert.Equal("QUALIFIED", report.CorrectedShadow.Qualification);
    }

    [Fact]
    public async Task FhirScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new FhirDemoScenario());

        Assert.Equal("proofshift.fhir", report.PackId);
        Assert.Equal(9, report.SeededDefectCount);
        Assert.Equal(7_000, report.CheckpointArtifactCount);
        Assert.Equal(15, report.DefectiveExternalObservation.VerificationFailureCount);
        Assert.Equal(15, report.DefectiveExternalObservation.EvidenceReferences.Count);
        Assert.Equal(0, report.CorrectedShadow.VerificationFailureCount);
        Assert.Equal(0, report.CorrectedShadow.UnaccountedSourceCount);
        Assert.Equal(0, report.CorrectedShadow.UnexplainedTargetCount);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryOutcome);
        Assert.Equal("PASSED", report.CorrectedShadow.RecoveryRehearsalOutcome);
        Assert.Equal("QUALIFIED", report.CorrectedShadow.Qualification);
    }

    private static async Task<DemoAssuranceReport> ExecuteAsync(IDemoScenario scenario)
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"proofshift-{scenario.Id}-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var context = new DemoScenarioExecutionContext(workspace,
                Path.Combine(workspace, "fixture", "assurance-report.json"),
                TestContext.Current.CancellationToken);
            DemoScenarioExecutionResult execution;
            try
            {
                execution = await scenario.ExecuteAsync(context);
            }
            catch (DemoScenarioUnavailableException exception)
            {
                throw Xunit.Sdk.SkipException.ForSkip(exception.Message);
            }

            return execution.Data.Deserialize<DemoAssuranceReport>(JsonOptions)
                ?? throw new InvalidDataException($"The {scenario.Id} scenario returned an invalid report.");
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
