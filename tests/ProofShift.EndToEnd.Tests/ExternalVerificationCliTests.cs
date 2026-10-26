using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class ExternalVerificationCliTests
{
    [Fact]
    public async Task ExternalVerificationPersistsBoundReportsAndComparesWithoutProjectionOrRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-external-cli-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var targetRoot = Path.Combine(root, "target");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        var sourceVariable = "PROOFSHIFT_CLI_TEST_SOURCE_ROOT";
        var targetVariable = "PROOFSHIFT_CLI_TEST_TARGET_ROOT";
        var previousSource = Environment.GetEnvironmentVariable(sourceVariable);
        var previousTarget = Environment.GetEnvironmentVariable(targetVariable);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "work-1.txt"),
                "alpha", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "work-2.txt"),
                "beta", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(targetRoot, "work-1.txt"),
                "incorrect", TestContext.Current.CancellationToken);
            await WriteProjectAsync(root, TestContext.Current.CancellationToken);
            Environment.SetEnvironmentVariable(sourceVariable, sourceRoot);
            Environment.SetEnvironmentVariable(targetVariable, targetRoot);

            var configuration = Path.Combine(root, "proofshift.yaml");
            var snapshot = await RunCliAsync("snapshot", configuration, "--json");
            Assert.Equal(0, snapshot.ExitCode);
            using var checkpoint = JsonDocument.Parse(snapshot.Output);
            var checkpointId = checkpoint.RootElement.GetProperty("checkpointId").GetString()!;
            Assert.Equal("complete", checkpoint.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, checkpoint.RootElement.GetProperty("capturedArtifacts").GetInt64());

            var invalidLabel = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "", "--json");
            Assert.Equal(2, invalidLabel.ExitCode);
            Assert.Contains("non-empty checkpoint and observation label", invalidLabel.Error, StringComparison.Ordinal);
            Assert.Empty(invalidLabel.Output);

            var unavailableTarget = Path.Combine(root, "target-path-must-not-be-reported");
            Environment.SetEnvironmentVariable(targetVariable, unavailableTarget);
            var targetFailure = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "external-target-unavailable", "--json");
            Assert.Equal(1, targetFailure.ExitCode);
            Assert.Empty(targetFailure.Output);
            Assert.Contains("External target Verification failed", targetFailure.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(unavailableTarget, targetFailure.Error, StringComparison.Ordinal);
            Environment.SetEnvironmentVariable(targetVariable, targetRoot);

            Directory.Move(sourceRoot, Path.Combine(root, "source-offline"));
            Assert.False(Directory.Exists(sourceRoot));

            var defectiveTargetHash = HashFile(Path.Combine(targetRoot, "work-1.txt"));
            var defective = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "external-defective-001", "--json");
            Assert.True(defective.ExitCode == 1, defective.Output + defective.Error);
            Assert.False(string.IsNullOrWhiteSpace(defective.Output), defective.Error);
            using var defectiveResult = JsonDocument.Parse(defective.Output);
            var defectiveRoot = defectiveResult.RootElement;
            Assert.Equal("external-target-verification", defectiveRoot.GetProperty("assuranceScope").GetString());
            Assert.Equal("failed", defectiveRoot.GetProperty("verificationOutcome").GetString());
            Assert.Equal(2, defectiveRoot.GetProperty("sourceArtifactCount").GetInt64());
            Assert.Equal(1, defectiveRoot.GetProperty("targetArtifactCount").GetInt64());
            Assert.Equal(2, defectiveRoot.GetProperty("verificationFailureCount").GetInt32());
            Assert.NotEqual(defectiveRoot.GetProperty("observationRunId").GetString(),
                defectiveRoot.GetProperty("verificationRunId").GetString());
            Assert.Equal(1, defectiveRoot.GetProperty("accountedSourceCount").GetInt64());
            Assert.Equal(1, defectiveRoot.GetProperty("unaccountedSourceCount").GetInt64());
            Assert.DoesNotContain("incorrect", defective.Output, StringComparison.Ordinal);
            Assert.Equal(defectiveTargetHash, HashFile(Path.Combine(targetRoot, "work-1.txt")));

            var defectiveRunId = defectiveRoot.GetProperty("verificationRunId").GetString()!;
            var ledgerPath = Path.Combine(root, ".proofshift", "verification-ledgers",
                Guid.Parse(defectiveRunId).ToString("N"), "ledger.sqlite");
            Assert.True(File.Exists(ledgerPath));
            var temporaryDirectory = Path.Combine(root, ".proofshift", "temporary");
            Assert.True(Directory.Exists(temporaryDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
            Directory.Delete(temporaryDirectory, recursive: true);

            var evidence = await RunCliAsync("evidence", configuration, "--run", defectiveRunId, "--json");
            Assert.Equal(0, evidence.ExitCode);
            using var evidenceResult = JsonDocument.Parse(evidence.Output);
            Assert.True(evidenceResult.RootElement.GetProperty("manifest").GetProperty("complete").GetBoolean());

            var report = await RunCliAsync("report", configuration, "--verification-run", defectiveRunId, "--json");
            Assert.Equal(0, report.ExitCode);
            using var reportResult = JsonDocument.Parse(report.Output);
            var reportRoot = reportResult.RootElement;
            Assert.Equal("external-target-verification", reportRoot.GetProperty("assuranceScope").GetString());
            Assert.Equal(2, reportRoot.GetProperty("sourceArtifactCount").GetInt64());
            Assert.Equal(1, reportRoot.GetProperty("accountedSourceCount").GetInt64());
            Assert.Equal(1, reportRoot.GetProperty("targetArtifactCount").GetInt64());
            Assert.Equal(1, reportRoot.GetProperty("lineagedTargetCount").GetInt64());
            Assert.Equal(2, reportRoot.GetProperty("failedFindingGroups").EnumerateObject()
                .Sum(item => item.Value.GetInt64()));
            Assert.False(reportRoot.GetProperty("recoveryAssessed").GetBoolean());
            Assert.False(reportRoot.GetProperty("qualificationAvailable").GetBoolean());
            Assert.Contains("does not prove that ProofShift executed the migration",
                reportRoot.GetProperty("claimBoundary").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("alpha", report.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("incorrect", report.Output, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(targetRoot, "work-1.txt"),
                "alpha", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(targetRoot, "work-2.txt"),
                "beta", TestContext.Current.CancellationToken);
            var correctedTargetHash = HashFile(Path.Combine(targetRoot, "work-1.txt"));
            var corrected = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "external-corrected-001", "--json");
            using var correctedResult = JsonDocument.Parse(corrected.Output);
            var correctedRunId = correctedResult.RootElement.GetProperty("verificationRunId").GetString()!;
            var correctedReport = await RunCliAsync("report", configuration, "--verification-run", correctedRunId, "--json");
            Assert.True(corrected.ExitCode == 0, corrected.Output + correctedReport.Output + corrected.Error);
            var correctedRoot = correctedResult.RootElement;
            Assert.Equal("passed", correctedRoot.GetProperty("verificationOutcome").GetString());
            Assert.Equal(0, correctedRoot.GetProperty("verificationFailureCount").GetInt32());
            Assert.Equal(2, correctedRoot.GetProperty("accountedSourceCount").GetInt64());
            Assert.Equal(2, correctedRoot.GetProperty("lineagedTargetCount").GetInt64());
            using var correctedReportResult = JsonDocument.Parse(correctedReport.Output);
            Assert.Equal("Passed", correctedReportResult.RootElement.GetProperty("verificationOutcome").GetString());
            Assert.Empty(correctedReportResult.RootElement.GetProperty("failedFindingGroups").EnumerateObject());

            var repeated = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "external-corrected-repeat", "--json");
            Assert.Equal(0, repeated.ExitCode);
            using var repeatedResult = JsonDocument.Parse(repeated.Output);
            var repeatedRoot = repeatedResult.RootElement;
            Assert.NotEqual(correctedRoot.GetProperty("verificationRunId").GetString(),
                repeatedRoot.GetProperty("verificationRunId").GetString());
            Assert.Equal(correctedRoot.GetProperty("targetFingerprint").GetString(),
                repeatedRoot.GetProperty("targetFingerprint").GetString());
            Assert.Equal(correctedRoot.GetProperty("evidenceFingerprint").GetString(),
                repeatedRoot.GetProperty("evidenceFingerprint").GetString());

            var humanOutput = await RunCliAsync("verify-external", configuration, "--checkpoint", checkpointId,
                "--observation", "external-human-output");
            Assert.Equal(0, humanOutput.ExitCode);
            Assert.Contains("Recovery: NOT ASSESSED", humanOutput.Output, StringComparison.Ordinal);
            Assert.Contains("Qualification: NOT AVAILABLE", humanOutput.Output, StringComparison.Ordinal);
            Assert.Contains("Vendor execution lineage: NOT OBSERVED", humanOutput.Output, StringComparison.Ordinal);
            Assert.Contains("Lineage basis: graph-derived expected lineage", humanOutput.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("QUALIFIED", humanOutput.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DryRunId", humanOutput.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("ProjectionRunId", humanOutput.Output, StringComparison.Ordinal);

            var comparison = await RunCliAsync("compare", configuration, "--before-verification", defectiveRunId,
                "--after-verification", correctedRoot.GetProperty("verificationRunId").GetString()!, "--json");
            Assert.Equal(0, comparison.ExitCode);
            using var comparisonResult = JsonDocument.Parse(comparison.Output);
            var comparisonRoot = comparisonResult.RootElement;
            Assert.Equal(2, comparisonRoot.GetProperty("findingsResolved").EnumerateArray()
                .Sum(item => item.GetProperty("count").GetInt64()));
            Assert.Empty(comparisonRoot.GetProperty("findingsIntroduced").EnumerateArray());
            Assert.True(comparisonRoot.GetProperty("targetFingerprintChanged").GetBoolean());
            Assert.True(comparisonRoot.GetProperty("evidenceFingerprintChanged").GetBoolean());
            Assert.True(comparisonRoot.GetProperty("verificationOutcomeChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("sourceFingerprintChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("checkpointSemanticsChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("configurationHashChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("graphHashChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("ruleSetFingerprintChanged").GetBoolean());
            Assert.False(comparisonRoot.GetProperty("providerVersionsChanged").GetBoolean());
            Assert.Equal("not applicable to verification-only comparison",
                comparisonRoot.GetProperty("recoveryComparison").GetString());
            Assert.Equal(correctedTargetHash, HashFile(Path.Combine(targetRoot, "work-1.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "projections")));
            Assert.False(Directory.Exists(Path.Combine(root, ".proofshift", "recovery")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(sourceVariable, previousSource);
            Environment.SetEnvironmentVariable(targetVariable, previousTarget);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteProjectAsync(string root, CancellationToken cancellationToken)
    {
        await WriteAsync(root, "proofshift.yaml", """
            proofshift: 1
            project:
              id: external-cli-test
              name: External CLI Test
            packs: []
            systems:
              source:
                file: systems/source.yaml
              target:
                file: systems/target.yaml
            migration:
              graph: migration/graph.yaml
            verification:
              rules: rules/rules.yaml
            recovery:
              policy: recovery/policy.yaml
            """, cancellationToken);
        await WriteAsync(root, "systems/source.yaml", """
            id: source
            name: Synthetic CSV Source
            role: source
            storage:
              records:
                connector: files
                root:
                  env: PROOFSHIFT_CLI_TEST_SOURCE_ROOT
            """, cancellationToken);
        await WriteAsync(root, "systems/target.yaml", """
            id: target
            name: External CSV Target
            role: target
            storage:
              records:
                connector: files
                root:
                  env: PROOFSHIFT_CLI_TEST_TARGET_ROOT
            """, cancellationToken);
        await WriteAsync(root, "migration/graph.yaml", """
            version: 1
            nodes:
              source-records:
                type: source
                system: source
                storage: records
                semanticType: Generic.Record
                selector:
                  kind: file-pattern
                  properties:
                    pattern: "**/*.txt"
                  identity: [relativePath]
              target-records:
                type: target
                system: target
                storage: records
                semanticType: Generic.Record
                selector:
                  kind: file-pattern
                  properties:
                    pattern: "**/*.txt"
                  identity: [relativePath]
            edges:
              records:
                from: [source-records]
                to: [target-records]
                operation:
                  type: copy
                recovery:
                  mode: restore
                  requiresSnapshot: true
            """, cancellationToken);
        await WriteAsync(root, "rules/rules.yaml", """
            version: 2
            rules:
              accounting:
                type: source-artifact-accounting
                version: "1"
                severity: critical
              lineage:
                type: target-lineage
                version: "1"
                severity: critical
              presence:
                type: target-presence
                version: "1"
                severity: error
              attributes:
                type: attribute-comparison
                version: "1"
                severity: error
            """, cancellationToken);
        await WriteAsync(root, "recovery/policy.yaml", """
            requireRecoveryForDestructiveOperations: true
            allowIrreversible:
              default: false
            requireValidatedRestore: true
            requireRecoveryRehearsal: true
            maximumIrreversibleArtifacts: 0
            """, cancellationToken);
    }

    private static async Task WriteAsync(string root, string relativePath, string content,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Packaged CLI process did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
