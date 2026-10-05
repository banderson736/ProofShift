using System.Data;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Pension;
using ProofShift.Projection;
using ProofShift.Recovery;
using ProofShift.Reporting;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PensionExternalCorpusVerificationTests
{
    private static readonly JsonSerializerOptions DemoJsonOptions = new() { WriteIndented = true };
    private const string TargetConnectionSecret = "PS09_CORPUS_TARGET_CONNECTION";
    private const string SourceConnectionSecret = "PS09_CORPUS_SOURCE_CONNECTION";
    private const string CsvRootSecret = "PS09_CORPUS_CSV_ROOT";
    private const string SourceFilesRootSecret = "PS09_CORPUS_SOURCE_FILES_ROOT";
    private const string TargetFilesRootSecret = "PS09_CORPUS_TARGET_FILES_ROOT";
    private const string IntegratedRunDirectoryVariable = "PS09_INTEGRATED_RUN_DIRECTORY";
    [Fact]
    public async Task FastDefectiveAndCorrectedDatasetsRunThroughVerificationServiceWithExactBusinessCounts()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var performanceRecorder = new PerformanceRecorder("Pension Fast Integrated");
        var generationStopwatch = Stopwatch.StartNew();
        PensionSyntheticRecord[] sourceData;
        using (var generationStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture, "synthetic source generation"))
        {
            sourceData = PensionSyntheticDatasetGenerator.Generate().ToArray();
            generationStage.AddArtifacts(sourceData.LongLength);
        }
        generationStopwatch.Stop();
        Assert.Equal(8_220, sourceData.Length);
        var environmentStartupStopwatch = Stopwatch.StartNew();
        var environmentStage = performanceRecorder.StartStage(PerformanceStageKind.Environment, "Docker database startup");
        var sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Synthetic-PS09-Only-Password!2026").Build();
        await sqlContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var sqlCleanup = sqlContainer;
        var postgresContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgresContainer.StartAsync(TestContext.Current.CancellationToken);
        environmentStartupStopwatch.Stop();
        environmentStage.Dispose();
        await using var postgresCleanup = postgresContainer;
        var configuredRunDirectory = Environment.GetEnvironmentVariable(IntegratedRunDirectoryVariable);
        var deleteRunDirectory = string.IsNullOrWhiteSpace(configuredRunDirectory);
        var directory = deleteRunDirectory
            ? Path.Combine(Path.GetTempPath(), $"proofshift-pension-corpus-{Guid.NewGuid():N}")
            : Path.GetFullPath(configuredRunDirectory!);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Integrated pension run directory is not empty; refusing to overwrite persisted artifacts.");
        Directory.CreateDirectory(directory);
        var csvRoot = Path.Combine(directory, "source-csv");
        var sourceFilesRoot = Path.Combine(directory, "source-files");
        var targetFilesRoot = Path.Combine(directory, "target-files");
        Directory.CreateDirectory(targetFilesRoot);
        var sourceLoadStopwatch = Stopwatch.StartNew();
        var sourceLoadStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture,
            "CSV/filesystem materialization and SQL Server bulk load");
        await MaterializeCsvAndFileSourcesAsync(sourceData, csvRoot, sourceFilesRoot);
        var (sourceSystem, targetSystem, configuration) = CreateConfiguration();
        var defectiveScenario = CreateScenario(sourceData, sourceSystem.Id, targetSystem.Id, falseReverse: true);
        var correctedScenario = CreateScenario(sourceData, sourceSystem.Id, targetSystem.Id, falseReverse: false);
        var sourceConnectors = new ConnectorRegistry(
        [
            new ProofShift.Connectors.SqlServer.SqlServerSourceConnector(),
            new CsvSourceConnector(),
            new FilesystemSourceConnector()
        ]);
        var sourceConnection = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        }.ConnectionString;
        var contextFactory = new RuntimeConnectorContextFactory(new CorpusEnvironmentProvider(new Dictionary<string, string>
        {
            [SourceConnectionSecret] = sourceConnection,
            [TargetConnectionSecret] = postgresContainer.GetConnectionString(),
            [CsvRootSecret] = csvRoot,
            [SourceFilesRootSecret] = sourceFilesRoot,
            [TargetFilesRootSecret] = targetFilesRoot
        }));
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(directory, "checkpoints"));
        var targetConnector = new ProofShift.Connectors.Postgres.PostgresShadowTargetConnector();
        try
        {
            var schemaDirectory = Path.Combine(AppContext.BaseDirectory, "scenarios", "pension-modernization", "ps09", "database");
            var sourceSchema = await File.ReadAllTextAsync(Path.Combine(schemaDirectory, "sqlserver-source.sql"),
                TestContext.Current.CancellationToken);
            await using (var connection = new SqlConnection(sourceConnection))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var create = connection.CreateCommand();
                create.CommandText = sourceSchema;
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                foreach (var kind in Enum.GetValues<PensionRecordKind>())
                {
                    if (kind is PensionRecordKind.Document or PensionRecordKind.HistoricalExport) continue;
                    await BulkLoadSourceKindAsync(connection, kind, sourceData.Where(record => record.Kind == kind).ToArray());
                }
            }
            sourceLoadStopwatch.Stop();
            var payloadArtifactCount = sourceData.LongCount(record => record.Kind is PensionRecordKind.Document or PensionRecordKind.HistoricalExport);
            sourceLoadStage.AddArtifacts(checked(sourceData.LongLength + payloadArtifactCount));
            sourceLoadStage.AddBytes(checked(DirectorySize(csvRoot) + DirectorySize(sourceFilesRoot)));
            sourceLoadStage.Dispose();
            var checkpointStopwatch = Stopwatch.StartNew();
            var defectiveCapture = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contextFactory)
                .CaptureAsync(configuration, defectiveScenario.Graph, performanceRecorder, TestContext.Current.CancellationToken);
            var correctedCapture = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contextFactory)
                .CaptureAsync(configuration, correctedScenario.Graph, performanceRecorder, TestContext.Current.CancellationToken);
            checkpointStopwatch.Stop();
            Assert.Equal(CheckpointStatus.Complete, defectiveCapture.Status);
            Assert.Equal(CheckpointStatus.Complete, correctedCapture.Status);
            Assert.Equal(8_495, defectiveCapture.Checkpoint!.ArtifactCount);
            Assert.Equal(8_495, correctedCapture.Checkpoint!.ArtifactCount);

            PensionSyntheticRecord[] cleanTarget;
            PensionSyntheticRecord[] defectiveTarget;
            using (var targetFixtureStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture,
                "corrected and defective target fixture generation"))
            {
                cleanTarget = PensionTargetDataModel.Transform(sourceData).ToArray();
                defectiveTarget = PensionDefectInjector.InjectTargetDefects(cleanTarget).ToArray();
                targetFixtureStage.AddArtifacts(checked(cleanTarget.LongLength + defectiveTarget.LongLength));
            }
            var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider(), new PensionPack()]);
            var rules = registry.Resolve(CreateRules());
            var service = new VerificationService(checkpointStore);

            var externalVerificationStopwatch = Stopwatch.StartNew();
            var corrected = await VerifyExternalAsync(service, configuration, correctedScenario, correctedCapture,
                cleanTarget, rules, contextFactory, targetConnector, new FilesystemShadowTargetConnector(),
                postgresContainer, directory, "corrected", performanceRecorder);
            Assert.True(corrected.Run.Outcome == VerificationOutcome.Passed,
                string.Join(Environment.NewLine, corrected.Findings.Where(finding => finding.Result == EvidenceResult.Fail)
                    .Select(finding => $"{finding.Code}: {finding.Explanation}")));
            Assert.Equal(8_495, corrected.Dispositions.Count);
            Assert.DoesNotContain(corrected.Dispositions, disposition =>
                disposition.Disposition is ArtifactDisposition.Unaccounted or ArtifactDisposition.Failed);
            Assert.Equal(8_595, corrected.ExpectedLineage.Count);
            Assert.All(corrected.ExpectedLineage, item => Assert.Equal(LineageBasis.GraphDerivedExpected, item.Basis));
            var correctedCounts = BusinessCounts(corrected.Findings, falseReverseEdges: 0);
            Assert.Equal(18, correctedCounts.Count);
            Assert.Equal(0L, correctedCounts.Values.Sum());

            var defective = await VerifyExternalAsync(service, configuration, defectiveScenario, defectiveCapture,
                defectiveTarget, rules, contextFactory, targetConnector, new FilesystemShadowTargetConnector(),
                postgresContainer, directory, "defective", performanceRecorder);
            Assert.Equal(VerificationOutcome.Failed, defective.Run.Outcome);
            var falseReverseEdges = defectiveScenario.Graph.Edges.Where(edge => edge.Recovery?.Mode == RecoveryMode.Reverse).ToArray();
            Assert.Equal(2, falseReverseEdges.Length);
            Assert.All(falseReverseEdges, edge => Assert.False(TransformationLossAnalyzer.Analyze(edge).IsReversible));
            var defects = BusinessCounts(defective.Findings, falseReverseEdges.Length);
            Assert.Equal(ExpectedDefects(), defects);
            Assert.Equal(149L, defects.Values.Sum());

            var externalEvidenceStore = new FileSystemEvidenceStore(Path.Combine(directory, "external-evidence"));
            await externalEvidenceStore.SaveAsync(corrected.EvidenceGraph, TestContext.Current.CancellationToken);
            await externalEvidenceStore.SaveAsync(defective.EvidenceGraph, TestContext.Current.CancellationToken);
            Assert.True(await externalEvidenceStore.VerifyIntegrityAsync(corrected.Run.Id, TestContext.Current.CancellationToken));
            Assert.True(await externalEvidenceStore.VerifyIntegrityAsync(defective.Run.Id, TestContext.Current.CancellationToken));

            var repeated = await VerifyExternalAsync(service, configuration, correctedScenario, correctedCapture,
                cleanTarget, rules, contextFactory, targetConnector, new FilesystemShadowTargetConnector(),
                postgresContainer, directory, "corrected-repeat", performanceRecorder);
            Assert.Equal(corrected.Run.TargetFingerprint, repeated.Run.TargetFingerprint);
            Assert.Equal(corrected.Run.RuleSetFingerprint, repeated.Run.RuleSetFingerprint);
            Assert.Equal(corrected.Run.EvidenceFingerprint, repeated.Run.EvidenceFingerprint);
            externalVerificationStopwatch.Stop();

            var integratedAssuranceStopwatch = Stopwatch.StartNew();
            using (var targetTemplateStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture,
                "PostgreSQL shadow target template setup"))
            {
                await CreatePostgresTargetTemplatesAsync(postgresContainer, cleanTarget, correctedScenario);
                targetTemplateStage.AddArtifacts(correctedScenario.TargetNodes.Count);
            }
            var targetConnectors = new ShadowTargetConnectorRegistry([targetConnector, new FilesystemShadowTargetConnector()]);
            var correctedPipeline = await ProjectVerifyAndQualifyAsync(checkpointStore, sourceConnectors, targetConnectors,
                contextFactory, configuration, correctedScenario, correctedCapture, rules, directory,
                "projected-corrected", performanceRecorder);
            Assert.Equal(ProjectionStatus.Succeeded, correctedPipeline.Projection.Status);
            Assert.Equal(8_595, correctedPipeline.Projection.SourceArtifactCount);
            Assert.Equal(8_595, correctedPipeline.Projection.TargetArtifactCount);
            Assert.Equal(VerificationOutcome.Passed, correctedPipeline.Verification.Run.Outcome);
            Assert.Equal(8_495, correctedPipeline.Verification.Dispositions.Count);
            Assert.Equal(8_595, correctedPipeline.Verification.Lineage.Count);
            Assert.Equal(DryRunQualificationStatus.Qualified, correctedPipeline.Recovery.Qualification.Status);

            var falseReversePipeline = await ProjectVerifyAndQualifyAsync(checkpointStore, sourceConnectors, targetConnectors,
                contextFactory, configuration, defectiveScenario, defectiveCapture, rules, directory,
                "projected-false-reverse", performanceRecorder);
            Assert.Equal(ProjectionStatus.Succeeded, falseReversePipeline.Projection.Status);
            Assert.Equal(VerificationOutcome.Passed, falseReversePipeline.Verification.Run.Outcome);
            Assert.Equal(DryRunQualificationStatus.NotQualified, falseReversePipeline.Recovery.Qualification.Status);
            Assert.Equal(2, falseReversePipeline.Recovery.Assessment.Edges.Count(edge =>
                edge.Issues.Any(issue => issue.Code == RecoveryIssueCodes.InvalidReverse)));
            foreach (var declaration in PensionDefectInjector.FalseReverseDeclarations)
                Assert.Contains(falseReversePipeline.Recovery.Assessment.Edges, edge => edge.EdgeName == declaration.EdgeName &&
                    edge.Issues.Any(issue => issue.Code == RecoveryIssueCodes.InvalidReverse));

            var projectedDefectivePipeline = await ProjectVerifyAndQualifyAsync(checkpointStore, sourceConnectors,
                targetConnectors, contextFactory, configuration, defectiveScenario, defectiveCapture, rules,
                directory, "projected-defective", performanceRecorder, async projection =>
                {
                    await ReplaceProjectedPostgresTargetsAsync(postgresContainer, projection.Id, defectiveTarget, defectiveScenario);
                    await RemoveMissingProjectedFilesAsync(targetFilesRoot, projection.Id, cleanTarget, defectiveTarget,
                        defectiveScenario);
                });
            Assert.Equal(ProjectionStatus.Succeeded, projectedDefectivePipeline.Projection.Status);
            Assert.Equal(VerificationOutcome.Failed, projectedDefectivePipeline.Verification.Run.Outcome);
            Assert.Equal(8_495, projectedDefectivePipeline.Verification.Dispositions.Count);
            Assert.Equal(8_595, projectedDefectivePipeline.Verification.Lineage.Count);
            var projectedDefects = BusinessCounts(projectedDefectivePipeline.Verification.Findings,
                projectedDefectivePipeline.Recovery.Assessment.Edges.Count(edge =>
                    edge.Issues.Any(issue => issue.Code == RecoveryIssueCodes.InvalidReverse)));
            Assert.Equal(ExpectedDefects(), projectedDefects);
            Assert.Equal(149L, projectedDefects.Values.Sum());
            Assert.Equal(DryRunQualificationStatus.NotQualified, projectedDefectivePipeline.Recovery.Qualification.Status);

            integratedAssuranceStopwatch.Stop();
            var persistenceAndReportStopwatch = Stopwatch.StartNew();
            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(directory, ".proofshift", "verifications"));
            var evidencePersistenceStage = performanceRecorder.StartStage(PerformanceStageKind.Reporting,
                "Verification evidence persistence");
            await evidenceStore.SaveAsync(correctedPipeline.Verification.EvidenceGraph, TestContext.Current.CancellationToken);
            await evidenceStore.SaveAsync(falseReversePipeline.Verification.EvidenceGraph, TestContext.Current.CancellationToken);
            await evidenceStore.SaveAsync(projectedDefectivePipeline.Verification.EvidenceGraph, TestContext.Current.CancellationToken);
            Assert.True(await evidenceStore.VerifyIntegrityAsync(correctedPipeline.Verification.Run.Id, TestContext.Current.CancellationToken));
            Assert.True(await evidenceStore.VerifyIntegrityAsync(falseReversePipeline.Verification.Run.Id, TestContext.Current.CancellationToken));
            Assert.True(await evidenceStore.VerifyIntegrityAsync(projectedDefectivePipeline.Verification.Run.Id, TestContext.Current.CancellationToken));
            evidencePersistenceStage.AddArtifacts(correctedPipeline.Verification.EvidenceGraph.Records.Count +
                falseReversePipeline.Verification.EvidenceGraph.Records.Count +
                projectedDefectivePipeline.Verification.EvidenceGraph.Records.Count);
            evidencePersistenceStage.Dispose();

            var projectedRecoveryStore = new FileSystemRecoveryArtifactStore(Path.Combine(directory, ".proofshift", "recovery"));
            var recoveryPersistenceStage = performanceRecorder.StartStage(PerformanceStageKind.Reporting,
                "Recovery artifact persistence");
            var correctedReceipt = await projectedRecoveryStore.SaveAsync(correctedPipeline.Recovery, TestContext.Current.CancellationToken);
            var falseReverseReceipt = await projectedRecoveryStore.SaveAsync(falseReversePipeline.Recovery, TestContext.Current.CancellationToken);
            var defectiveReceipt = await projectedRecoveryStore.SaveAsync(projectedDefectivePipeline.Recovery, TestContext.Current.CancellationToken);
            Assert.True(await projectedRecoveryStore.VerifyIntegrityAsync(correctedPipeline.Recovery.Id, TestContext.Current.CancellationToken));
            Assert.True(await projectedRecoveryStore.VerifyIntegrityAsync(falseReversePipeline.Recovery.Id, TestContext.Current.CancellationToken));
            Assert.Equal(DryRunQualificationStatus.Qualified,
                (await projectedRecoveryStore.ReadSummaryAsync(correctedPipeline.Recovery.Id, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(DryRunQualificationStatus.NotQualified,
                (await projectedRecoveryStore.ReadSummaryAsync(falseReversePipeline.Recovery.Id, TestContext.Current.CancellationToken)).Status);
            Assert.True(await projectedRecoveryStore.VerifyIntegrityAsync(projectedDefectivePipeline.Recovery.Id, TestContext.Current.CancellationToken));
            Assert.Equal(DryRunQualificationStatus.NotQualified,
                (await projectedRecoveryStore.ReadSummaryAsync(projectedDefectivePipeline.Recovery.Id, TestContext.Current.CancellationToken)).Status);
            Assert.NotEmpty(correctedReceipt.IntegrityHash);
            Assert.NotEmpty(falseReverseReceipt.IntegrityHash);
            Assert.NotEmpty(defectiveReceipt.IntegrityHash);
            recoveryPersistenceStage.AddArtifacts(correctedPipeline.Recovery.EvidenceGraph.Graph.Records.Count +
                falseReversePipeline.Recovery.EvidenceGraph.Graph.Records.Count +
                projectedDefectivePipeline.Recovery.EvidenceGraph.Graph.Records.Count);
            recoveryPersistenceStage.Dispose();

            var reportConstructionStage = performanceRecorder.StartStage(PerformanceStageKind.Reporting,
                "persisted report and comparison construction");
            var correctedReport = await BuildPersistedReportAsync(directory, evidenceStore, projectedRecoveryStore,
                correctedPipeline.Verification, correctedPipeline.Recovery, "Synthetic PS-0.9 Fast Corrected");
            var defectiveReport = await BuildPersistedReportAsync(directory, evidenceStore, projectedRecoveryStore,
                projectedDefectivePipeline.Verification, projectedDefectivePipeline.Recovery, "Synthetic PS-0.9 Fast Defective");
            Assert.Equal("QUALIFIED", correctedReport.Qualification);
            Assert.Equal(0L, correctedReport.BusinessDiscrepancyCount);
            Assert.Equal(0L, correctedReport.UnaccountedSources);
            Assert.Equal(0L, correctedReport.UnexplainedTargets);
            Assert.Equal(0, correctedReport.VerificationFailureCount);
            Assert.Equal(0L, correctedReport.FailedRecoveryEdges);
            Assert.Equal("NOT QUALIFIED", defectiveReport.Qualification);
            Assert.Equal("FAILED", defectiveReport.VerificationOutcome);
            Assert.Equal(149L, defectiveReport.BusinessDiscrepancyCount);
            Assert.Equal(ExpectedDefects(), defectiveReport.DefectCounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            var reportDirectory = Path.Combine(directory, ".proofshift", "reports");
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{correctedReport.DryRunId}.json"),
                JsonSerializer.Serialize(correctedReport), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{defectiveReport.DryRunId}.json"),
                JsonSerializer.Serialize(defectiveReport), TestContext.Current.CancellationToken);
            var comparison = PensionAssuranceReportBuilder.Compare(defectiveReport, correctedReport);
            Assert.Equal("NOT QUALIFIED", comparison.BeforeQualification);
            Assert.Equal("QUALIFIED", comparison.AfterQualification);
            Assert.Equal(149L, comparison.DefectsResolved.Values.Sum());
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "defective-to-corrected-compare.json"),
                JsonSerializer.Serialize(comparison), TestContext.Current.CancellationToken);
            reportConstructionStage.AddArtifacts(3);
            reportConstructionStage.Dispose();

            var cliReportingStage = performanceRecorder.StartStage(PerformanceStageKind.Reporting,
                "CLI human and JSON reports/comparison");
            var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
            var cliAssembly = Path.Combine(repositoryRoot, "src", "ProofShift.Cli", "bin", "Debug", "net10.0", "ProofShift.Cli.dll");
            Assert.True(File.Exists(cliAssembly), $"CLI assembly not found: {cliAssembly}");
            var defectiveHuman = await RunCliAsync(directory, cliAssembly, "report", defectiveReport.DryRunId);
            Assert.True(defectiveHuman.ExitCode == 0, defectiveHuman.StandardError);
            Assert.Contains("Business Discrepancies: 149", defectiveHuman.StandardOutput);
            Assert.Contains("RESULT: NOT QUALIFIED", defectiveHuman.StandardOutput);
            var correctedHuman = await RunCliAsync(directory, cliAssembly, "report", correctedReport.DryRunId);
            Assert.True(correctedHuman.ExitCode == 0, correctedHuman.StandardError);
            Assert.Contains("Business Discrepancies: 0", correctedHuman.StandardOutput);
            Assert.Contains("RESULT: QUALIFIED DRY RUN", correctedHuman.StandardOutput);

            var defectiveMachine = await RunCliAsync(directory, cliAssembly, "report", defectiveReport.DryRunId, "--json");
            var correctedMachine = await RunCliAsync(directory, cliAssembly, "report", correctedReport.DryRunId, "--json");
            Assert.True(defectiveMachine.ExitCode == 0, defectiveMachine.StandardError);
            Assert.True(correctedMachine.ExitCode == 0, correctedMachine.StandardError);
            using var defectiveMachineJson = JsonDocument.Parse(defectiveMachine.StandardOutput);
            using var correctedMachineJson = JsonDocument.Parse(correctedMachine.StandardOutput);
            Assert.Equal("NOT QUALIFIED", defectiveMachineJson.RootElement.GetProperty("qualification").GetString());
            Assert.Equal(149L, defectiveMachineJson.RootElement.GetProperty("businessDiscrepancyCount").GetInt64());
            Assert.Equal(18, defectiveMachineJson.RootElement.GetProperty("defectCounts").EnumerateObject().Count());
            Assert.Equal("QUALIFIED", correctedMachineJson.RootElement.GetProperty("qualification").GetString());
            Assert.Equal(0L, correctedMachineJson.RootElement.GetProperty("businessDiscrepancyCount").GetInt64());
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{defectiveReport.DryRunId}.cli.json"),
                defectiveMachine.StandardOutput, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{correctedReport.DryRunId}.cli.json"),
                correctedMachine.StandardOutput, TestContext.Current.CancellationToken);

            var compareHuman = await RunCliAsync(directory, cliAssembly, "compare",
                defectiveReport.DryRunId, correctedReport.DryRunId);
            Assert.True(compareHuman.ExitCode == 0, compareHuman.StandardError);
            Assert.Contains("Before: NOT QUALIFIED", compareHuman.StandardOutput);
            Assert.Contains("After: QUALIFIED", compareHuman.StandardOutput);
            var compareMachine = await RunCliAsync(directory, cliAssembly, "compare",
                defectiveReport.DryRunId, correctedReport.DryRunId, "--json");
            Assert.True(compareMachine.ExitCode == 0, compareMachine.StandardError);
            using var comparisonJson = JsonDocument.Parse(compareMachine.StandardOutput);
            Assert.Equal(149L, comparisonJson.RootElement.GetProperty("defectsResolved").EnumerateObject()
                .Sum(property => property.Value.GetInt64()));
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "defective-to-corrected-compare.cli.json"),
                compareMachine.StandardOutput, TestContext.Current.CancellationToken);
            cliReportingStage.AddArtifacts(6);
            cliReportingStage.Dispose();
            persistenceAndReportStopwatch.Stop();
            var performanceRun = performanceRecorder.Complete();
            var performanceJson = PerformanceReportBuilder.ToJson(performanceRun);
            var performanceText = PerformanceReportBuilder.ToHumanReadable(performanceRun);
            await File.WriteAllTextAsync(Path.Combine(directory, "performance-run.json"), performanceJson,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "performance-run.txt"), performanceText,
                TestContext.Current.CancellationToken);
            Console.WriteLine(performanceText);

            var physicalCounts = sourceData.GroupBy(record => record.Kind)
                .ToDictionary(group => group.Key.ToString(), group => group.LongCount(), StringComparer.Ordinal);
            totalStopwatch.Stop();
            using var process = Process.GetCurrentProcess();
            var demoSummary = new
            {
                benchmarkScale = "fast",
                environmentStartupMilliseconds = environmentStartupStopwatch.Elapsed.TotalMilliseconds,
                generationMilliseconds = generationStopwatch.Elapsed.TotalMilliseconds,
                sourceMaterializationAndLoadMilliseconds = sourceLoadStopwatch.Elapsed.TotalMilliseconds,
                checkpointMilliseconds = checkpointStopwatch.Elapsed.TotalMilliseconds,
                externalTargetVerificationMilliseconds = externalVerificationStopwatch.Elapsed.TotalMilliseconds,
                projectionVerificationRecoveryMilliseconds = integratedAssuranceStopwatch.Elapsed.TotalMilliseconds,
                persistedReportingAndComparisonMilliseconds = persistenceAndReportStopwatch.Elapsed.TotalMilliseconds,
                totalMilliseconds = totalStopwatch.Elapsed.TotalMilliseconds,
                proofShiftRuntimeMilliseconds = Math.Max(0, performanceRun.ElapsedMicroseconds -
                    performanceRun.Stages.Where(stage => stage.Kind == PerformanceStageKind.Fixture)
                        .Sum(stage => stage.ElapsedMicroseconds)) / 1_000d,
                peakProcessWorkingSetBytes = process.PeakWorkingSet64,
                performanceRun,
                temporaryWorkspaceBytesAfterRun = DirectorySize(Path.Combine(directory, ".proofshift", "temporary")),
                persistedEvidenceRecords = correctedPipeline.Verification.EvidenceGraph.Records.Count +
                    falseReversePipeline.Verification.EvidenceGraph.Records.Count +
                    projectedDefectivePipeline.Verification.EvidenceGraph.Records.Count,
                sourceRecords = physicalCounts,
                csvRecords = physicalCounts[PensionRecordKind.Document.ToString()] + physicalCounts[PensionRecordKind.HistoricalExport.ToString()],
                binaryDocuments = physicalCounts[PensionRecordKind.Document.ToString()],
                historicalExports = physicalCounts[PensionRecordKind.HistoricalExport.ToString()],
                checkpointArtifacts = correctedCapture.Checkpoint.ArtifactCount,
                correctedProjectionTargets = correctedPipeline.Projection.TargetArtifactCount,
                defectiveBusinessDiscrepancies = defectiveReport.BusinessDiscrepancyCount,
                correctedBusinessDiscrepancies = correctedReport.BusinessDiscrepancyCount,
                unaccountedSources = correctedReport.UnaccountedSources,
                unexplainedTargets = correctedReport.UnexplainedTargets,
                defectiveQualification = defectiveReport.Qualification,
                correctedQualification = correctedReport.Qualification,
                defectiveDryRunId = defectiveReport.DryRunId,
                correctedDryRunId = correctedReport.DryRunId,
                sourceFingerprint = correctedReport.SourceFingerprint,
                correctedGraphHash = correctedReport.GraphHash,
                defectiveGraphHash = defectiveReport.GraphHash,
                correctedProjectionFingerprint = correctedReport.ProjectionFingerprint,
                defectiveProjectionFingerprint = defectiveReport.ProjectionFingerprint,
                correctedRuleSetFingerprint = correctedReport.RuleSetFingerprint,
                defectiveRuleSetFingerprint = defectiveReport.RuleSetFingerprint,
                correctedEvidenceFingerprint = correctedReport.VerificationEvidenceFingerprint,
                defectiveEvidenceFingerprint = defectiveReport.VerificationEvidenceFingerprint,
                correctedRecoveryAssessmentFingerprint = correctedReport.RecoveryAssessmentFingerprint,
                defectiveRecoveryAssessmentFingerprint = defectiveReport.RecoveryAssessmentFingerprint,
                correctedDryRunFingerprint = correctedReport.DryRunFingerprint,
                defectiveDryRunFingerprint = defectiveReport.DryRunFingerprint
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "demo-summary.json"),
                JsonSerializer.Serialize(demoSummary, DemoJsonOptions),
                TestContext.Current.CancellationToken);
            Console.WriteLine($"INTEGRATED_RUN_DIRECTORY={directory}");
            Console.WriteLine($"DEFECTIVE_DRY_RUN_ID={defectiveReport.DryRunId}");
            Console.WriteLine($"CORRECTED_DRY_RUN_ID={correctedReport.DryRunId}");
        }
        finally
        {
            if (deleteRunDirectory && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    private static string FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ProofShift.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ProofShift repository root could not be located from the test output directory.");
    }

    private static async Task<CliResult> RunCliAsync(string workingDirectory, string cliAssembly, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ProofShift CLI process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static (SystemDefinition Source, SystemDefinition Target, LoadedProjectConfiguration Configuration) CreateConfiguration()
    {
        var sourceEndpoints = new[]
        {
            new StorageEndpointDefinition(new StorageEndpointId("pension-source-db"), new ConnectorId("sqlserver"),
                [new KeyValuePair<string, string>("connection", $"secret:{SourceConnectionSecret}")]),
            new StorageEndpointDefinition(new StorageEndpointId("pension-source-csv"), new ConnectorId("csv"),
                [new KeyValuePair<string, string>("root", $"secret:{CsvRootSecret}"), new KeyValuePair<string, string>("delimiter", ";")]),
            new StorageEndpointDefinition(new StorageEndpointId("pension-source-files"), new ConnectorId("files"),
                [new KeyValuePair<string, string>("root", $"secret:{SourceFilesRootSecret}")])
        };
        var sourceSystem = new SystemDefinition(new SystemId("legacy-pension"), "Synthetic Legacy Pension",
            SystemRole.Source, sourceEndpoints);
        var targetSystem = new SystemDefinition(new SystemId("external-pension"), "Synthetic External Pension Target",
            SystemRole.ShadowTarget,
            [
                new StorageEndpointDefinition(new StorageEndpointId("external-target"), new ConnectorId("postgres"),
                    [new KeyValuePair<string, string>("connection", $"secret:{TargetConnectionSecret}")]),
                new StorageEndpointDefinition(new StorageEndpointId("external-files"), new ConnectorId("files"),
                    [new KeyValuePair<string, string>("root", $"secret:{TargetFilesRootSecret}")])
            ]);
        var configuration = new LoadedProjectConfiguration(
            new RootConfigurationDto(1, new ProjectConfigurationDto("ps09-fast", "Synthetic PS-0.9 Fast"),
                new PackConfigurationDto("proofshift.pension", "0.9.0"), [], null, null, null),
            [], [sourceSystem, targetSystem], [], "synthetic-ps09-fast", new string('a', 64));
        return (sourceSystem, targetSystem, configuration);
    }

    private static ScenarioGraph CreateScenario(IReadOnlyCollection<PensionSyntheticRecord> sourceData,
        SystemId sourceSystemId, SystemId targetSystemId, bool falseReverse)
    {
        var targetEndpointId = new StorageEndpointId("external-target");
        var semanticTypes = sourceData.GroupBy(record => record.Kind)
            .ToDictionary(group => group.Key, group => group.First().SemanticType);
        var sourceNodes = new Dictionary<PensionRecordKind, MigrationNode>();
        var targetNodes = new Dictionary<string, MigrationNode>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<PensionRecordKind>())
        {
            var sourceName = SourceNode(kind);
            var sourceSelector = kind is PensionRecordKind.Document or PensionRecordKind.HistoricalExport
                ? new ArtifactSelector("csv", [new KeyValuePair<string, string>("path", CsvFileName(kind)),
                    new KeyValuePair<string, string>("delimiter", ";")], SourceIdentityFields(kind))
                : new ArtifactSelector("table", [new KeyValuePair<string, string>("schema", "dbo"),
                    new KeyValuePair<string, string>("table", SourceTableName(kind))], SourceIdentityFields(kind));
            sourceNodes.Add(kind, new MigrationNode(Id(sourceName), sourceName, MigrationNodeType.Source,
                semanticTypes[kind], sourceSystemId, new StorageEndpointId(SourceEndpoint(kind)),
                sourceSelector));
        }
        targetNodes.Add("target-member", MakeTargetNode("target-member", "Pension.Member", targetSystemId, targetEndpointId, ["member_id"]));
        targetNodes.Add("target-member-status", MakeTargetNode("target-member-status", "Pension.MemberStatus", targetSystemId, targetEndpointId, ["member_id"]));
        foreach (var kind in Enum.GetValues<PensionRecordKind>().Where(kind => kind != PensionRecordKind.Member))
        {
            var targetName = TargetNode(kind);
            targetNodes.Add(targetName, MakeTargetNode(targetName, semanticTypes[kind], targetSystemId, targetEndpointId, TargetIdentityFields(kind)));
        }

        var fileSourceNodes = new Dictionary<PensionRecordKind, MigrationNode>();
        var fileTargetNodes = new Dictionary<PensionRecordKind, MigrationNode>();
        foreach (var kind in new[] { PensionRecordKind.Document, PensionRecordKind.HistoricalExport })
        {
            var fileKind = kind == PensionRecordKind.Document ? "document" : "historical-export";
            var semanticType = kind == PensionRecordKind.Document ? "Pension.DocumentPayload" : "Pension.HistoricalExportPayload";
            var sourceName = $"source-{fileKind}-files";
            var targetName = $"target-{fileKind}-files";
            fileSourceNodes.Add(kind, new MigrationNode(Id(sourceName), sourceName, MigrationNodeType.Source,
                semanticType, sourceSystemId, new StorageEndpointId("pension-source-files"),
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pattern", FilePattern(kind))], ["relativePath"])));
            fileTargetNodes.Add(kind, new MigrationNode(Id(targetName), targetName, MigrationNodeType.Archive,
                semanticType, targetSystemId, new StorageEndpointId("external-files"),
                new ArtifactSelector("file-pattern", [new KeyValuePair<string, string>("pathField", "relativePath")], ["relativePath"])));
        }

        var recovery = falseReverse ? RecoveryMode.Reverse : RecoveryMode.Restore;
        var edges = new List<MigrationEdge>
        {
            MakeEdge(PensionDefectInjector.FalseReverseDeclarations[0].EdgeName, sourceNodes[PensionRecordKind.Member],
                targetNodes["target-member"], MemberParticipantFields(), recovery),
            MakeEdge(PensionDefectInjector.FalseReverseDeclarations[1].EdgeName, sourceNodes[PensionRecordKind.Member],
                targetNodes["target-member-status"], MemberStatusFields(), recovery)
        };
        foreach (var kind in Enum.GetValues<PensionRecordKind>().Where(kind => kind != PensionRecordKind.Member))
            edges.Add(MakeEdge($"{TargetNode(kind)}-conversion", sourceNodes[kind], targetNodes[TargetNode(kind)],
                TargetFields(kind), RecoveryMode.Restore));
        foreach (var kind in fileSourceNodes.Keys)
        {
            var edgeName = $"{SourceNode(kind)}-binary-archive";
            edges.Add(new MigrationEdge(EdgeId(edgeName), edgeName, [fileSourceNodes[kind].Id], [fileTargetNodes[kind].Id],
                new MigrationOperation(MigrationOperationType.Archive), "1",
                new RecoveryDefinition(RecoveryMode.Restore, requiresSnapshot: true)));
        }
        var nodes = sourceNodes.Values.Concat(fileSourceNodes.Values).Concat(targetNodes.Values).Concat(fileTargetNodes.Values)
            .OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var names = nodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, nodes, edges, names);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new ScenarioGraph(new MigrationGraph(new MigrationGraphId(Id("pension-fast-graph").Value), nodes, edges, hash,
            GraphCanonicalizer.FormatVersion), sourceNodes, targetNodes, fileSourceNodes, fileTargetNodes);
    }

    private static MigrationEdge MakeEdge(string name, MigrationNode source, MigrationNode target,
        IEnumerable<TransformationFieldDefinition> fields, RecoveryMode mode) =>
        new(EdgeId(name), name, [source.Id], [target.Id], new MigrationOperation(MigrationOperationType.Transform, fields: fields),
            "1", new RecoveryDefinition(mode, requiresSnapshot: mode == RecoveryMode.Restore));

    private static IEnumerable<TransformationFieldDefinition> MemberParticipantFields() =>
    [
        new("member_id", "MEMBER_ID"),
        new("display_name", "FIRST_NM", [new TransformationStep(TransformationStepType.Trim, "1"), new TransformationStep(TransformationStepType.NormalizeString, "1")]),
        new("birth_date", "BIRTH_DT"), new("joined_date", "ENROLL_DT")
    ];

    private static IEnumerable<TransformationFieldDefinition> MemberStatusFields() =>
    [
        new("member_id", "MEMBER_ID"),
        new("status", "STATUS_CD", [Map(("A", "ACTIVE"), ("I", "INACTIVE"), ("R", "RETIRED"), ("unknown", "pass-through"))])
    ];

    private static IEnumerable<TransformationFieldDefinition> TargetFields(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Employment =>
        [
            new("source_employment_ref", "EMPLOYMENT_ID"), new("participant_id", "MEMBER_ID"),
            new("event_date", "PERIOD_FROM"), new("event_code", "PERIOD_INDEX", [Map(("0", "JOINED"), ("1", "TERMINATED"), ("2", "REINSTATED"))]),
            new("source_state", "EMPLOYMENT_STATUS")
        ],
        PensionRecordKind.Contribution =>
        [
            new("contribution_id", "TRANSACTION_ID"), new("participant_id", "MEMBER_ID"), new("payroll_period", "PAY_PERIOD"),
            new("contribution_kind", "CONTRIBUTION_CATEGORY"), new("contribution_amount", "CONTRIBUTION_AMT")
        ],
        PensionRecordKind.ServiceCredit =>
        [
            new("service_period_id", "SERVICE_ID"), new("participant_id", "MEMBER_ID"), new("service_from", "SERVICE_FROM"),
            new("service_to", "SERVICE_TO"), new("service_credit", "CREDIT_AMT"), new("credit_code", "CREDIT_TYPE")
        ],
        PensionRecordKind.Beneficiary =>
        [
            new("beneficiary_id", "BENEFICIARY_ID"), new("participant_id", "MEMBER_ID"),
            new("relationship_type", "RELATIONSHIP_CD"), new("allocation_pct", "ALLOCATION_PCT")
        ],
        PensionRecordKind.RetirementElection =>
        [
            new("election_id", "ELECTION_ID"), new("participant_id", "MEMBER_ID"),
            new("option_code", "OPTION_CD", [Map(("J50", "JOINT_SURVIVOR_50"), ("SINGLE", "SINGLE_LIFE"))]),
            new("effective_date", "EFFECTIVE_DT")
        ],
        PensionRecordKind.BenefitPayment =>
        [
            new("payment_id", "PAYMENT_ID"), new("participant_id", "MEMBER_ID"), new("payment_period", "PAYMENT_PERIOD"),
            new("paid_on", "PAYMENT_DT"), new("paid_amount", "GROSS_AMT")
        ],
        PensionRecordKind.Document =>
        [
            new("document_id", "DOCUMENT_ID"), new("participant_id", "MEMBER_ID"), new("document_type", "DOCUMENT_CATEGORY"),
            new("object_key", "RELATIVE_PATH"), new("content_hash", "CONTENT_SHA256")
        ],
        PensionRecordKind.HistoricalExport =>
        [
            new("export_id", "EXPORT_ID"), new("participant_id", "MEMBER_ID"),
            new("object_key", "RELATIVE_PATH"), new("content_hash", "CONTENT_SHA256")
        ],
        PensionRecordKind.Member => throw new ArgumentOutOfRangeException(nameof(kind)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static TransformationStep Map(params (string Key, string Value)[] values) =>
        new(TransformationStepType.CodeMap, "1", values.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

    private static VerificationRuleDefinition[] CreateRules()
    {
        var definitions = PensionSemanticRuleTests.RuleDefinitions();
        return definitions.Select(definition => definition.Id.Value switch
        {
            "member-status" => new VerificationRuleDefinition(definition.Id, definition.Type, definition.Version, definition.Severity,
                definition.Options.Where(pair => pair.Key is not ("targetNode" or "semanticType"))
                    .Append(new KeyValuePair<string, string>("targetNode", "target-member-status"))
                    .Append(new KeyValuePair<string, string>("semanticType", "Pension.MemberStatus"))),
            "employment-timeline" => new VerificationRuleDefinition(definition.Id, definition.Type, definition.Version, definition.Severity,
                definition.Options.Where(pair => pair.Key is not ("sourceMemberField" or "sourceStartField" or "sourceEndField" or "sourceStatusField"))
                    .Append(new KeyValuePair<string, string>("sourceMemberField", "MEMBER_ID"))
                    .Append(new KeyValuePair<string, string>("sourceStartField", "PERIOD_FROM"))
                    .Append(new KeyValuePair<string, string>("sourceEndField", "PERIOD_TO"))
                    .Append(new KeyValuePair<string, string>("sourceStatusField", "EMPLOYMENT_STATUS"))),
            _ => definition
        }).ToArray();
    }

    private static async Task<ExternalVerificationResult> VerifyExternalAsync(VerificationService service,
        LoadedProjectConfiguration configuration, ScenarioGraph scenario, SnapshotCaptureResult checkpoint,
        PensionSyntheticRecord[] targetData, VerificationRuleSet rules,
        RuntimeConnectorContextFactory contextFactory, ProofShift.Connectors.Postgres.PostgresShadowTargetConnector connector,
        FilesystemShadowTargetConnector fileConnector, PostgreSqlContainer postgresContainer,
        string directory, string observationId, PerformanceRecorder performanceRecorder)
    {
        var observationRunId = new RunId(Guid.NewGuid());
        using (var fixtureLoadStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture,
            "external target fixture load"))
        {
            var targetRecords = ToTargetRecords(targetData, scenario);
            await LoadExternalTargetAsync(postgresContainer, observationRunId, targetRecords);
            await LoadExternalFilesystemTargetAsync(configuration, scenario, contextFactory, fileConnector,
                targetData, observationRunId, TestContext.Current.CancellationToken);
            fixtureLoadStage.AddArtifacts(targetData.Length);
        }
        var runtimes = scenario.TargetNodes.Values.Select(node => new VerificationTargetRuntime(node.Name, connector,
                new ShadowTargetContext(contextFactory.Create(configuration, node), observationRunId, SystemRole.ShadowTarget)))
            .Concat(scenario.FileTargetNodes.Values.Select(node => new VerificationTargetRuntime(node.Name, fileConnector,
                new ShadowTargetContext(contextFactory.Create(configuration, node), observationRunId, SystemRole.ShadowTarget))))
            .ToArray();
        foreach (var runtime in runtimes)
        {
            var node = scenario.Graph.Nodes.Single(item => item.Name == runtime.NodeKey);
            await foreach (var record in runtime.Connector.ReadAsync(new ReadRequest(runtime.Context, node.Selector),
                TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken).ConfigureAwait(false))
            {
                foreach (var identityField in node.Selector.IdentityFields)
                    Assert.True(record.Values.TryGetValue(identityField, out var value) && value is not NullValue,
                        $"External target node '{node.Name}' has no value for identity field '{identityField}'. Fields: {string.Join(',', record.Values.Keys)}");
            }
        }
        var targetNodes = scenario.TargetNodes.Values.Concat(scenario.FileTargetNodes.Values).ToArray();
        var observation = new ExternalMigrationObservation(observationRunId, observationId, configuration.ConfigurationHash,
            scenario.Graph.GraphHash, checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!, checkpoint.Checkpoint.SourceFingerprint!,
            targetNodes.Select(node =>
            {
                var nodeConnector = node.EndpointId.Value == "external-files" ? (IShadowTargetConnector)fileConnector : connector;
                return new ExternalTargetEndpoint(node.Name, node.SystemId, node.EndpointId,
                    nodeConnector.Id, nodeConnector.Version);
            }), DateTimeOffset.UnixEpoch);
        return await service.VerifyExternalTargetAsync(configuration, scenario.Graph, observation, rules,
            Path.Combine(directory, $"working-{observationId}"), "0.9-test", runtimes, performanceRecorder,
            TestContext.Current.CancellationToken);
    }

    private static async Task LoadExternalFilesystemTargetAsync(LoadedProjectConfiguration configuration,
        ScenarioGraph scenario, RuntimeConnectorContextFactory contextFactory, FilesystemShadowTargetConnector targetConnector,
        IReadOnlyCollection<PensionSyntheticRecord> targetData, RunId runId, CancellationToken cancellationToken)
    {
        var sourceConnector = new FilesystemSourceConnector();
        foreach (var (kind, sourceNode) in scenario.FileSourceNodes)
        {
            var targetNode = scenario.FileTargetNodes[kind];
            var payloadPaths = targetData.Where(record => record.Kind == kind)
                .Select(record => ((StringValue)record.Values["object_key"]).Value)
                .ToHashSet(StringComparer.Ordinal);
            var sourceContext = contextFactory.Create(configuration, sourceNode);
            var targetContext = new ShadowTargetContext(contextFactory.Create(configuration, targetNode), runId, SystemRole.ShadowTarget);
            await targetConnector.PrepareAsync(targetContext, targetNode.Selector, cancellationToken);
            await foreach (var sourceRecord in sourceConnector.ReadAsync(sourceContext, sourceNode.Selector,
                new ReadOptions(), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var relativePath = ((StringValue)sourceRecord.Values["relativePath"]).Value;
                if (!payloadPaths.Contains(relativePath)) continue;
                await targetConnector.WriteAsync(new ShadowWriteRequest(targetContext, targetNode.Selector,
                    sourceRecord, targetNode.Name, (field, token) => sourceConnector.OpenBinaryReadAsync(sourceContext,
                        sourceNode.Selector, sourceRecord.Artifact,
                        (BinaryReferenceValue)sourceRecord.Values[field], token)), cancellationToken);
            }
            await targetConnector.CompleteAsync(targetContext, cancellationToken);
        }
    }

    private static async Task MaterializeCsvAndFileSourcesAsync(IReadOnlyCollection<PensionSyntheticRecord> records,
        string csvRoot, string filesRoot)
    {
        Directory.CreateDirectory(csvRoot);
        Directory.CreateDirectory(filesRoot);
        foreach (var kind in new[] { PensionRecordKind.Document, PensionRecordKind.HistoricalExport })
        {
            var mappings = SourceColumnMappings(kind);
            var rows = records.Where(record => record.Kind == kind).ToArray();
            var lines = new List<string> { string.Join(';', mappings.Values.Select(EscapeCsv)) };
            lines.AddRange(rows.Select(record => string.Join(';', mappings.Keys.Select(field =>
                EscapeCsv(DatabaseText(record.Values[field]))))));
            await File.WriteAllLinesAsync(Path.Combine(csvRoot, CsvFileName(kind)), lines, new UTF8Encoding(false),
                TestContext.Current.CancellationToken);

            foreach (var record in rows)
            {
                var relativePath = ((StringValue)record.Values["relative_path"]).Value;
                var fullPath = Path.Combine(filesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var bytes = SyntheticPayload(record);
                var expectedHash = ((StringValue)record.Values["content_hash"]).Value;
                Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                await File.WriteAllBytesAsync(fullPath, bytes, TestContext.Current.CancellationToken);
            }
        }
    }

    private static byte[] SyntheticPayload(PensionSyntheticRecord record)
    {
        var type = record.Kind == PensionRecordKind.Document ? "document" : "export";
        return Encoding.UTF8.GetBytes($"{PensionSyntheticDatasetGenerator.DefaultSeed}|{type}|{record.Sequence}");
    }

    private static string EscapeCsv(string value) => value.IndexOfAny([';', '"', '\r', '\n']) < 0
        ? value
        : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static long DirectorySize(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
        : 0;

    private static async Task CreatePostgresTargetTemplatesAsync(PostgreSqlContainer container,
        IReadOnlyCollection<PensionSyntheticRecord> targetData, ScenarioGraph scenario)
    {
        var recordsByNode = ToTargetRecords(targetData, scenario);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var node in scenario.TargetNodes.Values.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            var records = recordsByNode[node.Name];
            var columns = records.SelectMany(record => record.Values.Keys).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var definitions = columns.Select(column =>
            {
                var value = records.SelectMany(record => record.Values)
                    .First(pair => pair.Key == column && pair.Value is not NullValue).Value;
                return $"{QuoteIdentifier(column)} {PostgresType(value)}";
            });
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TABLE public.{QuoteIdentifier(TableName(node.Name))} ({string.Join(", ", definitions)})";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static string PostgresType(ValueNode value) => value switch
    {
        StringValue => "text",
        IntegerValue => "bigint",
        DecimalValue => "numeric",
        BooleanValue => "boolean",
        DateValue => "date",
        InstantValue => "timestamp with time zone",
        OffsetDateTimeValue => "timestamp with time zone",
        LocalDateTimeValue => "timestamp without time zone",
        _ => throw new InvalidDataException("Projected pension target template contains an unsupported value type.")
    };

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task<(ProjectionRun Projection, VerificationResult Verification, RecoveryRunResult Recovery)>
        ProjectVerifyAndQualifyAsync(FileSystemSnapshotStore checkpointStore, ConnectorRegistry sourceConnectors,
            ShadowTargetConnectorRegistry targetConnectors, RuntimeConnectorContextFactory contextFactory,
            LoadedProjectConfiguration configuration, ScenarioGraph scenario, SnapshotCaptureResult capture,
            VerificationRuleSet rules, string projectDirectory, string runName,
            PerformanceRecorder performanceRecorder,
            Func<ProjectionRun, Task>? beforeVerification = null)
    {
        await using var checkpoint = await checkpointStore.OpenCompleteAsync(
            capture.Id.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
        var projection = await new ShadowProjectionService(sourceConnectors, targetConnectors, contextFactory,
            new CheckpointSourceArtifactStreamProvider(checkpoint)).ProjectAsync(configuration, scenario.Graph,
                projectDirectory, performanceRecorder, TestContext.Current.CancellationToken);
        Assert.Equal(ProjectionStatus.Succeeded, projection.Status);
        var manifestHash = await ProjectionRunManifestStore.WriteAsync(projectDirectory, projection,
            TestContext.Current.CancellationToken);
        var manifest = await ProjectionRunManifestStore.ReadAsync(projectDirectory, projection.Id,
            TestContext.Current.CancellationToken);
        var binding = new ProjectionVerificationBinding(projection.Id, projection.ConfigurationHash, projection.GraphHash,
            capture.Id, capture.Checkpoint!.ManifestHash!, capture.Checkpoint.SourceFingerprint!, projection.Fingerprint!,
            projection.FingerprintVersion, projection.SourceArtifactCount, projection.TargetArtifactCount, "succeeded",
            manifestHash, projection.JournalPath, projection.ConnectorVersions);
        Assert.Equal(manifestHash, manifest.ManifestHash);

        var targets = scenario.TargetNodes.Values.Concat(scenario.FileTargetNodes.Values).Select(node => new VerificationTargetRuntime(node.Name,
            targetConnectors.Resolve(configuration.Systems.Single(system => system.Id == node.SystemId)
                .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId).Connector),
            new ShadowTargetContext(contextFactory.Create(configuration, node), projection.Id, SystemRole.ShadowTarget))).ToArray();
        if (beforeVerification is not null)
        {
            using var fixtureMutationStage = performanceRecorder.StartStage(PerformanceStageKind.Fixture,
                "projected target defect injection");
            await beforeVerification(projection);
            fixtureMutationStage.AddArtifacts(projection.TargetArtifactCount);
        }
        var verification = await new VerificationService(checkpointStore).VerifyAsync(configuration, scenario.Graph, binding,
            rules, projectDirectory, Path.Combine(projectDirectory, ".proofshift", "temporary", runName), "0.9-test",
            targets, performanceRecorder, TestContext.Current.CancellationToken);
        var recovery = await new RecoveryService(checkpointStore,
            new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator()]))
            .AssessAndRehearseAsync(configuration, scenario.Graph, binding, verification, new EffectiveRecoveryPolicy(),
                targets, projectDirectory, performanceRecorder, TestContext.Current.CancellationToken);
        return (projection, verification, recovery);
    }

    private static async Task ReplaceProjectedPostgresTargetsAsync(PostgreSqlContainer container, RunId runId,
        IReadOnlyCollection<PensionSyntheticRecord> targetData, ScenarioGraph scenario)
    {
        var schema = $"proofshift_shadow_{runId.Value:N}";
        var recordsByNode = ToTargetRecords(targetData, scenario);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var (nodeName, records) in recordsByNode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var columns = records.SelectMany(record => record.Values.Keys).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            await using (var replace = connection.CreateCommand())
            {
                replace.CommandText = $"DROP TABLE \"{schema}\".{QuoteIdentifier(TableName(nodeName))}; CREATE TABLE \"{schema}\".{QuoteIdentifier(TableName(nodeName))} ({string.Join(", ", columns.Select(column => $"{QuoteIdentifier(column)} text"))})";
                await replace.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var copy = $"COPY \"{schema}\".{QuoteIdentifier(TableName(nodeName))} ({string.Join(", ", columns.Select(QuoteIdentifier))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, TestContext.Current.CancellationToken);
            foreach (var record in records)
            {
                await importer.StartRowAsync(TestContext.Current.CancellationToken);
                foreach (var column in columns)
                {
                    if (!record.Values.TryGetValue(column, out var value) || value is NullValue)
                        await importer.WriteNullAsync(TestContext.Current.CancellationToken);
                    else
                        await importer.WriteAsync(DatabaseText(value), NpgsqlTypes.NpgsqlDbType.Text, TestContext.Current.CancellationToken);
                }
            }
            await importer.CompleteAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task RemoveMissingProjectedFilesAsync(string targetFilesRoot, RunId runId,
        IReadOnlyCollection<PensionSyntheticRecord> correctedTarget, IReadOnlyCollection<PensionSyntheticRecord> defectiveTarget,
        ScenarioGraph scenario)
    {
        var runRoot = Path.Combine(targetFilesRoot, runId.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        var indexPath = Path.Combine(runRoot, ".proofshift-internal", "identities.sqlite");
        await using var connection = new SqliteConnection($"Data Source={indexPath};Mode=ReadWrite;Cache=Private;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var kind in scenario.FileTargetNodes.Keys)
        {
            var node = scenario.FileTargetNodes[kind];
            var correctedPaths = correctedTarget.Where(record => record.Kind == kind)
                .Select(record => ((StringValue)record.Values["object_key"]).Value).ToHashSet(StringComparer.Ordinal);
            var defectivePaths = defectiveTarget.Where(record => record.Kind == kind)
                .Select(record => ((StringValue)record.Values["object_key"]).Value).ToHashSet(StringComparer.Ordinal);
            foreach (var path in correctedPaths.Except(defectivePaths, StringComparer.Ordinal))
            {
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM identities WHERE node_key = $node AND logical_path = $path";
                remove.Parameters.AddWithValue("$node", node.Name);
                remove.Parameters.AddWithValue("$path", path);
                Assert.Equal(1, await remove.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                var fullPath = Path.Combine(runRoot, node.Name, path.Replace('/', Path.DirectorySeparatorChar));
                File.Delete(fullPath);
            }
        }
    }

    private static async Task<PensionAssuranceReport> BuildPersistedReportAsync(string projectDirectory,
        FileSystemEvidenceStore evidenceStore, FileSystemRecoveryArtifactStore recoveryStore,
        VerificationResult verification, RecoveryRunResult recovery, string projectName)
    {
        Assert.True(await evidenceStore.VerifyIntegrityAsync(verification.Run.Id, TestContext.Current.CancellationToken));
        Assert.True(await recoveryStore.VerifyIntegrityAsync(recovery.Id, TestContext.Current.CancellationToken));
        var summary = await recoveryStore.ReadSummaryAsync(recovery.Id, TestContext.Current.CancellationToken);
        await using var evidenceStream = await evidenceStore.OpenReadAsync(verification.Run.Id, TestContext.Current.CancellationToken);
        using var evidenceDocument = await JsonDocument.ParseAsync(evidenceStream, cancellationToken: TestContext.Current.CancellationToken);
        var report = PensionAssuranceReportBuilder.Build(projectName, recovery.Id, evidenceDocument.RootElement, summary);
        var reportDirectory = Path.Combine(projectDirectory, ".proofshift", "reports");
        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{report.DryRunId}.json"),
            JsonSerializer.Serialize(report), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, $"{report.DryRunId}.txt"),
            RenderPensionReport(report), TestContext.Current.CancellationToken);
        return report;
    }

    private static string RenderPensionReport(PensionAssuranceReport report)
    {
        var lines = new List<string>
        {
            "ProofShift Public Pension Migration Assurance",
            $"Verification: {report.VerificationOutcome}",
            $"Recovery: {report.RehearsalOutcome.ToUpperInvariant()} ({report.FailedRecoveryEdges} failed edges)",
            $"Business Discrepancies: {report.BusinessDiscrepancyCount}"
        };
        lines.AddRange(report.DefectCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value}"));
        lines.Add($"Unaccounted Sources: {report.UnaccountedSources}");
        lines.Add($"Unexplained Targets: {report.UnexplainedTargets}");
        lines.Add($"Verification Failures: {report.VerificationFailureCount}");
        lines.Add($"Recovery Failures: {report.FailedRecoveryEdges}");
        lines.Add($"Qualification: {report.Qualification}");
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task LoadExternalTargetAsync(PostgreSqlContainer container, RunId observationRunId,
        IReadOnlyDictionary<string, IReadOnlyCollection<RecordEnvelope>> recordsByNode)
    {
        var schema = $"proofshift_shadow_{observationRunId.Value:N}";
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var createSchema = connection.CreateCommand())
        {
            createSchema.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await createSchema.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        foreach (var (nodeName, records) in recordsByNode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var columns = records.SelectMany(record => record.Values.Keys).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var table = TableName(nodeName);
            await using (var createTable = connection.CreateCommand())
            {
                createTable.CommandText = $"CREATE TABLE \"{schema}\".\"{table}\" ({string.Join(", ", columns.Select(column => $"\"{column}\" text"))})";
                await createTable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var copy = $"COPY \"{schema}\".\"{table}\" ({string.Join(", ", columns.Select(column => $"\"{column}\""))}) FROM STDIN (FORMAT BINARY)";
            await using var importer = await connection.BeginBinaryImportAsync(copy, TestContext.Current.CancellationToken);
            foreach (var record in records)
            {
                await importer.StartRowAsync(TestContext.Current.CancellationToken);
                foreach (var column in columns)
                {
                    if (!record.Values.TryGetValue(column, out var value) || value is NullValue)
                        await importer.WriteNullAsync(TestContext.Current.CancellationToken);
                    else
                        await importer.WriteAsync(DatabaseText(value), NpgsqlTypes.NpgsqlDbType.Text, TestContext.Current.CancellationToken);
                }
            }
            await importer.CompleteAsync(TestContext.Current.CancellationToken);
        }
    }

    private static string DatabaseText(ValueNode value) => value switch
    {
        StringValue item => item.Value,
        IntegerValue item => item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DecimalValue item => item.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        BooleanValue item => item.Value ? "true" : "false",
        DateValue item => item.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        InstantValue item => item.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        OffsetDateTimeValue item => item.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        LocalDateTimeValue item => item.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        BinaryReferenceValue item => item.Sha256,
        _ => throw new InvalidDataException("External pension target fixture contains an unsupported normalized value.")
    };

    private static Dictionary<string, IReadOnlyCollection<RecordEnvelope>> ToTargetRecords(
        IEnumerable<PensionSyntheticRecord> targetData, ScenarioGraph scenario)
    {
        var result = scenario.TargetNodes.Keys.ToDictionary(name => name, _ => new List<RecordEnvelope>(), StringComparer.Ordinal);
        foreach (var synthetic in targetData)
        {
            if (synthetic.Kind == PensionRecordKind.Member)
            {
                result["target-member"].Add(TargetRecord(synthetic, scenario.TargetNodes["target-member"],
                    synthetic.Values.Where(pair => pair.Key is not ("status" or "fixture_duplicate"))));
                result["target-member-status"].Add(TargetRecord(synthetic, scenario.TargetNodes["target-member-status"],
                    synthetic.Values.Where(pair => pair.Key is "member_id" or "status")));
            }
            else
            {
                var nodeName = TargetNode(synthetic.Kind);
                result[nodeName].Add(TargetRecord(synthetic, scenario.TargetNodes[nodeName],
                    synthetic.Values.Where(pair => pair.Key != "fixture_duplicate")));
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<RecordEnvelope>)pair.Value, StringComparer.Ordinal);
    }

    private static RecordEnvelope TargetRecord(PensionSyntheticRecord synthetic, MigrationNode node,
        IEnumerable<KeyValuePair<string, ValueNode>> fields)
    {
        var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var artifactType = StableArtifactIdentity.ArtifactTypeFor(node.Selector);
        var provisional = new RecordEnvelope(new ArtifactReference(new ArtifactId("pending"), node.SystemId,
            node.EndpointId, artifactType, "pending"), node.SemanticType, values,
            new ProvenanceMetadata(new ConnectorId("synthetic-target"), node.EndpointId, "external-fixture-loader", DateTimeOffset.UnixEpoch));
        var identity = GraphTargetIdentity.Create(provisional, node.Selector);
        return new RecordEnvelope(new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(
            node.SystemId.Value, node.EndpointId.Value, artifactType, identity)), node.SystemId, node.EndpointId,
            artifactType, identity), node.SemanticType, values, provisional.Provenance);
    }

    private static async Task BulkLoadSourceKindAsync(SqlConnection connection, PensionRecordKind kind,
        IReadOnlyCollection<PensionSyntheticRecord> records)
    {
        var mappings = SourceColumnMappings(kind);
        var table = new DataTable();
        foreach (var column in mappings.Values) table.Columns.Add(column, typeof(string));
        foreach (var record in records)
        {
            var row = table.NewRow();
            foreach (var (sourceField, databaseColumn) in mappings)
                row[databaseColumn] = record.Values[sourceField] is NullValue
                    ? DBNull.Value
                    : DatabaseText(record.Values[sourceField]);
            table.Rows.Add(row);
        }

        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = $"dbo.{SourceTableName(kind)}",
            BatchSize = 5_000,
            BulkCopyTimeout = 0
        };
        foreach (var column in mappings.Values) bulkCopy.ColumnMappings.Add(column, column);
        await bulkCopy.WriteToServerAsync(table, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> SourceColumnMappings(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Member => new Dictionary<string, string>
        {
            ["member_id"] = "MEMBER_ID", ["first_name"] = "FIRST_NM", ["status_code"] = "STATUS_CD",
            ["birth_date"] = "BIRTH_DT", ["enrollment_date"] = "ENROLL_DT"
        },
        PensionRecordKind.Employment => new Dictionary<string, string>
        {
            ["employment_id"] = "EMPLOYMENT_ID", ["member_id"] = "MEMBER_ID", ["employer_id"] = "EMPLOYER_CD",
            ["effective_from"] = "PERIOD_FROM", ["effective_to"] = "PERIOD_TO", ["period_index"] = "PERIOD_INDEX",
            ["status"] = "EMPLOYMENT_STATUS"
        },
        PensionRecordKind.Contribution => new Dictionary<string, string>
        {
            ["transaction_id"] = "TRANSACTION_ID", ["member_id"] = "MEMBER_ID", ["period"] = "PAY_PERIOD",
            ["category"] = "CONTRIBUTION_CATEGORY", ["amount"] = "CONTRIBUTION_AMT"
        },
        PensionRecordKind.ServiceCredit => new Dictionary<string, string>
        {
            ["service_id"] = "SERVICE_ID", ["member_id"] = "MEMBER_ID", ["period_from"] = "SERVICE_FROM",
            ["period_to"] = "SERVICE_TO", ["credit"] = "CREDIT_AMT", ["credit_type"] = "CREDIT_TYPE"
        },
        PensionRecordKind.Beneficiary => new Dictionary<string, string>
        {
            ["beneficiary_id"] = "BENEFICIARY_ID", ["member_id"] = "MEMBER_ID", ["relationship"] = "RELATIONSHIP_CD",
            ["allocation"] = "ALLOCATION_PCT"
        },
        PensionRecordKind.RetirementElection => new Dictionary<string, string>
        {
            ["election_id"] = "ELECTION_ID", ["member_id"] = "MEMBER_ID", ["election_code"] = "OPTION_CD",
            ["effective_date"] = "EFFECTIVE_DT"
        },
        PensionRecordKind.BenefitPayment => new Dictionary<string, string>
        {
            ["payment_id"] = "PAYMENT_ID", ["member_id"] = "MEMBER_ID", ["period"] = "PAYMENT_PERIOD",
            ["payment_date"] = "PAYMENT_DT", ["amount"] = "GROSS_AMT"
        },
        PensionRecordKind.Document => new Dictionary<string, string>
        {
            ["document_id"] = "DOCUMENT_ID", ["member_id"] = "MEMBER_ID", ["category"] = "DOCUMENT_CATEGORY",
            ["relative_path"] = "RELATIVE_PATH", ["content_hash"] = "CONTENT_SHA256"
        },
        PensionRecordKind.HistoricalExport => new Dictionary<string, string>
        {
            ["export_id"] = "EXPORT_ID", ["member_id"] = "MEMBER_ID", ["relative_path"] = "RELATIVE_PATH",
            ["content_hash"] = "CONTENT_SHA256"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string SourceTableName(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Member => "MEMBER",
        PensionRecordKind.Employment => "EMPLOYMENT_HISTORY",
        PensionRecordKind.Contribution => "CONTRIBUTION",
        PensionRecordKind.ServiceCredit => "SERVICE_CREDIT",
        PensionRecordKind.Beneficiary => "BENEFICIARY",
        PensionRecordKind.RetirementElection => "RETIREMENT_ELECTION",
        PensionRecordKind.BenefitPayment => "BENEFIT_PAYMENT",
        PensionRecordKind.Document => "DOCUMENT_INDEX",
        PensionRecordKind.HistoricalExport => "HISTORICAL_EXPORT_INDEX",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static MigrationNode MakeTargetNode(string name, string semantic, SystemId system, StorageEndpointId endpoint,
        IEnumerable<string> identities) => new(Id(name), name, MigrationNodeType.Target, semantic, system, endpoint,
        new ArtifactSelector("table", [new KeyValuePair<string, string>("name", $"public.{TableName(name)}")], identities));

    private static string TableName(string nodeName) => nodeName switch
    {
        "target-member" => "participant",
        "target-member-status" => "member_status",
        "target-employment" => "employment_event",
        "target-contribution" => "contribution_transaction",
        "target-servicecredit" => "service_period",
        "target-beneficiary" => "beneficiary_relationship",
        "target-retirementelection" => "retirement_election",
        "target-benefitpayment" => "benefit_payment",
        "target-document" => "document_object_index",
        "target-historicalexport" => "historical_export",
        _ => throw new ArgumentOutOfRangeException(nameof(nodeName))
    };

    private static string TargetNode(PensionRecordKind kind) => $"target-{kind.ToString().ToLowerInvariant()}";
    private static string SourceNode(PensionRecordKind kind) => $"source-{kind.ToString().ToLowerInvariant()}";
    private static string SourceEndpoint(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Document or PensionRecordKind.HistoricalExport => "pension-source-csv",
        _ => "pension-source-db"
    };

    private static string CsvFileName(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Document => "documents.csv",
        PensionRecordKind.HistoricalExport => "historical-exports.csv",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string FilePattern(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Document => "member/**/*.bin",
        PensionRecordKind.HistoricalExport => "history/**/*.csv",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string[] SourceIdentityFields(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Member => ["MEMBER_ID"],
        PensionRecordKind.Employment => ["EMPLOYMENT_ID"],
        PensionRecordKind.Contribution => ["TRANSACTION_ID"],
        PensionRecordKind.ServiceCredit => ["SERVICE_ID"],
        PensionRecordKind.Beneficiary => ["BENEFICIARY_ID"],
        PensionRecordKind.RetirementElection => ["ELECTION_ID"],
        PensionRecordKind.BenefitPayment => ["PAYMENT_ID"],
        PensionRecordKind.Document => ["DOCUMENT_ID"],
        PensionRecordKind.HistoricalExport => ["EXPORT_ID"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string[] TargetIdentityFields(PensionRecordKind kind) => kind switch
    {
        PensionRecordKind.Employment => ["source_employment_ref"],
        PensionRecordKind.Contribution => ["contribution_id"],
        PensionRecordKind.ServiceCredit => ["service_period_id"],
        PensionRecordKind.Beneficiary => ["beneficiary_id"],
        PensionRecordKind.RetirementElection => ["election_id"],
        PensionRecordKind.BenefitPayment => ["payment_id"],
        PensionRecordKind.Document => ["document_id"],
        PensionRecordKind.HistoricalExport => ["export_id"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Dictionary<string, long> BusinessCounts(IEnumerable<VerificationFinding> findings, long falseReverseEdges)
    {
        var failures = findings.Where(finding => finding.Result == EvidenceResult.Fail)
            .Select(finding => new { code = finding.Code, result = "Fail" }).ToArray();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(failures));
        return new Dictionary<string, long>(PensionAssuranceReportBuilder.CountBusinessDiscrepancies(
            document.RootElement.EnumerateArray().ToArray(), falseReverseEdges), StringComparer.Ordinal);
    }

    private static Dictionary<string, long> ExpectedDefects() => new(StringComparer.Ordinal)
    {
        ["missingMembers"] = 7, ["duplicateMembers"] = 4, ["wrongMemberStatuses"] = 3,
        ["missingEmploymentPeriods"] = 12, ["incorrectEmploymentDates"] = 8, ["incorrectServiceCreditTotals"] = 17,
        ["missingContributions"] = 39, ["duplicateContributions"] = 8, ["incorrectContributionAmounts"] = 6,
        ["benefitPaymentDiscrepancies"] = 11, ["brokenBeneficiaryRelationships"] = 6, ["wrongMemberBeneficiaries"] = 2,
        ["incorrectRetirementElectionMappings"] = 3, ["incorrectCodeTransformations"] = 5,
        ["missingDocuments"] = 9, ["wrongMemberDocuments"] = 4, ["missingHistoricalExports"] = 3,
        ["falseReversibleTransformations"] = 2
    };

    private static MigrationNodeId Id(string name) => new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16)));
    private static MigrationEdgeId EdgeId(string name) => new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"edge:{name}")).AsSpan(0, 16)));

    private sealed record ScenarioGraph(MigrationGraph Graph, IReadOnlyDictionary<PensionRecordKind, MigrationNode> SourceNodes,
        IReadOnlyDictionary<string, MigrationNode> TargetNodes,
        IReadOnlyDictionary<PensionRecordKind, MigrationNode> FileSourceNodes,
        IReadOnlyDictionary<PensionRecordKind, MigrationNode> FileTargetNodes);

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CorpusEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }

}
