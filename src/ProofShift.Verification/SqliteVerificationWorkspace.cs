using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Snapshots;

namespace ProofShift.Verification;

public sealed class SqliteVerificationWorkspace : IVerificationWorkspace
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public long SourceArtifactCount { get; private set; }
    public long ExpectedTargetCount { get; private set; }
    public long ActualTargetCount { get; private set; }

    private SqliteVerificationWorkspace(string directory, string databasePath, SqliteConnection connection)
    {
        _directory = directory;
        _databasePath = databasePath;
        _connection = connection;
    }

    public static async Task<SqliteVerificationWorkspace> CreateAsync(string temporaryRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        var directory = Path.Combine(Path.GetFullPath(temporaryRoot), $"proofshift-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "working-set.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                CREATE TABLE source_artifacts (
                    artifact_id TEXT NOT NULL,
                    node_key TEXT NOT NULL,
                    system_id TEXT NOT NULL,
                    endpoint_id TEXT NOT NULL,
                    artifact_type TEXT NOT NULL,
                    identity_hash TEXT NOT NULL,
                    semantic_type TEXT NOT NULL,
                    record_hash TEXT NOT NULL,
                    record_json TEXT NOT NULL,
                    PRIMARY KEY(node_key, artifact_id)
                );
                CREATE TABLE expected_targets (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    node_key TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    target_system TEXT NOT NULL,
                    target_endpoint TEXT NOT NULL,
                    target_type TEXT NOT NULL,
                    identity_hash TEXT NOT NULL,
                    semantic_type TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    source_node_key TEXT NOT NULL,
                    edge_id TEXT NOT NULL,
                    edge_name TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    record_json TEXT NOT NULL
                );
                CREATE INDEX expected_identity_idx ON expected_targets(node_key, identity_hash);
                CREATE TABLE expected_values (
                    expected_seq INTEGER NOT NULL,
                    field TEXT NOT NULL,
                    value_hash TEXT NOT NULL,
                    PRIMARY KEY(expected_seq, field)
                );
                CREATE TABLE journal_entries (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    result TEXT NOT NULL,
                    target_node TEXT,
                    target_id TEXT,
                    target_system TEXT,
                    target_endpoint TEXT,
                    target_type TEXT,
                    target_identity_hash TEXT,
                    edge_id TEXT NOT NULL,
                    edge_name TEXT NOT NULL,
                    edge_version TEXT NOT NULL,
                    failure_code TEXT
                );
                CREATE INDEX journal_target_idx ON journal_entries(result, target_node, target_identity_hash);
                CREATE TABLE journal_sources (
                    journal_seq INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    source_node_key TEXT NOT NULL,
                    PRIMARY KEY(journal_seq, source_node_key, source_id)
                );
                CREATE INDEX journal_source_idx ON journal_sources(source_id);
                CREATE TABLE actual_targets (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    node_key TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    target_system TEXT NOT NULL,
                    target_endpoint TEXT NOT NULL,
                    target_type TEXT NOT NULL,
                    identity_hash TEXT NOT NULL,
                    semantic_type TEXT NOT NULL,
                    record_hash TEXT NOT NULL,
                    record_json TEXT NOT NULL
                );
                CREATE INDEX actual_identity_idx ON actual_targets(node_key, identity_hash);
                CREATE TABLE actual_values (
                    target_seq INTEGER NOT NULL,
                    field TEXT NOT NULL,
                    value_hash TEXT NOT NULL,
                    PRIMARY KEY(target_seq, field)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteVerificationWorkspace(directory, path, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async Task AddSourceArtifactAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO source_artifacts (artifact_id,node_key,system_id,endpoint_id,artifact_type,identity_hash,semantic_type,record_hash,record_json) VALUES ($id,$node,$system,$endpoint,$type,$identity,$semantic,$record,$recordJson) ON CONFLICT(node_key,artifact_id) DO NOTHING";
        AddArtifactParameters(command, record.Artifact, nodeKey);
        command.Parameters.AddWithValue("$semantic", record.SemanticType);
        command.Parameters.AddWithValue("$record", SnapshotFingerprints.RecordFingerprint(record));
        command.Parameters.AddWithValue("$recordJson", VerificationArtifactRecordCodec.Encode(record));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0) SourceArtifactCount++;
    }

    public async Task<bool> ContainsSourceArtifactAsync(string nodeKey, ArtifactReference artifact, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM source_artifacts WHERE node_key=$node AND artifact_id=$id AND system_id=$system AND endpoint_id=$endpoint AND artifact_type=$type AND identity_hash=$identity LIMIT 1";
        command.Parameters.AddWithValue("$node", nodeKey);
        command.Parameters.AddWithValue("$id", artifact.Id.Value);
        command.Parameters.AddWithValue("$system", artifact.SystemId.Value);
        command.Parameters.AddWithValue("$endpoint", artifact.EndpointId.Value);
        command.Parameters.AddWithValue("$type", artifact.ArtifactType);
        command.Parameters.AddWithValue("$identity", IdentityHash(artifact.Identity));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task AddExpectedTargetAsync(string nodeKey, RecordEnvelope expected, string sourceNodeKey, RecordEnvelope source,
        MigrationEdge edge, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var transaction = _connection.BeginTransaction();
        await using var targetCommand = _connection.CreateCommand();
        targetCommand.Transaction = (SqliteTransaction)transaction;
        targetCommand.CommandText = "INSERT INTO expected_targets (node_key,target_id,target_system,target_endpoint,target_type,identity_hash,semantic_type,source_id,source_node_key,edge_id,edge_name,operation,record_json) VALUES ($node,$id,$system,$endpoint,$type,$identity,$semantic,$source,$sourceNode,$edge,$edgeName,$operation,$recordJson)";
        AddArtifactParameters(targetCommand, expected.Artifact, nodeKey, "$target");
        targetCommand.Parameters.AddWithValue("$semantic", expected.SemanticType);
        targetCommand.Parameters.AddWithValue("$source", source.Artifact.Id.Value);
        targetCommand.Parameters.AddWithValue("$sourceNode", sourceNodeKey);
        targetCommand.Parameters.AddWithValue("$edge", edge.Id.Value.ToString("D", CultureInfo.InvariantCulture));
        targetCommand.Parameters.AddWithValue("$edgeName", edge.Name);
        targetCommand.Parameters.AddWithValue("$operation", edge.Operation.Type.ToString());
        targetCommand.Parameters.AddWithValue("$recordJson", VerificationArtifactRecordCodec.Encode(expected));
        await targetCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var expectedSequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        foreach (var pair in expected.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await using var valueCommand = _connection.CreateCommand();
            valueCommand.Transaction = (SqliteTransaction)transaction;
            valueCommand.CommandText = "INSERT INTO expected_values (expected_seq,field,value_hash) VALUES ($seq,$field,$hash)";
            valueCommand.Parameters.AddWithValue("$seq", expectedSequence);
            valueCommand.Parameters.AddWithValue("$field", pair.Key);
            valueCommand.Parameters.AddWithValue("$hash", FingerprintValue(pair.Value));
            await valueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ExpectedTargetCount++;
    }

    public async Task<bool> ContainsExpectedTargetAsync(string nodeKey, ArtifactReference target, string sourceNodeKey,
        ArtifactReference source, MigrationEdgeId edgeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM expected_targets WHERE node_key=$node AND target_id=$target AND target_system=$system AND target_endpoint=$endpoint AND target_type=$type AND identity_hash=$identity AND source_node_key=$sourceNode AND source_id=$source AND edge_id=$edge LIMIT 1";
        command.Parameters.AddWithValue("$node", nodeKey);
        command.Parameters.AddWithValue("$target", target.Id.Value);
        command.Parameters.AddWithValue("$system", target.SystemId.Value);
        command.Parameters.AddWithValue("$endpoint", target.EndpointId.Value);
        command.Parameters.AddWithValue("$type", target.ArtifactType);
        command.Parameters.AddWithValue("$identity", IdentityHash(target.Identity));
        command.Parameters.AddWithValue("$sourceNode", sourceNodeKey);
        command.Parameters.AddWithValue("$source", source.Id.Value);
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task AddJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var transaction = _connection.BeginTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO journal_entries (result,target_node,target_id,target_system,target_endpoint,target_type,target_identity_hash,edge_id,edge_name,edge_version,failure_code) VALUES ($result,$node,$id,$system,$endpoint,$type,$identity,$edge,$edgeName,$edgeVersion,$failure)";
        command.Parameters.AddWithValue("$result", entry.Result);
        command.Parameters.AddWithValue("$node", (object?)entry.TargetNode ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)entry.Target?.Id.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$system", (object?)entry.Target?.SystemId.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$endpoint", (object?)entry.Target?.EndpointId.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (object?)entry.Target?.ArtifactType ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", entry.Target is null ? DBNull.Value : IdentityHash(entry.Target.Identity));
        command.Parameters.AddWithValue("$edge", entry.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$edgeName", entry.EdgeName);
        command.Parameters.AddWithValue("$edgeVersion", entry.EdgeVersion);
        command.Parameters.AddWithValue("$failure", (object?)entry.FailureCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        foreach (var source in entry.Sources)
        {
            await using var sourceCommand = _connection.CreateCommand();
            sourceCommand.Transaction = (SqliteTransaction)transaction;
            sourceCommand.CommandText = "INSERT OR IGNORE INTO journal_sources (journal_seq,source_id,source_node_key) VALUES ($seq,$source,$sourceNode)";
            sourceCommand.Parameters.AddWithValue("$seq", sequence);
            sourceCommand.Parameters.AddWithValue("$source", source.Artifact.Id.Value);
            sourceCommand.Parameters.AddWithValue("$sourceNode", source.NodeKey);
            await sourceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddTargetObservationAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var targetCommand = _connection.CreateCommand();
        targetCommand.Transaction = (SqliteTransaction)transaction;
        targetCommand.CommandText = "INSERT INTO actual_targets (node_key,target_id,target_system,target_endpoint,target_type,identity_hash,semantic_type,record_hash,record_json) VALUES ($node,$id,$system,$endpoint,$type,$identity,$semantic,$record,$recordJson)";
        AddArtifactParameters(targetCommand, record.Artifact, nodeKey, "$target");
        targetCommand.Parameters.AddWithValue("$semantic", record.SemanticType);
        targetCommand.Parameters.AddWithValue("$record", SnapshotFingerprints.RecordFingerprint(record));
        targetCommand.Parameters.AddWithValue("$recordJson", VerificationArtifactRecordCodec.Encode(record));
        await targetCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var sequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        foreach (var pair in record.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await using var valueCommand = _connection.CreateCommand();
            valueCommand.Transaction = (SqliteTransaction)transaction;
            valueCommand.CommandText = "INSERT INTO actual_values (target_seq,field,value_hash) VALUES ($seq,$field,$hash)";
            valueCommand.Parameters.AddWithValue("$seq", sequence);
            valueCommand.Parameters.AddWithValue("$field", pair.Key);
            valueCommand.Parameters.AddWithValue("$hash", FingerprintValue(pair.Value));
            await valueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ActualTargetCount++;
    }

    public async IAsyncEnumerable<VerificationSourceFact> ReadSourceFactsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.artifact_id,s.node_key,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,s.semantic_type,
                   SUM(CASE WHEN j.result='produced' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.result='excluded' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN j.result='failed' THEN 1 ELSE 0 END),
                   group_concat(DISTINCT CASE WHEN j.result='produced' THEN j.target_id END),
                   group_concat(DISTINCT CASE WHEN j.result IN ('produced','excluded','failed') THEN j.edge_id END)
            FROM source_artifacts s
            LEFT JOIN journal_sources js ON js.source_id=s.artifact_id AND js.source_node_key=s.node_key
            LEFT JOIN journal_entries j ON j.seq=js.journal_seq
            GROUP BY s.artifact_id,s.node_key,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,s.semantic_type
            ORDER BY s.node_key,s.artifact_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationSourceFact(
                reader.GetString(1), reader.GetString(6), MakeArtifact(reader.GetString(0), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.IsDBNull(7) ? 0 : reader.GetInt32(7), reader.IsDBNull(8) ? 0 : reader.GetInt32(8), reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                SplitIds(reader.IsDBNull(10) ? null : reader.GetString(10)).Select(value => new ArtifactId(value)).ToArray(),
                SplitIds(reader.IsDBNull(11) ? null : reader.GetString(11)).Select(value => new MigrationEdgeId(Guid.Parse(value))).ToArray());
        }
    }

    public IAsyncEnumerable<VerificationTargetFact> ReadMaterializedJournalTargetsAsync(CancellationToken cancellationToken) =>
        ReadJournalTargetFactsAsync(requiredActualCount: null, cancellationToken);

    public async IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsAsync(VerificationArtifactRole role,
        string? nodeKey, string? semanticType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyCollection<string>? orderByFields = null)
    {
        ThrowIfDisposed();
        var (table, idColumn, sequenceColumn) = role switch
        {
            VerificationArtifactRole.Source => ("source_artifacts", "artifact_id", "rowid"),
            VerificationArtifactRole.ExpectedTarget => ("expected_targets", "target_id", "seq"),
            VerificationArtifactRole.ActualTarget => ("actual_targets", "target_id", "seq"),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var filters = new List<string>();
        var ordering = new List<string> { "t.node_key" };
        await using var command = _connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(nodeKey))
        {
            filters.Add("t.node_key=$node");
            command.Parameters.AddWithValue("$node", nodeKey.Trim());
        }
        if (!string.IsNullOrWhiteSpace(semanticType))
        {
            filters.Add("t.semantic_type=$semantic");
            command.Parameters.AddWithValue("$semantic", semanticType.Trim());
        }
        var orderIndex = 0;
        foreach (var field in orderByFields ?? [])
        {
            if (string.IsNullOrWhiteSpace(field)) throw new ArgumentException("Ordering fields must not be empty.", nameof(orderByFields));
            var parameter = $"$orderField{orderIndex}";
            command.Parameters.AddWithValue(parameter, field.Trim());
            ordering.Add($"(SELECT COALESCE(json_extract(value, '$.value.text'), CAST(json_extract(value, '$.value.integer') AS TEXT), CASE json_extract(value, '$.value.boolean') WHEN 1 THEN 'true' WHEN 0 THEN 'false' ELSE '' END, '') FROM json_each(t.record_json, '$.values') WHERE json_extract(value, '$.name')={parameter} LIMIT 1)");
            orderIndex++;
        }
        ordering.Add($"t.identity_hash");
        ordering.Add($"t.{idColumn}");
        command.CommandText = $"SELECT t.node_key,t.semantic_type,t.record_json FROM {table} AS t{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))} ORDER BY {string.Join(',', ordering)}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return VerificationArtifactRecordCodec.Decode(reader.GetString(2), reader.GetString(0), role,
                reader.GetString(1));
        }
    }

    public IAsyncEnumerable<VerificationTargetFact> ReadMissingTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadJournalTargetFactsAsync(requiredActualCount: 0, cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadActualTargetsAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("1=1", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("NOT EXISTS (SELECT 1 FROM journal_entries j WHERE j.result='produced' AND j.target_node=a.node_key AND j.target_identity_hash=a.identity_hash)", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadDuplicateTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadDuplicateFactsAsync(cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutLineageAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("NOT EXISTS (SELECT 1 FROM journal_entries j JOIN journal_sources js ON js.journal_seq=j.seq WHERE j.result='produced' AND j.target_node=a.node_key AND j.target_identity_hash=a.identity_hash)", cancellationToken);

    public async IAsyncEnumerable<VerificationAttributeComparison> ReadAttributeComparisonsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.node_key,s.node_key,s.semantic_type,s.artifact_id,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,
                   e.target_id,e.target_system,e.target_endpoint,e.target_type,e.identity_hash,
                   a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash,
                   e.edge_id,e.edge_name,ev.field,ev.value_hash,av.value_hash
            FROM expected_targets e
            JOIN source_artifacts s ON s.artifact_id=e.source_id AND s.node_key=e.source_node_key
            JOIN expected_values ev ON ev.expected_seq=e.seq
            JOIN actual_targets a ON a.node_key=e.node_key AND a.identity_hash=e.identity_hash
            JOIN actual_values av ON av.target_seq=a.seq AND av.field=ev.field
            WHERE (SELECT COUNT(*) FROM actual_targets d WHERE d.node_key=a.node_key AND d.identity_hash=a.identity_hash)=1
              AND EXISTS (SELECT 1 FROM journal_entries j JOIN journal_sources js ON js.journal_seq=j.seq
                                                    WHERE j.result='produced' AND j.target_id=e.target_id AND j.edge_id=e.edge_id
                                                        AND js.source_id=e.source_id AND js.source_node_key=e.source_node_key)
            ORDER BY e.node_key,e.identity_hash,e.edge_id,ev.field
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedHash = reader.GetString(21);
            var actualHash = reader.GetString(22);
            yield return new VerificationAttributeComparison(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                MakeArtifact(reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)),
                MakeArtifact(reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)),
                MakeArtifact(reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetString(17)),
                new MigrationEdgeId(Guid.Parse(reader.GetString(18))), reader.GetString(19), reader.GetString(20),
                expectedHash, actualHash, expectedHash == actualHash);
        }
    }

    public async IAsyncEnumerable<LineageRecord> ReadLineageAsync(string graphHash,
        IReadOnlyDictionary<string, MigrationNodeId> graphNodeIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT j.target_id,j.target_system,j.target_endpoint,j.target_type,j.target_identity_hash,
                     s.artifact_id,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,j.edge_id,j.target_node,s.node_key
            FROM journal_entries j
            JOIN journal_sources js ON js.journal_seq=j.seq
            JOIN source_artifacts s ON s.artifact_id=js.source_id AND s.node_key=js.source_node_key
            WHERE j.result='produced' AND j.target_id IS NOT NULL
            ORDER BY j.target_node,j.target_id,s.artifact_id,j.edge_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        (string NodeKey, string TargetId)? currentTarget = null;
        string? currentTargetNode = null;
        ArtifactReference? target = null;
        var sources = new SortedDictionary<string, (ArtifactReference Artifact, MigrationNodeId NodeId)>(StringComparer.Ordinal);
        var edges = new SortedSet<MigrationEdgeId>(Comparer<MigrationEdgeId>.Create((left, right) => left.Value.CompareTo(right.Value)));
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetId = reader.GetString(0);
            var targetNode = reader.GetString(11);
            if (currentTarget is not null && (targetNode != currentTarget.Value.NodeKey || targetId != currentTarget.Value.TargetId))
            {
                yield return new LineageRecord(target!, sources.Values.Select(source => source.Artifact), edges,
                    graphHash, graphNodeIds[currentTargetNode!], sources.Values.Select(source => source.NodeId));
                sources.Clear();
                edges.Clear();
            }

            currentTarget = (targetNode, targetId);
            currentTargetNode = targetNode;
            target = MakeArtifact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            var sourceId = reader.GetString(5);
            var sourceNodeKey = reader.GetString(12);
            sources.TryAdd($"{sourceNodeKey}\0{sourceId}", (MakeArtifact(sourceId, reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)),
                graphNodeIds[sourceNodeKey]));
            edges.Add(new MigrationEdgeId(Guid.Parse(reader.GetString(10))));
        }

        if (currentTarget is not null)
            yield return new LineageRecord(target!, sources.Values.Select(source => source.Artifact), edges,
                graphHash, graphNodeIds[currentTargetNode!], sources.Values.Select(source => source.NodeId));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
        TryDeleteDirectory(_directory);
    }

    private async IAsyncEnumerable<VerificationTargetFact> ReadJournalTargetFactsAsync(int? requiredActualCount,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT j.target_node,j.target_id,j.target_system,j.target_endpoint,j.target_type,j.target_identity_hash,
                     COALESCE((SELECT e.semantic_type FROM expected_targets e WHERE e.node_key=j.target_node AND e.target_id=j.target_id LIMIT 1),'') AS semantic_type,
                     (SELECT COUNT(*) FROM actual_targets a WHERE a.node_key=j.target_node AND a.identity_hash=j.target_identity_hash) AS actual_count,
                     s.node_key,s.artifact_id,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,j.edge_id
            FROM journal_entries j
            JOIN journal_sources js ON js.journal_seq=j.seq
            JOIN source_artifacts s ON s.artifact_id=js.source_id AND s.node_key=js.source_node_key
            WHERE j.result='produced' AND j.target_id IS NOT NULL
            ORDER BY j.target_node,j.target_identity_hash,j.target_id,s.artifact_id,j.edge_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        (string NodeKey, string TargetId, string IdentityHash)? currentKey = null;
        string? nodeKey = null;
        string? targetId = null;
        string? targetSystem = null;
        string? targetEndpoint = null;
        string? targetType = null;
        string? identityHash = null;
        string? semanticType = null;
        var actualCount = 0;
        var sources = new Dictionary<string, VerificationGraphArtifact>(StringComparer.Ordinal);
        var edges = new HashSet<MigrationEdgeId>();

        async IAsyncEnumerable<VerificationTargetFact> FlushAsync()
        {
            if (currentKey is null || (requiredActualCount is not null && actualCount != requiredActualCount.Value)) yield break;
            yield return new VerificationTargetFact(nodeKey!, semanticType!,
                MakeArtifact(targetId!, targetSystem!, targetEndpoint!, targetType!, identityHash!), actualCount,
                sources.Values.ToArray(), edges.OrderBy(edge => edge.Value).ToArray());
            await Task.CompletedTask;
        }

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowKey = (reader.GetString(0), reader.GetString(1), reader.GetString(5));
            if (currentKey is not null && currentKey.Value != rowKey)
            {
                await foreach (var fact in FlushAsync().ConfigureAwait(false)) yield return fact;
                sources.Clear();
                edges.Clear();
            }

            if (currentKey is null || currentKey.Value != rowKey)
            {
                currentKey = rowKey;
                nodeKey = rowKey.Item1;
                targetId = rowKey.Item2;
                targetSystem = reader.GetString(2);
                targetEndpoint = reader.GetString(3);
                targetType = reader.GetString(4);
                identityHash = rowKey.Item3;
                semanticType = reader.GetString(6);
                actualCount = reader.GetInt32(7);
            }

            var sourceNodeKey = reader.GetString(8);
            var sourceId = reader.GetString(9);
            sources.TryAdd($"{sourceNodeKey}\0{sourceId}", new VerificationGraphArtifact(sourceNodeKey,
                MakeArtifact(sourceId, reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13))));
            edges.Add(new MigrationEdgeId(Guid.Parse(reader.GetString(14))));
        }

        await foreach (var fact in FlushAsync().ConfigureAwait(false)) yield return fact;
    }

    private async IAsyncEnumerable<VerificationTargetFact> ReadActualTargetFactsAsync(string condition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash,COUNT(*)
            FROM actual_targets a
            WHERE {condition}
            GROUP BY a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash
            ORDER BY a.node_key,a.identity_hash
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationTargetFact(reader.GetString(0), reader.GetString(1),
                MakeArtifact(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                reader.GetInt32(7), [], []);
        }
    }

    private async IAsyncEnumerable<VerificationTargetFact> ReadDuplicateFactsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT node_key,semantic_type,MIN(target_id),MIN(target_system),MIN(target_endpoint),MIN(target_type),identity_hash,COUNT(*)
            FROM actual_targets GROUP BY node_key,semantic_type,identity_hash
            HAVING COUNT(*)>1 ORDER BY node_key,identity_hash
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationTargetFact(reader.GetString(0), reader.GetString(1),
                MakeArtifact(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                reader.GetInt32(7), [], []);
        }
    }

    private static void AddArtifactParameters(SqliteCommand command, ArtifactReference artifact, string nodeKey, string prefix = "$source")
    {
        command.Parameters.AddWithValue(prefix == "$source" ? "$node" : "$node", nodeKey);
        command.Parameters.AddWithValue(prefix == "$source" ? "$id" : "$id", artifact.Id.Value);
        command.Parameters.AddWithValue(prefix == "$source" ? "$system" : "$system", artifact.SystemId.Value);
        command.Parameters.AddWithValue(prefix == "$source" ? "$endpoint" : "$endpoint", artifact.EndpointId.Value);
        command.Parameters.AddWithValue(prefix == "$source" ? "$type" : "$type", artifact.ArtifactType);
        command.Parameters.AddWithValue(prefix == "$source" ? "$identity" : "$identity", IdentityHash(artifact.Identity));
    }

    private static ArtifactReference MakeArtifact(string id, string system, string endpoint, string type, string identityHash) =>
        new(new ArtifactId(id), new SystemId(system), new StorageEndpointId(endpoint), type, $"sha256:{identityHash}");

    private static string[] SplitIds(string? value) => string.IsNullOrEmpty(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string IdentityHash(string identity) => Hash(Encoding.UTF8.GetBytes(identity));

    private static string FingerprintValue(ValueNode value) => Hash(Encoding.UTF8.GetBytes(GraphTargetIdentity.CanonicalValue(value)));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task<long> ReadLastInsertRowIdAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SQLite did not return a row ID."));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch { }
    }
}
