using System.Text.Json;
using ProofShift.DemoScenarios;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class PhysicalDemoScenarioTests
{
    [Fact]
    public async Task JusticeScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new JusticeDemoScenario());

        Assert.Equal("proofshift.justice", report.GetProperty("packId").GetString());
        Assert.Equal(15, report.GetProperty("seededDefectCount").GetInt32());
        Assert.Equal(16_200, report.GetProperty("checkpointArtifactCount").GetInt64());
        Assert.Equal(24, report.GetProperty("defectiveExternalObservation").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(24, report.GetProperty("defectiveExternalObservation").GetProperty("evidenceReferences").GetArrayLength());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unaccountedSourceCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unexplainedTargetCount").GetInt32());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryOutcome").GetString());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryRehearsalOutcome").GetString());
        Assert.Equal("QUALIFIED", report.GetProperty("correctedShadow").GetProperty("qualification").GetString());
    }

    [Fact]
    public async Task HcmScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new HcmDemoScenario());

        Assert.Equal("proofshift.hcm", report.GetProperty("packId").GetString());
        Assert.Equal(14, report.GetProperty("seededDefectCount").GetInt32());
        Assert.Equal(40_575, report.GetProperty("checkpointArtifactCount").GetInt64());
        Assert.Equal(25, report.GetProperty("defectiveExternalObservation").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(25, report.GetProperty("defectiveExternalObservation").GetProperty("evidenceReferences").GetArrayLength());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unaccountedSourceCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unexplainedTargetCount").GetInt32());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryOutcome").GetString());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryRehearsalOutcome").GetString());
        Assert.Equal("QUALIFIED", report.GetProperty("correctedShadow").GetProperty("qualification").GetString());
    }

    [Fact]
    public async Task FhirScenarioPreservesExactPhysicalAssuranceResults()
    {
        var report = await ExecuteAsync(new FhirDemoScenario());

        Assert.Equal("proofshift.fhir", report.GetProperty("packId").GetString());
        Assert.Equal(9, report.GetProperty("seededDefectCount").GetInt32());
        Assert.Equal(7_000, report.GetProperty("checkpointArtifactCount").GetInt64());
        Assert.Equal(15, report.GetProperty("defectiveExternalObservation").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(15, report.GetProperty("defectiveExternalObservation").GetProperty("evidenceReferences").GetArrayLength());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("verificationFailureCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unaccountedSourceCount").GetInt32());
        Assert.Equal(0, report.GetProperty("correctedShadow").GetProperty("unexplainedTargetCount").GetInt32());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryOutcome").GetString());
        Assert.Equal("PASSED", report.GetProperty("correctedShadow").GetProperty("recoveryRehearsalOutcome").GetString());
        Assert.Equal("QUALIFIED", report.GetProperty("correctedShadow").GetProperty("qualification").GetString());
    }

    private static async Task<JsonElement> ExecuteAsync(IDemoScenario scenario)
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

            return execution.Data;
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
