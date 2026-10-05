using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Npgsql;
using DotNet.Testcontainers.Builders;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Domain;
using ProofShift.Projection;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

[Collection("DockerIntegration")]
public sealed class RelationalConnectorIntegrationTests
{
    private const string ContainerPassword = "Synthetic-PS04-Only-Password!2026";
    private const string ExactAmount = "12345678901234567890.12345678";
    private static readonly string[] CompositePostgresKey = ["member_id", "period"];
    private static readonly string[] CompositeSqlServerKey = ["MemberId", "Period"];

    [Fact]
    public async Task PostgreSqlShadowConnectorIsolatesWritesAndReadsMaterializedValuesBack()
    {
        var container = await StartPostgresContainerAsync();
        await using var cleanup = container;
        await using (var setup = new NpgsqlConnection(container.GetConnectionString()))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = """
                CREATE TABLE public.participant (
                    id integer NOT NULL PRIMARY KEY,
                    name text NOT NULL,
                    amount numeric(28, 8) NOT NULL,
                    birth_date date NOT NULL,
                    local_at timestamp without time zone NOT NULL,
                    instant_at timestamp with time zone NOT NULL,
                    optional_value text NULL
                );
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connector = new PostgresShadowTargetConnector();
        var selector = TableSelector("public.participant", ["id"]);
        var runId = new RunId(Guid.NewGuid());
        var context = new ShadowTargetContext(
            RelationalContext("postgres", "shadow-system", "shadow-db", container.GetConnectionString()),
            runId,
            SystemRole.ShadowTarget);
        await connector.PrepareAsync(context, selector, TestContext.Current.CancellationToken);
        var identity = "2:id=9:integer:1";
        var record = new RecordEnvelope(
            new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId("shadow-system", "shadow-db", "row", identity)),
                new SystemId("shadow-system"), new StorageEndpointId("shadow-db"), "table", identity),
            "Pension.Member",
            new Dictionary<string, ValueNode>
            {
                ["id"] = new IntegerValue(1),
                ["name"] = new StringValue("Ada"),
                ["amount"] = new DecimalValue(12345678901234567890.12345678m),
                ["birth_date"] = new DateValue(new DateOnly(1990, 2, 3)),
                ["local_at"] = new LocalDateTimeValue(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)),
                ["instant_at"] = new InstantValue(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))),
                ["optional_value"] = new NullValue()
            },
            new ProvenanceMetadata(new ConnectorId("postgres"), new StorageEndpointId("shadow-db"), identity, DateTimeOffset.UnixEpoch));
        var request = new ShadowWriteRequest(context, selector, record, "participant");
        await connector.WriteAsync(request, TestContext.Current.CancellationToken);
        await connector.CompleteAsync(context, TestContext.Current.CancellationToken);

        var readBack = Assert.Single(await ReadAllAsync(connector.ReadAsync(new ReadRequest(context, selector), TestContext.Current.CancellationToken)));
        Assert.Equal(new DecimalValue(12345678901234567890.12345678m), readBack.Values["amount"]);
        Assert.Equal(new DateValue(new DateOnly(1990, 2, 3)), readBack.Values["birth_date"]);
        Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)), readBack.Values["local_at"]);
        Assert.Equal(new InstantValue(new DateTimeOffset(2025, 1, 2, 1, 4, 5, TimeSpan.Zero)), readBack.Values["instant_at"]);
        Assert.Equal(new NullValue(), readBack.Values["optional_value"]);
        await Assert.ThrowsAsync<ProjectionConnectorException>(() => connector.WriteAsync(request, TestContext.Current.CancellationToken));

        var secondRun = new ShadowTargetContext(
            RelationalContext("postgres", "shadow-system", "shadow-db", container.GetConnectionString()),
            new RunId(Guid.NewGuid()),
            SystemRole.ShadowTarget);
        await connector.PrepareAsync(secondRun, selector, TestContext.Current.CancellationToken);
        Assert.Empty(await ReadAllAsync(connector.ReadAsync(new ReadRequest(secondRun, selector), TestContext.Current.CancellationToken)));
        await using var verify = new NpgsqlConnection(container.GetConnectionString());
        await verify.OpenAsync(TestContext.Current.CancellationToken);
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM public.participant";
        Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task PostgreSqlShadowWriteSessionRollsBackDuplicateBatchAndCommitsNextBatch()
    {
        var container = await StartPostgresContainerAsync();
        await using var cleanup = container;
        await using (var setup = new NpgsqlConnection(container.GetConnectionString()))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = "CREATE TABLE public.participant (id integer NOT NULL PRIMARY KEY, name text NOT NULL, amount numeric(28, 8) NOT NULL, birth_date date NOT NULL, local_at timestamp without time zone NOT NULL, instant_at timestamp with time zone NOT NULL, optional_value text NULL)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connector = new PostgresShadowTargetConnector();
        var selector = TableSelector("public.participant", ["id"]);
        var context = new ShadowTargetContext(
            RelationalContext("postgres", "shadow-system", "shadow-db", container.GetConnectionString()),
            new RunId(Guid.NewGuid()),
            SystemRole.ShadowTarget);
        await connector.PrepareAsync(context, selector, TestContext.Current.CancellationToken);
        await using var session = await connector.OpenWriteSessionAsync(context, selector, 2, TestContext.Current.CancellationToken);

        RecordEnvelope Record(int id, string identity)
        {
            var artifact = new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(
                    "shadow-system", "shadow-db", "table", identity)),
                new SystemId("shadow-system"), new StorageEndpointId("shadow-db"), "table", identity);
            return new RecordEnvelope(artifact, "Generic.Member", new Dictionary<string, ValueNode>
            {
                ["id"] = new IntegerValue(id),
                ["name"] = new StringValue($"Member {id.ToString(CultureInfo.InvariantCulture)}"),
                ["amount"] = new DecimalValue(id),
                ["birth_date"] = new DateValue(new DateOnly(1990, 2, 3)),
                ["local_at"] = new LocalDateTimeValue(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)),
                ["instant_at"] = new InstantValue(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)),
                ["optional_value"] = new NullValue()
            }, new ProvenanceMetadata(new ConnectorId("postgres"), new StorageEndpointId("shadow-db"), identity, DateTimeOffset.UnixEpoch));
        }

        var duplicateBatch = new[]
        {
            new ShadowWriteRequest(context, selector, Record(1, "same-identity"), "member-node"),
            new ShadowWriteRequest(context, selector, Record(2, "same-identity"), "member-node")
        };
        var duplicate = await Assert.ThrowsAsync<ProjectionConnectorException>(() =>
            session.WriteBatchAsync(duplicateBatch, TestContext.Current.CancellationToken));
        Assert.Equal("PSPROJ_TARGET_DUPLICATE", duplicate.Code);
        Assert.Empty(await ReadAllAsync(connector.ReadAsync(new ReadRequest(context, selector), TestContext.Current.CancellationToken)));

        await using (var verify = new NpgsqlConnection(container.GetConnectionString()))
        {
            await verify.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = verify.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"proofshift_shadow_{context.RunId.Value:N}\".\"__ps_projection_identity\"";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        var validBatch = new[]
        {
            new ShadowWriteRequest(context, selector, Record(1, "identity-one"), "member-node"),
            new ShadowWriteRequest(context, selector, Record(2, "identity-two"), "member-node")
        };
        await session.WriteBatchAsync(validBatch, TestContext.Current.CancellationToken);
        await connector.CompleteAsync(context, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await ReadAllAsync(connector.ReadAsync(new ReadRequest(context, selector), TestContext.Current.CancellationToken))).Count);
    }

    [Fact]
    public async Task PostgreSqlConnectorInspectsAndStreamsCompositeIdentityAndTypedValues()
    {
        var container = await StartPostgresContainerAsync();
        await using var cleanup = container;

        await using (var setup = new NpgsqlConnection(container.GetConnectionString()))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = $"""
                CREATE SCHEMA assurance;
                CREATE TABLE assurance.member_rows (
                    member_id integer NOT NULL,
                    period integer NOT NULL,
                    amount numeric(28, 8) NOT NULL,
                    active boolean NOT NULL,
                    effective_at timestamptz NOT NULL,
                    local_at timestamp without time zone NOT NULL,
                    optional_value text NULL,
                    member_guid uuid NOT NULL,
                    payload bytea NOT NULL,
                    PRIMARY KEY (member_id, period)
                );
                INSERT INTO assurance.member_rows
                    (member_id, period, amount, active, effective_at, local_at, optional_value, member_guid, payload)
                SELECT generated,
                       202501,
                       {ExactAmount}::numeric,
                       generated % 2 = 0,
                       '2025-01-02 03:04:05+02'::timestamptz,
                       '2025-01-02 03:04:05'::timestamp,
                       CASE WHEN generated = 1 THEN NULL ELSE 'synthetic' END,
                       '123e4567-e89b-12d3-a456-426614174000'::uuid,
                       decode('01020304', 'hex')
                FROM generate_series(1, 100) AS generated;
                ANALYZE assurance.member_rows;
                CREATE TABLE assurance.duplicate_rows (identity_id integer NOT NULL, payload text);
                INSERT INTO assurance.duplicate_rows VALUES (1, 'first'), (1, 'second');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connector = new PostgresSourceConnector();
        var selector = TableSelector("assurance.member_rows", CompositePostgresKey);
        var context = RelationalContext("postgres", "pg-source", "member-store", container.GetConnectionString());
        var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
        var rows = await ReadAllAsync(connector.ReadAsync(context, selector, new ReadOptions(batchSize: 17), TestContext.Current.CancellationToken));

        Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
        Assert.Equal(CompositePostgresKey, inspection.PrimaryKeyFields);
        Assert.Equal(CompositePostgresKey, inspection.IdentityFields);
        Assert.Equal(9, inspection.Columns.Count);
        Assert.Equal(100, rows.Count);
        Assert.Equal(ExactAmount, Assert.IsType<DecimalValue>(rows[0].Values["amount"]).Value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(new BooleanValue(false), rows[0].Values["active"]);
        Assert.Equal(new NullValue(), rows[0].Values["optional_value"]);
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", Assert.IsType<StringValue>(rows[0].Values["member_guid"]).Value);
        var postgresBinary = Assert.IsType<BinaryReferenceValue>(rows[0].Values["payload"]);
        Assert.Equal(4, postgresBinary.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(), postgresBinary.Sha256);
        Assert.Equal("assurance.member_rows|9:member_id=1:1|6:period=6:202501", rows[0].Artifact.Identity);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 1, 4, 5, TimeSpan.Zero),
            Assert.IsType<InstantValue>(rows[0].Values["effective_at"]).Value);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            Assert.IsType<LocalDateTimeValue>(rows[0].Values["local_at"]).Value);
        Assert.All(rows, row => Assert.Equal("pg-source", row.Artifact.SystemId.Value));

        var postgresResolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(connector);
        await using (var binaryStream = await postgresResolver.OpenBinaryReadAsync(
            context, selector, rows[0].Artifact, postgresBinary, TestContext.Current.CancellationToken))
        using (var output = new MemoryStream())
        {
            await binaryStream.CopyToAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal(postgresBinary.ContentLength, output.Length);
            Assert.Equal(postgresBinary.Sha256, Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant());
        }

        var duplicateSelector = TableSelector("assurance.duplicate_rows", ["identity_id"]);
        var duplicateInspection = await connector.InspectAsync(context, duplicateSelector, TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Valid, duplicateInspection.Status);
        await Assert.ThrowsAsync<ConnectorReadException>(() => ReadAllAsync(
            connector.ReadAsync(context, duplicateSelector, new ReadOptions(), TestContext.Current.CancellationToken)));
        var noIdentityInspection = await connector.InspectAsync(
            context,
            TableSelector("assurance.duplicate_rows", []),
            TestContext.Current.CancellationToken);
        Assert.Contains(noIdentityInspection.Issues, issue => issue.Code == ConnectorIssueCodes.NonDeterministicIdentity);
        var missingTableInspection = await connector.InspectAsync(
            context,
            TableSelector("assurance.missing_rows", CompositePostgresKey),
            TestContext.Current.CancellationToken);
        Assert.Contains(missingTableInspection.Issues, issue => issue.Code == ConnectorIssueCodes.SourceObjectNotFound);
    }

    [Fact]
    public async Task SqlServerConnectorInspectsAndStreamsCompositeIdentityAndTypedValues()
    {
        var container = await StartSqlServerContainerAsync();
        await using var cleanup = container;

        var connectionStringBuilder = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            Encrypt = false,
            TrustServerCertificate = true
        };
        await using (var setup = new SqlConnection(connectionStringBuilder.ConnectionString))
        {
            await setup.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = setup.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE dbo.MemberRows (
                    MemberId int NOT NULL,
                    Period int NOT NULL,
                    Amount decimal(28, 8) NOT NULL,
                    Active bit NOT NULL,
                    EffectiveDate date NOT NULL,
                    EffectiveAt datetimeoffset(7) NOT NULL,
                    LocalAt datetime2(7) NOT NULL,
                    OptionalValue nvarchar(50) NULL,
                    MemberGuid uniqueidentifier NOT NULL,
                    Payload varbinary(8) NOT NULL,
                    CONSTRAINT PK_MemberRows PRIMARY KEY (MemberId, Period)
                );
                WITH numbers AS (
                    SELECT TOP (100) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS generated
                    FROM sys.all_objects
                )
                INSERT INTO dbo.MemberRows
                    (MemberId, Period, Amount, Active, EffectiveDate, EffectiveAt, LocalAt, OptionalValue, MemberGuid, Payload)
                SELECT generated,
                       202501,
                       {ExactAmount},
                       CASE WHEN generated % 2 = 0 THEN 1 ELSE 0 END,
                       '2025-01-02',
                       '2025-01-02T03:04:05+02:00',
                       '2025-01-02T03:04:05',
                       CASE WHEN generated = 1 THEN NULL ELSE N'synthetic' END,
                       '123e4567-e89b-12d3-a456-426614174000',
                       0x01020304
                FROM numbers;
                CREATE TABLE dbo.DuplicateRows (IdentityId int NOT NULL, Payload nvarchar(20));
                INSERT INTO dbo.DuplicateRows VALUES (1, N'first'), (1, N'second');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connector = new SqlServerSourceConnector();
        var selector = TableSelector("dbo.MemberRows", CompositeSqlServerKey);
        var context = RelationalContext("sqlserver", "sql-source", "member-store", connectionStringBuilder.ConnectionString);
        var inspection = await connector.InspectAsync(context, selector, TestContext.Current.CancellationToken);
        var rows = await ReadAllAsync(connector.ReadAsync(context, selector, new ReadOptions(batchSize: 13), TestContext.Current.CancellationToken));

        Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
        Assert.Equal(CompositeSqlServerKey, inspection.PrimaryKeyFields);
        Assert.Equal(CompositeSqlServerKey, inspection.IdentityFields);
        Assert.Equal(10, inspection.Columns.Count);
        Assert.Equal(100, rows.Count);
        Assert.Equal(ExactAmount, Assert.IsType<DecimalValue>(rows[0].Values["Amount"]).Value.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(new BooleanValue(false), rows[0].Values["Active"]);
        Assert.Equal(new DateValue(new DateOnly(2025, 1, 2)), rows[0].Values["EffectiveDate"]);
        Assert.Equal(new NullValue(), rows[0].Values["OptionalValue"]);
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", Assert.IsType<StringValue>(rows[0].Values["MemberGuid"]).Value);
        var sqlBinary = Assert.IsType<BinaryReferenceValue>(rows[0].Values["Payload"]);
        Assert.Equal(4, sqlBinary.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant(), sqlBinary.Sha256);
        Assert.Equal("dbo.MemberRows|8:MemberId=1:1|6:Period=6:202501", rows[0].Artifact.Identity);
        var sqlOffset = Assert.IsType<OffsetDateTimeValue>(rows[0].Values["EffectiveAt"]).Value;
        Assert.Equal(TimeSpan.FromHours(2), sqlOffset.Offset);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), sqlOffset);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Unspecified),
            Assert.IsType<LocalDateTimeValue>(rows[0].Values["LocalAt"]).Value);
        Assert.All(rows, row => Assert.Equal("sql-source", row.Artifact.SystemId.Value));

        var sqlResolver = Assert.IsAssignableFrom<ISourceBinaryContentResolver>(connector);
        await using (var binaryStream = await sqlResolver.OpenBinaryReadAsync(
            context, selector, rows[0].Artifact, sqlBinary, TestContext.Current.CancellationToken))
        using (var output = new MemoryStream())
        {
            await binaryStream.CopyToAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal(sqlBinary.ContentLength, output.Length);
            Assert.Equal(sqlBinary.Sha256, Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant());
        }

        var duplicateSelector = TableSelector("dbo.DuplicateRows", ["IdentityId"]);
        var duplicateInspection = await connector.InspectAsync(context, duplicateSelector, TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Valid, duplicateInspection.Status);
        await Assert.ThrowsAsync<ConnectorReadException>(() => ReadAllAsync(
            connector.ReadAsync(context, duplicateSelector, new ReadOptions(), TestContext.Current.CancellationToken)));
        var noIdentityInspection = await connector.InspectAsync(
            context,
            TableSelector("dbo.DuplicateRows", []),
            TestContext.Current.CancellationToken);
        Assert.Contains(noIdentityInspection.Issues, issue => issue.Code == ConnectorIssueCodes.NonDeterministicIdentity);
        var missingTableInspection = await connector.InspectAsync(
            context,
            TableSelector("dbo.MissingRows", CompositeSqlServerKey),
            TestContext.Current.CancellationToken);
        Assert.Contains(missingTableInspection.Issues, issue => issue.Code == ConnectorIssueCodes.SourceObjectNotFound);
    }

    private static ArtifactSelector TableSelector(string name, IEnumerable<string> identity) =>
        new("table", [new KeyValuePair<string, string>("name", name)], identity);

    private static async Task<PostgreSqlContainer> StartPostgresContainerAsync()
    {
        PostgreSqlContainer? container = null;
        try
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("proofshift_synthetic")
                .WithUsername("proofshift_test")
                .WithPassword(ContainerPassword)
                .Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            return container;
        }
        catch (DockerUnavailableException)
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }

            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for PostgreSQL integration tests.");
        }
    }

    private static async Task<MsSqlContainer> StartSqlServerContainerAsync()
    {
        MsSqlContainer? container = null;
        try
        {
            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
                .WithPassword(ContainerPassword)
                .Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            return container;
        }
        catch (DockerUnavailableException)
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }

            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for SQL Server integration tests.");
        }
    }

    private static ConnectorContext RelationalContext(string connector, string system, string endpoint, string connectionString) =>
        new(system, endpoint, new ConnectorId(connector), "member-node", "Generic.Member",
            new RuntimeConfiguration([
                new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(connectionString, isSecret: true))
            ]));

    private static async Task<List<RecordEnvelope>> ReadAllAsync(IAsyncEnumerable<RecordEnvelope> records)
    {
        var values = new List<RecordEnvelope>();
        await foreach (var record in records.WithCancellation(TestContext.Current.CancellationToken))
        {
            values.Add(record);
        }

        return values;
    }
}
