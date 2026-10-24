using System.Diagnostics;
using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Evidence;
using ProofShift.Packs.Abstractions;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Reporting;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

internal static class PersistedAssuranceReportTestHelper
{
    private static readonly string[] ForeignPackTerms =
        ["Pension.Member", "Utility.Account", "Justice.Case", "HCM.Worker", "Fhir.Patient"];

    public static async Task AssertCliReportAndComparisonAsync(string artifactDirectory,
        string verificationProjectDirectory, string projectName, string packId, string packVersion,
        string claimBoundary, DryRunId dryRunId, CancellationToken cancellationToken)
    {
        var recoveryStore = new FileSystemRecoveryArtifactStore(
            Path.Combine(artifactDirectory, ".proofshift", "recovery"));
        var recovery = await recoveryStore.ReadSummaryAsync(dryRunId, cancellationToken);
        var cliRootDirectory = Path.Combine(Path.GetTempPath(), $"proofshift-report-cli-{Guid.NewGuid():N}");
        var cliProjectDirectory = Path.Combine(cliRootDirectory, projectName);
        try
        {
            var cliStoreRoot = Path.Combine(cliProjectDirectory, ".proofshift");
            CopyDirectory(Path.Combine(artifactDirectory, ".proofshift", "recovery",
                    dryRunId.Value.ToString("N")), Path.Combine(cliStoreRoot, "recovery", dryRunId.Value.ToString("N")));
            CopyDirectory(Path.Combine(artifactDirectory, ".proofshift", "verifications",
                    recovery.VerificationRunId.Value.ToString("N")),
                Path.Combine(cliStoreRoot, "verifications", recovery.VerificationRunId.Value.ToString("N")));
            CopyDirectory(Path.Combine(artifactDirectory, "checkpoints",
                    recovery.CheckpointId.Value.ToString("N")),
                Path.Combine(cliStoreRoot, "checkpoints", recovery.CheckpointId.Value.ToString("N")));
            CopyDirectory(Path.Combine(verificationProjectDirectory, ".proofshift", "projections",
                    recovery.ProjectionRunId.Value.ToString("N")),
                Path.Combine(cliStoreRoot, "projections", recovery.ProjectionRunId.Value.ToString("N")));
            CopyDirectory(Path.Combine(verificationProjectDirectory, ".proofshift", "verification-ledgers"),
                Path.Combine(cliStoreRoot, "verification-ledgers"));

            var cliAssembly = Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll");
            Assert.True(File.Exists(cliAssembly), $"CLI assembly not found: {cliAssembly}");

            var dryRunText = dryRunId.Value.ToString("D");
            var human = await RunCliAsync(cliProjectDirectory, cliAssembly, cancellationToken,
                "report", dryRunText);
            Assert.True(human.ExitCode == 0,
                $"Human report failed with exit code {human.ExitCode}. stdout: {human.StandardOutput} stderr: {human.StandardError}");
            Assert.Contains($"Project: {projectName}", human.StandardOutput, StringComparison.Ordinal);
            Assert.Contains($"({packId}@{packVersion})", human.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(claimBoundary, human.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("RESULT: QUALIFIED DRY RUN", human.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Source Accounting: COMPLETE", human.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Target Lineage: COMPLETE", human.StandardOutput, StringComparison.Ordinal);

            var machine = await RunCliAsync(cliProjectDirectory, cliAssembly, cancellationToken,
                "report", dryRunText, "--json");
            var repeatedMachine = await RunCliAsync(cliProjectDirectory, cliAssembly, cancellationToken,
                "report", dryRunText, "--json");
            Assert.True(machine.ExitCode == 0,
                $"JSON report failed with exit code {machine.ExitCode}. stdout: {machine.StandardOutput} stderr: {machine.StandardError}");
            Assert.True(repeatedMachine.ExitCode == 0,
                $"Repeated JSON report failed with exit code {repeatedMachine.ExitCode}. stdout: {repeatedMachine.StandardOutput} stderr: {repeatedMachine.StandardError}");
            Assert.Equal(machine.StandardOutput, repeatedMachine.StandardOutput);
            using var report = JsonDocument.Parse(machine.StandardOutput);
            var reportRoot = report.RootElement;
            Assert.Equal("proofshift-persisted-assurance-report-v1",
                reportRoot.GetProperty("format").GetString());
            Assert.Equal(packId, reportRoot.GetProperty("packId").GetString());
            Assert.Equal(packVersion, reportRoot.GetProperty("packVersion").GetString());
            Assert.Equal(claimBoundary, reportRoot.GetProperty("claimBoundary").GetString());
            Assert.Equal("QUALIFIED", reportRoot.GetProperty("qualification").GetString());
            Assert.Equal("Passed", reportRoot.GetProperty("verificationOutcome").GetString());
            Assert.Equal("COMPLETE", reportRoot.GetProperty("sourceAccountingStatus").GetString());
            Assert.Equal("COMPLETE", reportRoot.GetProperty("targetLineageStatus").GetString());
            Assert.Equal("Passed", reportRoot.GetProperty("recoveryAssessmentOutcome").GetString());
            AssertNoExpectedOrActualProperties(machine.StandardOutput);
            foreach (var foreignTerm in ForeignPackTerms
                         .Where(term => !claimBoundary.Contains(term, StringComparison.Ordinal)))
                Assert.DoesNotContain(foreignTerm, machine.StandardOutput, StringComparison.Ordinal);

            var comparison = await RunCliAsync(cliProjectDirectory, cliAssembly, cancellationToken,
                "compare", dryRunText, dryRunText, "--json");
            Assert.True(comparison.ExitCode == 0,
                $"JSON comparison failed with exit code {comparison.ExitCode}. stdout: {comparison.StandardOutput} stderr: {comparison.StandardError}");
            using var comparisonJson = JsonDocument.Parse(comparison.StandardOutput);
            Assert.Equal("proofshift-persisted-assurance-comparison-v1",
                comparisonJson.RootElement.GetProperty("format").GetString());
            foreach (var property in new[]
                     {
                         "configurationHashChanged", "graphHashChanged", "sourceFingerprintChanged",
                         "checkpointSemanticsChanged", "projectionSemanticsChanged", "observationSemanticsChanged",
                         "ruleSetFingerprintChanged", "verificationEvidenceFingerprintChanged",
                         "dryRunFingerprintChanged", "recoveryPolicyFingerprintChanged",
                         "recoveryEvidenceFingerprintChanged", "recoveryReadinessChanged",
                         "sourceAccountingGapsChanged", "targetLineageGapsChanged", "qualificationChanged"
                     })
                Assert.False(comparisonJson.RootElement.GetProperty(property).GetBoolean(), property);
        }
        finally
        {
            if (Directory.Exists(cliRootDirectory))
                Directory.Delete(cliRootDirectory, recursive: true);
        }
    }

    public static async Task StageCheckpointForCliAsync(string artifactDirectory, DryRunId dryRunId,
        CancellationToken cancellationToken)
    {
        var recoveryStore = new FileSystemRecoveryArtifactStore(
            Path.Combine(artifactDirectory, ".proofshift", "recovery"));
        var recovery = await recoveryStore.ReadSummaryAsync(dryRunId, cancellationToken);
        var checkpointId = recovery.CheckpointId.Value.ToString("N");
        CopyDirectory(Path.Combine(artifactDirectory, "checkpoints", checkpointId),
            Path.Combine(artifactDirectory, ".proofshift", "checkpoints", checkpointId));
    }

    public static async Task<PersistedAssuranceReport> BuildAsync(string artifactDirectory,
        string verificationProjectDirectory, string projectName, IDomainPack pack, DryRunId dryRunId,
        CancellationToken cancellationToken)
    {
        var recoveryStore = new FileSystemRecoveryArtifactStore(
            Path.Combine(artifactDirectory, ".proofshift", "recovery"));
        var recovery = await recoveryStore.ReadSummaryAsync(dryRunId, cancellationToken);
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(artifactDirectory, "checkpoints"));
        var checkpoint = await checkpointStore.ReadManifestAsync(
            recovery.CheckpointId.Value.ToString("D"), cancellationToken);
        var projection = await ProjectionRunManifestStore.ReadAsync(verificationProjectDirectory,
            recovery.ProjectionRunId, cancellationToken);
        var evidenceStore = new FileSystemEvidenceStore(
            Path.Combine(artifactDirectory, ".proofshift", "verifications"));
        if (!await evidenceStore.VerifyIntegrityAsync(recovery.VerificationRunId, cancellationToken))
            throw new InvalidDataException("Test Evidence integrity verification failed.");
        var manifest = await evidenceStore.ReadManifestAsync(recovery.VerificationRunId, cancellationToken);
        var ledgerStore = await SqliteVerificationLedgerStore.OpenAsync(verificationProjectDirectory,
            recovery.VerificationLedger, cancellationToken);
        await using (ledgerStore.ConfigureAwait(false))
        {
            var ledger = await ledgerStore.ReadSummaryAsync(cancellationToken);
            return await PersistedAssuranceReportBuilder.BuildAsync(projection.ProjectId, projectName, dryRunId,
                pack, manifest, evidenceStore.ReadRecordsAsync(recovery.VerificationRunId, cancellationToken),
                recovery, checkpoint, ledger, cancellationToken);
        }
    }

    public static void AssertNoExpectedOrActualProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertNoExpectedOrActualProperties(document.RootElement);
    }

    private static void AssertNoExpectedOrActualProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.False(property.Name.Equals("expected", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("actual", StringComparison.OrdinalIgnoreCase),
                    $"Report contains a raw '{property.Name}' field.");
                AssertNoExpectedOrActualProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoExpectedOrActualProperties(item);
        }
    }

    private static async Task<CliResult> RunCliAsync(string workingDirectory, string cliAssembly,
            CancellationToken cancellationToken, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(cliAssembly);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("ProofShift CLI process could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), output, error);
            return new CliResult(process.ExitCode, await output, await error);
        }

    private static void CopyDirectory(string source, string destination)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException($"Persisted CLI test artifact directory is missing: {source}");
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
