#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package Microsoft.Data.Sqlite@10.0.12

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
    throw new ArgumentException("Provide the preserved working-set.sqlite path.");

await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.GetFullPath(args[0]),
    Mode = SqliteOpenMode.ReadOnly,
    Pooling = false
}.ToString());
await connection.OpenAsync();
connection.CreateCollation("DECIMAL_ORDER", (left, right) =>
    decimal.Parse(left!, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(right!, CultureInfo.InvariantCulture)));

var predicates = new Dictionary<string, string>
{
    ["original"] = "NOT EXISTS (SELECT 1 FROM expected_targets e JOIN source_artifacts s ON s.artifact_id=e.source_id AND s.node_key=e.source_node_key WHERE e.node_key=a.node_key AND e.identity_hash=a.identity_hash)",
    ["candidate"] = "NOT EXISTS (SELECT 1 FROM expected_targets e INDEXED BY expected_identity_idx JOIN source_artifacts s ON s.artifact_id=e.source_id AND s.node_key=e.source_node_key WHERE e.node_key=a.node_key AND e.identity_hash=a.identity_hash)"
};

foreach (var (name, predicate) in predicates)
{
    await using var explain = connection.CreateCommand();
    explain.CommandText = $"EXPLAIN QUERY PLAN SELECT a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash,COUNT(*) FROM actual_targets a WHERE {predicate} GROUP BY a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash ORDER BY a.node_key,a.identity_hash";
    var plan = new List<string>();
    await using (var reader = await explain.ExecuteReaderAsync())
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
    Console.WriteLine(JsonSerializer.Serialize(new { name, plan }));

    foreach (var limit in name == "candidate" ? new[] { 10, 100, -1 } : new[] { 10, 100 })
    {
        await using var command = connection.CreateCommand();
        command.CommandText = limit < 0
            ? $"SELECT COUNT(*) FROM actual_targets a WHERE {predicate}"
            : $"SELECT COUNT(*) FROM (SELECT * FROM actual_targets LIMIT {limit}) a WHERE {predicate}";
        var stopwatch = Stopwatch.StartNew();
        var uncovered = await command.ExecuteScalarAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { name, limit, uncovered, elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds }));
    }
}