using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Domain;
using ProofShift.Engine;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PhysicalDiscoveryTests
{
    [Fact]
    public async Task CsvAndFilesystemDiscoveryKeepInferenceAggregateAndRespectRootBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "records.csv"), "id;amount;enabled\n1;2.50;true\n2;;false\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "sensitive-member-name.pdf"), "synthetic", TestContext.Current.CancellationToken);
            var csvContext = new ConnectorContext("source", "csv", new ConnectorId("csv"), "discovery", "Physical.Uninterpreted",
                new RuntimeConfiguration([new("root", RuntimeSetting.FromRuntimeValue(root)), new("delimiter", RuntimeSetting.FromRuntimeValue(";"))]));
            var selector = new ArtifactSelector("csv", [new("path", "records.csv"), new("delimiter", ";")], ["id"]);
            var csv = await new CsvSourceConnector().DiscoverAsync(csvContext, [selector], TestContext.Current.CancellationToken);
            var item = Assert.Single(csv.Objects);
            Assert.Equal(2, item.RecordCount);
            Assert.Equal("integer", item.Fields.Single(field => field.Name == "id").NativeType);
            Assert.Equal(1, item.Fields.Single(field => field.Name == "amount").EmptyValues);
            Assert.True(item.Fields.All(field => field.TypeInferred));
            Assert.True(PhysicalDiscovery.Verify(csv));
            var escaped = new ArtifactSelector("csv", [new("path", "../outside.csv")], ["id"]);
            await Assert.ThrowsAsync<ConnectorReadException>(() => new CsvSourceConnector().DiscoverAsync(csvContext, [escaped], TestContext.Current.CancellationToken));
            var filesContext = new ConnectorContext("source", "files", new ConnectorId("files"), "discovery", "Physical.Uninterpreted", csvContext.Configuration);
            var files = await new FilesystemSourceConnector().DiscoverAsync(filesContext, [], TestContext.Current.CancellationToken);
            Assert.Equal(2, files.Objects.Sum(physical => physical.RecordCount));
            Assert.Contains(files.Objects, physical => physical.Name == "**/*.pdf" && physical.RecordCount == 1);
            var serialized = JsonSerializer.Serialize(files);
            Assert.DoesNotContain("sensitive-member-name", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(root, serialized, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PostgreSqlDiscoveryReportsPhysicalTablesViewsKeysAndForeignKeysWithoutCredentials()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE parent(id integer PRIMARY KEY, code varchar(20) UNIQUE); CREATE TABLE child(id integer PRIMARY KEY,parent_id integer NOT NULL REFERENCES parent(id)); CREATE VIEW parent_view AS SELECT id,code FROM parent";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var connector = new PostgresSourceConnector();
        var context = Context("postgres", container.GetConnectionString());
        var first = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        var second = await connector.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.True(PhysicalDiscovery.Verify(first));
        Assert.Contains(first.Objects, item => item.Name == "parent_view" && item.Kind == "view");
        var parent = first.Objects.Single(item => item.Name == "parent");
        Assert.Contains(parent.Keys, key => key.Primary && key.Fields.SequenceEqual(["id"]));
        Assert.Contains(parent.Keys, key => !key.Primary && key.Fields.SequenceEqual(["code"]));
        Assert.Contains(first.Objects.Single(item => item.Name == "child").Relationships,
            relationship => relationship.TargetObject == "parent" && relationship.Fields.SequenceEqual(["parent_id"]));
        Assert.DoesNotContain(container.GetConnectionString(), JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlServerDiscoveryReportsPhysicalTablesViewsKeysAndForeignKeysWithoutSourceWrites()
    {
        await using var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE dbo.parent(id int PRIMARY KEY,code nvarchar(20) UNIQUE); CREATE TABLE dbo.child(id int PRIMARY KEY,parent_id int NOT NULL REFERENCES dbo.parent(id));";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "CREATE VIEW dbo.parent_view AS SELECT id,code FROM dbo.parent";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var connector = new SqlServerSourceConnector();
        var first = await connector.DiscoverAsync(Context("sqlserver", container.GetConnectionString()), [], TestContext.Current.CancellationToken);
        Assert.True(PhysicalDiscovery.Verify(first));
        Assert.Contains(first.Objects, item => item.Name == "parent_view" && item.Kind == "view");
        Assert.Contains(first.Objects.Single(item => item.Name == "parent").Keys, key => key.Primary && key.Fields.SequenceEqual(["id"]));
        Assert.Contains(first.Objects.Single(item => item.Name == "parent").Keys, key => !key.Primary && key.Fields.SequenceEqual(["code"]));
        Assert.Contains(first.Objects.Single(item => item.Name == "child").Relationships, relationship => relationship.TargetObject == "parent");
        command.CommandText = "SELECT COUNT(*) FROM dbo.parent";
        Assert.Equal(0, (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        Assert.DoesNotContain(container.GetConnectionString(), JsonSerializer.Serialize(first), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryFingerprintExcludesObservationCountsAndIdentityButDetectsStructureDrift()
    {
        var context = Context("postgres", "synthetic-not-resolved");
        var physical = new PhysicalObject("public", "records", "table", [new("id", "integer", false, 1)], [], [], 1, 10);
        var first = PhysicalDiscovery.Create(context, "postgres", "0.1.0", [physical], DateTimeOffset.UnixEpoch);
        var later = PhysicalDiscovery.Create(context, "postgres", "0.1.0", [physical with { RecordCount = 500, Bytes = 3000 }], DateTimeOffset.UtcNow);
        Assert.Equal(first.Fingerprint, later.Fingerprint);
        var changed = PhysicalDiscovery.Create(context, "postgres", "0.1.0",
            [physical with { Fields = [new("id", "bigint", false, 1)] }]);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.False(PhysicalDiscovery.Verify(first with { Fingerprint = changed.Fingerprint }));
    }

    [Fact]
    public async Task DiscoveryStoreBindsManifestAndDiffDetectsFieldsKeysAndRelationships()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-discovery-store-{Guid.NewGuid():N}");
        try
        {
            var context = Context("postgres", "credential-never-persisted");
            var original = new PhysicalObject("public", "records", "table",
                [new("id", "integer", false, 1), new("removed", "text", true, 2)],
                [new("pk", true, ["id"])], []);
            var before = PhysicalDiscovery.Create(context, "postgres", "0.1.0", [original]);
            await PhysicalDiscoveryStore.SaveAsync(root, before, TestContext.Current.CancellationToken);
            Assert.Equal(before.Fingerprint, (await PhysicalDiscoveryStore.ReadAsync(root, TestContext.Current.CancellationToken)).Fingerprint);
            var changed = original with
            {
                Fields = [new("id", "bigint", true, 1), new("added", "text", true, 2)],
                Keys = [new("new-pk", true, ["added"]), new("uq", false, ["id"])],
                Relationships = [new("fk", ["id"], "public", "other", ["id"])]
            };
            var after = PhysicalDiscovery.Create(context, "postgres", "0.1.0", [changed]);
            var changes = PhysicalDiscoveryDiff.Compare(before, after);
            Assert.Contains(changes, change => change.Kind == "field-type-changed");
            Assert.Contains(changes, change => change.Kind == "field-nullability-changed");
            Assert.Contains(changes, change => change.Kind == "field-added");
            Assert.Contains(changes, change => change.Kind == "field-removed");
            Assert.Contains(changes, change => change.Kind == "primary-key-changed");
            Assert.Contains(changes, change => change.Kind == "unique-constraint-changed");
            Assert.Contains(changes, change => change.Kind == "foreign-key-changed");
            await File.AppendAllTextAsync(Path.Combine(root, "physical-model.json"), " ", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => PhysicalDiscoveryStore.ReadAsync(root, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ConnectorContext Context(string connector, string connection) => new("logical-source", "records",
        new ConnectorId(connector), "discovery", "Physical.Uninterpreted", new RuntimeConfiguration(
            [new KeyValuePair<string, RuntimeSetting>("connection", RuntimeSetting.FromRuntimeValue(connection, isSecret: true))]));
}