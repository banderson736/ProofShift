using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Verification;

public interface IVerificationLedgerStore : IAsyncDisposable
{
    VerificationLedgerStoreReceipt Receipt { get; }
    Task RegisterGraphNodeAsync(string nodeKey, MigrationNodeId nodeId, CancellationToken cancellationToken);
    Task RegisterSourceAsync(string nodeKey, MigrationNodeId nodeId, ArtifactReference artifact, CancellationToken cancellationToken);
    Task RegisterTargetAsync(string nodeKey, MigrationNodeId nodeId, ArtifactReference artifact, CancellationToken cancellationToken);
    Task AppendDispositionAsync(ArtifactDispositionRecord disposition, CancellationToken cancellationToken);
    Task AppendLineageAsync(LineageRecord lineage, CancellationToken cancellationToken);
    Task AppendJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken);
    Task<VerificationLedgerSummary> CompleteAsync(CancellationToken cancellationToken);
    Task<VerificationLedgerCoverageResult> ValidateCoverageAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ArtifactDispositionRecord> ReadDispositionsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<LineageRecord> ReadLineageAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationSourceLineageTargets> ReadLineageTargetsBySourceAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationJournalEntry> ReadJournalEntriesAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationJournalEntry> ReadJournalEntriesForEdgeAsync(MigrationEdgeId edgeId, CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationJournalScope> ReadJournalScopeAsync(MigrationEdgeId edgeId, bool targets, CancellationToken cancellationToken);
    IAsyncEnumerable<VerificationJournalScope> ReadDistinctJournalScopeAsync(MigrationEdgeId edgeId, CancellationToken cancellationToken);
    Task<VerificationJournalScopeCounts> ReadJournalScopeCountsAsync(MigrationEdgeId edgeId, CancellationToken cancellationToken);
    Task<ArtifactDispositionRecord?> FindDispositionAsync(MigrationNodeId sourceNodeId, ArtifactId sourceId, CancellationToken cancellationToken);
    IAsyncEnumerable<LineageRecord> FindLineageBySourceAsync(MigrationNodeId sourceNodeId, ArtifactId sourceId, CancellationToken cancellationToken);
    IAsyncEnumerable<LineageRecord> FindLineageByTargetAsync(MigrationNodeId targetNodeId, ArtifactId targetId, CancellationToken cancellationToken);
    Task<bool> HasJournalEntryForEdgeAsync(MigrationEdgeId edgeId, string result, CancellationToken cancellationToken);
    Task<VerificationLedgerSummary> ReadSummaryAsync(CancellationToken cancellationToken);
}

public sealed record VerificationLedgerStoreReceipt(Guid StoreId, string RelativePath, string Fingerprint,
    long SourceCount, long TargetCount, long DispositionCount, long LineageCount, long JournalEntryCount);

public sealed record VerificationLedgerSummary(long SourceCount, long TargetCount, long DispositionCount,
    long UnaccountedDispositionCount, long LineageCount, long JournalEntryCount, string Fingerprint);

public sealed record VerificationLedgerCoverageResult(long MissingDispositions, long UnexpectedDispositions,
    long UnaccountedDispositions, long MissingLineage, long UnexpectedLineage, long UnboundLineageSources)
{
    public bool IsValid => MissingDispositions == 0 && UnexpectedDispositions == 0 && UnaccountedDispositions == 0 &&
        MissingLineage == 0 && UnexpectedLineage == 0 && UnboundLineageSources == 0;
}

public sealed record VerificationJournalScope(string NodeKey, MigrationNodeId NodeId, ArtifactId ArtifactId);
public sealed record VerificationJournalScopeCounts(long SourceCount, long TargetCount);
public sealed record VerificationLineageTarget(MigrationNodeId TargetNodeId, ArtifactReference Target);
public sealed record VerificationSourceLineageTargets(string SourceNodeKey, MigrationNodeId SourceNodeId,
    ArtifactId SourceId, IReadOnlyCollection<VerificationLineageTarget> Targets);

public sealed class SqliteVerificationLedgerStore : IVerificationLedgerStore
{
    private const int WriteBatchSize = 128;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private readonly PerformanceRecorder? _performanceRecorder;
    private SqliteTransaction? _writeTransaction;
    private int _pendingWrites;
    private long _writeMicroseconds;
    private long _writeOperations;
    private bool _completed;
    private bool _disposed;
    private VerificationLedgerStoreReceipt? _receipt;
    private VerificationLedgerSummary? _verifiedSummary;

    public VerificationLedgerStoreReceipt Receipt => _receipt ??
        throw new InvalidOperationException("Verification ledger has not been completed.");

    private SqliteVerificationLedgerStore(string root, string directory, string databasePath, SqliteConnection connection,
        PerformanceRecorder? performanceRecorder = null)
    {
        _root = root;
        _directory = directory;
        _databasePath = databasePath;
        _connection = connection;
        _performanceRecorder = performanceRecorder;
    }

    public static async Task<SqliteVerificationLedgerStore> CreateAsync(string projectDirectory, RunId runId,
        CancellationToken cancellationToken, PerformanceRecorder? performanceRecorder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (runId.Value == Guid.Empty) throw new ArgumentException("Verification run ID must not be empty.", nameof(runId));
        var root = Path.GetFullPath(Path.Combine(projectDirectory, ".proofshift", "verification-ledgers"));
        var directory = Path.Combine(root, runId.Value.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "ledger.sqlite");
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
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE graph_nodes (
                    node_key TEXT PRIMARY KEY,
                    node_id TEXT NOT NULL UNIQUE
                );
                CREATE TABLE sources (
                    node_id TEXT NOT NULL,
                    node_key TEXT NOT NULL,
                    artifact_id TEXT NOT NULL,
                    system_id TEXT NOT NULL,
                    endpoint_id TEXT NOT NULL,
                    artifact_type TEXT NOT NULL,
                    PRIMARY KEY(node_id, artifact_id)
                );
                CREATE INDEX sources_by_artifact_idx ON sources(artifact_id, node_id);
                CREATE TABLE targets (
                    node_id TEXT NOT NULL,
                    node_key TEXT NOT NULL,
                    artifact_id TEXT NOT NULL,
                    system_id TEXT NOT NULL,
                    endpoint_id TEXT NOT NULL,
                    artifact_type TEXT NOT NULL,
                    PRIMARY KEY(node_id, artifact_id)
                );
                CREATE INDEX targets_by_artifact_idx ON targets(artifact_id, node_id);
                CREATE TABLE dispositions (
                    source_node_id TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    disposition TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY(source_node_id, source_id)
                );
                CREATE INDEX dispositions_by_type_idx ON dispositions(disposition, source_node_id, source_id);
                CREATE TABLE lineage (
                    target_node_id TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    target_system TEXT NOT NULL,
                    target_endpoint TEXT NOT NULL,
                    target_type TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY(target_node_id, target_id)
                );
                CREATE TABLE lineage_sources (
                    target_node_id TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    source_node_id TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    PRIMARY KEY(target_node_id, target_id, source_node_id, source_id)
                );
                CREATE INDEX lineage_by_source_idx ON lineage_sources(source_node_id, source_id, target_node_id, target_id);
                CREATE TABLE journal (
                    edge_id TEXT NOT NULL,
                    result TEXT NOT NULL,
                    target_node TEXT,
                    target_id TEXT,
                    payload TEXT NOT NULL
                );
                CREATE INDEX journal_by_edge_idx ON journal(edge_id, result, target_node, target_id);
                CREATE INDEX journal_by_target_idx ON journal(result, target_node, target_id);
                CREATE TABLE journal_scope (
                    edge_id TEXT NOT NULL,
                    is_target INTEGER NOT NULL,
                    node_key TEXT NOT NULL,
                    node_id TEXT NOT NULL,
                    artifact_id TEXT NOT NULL,
                    PRIMARY KEY(edge_id,is_target,node_id,artifact_id)
                );
                CREATE INDEX journal_scope_by_edge_idx ON journal_scope(edge_id,is_target,node_id,artifact_id);
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteVerificationLedgerStore(root, directory, path, connection, performanceRecorder);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public static async Task<SqliteVerificationLedgerStore> OpenAsync(string projectDirectory,
        VerificationLedgerStoreReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(receipt);
        var root = Path.GetFullPath(Path.Combine(projectDirectory, ".proofshift", "verification-ledgers"));
        var expectedRelative = Path.Combine(receipt.StoreId.ToString("N", CultureInfo.InvariantCulture), "ledger.sqlite")
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(receipt.RelativePath, expectedRelative, StringComparison.Ordinal))
            throw new InvalidDataException("Verification ledger receipt path does not match its store identifier.");
        var path = Path.GetFullPath(Path.Combine(root, receipt.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException("Verification ledger path is outside its configured root.");
        var directory = Path.GetDirectoryName(path)!;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var store = new SqliteVerificationLedgerStore(root, directory, path, connection)
            {
                _completed = true,
                _receipt = receipt
            };
            var summary = await store.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            if (summary.Fingerprint != receipt.Fingerprint || summary.SourceCount != receipt.SourceCount ||
                summary.TargetCount != receipt.TargetCount || summary.DispositionCount != receipt.DispositionCount ||
                summary.LineageCount != receipt.LineageCount || summary.JournalEntryCount != receipt.JournalEntryCount)
                throw new InvalidDataException("Verification ledger contents do not match their receipt.");
            store._verifiedSummary = summary;
            return store;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task RegisterSourceAsync(string nodeKey, MigrationNodeId nodeId, ArtifactReference artifact,
        CancellationToken cancellationToken) => RegisterArtifactAsync("sources", nodeKey, nodeId, artifact, cancellationToken);

    public async Task RegisterGraphNodeAsync(string nodeKey, MigrationNodeId nodeId, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO graph_nodes(node_key,node_id) VALUES($key,$id)";
        command.Parameters.AddWithValue("$key", Required(nodeKey, nameof(nodeKey)));
        command.Parameters.AddWithValue("$id", nodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public Task RegisterTargetAsync(string nodeKey, MigrationNodeId nodeId, ArtifactReference artifact,
        CancellationToken cancellationToken) => RegisterArtifactAsync("targets", nodeKey, nodeId, artifact, cancellationToken);

    private async Task RegisterArtifactAsync(string table, string nodeKey, MigrationNodeId nodeId,
        ArtifactReference artifact, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        if (table is not ("sources" or "targets")) throw new ArgumentOutOfRangeException(nameof(table));
        ArgumentNullException.ThrowIfNull(artifact);
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}(node_id,node_key,artifact_id,system_id,endpoint_id,artifact_type) VALUES($nodeId,$nodeKey,$id,$system,$endpoint,$type) ON CONFLICT(node_id,artifact_id) DO NOTHING";
        command.Parameters.AddWithValue("$nodeId", nodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$nodeKey", Required(nodeKey, nameof(nodeKey)));
        AddArtifactParameters(command, artifact);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public async Task AppendDispositionAsync(ArtifactDispositionRecord disposition, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        ArgumentNullException.ThrowIfNull(disposition);
        if (disposition.SourceNodeId is not { } sourceNodeId)
            throw new ArgumentException("Persisted dispositions must be graph scoped.", nameof(disposition));
        var targetNodeIds = disposition.TargetNodeIds;
        if (disposition.Targets.Count != targetNodeIds.Count)
            throw new ArgumentException("Every persisted target reference must have graph-node scope.", nameof(disposition));
        var document = new DispositionDocument(ToDocument(disposition.Source), sourceNodeId.Value.ToString("D", CultureInfo.InvariantCulture),
            disposition.Disposition.ToString(), disposition.Targets.Select((target, index) =>
                new ScopedArtifactDocument(targetNodeIds[index].Value.ToString("D", CultureInfo.InvariantCulture), ToDocument(target))).ToArray(),
            disposition.Reason);
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dispositions(source_node_id,source_id,disposition,payload) VALUES($node,$id,$disposition,$payload)";
        command.Parameters.AddWithValue("$node", document.SourceNodeId);
        command.Parameters.AddWithValue("$id", disposition.Source.Id.Value);
        command.Parameters.AddWithValue("$disposition", document.Disposition);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    private async Task AddJournalScopeAsync(SqliteTransaction transaction, string edgeId, bool target,
        string nodeKey, string artifactId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO journal_scope(edge_id,is_target,node_key,node_id,artifact_id) SELECT $edge,$target,$key,node_id,$artifact FROM graph_nodes WHERE node_key=$key";
        command.Parameters.AddWithValue("$edge", edgeId);
        command.Parameters.AddWithValue("$target", target ? 1 : 0);
        command.Parameters.AddWithValue("$key", nodeKey);
        command.Parameters.AddWithValue("$artifact", artifactId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1 &&
            await GraphNodeExistsAsync(nodeKey, cancellationToken).ConfigureAwait(false) is false)
            throw new InvalidDataException("Verification journal scope references a graph node that was not registered.");
    }

    private async Task<bool> GraphNodeExistsAsync(string nodeKey, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM graph_nodes WHERE node_key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", nodeKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task AppendLineageAsync(LineageRecord lineage, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.TargetNodeId is not { } targetNodeId || lineage.SourceNodeIds.Count != lineage.Sources.Count)
            throw new ArgumentException("Persisted lineage must have graph-node scope for its target and every source.", nameof(lineage));
        var sources = lineage.Sources.Select((source, index) => new ScopedArtifactDocument(
            lineage.SourceNodeIds[index].Value.ToString("D", CultureInfo.InvariantCulture), ToDocument(source))).ToArray();
        Array.Sort(sources, static (left, right) =>
        {
            var node = string.CompareOrdinal(left.NodeId, right.NodeId);
            return node != 0 ? node : string.CompareOrdinal(left.Artifact.Id, right.Artifact.Id);
        });
        var document = new LineageDocument(ToDocument(lineage.Target), targetNodeId.Value.ToString("D", CultureInfo.InvariantCulture),
            sources, lineage.Path.Select(edge => edge.Value.ToString("D", CultureInfo.InvariantCulture)).ToArray(),
            lineage.PlanHash, lineage.Basis.ToString());
        var transaction = GetWriteTransaction();
        await using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO lineage(target_node_id,target_id,target_system,target_endpoint,target_type,payload) VALUES($node,$id,$system,$endpoint,$type,$payload)";
            command.Parameters.AddWithValue("$node", document.TargetNodeId);
            command.Parameters.AddWithValue("$id", lineage.Target.Id.Value);
            command.Parameters.AddWithValue("$system", lineage.Target.SystemId.Value);
            command.Parameters.AddWithValue("$endpoint", lineage.Target.EndpointId.Value);
            command.Parameters.AddWithValue("$type", lineage.Target.ArtifactType);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var source in sources)
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO lineage_sources(target_node_id,target_id,source_node_id,source_id) VALUES($targetNode,$targetId,$sourceNode,$sourceId)";
            command.Parameters.AddWithValue("$targetNode", document.TargetNodeId);
            command.Parameters.AddWithValue("$targetId", lineage.Target.Id.Value);
            command.Parameters.AddWithValue("$sourceNode", source.NodeId);
            command.Parameters.AddWithValue("$sourceId", source.Artifact.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public async Task AppendJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        ArgumentNullException.ThrowIfNull(entry);
        var document = new JournalDocument(entry.Result, entry.TargetNode,
            entry.Target is null ? null : ToDocument(entry.Target),
            entry.Sources.Select(source => new ScopedArtifactDocument(source.NodeKey, ToDocument(source.Artifact)))
                .OrderBy(source => source.NodeId, StringComparer.Ordinal).ThenBy(source => source.Artifact.Id, StringComparer.Ordinal).ToArray(),
            entry.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture), entry.EdgeName, entry.EdgeVersion, entry.FailureCode);
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO journal(edge_id,result,target_node,target_id,payload) VALUES($edge,$result,$node,$target,$payload)";
        command.Parameters.AddWithValue("$edge", document.EdgeId);
        command.Parameters.AddWithValue("$result", document.Result);
        command.Parameters.AddWithValue("$node", (object?)document.TargetNode ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)document.Target?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (document.Result is "produced" or "excluded")
        {
            foreach (var source in document.Sources)
                await AddJournalScopeAsync(transaction, document.EdgeId, false, source.NodeId, source.Artifact.Id,
                    cancellationToken).ConfigureAwait(false);
            if (document.Target is not null && document.TargetNode is not null)
                await AddJournalScopeAsync(transaction, document.EdgeId, true, document.TargetNode, document.Target.Id,
                    cancellationToken).ConfigureAwait(false);
        }
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public async Task<VerificationLedgerSummary> CompleteAsync(CancellationToken cancellationToken)
    {
        ValidateWritable();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        var sourceCount = await CountAsync("sources", cancellationToken).ConfigureAwait(false);
        var targetCount = await CountAsync("targets", cancellationToken).ConfigureAwait(false);
        var dispositionCount = await CountAsync("dispositions", cancellationToken).ConfigureAwait(false);
        var unaccountedCount = await CountValueAsync("SELECT COUNT(*) FROM dispositions WHERE disposition IN ('Unaccounted','Failed')", cancellationToken).ConfigureAwait(false);
        var lineageCount = await CountAsync("lineage", cancellationToken).ConfigureAwait(false);
        var journalCount = await CountAsync("journal", cancellationToken).ConfigureAwait(false);
        var fingerprint = await ComputeFingerprintAsync(cancellationToken).ConfigureAwait(false);
        await using (var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO metadata(key,value) VALUES('complete','true'),('fingerprint',$fingerprint),('sourceCount',$sources),('targetCount',$targets),('dispositionCount',$dispositions),('unaccountedCount',$unaccounted),('lineageCount',$lineage),('journalCount',$journal)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$sources", sourceCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$targets", targetCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$dispositions", dispositionCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$unaccounted", unaccountedCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$lineage", lineageCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$journal", journalCount.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        _completed = true;
        var relativePath = Path.GetRelativePath(_root, _databasePath).Replace(Path.DirectorySeparatorChar, '/');
        _receipt = new VerificationLedgerStoreReceipt(Guid.Parse(Path.GetFileName(_directory)), relativePath,
            fingerprint, sourceCount, targetCount, dispositionCount, lineageCount, journalCount);
        _verifiedSummary = new VerificationLedgerSummary(sourceCount, targetCount, dispositionCount, unaccountedCount,
            lineageCount, journalCount, fingerprint);
        _performanceRecorder?.RecordMeasuredStage(PerformanceStageKind.Verification, "verification ledger writes",
            Interlocked.Read(ref _writeMicroseconds), Interlocked.Read(ref _writeOperations),
            measurements: [new PerformanceMeasurement("boundedWriteOperations", Interlocked.Read(ref _writeOperations), "operations")]);
        return _verifiedSummary;
    }

    public async Task<VerificationLedgerSummary> ReadSummaryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_verifiedSummary is { } cached) return cached;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM metadata ORDER BY key";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) metadata.Add(reader.GetString(0), reader.GetString(1));
        }
        if (!metadata.TryGetValue("complete", out var complete) || complete != "true")
            throw new InvalidDataException("Verification ledger is incomplete.");
        var actual = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
        if (actual.Fingerprint != metadata["fingerprint"] || actual.SourceCount != ParseCount("sourceCount") ||
            actual.TargetCount != ParseCount("targetCount") || actual.DispositionCount != ParseCount("dispositionCount") ||
            actual.UnaccountedDispositionCount != ParseCount("unaccountedCount") || actual.LineageCount != ParseCount("lineageCount") ||
            actual.JournalEntryCount != ParseCount("journalCount"))
            throw new InvalidDataException("Verification ledger integrity validation failed.");
        _verifiedSummary = actual;
        return actual;

        long ParseCount(string key) => long.Parse(metadata[key], CultureInfo.InvariantCulture);
    }

    public async Task<VerificationLedgerCoverageResult> ValidateCoverageAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        var missingDispositions = await CountValueAsync("SELECT COUNT(*) FROM sources s LEFT JOIN dispositions d ON d.source_node_id=s.node_id AND d.source_id=s.artifact_id WHERE d.source_id IS NULL", cancellationToken).ConfigureAwait(false);
        var unexpectedDispositions = await CountValueAsync("SELECT COUNT(*) FROM dispositions d LEFT JOIN sources s ON s.node_id=d.source_node_id AND s.artifact_id=d.source_id WHERE s.artifact_id IS NULL", cancellationToken).ConfigureAwait(false);
        var unaccounted = await CountValueAsync("SELECT COUNT(*) FROM dispositions WHERE disposition IN ('Unaccounted','Failed')", cancellationToken).ConfigureAwait(false);
        var missingLineage = await CountValueAsync("SELECT COUNT(*) FROM targets t LEFT JOIN lineage l ON l.target_node_id=t.node_id AND l.target_id=t.artifact_id WHERE l.target_id IS NULL", cancellationToken).ConfigureAwait(false);
        var unexpectedLineage = await CountValueAsync("SELECT COUNT(*) FROM lineage l LEFT JOIN targets t ON t.node_id=l.target_node_id AND t.artifact_id=l.target_id WHERE t.artifact_id IS NULL", cancellationToken).ConfigureAwait(false);
        var unboundSources = await CountValueAsync("SELECT COUNT(*) FROM lineage l WHERE NOT EXISTS (SELECT 1 FROM lineage_sources ls JOIN sources s ON s.node_id=ls.source_node_id AND s.artifact_id=ls.source_id WHERE ls.target_node_id=l.target_node_id AND ls.target_id=l.target_id)", cancellationToken).ConfigureAwait(false);
        return new VerificationLedgerCoverageResult(missingDispositions, unexpectedDispositions, unaccounted,
            missingLineage, unexpectedLineage, unboundSources);
    }

    public async IAsyncEnumerable<ArtifactDispositionRecord> ReadDispositionsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM dispositions ORDER BY source_node_id,source_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<DispositionDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async IAsyncEnumerable<LineageRecord> ReadLineageAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM lineage ORDER BY target_node_id,target_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<LineageDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async IAsyncEnumerable<VerificationSourceLineageTargets> ReadLineageTargetsBySourceAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var readConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await readConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = readConnection.CreateCommand();
        command.CommandText = """
            SELECT s.node_key,ls.source_node_id,ls.source_id,ls.target_node_id,l.target_id,
                   l.target_system,l.target_endpoint,l.target_type
            FROM lineage_sources ls
            JOIN sources s ON s.node_id=ls.source_node_id AND s.artifact_id=ls.source_id
            JOIN lineage l ON l.target_node_id=ls.target_node_id AND l.target_id=ls.target_id
            ORDER BY s.node_key,ls.source_id,ls.source_node_id,l.target_node_id,l.target_id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        (string NodeKey, string NodeId, string SourceId)? current = null;
        var targets = new List<VerificationLineageTarget>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (current is not null && current.Value != key)
            {
                yield return new VerificationSourceLineageTargets(current.Value.NodeKey,
                    new MigrationNodeId(Guid.Parse(current.Value.NodeId)), new ArtifactId(current.Value.SourceId), targets.ToArray());
                targets.Clear();
            }
            current = key;
            var targetNodeId = new MigrationNodeId(Guid.Parse(reader.GetString(3)));
            var artifact = new ArtifactReference(new ArtifactId(reader.GetString(4)), new SystemId(reader.GetString(5)),
                new StorageEndpointId(reader.GetString(6)), reader.GetString(7), $"opaque:{reader.GetString(4)}");
            targets.Add(new VerificationLineageTarget(targetNodeId, artifact));
        }
        if (current is not null)
            yield return new VerificationSourceLineageTargets(current.Value.NodeKey,
                new MigrationNodeId(Guid.Parse(current.Value.NodeId)), new ArtifactId(current.Value.SourceId), targets.ToArray());
    }

    public async IAsyncEnumerable<VerificationJournalEntry> ReadJournalEntriesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM journal ORDER BY edge_id,result,target_node,target_id,payload";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<JournalDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async IAsyncEnumerable<VerificationJournalEntry> ReadJournalEntriesForEdgeAsync(MigrationEdgeId edgeId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM journal WHERE edge_id=$edge ORDER BY result,target_node,target_id,payload";
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<JournalDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async IAsyncEnumerable<VerificationJournalScope> ReadJournalScopeAsync(MigrationEdgeId edgeId, bool targets,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT node_key,node_id,artifact_id FROM journal_scope WHERE edge_id=$edge AND is_target=$target ORDER BY node_id,artifact_id";
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$target", targets ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationJournalScope(reader.GetString(0), new MigrationNodeId(Guid.Parse(reader.GetString(1))),
                new ArtifactId(reader.GetString(2)));
        }
    }

    public async Task<VerificationJournalScopeCounts> ReadJournalScopeCountsAsync(MigrationEdgeId edgeId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT is_target,COUNT(*) FROM journal_scope WHERE edge_id=$edge GROUP BY is_target";
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        long sources = 0;
        long targets = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(0) == 0) sources = reader.GetInt64(1);
            else targets = reader.GetInt64(1);
        }
        return new VerificationJournalScopeCounts(sources, targets);
    }

    public async IAsyncEnumerable<VerificationJournalScope> ReadDistinctJournalScopeAsync(MigrationEdgeId edgeId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT node_key,node_id,artifact_id FROM journal_scope WHERE edge_id=$edge GROUP BY node_key,node_id,artifact_id ORDER BY node_id,artifact_id";
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationJournalScope(reader.GetString(0), new MigrationNodeId(Guid.Parse(reader.GetString(1))),
                new ArtifactId(reader.GetString(2)));
        }
    }

    public async Task<ArtifactDispositionRecord?> FindDispositionAsync(MigrationNodeId sourceNodeId, ArtifactId sourceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM dispositions WHERE source_node_id=$node AND source_id=$source";
        command.Parameters.AddWithValue("$node", sourceNodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", sourceId.Value);
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return payload is null ? null : FromDocument(JsonSerializer.Deserialize<DispositionDocument>(payload, JsonOptions)!);
    }

    public async IAsyncEnumerable<LineageRecord> FindLineageBySourceAsync(MigrationNodeId sourceNodeId, ArtifactId sourceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT l.payload FROM lineage l JOIN lineage_sources s ON s.target_node_id=l.target_node_id AND s.target_id=l.target_id WHERE s.source_node_id=$node AND s.source_id=$source ORDER BY l.target_node_id,l.target_id";
        command.Parameters.AddWithValue("$node", sourceNodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", sourceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<LineageDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async IAsyncEnumerable<LineageRecord> FindLineageByTargetAsync(MigrationNodeId targetNodeId, ArtifactId targetId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM lineage WHERE target_node_id=$node AND target_id=$target";
        command.Parameters.AddWithValue("$node", targetNodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$target", targetId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return FromDocument(JsonSerializer.Deserialize<LineageDocument>(reader.GetString(0), JsonOptions)!);
        }
    }

    public async Task<bool> HasJournalEntryForEdgeAsync(MigrationEdgeId edgeId, string result, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM journal WHERE edge_id=$edge AND result=$result LIMIT 1";
        command.Parameters.AddWithValue("$edge", edgeId.Value.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$result", Required(result, nameof(result)).ToLowerInvariant());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<VerificationLedgerSummary> GetCountsAsync(CancellationToken cancellationToken)
    {
        var sources = await CountAsync("sources", cancellationToken).ConfigureAwait(false);
        var targets = await CountAsync("targets", cancellationToken).ConfigureAwait(false);
        var dispositions = await CountAsync("dispositions", cancellationToken).ConfigureAwait(false);
        var unaccounted = await CountValueAsync("SELECT COUNT(*) FROM dispositions WHERE disposition IN ('Unaccounted','Failed')", cancellationToken).ConfigureAwait(false);
        var lineage = await CountAsync("lineage", cancellationToken).ConfigureAwait(false);
        var journal = await CountAsync("journal", cancellationToken).ConfigureAwait(false);
        return new VerificationLedgerSummary(sources, targets, dispositions, unaccounted, lineage, journal,
            await ComputeFingerprintAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("proofshift-verification-ledger-v1");
        await AppendRowsAsync("sources", "node_id,artifact_id", cancellationToken).ConfigureAwait(false);
        await AppendRowsAsync("targets", "node_id,artifact_id", cancellationToken).ConfigureAwait(false);
        await AppendRowsAsync("dispositions", "source_node_id,source_id", cancellationToken).ConfigureAwait(false);
        await AppendRowsAsync("lineage", "target_node_id,target_id", cancellationToken).ConfigureAwait(false);
        await AppendRowsAsync("journal", "edge_id,result,target_node,target_id,payload", cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        async Task AppendRowsAsync(string table, string orderBy, CancellationToken token)
        {
            Append(table);
            await using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY {orderBy}";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                for (var index = 0; index < reader.FieldCount; index++)
                    Append(reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        if (table is not ("sources" or "targets" or "dispositions" or "lineage" or "journal"))
            throw new ArgumentOutOfRangeException(nameof(table));
        return await CountValueAsync($"SELECT COUNT(*) FROM {table}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CountValueAsync(string query, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = query;
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private SqliteTransaction GetWriteTransaction()
    {
        ValidateWritable();
        return _writeTransaction ??= _connection.BeginTransaction();
    }

    private void RecordWrite(long startedTimestamp)
    {
        Interlocked.Add(ref _writeMicroseconds, checked((long)Math.Round(Stopwatch.GetElapsedTime(startedTimestamp).TotalMicroseconds)));
        Interlocked.Increment(ref _writeOperations);
    }

    private async Task CompleteWriteAsync(CancellationToken cancellationToken)
    {
        _pendingWrites++;
        if (_pendingWrites >= WriteBatchSize) await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushWritesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_writeTransaction is not { } transaction) return;
        _writeTransaction = null;
        _pendingWrites = 0;
        try { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }
        finally { await transaction.DisposeAsync().ConfigureAwait(false); }
    }

    private void ValidateWritable()
    {
        ThrowIfDisposed();
        if (_completed) throw new InvalidOperationException("Verification ledger is already complete.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_writeTransaction is { } transaction)
        {
            _writeTransaction = null;
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { await transaction.DisposeAsync().ConfigureAwait(false); }
        }
        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
        if (!_completed) TryDeleteDirectory(_directory);
    }

    private static void AddArtifactParameters(SqliteCommand command, ArtifactReference artifact)
    {
        command.Parameters.AddWithValue("$id", artifact.Id.Value);
        command.Parameters.AddWithValue("$system", artifact.SystemId.Value);
        command.Parameters.AddWithValue("$endpoint", artifact.EndpointId.Value);
        command.Parameters.AddWithValue("$type", artifact.ArtifactType);
    }

    private static ArtifactDocument ToDocument(ArtifactReference artifact) =>
        new(artifact.Id.Value, artifact.SystemId.Value, artifact.EndpointId.Value, artifact.ArtifactType);

    private static ArtifactReference FromDocument(ArtifactDocument artifact) =>
        new(new ArtifactId(artifact.Id), new SystemId(artifact.System), new StorageEndpointId(artifact.Endpoint),
            artifact.Type, $"opaque:{artifact.Id}");

    private static ArtifactDispositionRecord FromDocument(DispositionDocument document)
    {
        var sourceNode = new MigrationNodeId(Guid.Parse(document.SourceNodeId));
        var targets = document.Targets.Select(item => FromDocument(item.Artifact)).ToArray();
        var targetNodes = document.Targets.Select(item => new MigrationNodeId(Guid.Parse(item.NodeId))).ToArray();
        return new ArtifactDispositionRecord(FromDocument(document.Source), Enum.Parse<ArtifactDisposition>(document.Disposition),
            targets, document.Reason, sourceNode, targetNodes);
    }

    private static LineageRecord FromDocument(LineageDocument document) =>
        new(FromDocument(document.Target), document.Sources.Select(item => FromDocument(item.Artifact)),
            document.Path.Select(value => new MigrationEdgeId(Guid.Parse(value))), document.PlanHash,
            new MigrationNodeId(Guid.Parse(document.TargetNodeId)),
            document.Sources.Select(item => new MigrationNodeId(Guid.Parse(item.NodeId))),
            Enum.Parse<LineageBasis>(document.Basis));

    private static VerificationJournalEntry FromDocument(JournalDocument document) =>
        new(document.Result, document.TargetNode,
            document.Target is null ? null : FromDocument(document.Target),
            document.Sources.Select(source => new VerificationGraphArtifact(source.NodeId, FromDocument(source.Artifact))),
            new MigrationEdgeId(Guid.Parse(document.EdgeId)), document.EdgeName, document.EdgeVersion, document.FailureCode);

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }

    private sealed record ArtifactDocument(string Id, string System, string Endpoint, string Type);
    private sealed record ScopedArtifactDocument(string NodeId, ArtifactDocument Artifact);
    private sealed record DispositionDocument(ArtifactDocument Source, string SourceNodeId, string Disposition,
        ScopedArtifactDocument[] Targets, string? Reason);
    private sealed record LineageDocument(ArtifactDocument Target, string TargetNodeId, ScopedArtifactDocument[] Sources,
        string[] Path, string PlanHash, string Basis);
    private sealed record JournalDocument(string Result, string? TargetNode, ArtifactDocument? Target,
        ScopedArtifactDocument[] Sources, string EdgeId, string EdgeName, string EdgeVersion, string? FailureCode);
}
