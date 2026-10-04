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
using ProofShift.Projection;
using ProofShift.Snapshots;
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
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(csvRoot, recursive: true);
            Directory.Delete(shadowRoot, recursive: true);
            Directory.Delete(projectRoot, recursive: true);
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
            Endpoint("member-database", "sqlserver", ("connection", "secret:PS05_SQL_CONNECTION")),
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
            Edge("member-split", [memberSource], [participant, status], MigrationOperationType.Split, memberFields),
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
        MigrationOperationType operation, IEnumerable<TransformationFieldDefinition> fields) =>
        new(new MigrationEdgeId(Guid.NewGuid()), name, sources.Select(node => node.Id), targets.Select(node => node.Id),
            new MigrationOperation(operation, fields: fields, isDestructive: operation == MigrationOperationType.Exclude), "1",
            new RecoveryDefinition(RecoveryMode.Reverse));

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