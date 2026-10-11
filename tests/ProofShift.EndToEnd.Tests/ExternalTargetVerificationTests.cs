using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Npgsql;
using ProofShift.Connectors.SqlServer;
using Testcontainers.PostgreSql;
using Testcontainers.MsSql;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Packs.Pension;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class ExternalTargetVerificationTests
{
    [Fact]
    public async Task ExternalVerificationObservesOrdinaryTargetWithSelectOnlyDatabasePrivileges()
    {
        await using var sourceContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await sourceContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var targetContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await targetContainer.StartAsync(TestContext.Current.CancellationToken);
        var sourceSystem = new SystemDefinition(new SystemId("legacy"), "Synthetic Source", SystemRole.Source,
            [new StorageEndpointDefinition(new StorageEndpointId("records"), new ConnectorId("sqlserver"),
                [new KeyValuePair<string, string>("connection", "secret:READONLY_TEST_SOURCE")])]);
        var targetSystem = new SystemDefinition(new SystemId("external"), "Observed Target", SystemRole.Target,
            [new StorageEndpointDefinition(new StorageEndpointId("records"), new ConnectorId("postgres"),
                [new KeyValuePair<string, string>("connection", "secret:READONLY_TEST_TARGET")])]);
        var source = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source", MigrationNodeType.Source, "Generic.Record",
            sourceSystem.Id, sourceSystem.StorageEndpoints[0].Id, new ArtifactSelector("table", [new("name", "dbo.records")], ["id"]));
        var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target", MigrationNodeType.Target, "Generic.Record",
            targetSystem.Id, targetSystem.StorageEndpoints[0].Id, new ArtifactSelector("table", [new("name", "public.records")], ["id"]));
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "mapping", [source.Id], [target.Id],
            new MigrationOperation(MigrationOperationType.Copy), "1", new RecoveryDefinition(RecoveryMode.Reverse));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source, target], [edge], new string('b', 64), "test");
        var configuration = new LoadedProjectConfiguration(new RootConfigurationDto(1,
            new ProjectConfigurationDto("read-only-test", "Read-only Target"), null, [], null, null, null), [],
            [sourceSystem, targetSystem], [], "read-only-test", new string('a', 64));
        await using (var connection = new SqlConnection(sourceContainer.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.records(id int NOT NULL PRIMARY KEY,value nvarchar(30) NOT NULL); INSERT INTO dbo.records VALUES(1,N'synthetic');";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var connection = new NpgsqlConnection(targetContainer.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE public.records(id integer NOT NULL PRIMARY KEY,value text NOT NULL); INSERT INTO public.records VALUES(1,'synthetic'); CREATE ROLE proofshift_observer LOGIN PASSWORD 'Synthetic-Read-Only!2026'; GRANT USAGE ON SCHEMA public TO proofshift_observer; GRANT SELECT ON public.records TO proofshift_observer;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var observedConnection = new NpgsqlConnectionStringBuilder(targetContainer.GetConnectionString())
        { Username = "proofshift_observer", Password = "Synthetic-Read-Only!2026" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(observedConnection))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT has_table_privilege(current_user,'public.records','INSERT') OR has_table_privilege(current_user,'public.records','UPDATE') OR has_table_privilege(current_user,'public.records','DELETE')";
            Assert.False((bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        var contexts = new RuntimeConnectorContextFactory(new MapEnvironmentProvider(new Dictionary<string, string>
        {
            ["READONLY_TEST_SOURCE"] = sourceContainer.GetConnectionString(), ["READONLY_TEST_TARGET"] = observedConnection
        }));
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-readonly-{Guid.NewGuid():N}");
        try
        {
            var snapshots = new FileSystemSnapshotStore(Path.Combine(root, "snapshots"));
            var checkpoint = await new SnapshotCaptureService(new ConnectorRegistry([new SqlServerSourceConnector()]), snapshots, contexts)
                .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, checkpoint.Status);
            var runId = new RunId(Guid.NewGuid());
            var observer = new ReaderTargetObserver(new ProofShift.Connectors.Postgres.PostgresSourceConnector());
            var observation = new ExternalMigrationObservation(runId, "vendor-loaded", configuration.ConfigurationHash, graph.GraphHash,
                checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!, checkpoint.Checkpoint.SourceFingerprint!,
                [new ExternalTargetEndpoint(target.Name, targetSystem.Id, target.EndpointId, observer.Id, observer.Version)], DateTimeOffset.UnixEpoch);
            var runtime = new VerificationTargetRuntime(target.Name, observer,
                new TargetObservationContext(contexts.Create(configuration, target), runId, SystemRole.Target));
            Assert.Null(runtime.ShadowWriter);
            var rules = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]).Resolve(
                [Rule("accounting", "source-artifact-accounting"), Rule("lineage", "target-lineage"), Rule("presence", "target-presence"), Rule("values", "attribute-comparison")]);
            var result = await new VerificationService(snapshots).VerifyExternalTargetAsync(configuration, graph, observation, rules,
                Path.Combine(root, "working"), "0.10-test", [runtime], TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, result.Run.Outcome);
            Assert.Equal(1, result.Ledger.LineageCount);
            Assert.Contains(result.Findings, finding => finding.RuleId.Value == "values" &&
                finding.Code == "AttributeComparison" && finding.Result == EvidenceResult.Pass);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private const string TargetSecret = "PS09_EXTERNAL_TARGET_CONNECTION";
    private const string SourceSecret = "PS09_EXTERNAL_SOURCE_CONNECTION";

    [Fact]
    public async Task VerificationServiceChecksExternalTargetWithoutProjectionManifestOrJournal()
    {
        var sqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Synthetic-PS09-Only-Password!2026").Build();
        await sqlContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var sqlCleanup = sqlContainer;
        var postgresContainer = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgresContainer.StartAsync(TestContext.Current.CancellationToken);
        await using var postgresCleanup = postgresContainer;
        var sourceSystem = new SystemDefinition(new SystemId("legacy"), "Synthetic Legacy", SystemRole.Source,
            [new StorageEndpointDefinition(new StorageEndpointId("source-store"), new ConnectorId("sqlserver"),
                [new KeyValuePair<string, string>("connection", $"secret:{SourceSecret}"),
                 new KeyValuePair<string, string>("checkpoint.consistency", "transaction-consistent"),
                 new KeyValuePair<string, string>("checkpoint.isolation", "serializable")])]);
        var targetSystem = new SystemDefinition(new SystemId("shadow"), "Externally Loaded Target", SystemRole.ShadowTarget,
            [new StorageEndpointDefinition(new StorageEndpointId("target-store"), new ConnectorId("postgres"),
                [new KeyValuePair<string, string>("connection", $"secret:{TargetSecret}")])]);
        var configurationHash = new string('a', 64);
        var graphHash = new string('b', 64);
        var sourceNode = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source-members", MigrationNodeType.Source,
            "Pension.Member", sourceSystem.Id, sourceSystem.StorageEndpoints[0].Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "dbo.MEMBER")], ["MEMBER_ID"]));
        var targetNode = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target-members", MigrationNodeType.Target,
            "Pension.Member", targetSystem.Id, targetSystem.StorageEndpoints[0].Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "public.participant")], ["member_id"]));
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "member-conversion", [sourceNode.Id], [targetNode.Id],
            new MigrationOperation(MigrationOperationType.Transform, fields:
            [
                new TransformationFieldDefinition("member_id", "MEMBER_ID"),
                new TransformationFieldDefinition("status", "STATUS_CD",
                    [new TransformationStep(TransformationStepType.CodeMap, "1", [new KeyValuePair<string, string>("A", "ACTIVE")])])
            ]), "1", new RecoveryDefinition(RecoveryMode.Reverse));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [sourceNode, targetNode], [edge], graphHash, "external-test-v1");
        var configuration = new LoadedProjectConfiguration(
            new RootConfigurationDto(1, new ProjectConfigurationDto("external-pension", "External Pension Check"), null, [], null, null, null),
            [], [sourceSystem, targetSystem], [], "external-pension-config", configurationHash);
        var targetRecord = TargetRecord("M00000001", "ACTIVE", targetSystem, targetNode);
        var directory = Path.Combine(Path.GetTempPath(), $"proofshift-external-verification-{Guid.NewGuid():N}");
        var checkpointStore = new FileSystemSnapshotStore(Path.Combine(directory, "checkpoints"));
        var sourceConnectors = new ConnectorRegistry([new SqlServerSourceConnector()]);
        var sqlBuilder = new SqlConnectionStringBuilder(sqlContainer.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        };
        var contextFactory = new RuntimeConnectorContextFactory(new MapEnvironmentProvider(new Dictionary<string, string>
        {
            [SourceSecret] = sqlBuilder.ConnectionString,
            [TargetSecret] = postgresContainer.GetConnectionString()
        }));
        try
        {
            await using (var connection = new SqlConnection(sqlBuilder.ConnectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var seed = connection.CreateCommand();
                seed.CommandText = "CREATE TABLE dbo.MEMBER (MEMBER_ID varchar(12) NOT NULL PRIMARY KEY, STATUS_CD char(1) NOT NULL); INSERT INTO dbo.MEMBER (MEMBER_ID, STATUS_CD) VALUES ('M00000001', 'A')";
                await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var checkpoint = await new SnapshotCaptureService(sourceConnectors, checkpointStore, contextFactory).CaptureAsync(
                configuration, graph, TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, checkpoint.Status);
            var externalRunId = new RunId(Guid.NewGuid());
            var schema = $"proofshift_shadow_{externalRunId.Value:N}";
            await using (var connection = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var load = connection.CreateCommand();
                load.CommandText = $"CREATE SCHEMA \"{schema}\"; CREATE TABLE \"{schema}\".participant (member_id text NOT NULL, status text NOT NULL); INSERT INTO \"{schema}\".participant (member_id, status) VALUES (@member, @status)";
                load.Parameters.AddWithValue("member", ((StringValue)targetRecord.Values["member_id"]).Value);
                load.Parameters.AddWithValue("status", ((StringValue)targetRecord.Values["status"]).Value);
                await load.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var targetConnector = new ProofShift.Connectors.Postgres.PostgresShadowTargetConnector();
            var observation = new ExternalMigrationObservation(externalRunId, "vendor-conversion-42",
                configurationHash, graphHash, checkpoint.Id, checkpoint.Checkpoint!.ManifestHash!,
                checkpoint.Checkpoint.SourceFingerprint!,
                [new ExternalTargetEndpoint(targetNode.Name, targetSystem.Id, targetSystem.StorageEndpoints[0].Id,
                    targetConnector.Id, targetConnector.Version)], DateTimeOffset.UnixEpoch);
            var runtimes = new[]
            {
                new VerificationTargetRuntime(targetNode.Name, targetConnector,
                    new ShadowTargetContext(contextFactory.Create(configuration, targetNode), externalRunId, SystemRole.ShadowTarget))
            };
            var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider(), new PensionPack()]);
            var rules = registry.Resolve(
            [
                Rule("source-accounting", "source-artifact-accounting"),
                Rule("target-lineage", "target-lineage"),
                Rule("target-presence", "target-presence"),
                Rule("unexpected-targets", "unexpected-target"),
                Rule("member-presence", "pension-member-presence", ("targetNode", targetNode.Name), ("businessKey", "member_id")),
                Rule("member-uniqueness", "pension-member-uniqueness", ("targetNode", targetNode.Name), ("businessKey", "member_id")),
                Rule("member-status", "pension-member-status", ("targetNode", targetNode.Name), ("businessKey", "member_id"), ("attribute", "status"))
            ]);

            var service = new VerificationService(checkpointStore);
            var result = await service.VerifyExternalTargetAsync(configuration, graph, observation, rules,
                Path.Combine(directory, "working"), "0.9-test", runtimes, TestContext.Current.CancellationToken);

            Assert.True(result.Run.Outcome == VerificationOutcome.Passed,
                string.Join(Environment.NewLine, result.Findings.Where(finding => finding.Result == EvidenceResult.Fail)
                    .Select(finding => $"{finding.Code}: {finding.Explanation} [{finding.StableKey}]")));
            Assert.Equal("vendor-conversion-42", result.Run.ObservationId);
            Assert.Equal(1, result.Ledger.DispositionCount);
            Assert.Equal(1, result.Ledger.LineageCount);
            await using var verificationLedger = await SqliteVerificationLedgerStore.OpenAsync(
                Path.Combine(directory, "working"), result.Ledger, TestContext.Current.CancellationToken);
            await using var dispositionReader = verificationLedger.ReadDispositionsAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await dispositionReader.MoveNextAsync());
            var disposition = dispositionReader.Current;
            Assert.Equal(ArtifactDisposition.Transformed, disposition.Disposition);
            Assert.False(await dispositionReader.MoveNextAsync());
            await using var lineageReader = verificationLedger.ReadLineageAsync(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await lineageReader.MoveNextAsync());
            var lineage = lineageReader.Current;
            Assert.Equal(LineageBasis.GraphDerivedExpected, lineage.Basis);
            Assert.False(await lineageReader.MoveNextAsync());
            var observationFinding = Assert.Single(result.Findings, finding => finding.Code == "ExternalTargetObserved");
            Assert.Equal("ProofShift independently observed externally populated target state; this observation does not claim ProofShift executed the migration.",
                observationFinding.Explanation);
            Assert.DoesNotContain(result.Findings, finding => finding.Code == "MaterializedTargetFingerprint");
            Assert.False(Directory.Exists(Path.Combine(directory, ".proofshift", "projections")));
            await using (var verifyExternalRow = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await verifyExternalRow.OpenAsync(TestContext.Current.CancellationToken);
                await using var query = verifyExternalRow.CreateCommand();
                query.CommandText = $"SELECT status FROM \"{schema}\".participant WHERE member_id = @member";
                query.Parameters.AddWithValue("member", "M00000001");
                Assert.Equal("ACTIVE", (string)(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
            var evidenceStore = new FileSystemEvidenceStore(Path.Combine(directory, "evidence"));
            await evidenceStore.SaveAsync(result.EvidenceGraph, TestContext.Current.CancellationToken);
            Assert.True(await evidenceStore.VerifyIntegrityAsync(result.Run.Id, TestContext.Current.CancellationToken));

            var repeatedExternalRunId = new RunId(Guid.NewGuid());
            var repeatedSchema = $"proofshift_shadow_{repeatedExternalRunId.Value:N}";
            await using (var connection = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var loadRepeat = connection.CreateCommand();
                loadRepeat.CommandText = $"CREATE SCHEMA \"{repeatedSchema}\"; CREATE TABLE \"{repeatedSchema}\".participant (member_id text NOT NULL, status text NOT NULL); INSERT INTO \"{repeatedSchema}\".participant (member_id, status) VALUES ('M00000001', 'ACTIVE')";
                await loadRepeat.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var repeatedObservation = new ExternalMigrationObservation(repeatedExternalRunId, "vendor-conversion-42-repeat",
                configurationHash, graphHash, checkpoint.Id, checkpoint.Checkpoint.ManifestHash!,
                checkpoint.Checkpoint.SourceFingerprint!, observation.Targets, DateTimeOffset.UnixEpoch);
            var repeatedRuntime = new VerificationTargetRuntime(targetNode.Name, targetConnector,
                new ShadowTargetContext(contextFactory.Create(configuration, targetNode), repeatedExternalRunId, SystemRole.ShadowTarget));
            var repeatedResult = await service.VerifyExternalTargetAsync(configuration, graph, repeatedObservation, rules,
                Path.Combine(directory, "working-repeat"), "0.9-test", [repeatedRuntime], TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Passed, repeatedResult.Run.Outcome);
            Assert.NotEqual(result.Run.Id, repeatedResult.Run.Id);
            Assert.Equal(result.Run.TargetFingerprint, repeatedResult.Run.TargetFingerprint);
            Assert.Equal(result.Run.EvidenceFingerprint, repeatedResult.Run.EvidenceFingerprint);

            var defectiveExternalRunId = new RunId(Guid.NewGuid());
            var defectiveSchema = $"proofshift_shadow_{defectiveExternalRunId.Value:N}";
            await using (var connection = new NpgsqlConnection(postgresContainer.GetConnectionString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var loadDefect = connection.CreateCommand();
                loadDefect.CommandText = $"CREATE SCHEMA \"{defectiveSchema}\"; CREATE TABLE \"{defectiveSchema}\".participant (member_id text NOT NULL, status text NOT NULL); INSERT INTO \"{defectiveSchema}\".participant (member_id, status) VALUES ('M00000001', 'RETIRED')";
                await loadDefect.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var defectiveObservation = new ExternalMigrationObservation(defectiveExternalRunId,
                "vendor-conversion-43-defective", configurationHash, graphHash, checkpoint.Id,
                checkpoint.Checkpoint.ManifestHash!, checkpoint.Checkpoint.SourceFingerprint!,
                observation.Targets, DateTimeOffset.UnixEpoch);
            var defectiveRuntime = new VerificationTargetRuntime(targetNode.Name, targetConnector,
                new ShadowTargetContext(contextFactory.Create(configuration, targetNode), defectiveExternalRunId, SystemRole.ShadowTarget));
            var defectiveResult = await service.VerifyExternalTargetAsync(configuration, graph, defectiveObservation, rules,
                Path.Combine(directory, "working-defective"), "0.9-test", [defectiveRuntime], TestContext.Current.CancellationToken);
            Assert.Equal(VerificationOutcome.Failed, defectiveResult.Run.Outcome);
            Assert.Contains(defectiveResult.Findings, finding => finding.Code == "WrongMemberStatus");
            Assert.NotEqual(result.Run.EvidenceFingerprint, defectiveResult.Run.EvidenceFingerprint);
            await evidenceStore.SaveAsync(defectiveResult.EvidenceGraph, TestContext.Current.CancellationToken);
            Assert.True(await evidenceStore.VerifyIntegrityAsync(defectiveResult.Run.Id, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static VerificationRuleDefinition Rule(string id, string type, params (string Key, string Value)[] options) =>
        new(new RuleId(id), type, "1", EvidenceSeverity.Critical,
            options.Select(option => new KeyValuePair<string, string>(option.Key, option.Value)));

    private static RecordEnvelope TargetRecord(string memberId, string status, SystemDefinition system, MigrationNode node)
    {
        var values = new Dictionary<string, ValueNode>
        {
            ["member_id"] = new StringValue(memberId),
            ["status"] = new StringValue(status)
        };
        var identity = GraphTargetIdentity.Create(new RecordEnvelope(
            new ArtifactReference(new ArtifactId("pending"), system.Id, node.EndpointId,
                StableArtifactIdentity.ArtifactTypeFor(node.Selector), "pending"),
            node.SemanticType, values,
            new ProvenanceMetadata(new ConnectorId("synthetic-target"), node.EndpointId, "external-fixture-loader", DateTimeOffset.UnixEpoch)),
            node.Selector);
        var artifact = new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(system.Id.Value,
            node.EndpointId.Value, StableArtifactIdentity.ArtifactTypeFor(node.Selector), identity)), system.Id,
            node.EndpointId, StableArtifactIdentity.ArtifactTypeFor(node.Selector), identity);
        return new RecordEnvelope(artifact, node.SemanticType, values,
            new ProvenanceMetadata(new ConnectorId("synthetic-target"), node.EndpointId, "external-fixture-loader", DateTimeOffset.UnixEpoch));
    }

    private sealed class MapEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.GetValueOrDefault(name);
    }
}
