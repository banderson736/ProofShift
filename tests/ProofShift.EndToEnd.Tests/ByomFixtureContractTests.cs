using System.Text.Json;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class ByomFixtureContractTests
{
    private static readonly string[] ExpectedDefectiveShadowRuleIds =
    [
        "budget-attributes",
        "budget-total",
        "proofshift.verification.materialized-state"
    ];

    [Fact]
    public void PublicWorksFixtureIsSyntheticDeterministicCredentialFreeAndOutsideDemoRegistry()
    {
        var repositoryRoot = DemoScenarioTestPaths.FindRepositoryRoot();
        var fixtureRoot = Path.Combine(repositoryRoot, "scenarios", "byom-public-works");
        var fixtureFiles = Directory.GetFiles(fixtureRoot, "*", SearchOption.AllDirectories);
        var fixtureText = string.Join(Environment.NewLine,
            fixtureFiles.Select(File.ReadAllText));
        using var expectations = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "expectations.json")));
        var expected = expectations.RootElement;
        var source = expected.GetProperty("source");
        var defective = expected.GetProperty("defectiveTarget");
        var corrected = expected.GetProperty("correctedTarget");

        Assert.Equal("proofshift-byom-expectations-v1", expected.GetProperty("format").GetString());
        Assert.Equal(source.GetProperty("total").GetInt32(),
            source.GetProperty("assets").GetInt32() + source.GetProperty("workOrders").GetInt32() +
            source.GetProperty("history").GetInt32());
        Assert.Equal(defective.GetProperty("total").GetInt32(),
            defective.GetProperty("assets").GetInt32() + defective.GetProperty("workOrders").GetInt32() +
            defective.GetProperty("history").GetInt32());
        Assert.Equal(corrected.GetProperty("total").GetInt32(),
            corrected.GetProperty("assets").GetInt32() + corrected.GetProperty("workOrders").GetInt32() +
            corrected.GetProperty("history").GetInt32());
        Assert.Equal(102, source.GetProperty("total").GetInt32());
        Assert.Equal(100, defective.GetProperty("total").GetInt32());
        Assert.Equal(102, corrected.GetProperty("total").GetInt32());
        Assert.Equal(2, defective.GetProperty("findingCodeCounts").GetProperty("MissingTarget").GetInt32());
        Assert.Equal(2, defective.GetProperty("findingCodeCounts").GetProperty("AttributeMismatch").GetInt32());
        Assert.Equal(2, defective.GetProperty("findingCodeCounts").GetProperty("UnaccountedArtifact").GetInt32());
        Assert.Equal(3, defective.GetProperty("verificationFailedRules").GetInt32());
        Assert.Equal(6, defective.GetProperty("findingRecords").GetInt32());
        Assert.Equal(0, corrected.GetProperty("verificationFailedRules").GetInt32());
        Assert.False(corrected.GetProperty("recoveryAssessed").GetBoolean());
        Assert.False(corrected.GetProperty("qualificationAvailable").GetBoolean());
        Assert.Equal(
            ExpectedDefectiveShadowRuleIds,
            expected.GetProperty("shadow").GetProperty("defectiveFailedRuleIds").EnumerateArray()
                .Select(ruleId => ruleId.GetString()).ToArray());

        var configuration = File.ReadAllText(Path.Combine(fixtureRoot, "discovery", "proofshift.yaml"));
        Assert.Contains("packs: []", configuration, StringComparison.Ordinal);
        Assert.Contains("Synthetic", fixtureText, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)([A-Z]:\\Users\\[^""'\s]+|/Users/[^/\s""']+|/home/[^/\s""']+)",
            fixtureText);
        Assert.DoesNotMatch(@"(?i)(password\s*=\s*[^$<\s][^;\s]*|pwd\s*=\s*[^$<\s][^;\s]*|gh[pousr]_[A-Za-z0-9]{20,})",
            fixtureText);

        var demoRunnerRoot = Path.Combine(repositoryRoot, "src", "ProofShift.DemoRunner");
        var demoRunnerSources = string.Join(Environment.NewLine,
            Directory.GetFiles(demoRunnerRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("ByomDemoScenario", demoRunnerSources, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicWorksDemoScenario", demoRunnerSources, StringComparison.Ordinal);

        var packagingScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "package-evaluation.ps1"));
        var smokeScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "smoke-evaluation-package.ps1"));
        var runner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-byom-evaluation.ps1"));
        var workspaceRunner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts",
            "run-guided-workspace-evaluation.ps1"));
        Assert.Contains("scenarios/** including the synthetic BYOM evaluation kit", packagingScript,
            StringComparison.Ordinal);
        Assert.Contains("scenarios/byom-public-works/expectations.json", smokeScript, StringComparison.Ordinal);
        Assert.Contains("scenarios/byom-public-works/database/postgres-shadow-target-schema.sql", smokeScript,
            StringComparison.Ordinal);
        Assert.Contains("run-byom-evaluation.ps1", smokeScript, StringComparison.Ordinal);
        Assert.Contains("Packaged workspace command '$command' did not return valid JSON ($parseExceptionType).",
            workspaceRunner, StringComparison.Ordinal);
        Assert.Contains("Where-Object { $_ -match '^\\s*PS[A-Z0-9]+:' }", workspaceRunner,
            StringComparison.Ordinal);
        Assert.Contains("Output omitted.", workspaceRunner, StringComparison.Ordinal);

        var defectiveVerification = runner.IndexOf("$defectiveShadowVerification = Invoke-ProductJson",
            StringComparison.Ordinal);
        Assert.True(defectiveVerification >= 0);
        var evidenceRead = runner.IndexOf("$defectiveShadowEvidence = Invoke-ProductJson",
            defectiveVerification, StringComparison.Ordinal);
        Assert.True(evidenceRead > defectiveVerification);
        var diagnosticOutput = runner.IndexOf("Defective shadow failed findings:", evidenceRead,
            StringComparison.Ordinal);
        Assert.True(diagnosticOutput > evidenceRead);
        var failedRuleAssertion = runner.IndexOf("Defective customer-data failed rule IDs", diagnosticOutput,
            StringComparison.Ordinal);

        Assert.True(failedRuleAssertion > diagnosticOutput);
        Assert.Contains("findingId = $finding.id", runner, StringComparison.Ordinal);
        Assert.Contains("ruleType = $findingRuleType", runner, StringComparison.Ordinal);
        Assert.Contains("$_.result -eq \"Fail\"", runner, StringComparison.Ordinal);
        Assert.Contains("inputReferences =", runner, StringComparison.Ordinal);
        Assert.Contains("exceptionAnnotation =", runner, StringComparison.Ordinal);
        Assert.Contains("Write-Host (\"Defective shadow failed findings: \"", runner, StringComparison.Ordinal);
        Assert.Contains("Write-Host (\"Defective shadow exception evidence: \"", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowBudgetFixtureIsIsolatedFromExternalObservationDatabases()
    {
        var repositoryRoot = DemoScenarioTestPaths.FindRepositoryRoot();
        var fixtureRoot = Path.Combine(repositoryRoot, "scenarios", "byom-public-works");
        var sourceSql = File.ReadAllText(Path.Combine(fixtureRoot, "database", "sqlserver-source.sql"));
        var externalSourceSql = sourceSql.Split("USE byom_shadow;", StringSplitOptions.None)[0];
        var externalTargetSql = File.ReadAllText(Path.Combine(fixtureRoot, "database", "postgres-target-schema.sql"));
        var shadowTargetSql = File.ReadAllText(Path.Combine(fixtureRoot, "database", "postgres-shadow-target-schema.sql"));
        var shadowSourceSystem = File.ReadAllText(Path.Combine(fixtureRoot, "shadow", "systems", "source.yaml"));
        var shadowTargetSystem = File.ReadAllText(Path.Combine(fixtureRoot, "shadow", "systems", "shadow.yaml"));
        var runner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "run-byom-evaluation.ps1"));

        Assert.DoesNotContain("asset_budgets", externalSourceSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asset_budgets", externalTargetSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("asset_budgets", shadowTargetSql, StringComparison.Ordinal);
        Assert.Contains("PROOFSHIFT_SHADOW_SOURCE_CONNECTION", shadowSourceSystem, StringComparison.Ordinal);
        Assert.Contains("PROOFSHIFT_SHADOW_TARGET_CONNECTION", shadowTargetSystem, StringComparison.Ordinal);
        Assert.Contains("Database=byom_shadow;", runner, StringComparison.Ordinal);
    }
}
