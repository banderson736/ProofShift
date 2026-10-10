using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProofShift.Verification.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProofShift.EndToEnd.Tests")]

namespace ProofShift.Verification;

public enum VerificationLedgerState { Pending, Finalizing, Complete, Failed, Cancelled }
internal enum LedgerFinalizationPhase { TableCopied, IntegrityValidated }

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
    long SourceCount, long TargetCount, long DispositionCount, long LineageCount, long JournalEntryCount,
    string? AuxiliaryFingerprint = null);

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
    private const int WriteBatchSize = 4096;
    private const int FinalizationBatchSize = 16384;
    private enum LedgerTable { GraphNodes, Sources, Targets, Dispositions, Lineage, LineageSources, Journal, JournalSourceScope, JournalTargetScope, Metadata, Count }
    private static readonly string[] LedgerTableNames = ["graph_nodes", "sources", "targets", "dispositions", "lineage", "lineage_sources", "journal", "journal_scope_sources", "journal_scope_targets", "metadata"];
    private static readonly HashSet<LedgerTable> PayloadTables = [LedgerTable.Dispositions, LedgerTable.Lineage, LedgerTable.Journal];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;
    private readonly string _directory;
    private readonly string _databasePath;
    private SqliteConnection _connection;
    private readonly PerformanceRecorder? _performanceRecorder;
    private SqliteTransaction? _writeTransaction;
    private int _pendingWrites;
    private long _writeMicroseconds;
    private long _writeOperations;
    private readonly long[] _successfulInsertCommands = new long[(int)LedgerTable.Count];
    private readonly long[] _insertedRows = new long[(int)LedgerTable.Count];
    private readonly long[] _serializedPayloadBytes = new long[(int)LedgerTable.Count];
    private bool _completed;
    private bool _disposed;
    private VerificationLedgerStoreReceipt? _receipt;
    private VerificationLedgerSummary? _verifiedSummary;
    private string? _verifiedAuxiliaryFingerprint;
    private readonly Dictionary<string, MigrationNodeId> _graphNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Table, string Sql)> _deferredSecondaryIndexes = new(StringComparer.Ordinal);
    private long _derivedLineageRow;
    private long _derivedJournalRow;
    private string StagingPath => _databasePath + ".staging";
    public VerificationLedgerState State { get; private set; } = VerificationLedgerState.Pending;
    internal Action<LedgerFinalizationPhase>? FinalizationObserver { get; set; }

    public VerificationLedgerStoreReceipt Receipt => State == VerificationLedgerState.Complete && _receipt is { } receipt
        ? receipt : throw new InvalidOperationException("Verification ledger has not been completed.");

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
        if (Directory.Exists(directory))
            throw new IOException("Verification ledger run directory already exists.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "ledger.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path + ".staging",
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
                INSERT INTO metadata(key,value) VALUES('state','Pending');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var store = new SqliteVerificationLedgerStore(root, directory, path, connection, performanceRecorder);
            command.CommandText = "SELECT name,tbl_name,sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL ORDER BY name";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    store._deferredSecondaryIndexes.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
            foreach (var name in store._deferredSecondaryIndexes.Keys)
            {
                command.CommandText = $"DROP INDEX \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return store;
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
                _receipt = receipt,
                State = VerificationLedgerState.Complete
            };
            var summary = await store.ReadSummaryAsync(cancellationToken).ConfigureAwait(false);
            if (summary.Fingerprint != receipt.Fingerprint || summary.SourceCount != receipt.SourceCount ||
                summary.TargetCount != receipt.TargetCount || summary.DispositionCount != receipt.DispositionCount ||
                summary.LineageCount != receipt.LineageCount || summary.JournalEntryCount != receipt.JournalEntryCount)
                throw new InvalidDataException("Verification ledger contents do not match their receipt.");
            if (receipt.AuxiliaryFingerprint is not null)
            {
                await using var stateCommand = connection.CreateCommand();
                stateCommand.CommandText = "SELECT value FROM metadata WHERE key='state'";
                if (await stateCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string != nameof(VerificationLedgerState.Complete) ||
                    receipt.AuxiliaryFingerprint != store._verifiedAuxiliaryFingerprint)
                    throw new InvalidDataException("Verification ledger auxiliary contents do not match their receipt.");
            }
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
        CancellationToken cancellationToken) => StageAsync(() => RegisterArtifactAsync("sources", nodeKey, nodeId, artifact, cancellationToken));

    public Task RegisterGraphNodeAsync(string nodeKey, MigrationNodeId nodeId, CancellationToken cancellationToken) =>
        StageAsync(() => RegisterGraphNodeCoreAsync(nodeKey, nodeId, cancellationToken));

    private async Task RegisterGraphNodeCoreAsync(string nodeKey, MigrationNodeId nodeId, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO graph_nodes(node_key,node_id) VALUES($key,$id)";
        command.Parameters.AddWithValue("$key", Required(nodeKey, nameof(nodeKey)));
        command.Parameters.AddWithValue("$id", nodeId.Value.ToString("D", CultureInfo.InvariantCulture));
        var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        RecordInsert(LedgerTable.GraphNodes, insertedRows);
        _graphNodes.Add(Required(nodeKey, nameof(nodeKey)), nodeId);
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public Task RegisterTargetAsync(string nodeKey, MigrationNodeId nodeId, ArtifactReference artifact,
        CancellationToken cancellationToken) => StageAsync(() => RegisterArtifactAsync("targets", nodeKey, nodeId, artifact, cancellationToken));

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
        var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        RecordInsert(table == "sources" ? LedgerTable.Sources : LedgerTable.Targets, insertedRows);
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public Task AppendDispositionAsync(ArtifactDispositionRecord disposition, CancellationToken cancellationToken) =>
        StageAsync(() => AppendDispositionCoreAsync(disposition, cancellationToken));

    private async Task AppendDispositionCoreAsync(ArtifactDispositionRecord disposition, CancellationToken cancellationToken)
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
        var payload = JsonSerializer.Serialize(document, JsonOptions);
        command.Parameters.AddWithValue("$payload", payload);
        var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        RecordInsert(LedgerTable.Dispositions, insertedRows, Encoding.UTF8.GetByteCount(payload));
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public Task AppendLineageAsync(LineageRecord lineage, CancellationToken cancellationToken) =>
        StageAsync(() => AppendLineageCoreAsync(lineage, cancellationToken));

    private async Task AppendLineageCoreAsync(LineageRecord lineage, CancellationToken cancellationToken)
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
        for (var index = 1; index < sources.Length; index++)
            if (sources[index - 1].NodeId == sources[index].NodeId &&
                sources[index - 1].Artifact.Id == sources[index].Artifact.Id)
                throw new SqliteException("Duplicate graph-scoped lineage source binding.", 19);
        var document = new LineageDocument(ToDocument(lineage.Target), targetNodeId.Value.ToString("D", CultureInfo.InvariantCulture),
            sources, lineage.Path.Select(edge => edge.Value.ToString("D", CultureInfo.InvariantCulture)).ToArray(),
            lineage.PlanHash, lineage.Basis.ToString());
        var payload = JsonSerializer.Serialize(document, JsonOptions);
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
            command.Parameters.AddWithValue("$payload", payload);
            var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            RecordInsert(LedgerTable.Lineage, insertedRows, Encoding.UTF8.GetByteCount(payload));
        }
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public Task AppendJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken) =>
        StageAsync(() => AppendJournalEntryCoreAsync(entry, cancellationToken));

    private async Task AppendJournalEntryCoreAsync(VerificationJournalEntry entry, CancellationToken cancellationToken)
    {
        var writeStarted = Stopwatch.GetTimestamp();
        ValidateWritable();
        ArgumentNullException.ThrowIfNull(entry);
        var document = new JournalDocument(entry.Result, entry.TargetNode,
            entry.Target is null ? null : ToDocument(entry.Target),
            entry.Sources.Select(source => new ScopedArtifactDocument(source.NodeKey, ToDocument(source.Artifact)))
                .OrderBy(source => source.NodeId, StringComparer.Ordinal).ThenBy(source => source.Artifact.Id, StringComparer.Ordinal).ToArray(),
            entry.EdgeId.Value.ToString("D", CultureInfo.InvariantCulture), entry.EdgeName, entry.EdgeVersion, entry.FailureCode);
        if (document.Result is "produced" or "excluded")
        {
            if (document.Sources.Any(source => !_graphNodes.ContainsKey(source.NodeId)) ||
                (document.Target is not null && document.TargetNode is not null && !_graphNodes.ContainsKey(document.TargetNode)))
                throw new InvalidDataException("Verification journal scope references a graph node that was not registered.");
        }
        var transaction = GetWriteTransaction();
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO journal(edge_id,result,target_node,target_id,payload) VALUES($edge,$result,$node,$target,$payload)";
        command.Parameters.AddWithValue("$edge", document.EdgeId);
        command.Parameters.AddWithValue("$result", document.Result);
        command.Parameters.AddWithValue("$node", (object?)document.TargetNode ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)document.Target?.Id ?? DBNull.Value);
        var payload = JsonSerializer.Serialize(document, JsonOptions);
        command.Parameters.AddWithValue("$payload", payload);
        var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        RecordInsert(LedgerTable.Journal, insertedRows, Encoding.UTF8.GetByteCount(payload));
        await CompleteWriteAsync(cancellationToken).ConfigureAwait(false);
        RecordWrite(writeStarted);
    }

    public async Task<VerificationLedgerSummary> CompleteAsync(CancellationToken cancellationToken)
    {
        ValidateWritable();
        try
        {
            await FinalizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailAsync(exception).ConfigureAwait(false);
            throw;
        }
        return _verifiedSummary!;
    }

    private async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStagingIndexesAsync(null, cancellationToken).ConfigureAwait(false);
        using var finalizationStage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification ledger durable finalization");
        finalizationStage?.AddMeasurement("stagingLifecycleSqlExecutionOperations", 2, "commands");
        finalizationStage?.AddMeasurement("durableLifecycleSqlExecutionOperations", 3, "commands");
        State = VerificationLedgerState.Finalizing;
        await SetStateAsync(_connection, State, cancellationToken).ConfigureAwait(false);
        var expected = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
        var expectedCoverage = await ValidateCoverageAsync(cancellationToken).ConfigureAwait(false);
        var schemas = new List<string>();
        var secondaryIndexes = new List<string>();
        await using (var schemaCommand = _connection.CreateCommand())
        {
            schemaCommand.CommandText = "SELECT type,sql FROM sqlite_master WHERE sql IS NOT NULL AND type IN ('table','index') ORDER BY type DESC,name";
            await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(0) == "index") secondaryIndexes.Add(reader.GetString(1));
                else schemas.Add(reader.GetString(1));
            }
        }
        var finalConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, Cache = SqliteCacheMode.Private
        }.ToString());
        try
        {
            await finalConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var schemaCommand = finalConnection.CreateCommand())
            {
                schemaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" + string.Join(";", schemas);
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await SetStateAsync(finalConnection, State, cancellationToken).ConfigureAwait(false);
            await using (var attach = finalConnection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS staging";
                attach.Parameters.AddWithValue("$path", StagingPath);
                await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var table in new[] { "graph_nodes", "sources", "targets", "dispositions", "lineage", "lineage_sources", "journal", "journal_scope" })
            {
                using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, $"verification ledger finalize {table}");
                var upper = await CountValueAsync($"SELECT COALESCE(MAX(rowid),0) FROM {table}", cancellationToken).ConfigureAwait(false);
                long rows = 0;
                long operations = 0;
                long maximumWalBytes = 0;
                long maximumRowsPerOperation = 0;
                for (long lower = 0; lower < upper; lower += FinalizationBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var transaction = finalConnection.BeginTransaction();
                    await using var copy = finalConnection.CreateCommand();
                    copy.Transaction = transaction;
                    copy.CommandText = $"INSERT INTO {table} SELECT * FROM staging.{table} WHERE rowid>$lower AND rowid<=$upper ORDER BY rowid";
                    copy.Parameters.AddWithValue("$lower", lower);
                    copy.Parameters.AddWithValue("$upper", Math.Min(upper, lower + FinalizationBatchSize));
                    var copiedRows = await copy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    rows += copiedRows;
                    maximumRowsPerOperation = Math.Max(maximumRowsPerOperation, copiedRows);
                    operations++;
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    maximumWalBytes = Math.Max(maximumWalBytes, File.Exists(_databasePath + "-wal") ? new FileInfo(_databasePath + "-wal").Length : 0);
                }
                stage?.AddMeasurement("stagedRows", await CountValueAsync($"SELECT COUNT(*) FROM {table}", cancellationToken).ConfigureAwait(false), "rows");
                stage?.AddMeasurement("finalizedRows", rows, "rows");
                stage?.AddMeasurement("sqlExecutionOperations", operations, "commands");
                stage?.AddMeasurement("maximumRowsPerOperation", maximumRowsPerOperation, "rows");
                stage?.AddMeasurement("durableWalBytesObserved", maximumWalBytes, "bytes");
                FinalizationObserver?.Invoke(LedgerFinalizationPhase.TableCopied);
            }
            using (var indexStage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification ledger destination secondary index build"))
            {
                foreach (var sql in secondaryIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var command = finalConnection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                indexStage?.AddMeasurement("secondaryIndexBuilds", secondaryIndexes.Count, "indexes");
            }
            await using (var detach = finalConnection.CreateCommand())
            {
                detach.CommandText = "DETACH DATABASE staging";
                await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await TrySetFailureStateAsync(finalConnection, cancellationToken.IsCancellationRequested ? VerificationLedgerState.Cancelled : VerificationLedgerState.Failed,
                CancellationToken.None).ConfigureAwait(false);
            await finalConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = finalConnection;
        var actual = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
        if (expected != actual || expectedCoverage != await ValidateCoverageAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Verification ledger finalization changed semantic facts or coverage.");
        await using (var integrity = _connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check";
            if (!string.Equals(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("Verification ledger SQLite integrity validation failed.");
        }
        FinalizationObserver?.Invoke(LedgerFinalizationPhase.IntegrityValidated);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceCount = actual.SourceCount;
        var targetCount = actual.TargetCount;
        var dispositionCount = actual.DispositionCount;
        var unaccountedCount = actual.UnaccountedDispositionCount;
        var lineageCount = actual.LineageCount;
        var journalCount = actual.JournalEntryCount;
        var fingerprint = actual.Fingerprint;
        var auxiliaryFingerprint = await ComputeFingerprintAsync(cancellationToken, auxiliary: true).ConfigureAwait(false);
        _verifiedAuxiliaryFingerprint = auxiliaryFingerprint;
        using var metadataStage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification ledger finalize metadata");
        await using (var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "INSERT INTO metadata(key,value) VALUES('complete','true'),('fingerprint',$fingerprint),('sourceCount',$sources),('targetCount',$targets),('dispositionCount',$dispositions),('unaccountedCount',$unaccounted),('lineageCount',$lineage),('journalCount',$journal),('auxiliaryFingerprint',$auxiliary)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$auxiliary", auxiliaryFingerprint);
            command.Parameters.AddWithValue("$sources", sourceCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$targets", targetCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$dispositions", dispositionCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$unaccounted", unaccountedCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$lineage", lineageCount.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$journal", journalCount.ToString(CultureInfo.InvariantCulture));
            var insertedRows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            RecordInsert(LedgerTable.Metadata, insertedRows);
            await using var stateCommand = _connection.CreateCommand();
            stateCommand.Transaction = (SqliteTransaction)transaction;
            stateCommand.CommandText = "UPDATE metadata SET value='Complete' WHERE key='state'";
            await stateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        metadataStage?.AddMeasurement("finalizedRows", 10, "rows");
        metadataStage?.AddMeasurement("sqlExecutionOperations", 2, "commands");
        _completed = true;
        State = VerificationLedgerState.Complete;
        var relativePath = Path.GetRelativePath(_root, _databasePath).Replace(Path.DirectorySeparatorChar, '/');
        _receipt = new VerificationLedgerStoreReceipt(Guid.Parse(Path.GetFileName(_directory)), relativePath,
            fingerprint, sourceCount, targetCount, dispositionCount, lineageCount, journalCount, auxiliaryFingerprint);
        _verifiedSummary = new VerificationLedgerSummary(sourceCount, targetCount, dispositionCount, unaccountedCount,
            lineageCount, journalCount, fingerprint);
        finalizationStage?.AddMeasurement("stagingDatabaseBytes", File.Exists(StagingPath) ? new FileInfo(StagingPath).Length : 0, "bytes");
        finalizationStage?.AddMeasurement("durableDatabaseBytes", new FileInfo(_databasePath).Length, "bytes");
        finalizationStage?.AddMeasurement("durableWalBytes", File.Exists(_databasePath + "-wal") ? new FileInfo(_databasePath + "-wal").Length : 0, "bytes");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(StagingPath + suffix); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (_performanceRecorder is not null)
        {
            var measurements = new List<PerformanceMeasurement>
            {
                new("boundedWriteOperations", Interlocked.Read(ref _writeOperations), "operations"),
                new("ledgerApiWriteCalls", Interlocked.Read(ref _writeOperations), "calls")
            };
            for (var index = 0; index < LedgerTableNames.Length; index++)
            {
                var table = LedgerTableNames[index];
                measurements.Add(new PerformanceMeasurement($"successfulInsertCommands.{table}",
                    Interlocked.Read(ref _successfulInsertCommands[index]), "commands"));
                measurements.Add(new PerformanceMeasurement($"insertedRows.{table}",
                    Interlocked.Read(ref _insertedRows[index]), "rows"));
                if (PayloadTables.Contains((LedgerTable)index))
                    measurements.Add(new PerformanceMeasurement($"serializedPayloadBytes.{table}",
                        Interlocked.Read(ref _serializedPayloadBytes[index]), "bytes"));
            }
            _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification, "verification ledger staging writes",
                Interlocked.Read(ref _writeMicroseconds), Interlocked.Read(ref _writeOperations), measurements: measurements);
        }
    }

    private static async Task SetStateAsync(SqliteConnection connection, VerificationLedgerState state, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata(key,value) VALUES('state',$state) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$state", state.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TrySetFailureStateAsync(SqliteConnection connection, VerificationLedgerState state, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        try { await SetStateAsync(connection, state, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException) { }
    }

    private async Task StageAsync(Func<Task> action)
    {
        ValidateWritable();
        try { await action().ConfigureAwait(false); }
        catch (Exception exception)
        {
            await FailAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task FailAsync(Exception exception)
    {
        State = exception is OperationCanceledException ? VerificationLedgerState.Cancelled : VerificationLedgerState.Failed;
        _completed = false;
        _receipt = null;
        _verifiedSummary = null;
        _verifiedAuxiliaryFingerprint = null;
        if (_writeTransaction is { } pending)
        {
            _writeTransaction = null;
            try { await pending.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { await pending.DisposeAsync().ConfigureAwait(false); }
        }
        await TrySetFailureStateAsync(_connection, State, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EnsureDerivedFactsAsync(CancellationToken cancellationToken)
    {
        if (_completed || State != VerificationLedgerState.Pending) return;
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await DeriveAsync("lineage", _derivedLineageRow, false).ConfigureAwait(false);
        await DeriveAsync("journal", _derivedJournalRow, true).ConfigureAwait(false);

        async Task DeriveAsync(string table, long previous, bool journal)
        {
            var upper = await CountValueAsync($"SELECT COALESCE(MAX(rowid),0) FROM {table}", cancellationToken).ConfigureAwait(false);
            using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, $"verification ledger staging derive {table}");
            for (var lower = previous; lower < upper; lower += FinalizationBatchSize)
            {
                await using var transaction = _connection.BeginTransaction();
                foreach (var kind in journal ? new[] { LedgerTable.JournalSourceScope, LedgerTable.JournalTargetScope } : new[] { LedgerTable.LineageSources })
                {
                    await using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = kind switch
                    {
                        LedgerTable.LineageSources => "INSERT INTO lineage_sources SELECT l.target_node_id,l.target_id,json_extract(s.value,'$.nodeId'),json_extract(s.value,'$.artifact.id') FROM lineage l,json_each(l.payload,'$.sources') s WHERE l.rowid>$lower AND l.rowid<=$upper",
                        LedgerTable.JournalSourceScope => "INSERT OR IGNORE INTO journal_scope SELECT j.edge_id,0,g.node_key,g.node_id,json_extract(s.value,'$.artifact.id') FROM journal j,json_each(j.payload,'$.sources') s JOIN graph_nodes g ON g.node_key=json_extract(s.value,'$.nodeId') WHERE j.rowid>$lower AND j.rowid<=$upper AND j.result IN ('produced','excluded') ORDER BY j.rowid,s.key",
                        _ => "INSERT OR IGNORE INTO journal_scope SELECT j.edge_id,1,g.node_key,g.node_id,j.target_id FROM journal j JOIN graph_nodes g ON g.node_key=j.target_node WHERE j.rowid>$lower AND j.rowid<=$upper AND j.result IN ('produced','excluded') AND j.target_id IS NOT NULL ORDER BY j.rowid"
                    };
                    command.Parameters.AddWithValue("$lower", lower);
                    command.Parameters.AddWithValue("$upper", Math.Min(upper, lower + FinalizationBatchSize));
                    RecordInsert(kind, await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (journal) _derivedJournalRow = upper;
            else _derivedLineageRow = upper;
        }
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
        if (metadata.TryGetValue("state", out var state) && state != nameof(VerificationLedgerState.Complete))
            throw new InvalidDataException("Verification ledger is not finalized.");
        if (metadata.ContainsKey("state") || metadata.ContainsKey("auxiliaryFingerprint"))
        {
            if (!metadata.TryGetValue("state", out var finalizedState) || finalizedState != nameof(VerificationLedgerState.Complete) ||
                !metadata.TryGetValue("auxiliaryFingerprint", out var auxiliary))
                throw new InvalidDataException("Verification ledger auxiliary integrity validation failed.");
            _verifiedAuxiliaryFingerprint = await ComputeFingerprintAsync(cancellationToken, auxiliary: true).ConfigureAwait(false);
            if (auxiliary != _verifiedAuxiliaryFingerprint)
                throw new InvalidDataException("Verification ledger auxiliary integrity validation failed.");
        }
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStagingIndexesAsync("lineage_sources", cancellationToken).ConfigureAwait(false);
        await using var readConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _completed ? _databasePath : StagingPath,
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
        await EnsureStagingIndexesAsync("journal", cancellationToken).ConfigureAwait(false);
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureDerivedFactsAsync(cancellationToken).ConfigureAwait(false);
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStagingIndexesAsync("lineage_sources", cancellationToken).ConfigureAwait(false);
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
        await EnsureStagingIndexesAsync("journal", cancellationToken).ConfigureAwait(false);
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

    private async Task<string> ComputeFingerprintAsync(CancellationToken cancellationToken, bool auxiliary = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (auxiliary)
        {
            Append("proofshift-verification-ledger-auxiliary-v1");
            await AppendRowsAsync("graph_nodes", "node_key", cancellationToken).ConfigureAwait(false);
            await AppendRowsAsync("lineage_sources", "target_node_id,target_id,source_node_id,source_id", cancellationToken).ConfigureAwait(false);
            await AppendRowsAsync("journal_scope", "edge_id,is_target,node_id,artifact_id", cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
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

    private async Task EnsureStagingIndexesAsync(string? table, CancellationToken cancellationToken)
    {
        var required = _deferredSecondaryIndexes.Where(pair => table is null || pair.Value.Table == table).ToArray();
        if (required.Length == 0) return;
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification ledger staging secondary index build");
        foreach (var (name, definition) in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = _connection.CreateCommand();
            command.CommandText = definition.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _deferredSecondaryIndexes.Remove(name);
            stage?.AddMeasurement($"indexBuilt.{name}", 1, "indexes");
        }
        stage?.AddMeasurement("secondaryIndexBuilds", required.Length, "indexes");
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

    private void RecordInsert(LedgerTable table, int insertedRows, int payloadBytes = 0)
    {
        var index = (int)table;
        Interlocked.Increment(ref _successfulInsertCommands[index]);
        Interlocked.Add(ref _insertedRows[index], insertedRows);
        Interlocked.Add(ref _serializedPayloadBytes[index], payloadBytes);
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
        if (State != VerificationLedgerState.Pending) throw new InvalidOperationException("Verification ledger cannot accept writes after finalization begins.");
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
