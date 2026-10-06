using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Data.SqlClient;
using Npgsql;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Oracle;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Snapshots;
using DotNet.Testcontainers.Builders;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class OracleConnectorIntegrationTests
{
    private const string Image = "oracle/database:23.26.0-free";
    private const string Password = "OraclePS010C2026";

    [Fact]
    public async Task OracleConnectionFailureDiagnosticsRedactCredentials()
    {
        const string secret = "Synthetic-Oracle-Redaction-Secret";
        var connectionString = $"User Id=synthetic;Password={secret};Data Source=127.0.0.1:1/FREEPDB1;Connection Timeout=1;Pooling=false";
        var context = Context("oracle-redaction", "source-db", connectionString, "PS_C_OWNER");
        var result = await new OracleSourceConnector().InspectAsync(context,
            new ArtifactSelector("table", [new("name", "PS_C_OWNER.ROWS")], ["ID"]), TestContext.Current.CancellationToken);
        var diagnostics = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
        Assert.Equal(SourceInspectionStatus.Failed, result.Status);
        Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OracleFreeDiscoversInspectsStreamsExactTypesLobsAndReadOnlyTargetObservation()
    {
        const int measurementRecordCount = 20000;
        await using var container = new ContainerBuilder(Image)
            .WithPortBinding(1521, true)
            .WithEnvironment("ORACLE_PWD", Password)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("DATABASE IS READY TO USE",
                options => options.WithTimeout(TimeSpan.FromMinutes(15))))
            .Build();
        var containerStartup = Stopwatch.StartNew();
        try
        {
            await container.StartAsync(TestContext.Current.CancellationToken);
            containerStartup.Stop();
        }
        catch (DockerUnavailableException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Oracle Free integration tests.");
        }

        var dataSource = $"{container.Hostname}:{container.GetMappedPublicPort(1521)}/FREEPDB1";
        var adminConnectionString = $"User Id=SYSTEM;Password={Password};Data Source={dataSource};Pooling=false";
        const string ownerPassword = "Synthetic-PS010C-Owner!2026";
        const string readerPassword = "Synthetic-PS010C-Reader!2026";
        var fixtureProvisioning = Stopwatch.StartNew();
        await using (var admin = new OracleConnection(adminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(admin, $"CREATE USER PS_C_OWNER IDENTIFIED BY \"{ownerPassword}\"");
            await ExecuteAsync(admin, "GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO PS_C_OWNER");
            await ExecuteAsync(admin, "ALTER USER PS_C_OWNER QUOTA UNLIMITED ON USERS");
            await ExecuteAsync(admin, $"CREATE USER PS_C_READER IDENTIFIED BY \"{readerPassword}\"");
            await ExecuteAsync(admin, "GRANT CREATE SESSION TO PS_C_READER");
        }

        var connectionString = $"User Id=PS_C_OWNER;Password={ownerPassword};Data Source={dataSource};Pooling=false";
        await using (var setup = new OracleConnection(connectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(setup, "CREATE TABLE PS_C_PARENT (ID NUMBER(10) CONSTRAINT PS_C_P_PK PRIMARY KEY, CODE VARCHAR2(40 CHAR) CONSTRAINT PS_C_P_UQ UNIQUE)");
            await ExecuteAsync(setup, "CREATE TABLE PS_C_ROWS (ID NUMBER(10) CONSTRAINT PS_C_R_PK PRIMARY KEY, AMOUNT NUMBER(28,6), LARGE_TOTAL NUMBER(28,6), NEGATIVE_AMOUNT NUMBER(18,6), FIXED_VALUE CHAR(4 CHAR), TEXT_VALUE VARCHAR2(100 CHAR), UNICODE_VALUE NVARCHAR2(80), DATE_VALUE DATE, LOCAL_VALUE TIMESTAMP(6), OFFSET_VALUE TIMESTAMP(6) WITH TIME ZONE, OPTIONAL_VALUE VARCHAR2(20), PAYLOAD BLOB, DOCUMENT CLOB, PARENT_ID NUMBER(10) CONSTRAINT PS_C_R_FK REFERENCES PS_C_PARENT(ID))");
            await ExecuteAsync(setup, "CREATE TABLE PS_C_MEASURE_ROWS (ID NUMBER(10) PRIMARY KEY, AMOUNT NUMBER(18,6) NOT NULL)");
            for (var offset = 1; offset <= measurementRecordCount; offset += 500)
            {
                var insert = new StringBuilder("INSERT ALL ");
                var limit = Math.Min(offset + 499, measurementRecordCount);
                for (var id = offset; id <= limit; id++)
                {
                    var amount = (id / 1000m + 0.123456m).ToString("F6", CultureInfo.InvariantCulture);
                    insert.Append("INTO PS_C_MEASURE_ROWS (ID,AMOUNT) VALUES (")
                        .Append(id.ToString(CultureInfo.InvariantCulture)).Append(',').Append(amount).Append(") ");
                }
                insert.Append("SELECT 1 FROM DUAL");
                await ExecuteAsync(setup, insert.ToString());
            }
            await ExecuteAsync(setup, "CREATE VIEW PS_C_ROWS_VIEW AS SELECT ID, AMOUNT FROM PS_C_ROWS");
            await ExecuteAsync(setup, "INSERT INTO PS_C_PARENT (ID,CODE) VALUES (1,'parent-one')");
            await ExecuteAsync(setup, "INSERT INTO PS_C_ROWS VALUES (1,0.01,12345678901234567890.123456,-42.123456,'A','Oracle synthetic text',N'\u96ea\u306e\u5b50',TO_DATE('2025-03-04 05:06:07','YYYY-MM-DD HH24:MI:SS'),TO_TIMESTAMP('2025-03-04 05:06:07.123456','YYYY-MM-DD HH24:MI:SS.FF'),TO_TIMESTAMP_TZ('2025-03-04 05:06:07.123456 +05:30','YYYY-MM-DD HH24:MI:SS.FF TZH:TZM'),NULL,TO_BLOB(HEXTORAW('00010203FEFF')),TO_CLOB('synthetic clob payload'),1)");
        }
        await using (var admin = new OracleConnection(adminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(admin, "GRANT SELECT ON PS_C_OWNER.PS_C_ROWS TO PS_C_READER");
        }
        fixtureProvisioning.Stop();

        var connector = new OracleSourceConnector();
        var context = Context("oracle-source", "source-db", connectionString, "PS_C_OWNER");
        var discovered = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.True(PhysicalDiscovery.Verify(discovered));
        var sourceTable = Assert.Single(discovered.Objects, item => item.Name == "PS_C_ROWS" && item.Schema == "PS_C_OWNER");
        Assert.Equal("table", sourceTable.Kind);
        Assert.Contains(sourceTable.Fields, field => field.Name == "DATE_VALUE" && field.NativeType == "DATE" && field.IsNullable);
        Assert.Contains(sourceTable.Keys, key => key.Primary && key.Fields.SequenceEqual(["ID"]));
        var parentTable = Assert.Single(discovered.Objects, item => item.Name == "PS_C_PARENT" && item.Schema == "PS_C_OWNER");
        Assert.Contains(parentTable.Keys, key => !key.Primary && key.Fields.SequenceEqual(["CODE"]));
        Assert.Contains(sourceTable.Relationships, relation => relation.TargetObject == "PS_C_PARENT");
        Assert.Contains(discovered.Objects, item => item.Name == "PS_C_ROWS_VIEW" && item.Kind == "view");
        var rediscovered = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.Equal(discovered.Fingerprint, rediscovered.Fingerprint);

        var selector = new ArtifactSelector("table", [new("name", "PS_C_OWNER.PS_C_ROWS")], ["ID"]);
        var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
        Assert.True(inspection.Status == SourceInspectionStatus.Valid,
            string.Join(Environment.NewLine, inspection.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        Assert.Equal("ID", Assert.Single(inspection.PrimaryKeyFields));
        Assert.Equal(SourceConsistencyGuarantee.Observed, connector.CheckpointConsistency);
        var checkpointRows = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadForCheckpointAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken))
            checkpointRows.Add(record);
        var row = Assert.Single(checkpointRows);
        var measurementSelector = new ArtifactSelector("table", [new("name", "PS_C_OWNER.PS_C_MEASURE_ROWS")], ["ID"]);
        var payloadBytesEstimate = EstimateScalarPayloadBytes(measurementRecordCount);
        RecordEnvelope? firstMeasuredRow = null;
        var measurement = await ConnectorReadMeasurement.MeasureAsync("Oracle 26ai", 0, payloadBytesEstimate,
            async cancellationToken =>
            {
                long count = 0;
                await foreach (var record in connector.ReadAsync(context, measurementSelector, new ReadOptions(), cancellationToken))
                {
                    firstMeasuredRow ??= record;
                    count++;
                }
                return count;
            }, containerStartup.Elapsed.TotalSeconds, fixtureProvisioning.Elapsed.TotalSeconds,
            TestContext.Current.CancellationToken);
        Assert.Equal(measurementRecordCount, measurement.RecordCount);
        Assert.IsType<DecimalValue>(firstMeasuredRow!.Values["AMOUNT"]);
        using (var cancellation = new CancellationTokenSource())
        {
            await using var enumerator = connector.ReadAsync(context, selector, new ReadOptions(), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }
        Assert.Equal(new DecimalValue(0.01m), row.Values["AMOUNT"]);
        Assert.Equal(new DecimalValue(12345678901234567890.123456m), row.Values["LARGE_TOTAL"]);
        Assert.Equal(new DecimalValue(-42.123456m), row.Values["NEGATIVE_AMOUNT"]);
        Assert.Equal("A", Assert.IsType<StringValue>(row.Values["FIXED_VALUE"]).Value.TrimEnd());
        Assert.Equal(new StringValue("Oracle synthetic text"), row.Values["TEXT_VALUE"]);
        Assert.Equal(new StringValue("\u96ea\u306e\u5b50"), row.Values["UNICODE_VALUE"]);
        Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified)), row.Values["DATE_VALUE"]);
        Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, 123, DateTimeKind.Unspecified).AddTicks(4560)), row.Values["LOCAL_VALUE"]);
        Assert.Equal(new OffsetDateTimeValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.5)).AddTicks(1234560)), row.Values["OFFSET_VALUE"]);
        Assert.IsType<NullValue>(row.Values["OPTIONAL_VALUE"]);
        Assert.Equal(new StringValue("synthetic clob payload"), row.Values["DOCUMENT"]);
        var binary = Assert.IsType<BinaryReferenceValue>(row.Values["PAYLOAD"]);
        var expectedHash = Convert.ToHexString(SHA256.HashData([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff])).ToLowerInvariant();
        Assert.Equal(6, binary.ContentLength);
        Assert.Equal(expectedHash, binary.Sha256);
        var resolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(connector);
        await using (var stream = await resolver.OpenBinaryReadAsync(context, selector, row.Artifact, binary, TestContext.Current.CancellationToken))
        {
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, TestContext.Current.CancellationToken);
            Assert.Equal([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff], content.ToArray());
        }

        var readonlyConnection = $"User Id=PS_C_READER;Password={readerPassword};Data Source={dataSource};Pooling=false";
        var targetContext = Context("oracle-target", "target-db", readonlyConnection, "PS_C_OWNER");
        var observer = new ReaderTargetObserver(connector);
        var observed = new List<RecordEnvelope>();
        await foreach (var record in observer.ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(targetContext, new RunId(Guid.NewGuid()), SystemRole.Target), selector, new ReadOptions()), TestContext.Current.CancellationToken))
            observed.Add(record);
        var target = Assert.Single(observed);
        Assert.Equal("2:ID=9:integer:1", target.Artifact.Identity);
        Assert.Equal(row.Values["LARGE_TOTAL"], target.Values["LARGE_TOTAL"]);
        await using (var readOnly = new OracleConnection(readonlyConnection))
        {
            await readOnly.OpenAsync(TestContext.Current.CancellationToken);
            foreach (var sql in new[]
                     {
                         "INSERT INTO PS_C_OWNER.PS_C_ROWS (ID) VALUES (2)",
                         "UPDATE PS_C_OWNER.PS_C_ROWS SET AMOUNT=2 WHERE ID=1",
                         "DELETE FROM PS_C_OWNER.PS_C_ROWS WHERE ID=1"
                     })
            {
                await using var denied = readOnly.CreateCommand();
                denied.CommandText = sql;
                await Assert.ThrowsAsync<OracleException>(() => denied.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }
        }
        Assert.DoesNotContain("PS_C_READER", System.Text.Json.JsonSerializer.Serialize(target.Provenance), StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, System.Text.Json.JsonSerializer.Serialize(discovered), StringComparison.Ordinal);

        await using (var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Synthetic-PS010C-SqlServer!2026").Build())
        {
            await sqlServer.StartAsync(TestContext.Current.CancellationToken);
            var sqlConnectionString = new SqlConnectionStringBuilder(sqlServer.GetConnectionString())
            { Encrypt = false, TrustServerCertificate = true }.ConnectionString;
            await using (var seed = new SqlConnection(sqlConnectionString))
            {
                await seed.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = seed.CreateCommand();
                command.CommandText = "CREATE TABLE dbo.RECORDS (ID int NOT NULL PRIMARY KEY, LARGE_TOTAL decimal(28,6) NOT NULL, LOCAL_VALUE datetime2(6) NOT NULL); INSERT INTO dbo.RECORDS VALUES (1,12345678901234567890.123456,'2025-03-04T05:06:07.123456')";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var sqlSource = new SqlServerSourceConnector();
            var sqlSourceContext = new ConnectorContext("sql-source", "records", new ConnectorId("sqlserver"),
                "sql-source-node", "Generic.Record", new RuntimeConfiguration([
                    new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(sqlConnectionString, isSecret: true))]));
            var sqlRows = new List<RecordEnvelope>();
            await foreach (var sourceRow in sqlSource.ReadAsync(sqlSourceContext,
                new ArtifactSelector("table", [new("name", "dbo.RECORDS")], ["ID"]), new ReadOptions(), TestContext.Current.CancellationToken))
                sqlRows.Add(sourceRow);
            var independentlyObservedOracleTarget = Assert.Single(observed);
            Assert.Equal(Assert.Single(sqlRows).Values["LARGE_TOTAL"], independentlyObservedOracleTarget.Values["LARGE_TOTAL"]);
            Assert.Equal(Assert.Single(sqlRows).Values["LOCAL_VALUE"], independentlyObservedOracleTarget.Values["LOCAL_VALUE"]);
        }

        await using (var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build())
        {
            await postgres.StartAsync(TestContext.Current.CancellationToken);
            await using (var seed = new NpgsqlConnection(postgres.GetConnectionString()))
            {
                await seed.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = seed.CreateCommand();
                command.CommandText = "CREATE SCHEMA assurance; CREATE TABLE assurance.records(id integer PRIMARY KEY, amount numeric(28,6) NOT NULL, large_total numeric(28,6) NOT NULL, local_value timestamp without time zone NOT NULL); INSERT INTO assurance.records VALUES (1,0.01,12345678901234567890.123456,TIMESTAMP '2025-03-04 05:06:07.123456'); CREATE ROLE oracle_cross_reader LOGIN PASSWORD 'Synthetic-Oracle-Cross-Read!2026'; GRANT USAGE ON SCHEMA assurance TO oracle_cross_reader; GRANT SELECT ON assurance.records TO oracle_cross_reader;";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var postgresReadOnly = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
            { Username = "oracle_cross_reader", Password = "Synthetic-Oracle-Cross-Read!2026" }.ConnectionString;
            var postgresContext = new ConnectorContext("postgres-target", "records", new ConnectorId("postgres"),
                "postgres-target-node", "Generic.Record", new RuntimeConfiguration([
                    new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(postgresReadOnly, isSecret: true))]));
            var postgresObserved = new List<RecordEnvelope>();
            await foreach (var targetRow in new ReaderTargetObserver(new PostgresSourceConnector()).ObserveAsync(new TargetObservationRequest(
                new TargetObservationContext(postgresContext, new RunId(Guid.NewGuid()), SystemRole.Target),
                new ArtifactSelector("table", [new("name", "assurance.records")], ["id"]), new ReadOptions()), TestContext.Current.CancellationToken))
                postgresObserved.Add(targetRow);
            var oracleRows = new List<RecordEnvelope>();
            await foreach (var sourceRow in connector.ReadAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken))
                oracleRows.Add(sourceRow);
            var oracleSource = Assert.Single(oracleRows);
            var postgresTarget = Assert.Single(postgresObserved);
            Assert.Equal(oracleSource.Values["AMOUNT"], postgresTarget.Values["amount"]);
            Assert.Equal(oracleSource.Values["LARGE_TOTAL"], postgresTarget.Values["large_total"]);
            Assert.Equal(oracleSource.Values["LOCAL_VALUE"], postgresTarget.Values["local_value"]);
        }

        var checkpointRoot = Path.Combine(Path.GetTempPath(), $"proofshift-oracle-checkpoint-{Guid.NewGuid():N}");
        try
        {
            const string connectionSecret = "PS010C_ORACLE_CHECKPOINT_CONNECTION";
            var sourceSystem = new SystemDefinition(new SystemId("oracle-checkpoint-source"), "Oracle Source", SystemRole.Source,
            [new StorageEndpointDefinition(new StorageEndpointId("source-db"), new ConnectorId("oracle"),
                [new("connection", $"secret:{connectionSecret}"), new("schema", "PS_C_OWNER")])]);
            var sourceNode = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "oracle-source-row", MigrationNodeType.Source,
                "Generic.Record", sourceSystem.Id, sourceSystem.StorageEndpoints[0].Id, selector);
            var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [sourceNode], [], new string('a', 64), "oracle-checkpoint-test-v1");
            var configuration = new LoadedProjectConfiguration(new RootConfigurationDto(1,
                new ProjectConfigurationDto("oracle-checkpoint", "Oracle Checkpoint"), null, [], null, null, null), [],
                [sourceSystem], [], "oracle-checkpoint", new string('b', 64));
            var environment = new OracleTestEnvironmentProvider(new Dictionary<string, string> { [connectionSecret] = connectionString });
            var factory = new RuntimeConnectorContextFactory(environment);
            var store = new FileSystemSnapshotStore(checkpointRoot);
            var capture = await new SnapshotCaptureService(new ConnectorRegistry([connector]), store, factory)
                .CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, capture.Status);
            Assert.False(capture.Checkpoint!.CrossSystemAtomic);
            await container.StopAsync(TestContext.Current.CancellationToken);
            await using var loaded = await store.OpenCompleteAsync(capture.Id.Value.ToString("N"), TestContext.Current.CancellationToken);
            var replay = new CheckpointSourceArtifactStreamProvider(loaded);
            var replayContext = factory.Create(configuration, sourceNode);
            _ = await replay.ValidateAsync(sourceNode.Name, replayContext, selector, TestContext.Current.CancellationToken);
            var replayed = new List<RecordEnvelope>();
            await foreach (var record in replay.ReadAsync(sourceNode.Name, replayContext, selector, TestContext.Current.CancellationToken))
                replayed.Add(record);
            var replayedRow = Assert.Single(replayed);
            Assert.Equal(row.Values["LARGE_TOTAL"], replayedRow.Values["LARGE_TOTAL"]);
            var replayedBlob = Assert.IsType<BinaryReferenceValue>(replayedRow.Values["PAYLOAD"]);
            await using var replayStream = await replay.OpenBinaryReadAsync(sourceNode.Name, replayContext, selector,
                replayedRow.Artifact, replayedBlob, TestContext.Current.CancellationToken);
            using var replayContent = new MemoryStream();
            await replayStream.CopyToAsync(replayContent, TestContext.Current.CancellationToken);
            Assert.Equal([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff], replayContent.ToArray());
        }
        finally
        {
            if (Directory.Exists(checkpointRoot)) Directory.Delete(checkpointRoot, recursive: true);
        }
    }

    private static ConnectorContext Context(string system, string endpoint, string connection, string schema) =>
        new(system, endpoint, new ConnectorId("oracle"), "records", "Generic.Record", new RuntimeConfiguration([
            new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(connection, isSecret: true)),
            new KeyValuePair<string, RuntimeSetting>("schema", RuntimeSetting.FromRuntimeValue(schema))]));

    private static async Task ExecuteAsync(OracleConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static long EstimateScalarPayloadBytes(int recordCount)
    {
        long bytes = 0;
        for (var id = 1; id <= recordCount; id++)
        {
            bytes += Encoding.UTF8.GetByteCount(id.ToString(CultureInfo.InvariantCulture));
            bytes += Encoding.UTF8.GetByteCount((id / 1000m + 0.123456m).ToString("F6", CultureInfo.InvariantCulture));
        }
        return bytes;
    }

    private sealed class OracleTestEnvironmentProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.TryGetValue(name, out var value) ? value : null;
    }
}