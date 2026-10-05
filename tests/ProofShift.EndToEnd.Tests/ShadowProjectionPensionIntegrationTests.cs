using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using DotNet.Testcontainers.Builders;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Projection;
using ProofShift.Packs.Pension;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class ShadowProjectionPensionIntegrationTests
{
    private const string Password = "Synthetic-PS05-Only-Password!2026";

    [Fact]
    public async Task TenMemberProjectionStreamsAllSourcesFailsUnknownCodeAndKeepsRerunsIsolated()
    {
        var sqlContainer = await StartSqlServerContainerAsync();
        await using var sqlCleanup = sqlContainer;
        var postgresContainer = await StartPostgresContainerAsync();
        await using var postgresCleanup = postgresContainer;
        var sourceRoot = CreateTemporaryDirectory();
        var csvRoot = CreateTemporaryDirectory();
        var shadowRoot = CreateTemporaryDirectory();
        var projectRoot = CreateTemporaryDirectory();
        try
        {
            var sqlBuilder = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
            {
                Encrypt = false,
                TrustServerCertificate = true
            };
            await SetupSourceAndTargetAsync(sqlBuilder.ConnectionString, postgresContainer.GetConnectionString());
            Directory.CreateDirectory(Path.Combine(sourceRoot, "member", "1"));
            var documentBytes = Encoding.UTF8.GetBytes("synthetic member statement bytes\n");
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "member", "1", "statement.pdf"), documentBytes,
                TestContext.Current.CancellationToken);
            var csvRows = new StringBuilder("MEMBER_ID;PAY_PERIOD;NOTE\n");
            for (var memberId = 1; memberId <= 10; memberId++)
            {
                csvRows.Append(CultureInfo.InvariantCulture, $"{memberId};2025-01;supplemental-{memberId}\n");
            }

            await File.WriteAllTextAsync(Path.Combine(csvRoot, "supplemental.csv"), csvRows.ToString(), new UTF8Encoding(false),
                TestContext.Current.CancellationToken);
            var sourceConnection = new SqlConnectionStringBuilder(sqlBuilder.ConnectionString).ConnectionString;
            var environment = new MapEnvironmentProvider(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PS05_SQL_CONNECTION"] = sourceConnection,
                ["PS05_POSTGRES_CONNECTION"] = postgresContainer.GetConnectionString(),
                ["PS05_SOURCE_FILES"] = sourceRoot,
                ["PS05_SOURCE_CSV"] = csvRoot,
                ["PS05_SHADOW_FILES"] = shadowRoot
            });
            var configuration = CreateConfiguration();
            var graph = CreateGraph();
            var sourceConnectors = new ConnectorRegistry(
            [
                new PostgresSourceConnector(),
                new SqlServerSourceConnector(),
                new FilesystemSourceConnector(),
                new CsvSourceConnector()
            ]);
            var targetConnectors = new ShadowTargetConnectorRegistry(
            [new PostgresShadowTargetConnector(), new FilesystemShadowTargetConnector()]);
            var service = new ShadowProjectionService(sourceConnectors, targetConnectors, new RuntimeConnectorContextFactory(environment));

            var failed = await service.ProjectAsync(configuration, graph, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(ProjectionStatus.Failed, failed.Status);
            Assert.Equal("PSPROJ_CODE_MAP", failed.FailureCode);
            Assert.Equal(21, failed.SourceArtifactCount);
            Assert.Equal(29, failed.TargetArtifactCount);
            Assert.Equal(1, failed.FailureCount);
            Assert.Null(failed.Fingerprint);
            var failedJournalPath = Path.Combine(projectRoot, failed.JournalPath.Replace('/', Path.DirectorySeparatorChar));
            var failedJournal = await File.ReadAllTextAsync(failedJournalPath, TestContext.Current.CancellationToken);
            Assert.Contains("failed", failedJournal, StringComparison.Ordinal);
            Assert.Contains("MEMBER_ID=2:10", failedJournal, StringComparison.Ordinal);
            Assert.DoesNotContain(sourceConnection, failedJournal, StringComparison.Ordinal);

            await using (var sourceConnectionToFix = new SqlConnection(sourceConnection))
            {
                await sourceConnectionToFix.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = sourceConnectionToFix.CreateCommand();
                update.CommandText = "UPDATE dbo.MEMBER SET MEMBER_STATUS = @status WHERE MEMBER_ID = @id";
                update.Parameters.AddWithValue("status", "A");
                update.Parameters.AddWithValue("id", 10);
                await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var succeeded = await service.ProjectAsync(configuration, graph, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(ProjectionStatus.Succeeded, succeeded.Status);
            Assert.Equal(21, succeeded.SourceArtifactCount);
            Assert.Equal(31, succeeded.TargetArtifactCount);
            Assert.Equal(0, succeeded.FailureCount);
            Assert.Equal(64, succeeded.Fingerprint?.Length);
            Assert.NotEqual(failed.Id, succeeded.Id);

            var schema = $"proofshift_shadow_{succeeded.Id.Value:N}";
            await using (var projected = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await projected.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal(10L, await CountAsync(projected, schema, "participant"));
                Assert.Equal(10L, await CountAsync(projected, schema, "member_status"));
                Assert.Equal(10L, await CountAsync(projected, schema, "supplemental_member"));
                await using var templateCount = projected.CreateCommand();
                templateCount.CommandText = "SELECT (SELECT COUNT(*) FROM public.participant) + (SELECT COUNT(*) FROM public.member_status) + (SELECT COUNT(*) FROM public.supplemental_member)";
                Assert.Equal(0L, (long)(await templateCount.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

                await using var verifyRow = projected.CreateCommand();
                verifyRow.CommandText = $"SELECT first_name, status, amount, birth_date, local_at, optional_value FROM \"{schema}\".participant WHERE id = 1";
                await using var reader = await verifyRow.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("ADA LOVELACE", reader.GetString(0));
                Assert.Equal("ACTIVE", reader.GetString(1));
                Assert.Equal(12345678901234567890.12345678m, reader.GetDecimal(2));
                Assert.Equal(new DateOnly(1990, 2, 3), reader.GetFieldValue<DateOnly>(3));
                Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(4).Kind);
                Assert.True(await reader.IsDBNullAsync(5, TestContext.Current.CancellationToken));
            }

            var shadowDocument = Path.Combine(shadowRoot, succeeded.Id.Value.ToString("N"), "participant-documents", "member", "1", "statement.pdf");
            Assert.Equal(documentBytes, await File.ReadAllBytesAsync(shadowDocument, TestContext.Current.CancellationToken));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(documentBytes)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(shadowDocument, TestContext.Current.CancellationToken))).ToLowerInvariant());
            Assert.True(File.Exists(Path.Combine(sourceRoot, "member", "1", "statement.pdf")));
            Assert.Equal(10, await CountSourceMembersAsync(sourceConnection));

            var checkpointStore = new FileSystemSnapshotStore(Path.Combine(projectRoot, ".proofshift", "checkpoints"));
            var capture = await new SnapshotCaptureService(sourceConnectors, checkpointStore,
                new RuntimeConnectorContextFactory(environment)).CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
            Assert.True(capture.Status == CheckpointStatus.Complete,
                $"Checkpoint capture failed with {capture.FailureCode} after {capture.CapturedSourceNodes}/{capture.ExpectedSourceNodes} source nodes.");
            Assert.NotNull(capture.Checkpoint);
            Assert.Equal(3, capture.CapturedSourceNodes);
            Assert.False(capture.Checkpoint.CrossSystemAtomic);
            Assert.Equal(2, capture.Checkpoint.Endpoints.Count(endpoint => endpoint.SourceConsistency == SourceConsistencyGuarantee.Observed));
            var falseReverseGraph = CreateFalseReverseGraph(graph);
            var falseReverseCapture = await new SnapshotCaptureService(sourceConnectors, checkpointStore,
                new RuntimeConnectorContextFactory(environment)).CaptureAsync(configuration, falseReverseGraph,
                TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, falseReverseCapture.Status);
            Assert.NotNull(falseReverseCapture.Checkpoint);

            await File.WriteAllTextAsync(Path.Combine(csvRoot, "supplemental.csv"), "MEMBER_ID;PAY_PERIOD;NOTE\n1;2025-01;modified-after-capture\n",
                new UTF8Encoding(false), TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "member", "1", "statement.pdf"),
                Encoding.UTF8.GetBytes("modified document after capture\n"), TestContext.Current.CancellationToken);
            await sqlContainer.StopAsync(TestContext.Current.CancellationToken);

            await using var loadedCheckpoint = await checkpointStore.OpenCompleteAsync(
                capture.Id.Value.ToString("N", CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
            var replayProvider = new CheckpointSourceArtifactStreamProvider(loadedCheckpoint);
            var replayService = new ShadowProjectionService(sourceConnectors, targetConnectors,
                new RuntimeConnectorContextFactory(environment), replayProvider);
            var replayed = await replayService.ProjectAsync(configuration, graph, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(ProjectionStatus.Succeeded, replayed.Status);
            Assert.Equal(21, replayed.SourceArtifactCount);
            Assert.Equal(31, replayed.TargetArtifactCount);
            Assert.Equal(succeeded.Fingerprint, replayed.Fingerprint);
            Assert.Equal(capture.Id.Value.ToString("N", CultureInfo.InvariantCulture), replayed.CheckpointId);
            Assert.Equal(capture.Checkpoint.SourceFingerprint, replayed.CheckpointSourceFingerprint);
            Assert.Equal(capture.Checkpoint.ManifestHash, replayed.CheckpointManifestHash);

            var projectionManifestHash = await ProjectionRunManifestStore.WriteAsync(projectRoot, replayed,
                TestContext.Current.CancellationToken);
            var manifest = await ProjectionRunManifestStore.ReadAsync(projectRoot, replayed.Id, TestContext.Current.CancellationToken);
            var binding = new ProjectionVerificationBinding(replayed.Id, replayed.ConfigurationHash, replayed.GraphHash,
                capture.Id, capture.Checkpoint.ManifestHash!, capture.Checkpoint.SourceFingerprint!, replayed.Fingerprint!,
                replayed.FingerprintVersion, replayed.SourceArtifactCount, replayed.TargetArtifactCount, "succeeded", projectionManifestHash,
                replayed.JournalPath, replayed.ConnectorVersions);
            Assert.Equal(projectionManifestHash, manifest.ManifestHash);

            var targetRuntimes = graph.Nodes.Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                .Select(node => new VerificationTargetRuntime(node.Name,
                    targetConnectors.Resolve(configuration.Systems.Single(system => system.Id == node.SystemId)
                        .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId).Connector),
                    new ShadowTargetContext(new RuntimeConnectorContextFactory(environment).Create(configuration, node),
                        replayed.Id, SystemRole.ShadowTarget))).ToArray();
            var ruleRegistry = new VerificationRuleRegistry([new GenericVerificationRuleProvider(), new PensionPack()]);
            var ruleSet = ruleRegistry.Resolve(CreateVerificationRules());
            var verificationService = new VerificationService(checkpointStore);
            var clean = await verificationService.VerifyAsync(configuration, graph, binding, ruleSet, projectRoot,
                Path.Combine(projectRoot, ".proofshift", "temporary"), "0.1.0", targetRuntimes,
                TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, clean.Run.Outcome);
            Assert.Equal(21, clean.Ledger.DispositionCount);
            Assert.Equal(31, clean.Ledger.LineageCount);

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verificationService.VerifyAsync(configuration, graph,
                    binding, ruleSet, projectRoot, Path.Combine(projectRoot, ".proofshift", "temporary"), "0.1.0",
                    targetRuntimes, cancelled.Token));
            }

            var throwingRules = new VerificationRuleRegistry([new ThrowingRuleProvider()]).Resolve(
                [new VerificationRuleDefinition(new RuleId("throwing-test"), "throwing-test", "1", EvidenceSeverity.Error)]);
            var ruleFailure = await Assert.ThrowsAsync<VerificationRuleException>(() => verificationService.VerifyAsync(
                configuration, graph, binding, throwingRules, projectRoot, Path.Combine(projectRoot, ".proofshift", "temporary"),
                "0.1.0", targetRuntimes, TestContext.Current.CancellationToken));
            Assert.Equal(VerificationIssueCodes.RuleExecutionFailure, ruleFailure.Code);

            var verifiedSchema = $"proofshift_shadow_{replayed.Id.Value:N}";
            await using (var mutate = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await mutate.OpenAsync(TestContext.Current.CancellationToken);
                await using var seedDefects = mutate.CreateCommand();
                seedDefects.CommandText = $"""
                    UPDATE "{verifiedSchema}".member_status SET status = 'RETIRED' WHERE id = 1;
                    UPDATE "{verifiedSchema}".participant SET first_name = 'broken name' WHERE id = 3;
                    DELETE FROM "{verifiedSchema}".participant WHERE id = 2;
                    INSERT INTO "{verifiedSchema}".participant VALUES (999, 'UNEXPLAINED', 'ACTIVE', 1, DATE '1990-02-03', TIMESTAMP '2025-01-02 03:04:05', 'synthetic');
                    ALTER TABLE "{verifiedSchema}".participant DROP CONSTRAINT participant_pkey;
                    INSERT INTO "{verifiedSchema}".participant SELECT * FROM "{verifiedSchema}".participant WHERE id = 1;
                    """;
                Assert.Equal(5, await seedDefects.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                await using var statusCheck = mutate.CreateCommand();
                statusCheck.CommandText = $"SELECT status FROM \"{verifiedSchema}\".member_status WHERE id = 1";
                Assert.Equal("RETIRED", (string)(await statusCheck.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            var changed = await verificationService.VerifyAsync(configuration, graph, binding, ruleSet, projectRoot,
                Path.Combine(projectRoot, ".proofshift", "temporary"), "0.1.0", targetRuntimes,
                TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Failed, changed.Run.Outcome);
            Assert.Contains(changed.Findings, finding => finding.Code == "MaterializedTargetChanged");
            Assert.Contains(changed.Findings, finding => finding.Code == "AttributeMismatch" && finding.StableKey.Contains("member-status-attributes", StringComparison.Ordinal));
            Assert.Contains(changed.Findings, finding => finding.Code == "AttributeMismatch" && finding.StableKey.Contains("participant-name-attributes", StringComparison.Ordinal));
            Assert.Contains(changed.Findings, finding => finding.Code == "MissingTarget");
            Assert.Contains(changed.Findings, finding => finding.Code == "UnexpectedTarget");
            Assert.Contains(changed.Findings, finding => finding.Code == "DuplicateTarget");
            Assert.NotEqual(clean.Run.EvidenceFingerprint, changed.Run.EvidenceFingerprint);

            await using (var repair = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await repair.OpenAsync(TestContext.Current.CancellationToken);
                await using var repairTargets = repair.CreateCommand();
                repairTargets.CommandText = $"""
                    UPDATE "{verifiedSchema}".member_status SET status = 'ACTIVE' WHERE id = 1;
                    UPDATE "{verifiedSchema}".participant SET first_name = 'MEMBER 3' WHERE id = 3;
                    DELETE FROM "{verifiedSchema}".participant WHERE id = 999;
                    DELETE FROM "{verifiedSchema}".participant WHERE id = 1;
                    INSERT INTO "{verifiedSchema}".participant VALUES (1, 'ADA LOVELACE', 'ACTIVE', 12345678901234567890.12345678, DATE '1990-02-03', TIMESTAMP '2025-01-02 03:04:05', NULL);
                    INSERT INTO "{verifiedSchema}".participant VALUES (2, 'MEMBER 2', 'ACTIVE', 12345678901234567890.12345678, DATE '1990-02-03', TIMESTAMP '2025-01-02 03:04:05', 'synthetic');
                    ALTER TABLE "{verifiedSchema}".participant ADD CONSTRAINT participant_pkey PRIMARY KEY (id);
                    """;
                Assert.Equal(7, await repairTargets.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            var repaired = await verificationService.VerifyAsync(configuration, graph, binding, ruleSet, projectRoot,
                Path.Combine(projectRoot, ".proofshift", "temporary"), "0.1.0", targetRuntimes,
                TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, repaired.Run.Outcome);
            Assert.Equal(clean.Run.EvidenceFingerprint, repaired.Run.EvidenceFingerprint);
            var changedRuleSet = ruleRegistry.Resolve(CreateVerificationRules(attribute: "amount"));
            Assert.NotEqual(ruleSet.Fingerprint, changedRuleSet.Fingerprint);

            var recoveryService = new RecoveryService(checkpointStore,
                new RecoveryCompensatorRegistry([new ShadowBaselineRestoreCompensator(), new SemanticNameCaseCompensator()]));
            var recovery = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), targetRuntimes, projectRoot, TestContext.Current.CancellationToken);
            Assert.True(recovery.Qualification.Status == DryRunQualificationStatus.Qualified,
                $"Qualification={recovery.Qualification.Status}; assessment={recovery.Assessment.Outcome}; rehearsal={recovery.Rehearsal.Outcome}; issues={string.Join(" | ", recovery.Qualification.Reasons.Select(issue => $"{issue.Code}: {issue.Message}"))}; edges={string.Join(" | ", recovery.Assessment.Edges.Select(edge => $"{edge.EdgeName}={edge.Result}({string.Join(",", edge.Issues.Select(issue => issue.Code))})"))}");
            Assert.Equal(RecoveryRehearsalOutcome.Passed, recovery.Rehearsal.Outcome);
            Assert.Equal(replayed.Fingerprint, recovery.Rehearsal.BaselineFingerprint);
            Assert.NotEqual(recovery.Rehearsal.BaselineFingerprint, recovery.Rehearsal.RestoredFingerprint);
            Assert.Equal(recovery.Rehearsal.BaselineFingerprint, recovery.Rehearsal.ShadowCleanupFingerprint);
            Assert.Equal(2, recovery.Rehearsal.Checkpoints.Count);
            Assert.All(recovery.Rehearsal.Checkpoints, item =>
            {
                Assert.NotEqual(capture.Id.Value, item.Id.Value);
                Assert.Equal("shadow-pension", item.SystemId.Value);
            });
            Assert.Equal(31, recovery.Assessment.Coverage.AffectedArtifacts);
            Assert.Equal(31, recovery.Assessment.Coverage.RecoverableArtifacts);
            Assert.NotEmpty(recovery.Assessment.Coverage.BySemanticType);
            var streamedCoverageCount = 0;
            await foreach (var item in RecoveryService.ReadArtifactCoverageAsync(projectRoot, graph, recovery.Assessment,
                TestContext.Current.CancellationToken).WithCancellation(TestContext.Current.CancellationToken))
            {
                Assert.True(item.Covered);
                Assert.True(item.Recoverable);
                streamedCoverageCount++;
            }
            Assert.Equal(31, streamedCoverageCount);

            var repeatedRecovery = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), targetRuntimes, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(recovery.Assessment.Fingerprint, repeatedRecovery.Assessment.Fingerprint);
            Assert.Equal(recovery.Rehearsal.Fingerprint, repeatedRecovery.Rehearsal.Fingerprint);
            Assert.Equal(recovery.Qualification.DryRunFingerprint, repeatedRecovery.Qualification.DryRunFingerprint);

            var recoveryArtifactStore = new FileSystemRecoveryArtifactStore(Path.Combine(projectRoot, ".proofshift", "recovery"));
            var recoveryReceipt = await recoveryArtifactStore.SaveAsync(recovery, TestContext.Current.CancellationToken);
            Assert.True(await recoveryArtifactStore.VerifyIntegrityAsync(recovery.Id, TestContext.Current.CancellationToken));
            var recoverySummary = await recoveryArtifactStore.ReadSummaryAsync(recovery.Id, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.Qualified, recoverySummary.Status);
            var assessmentArtifact = Path.Combine(projectRoot, ".proofshift", "recovery",
                recovery.Id.Value.ToString("N"), "assessment.json");
            Assert.True(File.Exists(assessmentArtifact));
            Assert.NotEmpty(recoveryReceipt.IntegrityHash);

            var changedPolicy = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(requireRecoveryForDestructiveOperations: true, allowIrreversibleOperations: true,
                    requireValidatedRestore: true, requireRecoveryRehearsal: true, maximumIrreversibleArtifacts: 1),
                targetRuntimes, projectRoot, TestContext.Current.CancellationToken);
            Assert.NotEqual(recovery.Qualification.PolicyFingerprint, changedPolicy.Qualification.PolicyFingerprint);
            Assert.NotEqual(recovery.Assessment.Fingerprint, changedPolicy.Assessment.Fingerprint);
            Assert.NotEqual(recovery.Qualification.DryRunFingerprint, changedPolicy.Qualification.DryRunFingerprint);

            await using (var staleTarget = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await staleTarget.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = staleTarget.CreateCommand();
                update.CommandText = $"UPDATE \"{verifiedSchema}\".member_status SET status = 'RETIRED' WHERE id = 1";
                Assert.Equal(1, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }
            var stale = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), targetRuntimes, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.NotQualified, stale.Qualification.Status);
            Assert.Contains(stale.Qualification.Reasons, issue => issue.Code == QualificationIssueCodes.StaleTarget);
            await using (var repairTarget = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await repairTarget.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = repairTarget.CreateCommand();
                update.CommandText = $"UPDATE \"{verifiedSchema}\".member_status SET status = 'ACTIVE' WHERE id = 1";
                Assert.Equal(1, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            var missingCapabilityTargets = targetRuntimes.Select(target => target.Connector.Id.Value == "files"
                ? new VerificationTargetRuntime(target.NodeKey, new ReadOnlyShadowConnectorProxy(target.Connector), target.Context)
                : target).ToArray();
            var missingCapability = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), missingCapabilityTargets, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.NotQualified, missingCapability.Qualification.Status);
            Assert.Contains(missingCapability.Qualification.Reasons, issue => issue.Code == RecoveryIssueCodes.MissingCapability ||
                issue.Code == RecoveryIssueCodes.RehearsalFailed);

            var corruptCheckpointTargets = targetRuntimes.Select(target => target.Connector.Id.Value == "postgres"
                ? new VerificationTargetRuntime(target.NodeKey,
                    new RecoveryConnectorProxy((IShadowTargetRecoveryConnector)target.Connector, corruptCheckpoint: true), target.Context)
                : target).ToArray();
            var corruptCheckpoint = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), corruptCheckpointTargets, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.NotQualified, corruptCheckpoint.Qualification.Status);
            Assert.Contains(corruptCheckpoint.Qualification.Reasons, issue => issue.Code == RecoveryIssueCodes.CorruptedCheckpoint);

            var failedMutationTargets = targetRuntimes.Select(target => target.Connector.Id.Value == "postgres"
                ? new VerificationTargetRuntime(target.NodeKey,
                    new RecoveryConnectorProxy((IShadowTargetRecoveryConnector)target.Connector, failMutation: true), target.Context)
                : target).ToArray();
            var failedRehearsal = await recoveryService.AssessAndRehearseAsync(configuration, graph, binding, repaired,
                new EffectiveRecoveryPolicy(), failedMutationTargets, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.NotQualified, failedRehearsal.Qualification.Status);
            Assert.Equal(RecoveryRehearsalOutcome.Failed, failedRehearsal.Rehearsal.Outcome);
            var postFailureVerification = await verificationService.VerifyAsync(configuration, graph, binding, ruleSet,
                projectRoot, Path.Combine(projectRoot, ".proofshift", "temporary"), "0.1.0", targetRuntimes,
                TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, postFailureVerification.Run.Outcome);
            Assert.Equal(repaired.Run.EvidenceFingerprint, postFailureVerification.Run.EvidenceFingerprint);

            await using var falseReverseCheckpoint = await checkpointStore.OpenCompleteAsync(
                falseReverseCapture.Id.Value.ToString("N", CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
            var falseReverseProjection = await new ShadowProjectionService(sourceConnectors, targetConnectors,
                new RuntimeConnectorContextFactory(environment),
                new CheckpointSourceArtifactStreamProvider(falseReverseCheckpoint)).ProjectAsync(configuration,
                falseReverseGraph, projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(ProjectionStatus.Succeeded, falseReverseProjection.Status);
            await ProjectionRunManifestStore.WriteAsync(projectRoot, falseReverseProjection, TestContext.Current.CancellationToken);
            var falseReverseManifest = await ProjectionRunManifestStore.ReadAsync(projectRoot, falseReverseProjection.Id,
                TestContext.Current.CancellationToken);
            var falseReverseBinding = new ProjectionVerificationBinding(falseReverseProjection.Id,
                falseReverseProjection.ConfigurationHash, falseReverseProjection.GraphHash, falseReverseCapture.Id,
                falseReverseCapture.Checkpoint!.ManifestHash!, falseReverseCapture.Checkpoint.SourceFingerprint!,
                falseReverseProjection.Fingerprint!, falseReverseProjection.FingerprintVersion,
                falseReverseProjection.SourceArtifactCount, falseReverseProjection.TargetArtifactCount, "succeeded",
                falseReverseManifest.ManifestHash, falseReverseProjection.JournalPath, falseReverseProjection.ConnectorVersions);
            var falseReverseTargets = falseReverseGraph.Nodes
                .Where(node => node.Type is MigrationNodeType.Target or MigrationNodeType.Archive)
                .Select(node => new VerificationTargetRuntime(node.Name,
                    targetConnectors.Resolve(configuration.Systems.Single(system => system.Id == node.SystemId)
                        .StorageEndpoints.Single(endpoint => endpoint.Id == node.EndpointId).Connector),
                    new ShadowTargetContext(new RuntimeConnectorContextFactory(environment).Create(configuration, node),
                        falseReverseProjection.Id, SystemRole.ShadowTarget))).ToArray();
            var falseReverseVerification = await verificationService.VerifyAsync(configuration, falseReverseGraph,
                falseReverseBinding, ruleSet, projectRoot, Path.Combine(projectRoot, ".proofshift", "temporary"),
                "0.1.0", falseReverseTargets, TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, falseReverseVerification.Run.Outcome);
            var falseReverseResult = await recoveryService.AssessAndRehearseAsync(configuration, falseReverseGraph,
                falseReverseBinding, falseReverseVerification, new EffectiveRecoveryPolicy(), falseReverseTargets,
                projectRoot, TestContext.Current.CancellationToken);
            Assert.Equal(DryRunQualificationStatus.NotQualified, falseReverseResult.Qualification.Status);
            foreach (var declaration in PensionDefectInjector.FalseReverseDeclarations)
                Assert.Contains(falseReverseResult.Assessment.Edges, edge => edge.EdgeName == declaration.EdgeName &&
                    edge.Issues.Any(issue => issue.Code == RecoveryIssueCodes.InvalidReverse));

            await File.AppendAllTextAsync(assessmentArtifact, "tampered", TestContext.Current.CancellationToken);
            Assert.False(await recoveryArtifactStore.VerifyIntegrityAsync(recovery.Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(csvRoot, recursive: true);
            Directory.Delete(shadowRoot, recursive: true);
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static IReadOnlyCollection<VerificationRuleDefinition> CreateVerificationRules(string attribute = "status") =>
    [
        new(new RuleId("source-accounting"), "source-artifact-accounting", "1", EvidenceSeverity.Error),
        new(new RuleId("target-lineage"), "target-lineage", "1", EvidenceSeverity.Error),
        new(new RuleId("target-presence"), "target-presence", "1", EvidenceSeverity.Error),
        new(new RuleId("unexpected-targets"), "unexpected-target", "1", EvidenceSeverity.Error),
        new(new RuleId("pension-member-accounting"), "pension-member-accounting", "1", EvidenceSeverity.Error,
            [new KeyValuePair<string, string>("semanticType", "Pension.Member")]),
        new(new RuleId("participant-uniqueness"), "pension-member-uniqueness", "1", EvidenceSeverity.Error,
            [new KeyValuePair<string, string>("semanticType", "Pension.Member"), new KeyValuePair<string, string>("targetNode", "participant")]),
        new(new RuleId("member-status-attributes"), "pension-member-attribute", "1", EvidenceSeverity.Error,
            [new KeyValuePair<string, string>("semanticType", "Pension.Member"), new KeyValuePair<string, string>("targetNode", "member-status"),
             new KeyValuePair<string, string>("attribute", attribute)]),
        new(new RuleId("participant-name-attributes"), "attribute-comparison", "1", EvidenceSeverity.Error,
            [new KeyValuePair<string, string>("semanticType", "Pension.Member"), new KeyValuePair<string, string>("targetNode", "participant"),
             new KeyValuePair<string, string>("attribute", "first_name")])
    ];

    private sealed class ThrowingRuleProvider : IVerificationRuleProvider
    {
        public string Id => "test.throwing";
        public string Version => "1";
        public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
            [new("throwing-test", definition => new ThrowingRule(definition.Id, definition.Version))];
    }

    private sealed class SemanticNameCaseCompensator : IRecoveryCompensator
    {
        public string StrategyId => "semantic-member-name-case";
        public string Version => "1";
        public RecoveryValidationMode ValidationMode => RecoveryValidationMode.SemanticCompensation;

        public RecoveryCheckResult Validate(MigrationEdge edge) =>
            edge.Recovery?.Strategy == StrategyId ? RecoveryCheckResult.Pass : RecoveryCheckResult.Fail;

        public async Task ExecuteShadowRehearsalAsync(IReadOnlyCollection<RecoveryShadowCheckpointBinding> checkpoints,
            CancellationToken cancellationToken)
        {
            foreach (var checkpoint in checkpoints)
                await checkpoint.Connector.RestoreRecoveryCheckpointAsync(checkpoint.Request, checkpoint.Checkpoint, cancellationToken);
            var postgres = checkpoints.Single(checkpoint => checkpoint.Request.ConnectorId.Value == "postgres");
            var context = postgres.Request.Targets.Single(target => target.Context.ConnectorContext.NodeKey == "participant").Context;
            var connectionString = context.ConnectorContext.Configuration.GetRequired("connection").UseValue(value => value);
            var schema = $"proofshift_shadow_{context.RunId.Value:N}";
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE \"{schema}\".participant SET first_name = lower(first_name) WHERE id = 1; UPDATE \"{schema}\".member_status SET first_name = lower(first_name) WHERE id = 1";
            Assert.Equal(2, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        public async Task<RecoveryCheckResult> ValidateShadowOutcomeAsync(MigrationEdge edge,
            RecoveryCompensationValidationContext context, CancellationToken cancellationToken)
        {
            if (edge.Recovery?.Strategy != StrategyId || context.BaselineFingerprint == context.RestoredFingerprint ||
                context.BaselineArtifactCount != context.RestoredArtifactCount) return RecoveryCheckResult.Fail;
            var postgres = context.Checkpoints.Single(checkpoint => checkpoint.Request.ConnectorId.Value == "postgres");
            var target = postgres.Request.Targets.Single(item => item.Context.ConnectorContext.NodeKey == "participant");
            var connectionString = target.Context.ConnectorContext.Configuration.GetRequired("connection").UseValue(value => value);
            var schema = $"proofshift_shadow_{target.Context.RunId.Value:N}";
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT (SELECT COUNT(*) FROM \"{schema}\".participant WHERE id = 1 AND lower(first_name) = 'ada lovelace') + (SELECT COUNT(*) FROM \"{schema}\".member_status WHERE id = 1 AND lower(first_name) = 'ada lovelace')";
            return (long)(await command.ExecuteScalarAsync(cancellationToken))! == 2
                ? RecoveryCheckResult.Pass : RecoveryCheckResult.Fail;
        }
    }

    private class ShadowConnectorProxy(IShadowTargetConnector inner) : IShadowTargetConnector
    {
        protected IShadowTargetConnector Inner { get; } = inner;
        public ConnectorId Id => Inner.Id;
        public string Version => Inner.Version;
        public Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken) =>
            Inner.PrepareAsync(context, selector, cancellationToken);
        public Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken) =>
            Inner.WriteAsync(request, cancellationToken);
        public Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken) =>
            Inner.CompleteAsync(context, cancellationToken);
        public IAsyncEnumerable<RecordEnvelope> ReadAsync(ReadRequest request, CancellationToken cancellationToken) =>
            Inner.ReadAsync(request, cancellationToken);
    }

    private sealed class ReadOnlyShadowConnectorProxy(IShadowTargetConnector inner) : ShadowConnectorProxy(inner);

    private sealed class RecoveryConnectorProxy(IShadowTargetRecoveryConnector inner,
        bool corruptCheckpoint = false, bool failMutation = false) : ShadowConnectorProxy(inner), IShadowTargetRecoveryConnector
    {
        private IShadowTargetRecoveryConnector RecoveryInner { get; } = inner;

        public async Task<ShadowRecoveryCheckpoint> CaptureRecoveryCheckpointAsync(ShadowRecoveryRequest request,
            CancellationToken cancellationToken)
        {
            var checkpoint = await RecoveryInner.CaptureRecoveryCheckpointAsync(request, cancellationToken);
            return corruptCheckpoint
                ? new ShadowRecoveryCheckpoint(checkpoint.Id, checkpoint.ShadowRunId, checkpoint.SystemId,
                    checkpoint.EndpointId, checkpoint.ConnectorId, checkpoint.ConnectorVersion, checkpoint.GraphHash,
                    checkpoint.BaselineFingerprint, checkpoint.TargetArtifactCount, checkpoint.Reference,
                    new string('0', 64), checkpoint.CapturedAt)
                : checkpoint;
        }

        public Task ValidateRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
            CancellationToken cancellationToken) => RecoveryInner.ValidateRecoveryCheckpointAsync(request, checkpoint, cancellationToken);

        public async Task<ShadowRecoveryMutation> ApplyControlledMutationAsync(ShadowRecoveryRequest request,
            CancellationToken cancellationToken)
        {
            var mutation = await RecoveryInner.ApplyControlledMutationAsync(request, cancellationToken);
            return failMutation ? new ShadowRecoveryMutation(0, "Injected deterministic rehearsal failure.") : mutation;
        }

        public Task RestoreRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
            CancellationToken cancellationToken) => RecoveryInner.RestoreRecoveryCheckpointAsync(request, checkpoint, cancellationToken);
    }

    private sealed class ThrowingRule(RuleId id, string version) : ProofShift.Verification.IVerificationRule
    {
        public RuleId Id => id;
        public string Version => version;
        public VerificationScope Scope => VerificationScope.Entity;

        public async IAsyncEnumerable<VerificationFinding> EvaluateAsync(VerificationExecutionContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            await Task.FromException(new InvalidOperationException("synthetic rule failure"));
            yield break;
        }
    }

    [Fact]
    public async Task CliValidatesInspectsAndProjectsTheSyntheticPensionFixture()
    {
        var sqlContainer = await StartSqlServerContainerAsync();
        await using var sqlCleanup = sqlContainer;
        var postgresContainer = await StartPostgresContainerAsync();
        await using var postgresCleanup = postgresContainer;
        var shadowRoot = CreateTemporaryDirectory();
        var sqlBuilder = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        };
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "scenarios", "pension-modernization", "ps05");
        var fixture = Path.Combine(fixtureRoot, "proofshift.yaml");
        var cli = Path.Combine(AppContext.BaseDirectory, "ProofShift.Cli.dll");
        var sourceRoot = Path.Combine(fixtureRoot, "sources");
        var csvPath = Path.Combine(sourceRoot, "supplemental.csv");
        var documentPath = Path.Combine(sourceRoot, "member", "1", "statement.pdf");
        byte[]? originalCsv = null;
        byte[]? originalDocument = null;
        try
        {
            Assert.True(File.Exists(cli), "The CLI assembly should be copied to the EndToEnd test output.");
            await SetupSourceAndTargetAsync(sqlBuilder.ConnectionString, postgresContainer.GetConnectionString());
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PS05_SQL_CONNECTION"] = sqlBuilder.ConnectionString,
                ["PS05_POSTGRES_CONNECTION"] = postgresContainer.GetConnectionString(),
                ["PS05_SOURCE_FILES"] = sourceRoot,
                ["PS05_SOURCE_CSV"] = sourceRoot,
                ["PS05_SHADOW_FILES"] = shadowRoot
            };

            var validate = await RunCliAsync(cli, fixture, environment, "validate");
            Assert.Equal(0, validate.ExitCode);
            Assert.Contains("VALID", validate.StandardOutput, StringComparison.Ordinal);
            var plan = await RunCliAsync(cli, fixture, environment, "plan");
            Assert.Equal(0, plan.ExitCode);
            Assert.Contains("VALID", plan.StandardOutput, StringComparison.Ordinal);
            var inspect = await RunCliAsync(cli, fixture, environment, "inspect");
            Assert.Equal(0, inspect.ExitCode);
            Assert.Contains("source-members", inspect.StandardOutput, StringComparison.Ordinal);

            var failed = await RunCliAsync(cli, fixture, environment, "project");
            Assert.Equal(1, failed.ExitCode);
            Assert.Contains("RESULT: FAILED", failed.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("PSPROJ_CODE_MAP", failed.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(sqlBuilder.ConnectionString, failed.StandardOutput, StringComparison.Ordinal);

            await using (var source = new SqlConnection(sqlBuilder.ConnectionString))
            {
                await source.OpenAsync(TestContext.Current.CancellationToken);
                await using var update = source.CreateCommand();
                update.CommandText = "UPDATE dbo.MEMBER SET MEMBER_STATUS = @status WHERE MEMBER_ID = @id";
                update.Parameters.AddWithValue("status", "A");
                update.Parameters.AddWithValue("id", 10);
                await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var succeeded = await RunCliAsync(cli, fixture, environment, "project", "--json");
            Assert.True(succeeded.ExitCode == 0,
                $"Successful rerun failed with exit code {succeeded.ExitCode}. stdout: {succeeded.StandardOutput} stderr: {succeeded.StandardError}");
            Assert.DoesNotContain(sqlBuilder.ConnectionString, succeeded.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(postgresContainer.GetConnectionString(), succeeded.StandardOutput, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(succeeded.StandardOutput);
            var root = json.RootElement;
            Assert.Equal("projection", root.GetProperty("runType").GetString());
            Assert.Equal("succeeded", root.GetProperty("status").GetString());
            Assert.Equal(21, root.GetProperty("sourceArtifactCount").GetInt64());
            Assert.Equal(31, root.GetProperty("targetArtifactCount").GetInt64());
            Assert.Equal("proofshift-projection-fingerprint-v1", root.GetProperty("projectionFingerprintVersion").GetString());
            var liveProjectionFingerprint = root.GetProperty("projectionFingerprint").GetString();
            var runId = Guid.Parse(root.GetProperty("runId").GetString()!);
            var schema = $"proofshift_shadow_{runId:N}";
            await using (var projected = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await projected.OpenAsync(TestContext.Current.CancellationToken);
                Assert.Equal(10L, await CountAsync(projected, schema, "participant"));
                Assert.Equal(10L, await CountAsync(projected, schema, "member_status"));
                Assert.Equal(10L, await CountAsync(projected, schema, "supplemental_member"));
                await using var supplementalValue = projected.CreateCommand();
                supplementalValue.CommandText = $"SELECT member_id FROM \"{schema}\".supplemental_member WHERE pay_period = '2025-01' ORDER BY member_id LIMIT 1";
                Assert.Equal("1", (string)(await supplementalValue.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
                await using var productionTable = projected.CreateCommand();
                productionTable.CommandText = "SELECT COUNT(*) FROM public.participant";
                Assert.Equal(0L, (long)(await productionTable.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            var expectedDocument = await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "member", "1", "statement.pdf"),
                TestContext.Current.CancellationToken);
            var projectedDocument = Path.Combine(shadowRoot, runId.ToString("N"), "participant-documents", "member", "1", "statement.pdf");
            Assert.Equal(expectedDocument, await File.ReadAllBytesAsync(projectedDocument, TestContext.Current.CancellationToken));

            originalCsv = await File.ReadAllBytesAsync(csvPath, TestContext.Current.CancellationToken);
            originalDocument = await File.ReadAllBytesAsync(documentPath, TestContext.Current.CancellationToken);
            var dryRun = await RunCliAsync(cli, fixture, environment, "dry-run", "--json");
            Assert.True(dryRun.ExitCode == 0,
                $"Dry-run CLI failed with exit code {dryRun.ExitCode}. stdout: {dryRun.StandardOutput} stderr: {dryRun.StandardError}");
            Assert.DoesNotContain(sqlBuilder.ConnectionString, dryRun.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(postgresContainer.GetConnectionString(), dryRun.StandardOutput, StringComparison.Ordinal);
            using var dryRunJson = JsonDocument.Parse(dryRun.StandardOutput);
            Assert.Equal("qualified", dryRunJson.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("succeeded", dryRunJson.RootElement.GetProperty("projectionStatus").GetString());
            Assert.Equal("passed", dryRunJson.RootElement.GetProperty("verificationOutcome").GetString());
            Assert.Equal("passed", dryRunJson.RootElement.GetProperty("rehearsalOutcome").GetString());
            Assert.Equal(31, dryRunJson.RootElement.GetProperty("affectedArtifacts").GetInt64());
            var dryRunId = dryRunJson.RootElement.GetProperty("dryRunId").GetString();
            var recoveryReport = await RunCliAsync(cli, fixture, environment, "recovery", "--run", dryRunId!, "--json");
            Assert.Equal(0, recoveryReport.ExitCode);
            using var recoveryReportJson = JsonDocument.Parse(recoveryReport.StandardOutput);
            Assert.Equal("qualified", recoveryReportJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(dryRunJson.RootElement.GetProperty("dryRunFingerprint").GetString(),
                recoveryReportJson.RootElement.GetProperty("dryRunFingerprint").GetString());

            var pensionReport = await RunCliAsync(cli, fixture, environment, "report", "--run", dryRunId!, "--json");
            Assert.Equal(0, pensionReport.ExitCode);
            Assert.DoesNotContain(sqlBuilder.ConnectionString, pensionReport.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(postgresContainer.GetConnectionString(), pensionReport.StandardOutput, StringComparison.Ordinal);
            using var pensionReportJson = JsonDocument.Parse(pensionReport.StandardOutput);
            Assert.Equal("proofshift-pension-assurance-report-v1", pensionReportJson.RootElement.GetProperty("format").GetString());
            Assert.Equal("QUALIFIED", pensionReportJson.RootElement.GetProperty("qualification").GetString());
            Assert.Equal(0, pensionReportJson.RootElement.GetProperty("businessDiscrepancyCount").GetInt64());
            var defectCounts = pensionReportJson.RootElement.GetProperty("defectCounts");
            Assert.Equal(18, defectCounts.EnumerateObject().Count());
            Assert.All(defectCounts.EnumerateObject(), category => Assert.Equal(0, category.Value.GetInt64()));
            Assert.Equal(dryRunJson.RootElement.GetProperty("verificationEvidenceFingerprint").GetString(),
                pensionReportJson.RootElement.GetProperty("verificationEvidenceFingerprint").GetString());
            Assert.Empty(pensionReportJson.RootElement.GetProperty("exceptions").EnumerateArray());

            var sameRunComparison = await RunCliAsync(cli, fixture, environment, "compare",
                "--before", dryRunId!, "--after", dryRunId!, "--json");
            Assert.Equal(0, sameRunComparison.ExitCode);
            using var comparisonJson = JsonDocument.Parse(sameRunComparison.StandardOutput);
            Assert.False(comparisonJson.RootElement.GetProperty("evidenceFingerprintChanged").GetBoolean());
            Assert.Empty(comparisonJson.RootElement.GetProperty("defectsResolved").EnumerateObject());
            Assert.Empty(comparisonJson.RootElement.GetProperty("defectsIntroduced").EnumerateObject());

            var snapshot = await RunCliAsync(cli, fixture, environment, "snapshot", "--json");
            Assert.Equal(0, snapshot.ExitCode);
            using var snapshotJson = JsonDocument.Parse(snapshot.StandardOutput);
            var checkpoint = snapshotJson.RootElement;
            Assert.Equal("complete", checkpoint.GetProperty("status").GetString());
            Assert.False(checkpoint.GetProperty("crossSystemAtomic").GetBoolean());
            var checkpointId = checkpoint.GetProperty("checkpointId").GetString();

            await File.WriteAllTextAsync(csvPath, "MEMBER_ID;PAY_PERIOD;NOTE\n1;2025-01;modified-after-capture\n",
                new UTF8Encoding(false), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(documentPath, "modified after checkpoint\n", TestContext.Current.CancellationToken);
            await sqlContainer.StopAsync(TestContext.Current.CancellationToken);

            var replay = await RunCliAsync(cli, fixture, environment, "project", "--checkpoint", checkpointId!, "--json");
            Assert.True(replay.ExitCode == 0,
                $"Checkpoint CLI replay failed with exit code {replay.ExitCode}. stdout: {replay.StandardOutput} stderr: {replay.StandardError}");
            using var replayJson = JsonDocument.Parse(replay.StandardOutput);
            Assert.Equal("succeeded", replayJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(liveProjectionFingerprint, replayJson.RootElement.GetProperty("projectionFingerprint").GetString());
            Assert.Equal(checkpointId, replayJson.RootElement.GetProperty("checkpointId").GetString());

            var projectionRunId = replayJson.RootElement.GetProperty("runId").GetString();
            var verification = await RunCliAsync(cli, fixture, environment, "verify", "--checkpoint", checkpointId!,
                "--projection", projectionRunId!, "--json");
            Assert.True(verification.ExitCode == 0,
                $"Verification CLI failed with exit code {verification.ExitCode}. stdout: {verification.StandardOutput} stderr: {verification.StandardError}");
            Assert.DoesNotContain(sqlBuilder.ConnectionString, verification.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(postgresContainer.GetConnectionString(), verification.StandardOutput, StringComparison.Ordinal);
            using var verificationJson = JsonDocument.Parse(verification.StandardOutput);
            Assert.Equal("passed", verificationJson.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(9, verificationJson.RootElement.GetProperty("findings").GetInt32());
            var verificationRunId = verificationJson.RootElement.GetProperty("runId").GetString();
            var evidence = await RunCliAsync(cli, fixture, environment, "evidence", "--run", verificationRunId!, "--json");
            Assert.Equal(0, evidence.ExitCode);
            Assert.Contains("proofshift-evidence-store-v2", evidence.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (originalCsv is not null) File.WriteAllBytes(csvPath, originalCsv);
            if (originalDocument is not null) File.WriteAllBytes(documentPath, originalDocument);
            var generatedState = Path.Combine(fixtureRoot, ".proofshift");
            if (Directory.Exists(generatedState)) Directory.Delete(generatedState, recursive: true);
            Directory.Delete(shadowRoot, recursive: true);
        }
    }

    private static async Task<CliResult> RunCliAsync(string cliAssembly, string configuration, IReadOnlyDictionary<string, string> environment,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cliAssembly);
        start.ArgumentList.Add(arguments[0]);
        start.ArgumentList.Add(configuration);
        foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ProofShift CLI process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private static LoadedProjectConfiguration CreateConfiguration()
    {
        var source = new SystemDefinition(new SystemId("legacy-pension"), "Synthetic Legacy Pension", SystemRole.Source,
        [
            Endpoint("member-database", "sqlserver", ("connection", "secret:PS05_SQL_CONNECTION"),
                ("checkpoint.consistency", "transaction-consistent"), ("checkpoint.isolation", "serializable")),
            Endpoint("member-documents", "files", ("root", "env:PS05_SOURCE_FILES")),
            Endpoint("supplemental-members", "csv", ("root", "env:PS05_SOURCE_CSV"))
        ]);
        var shadow = new SystemDefinition(new SystemId("shadow-pension"), "Synthetic Shadow Pension", SystemRole.ShadowTarget,
        [
            Endpoint("pension-database", "postgres", ("connection", "secret:PS05_POSTGRES_CONNECTION")),
            Endpoint("pension-documents", "files", ("root", "env:PS05_SHADOW_FILES"))
        ]);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("synthetic-pension-ps05", "Synthetic Pension PS-0.5"), null, [], null, null, null);
        var systemDtos = new[] { source, shadow }.Select(system => new SystemConfigurationDto(
            system.Id.Value,
            system.Name,
            system.Role switch { SystemRole.ShadowTarget => "shadow-target", SystemRole.Source => "source", _ => "target" },
            system.StorageEndpoints.Select(endpoint => new StorageEndpointConfigurationDto(
                endpoint.Id.Value,
                endpoint.Connector.Value,
                endpoint.Configuration.Select(pair => new KeyValuePair<string, ConfigurationSetting>(
                    pair.Key,
                    new LiteralConfigurationSetting(pair.Value)))))));
        return new LoadedProjectConfiguration(root, systemDtos, [source, shadow], [], "synthetic-canonical-configuration-v1",
            new string('a', 64));
    }

    private static MigrationGraph CreateGraph()
    {
        var sourceSystem = new SystemId("legacy-pension");
        var targetSystem = new SystemId("shadow-pension");
        var memberSource = Node("source-members", MigrationNodeType.Source, sourceSystem, "member-database", "table", "dbo.MEMBER", ["MEMBER_ID"]);
        var csvSource = Node("source-csv", MigrationNodeType.Source, sourceSystem, "supplemental-members", "csv", "supplemental.csv", ["MEMBER_ID", "PAY_PERIOD"],
            ("delimiter", ";"));
        var documentSource = Node("source-documents", MigrationNodeType.Source, sourceSystem, "member-documents", "file-pattern", "member/**/*.pdf", ["relativePath"]);
        var participant = Node("participant", MigrationNodeType.Target, targetSystem, "pension-database", "table", "public.participant", ["id"]);
        var status = Node("member-status", MigrationNodeType.Target, targetSystem, "pension-database", "table", "public.member_status", ["id"]);
        var supplemental = Node("supplemental-member", MigrationNodeType.Target, targetSystem, "pension-database", "table", "public.supplemental_member", ["member_id", "pay_period"]);
        var documents = Node("participant-documents", MigrationNodeType.Archive, targetSystem, "pension-documents", "file-pattern", "ignored", ["relativePath"],
            ("pathField", "relativePath"));
        var memberFields = new[]
        {
            new TransformationFieldDefinition("id", "MEMBER_ID"),
            new TransformationFieldDefinition("first_name", "FIRST_NM", [new TransformationStep(TransformationStepType.Trim, "1"), new TransformationStep(TransformationStepType.NormalizeString, "1")]),
            new TransformationFieldDefinition("status", "MEMBER_STATUS", [new TransformationStep(TransformationStepType.CodeMap, "1", [new KeyValuePair<string, string>("A", "ACTIVE"), new KeyValuePair<string, string>("R", "RETIRED")])]),
            new TransformationFieldDefinition("amount", "AMOUNT"),
            new TransformationFieldDefinition("birth_date", "BIRTH_DATE"),
            new TransformationFieldDefinition("local_at", "LOCAL_AT"),
            new TransformationFieldDefinition("optional_value", "OPTIONAL_VALUE")
        };
        var edges = new[]
        {
            Edge("member-split", [memberSource], [participant, status], MigrationOperationType.Split, memberFields,
                RecoveryMode.Compensate, "semantic-member-name-case"),
            Edge("supplemental-map", [csvSource], [supplemental], MigrationOperationType.Map,
            [
                new TransformationFieldDefinition("member_id", "MEMBER_ID"),
                new TransformationFieldDefinition("pay_period", "PAY_PERIOD"),
                new TransformationFieldDefinition("note", "NOTE")
            ]),
            Edge("document-archive", [documentSource], [documents], MigrationOperationType.Archive, [])
        };
        return new MigrationGraph(new MigrationGraphId(Guid.Parse("6e514548-8244-4dd0-9d91-4c93e975c3c5")),
            [memberSource, csvSource, documentSource, participant, status, supplemental, documents], edges,
            new string('d', 64), "test-v1");
    }

    private static MigrationGraph CreateFalseReverseGraph(MigrationGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Name, StringComparer.Ordinal);
        var edges = new List<MigrationEdge>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Name != "member-split")
            {
                edges.Add(edge);
                continue;
            }

            var participant = nodes["participant"];
            var memberStatus = nodes["member-status"];
            var originalFields = edge.Operation.Fields.ToArray();
            var nameDeclaration = PensionDefectInjector.FalseReverseDeclarations[0];
            edges.Add(new MigrationEdge(edge.Id, nameDeclaration.EdgeName, edge.Sources, [participant.Id],
                new MigrationOperation(MigrationOperationType.Transform, fields: originalFields), edge.Version,
                new RecoveryDefinition(RecoveryMode.Reverse)));

            var statusFields = originalFields.Select(field => field.Target switch
            {
                "first_name" => new TransformationFieldDefinition(field.Target, field.Source),
                "status" => new TransformationFieldDefinition(field.Target, field.Source,
                    [new TransformationStep(TransformationStepType.CodeMap, "1",
                        [new KeyValuePair<string, string>("A", "ACTIVE"), new KeyValuePair<string, string>("R", "RETIRED"),
                         new KeyValuePair<string, string>("unknown", "pass-through")])]),
                _ => field
            }).ToArray();
            var statusDeclaration = PensionDefectInjector.FalseReverseDeclarations[1];
            var statusEdgeId = new MigrationEdgeId(new Guid(SHA256.HashData(
                Encoding.UTF8.GetBytes($"proofshift-pension-defect-v1:{statusDeclaration.EdgeName}")).AsSpan(0, 16)));
            edges.Add(new MigrationEdge(statusEdgeId, statusDeclaration.EdgeName, edge.Sources, [memberStatus.Id],
                new MigrationOperation(MigrationOperationType.Transform, fields: statusFields), edge.Version,
                new RecoveryDefinition(RecoveryMode.Reverse)));
        }
        var externalNodeKeys = graph.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var canonical = GraphCanonicalizer.Canonicalize(1, graph.Nodes, edges, externalNodeKeys);
        var graphHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new MigrationGraph(graph.Id, graph.Nodes, edges, graphHash, GraphCanonicalizer.FormatVersion);
    }

    private static MigrationNode Node(string name, MigrationNodeType type, SystemId system, string endpoint, string kind,
        string nameProperty, IEnumerable<string> identity, params (string Key, string Value)[] extraProperties)
    {
        var properties = new List<KeyValuePair<string, string>> { new("name", nameProperty) };
        properties.AddRange(extraProperties.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));
        if (kind == "csv")
        {
            properties[0] = new KeyValuePair<string, string>("path", nameProperty);
        }
        else if (kind == "file-pattern")
        {
            properties[0] = new KeyValuePair<string, string>("pattern", nameProperty);
        }

        return new MigrationNode(new MigrationNodeId(Guid.NewGuid()), name, type, "Pension.Member", system,
            new StorageEndpointId(endpoint), new ArtifactSelector(kind, properties, identity));
    }

    private static MigrationEdge Edge(string name, IEnumerable<MigrationNode> sources, IEnumerable<MigrationNode> targets,
        MigrationOperationType operation, IEnumerable<TransformationFieldDefinition> fields,
        RecoveryMode recoveryMode = RecoveryMode.Restore, string? recoveryStrategy = "restore-shadow-baseline") =>
        new(new MigrationEdgeId(Guid.NewGuid()), name, sources.Select(node => node.Id), targets.Select(node => node.Id),
            new MigrationOperation(operation, fields: fields, isDestructive: operation == MigrationOperationType.Exclude), "1",
            new RecoveryDefinition(recoveryMode, recoveryStrategy,
                requiresSnapshot: recoveryMode == RecoveryMode.Restore));

    private static StorageEndpointDefinition Endpoint(string name, string connector, params (string Key, string Value)[] settings) =>
        new(new StorageEndpointId(name), new ConnectorId(connector), settings.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

    private static async Task SetupSourceAndTargetAsync(string sqlConnectionString, string postgresConnectionString)
    {
        await using (var source = new SqlConnection(sqlConnectionString))
        {
            await source.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = source.CreateCommand();
            command.CommandText = """
                CREATE TABLE dbo.MEMBER (
                    MEMBER_ID int NOT NULL PRIMARY KEY,
                    FIRST_NM nvarchar(100) NOT NULL,
                    MEMBER_STATUS char(1) NOT NULL,
                    AMOUNT decimal(28, 8) NOT NULL,
                    BIRTH_DATE date NOT NULL,
                    LOCAL_AT datetime2(7) NOT NULL,
                    OPTIONAL_VALUE nvarchar(50) NULL
                );
                WITH numbers AS (
                    SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS id
                    FROM sys.all_objects
                )
                INSERT INTO dbo.MEMBER (MEMBER_ID, FIRST_NM, MEMBER_STATUS, AMOUNT, BIRTH_DATE, LOCAL_AT, OPTIONAL_VALUE)
                SELECT id,
                       CASE WHEN id = 1 THEN N'  ada lovelace  ' ELSE CONCAT(N'Member ', id) END,
                       CASE WHEN id = 10 THEN 'X' ELSE 'A' END,
                       12345678901234567890.12345678,
                       '1990-02-03',
                       '2025-01-02T03:04:05',
                       CASE WHEN id = 1 THEN NULL ELSE N'synthetic' END
                FROM numbers;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var target = new NpgsqlConnection(postgresConnectionString);
        await target.OpenAsync(TestContext.Current.CancellationToken);
        await using var targetCommand = target.CreateCommand();
        targetCommand.CommandText = """
            CREATE TABLE public.participant (
                id integer NOT NULL PRIMARY KEY,
                first_name text NOT NULL,
                status text NOT NULL,
                amount numeric(28, 8) NOT NULL,
                birth_date date NOT NULL,
                local_at timestamp without time zone NOT NULL,
                optional_value text NULL
            );
            CREATE TABLE public.member_status (
                id integer NOT NULL PRIMARY KEY,
                first_name text NOT NULL,
                status text NOT NULL,
                amount numeric(28, 8) NOT NULL,
                birth_date date NOT NULL,
                local_at timestamp without time zone NOT NULL,
                optional_value text NULL
            );
            CREATE TABLE public.supplemental_member (
                member_id text NOT NULL,
                pay_period text NOT NULL,
                note text NOT NULL,
                PRIMARY KEY (member_id, pay_period)
            );
            """;
        await targetCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string schema, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table";
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        command.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{table}\"";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<int> CountSourceMembersAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.MEMBER";
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static ConnectorContext RelationalContext(string connector, string system, string endpoint, string node,
        string setting, string value) => new(system, endpoint, new ConnectorId(connector), node, "Pension.Member",
        new RuntimeConfiguration([new KeyValuePair<string, RuntimeSetting>(setting, RuntimeSetting.FromRuntimeValue(value, isSecret: true))]));

    private static async Task<MsSqlContainer> StartSqlServerContainerAsync()
    {
        MsSqlContainer? container = null;
        try
        {
            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").WithPassword(Password).Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            return container;
        }
        catch (DockerUnavailableException)
        {
            if (container is not null) await container.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for the PS-0.5 projection integration test.");
        }
    }

    private static async Task<PostgreSqlContainer> StartPostgresContainerAsync()
    {
        PostgreSqlContainer? container = null;
        try
        {
            container = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("proofshift_ps05")
                .WithUsername("proofshift_test").WithPassword(Password).Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            return container;
        }
        catch (DockerUnavailableException)
        {
            if (container is not null) await container.DisposeAsync();
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for the PS-0.5 projection integration test.");
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-ps05-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MapEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}