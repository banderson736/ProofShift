using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Diagnostics;
using IBM.Data.Db2;
using Npgsql;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Db2;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Connectors.StructuredFiles;
using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Snapshots;
using DotNet.Testcontainers.Builders;
using Testcontainers.PostgreSql;
using Testcontainers.MsSql;
using Microsoft.Data.SqlClient;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class Db2ConnectorIntegrationTests
{
    private const string Image = "icr.io/db2_community/db2:11.5.9.0";
    private const string Password = "Synthetic-PS010C-Db2-Only!2026";

    [Fact]
    public async Task Db2ConnectionFailureDiagnosticsRedactCredentials()
    {
        const string secret = "Synthetic-Db2-Redaction-Secret";
        var connectionString = $"Server=127.0.0.1:1;Database=PS010C;UID=synthetic;PWD={secret};Connect Timeout=1";
        var context = Context("db2-redaction", "source-db", connectionString, "PS_C");
        var result = await new Db2SourceConnector().InspectAsync(context,
            new ArtifactSelector("table", [new("name", "PS_C.ROWS")], ["ID"]), TestContext.Current.CancellationToken);
        var diagnostics = string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message));
        Assert.Equal(SourceInspectionStatus.Failed, result.Status);
        Assert.DoesNotContain(secret, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionString, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db2CommunityDiscoversInspectsStreamsExactTypesLobsAndReadOnlyTargetObservation()
    {
        const int measurementRecordCount = 20000;
        await using var container = new ContainerBuilder(Image)
            .WithPortBinding(50000, true)
            .WithEnvironment("LICENSE", "accept")
            .WithEnvironment("DB2INST1_PASSWORD", Password)
            .WithEnvironment("DBNAME", "PS010C")
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.Privileged = true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Setup has completed", options => options.WithTimeout(TimeSpan.FromMinutes(10))))
            .Build();
        var containerStartup = Stopwatch.StartNew();
        await container.StartAsync(TestContext.Current.CancellationToken);
        containerStartup.Stop();

        var connectionString = $"Server={container.Hostname}:{container.GetMappedPublicPort(50000)};Database=PS010C;UID=db2inst1;PWD={Password};";
        var fixtureProvisioning = Stopwatch.StartNew();
        await using (var setup = new DB2Connection(connectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(setup, "CREATE SCHEMA PS_C");
            await ExecuteAsync(setup, "CREATE TABLE PS_C.MEASURE_ROWS (ID INTEGER NOT NULL PRIMARY KEY, AMOUNT DECIMAL(18,6) NOT NULL)");
            for (var offset = 1; offset <= measurementRecordCount; offset += 500)
            {
                var insert = new StringBuilder("INSERT INTO PS_C.MEASURE_ROWS (ID,AMOUNT) VALUES ");
                var limit = Math.Min(offset + 499, measurementRecordCount);
                for (var id = offset; id <= limit; id++)
                {
                    if (id > offset) insert.Append(',');
                    var amount = (id / 1000m + 0.123456m).ToString("F6", CultureInfo.InvariantCulture);
                    insert.Append('(').Append(id.ToString(CultureInfo.InvariantCulture)).Append(',').Append(amount).Append(')');
                }
                await ExecuteAsync(setup, insert.ToString());
            }
            await ExecuteAsync(setup, "CREATE TABLE PS_C.PARENT (ID INTEGER NOT NULL PRIMARY KEY, CODE VARCHAR(40) NOT NULL UNIQUE)");
            await ExecuteAsync(setup, "CREATE TABLE PS_C.RECORDS (ID INTEGER NOT NULL PRIMARY KEY, ROW_CODE VARCHAR(40) NOT NULL UNIQUE, SMALL_VALUE SMALLINT, INTEGER_VALUE INTEGER, BIG_VALUE BIGINT, EXACT_AMOUNT DECIMAL(28,6), NEGATIVE_AMOUNT NUMERIC(18,6), APPROX_VALUE DOUBLE, CHAR_VALUE CHAR(4), TEXT_VALUE VARCHAR(100), UNICODE_VALUE VARCHAR(100), DATE_VALUE DATE, TIME_VALUE TIME, LOCAL_VALUE TIMESTAMP, OPTIONAL_VALUE VARCHAR(20), BINARY_VALUE VARBINARY(16), PAYLOAD BLOB(1024), DOCUMENT CLOB(1024), PARENT_ID INTEGER NOT NULL, CONSTRAINT PS_C_FK FOREIGN KEY (PARENT_ID) REFERENCES PS_C.PARENT(ID))");
            await ExecuteAsync(setup, "CREATE VIEW PS_C.RECORDS_VIEW AS SELECT ID,EXACT_AMOUNT FROM PS_C.RECORDS");
            await ExecuteAsync(setup, "INSERT INTO PS_C.PARENT VALUES (1,'parent-one')");
            await ExecuteAsync(setup, "INSERT INTO PS_C.RECORDS (ID,ROW_CODE,SMALL_VALUE,INTEGER_VALUE,BIG_VALUE,EXACT_AMOUNT,NEGATIVE_AMOUNT,APPROX_VALUE,CHAR_VALUE,TEXT_VALUE,UNICODE_VALUE,DATE_VALUE,TIME_VALUE,LOCAL_VALUE,OPTIONAL_VALUE,BINARY_VALUE,PAYLOAD,DOCUMENT,PARENT_ID) VALUES (1,'row-one',2,3,4000000000,12345678901234567890.123456,-42.123456,1.25,'A','Db2 synthetic text','\u96ea\u306e\u5b50',DATE('2025-03-04'),TIME('12:34:56'),TIMESTAMP('2025-03-04 05:06:07.123456'),NULL,BX'0102',BLOB(X'00010203FEFF'),CLOB('synthetic clob payload'),1)");
        }

        var createReader = await container.ExecAsync(["bash", "-c", "useradd -m proofshift_reader && printf '%s:%s' proofshift_reader 'Synthetic-PS010C-Db2-Reader!2026' | chpasswd"],
            TestContext.Current.CancellationToken);
        Assert.Equal(0, createReader.ExitCode);
        await using (var grant = new DB2Connection(connectionString))
        {
            await grant.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(grant, "GRANT SELECT ON TABLE PS_C.RECORDS TO USER PROOFSHIFT_READER");
        }
        fixtureProvisioning.Stop();

        var connector = new Db2SourceConnector();
        var context = Context("db2-source", "source-db", connectionString, "PS_C");
        var discovered = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.True(PhysicalDiscovery.Verify(discovered));
        var sourceTable = Assert.Single(discovered.Objects, item => item.Name == "RECORDS");
        Assert.Equal("table", sourceTable.Kind);
        Assert.Contains(sourceTable.Fields, field => field.Name == "EXACT_AMOUNT" && field.NativeType.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase));
        var primaryKey = Assert.Single(sourceTable.Keys, key => key.Primary);
        Assert.Equal(["ID"], primaryKey.Fields);
        var uniqueKey = Assert.Single(sourceTable.Keys, key => !key.Primary);
        Assert.Equal(["ROW_CODE"], uniqueKey.Fields);
        Assert.Contains(sourceTable.Relationships, relation => relation.TargetObject == "PARENT");
        Assert.Contains(discovered.Objects, item => item.Name == "RECORDS_VIEW" && item.Kind == "view");
        var later = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.Equal(discovered.Fingerprint, later.Fingerprint);
        await using (var driftConnection = new DB2Connection(connectionString))
        {
            await driftConnection.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(driftConnection, "ALTER TABLE PS_C.RECORDS ADD COLUMN DRIFT_COLUMN VARCHAR(12)");
        }
        var drifted = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.NotEqual(discovered.Fingerprint, drifted.Fingerprint);
        Assert.Contains(PhysicalDiscoveryDiff.Compare(discovered, drifted), change =>
            change.Kind == "field-added" && change.Field == "DRIFT_COLUMN");

        var selector = new ArtifactSelector("table", [new("name", "PS_C.RECORDS")], ["ID"]);
        var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
        Assert.Equal("ID", Assert.Single(inspection.PrimaryKeyFields));
        var records = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadForCheckpointAsync(context, selector, new ReadOptions(), TestContext.Current.CancellationToken))
            records.Add(record);
        var row = Assert.Single(records);
        var measurementSelector = new ArtifactSelector("table", [new("name", "PS_C.MEASURE_ROWS")], ["ID"]);
        var payloadBytesEstimate = EstimateScalarPayloadBytes(measurementRecordCount);
        RecordEnvelope? firstMeasuredRow = null;
        var measurement = await ConnectorReadMeasurement.MeasureAsync("Db2 LUW Community 11.5.9.0", 0, payloadBytesEstimate,
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
        Assert.Equal(new IntegerValue(2), row.Values["SMALL_VALUE"]);
        Assert.Equal(new IntegerValue(3), row.Values["INTEGER_VALUE"]);
        Assert.Equal(new IntegerValue(4000000000), row.Values["BIG_VALUE"]);
        Assert.Equal(new DecimalValue(12345678901234567890.123456m), row.Values["EXACT_AMOUNT"]);
        Assert.Equal(new DecimalValue(-42.123456m), row.Values["NEGATIVE_AMOUNT"]);
        Assert.IsType<StringValue>(row.Values["APPROX_VALUE"]);
        Assert.Equal("A", Assert.IsType<StringValue>(row.Values["CHAR_VALUE"]).Value.TrimEnd());
        Assert.Equal(new StringValue("Db2 synthetic text"), row.Values["TEXT_VALUE"]);
        Assert.Equal(new StringValue("\u96ea\u306e\u5b50"), row.Values["UNICODE_VALUE"]);
        Assert.Equal(new DateValue(new DateOnly(2025, 3, 4)), row.Values["DATE_VALUE"]);
        Assert.Equal(new StringValue("12:34:56"), row.Values["TIME_VALUE"]);
        Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, 123, DateTimeKind.Unspecified).AddTicks(4560)), row.Values["LOCAL_VALUE"]);
        Assert.IsType<NullValue>(row.Values["OPTIONAL_VALUE"]);
        Assert.Equal(new StringValue("synthetic clob payload"), row.Values["DOCUMENT"]);
        var binary = Assert.IsType<BinaryReferenceValue>(row.Values["PAYLOAD"]);
        var expectedHash = Convert.ToHexString(SHA256.HashData([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff])).ToLowerInvariant();
        Assert.Equal(6, binary.ContentLength);
        Assert.Equal(expectedHash, binary.Sha256);
        var variableBinary = Assert.IsType<BinaryReferenceValue>(row.Values["BINARY_VALUE"]);
        Assert.Equal(2, variableBinary.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([0x01, 0x02])).ToLowerInvariant(), variableBinary.Sha256);
        var resolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(connector);
        await using (var stream = await resolver.OpenBinaryReadAsync(context, selector, row.Artifact, binary, TestContext.Current.CancellationToken))
        {
            using var content = new MemoryStream();
            await stream.CopyToAsync(content, TestContext.Current.CancellationToken);
            Assert.Equal([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff], content.ToArray());
        }

        var readOnlyConnection = $"Server={container.Hostname}:{container.GetMappedPublicPort(50000)};Database=PS010C;UID=proofshift_reader;PWD=Synthetic-PS010C-Db2-Reader!2026;";
        var targetContext = Context("db2-target", "target-db", readOnlyConnection, "PS_C");
        await using (var readOnly = new DB2Connection(readOnlyConnection))
        {
            await readOnly.OpenAsync(TestContext.Current.CancellationToken);
            await using var deniedWrite = readOnly.CreateCommand();
            deniedWrite.CommandText = "INSERT INTO PS_C.RECORDS (ID,ROW_CODE,PARENT_ID) VALUES (2,'forbidden',1)";
            await Assert.ThrowsAsync<DB2Exception>(() => deniedWrite.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
        var observed = new List<RecordEnvelope>();
        await foreach (var record in new ReaderTargetObserver(connector).ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(targetContext, new RunId(Guid.NewGuid()), SystemRole.Target), selector, new ReadOptions()), TestContext.Current.CancellationToken))
            observed.Add(record);
        var observedRecord = Assert.Single(observed);
        Assert.Equal("2:ID=9:integer:1", observedRecord.Artifact.Identity);
        await VerifySqlServerToDb2Async(observedRecord, TestContext.Current.CancellationToken);
        var observedBinary = Assert.IsType<BinaryReferenceValue>(observedRecord.Values["PAYLOAD"]);
        await using (var observedStream = await resolver.OpenBinaryReadAsync(targetContext, selector, observedRecord.Artifact,
            observedBinary, TestContext.Current.CancellationToken))
        {
            using var observedContent = new MemoryStream();
            await observedStream.CopyToAsync(observedContent, TestContext.Current.CancellationToken);
            Assert.Equal([0x00, 0x01, 0x02, 0x03, 0xfe, 0xff], observedContent.ToArray());
        }
        await VerifyDb2ToPostgresAsync(row, TestContext.Current.CancellationToken);

        var fileRoot = Path.Combine(Path.GetTempPath(), $"proofshift-db2-mixed-{Guid.NewGuid():N}");
        var checkpointRoot = Path.Combine(Path.GetTempPath(), $"proofshift-db2-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(fileRoot, "docs"));
        var fixedPath = Path.Combine(fileRoot, "members.txt");
        var ndjsonPath = Path.Combine(fileRoot, "events.ndjson");
        var documentPath = Path.Combine(fileRoot, "docs", "letter.bin");
        await File.WriteAllTextAsync(fixedPath, "0007\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(ndjsonPath, "{\"id\":8,\"amount\":0.01}\n", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(documentPath, [0x41, 0x00, 0xfe, 0xff], TestContext.Current.CancellationToken);

        const string mixedDb2Secret = "PS010C_MIXED_DB2";
        const string mixedFilesRoot = "PS010C_MIXED_FILES";
        var mixedSystem = new SystemDefinition(new SystemId("mixed-source"), "Synthetic Mixed Source", SystemRole.Source,
        [
            new StorageEndpointDefinition(new StorageEndpointId("db2-records"), new ConnectorId("db2"),
                [new("connection", $"secret:{mixedDb2Secret}"), new("schema", "PS_C")]),
            new StorageEndpointDefinition(new StorageEndpointId("fixed-records"), new ConnectorId("fixed-width"),
                [new("root", $"env:{mixedFilesRoot}")]),
            new StorageEndpointDefinition(new StorageEndpointId("event-records"), new ConnectorId("ndjson"),
                [new("root", $"env:{mixedFilesRoot}")]),
            new StorageEndpointDefinition(new StorageEndpointId("documents"), new ConnectorId("files"),
                [new("root", $"env:{mixedFilesRoot}")])
        ]);
        var mixedNodes = new MigrationNode[]
        {
            new(new MigrationNodeId(Guid.NewGuid()), "db2-members", MigrationNodeType.Source, "Generic.Record", mixedSystem.Id,
                new StorageEndpointId("db2-records"), new ArtifactSelector("table", [new("name", "PS_C.RECORDS"), new("columns", "ID,EXACT_AMOUNT")], ["ID"])),
            new(new MigrationNodeId(Guid.NewGuid()), "fixed-members", MigrationNodeType.Source, "Generic.Record", mixedSystem.Id,
                new StorageEndpointId("fixed-records"), new ArtifactSelector("fixed-width", [new("path", "members.txt"), new("width", "4"),
                    new("fields.id.start", "1"), new("fields.id.length", "4"), new("fields.id.type", "integer")], ["id"])),
            new(new MigrationNodeId(Guid.NewGuid()), "ndjson-events", MigrationNodeType.Source, "Generic.Record", mixedSystem.Id,
                new StorageEndpointId("event-records"), new ArtifactSelector("ndjson", [new("path", "events.ndjson"),
                    new("fields.id.type", "integer"), new("fields.amount.type", "decimal")], ["id"])),
            new(new MigrationNodeId(Guid.NewGuid()), "member-documents", MigrationNodeType.Source, "Generic.Document", mixedSystem.Id,
                new StorageEndpointId("documents"), new ArtifactSelector("file-pattern", [new("pattern", "docs/*.bin")], ["relativePath"]))
        };
        var mixedGraph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), mixedNodes, [], new string('d', 64), "mixed-test-v1");
        var mixedConfiguration = new LoadedProjectConfiguration(new RootConfigurationDto(1,
            new ProjectConfigurationDto("db2-mixed-checkpoint", "Db2 Mixed Checkpoint"), null, [], null, null, null), [],
            [mixedSystem], [], "db2-mixed-checkpoint", new string('e', 64));
        var contextFactory = new RuntimeConnectorContextFactory(new TestEnvironmentVariableProvider(new Dictionary<string, string>
        {
            [mixedDb2Secret] = connectionString,
            [mixedFilesRoot] = fileRoot
        }));
        try
        {
            var checkpointStore = new FileSystemSnapshotStore(checkpointRoot);
            var connectors = new ConnectorRegistry([connector, new StructuredFileConnector("fixed-width"),
                new StructuredFileConnector("ndjson"), new FilesystemSourceConnector()]);
            var capture = await new SnapshotCaptureService(connectors, checkpointStore, contextFactory)
                .CaptureAsync(mixedConfiguration, mixedGraph, TestContext.Current.CancellationToken);
            Assert.Equal(CheckpointStatus.Complete, capture.Status);
            Assert.Equal(4, capture.Checkpoint!.Endpoints.Count);
            Assert.Equal(4, capture.CapturedArtifacts);
            Assert.False(capture.Checkpoint.CrossSystemAtomic);

            await container.StopAsync(TestContext.Current.CancellationToken);
            File.Delete(fixedPath);
            File.Delete(ndjsonPath);
            File.Delete(documentPath);
            await using var loaded = await checkpointStore.OpenCompleteAsync(capture.Id.Value.ToString("N"), TestContext.Current.CancellationToken);
            var replay = new CheckpointSourceArtifactStreamProvider(loaded);
            var replayCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in mixedNodes)
            {
                var nodeContext = contextFactory.Create(mixedConfiguration, node);
                _ = await replay.ValidateAsync(node.Name, nodeContext, node.Selector, TestContext.Current.CancellationToken);
                var nodeCount = 0;
                await foreach (var record in replay.ReadAsync(node.Name, nodeContext, node.Selector, TestContext.Current.CancellationToken))
                {
                    nodeCount++;
                    if (node.Name == "member-documents")
                    {
                        var reference = Assert.IsType<BinaryReferenceValue>(record.Values["content"]);
                        await using var stream = await replay.OpenBinaryReadAsync(node.Name, nodeContext, node.Selector,
                            record.Artifact, reference, TestContext.Current.CancellationToken);
                        using var content = new MemoryStream();
                        await stream.CopyToAsync(content, TestContext.Current.CancellationToken);
                        Assert.Equal([0x41, 0x00, 0xfe, 0xff], content.ToArray());
                    }
                }
                replayCounts.Add(node.Name, nodeCount);
            }
            Assert.All(replayCounts.Values, count => Assert.Equal(1, count));
            Assert.False(loaded.Manifest.CrossSystemAtomic);
        }
        finally
        {
            if (Directory.Exists(fileRoot)) Directory.Delete(fileRoot, recursive: true);
            if (Directory.Exists(checkpointRoot)) Directory.Delete(checkpointRoot, recursive: true);
        }
    }

    private static async Task VerifySqlServerToDb2Async(RecordEnvelope db2TargetRow, CancellationToken cancellationToken)
    {
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithPassword("Synthetic-PS010C-SqlServer!2026").Build();
        await sqlServer.StartAsync(cancellationToken);
        var connectionString = new SqlConnectionStringBuilder(sqlServer.GetConnectionString())
        { Encrypt = false, TrustServerCertificate = true }.ConnectionString;
        await using (var seed = new SqlConnection(connectionString))
        {
            await seed.OpenAsync(cancellationToken);
            await using var command = seed.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.RECORDS (ID int NOT NULL PRIMARY KEY, EXACT_AMOUNT decimal(28,6) NOT NULL, LOCAL_VALUE datetime2(6) NOT NULL); INSERT INTO dbo.RECORDS VALUES (1,12345678901234567890.123456,'2025-03-04T05:06:07.123456')";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var sourceContext = new ConnectorContext("sqlserver-source", "source-records", new ConnectorId("sqlserver"),
            "source-node", "Generic.Record", new RuntimeConfiguration([
                new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(connectionString, isSecret: true))]));
        var sourceRows = new List<RecordEnvelope>();
        await foreach (var sourceRow in new SqlServerSourceConnector().ReadAsync(sourceContext,
            new ArtifactSelector("table", [new("name", "dbo.RECORDS")], ["ID"]), new ReadOptions(), cancellationToken))
            sourceRows.Add(sourceRow);
        var sqlServerSourceRow = Assert.Single(sourceRows);
        Assert.Equal(sqlServerSourceRow.Values["EXACT_AMOUNT"], db2TargetRow.Values["EXACT_AMOUNT"]);
        Assert.Equal(sqlServerSourceRow.Values["LOCAL_VALUE"], db2TargetRow.Values["LOCAL_VALUE"]);
    }

    private static async Task VerifyDb2ToPostgresAsync(RecordEnvelope db2SourceRow, CancellationToken cancellationToken)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync(cancellationToken);
        await using (var setup = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await setup.OpenAsync(cancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = "CREATE SCHEMA assurance; CREATE TABLE assurance.records(id integer PRIMARY KEY, amount numeric(28,6) NOT NULL, birth_date date NOT NULL, local_at timestamp without time zone NOT NULL, payload bytea NOT NULL); INSERT INTO assurance.records VALUES (1,12345678901234567890.123456,DATE '2025-03-04',TIMESTAMP '2025-03-04 05:06:07.123456',decode('00010203feff','hex')); CREATE ROLE proofshift_cross_reader LOGIN PASSWORD 'Synthetic-Cross-Provider-Read!2026'; GRANT USAGE ON SCHEMA assurance TO proofshift_cross_reader; GRANT SELECT ON assurance.records TO proofshift_cross_reader;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var postgresReadOnly = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = "proofshift_cross_reader", Password = "Synthetic-Cross-Provider-Read!2026" }.ConnectionString;
        await using (var readOnly = new NpgsqlConnection(postgresReadOnly))
        {
            await readOnly.OpenAsync(cancellationToken);
            await using var deniedWrite = readOnly.CreateCommand();
            deniedWrite.CommandText = "INSERT INTO assurance.records VALUES (2,0.01,DATE '2026-01-01',TIMESTAMP '2026-01-01 00:00:00',decode('01','hex'))";
            await Assert.ThrowsAsync<PostgresException>(() => deniedWrite.ExecuteNonQueryAsync(cancellationToken));
        }

        var context = new ConnectorContext("postgres-target", "target-records", new ConnectorId("postgres"),
            "target-node", "Generic.Record", new RuntimeConfiguration([
                new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(postgresReadOnly, isSecret: true))]));
        var observed = new List<RecordEnvelope>();
        await foreach (var targetRecord in new ReaderTargetObserver(new PostgresSourceConnector()).ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(context, new RunId(Guid.NewGuid()), SystemRole.Target),
            new ArtifactSelector("table", [new("name", "assurance.records")], ["id"]), new ReadOptions()), cancellationToken))
            observed.Add(targetRecord);
        var targetRow = Assert.Single(observed);
        Assert.Equal(db2SourceRow.Values["EXACT_AMOUNT"], targetRow.Values["amount"]);
        Assert.Equal(db2SourceRow.Values["DATE_VALUE"], targetRow.Values["birth_date"]);
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

    private static ConnectorContext Context(string system, string endpoint, string connection, string schema) =>
        new(system, endpoint, new ConnectorId("db2"), "records", "Generic.Record", new RuntimeConfiguration([
            new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(connection, isSecret: true)),
            new KeyValuePair<string, RuntimeSetting>("schema", RuntimeSetting.FromRuntimeValue(schema))]));

    private static async Task ExecuteAsync(DB2Connection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class TestEnvironmentVariableProvider(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
    {
        public string? GetValue(string name) => values.TryGetValue(name, out var value) ? value : null;
    }
}