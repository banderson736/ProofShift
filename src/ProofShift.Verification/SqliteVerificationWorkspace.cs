using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Snapshots;

namespace ProofShift.Verification;

public sealed class SqliteVerificationWorkspace : IVerificationWorkspace
{
    private const int WriteBatchSize = 128;
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private readonly PerformanceRecorder? _performanceRecorder;
    private readonly VerificationExecutionPlan? _executionPlan;
    private readonly Dictionary<string, SqliteCommand> _writeCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<VerificationWorksetKey, WorksetMaterializationMetrics> _worksetMetrics = [];
    private readonly object _worksetMetricsGate = new();
    private SqliteCommand? _orderingKeyInsert;
    private bool _orderingIndexCreated;
        private bool _expectedPartitionIndexCreated;
    private long _committedTransactions;
    private readonly Dictionary<string, string[]> _queryPlans = new(StringComparer.Ordinal);
    private SqliteTransaction? _writeTransaction;
    private int _pendingWriteOperations;
    private long _journalPreparationTicks;
    private long _journalExecutionTicks;
    private long _journalCommitTicks;
    private long _journalScratchObservationTicks;
    private long _journalTransactionDisposeTicks;
    private long _journalTransactionFlushTicks;
    private long _journalEntryCount;
    private long _journalStatementCount;
    private long _journalFlushCount;
    private long _journalPendingEntryCount;
    private int _journalWriteDiagnosticsRecorded;
    private bool _disposed;
    private VerificationExpectedWorkset? _sharedExpected;
    private bool _publishedExpected;
    private bool _retainPublishedExpected;
    private int _partitionCount = 1;
    private int _maxPartitionWorkers = 1;
    private readonly AsyncLocal<int?> _activePartition = new();
    private int? _ruleEvaluationPartition;
    private readonly List<PartitionWriter> _partitionWriters = [];
    private readonly List<PartitionWorkerLane> _partitionWorkerLanes = [];
    private readonly CancellationTokenSource _partitionWorkerStop = new();
    private ExceptionDispatchInfo? _partitionWorkerFailure;
    private long _actualTargetCount;
    private int _maxSimultaneousPartitionWorkers;
    private long _referenceDmlOperations;
    private long _referenceDmlMicroseconds;
    private long[] _partitionActualRows = [0];
    private long[] _partitionJournalRows = [0];
    private long[] _partitionDbHighWater = [0];
    private long[] _partitionWalHighWater = [0];
    private PartitionWorkerPhaseMetrics[] _actualWorkerMetrics = [new()];
    private PartitionWorkerPhaseMetrics[] _journalWorkerMetrics = [new()];
    private long _sqliteBusyExceptions;
    private int _activePartitionWorkers;
    private int _activeActualWorkers;
    private int _maxActualWorkers;
    private int _activeJournalWorkers;
    private int _maxJournalWorkers;
        private int _actualWorkerMetricsRecorded;
        private int _journalWorkerMetricsRecorded;
    private string? _ruleScanId;
    private long _lookupCalls;
    private long _lookupMicroseconds;
    private long _lookupMatches;

    internal void SetRuleScanContext(string? ruleId)
    {
        if (_ruleScanId is not null && _lookupCalls > 0)
            _performanceRecorder?.RecordMeasuredStage(PerformanceStageKind.Verification, $"verification rule lookup {_ruleScanId}",
                _lookupMicroseconds, artifactCount: _lookupMatches,
                measurements: [new PerformanceMeasurement("lookupQueryExecutions", _lookupCalls, "queries")]);
        _lookupCalls = 0;
        _lookupMicroseconds = 0;
        _lookupMatches = 0;
        _ruleScanId = ruleId;
    }

    public long SourceArtifactCount { get; private set; }
    public long ExpectedTargetCount { get; private set; }
    public long ActualTargetCount => Interlocked.Read(ref _actualTargetCount);

    public void SetRuleEvaluationPartition(int? partitionIndex)
    {
        if (partitionIndex is { } index && (index < 0 || index >= _partitionCount))
            throw new ArgumentOutOfRangeException(nameof(partitionIndex));
        _ruleEvaluationPartition = partitionIndex;
    }

    private SqliteVerificationWorkspace(string directory, string databasePath, SqliteConnection connection,
        PerformanceRecorder? performanceRecorder, VerificationExecutionPlan? executionPlan)
    {
        _directory = directory;
        _databasePath = databasePath;
        _connection = connection;
        _performanceRecorder = performanceRecorder;
        _executionPlan = executionPlan;
    }

    public static Task<SqliteVerificationWorkspace> CreateAsync(string temporaryRoot, CancellationToken cancellationToken) =>
        CreateAsync(temporaryRoot, performanceRecorder: null, cancellationToken);

    public static async Task<SqliteVerificationWorkspace> CreateAsync(string temporaryRoot,
        PerformanceRecorder? performanceRecorder, CancellationToken cancellationToken)
        => await CreateAsync(temporaryRoot, performanceRecorder, executionPlan: null, cancellationToken).ConfigureAwait(false);

    public static async Task<SqliteVerificationWorkspace> CreateAsync(string temporaryRoot,
        PerformanceRecorder? performanceRecorder, VerificationExecutionPlan? executionPlan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        var directory = Path.Combine(Path.GetFullPath(temporaryRoot), $"proofshift-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "working-set.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseUri(path),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateCollation("DECIMAL_ORDER", CompareDecimalStrings);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
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
                    partition_bucket INTEGER NOT NULL,
                    semantic_type TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    source_node_key TEXT NOT NULL,
                    edge_id TEXT NOT NULL,
                    edge_name TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    record_json TEXT NOT NULL
                );
                CREATE INDEX expected_identity_idx ON expected_targets(node_key, identity_hash);
                CREATE INDEX expected_journal_binding_idx ON expected_targets(
                    node_key,target_id,source_node_key,source_id,edge_id,target_system,target_endpoint,target_type,identity_hash);
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
                CREATE INDEX journal_produced_ancestry_idx ON journal_entries(result,target_node,target_id,edge_id);
                CREATE TABLE journal_sources (
                    journal_seq INTEGER NOT NULL,
                    source_id TEXT NOT NULL,
                    source_node_key TEXT NOT NULL,
                    source_system TEXT NOT NULL,
                    source_endpoint TEXT NOT NULL,
                    source_type TEXT NOT NULL,
                    source_identity_hash TEXT NOT NULL,
                    PRIMARY KEY(journal_seq, source_node_key, source_id)
                );
                CREATE INDEX journal_source_idx ON journal_sources(source_id);
                CREATE INDEX journal_source_binding_idx ON journal_sources(
                    source_node_key,source_id,source_system,source_endpoint,source_type,source_identity_hash,journal_seq);
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
                CREATE TABLE artifact_order_keys (
                    role TEXT NOT NULL,
                    node_key TEXT NOT NULL,
                    semantic_type TEXT NOT NULL,
                    artifact_id TEXT NOT NULL,
                    record_seq INTEGER NOT NULL,
                    field TEXT NOT NULL,
                    key_kind INTEGER NOT NULL,
                    text_value TEXT,
                    integer_value INTEGER,
                    decimal_value TEXT COLLATE DECIMAL_ORDER,
                    temporal_value TEXT,
                    boolean_value INTEGER,
                    PRIMARY KEY(role,node_key,artifact_id,record_seq,field)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteVerificationWorkspace(directory, path, connection, performanceRecorder, executionPlan);
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
        if (_sharedExpected is not null || _publishedExpected) throw new InvalidOperationException("Shared source state is immutable.");
        var transaction = GetWriteTransaction();
        var command = GetWriteCommand("INSERT INTO source_artifacts (artifact_id,node_key,system_id,endpoint_id,artifact_type,identity_hash,semantic_type,record_hash,record_json) VALUES ($id,$node,$system,$endpoint,$type,$identity,$semantic,$record,$recordJson) ON CONFLICT(node_key,artifact_id) DO NOTHING", transaction);
        AddArtifactParameters(command, record.Artifact, nodeKey);
        command.Parameters.AddWithValue("$semantic", record.SemanticType);
        command.Parameters.AddWithValue("$record", SnapshotFingerprints.RecordFingerprint(record));
        var requiredFields = GetRequiredFields(VerificationArtifactRole.Source, record.SemanticType);
        var fullSerializedBytes = 0L;
        var recordJson = _performanceRecorder is null
            ? VerificationArtifactRecordCodec.Encode(record, requiredFields)
            : VerificationArtifactRecordCodec.Encode(record, requiredFields, out fullSerializedBytes);
        command.Parameters.AddWithValue("$recordJson", recordJson);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            RecordWorksetMaterialization(VerificationArtifactRole.Source, record, requiredFields, recordJson, fullSerializedBytes);
            SourceArtifactCount++;
            await AddOrderingKeysAsync(transaction, VerificationArtifactRole.Source, nodeKey, record, 0, cancellationToken).ConfigureAwait(false);
        }
        await CompleteWriteOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ContainsSourceArtifactAsync(string nodeKey, ArtifactReference artifact, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.Transaction = _writeTransaction;
        command.CommandText = "SELECT 1 FROM source_artifacts WHERE node_key=$node AND artifact_id=$id AND system_id=$system AND endpoint_id=$endpoint AND artifact_type=$type AND identity_hash=$identity LIMIT 1";
        command.Parameters.AddWithValue("$node", nodeKey);
        command.Parameters.AddWithValue("$id", artifact.Id.Value);
        command.Parameters.AddWithValue("$system", artifact.SystemId.Value);
        command.Parameters.AddWithValue("$endpoint", artifact.EndpointId.Value);
        command.Parameters.AddWithValue("$type", artifact.ArtifactType);
        command.Parameters.AddWithValue("$identity", IdentityHash(artifact.Identity));
        return await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> ContainsFieldValueAsync(VerificationArtifactRole role, string nodeKey, string semanticType,
        string field, ValueNode value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        if (role == VerificationArtifactRole.ExpectedTarget && _ruleEvaluationPartition is not null)
            await EnsureExpectedPartitionIndexForEvaluationAsync(cancellationToken).ConfigureAwait(false);
        if (_executionPlan is not null && !_executionPlan.GetRequiredKeys(role, semanticType).Any(key =>
            key.Field == field && key.Role == VerificationOrderingRole.Lookup))
            throw new VerificationRuleException("PSRULE008",
                $"Lookup field '{field}' is not declared for the {role} workset.");
        await EnsureOrderingIndexAsync(cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeOrderingValue(value);
        var valueColumn = normalized.Kind switch
        {
            0 => null,
            1 or 9 or 10 or 11 => "text_value",
            2 => "integer_value",
            3 => "decimal_value",
            4 or 5 or 6 or 7 => "temporal_value",
            8 => "boolean_value",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        await using var command = _connection.CreateCommand();
        command.CommandText = valueColumn is null
            ? "SELECT 1 FROM artifact_order_keys WHERE role=$role AND node_key=$node AND semantic_type=$semantic AND field=$field AND key_kind=0 LIMIT 1"
            : $"SELECT 1 FROM artifact_order_keys WHERE role=$role AND node_key=$node AND semantic_type=$semantic AND field=$field AND key_kind=$kind AND {valueColumn}=$value LIMIT 1";
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$node", nodeKey);
        command.Parameters.AddWithValue("$semantic", semanticType);
        command.Parameters.AddWithValue("$field", field);
        if (valueColumn is not null)
        {
            command.Parameters.AddWithValue("$kind", normalized.Kind);
            object? normalizedValue = valueColumn switch
            {
                "text_value" => normalized.Text,
                "integer_value" => normalized.Integer,
                "decimal_value" => normalized.Decimal,
                "temporal_value" => normalized.Temporal,
                "boolean_value" => normalized.Boolean,
                _ => null
            };
            command.Parameters.AddWithValue("$value", normalizedValue ?? (object)DBNull.Value);
        }
        var started = _performanceRecorder is null ? 0 : Stopwatch.GetTimestamp();
        var found = await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false) is not null;
        if (_performanceRecorder is not null && _ruleScanId is not null)
        {
            _lookupCalls++;
            _lookupMatches += found ? 1 : 0;
            _lookupMicroseconds += checked((long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMicroseconds));
        }
        return found;
    }

    public async Task AddExpectedTargetAsync(string nodeKey, RecordEnvelope expected, string sourceNodeKey, RecordEnvelope source,
        MigrationEdge edge, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sharedExpected is not null || _publishedExpected) throw new InvalidOperationException("Shared expected state is immutable.");
        var transaction = GetWriteTransaction();
        var targetCommand = GetWriteCommand("INSERT INTO expected_targets (node_key,target_id,target_system,target_endpoint,target_type,identity_hash,partition_bucket,semantic_type,source_id,source_node_key,edge_id,edge_name,operation,record_json) VALUES ($node,$id,$system,$endpoint,$type,$identity,$partition,$semantic,$source,$sourceNode,$edge,$edgeName,$operation,$recordJson)", transaction);
        AddArtifactParameters(targetCommand, expected.Artifact, nodeKey, "$target");
        targetCommand.Parameters.AddWithValue("$partition", AssignStableTargetBucket(nodeKey, expected.Artifact.Identity));
        targetCommand.Parameters.AddWithValue("$semantic", expected.SemanticType);
        targetCommand.Parameters.AddWithValue("$source", source.Artifact.Id.Value);
        targetCommand.Parameters.AddWithValue("$sourceNode", sourceNodeKey);
        targetCommand.Parameters.AddWithValue("$edge", edge.Id.Value.ToString("D", CultureInfo.InvariantCulture));
        targetCommand.Parameters.AddWithValue("$edgeName", edge.Name);
        targetCommand.Parameters.AddWithValue("$operation", edge.Operation.Type.ToString());
        var requiredFields = GetRequiredFields(VerificationArtifactRole.ExpectedTarget, expected.SemanticType);
        var fullSerializedBytes = 0L;
        var recordJson = _performanceRecorder is null
            ? VerificationArtifactRecordCodec.Encode(expected, requiredFields)
            : VerificationArtifactRecordCodec.Encode(expected, requiredFields, out fullSerializedBytes);
        targetCommand.Parameters.AddWithValue("$recordJson", recordJson);
        await targetCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var expectedSequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        await AddOrderingKeysAsync(transaction, VerificationArtifactRole.ExpectedTarget, nodeKey, expected, expectedSequence,
            cancellationToken).ConfigureAwait(false);
        foreach (var pair in expected.Values.Where(pair => requiredFields is null || requiredFields.Contains(pair.Key, StringComparer.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var valueCommand = GetWriteCommand("INSERT INTO expected_values (expected_seq,field,value_hash) VALUES ($seq,$field,$hash)", transaction);
            valueCommand.Parameters.AddWithValue("$seq", expectedSequence);
            valueCommand.Parameters.AddWithValue("$field", pair.Key);
            valueCommand.Parameters.AddWithValue("$hash", FingerprintValue(pair.Value));
            await valueCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        RecordWorksetMaterialization(VerificationArtifactRole.ExpectedTarget, expected, requiredFields, recordJson,
            fullSerializedBytes);
        ExpectedTargetCount++;
        await CompleteWriteOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ContainsExpectedTargetAsync(string nodeKey, ArtifactReference target, string sourceNodeKey,
        ArtifactReference source, MigrationEdgeId edgeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var command = _connection.CreateCommand();
        command.Transaction = _writeTransaction;
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
        return await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task AddJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var partitionIndex = entry.Target is not null && entry.TargetNode is not null
            ? AssignScratchPartition(entry.TargetNode, entry.Target.Identity)
            : entry.Sources.Count > 0 ? AssignScratchPartition(entry.Sources[0].NodeKey, entry.Sources[0].Artifact.Id.Value) : 0;
        var previousPartition = _activePartition.Value;
        _activePartition.Value = _partitionCount > 1 ? partitionIndex : null;
        try
        {
            await AddJournalEntryCoreAsync(entry, partitionIndex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activePartition.Value = previousPartition;
        }
    }

    internal Task QueueJournalEntryAsync(VerificationJournalEntry entry, CancellationToken cancellationToken)
    {
        var partitionIndex = entry.Target is not null && entry.TargetNode is not null
            ? AssignScratchPartition(entry.TargetNode, entry.Target.Identity)
            : entry.Sources.Count > 0 ? AssignScratchPartition(entry.Sources[0].NodeKey, entry.Sources[0].Artifact.Id.Value) : 0;
        return QueuePartitionOperationAsync(partitionIndex, PartitionWorkKind.JournalEntry,
            token => AddJournalEntryAsync(entry, token), cancellationToken);
    }

    private async Task AddJournalEntryCoreAsync(VerificationJournalEntry entry, int partitionIndex,
        CancellationToken cancellationToken)
    {
        var measureJournalWrites = _performanceRecorder is not null;
        var preparationStarted = measureJournalWrites ? Stopwatch.GetTimestamp() : 0;
        var transaction = GetWriteTransaction();
        var command = GetWriteCommand("INSERT INTO journal_entries (result,target_node,target_id,target_system,target_endpoint,target_type,target_identity_hash,edge_id,edge_name,edge_version,failure_code) VALUES ($result,$node,$id,$system,$endpoint,$type,$identity,$edge,$edgeName,$edgeVersion,$failure)", transaction);
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
        if (measureJournalWrites)
            Interlocked.Add(ref _journalPreparationTicks, Stopwatch.GetElapsedTime(preparationStarted).Ticks);
        var executionStarted = measureJournalWrites ? Stopwatch.GetTimestamp() : 0;
        await ExecuteScratchDmlAsync(command, cancellationToken).ConfigureAwait(false);
        if (measureJournalWrites)
        {
            Interlocked.Add(ref _journalExecutionTicks, Stopwatch.GetElapsedTime(executionStarted).Ticks);
            Interlocked.Increment(ref _journalStatementCount);
        }
        var sequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        foreach (var source in entry.Sources)
        {
            preparationStarted = measureJournalWrites ? Stopwatch.GetTimestamp() : 0;
            var sourceCommand = GetWriteCommand("INSERT OR IGNORE INTO journal_sources (journal_seq,source_id,source_node_key,source_system,source_endpoint,source_type,source_identity_hash) VALUES ($seq,$source,$sourceNode,$system,$endpoint,$type,$identity)", transaction);
            sourceCommand.Parameters.AddWithValue("$seq", sequence);
            sourceCommand.Parameters.AddWithValue("$source", source.Artifact.Id.Value);
            sourceCommand.Parameters.AddWithValue("$sourceNode", source.NodeKey);
            sourceCommand.Parameters.AddWithValue("$system", source.Artifact.SystemId.Value);
            sourceCommand.Parameters.AddWithValue("$endpoint", source.Artifact.EndpointId.Value);
            sourceCommand.Parameters.AddWithValue("$type", source.Artifact.ArtifactType);
            sourceCommand.Parameters.AddWithValue("$identity", IdentityHash(source.Artifact.Identity));
            if (measureJournalWrites)
                Interlocked.Add(ref _journalPreparationTicks, Stopwatch.GetElapsedTime(preparationStarted).Ticks);
            executionStarted = measureJournalWrites ? Stopwatch.GetTimestamp() : 0;
            await ExecuteScratchDmlAsync(sourceCommand, cancellationToken).ConfigureAwait(false);
            if (measureJournalWrites)
            {
                Interlocked.Add(ref _journalExecutionTicks, Stopwatch.GetElapsedTime(executionStarted).Ticks);
                Interlocked.Increment(ref _journalStatementCount);
            }
        }
        if (measureJournalWrites)
        {
            Interlocked.Increment(ref _journalEntryCount);
            Interlocked.Increment(ref _journalPendingEntryCount);
            if (_partitionCount > 1) _partitionWriters[partitionIndex].PendingJournal++;
        }
        await CompleteWriteOperationAsync(cancellationToken).ConfigureAwait(false);
        _partitionJournalRows[partitionIndex]++;
    }

    public async Task<VerificationJournalValidationResult> ValidateJournalEntriesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureExpectedPartitionIndexForEvaluationAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                NOT EXISTS (
                    SELECT 1
                    FROM journal_sources js
                    LEFT JOIN source_artifacts s
                      ON s.node_key=js.source_node_key AND s.artifact_id=js.source_id
                     AND s.system_id=js.source_system AND s.endpoint_id=js.source_endpoint
                     AND s.artifact_type=js.source_type AND s.identity_hash=js.source_identity_hash
                    WHERE s.artifact_id IS NULL
                ),
                NOT EXISTS (
                    SELECT 1
                    FROM journal_entries j
                    WHERE j.result='produced' AND NOT EXISTS (
                        SELECT 1
                        FROM journal_sources js
                        JOIN expected_targets e
                          ON e.node_key=j.target_node AND e.target_id=j.target_id
                         AND e.target_system=j.target_system AND e.target_endpoint=j.target_endpoint
                         AND e.target_type=j.target_type AND e.identity_hash=j.target_identity_hash
                         AND e.source_id=js.source_id AND e.source_node_key=js.source_node_key
                         AND e.edge_id=j.edge_id
                        WHERE js.journal_seq=j.seq
                    )
                ),
                NOT EXISTS (
                    SELECT 1 FROM (
                        SELECT j.target_node,j.target_id,js.source_node_key,js.source_id,j.edge_id
                        FROM journal_entries j
                        JOIN journal_sources js ON js.journal_seq=j.seq
                        WHERE j.result='produced'
                        GROUP BY j.target_node,j.target_id,js.source_node_key,js.source_id,j.edge_id
                        HAVING COUNT(*)>1
                    ) duplicates
                ),
                (SELECT COUNT(*) FROM journal_entries WHERE result='produced')
            """;
        await CaptureQueryPlanAsync("journal validation", command, cancellationToken).ConfigureAwait(false);
            var measureQuery = _performanceRecorder is not null;
            var queryStarted = measureQuery ? Stopwatch.GetTimestamp() : 0;
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQLite journal validation did not return its summary row.");
        var result = new VerificationJournalValidationResult(reader.GetInt64(3), reader.GetBoolean(0),
            reader.GetBoolean(1), reader.GetBoolean(2));
        if (measureQuery)
        {
            var queryElapsedMicroseconds = checked((long)Math.Round(
                Stopwatch.GetElapsedTime(queryStarted).TotalMicroseconds));
            _performanceRecorder!.RecordMeasuredStage(PerformanceStageKind.Verification,
                "SQLite journal validation query", queryElapsedMicroseconds,
                measurements:
                [
                    new PerformanceMeasurement("queryExecutions", 1, "queries"),
                    new PerformanceMeasurement("rowsReturned", 1, "rows")
                ]);
        }
        return result;
    }

    public async Task AddTargetObservationAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var partitionIndex = AssignScratchPartition(nodeKey, record.Artifact.Identity);
        var previousPartition = _activePartition.Value;
        _activePartition.Value = _partitionCount > 1 ? partitionIndex : null;
        try
        {
            await AddTargetObservationCoreAsync(nodeKey, record, partitionIndex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activePartition.Value = previousPartition;
        }
    }

    internal Task QueueTargetObservationAsync(string nodeKey, RecordEnvelope record, CancellationToken cancellationToken)
    {
        var partitionIndex = AssignScratchPartition(nodeKey, record.Artifact.Identity);
        return QueuePartitionOperationAsync(partitionIndex, PartitionWorkKind.ActualTarget,
            token => AddTargetObservationAsync(nodeKey, record, token), cancellationToken);
    }

    private async Task AddTargetObservationCoreAsync(string nodeKey, RecordEnvelope record, int partitionIndex,
        CancellationToken cancellationToken)
    {
        var transaction = GetWriteTransaction();
        var targetCommand = GetWriteCommand("INSERT INTO actual_targets (node_key,target_id,target_system,target_endpoint,target_type,identity_hash,semantic_type,record_hash,record_json) VALUES ($node,$id,$system,$endpoint,$type,$identity,$semantic,$record,$recordJson)", transaction);
        AddArtifactParameters(targetCommand, record.Artifact, nodeKey, "$target");
        targetCommand.Parameters.AddWithValue("$semantic", record.SemanticType);
        targetCommand.Parameters.AddWithValue("$record", SnapshotFingerprints.RecordFingerprint(record));
        var requiredFields = GetRequiredFields(VerificationArtifactRole.ActualTarget, record.SemanticType);
        var fullSerializedBytes = 0L;
        var recordJson = _performanceRecorder is null
            ? VerificationArtifactRecordCodec.Encode(record, requiredFields)
            : VerificationArtifactRecordCodec.Encode(record, requiredFields, out fullSerializedBytes);
        targetCommand.Parameters.AddWithValue("$recordJson", recordJson);
        await ExecuteScratchDmlAsync(targetCommand, cancellationToken).ConfigureAwait(false);
        var sequence = await ReadLastInsertRowIdAsync(transaction, cancellationToken).ConfigureAwait(false);
        await AddOrderingKeysAsync(transaction, VerificationArtifactRole.ActualTarget, nodeKey, record, sequence,
            cancellationToken).ConfigureAwait(false);
        foreach (var pair in record.Values.Where(pair => requiredFields is null || requiredFields.Contains(pair.Key, StringComparer.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var valueCommand = GetWriteCommand("INSERT INTO actual_values (target_seq,field,value_hash) VALUES ($seq,$field,$hash)", transaction);
            valueCommand.Parameters.AddWithValue("$seq", sequence);
            valueCommand.Parameters.AddWithValue("$field", pair.Key);
            valueCommand.Parameters.AddWithValue("$hash", FingerprintValue(pair.Value));
            await ExecuteScratchDmlAsync(valueCommand, cancellationToken).ConfigureAwait(false);
        }
        RecordWorksetMaterialization(VerificationArtifactRole.ActualTarget, record, requiredFields, recordJson,
            fullSerializedBytes);
        Interlocked.Increment(ref _actualTargetCount);
        _partitionActualRows[partitionIndex]++;
        await CompleteWriteOperationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<VerificationSourceFact> ReadSourceFactsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureExpectedPartitionIndexForEvaluationAsync(cancellationToken).ConfigureAwait(false);
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
        await CaptureQueryPlanAsync("source disposition facts", command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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

    public async IAsyncEnumerable<VerificationSourceFact> ReadGraphDerivedSourceFactsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureExpectedPartitionIndexForEvaluationAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.artifact_id,s.node_key,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,s.semantic_type,
                   COUNT(DISTINCT e.seq),0,0,group_concat(DISTINCT e.target_id),group_concat(DISTINCT e.edge_id)
            FROM source_artifacts s
            LEFT JOIN expected_targets e ON e.source_id=s.artifact_id AND e.source_node_key=s.node_key
            GROUP BY s.artifact_id,s.node_key,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,s.semantic_type
            ORDER BY s.node_key,s.artifact_id
            """;
        await CaptureQueryPlanAsync("graph-derived source disposition facts", command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationSourceFact(
                reader.GetString(1), reader.GetString(6), MakeArtifact(reader.GetString(0), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.GetInt32(7), 0, 0,
                SplitIds(reader.IsDBNull(10) ? null : reader.GetString(10)).Select(value => new ArtifactId(value)).ToArray(),
                SplitIds(reader.IsDBNull(11) ? null : reader.GetString(11)).Select(value => new MigrationEdgeId(Guid.Parse(value))).ToArray());
        }
    }

    public IAsyncEnumerable<VerificationTargetFact> ReadMaterializedJournalTargetsAsync(CancellationToken cancellationToken) =>
        ReadJournalTargetFactsAsync(requiredActualCount: null, cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadGraphDerivedTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadGraphDerivedTargetFactsAsync(requiredActualCount: null, cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadMissingGraphDerivedTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadGraphDerivedTargetFactsAsync(requiredActualCount: 0, cancellationToken);

    public IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsAsync(VerificationArtifactRole role,
        string? nodeKey, string? semanticType,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? orderByFields = null)
    {
        var keys = (orderByFields ?? []).Select(field => new VerificationOrderingKey(
            semanticType ?? string.Empty, field, VerificationOrderingRole.Ordering)).ToArray();
        return ReadArtifactRecordsByKeysAsync(role, nodeKey, semanticType, keys, cancellationToken);
    }

    public async IAsyncEnumerable<VerificationArtifactRecord> ReadArtifactRecordsByKeysAsync(VerificationArtifactRole role,
        string? nodeKey, string? semanticType, IReadOnlyCollection<VerificationOrderingKey> orderByKeys,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        if (orderByKeys.Count > 0)
        {
            ValidatePlannedKeys(role, orderByKeys);
            await EnsureOrderingIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        var (table, idColumn, sequenceColumn) = role switch
        {
            VerificationArtifactRole.Source => ("source_artifacts", "artifact_id", "rowid"),
            VerificationArtifactRole.ExpectedTarget => ("expected_targets", "target_id", "seq"),
            VerificationArtifactRole.ActualTarget => ("actual_targets", "target_id", "seq"),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var filters = new List<string>();
        var ordering = new List<string> { "t.node_key" };
        var joins = new List<string>();
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
        if (role == VerificationArtifactRole.ExpectedTarget && _ruleEvaluationPartition is not null && _partitionCount > 1)
            filters.Add(ExpectedPartitionPredicate(command, "t"));
        command.Parameters.AddWithValue("$role", role.ToString());
        var orderIndex = 0;
        foreach (var key in orderByKeys)
        {
            if (string.IsNullOrWhiteSpace(key.Field)) throw new ArgumentException("Ordering key fields must not be empty.", nameof(orderByKeys));
            var parameter = $"$orderField{orderIndex}";
            var alias = $"order_key{orderIndex}";
            command.Parameters.AddWithValue(parameter, key.Field.Trim());
            var sequenceMatch = role == VerificationArtifactRole.Source ? $"{alias}.record_seq=0" : $"{alias}.record_seq=t.{sequenceColumn}";
            joins.Add($"LEFT JOIN artifact_order_keys AS {alias} ON {alias}.role=$role AND {alias}.node_key=t.node_key AND {alias}.semantic_type=t.semantic_type AND {alias}.artifact_id=t.{idColumn} AND {sequenceMatch} AND {alias}.field={parameter}");
            var direction = key.Descending ? " DESC" : " ASC";
            ordering.Add($"{alias}.key_kind{direction}");
            ordering.Add($"{alias}.text_value{direction}");
            ordering.Add($"{alias}.integer_value{direction}");
            ordering.Add($"{alias}.decimal_value COLLATE DECIMAL_ORDER{direction}");
            ordering.Add($"{alias}.temporal_value{direction}");
            ordering.Add($"{alias}.boolean_value{direction}");
            orderIndex++;
        }
        ordering.Add($"t.identity_hash");
        ordering.Add($"t.{idColumn}");
        command.CommandText = $"SELECT t.node_key,t.semantic_type,t.record_json FROM {table} AS t {string.Join(' ', joins)}{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))} ORDER BY {string.Join(',', ordering)}";
        if (_partitionCount > 1 && role == VerificationArtifactRole.ActualTarget)
        {
            var projections = ordering.Select((expression, index) =>
                expression.Replace(" DESC", string.Empty, StringComparison.Ordinal).Replace(" ASC", string.Empty, StringComparison.Ordinal)
                    .Replace(" COLLATE DECIMAL_ORDER", string.Empty, StringComparison.Ordinal) + $" AS sort{index}").ToArray();
            var finalOrdering = ordering.Select((expression, index) => $"sort{index}" +
                (expression.Contains(" COLLATE DECIMAL_ORDER", StringComparison.Ordinal) ? " COLLATE DECIMAL_ORDER" : string.Empty) +
                (expression.EndsWith(" DESC", StringComparison.Ordinal) ? " DESC" : " ASC")).ToArray();
            var partitionIndexes = _ruleEvaluationPartition is { } selectedPartition
                ? [selectedPartition]
                : Enumerable.Range(0, _partitionCount);
            var branches = partitionIndexes.Select(index =>
            {
                var localJoins = string.Join(' ', joins).Replace("LEFT JOIN artifact_order_keys", $"LEFT JOIN partition{index}.artifact_order_keys", StringComparison.Ordinal);
                return $"SELECT t.node_key,t.semantic_type,t.record_json,{string.Join(',', projections)} FROM partition{index}.actual_targets AS t {localJoins}{(filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters))}";
            });
            command.CommandText = $"SELECT node_key,semantic_type,record_json FROM ({string.Join(" UNION ALL ", branches)}) ORDER BY {string.Join(',', finalOrdering)}";
        }
        var queryCategory = $"SQLite ordered {role} artifact query";
        await CaptureQueryPlanAsync($"ordered-{role}-{string.Join(',', orderByKeys.Select(key => key.Field))}", command,
            cancellationToken).ConfigureAwait(false);
        var measureQuery = _performanceRecorder is not null;
        double queryElapsedMicroseconds = 0;
        var operationStarted = measureQuery ? Stopwatch.GetTimestamp() : 0;
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
        if (measureQuery)
            queryElapsedMicroseconds += Stopwatch.GetElapsedTime(operationStarted).TotalMicroseconds;
        long rowsReturned = 0;
        long decodedValues = 0;
        var completedScan = false;
        try
        {
            while (true)
            {
                operationStarted = measureQuery ? Stopwatch.GetTimestamp() : 0;
                var hasRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (measureQuery)
                    queryElapsedMicroseconds += Stopwatch.GetElapsedTime(operationStarted).TotalMicroseconds;
                if (!hasRow) { completedScan = true; break; }
                cancellationToken.ThrowIfCancellationRequested();
                operationStarted = measureQuery ? Stopwatch.GetTimestamp() : 0;
                var recordSemanticType = reader.GetString(1);
                var artifact = VerificationArtifactRecordCodec.Decode(reader.GetString(2), reader.GetString(0), role,
                    recordSemanticType, GetRequiredFields(role, recordSemanticType));
                if (measureQuery)
                    queryElapsedMicroseconds += Stopwatch.GetElapsedTime(operationStarted).TotalMicroseconds;
                rowsReturned++;
                if (measureQuery) decodedValues += artifact.Values.Count;
                yield return artifact;
            }
        }
        finally
        {
        if (measureQuery)
            _performanceRecorder!.RecordMeasuredStage(PerformanceStageKind.Verification, queryCategory,
                checked((long)Math.Round(queryElapsedMicroseconds)), artifactCount: rowsReturned,
                measurements:
                [
                    new PerformanceMeasurement("queryExecutions", 1, "queries"),
                    new PerformanceMeasurement("rowsReturned", rowsReturned, "rows")
                ]);
        if (measureQuery)
            _performanceRecorder!.RecordMeasuredStage(PerformanceStageKind.Verification,
                $"verification rule scan {_ruleScanId ?? "system"} {role} {semanticType ?? "*"}", 0,
                artifactCount: rowsReturned, measurements:
                [
                    new PerformanceMeasurement("scanExecutions", 1, "scans"),
                    new PerformanceMeasurement("rowsReturned", rowsReturned, "rows"),
                    new PerformanceMeasurement("typedRecordDecodes", rowsReturned, "records"),
                    new PerformanceMeasurement("fieldValueDecodes", decodedValues, "values"),
                    new PerformanceMeasurement("completedScans", completedScan ? 1 : 0, "scans"),
                    new PerformanceMeasurement("partialScans", completedScan ? 0 : 1, "scans")
                ]);
        }
    }

    public IAsyncEnumerable<VerificationTargetFact> ReadMissingTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadJournalTargetFactsAsync(requiredActualCount: 0, cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadActualTargetsAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("1=1", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("NOT EXISTS (SELECT 1 FROM journal_entries j WHERE j.result='produced' AND j.target_node=a.node_key AND j.target_identity_hash=a.identity_hash)", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadUnexpectedGraphTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("NOT EXISTS (SELECT 1 FROM expected_targets e WHERE e.node_key=a.node_key AND e.identity_hash=a.identity_hash)", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadDuplicateTargetFactsAsync(CancellationToken cancellationToken) =>
        ReadDuplicateFactsAsync(cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutLineageAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync(_partitionCount == 1
            ? "NOT EXISTS (SELECT 1 FROM journal_entries j JOIN journal_sources js ON js.journal_seq=j.seq WHERE j.result='produced' AND j.target_node=a.node_key AND j.target_identity_hash=a.identity_hash)"
            : "NOT (" + string.Join(" OR ", Enumerable.Range(0, _partitionCount).Select(index =>
                $"EXISTS (SELECT 1 FROM partition{index}.journal_entries j JOIN partition{index}.journal_sources js ON js.journal_seq=j.seq WHERE j.result='produced' AND j.target_node=a.node_key AND j.target_identity_hash=a.identity_hash)")) + ")", cancellationToken);

    public IAsyncEnumerable<VerificationTargetFact> ReadTargetsWithoutGraphDerivedLineageAsync(CancellationToken cancellationToken) =>
        ReadActualTargetFactsAsync("NOT EXISTS (SELECT 1 FROM expected_targets e INDEXED BY expected_identity_idx JOIN source_artifacts s ON s.artifact_id=e.source_id AND s.node_key=e.source_node_key WHERE e.node_key=a.node_key AND e.identity_hash=a.identity_hash)", cancellationToken);

    public async IAsyncEnumerable<VerificationAttributeComparison> ReadAttributeComparisonsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
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
        if (_ruleEvaluationPartition is { } partitionIndex && _partitionCount > 1)
        {
            var expectedPartition = ExpectedPartitionPredicate(command, "e");
            command.CommandText = command.CommandText
                .Replace("FROM actual_targets d", $"FROM partition{partitionIndex}.actual_targets d", StringComparison.Ordinal)
                .Replace("JOIN actual_targets a", $"JOIN partition{partitionIndex}.actual_targets a", StringComparison.Ordinal)
                .Replace("JOIN actual_values av", $"JOIN partition{partitionIndex}.actual_values av", StringComparison.Ordinal)
                .Replace("FROM journal_entries j", $"FROM partition{partitionIndex}.journal_entries j", StringComparison.Ordinal)
                .Replace("JOIN journal_sources js", $"JOIN partition{partitionIndex}.journal_sources js", StringComparison.Ordinal)
                .Replace("WHERE (SELECT COUNT(*)", $"WHERE {expectedPartition} AND (SELECT COUNT(*)", StringComparison.Ordinal);
        }
        await CaptureQueryPlanAsync("attribute comparisons", command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
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
        command.CommandText = LocalJournalBranches(command.CommandText, "ORDER BY j.target_node,j.target_id,s.artifact_id,j.edge_id", "ORDER BY 12,1,6,11");
        await CaptureQueryPlanAsync("projection lineage", command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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

    public async IAsyncEnumerable<LineageRecord> ReadGraphDerivedLineageAsync(string graphHash,
        IReadOnlyDictionary<string, MigrationNodeId> graphNodeIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var target in ReadGraphDerivedTargetFactsAsync(requiredActualCount: null, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.ActualCount == 0 || target.Sources.Count == 0 || target.EdgeIds.Count == 0) continue;
            yield return new LineageRecord(target.Artifact, target.Sources.Select(source => source.Artifact), target.EdgeIds,
                graphHash, graphNodeIds[target.NodeKey], target.Sources.Select(source => graphNodeIds[source.NodeKey]),
                LineageBasis.GraphDerivedExpected);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopPartitionWorkersAsync().ConfigureAwait(false);
        if (_performanceRecorder is not null && _writeTransaction is null && !_publishedExpected && _partitionWriters.All(writer => writer.Transaction is null))
            await WriteDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        foreach (var writer in _partitionWriters)
            if (writer.Transaction is { } partitionTransaction)
            {
                writer.Transaction = null;
                try { await partitionTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { await partitionTransaction.DisposeAsync().ConfigureAwait(false); }
            }
        if (_writeTransaction is { } transaction)
        {
            _writeTransaction = null;
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { await transaction.DisposeAsync().ConfigureAwait(false); }
        }
        foreach (var command in _writeCommands.Values)
            await command.DisposeAsync().ConfigureAwait(false);
        if (_orderingKeyInsert is not null)
            await _orderingKeyInsert.DisposeAsync().ConfigureAwait(false);
        foreach (var writer in _partitionWriters)
        {
            foreach (var command in writer.WriteCommands.Values)
                await command.DisposeAsync().ConfigureAwait(false);
            if (writer.OrderingKeyInsert is not null)
                await writer.OrderingKeyInsert.DisposeAsync().ConfigureAwait(false);
            await writer.Connection.DisposeAsync().ConfigureAwait(false);
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _partitionWorkerStop.Dispose();
        if (!_retainPublishedExpected)
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            TryDeleteDirectory(_directory);
        }
    }

    private async IAsyncEnumerable<VerificationTargetFact> ReadJournalTargetFactsAsync(int? requiredActualCount,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
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
        if (_ruleEvaluationPartition is { } partitionIndex && _partitionCount > 1)
        {
            var expectedPartition = ExpectedPartitionPredicate(command, "e");
            command.CommandText = command.CommandText
                .Replace("FROM journal_entries j", $"FROM partition{partitionIndex}.journal_entries j", StringComparison.Ordinal)
                .Replace("JOIN journal_sources js", $"JOIN partition{partitionIndex}.journal_sources js", StringComparison.Ordinal)
                .Replace("FROM actual_targets a", $"FROM partition{partitionIndex}.actual_targets a", StringComparison.Ordinal)
                .Replace("WHERE j.result='produced' AND j.target_id IS NOT NULL",
                    $"WHERE j.result='produced' AND j.target_id IS NOT NULL AND EXISTS (SELECT 1 FROM expected_targets e WHERE e.node_key=j.target_node AND e.identity_hash=j.target_identity_hash AND {expectedPartition})",
                    StringComparison.Ordinal);
        }
        else
        {
            command.CommandText = LocalJournalBranches(command.CommandText,
                "ORDER BY j.target_node,j.target_identity_hash,j.target_id,s.artifact_id,j.edge_id", "ORDER BY 1,6,2,10,15");
        }
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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

    private async IAsyncEnumerable<VerificationTargetFact> ReadGraphDerivedTargetFactsAsync(int? requiredActualCount,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.node_key,e.target_id,e.target_system,e.target_endpoint,e.target_type,e.identity_hash,e.semantic_type,
                   (SELECT COUNT(*) FROM actual_targets a WHERE a.node_key=e.node_key AND a.identity_hash=e.identity_hash),
                   s.node_key,s.artifact_id,s.system_id,s.endpoint_id,s.artifact_type,s.identity_hash,e.edge_id
            FROM expected_targets e
            JOIN source_artifacts s ON s.artifact_id=e.source_id AND s.node_key=e.source_node_key
            ORDER BY e.node_key,e.identity_hash,e.target_id,s.node_key,s.artifact_id,e.edge_id
            """;
        if (_ruleEvaluationPartition is { } partitionIndex && _partitionCount > 1)
        {
            var expectedPartition = ExpectedPartitionPredicate(command, "e");
            command.CommandText = command.CommandText.Replace(
                "(SELECT COUNT(*) FROM actual_targets a WHERE a.node_key=e.node_key AND a.identity_hash=e.identity_hash)",
                $"(SELECT COUNT(*) FROM partition{partitionIndex}.actual_targets a WHERE a.node_key=e.node_key AND a.identity_hash=e.identity_hash)",
                StringComparison.Ordinal);
            command.CommandText = command.CommandText.Replace("ORDER BY e.node_key", $"WHERE {expectedPartition} ORDER BY e.node_key", StringComparison.Ordinal);
        }
        await CaptureQueryPlanAsync("graph-derived lineage", command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash,COUNT(*)
            FROM actual_targets a
            WHERE {condition}
            GROUP BY a.node_key,a.semantic_type,a.target_id,a.target_system,a.target_endpoint,a.target_type,a.identity_hash
            ORDER BY a.node_key,a.identity_hash
            """;
        if (_ruleEvaluationPartition is { } partitionIndex && _partitionCount > 1)
            command.CommandText = command.CommandText.Replace("FROM actual_targets a",
                $"FROM partition{partitionIndex}.actual_targets a", StringComparison.Ordinal)
                .Replace("FROM journal_entries j", $"FROM partition{partitionIndex}.journal_entries j", StringComparison.Ordinal)
                .Replace("JOIN journal_sources js", $"JOIN partition{partitionIndex}.journal_sources js", StringComparison.Ordinal);
        await CaptureQueryPlanAsync("actual-target coverage: " + condition, command, cancellationToken).ConfigureAwait(false);
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
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
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        var actualTable = _ruleEvaluationPartition is { } partitionIndex && _partitionCount > 1
            ? $"partition{partitionIndex}.actual_targets" : "actual_targets";
        command.CommandText = $"""
            SELECT node_key,semantic_type,MIN(target_id),MIN(target_system),MIN(target_endpoint),MIN(target_type),identity_hash,COUNT(*)
            FROM {actualTable} GROUP BY node_key,semantic_type,identity_hash
            HAVING COUNT(*)>1 ORDER BY node_key,identity_hash
            """;
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VerificationTargetFact(reader.GetString(0), reader.GetString(1),
                MakeArtifact(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)),
                reader.GetInt32(7), [], []);
        }
    }

    private async Task AddOrderingKeysAsync(SqliteTransaction transaction, VerificationArtifactRole role,
        string nodeKey, RecordEnvelope record, long recordSequence, CancellationToken cancellationToken)
    {
        var partitionedActual = _partitionCount > 1 && role == VerificationArtifactRole.ActualTarget;
        var writer = partitionedActual
            ? _partitionWriters[_activePartition.Value ?? throw new InvalidOperationException("Partition write context is missing.")]
            : null;
        var command = writer is null ? _orderingKeyInsert : writer.OrderingKeyInsert;
        if (command is null)
        {
            command = (transaction.Connection ?? throw new InvalidOperationException("Scratch write transaction was disconnected.")).CreateCommand();
            var table = partitionedActual ? "main.artifact_order_keys"
                : _partitionCount > 1 ? "main.source_expected_order_keys" : "artifact_order_keys";
            command.CommandText = $"INSERT INTO {table}(role,node_key,semantic_type,artifact_id,record_seq,field,key_kind,text_value,integer_value,decimal_value,temporal_value,boolean_value) VALUES($role,$node,$semantic,$id,$seq,$field,$kind,$text,$integer,$decimal,$temporal,$boolean)";
            foreach (var name in new[] { "$role", "$node", "$semantic", "$id", "$seq", "$field", "$kind", "$text", "$integer", "$decimal", "$temporal", "$boolean" })
                command.Parameters.AddWithValue(name, DBNull.Value);
            command.Prepare();
            if (writer is not null) writer.OrderingKeyInsert = command;
            else _orderingKeyInsert = command;
        }
        command.Transaction = transaction;
        command.Parameters["$role"].Value = role.ToString();
        command.Parameters["$node"].Value = nodeKey;
        command.Parameters["$semantic"].Value = record.SemanticType;
        command.Parameters["$id"].Value = record.Artifact.Id.Value;
        command.Parameters["$seq"].Value = recordSequence;
        var requiredKeys = _executionPlan?.GetRequiredKeys(role, record.SemanticType);
        var fields = requiredKeys is null
            ? record.Values.Keys.Order(StringComparer.Ordinal)
            : requiredKeys.Select(key => key.Field).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!record.Values.TryGetValue(field, out var fieldValue)) continue;
            var value = NormalizeOrderingValue(fieldValue);
            command.Parameters["$field"].Value = field;
            command.Parameters["$kind"].Value = value.Kind;
            command.Parameters["$text"].Value = (object?)value.Text ?? DBNull.Value;
            command.Parameters["$integer"].Value = (object?)value.Integer ?? DBNull.Value;
            command.Parameters["$decimal"].Value = (object?)value.Decimal ?? DBNull.Value;
            command.Parameters["$temporal"].Value = (object?)value.Temporal ?? DBNull.Value;
            command.Parameters["$boolean"].Value = (object?)value.Boolean ?? DBNull.Value;
            await ExecuteScratchDmlAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private DomainList<string>? GetRequiredFields(VerificationArtifactRole role, string semanticType) =>
        _executionPlan?.GetRequiredFields(role, semanticType);

    private void RecordWorksetMaterialization(VerificationArtifactRole role, RecordEnvelope record,
        IReadOnlyCollection<string>? requiredFields, string projectedJson, long fullSerializedBytes)
    {
        if (_performanceRecorder is null) return;
        lock (_worksetMetricsGate)
        {
            var key = new VerificationWorksetKey(role, record.SemanticType);
            if (!_worksetMetrics.TryGetValue(key, out var metrics))
                _worksetMetrics.Add(key, metrics = new WorksetMaterializationMetrics());
            var required = requiredFields is null
                ? new HashSet<string>(record.Values.Keys, StringComparer.Ordinal)
                : new HashSet<string>(requiredFields, StringComparer.Ordinal);
            var retained = record.Values.Keys.Where(required.Contains).ToArray();
            metrics.Artifacts++;
            metrics.PhysicalFieldsObserved += record.Values.Count;
            metrics.FieldsRetained += retained.Length;
            metrics.FieldsOmitted += record.Values.Count - retained.Length;
            metrics.FullSerializedArtifactBytesObserved += fullSerializedBytes;
            metrics.ProjectedSerializedArtifactBytesWritten += Encoding.UTF8.GetByteCount(projectedJson);
            metrics.AvailableFields.UnionWith(record.Values.Keys);
            metrics.RequiredFields.UnionWith(required);
            metrics.OmittedFields.UnionWith(record.Values.Keys.Where(field => !required.Contains(field)));
        }
    }

    private void ValidatePlannedKeys(VerificationArtifactRole role,
        IReadOnlyCollection<VerificationOrderingKey> requestedKeys)
    {
        if (_executionPlan is null) return;
        foreach (var requested in requestedKeys)
            if (!_executionPlan.GetRequiredKeys(role, requested.SemanticType).Any(planned =>
                planned.Field == requested.Field && planned.Role == requested.Role))
                throw new VerificationRuleException("PSRULE008",
                    $"Ordered field '{requested.Field}' is not declared for the {role} workset.");
    }

    private async Task EnsureOrderingIndexAsync(CancellationToken cancellationToken)
    {
        if (_orderingIndexCreated) return;
        var indexRequirements = _executionPlan is null
            ? [new VerificationIndexRequirement("artifact_order_key_idx",
                new DomainList<VerificationArtifactRole>(Enum.GetValues<VerificationArtifactRole>()),
                new DomainList<string>([]), new DomainList<string>([]))]
            : _executionPlan.RequiredIndexes.ToArray();
        if (indexRequirements.Length == 0) return;
        using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification,
            "ordering key secondary index build");
        foreach (var requirement in indexRequirements)
        {
            var identifier = requirement.Name.Replace("\"", "\"\"", StringComparison.Ordinal);
            if (_partitionCount > 1 && _maxPartitionWorkers > 1)
            {
                await using (var coordinatorCommand = _connection.CreateCommand())
                {
                    coordinatorCommand.CommandText = $"CREATE INDEX main.\"{identifier}\" ON source_expected_order_keys(role,node_key,semantic_type,field,key_kind,text_value,integer_value,decimal_value COLLATE DECIMAL_ORDER,temporal_value,boolean_value,artifact_id,record_seq)";
                    await coordinatorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await Parallel.ForEachAsync(_partitionWriters,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _maxPartitionWorkers,
                        CancellationToken = cancellationToken
                    }, async (writer, token) =>
                    {
                        var started = Stopwatch.GetTimestamp();
                        using var partitionStage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification,
                            $"verification partition {writer.Index:D3} ordering index build");
                        try
                        {
                            await using var command = writer.Connection.CreateCommand();
                            command.CommandText = $"CREATE INDEX \"{identifier}\" ON artifact_order_keys(role,node_key,semantic_type,field,key_kind,text_value,integer_value,decimal_value COLLATE DECIMAL_ORDER,temporal_value,boolean_value,artifact_id,record_seq)";
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            Interlocked.Add(ref writer.IndexBuildMicroseconds,
                                checked((long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMicroseconds)));
                        }
                        catch (SqliteException exception)
                        {
                            if (exception.SqliteErrorCode == 5) Interlocked.Increment(ref _sqliteBusyExceptions);
                            throw;
                        }
                    }).ConfigureAwait(false);
                stage?.AddMeasurement($"indexBuilt.{requirement.Name}", 1, "indexes");
                stage?.AddMeasurement($"indexRoleCount.{requirement.Name}", requirement.Roles.Count, "roles");
                stage?.AddMeasurement($"indexSemanticTypeCount.{requirement.Name}", requirement.SemanticTypes.Count, "semantic-types");
                stage?.AddMeasurement($"indexFieldCount.{requirement.Name}", requirement.Fields.Count, "fields");
                continue;
            }
            for (var index = _partitionCount > 1 ? -1 : 0; index < _partitionCount; index++)
            {
                using var partitionIndexStage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification,
                    $"verification partition {(index < 0 ? "coordinator" : index.ToString("D3", CultureInfo.InvariantCulture))} ordering index build");
                await using var command = _connection.CreateCommand();
                var schema = _partitionCount == 1 || index < 0 ? "main" : $"partition{index}";
                var table = _partitionCount > 1 && index < 0 ? "source_expected_order_keys" : "artifact_order_keys";
                command.CommandText = $"CREATE INDEX {schema}.\"{identifier}\" ON {table}(role,node_key,semantic_type,field,key_kind,text_value,integer_value,decimal_value COLLATE DECIMAL_ORDER,temporal_value,boolean_value,artifact_id,record_seq)";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            stage?.AddMeasurement($"indexBuilt.{requirement.Name}", 1, "indexes");
            stage?.AddMeasurement($"indexRoleCount.{requirement.Name}", requirement.Roles.Count, "roles");
            stage?.AddMeasurement($"indexSemanticTypeCount.{requirement.Name}", requirement.SemanticTypes.Count, "semantic-types");
            stage?.AddMeasurement($"indexFieldCount.{requirement.Name}", requirement.Fields.Count, "fields");
        }
        _orderingIndexCreated = true;
        _performanceRecorder?.ObserveTemporaryWorkspaceBytes(DirectorySize(_directory));
    }

    private async Task EnsureExpectedPartitionIndexAsync(CancellationToken cancellationToken)
    {
        if (_expectedPartitionIndexCreated) return;
        using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification,
            "expected workset partition index build");
        await using var command = _connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS expected_partition_idx ON expected_targets(partition_bucket,node_key,identity_hash)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        stage?.AddMeasurement("expectedRowsIndexed", ExpectedTargetCount, "rows");
        stage?.AddMeasurement("partitionIndexCount", 1, "indexes");
        _expectedPartitionIndexCreated = true;
    }

    private Task EnsureExpectedPartitionIndexForEvaluationAsync(CancellationToken cancellationToken) =>
        _partitionCount > 1 && _ruleEvaluationPartition is not null && _sharedExpected is null
            ? EnsureExpectedPartitionIndexAsync(cancellationToken) : Task.CompletedTask;

    private static NormalizedOrderingValue NormalizeOrderingValue(ValueNode value) => value switch
    {
        NullValue => new(0),
        StringValue item => new(1, Text: item.Value),
        IntegerValue item => new(2, Integer: item.Value),
        DecimalValue item => new(3, Decimal: item.Value.ToString("G29", CultureInfo.InvariantCulture)),
        DateValue item => new(4, Temporal: item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        InstantValue item => new(5, Temporal: item.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        OffsetDateTimeValue item => new(6, Temporal: item.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        LocalDateTimeValue item => new(7, Temporal: item.Value.ToString("O", CultureInfo.InvariantCulture)),
        BooleanValue item => new(8, Boolean: item.Value ? 1 : 0),
        BinaryReferenceValue item => new(9, Text: item.Sha256),
        CollectionValue => new(10, Text: string.Empty),
        ObjectValue => new(11, Text: string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Unsupported normalized ordering-key value.")
    };

    private static int CompareDecimalStrings(string? left, string? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (decimal.TryParse(left, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var leftValue) &&
            decimal.TryParse(right, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var rightValue))
            return leftValue.CompareTo(rightValue);
        return string.Compare(left, right, StringComparison.Ordinal);
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

    public async Task ConfigurePartitionsAsync(int partitionCount, CancellationToken cancellationToken,
        int maxPartitionWorkers = 1)
    {
        ThrowIfDisposed();
        if (partitionCount is not (1 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(partitionCount), "Partition count must be 1, 4, or 8.");
        if (maxPartitionWorkers is not (1 or 2 or 4 or 6 or 8) || maxPartitionWorkers > partitionCount)
            throw new ArgumentOutOfRangeException(nameof(maxPartitionWorkers),
            "Partition worker count must be 1, 2, 4, 6, or 8 and cannot exceed the partition count.");
        if (SourceArtifactCount != 0 || ExpectedTargetCount != 0 || ActualTargetCount != 0 || _writeTransaction is not null)
            throw new InvalidOperationException("Configure scratch partitions before population.");
        if (_partitionCount != 1) throw new InvalidOperationException("Scratch partitions were already configured.");
        _maxPartitionWorkers = maxPartitionWorkers;
        if (partitionCount == 1) return;
        var tables = new[] { "actual_targets", "actual_values", "journal_entries", "journal_sources", "artifact_order_keys" };
        var definitions = new List<(string Type, string Name, string Table, string Sql)>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE sql IS NOT NULL AND type IN ('table','index') ORDER BY type DESC,name";
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (tables.Contains(reader.GetString(2), StringComparer.Ordinal))
                    definitions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        for (var index = 0; index < partitionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = $"ATTACH DATABASE $path AS partition{index}";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$path", DatabaseUri(Path.Combine(_directory, $"partition-{index:D3}.db")));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.Parameters.Clear();
            command.CommandText = $"PRAGMA partition{index}.journal_mode=WAL; PRAGMA partition{index}.synchronous=NORMAL";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var definition in definitions)
            {
                command.CommandText = definition.Type == "table"
                    ? definition.Sql.Replace("CREATE TABLE ", $"CREATE TABLE partition{index}.", StringComparison.Ordinal)
                    : definition.Sql.Replace("CREATE INDEX ", $"CREATE INDEX partition{index}.", StringComparison.Ordinal);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var writerConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseUri(Path.Combine(_directory, $"partition-{index:D3}.db")),
                Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString());
            _partitionWriters.Add(new PartitionWriter(index, writerConnection));
            await writerConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            writerConnection.CreateCollation("DECIMAL_ORDER", CompareDecimalStrings);
            await using var writerProfile = writerConnection.CreateCommand();
            writerProfile.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL";
            await writerProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        command.CommandText = "ALTER TABLE artifact_order_keys RENAME TO source_expected_order_keys; DROP TABLE actual_values; DROP TABLE actual_targets; DROP TABLE journal_sources; DROP TABLE journal_entries";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            var projections = new List<string>();
            if (table == "artifact_order_keys") projections.Add("SELECT * FROM main.source_expected_order_keys");
            for (var index = 0; index < partitionCount; index++)
            {
                var fields = table switch
                {
                    "actual_targets" => $"seq*16+{index} AS seq,node_key,target_id,target_system,target_endpoint,target_type,identity_hash,semantic_type,record_hash,record_json",
                    "actual_values" => $"target_seq*16+{index} AS target_seq,field,value_hash",
                    "journal_entries" => $"seq*16+{index} AS seq,result,target_node,target_id,target_system,target_endpoint,target_type,target_identity_hash,edge_id,edge_name,edge_version,failure_code",
                    "journal_sources" => $"journal_seq*16+{index} AS journal_seq,source_id,source_node_key,source_system,source_endpoint,source_type,source_identity_hash",
                    _ => $"role,node_key,semantic_type,artifact_id,record_seq*16+{index} AS record_seq,field,key_kind,text_value,integer_value,decimal_value,temporal_value,boolean_value"
                };
                projections.Add($"SELECT {fields} FROM partition{index}.{table}");
            }
            command.CommandText = $"CREATE TEMP VIEW {table} AS {string.Join(" UNION ALL ", projections)}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        _partitionCount = partitionCount;
        _partitionActualRows = new long[partitionCount];
        _partitionJournalRows = new long[partitionCount];
        _partitionDbHighWater = new long[partitionCount];
        _partitionWalHighWater = new long[partitionCount];
        _actualWorkerMetrics = Enumerable.Range(0, partitionCount).Select(_ => new PartitionWorkerPhaseMetrics()).ToArray();
        _journalWorkerMetrics = Enumerable.Range(0, partitionCount).Select(_ => new PartitionWorkerPhaseMetrics()).ToArray();
        StartPartitionWorkers();
    }

    private void StartPartitionWorkers()
    {
        if (_maxPartitionWorkers == 1) return;
        for (var laneIndex = 0; laneIndex < _maxPartitionWorkers; laneIndex++)
        {
            var lane = new PartitionWorkerLane(laneIndex, Channel.CreateBounded<PartitionWorkItem>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                }));
            _partitionWorkerLanes.Add(lane);
            lane.Worker = Task.Run(() => RunPartitionWorkerAsync(lane));
        }
    }

    private async Task RunPartitionWorkerAsync(PartitionWorkerLane lane)
    {
        try
        {
            await foreach (var item in lane.Channel.Reader.ReadAllAsync(_partitionWorkerStop.Token).ConfigureAwait(false))
            {
                var failure = Volatile.Read(ref _partitionWorkerFailure);
                if (failure is not null)
                {
                    item.Completion?.TrySetException(failure.SourceException);
                    continue;
                }
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        item.CancellationToken, _partitionWorkerStop.Token);
                    if (item.Kind is { } kind)
                        await ExecutePartitionOperationAsync(item.PartitionIndex, kind, item.EnqueuedTimestamp,
                            item.Execute, recordFailure: true, linked.Token).ConfigureAwait(false);
                    else
                        await item.Execute(linked.Token).ConfigureAwait(false);
                    item.Completion?.TrySetResult();
                }
                catch (Exception exception)
                {
                    if (exception is SqliteException sqliteException && sqliteException.SqliteErrorCode == 5)
                        Interlocked.Increment(ref _sqliteBusyExceptions);
                    var captured = ExceptionDispatchInfo.Capture(exception);
                    Interlocked.CompareExchange(ref _partitionWorkerFailure, captured, null);
                    item.Completion?.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException) when (_partitionWorkerStop.IsCancellationRequested)
        {
        }
        finally
        {
            while (lane.Channel.Reader.TryRead(out var pending))
                pending.Completion?.TrySetCanceled(_partitionWorkerStop.Token);
        }
    }

    private async Task QueuePartitionOperationAsync(int partitionIndex, PartitionWorkKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var failure = Volatile.Read(ref _partitionWorkerFailure);
        failure?.Throw();
        var enqueuedTimestamp = Stopwatch.GetTimestamp();
        var phaseMetrics = GetWorkerMetrics(kind, partitionIndex);
        phaseMetrics.RecordEnqueued(enqueuedTimestamp);
        if (_maxPartitionWorkers == 1)
        {
            await ExecutePartitionOperationAsync(partitionIndex, kind, enqueuedTimestamp, operation,
                recordFailure: false, cancellationToken).ConfigureAwait(false);
            return;
        }
        var lane = _partitionWorkerLanes[partitionIndex % _maxPartitionWorkers];
        var enqueueStarted = Stopwatch.GetTimestamp();
        try
        {
            await lane.Channel.Writer.WriteAsync(new PartitionWorkItem(partitionIndex, kind, enqueuedTimestamp,
                operation, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            Volatile.Read(ref _partitionWorkerFailure)?.Throw();
            throw;
        }
        finally
        {
            phaseMetrics.RecordProducerBackpressure(Stopwatch.GetElapsedTime(enqueueStarted).Ticks);
        }
    }

    private async Task ExecutePartitionOperationAsync(int partitionIndex, PartitionWorkKind kind,
        long enqueuedTimestamp, Func<CancellationToken, Task> operation, bool recordFailure,
        CancellationToken cancellationToken)
    {
        var phase = GetWorkerMetrics(kind, partitionIndex);
        var start = Stopwatch.GetTimestamp();
        phase.RecordStarted(Stopwatch.GetElapsedTime(enqueuedTimestamp, start).Ticks,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var phaseActiveWorkers = kind == PartitionWorkKind.ActualTarget
            ? Interlocked.Increment(ref _activeActualWorkers)
            : Interlocked.Increment(ref _activeJournalWorkers);
        UpdateMaximum(ref _maxSimultaneousPartitionWorkers, Interlocked.Increment(ref _activePartitionWorkers));
        if (kind == PartitionWorkKind.ActualTarget)
            UpdateMaximum(ref _maxActualWorkers, phaseActiveWorkers);
        else
            UpdateMaximum(ref _maxJournalWorkers, phaseActiveWorkers);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            if (exception.SqliteErrorCode == 5)
            {
                Interlocked.Increment(ref _sqliteBusyExceptions);
                phase.RecordBusyException();
            }
            if (recordFailure)
                Interlocked.CompareExchange(ref _partitionWorkerFailure, ExceptionDispatchInfo.Capture(exception), null);
            throw;
        }
        catch (Exception exception)
        {
            if (recordFailure)
                Interlocked.CompareExchange(ref _partitionWorkerFailure, ExceptionDispatchInfo.Capture(exception), null);
            throw;
        }
        finally
        {
            phase.RecordCompleted(Stopwatch.GetTimestamp() - start, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            phase.ExitWorker();
            Interlocked.Decrement(ref _activePartitionWorkers);
            if (kind == PartitionWorkKind.ActualTarget) Interlocked.Decrement(ref _activeActualWorkers);
            else Interlocked.Decrement(ref _activeJournalWorkers);
        }
    }

    private PartitionWorkerPhaseMetrics GetWorkerMetrics(PartitionWorkKind kind, int partitionIndex) =>
        kind == PartitionWorkKind.ActualTarget ? _actualWorkerMetrics[partitionIndex] : _journalWorkerMetrics[partitionIndex];

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (value <= current || Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }

    private static void UpdateMaximum(ref long maximum, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref maximum);
            if (value <= current || Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }

    internal async Task FlushPartitionWorkAsync(CancellationToken cancellationToken)
    {
        if (_partitionWorkerLanes.Count == 0)
        {
            foreach (var writer in _partitionWriters)
                await FlushPartitionWriterAsync(writer, cancellationToken).ConfigureAwait(false);
            RecordPartitionWorkerMetrics();
            return;
        }

        var completions = new TaskCompletionSource[_partitionWorkerLanes.Count];
        for (var laneIndex = 0; laneIndex < _partitionWorkerLanes.Count; laneIndex++)
        {
            var lane = _partitionWorkerLanes[laneIndex];
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completions[laneIndex] = completion;
            try
            {
                await lane.Channel.Writer.WriteAsync(new PartitionWorkItem(-1, null, Stopwatch.GetTimestamp(),
                    token => FlushLaneWritersAsync(lane.Index, token), cancellationToken, completion), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                Volatile.Read(ref _partitionWorkerFailure)?.Throw();
                throw;
            }
        }

        try
        {
            await Task.WhenAll(completions.Select(completion => completion.Task)).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Read(ref _partitionWorkerFailure)?.Throw();
            throw;
        }
        _performanceRecorder?.ObserveTemporaryWorkspaceBytes(DirectorySize(_directory));
        RecordPartitionWorkerMetrics();
    }

    private async Task FlushLaneWritersAsync(int laneIndex, CancellationToken cancellationToken)
    {
        for (var partitionIndex = laneIndex; partitionIndex < _partitionCount; partitionIndex += _maxPartitionWorkers)
            await FlushPartitionWriterAsync(_partitionWriters[partitionIndex], cancellationToken).ConfigureAwait(false);
    }

    private void RecordPartitionWorkerMetrics()
    {
        if (_performanceRecorder is null) return;
        RecordPhase("actual-target ingestion", PartitionWorkKind.ActualTarget, _actualWorkerMetrics, _partitionActualRows);
        RecordPhase("journal ingestion", PartitionWorkKind.JournalEntry, _journalWorkerMetrics, _partitionJournalRows);

        void RecordPhase(string phaseName, PartitionWorkKind kind, PartitionWorkerPhaseMetrics[] metrics, long[] rows)
        {
            var snapshots = metrics.Select(item => item.Snapshot()).ToArray();
            var queued = snapshots.Sum(item => item.Queued);
            if (queued == 0) return;
            var recorded = kind == PartitionWorkKind.ActualTarget
                ? Interlocked.Exchange(ref _actualWorkerMetricsRecorded, 1)
                : Interlocked.Exchange(ref _journalWorkerMetricsRecorded, 1);
            if (recorded != 0) return;
            var startTicks = snapshots.Where(item => item.Queued > 0).Min(item => item.FirstEnqueuedTimestamp);
            var endTicks = Stopwatch.GetTimestamp();
            var wallTicks = Math.Max(0, endTicks - startTicks);
            var executionTicks = snapshots.Sum(item => item.ExecutionTicks);
            var queueWaitTicks = snapshots.Sum(item => item.QueueWaitTicks);
            var producerBackpressureTicks = snapshots.Sum(item => item.ProducerBackpressureTicks);
            var maximumActive = kind == PartitionWorkKind.ActualTarget
                ? Volatile.Read(ref _maxActualWorkers) : Volatile.Read(ref _maxJournalWorkers);
            var averageActive = wallTicks == 0 ? 0 : executionTicks / (double)wallTicks;
            var workerCapacityTicks = checked(wallTicks * _maxPartitionWorkers);
            var idleTicks = Math.Max(0, workerCapacityTicks - executionTicks);
            var phaseStartUnix = snapshots.Where(item => item.Queued > 0).Min(item => item.FirstEnqueuedUnixMilliseconds);
            var phaseEndUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
                $"partition worker phase {phaseName}", StopwatchTicksToMicroseconds(wallTicks),
                artifactCount: snapshots.Sum(item => item.Completed),
                measurements:
                [
                    new PerformanceMeasurement("configuredWorkers", _maxPartitionWorkers, "workers"),
                    new PerformanceMeasurement("maximumSimultaneousWorkers", maximumActive, "workers"),
                    new PerformanceMeasurement("averageActiveWorkers", averageActive, "workers"),
                    new PerformanceMeasurement("workerIdleMicroseconds", StopwatchTicksToMicroseconds(idleTicks), "microseconds"),
                    new PerformanceMeasurement("workerExecutionMicroseconds", StopwatchTicksToMicroseconds(executionTicks), "microseconds"),
                    new PerformanceMeasurement("queueWaitMicroseconds", StopwatchTicksToMicroseconds(queueWaitTicks), "microseconds"),
                    new PerformanceMeasurement("producerBackpressureMicroseconds", StopwatchTicksToMicroseconds(producerBackpressureTicks), "microseconds"),
                    new PerformanceMeasurement("queuedOperations", queued, "operations"),
                    new PerformanceMeasurement("completedOperations", snapshots.Sum(item => item.Completed), "operations"),
                    new PerformanceMeasurement("sqliteBusyExceptions", snapshots.Sum(item => item.BusyExceptions), "exceptions"),
                    new PerformanceMeasurement("sqliteBusyRetries", 0, "retries"),
                    new PerformanceMeasurement("phaseStartUnixMilliseconds", phaseStartUnix, "unix-ms"),
                    new PerformanceMeasurement("phaseEndUnixMilliseconds", phaseEndUnix, "unix-ms")
                ]);

            for (var index = 0; index < snapshots.Length; index++)
            {
                var item = snapshots[index];
                if (item.Queued == 0) continue;
                var writer = _partitionCount > 1 ? _partitionWriters[index] : null;
                var firstExecutionUnix = item.FirstExecutionUnixMilliseconds;
                var lastExecutionUnix = item.LastExecutionUnixMilliseconds;
                var partitionStartTicks = item.FirstEnqueuedTimestamp;
                var partitionEndTicks = item.LastExecutionTimestamp == 0 ? partitionStartTicks : item.LastExecutionTimestamp;
                _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
                    $"partition worker partition-{index:D3} {phaseName}",
                    StopwatchTicksToMicroseconds(Math.Max(0, partitionEndTicks - partitionStartTicks)),
                    artifactCount: rows[index],
                    measurements:
                    [
                        new PerformanceMeasurement("partitionIndex", index, "partition"),
                        new PerformanceMeasurement("phaseKind", (double)kind, "enum"),
                        new PerformanceMeasurement("actualRows", _partitionActualRows[index], "rows"),
                        new PerformanceMeasurement("journalRows", _partitionJournalRows[index], "rows"),
                        new PerformanceMeasurement("queuedOperations", item.Queued, "operations"),
                        new PerformanceMeasurement("completedOperations", item.Completed, "operations"),
                        new PerformanceMeasurement("queueWaitMicroseconds", StopwatchTicksToMicroseconds(item.QueueWaitTicks), "microseconds"),
                        new PerformanceMeasurement("executionMicroseconds", StopwatchTicksToMicroseconds(item.ExecutionTicks), "microseconds"),
                        new PerformanceMeasurement("dmlExecutions", writer?.DmlOperations ?? _referenceDmlOperations, "commands"),
                        new PerformanceMeasurement("dmlElapsedMicroseconds", writer?.DmlMicroseconds ?? _referenceDmlMicroseconds, "microseconds"),
                        new PerformanceMeasurement("indexBuildElapsedMicroseconds", writer?.IndexBuildMicroseconds ?? 0, "microseconds"),
                        new PerformanceMeasurement("sqliteBusyExceptions", item.BusyExceptions, "exceptions"),
                        new PerformanceMeasurement("sqliteBusyRetries", 0, "retries"),
                        new PerformanceMeasurement("firstExecutionUnixMilliseconds", firstExecutionUnix, "unix-ms"),
                        new PerformanceMeasurement("lastExecutionUnixMilliseconds", lastExecutionUnix, "unix-ms")
                    ]);
            }
        }
    }

    private static long StopwatchTicksToMicroseconds(long ticks) =>
        checked((long)Math.Round(ticks * 1_000_000d / Stopwatch.Frequency));

    private async Task StopPartitionWorkersAsync()
    {
        if (_partitionWorkerLanes.Count == 0) return;
        foreach (var lane in _partitionWorkerLanes) lane.Channel.Writer.TryComplete();
        _partitionWorkerStop.Cancel();
        try { await Task.WhenAll(_partitionWorkerLanes.Select(lane => lane.Worker)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_partitionWorkerStop.IsCancellationRequested) { }
    }

    private string LocalJournalBranches(string query, string localOrder, string globalOrder)
    {
        if (_partitionCount == 1) return query;
        var orderStart = query.IndexOf(localOrder, StringComparison.Ordinal);
        if (orderStart < 0) throw new InvalidOperationException("Partition journal query ordering was not recognized.");
        var body = query[..orderStart];
        var branches = Enumerable.Range(0, _partitionCount).Select(index => body
            .Replace("FROM journal_entries j", $"FROM partition{index}.journal_entries j", StringComparison.Ordinal)
            .Replace("JOIN journal_sources js", $"JOIN partition{index}.journal_sources js", StringComparison.Ordinal));
        return $"SELECT * FROM ({string.Join(" UNION ALL ", branches)}) {globalOrder}";
    }

    private int AssignScratchPartition(string nodeKey, string identity) =>
        _executionPlan?.AssignTargetIdentityPartition(nodeKey, identity, _partitionCount)
        ?? VerificationPartitioning.AssignArtifactIdentity(nodeKey, identity, _partitionCount);

    private int AssignStableTargetBucket(string nodeKey, string identity) =>
        _executionPlan?.AssignTargetIdentityPartition(nodeKey, identity, VerificationPartitioning.StableBucketCount)
        ?? VerificationPartitioning.AssignArtifactIdentity(nodeKey, identity, VerificationPartitioning.StableBucketCount);

    private string ExpectedPartitionPredicate(SqliteCommand command, string alias)
    {
        var partitionIndex = _ruleEvaluationPartition
            ?? throw new InvalidOperationException("Expected partition filtering requires an active rule partition.");
        var buckets = Enumerable.Range(0, VerificationPartitioning.StableBucketCount)
            .Where(bucket => bucket % _partitionCount == partitionIndex).ToArray();
        var parameters = buckets.Select((bucket, index) =>
        {
            var name = $"$expectedBucket{index}";
            command.Parameters.AddWithValue(name, bucket);
            return name;
        });
        return $"{alias}.partition_bucket IN ({string.Join(',', parameters)})";
    }

    internal async Task BeginExpectedBuildAsync(VerificationExpectedWorksetKey key, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "CREATE TABLE expected_workset_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); INSERT INTO expected_workset_metadata VALUES('state','Building'),('inputKey',$key)";
        command.Parameters.AddWithValue("$key", key.Fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            format = "proofshift-expected-workset-v2", state = "Building", inputs = key
        }), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<VerificationExpectedWorkset> PublishExpectedAsync(VerificationExpectedWorksetKey key,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sharedExpected is not null || _publishedExpected || ActualTargetCount != 0)
            throw new InvalidOperationException("Only independently derived source/expected state may be published.");
        await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM journal_entries";
        if ((long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false))! != 0)
            throw new InvalidOperationException("Projection journal state cannot be published in an expected workset.");
        await EnsureOrderingIndexAsync(cancellationToken).ConfigureAwait(false);
        await EnsureExpectedPartitionIndexAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "SELECT COALESCE(SUM(length(CAST(record_json AS BLOB))),0) FROM source_artifacts";
        var sourceBytes = (long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!;
        command.CommandText = "SELECT COALESCE(SUM(length(CAST(record_json AS BLOB))),0) FROM expected_targets";
        var expectedBytes = (long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!;
        command.CommandText = "DROP TABLE actual_values; DROP TABLE actual_targets; DROP TABLE journal_sources; DROP TABLE journal_entries; INSERT OR REPLACE INTO expected_workset_metadata VALUES('state','Building'),('inputKey',$key),('sourceCount',$sources),('expectedCount',$expected)";
        command.Parameters.AddWithValue("$key", key.Fingerprint);
        command.Parameters.AddWithValue("$sources", SourceArtifactCount.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expected", ExpectedTargetCount.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = "PRAGMA quick_check";
        if (await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false) as string != "ok")
            throw new InvalidDataException("Expected workset SQLite integrity validation failed.");
        cancellationToken.ThrowIfCancellationRequested();
        command.CommandText = "UPDATE expected_workset_metadata SET value='Complete' WHERE key='state'; PRAGMA wal_checkpoint(TRUNCATE)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _publishedExpected = true;
        var path = Path.Combine(_directory, "expected.sqlite");
        File.Move(_databasePath, path);
        var checksum = await VerificationExpectedWorkset.ChecksumAsync(path, cancellationToken).ConfigureAwait(false);
        var completed = new VerificationExpectedWorkset(key, path, checksum, SourceArtifactCount, ExpectedTargetCount, sourceBytes, expectedBytes);
        await File.WriteAllTextAsync(Path.Combine(_directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            format = "proofshift-expected-workset-v2", state = "Complete", inputs = key,
            completed.SourceCount, completed.ExpectedCount, completed.SourceJsonBytes, completed.ExpectedJsonBytes, completed.Checksum
        }), cancellationToken).ConfigureAwait(false);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        RecordWorksetDiagnostics();
        return completed;
    }

    internal void RetainPublishedExpected() => _retainPublishedExpected = true;

    internal async Task AttachExpectedAsync(VerificationExpectedWorkset expected, VerificationExpectedWorksetKey key,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sharedExpected is not null || SourceArtifactCount != 0 || ExpectedTargetCount != 0)
            throw new InvalidOperationException("Expected worksets must attach before run-specific population.");
        if (_executionPlan is not null && key.RequirementsFingerprint != _executionPlan.ExpectedRequirementsFingerprint)
            throw new InvalidDataException("Expected workset requirements do not match this Verification plan.");
        await expected.ValidateAsync(key, cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $path AS expected";
        command.Parameters.AddWithValue("$path", DatabaseUri(expected.DatabasePath) + "?mode=ro&immutable=1");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = "SELECT value FROM expected.expected_workset_metadata WHERE key='state'";
        if (await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false) as string != "Complete")
            throw new InvalidDataException("Expected workset was not completed.");
        command.CommandText = "DROP TABLE expected_values; DROP TABLE expected_targets; DROP TABLE source_artifacts";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _sharedExpected = expected;
        SourceArtifactCount = expected.SourceCount;
        ExpectedTargetCount = expected.ExpectedCount;
    }

    private void RouteReadCommand(SqliteCommand command)
    {
        if (_sharedExpected is null) return;
        var sql = command.CommandText;
        sql = Regex.Replace(sql, @"(?<![\w.])(?:source_artifacts|expected_targets|expected_values)\b", match => "expected." + match.Value,
            RegexOptions.CultureInvariant);
        if (command.Parameters.Contains("$role") && command.Parameters["$role"].Value is string role && role != nameof(VerificationArtifactRole.ActualTarget))
            sql = Regex.Replace(sql, @"(?<![\w.])artifact_order_keys\b", "expected.artifact_order_keys", RegexOptions.CultureInvariant);
        command.CommandText = sql;
    }

    private static string DatabaseUri(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        return OperatingSystem.IsWindows() ? "file:" + uri["file:///".Length..] : uri;
    }

    private Task<SqliteDataReader> ExecuteReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        RouteReadCommand(command);
        return command.ExecuteReaderAsync(cancellationToken);
    }

    private Task<object?> ExecuteScalarReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        RouteReadCommand(command);
        return command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<long> ReadLastInsertRowIdAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var command = GetWriteCommand("SELECT last_insert_rowid()", transaction);
        return (long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SQLite did not return a row ID."));
    }

    private SqliteCommand GetWriteCommand(string sql, SqliteTransaction transaction)
    {
        var partitionIndex = _partitionCount > 1 ? _activePartition.Value : null;
        var commands = partitionIndex is { } index ? _partitionWriters[index].WriteCommands : _writeCommands;
        if (!commands.TryGetValue(sql, out var command))
        {
            command = (transaction.Connection ?? throw new InvalidOperationException("Scratch write transaction was disconnected.")).CreateCommand();
            command.CommandText = sql;
            command.Prepare();
            commands.Add(sql, command);
        }
        command.Transaction = transaction;
        command.Parameters.Clear();
        return command;
    }

    private SqliteTransaction GetWriteTransaction()
    {
        ThrowIfDisposed();
        if (_partitionCount > 1 && _activePartition.Value is { } partitionIndex)
        {
            var writer = _partitionWriters[partitionIndex];
            return writer.Transaction ??= writer.Connection.BeginTransaction();
        }
        return _writeTransaction ??= _partitionCount > 1 ? _connection.BeginTransaction(deferred: true) : _connection.BeginTransaction();
    }

    private async Task CompleteWriteOperationAsync(CancellationToken cancellationToken)
    {
        if (_partitionCount > 1 && _activePartition.Value is { } partitionIndex)
        {
            var writer = _partitionWriters[partitionIndex];
            writer.PendingWrites++;
            if (writer.PendingWrites >= WriteBatchSize) await FlushPartitionWriterAsync(writer, cancellationToken).ConfigureAwait(false);
            return;
        }
        _pendingWriteOperations++;
        if (_pendingWriteOperations >= WriteBatchSize)
            await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushWritesAsync(CancellationToken cancellationToken)
    {
        await FlushPartitionWorkAsync(cancellationToken).ConfigureAwait(false);
        if (_writeTransaction is not { } transaction) return;
        var containsJournalEntries = _performanceRecorder is not null && Interlocked.Read(ref _journalPendingEntryCount) > 0;
        var flushStarted = containsJournalEntries ? Stopwatch.GetTimestamp() : 0;
        _writeTransaction = null;
        _pendingWriteOperations = 0;
        try
        {
            var commitStarted = containsJournalEntries ? Stopwatch.GetTimestamp() : 0;
            try { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }
            finally
            {
                if (containsJournalEntries)
                    Interlocked.Add(ref _journalCommitTicks, Stopwatch.GetElapsedTime(commitStarted).Ticks);
            }
            _committedTransactions++;
                        Interlocked.Increment(ref _committedTransactions);
            if (_performanceRecorder is not null)
            {
                var observationStarted = containsJournalEntries ? Stopwatch.GetTimestamp() : 0;
                try { _performanceRecorder.ObserveTemporaryWorkspaceBytes(DirectorySize(_directory)); }
                finally
                {
                    if (containsJournalEntries)
                        Interlocked.Add(ref _journalScratchObservationTicks,
                            Stopwatch.GetElapsedTime(observationStarted).Ticks);
                }
                ObservePartitionHighWater();
            }
        }
        finally
        {
            var disposeStarted = containsJournalEntries ? Stopwatch.GetTimestamp() : 0;
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (containsJournalEntries)
                {
                    Interlocked.Add(ref _journalTransactionDisposeTicks,
                        Stopwatch.GetElapsedTime(disposeStarted).Ticks);
                    Interlocked.Add(ref _journalTransactionFlushTicks,
                        Stopwatch.GetElapsedTime(flushStarted).Ticks);
                    Interlocked.Increment(ref _journalFlushCount);
                    Interlocked.Exchange(ref _journalPendingEntryCount, 0);
                }
            }
        }
    }

    private async Task<int> ExecuteScratchDmlAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var started = _performanceRecorder is null ? 0 : Stopwatch.GetTimestamp();
        var result = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (_performanceRecorder is not null)
        {
            var elapsed = checked((long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMicroseconds));
            if (_partitionCount > 1 && _activePartition.Value is { } partitionIndex)
            {
                _partitionWriters[partitionIndex].DmlOperations++;
                _partitionWriters[partitionIndex].DmlMicroseconds += elapsed;
            }
            else
            {
                _referenceDmlOperations++;
                _referenceDmlMicroseconds += elapsed;
            }
        }
        return result;
    }

    private async Task FlushPartitionWriterAsync(PartitionWriter writer, CancellationToken cancellationToken)
    {
        if (writer.Transaction is not { } transaction) return;
        writer.Transaction = null;
        writer.PendingWrites = 0;
        var journalRows = writer.PendingJournal;
        writer.PendingJournal = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committedTransactions++;
            writer.CommittedTransactions++;
            writer.CommitMicroseconds += checked((long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMicroseconds));
            if (journalRows > 0)
            {
                Interlocked.Add(ref _journalCommitTicks, Stopwatch.GetElapsedTime(started).Ticks);
                Interlocked.Increment(ref _journalFlushCount);
                Interlocked.Add(ref _journalPendingEntryCount, -journalRows);
            }
            if (_performanceRecorder is not null)
            {
                if (_partitionWorkerLanes.Count == 0)
                    _performanceRecorder.ObserveTemporaryWorkspaceBytes(DirectorySize(_directory));
                ObservePartitionHighWater(writer.Index);
                        Interlocked.Increment(ref _committedTransactions);
            }
        }
        finally { await transaction.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class PartitionWorkerLane(int index, Channel<PartitionWorkItem> channel)
    {
        public int Index { get; } = index;
        public Channel<PartitionWorkItem> Channel { get; } = channel;
        public Task Worker { get; set; } = Task.CompletedTask;
    }

    private sealed record PartitionWorkItem(int PartitionIndex, PartitionWorkKind? Kind, long EnqueuedTimestamp,
        Func<CancellationToken, Task> Execute, CancellationToken CancellationToken,
        TaskCompletionSource? Completion = null);

    private sealed class PartitionWorkerPhaseMetrics
    {
        private long _firstEnqueuedTimestamp;
        private long _firstEnqueuedUnixMilliseconds;
        private long _firstExecutionUnixMilliseconds;
        private long _lastExecutionTimestamp;
        private long _lastExecutionUnixMilliseconds;
        private long _queueWaitTicks;
        private long _producerBackpressureTicks;
        private long _executionTicks;
        private long _queued;
        private long _completed;
        private long _busyExceptions;
        private int _activeWorkers;
        private int _maximumActiveWorkers;

        public int BusyExceptions => checked((int)Interlocked.Read(ref _busyExceptions));

        public void RecordEnqueued(long timestamp)
        {
            Interlocked.CompareExchange(ref _firstEnqueuedTimestamp, timestamp, 0);
            Interlocked.CompareExchange(ref _firstEnqueuedUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            Interlocked.Increment(ref _queued);
        }

        public void RecordProducerBackpressure(long ticks) => Interlocked.Add(ref _producerBackpressureTicks, ticks);

        public int RecordStarted(long queueWaitTicks, long startedUnixMilliseconds)
        {
            Interlocked.Add(ref _queueWaitTicks, queueWaitTicks);
            Interlocked.CompareExchange(ref _firstExecutionUnixMilliseconds, startedUnixMilliseconds, 0);
            var active = Interlocked.Increment(ref _activeWorkers);
            UpdateMaximum(ref _maximumActiveWorkers, active);
            return active;
        }

        public void RecordCompleted(long executionTicks, long endedUnixMilliseconds)
        {
            Interlocked.Add(ref _executionTicks, executionTicks);
            Interlocked.Increment(ref _completed);
            UpdateMaximum(ref _lastExecutionTimestamp, Stopwatch.GetTimestamp());
            UpdateMaximum(ref _lastExecutionUnixMilliseconds, endedUnixMilliseconds);
        }

        public void ExitWorker() => Interlocked.Decrement(ref _activeWorkers);
        public void RecordBusyException() => Interlocked.Increment(ref _busyExceptions);

        public PartitionWorkerPhaseSnapshot Snapshot() => new(
            Interlocked.Read(ref _firstEnqueuedTimestamp),
            Interlocked.Read(ref _firstEnqueuedUnixMilliseconds),
            Interlocked.Read(ref _firstExecutionUnixMilliseconds),
            Interlocked.Read(ref _lastExecutionTimestamp),
            Interlocked.Read(ref _lastExecutionUnixMilliseconds),
            Interlocked.Read(ref _queueWaitTicks),
            Interlocked.Read(ref _producerBackpressureTicks),
            Interlocked.Read(ref _executionTicks),
            Interlocked.Read(ref _queued),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _busyExceptions),
            Volatile.Read(ref _maximumActiveWorkers));
    }

    private readonly record struct PartitionWorkerPhaseSnapshot(
        long FirstEnqueuedTimestamp,
        long FirstEnqueuedUnixMilliseconds,
        long FirstExecutionUnixMilliseconds,
        long LastExecutionTimestamp,
        long LastExecutionUnixMilliseconds,
        long QueueWaitTicks,
        long ProducerBackpressureTicks,
        long ExecutionTicks,
        long Queued,
        long Completed,
        long BusyExceptions,
        int MaximumActiveWorkers);

    private enum PartitionWorkKind
    {
        ActualTarget,
        JournalEntry
    }

    private sealed class PartitionWriter(int index, SqliteConnection connection)
    {
        public int Index { get; } = index;
        public SqliteConnection Connection { get; } = connection;
        public Dictionary<string, SqliteCommand> WriteCommands { get; } = new(StringComparer.Ordinal);
        public SqliteCommand? OrderingKeyInsert { get; set; }
        public SqliteTransaction? Transaction { get; set; }
        public int PendingWrites { get; set; }
        public long PendingJournal { get; set; }
        public long CommittedTransactions { get; set; }
        public long CommitMicroseconds { get; set; }
        public long DmlOperations { get; set; }
        public long DmlMicroseconds { get; set; }
        public long IndexBuildMicroseconds;
    }

    private void RecordJournalWriteDiagnostics()
    {
        if (_performanceRecorder is null || Interlocked.Exchange(ref _journalWriteDiagnosticsRecorded, 1) != 0)
            return;
        var journalEntries = Interlocked.Read(ref _journalEntryCount);
        if (journalEntries == 0) return;
        var statementExecutions = Interlocked.Read(ref _journalStatementCount);
        var transactionFlushes = Interlocked.Read(ref _journalFlushCount);
        var transactionFlushTicks = Interlocked.Read(ref _journalTransactionFlushTicks);
        _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
            "workspace journal normalization and parameter binding",
            ToMicroseconds(Interlocked.Read(ref _journalPreparationTicks)), artifactCount: journalEntries,
            measurements: [new PerformanceMeasurement("journalEntries", journalEntries, "entries")]);
        _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
            "workspace journal SQLite execution",
            ToMicroseconds(Interlocked.Read(ref _journalExecutionTicks)), artifactCount: statementExecutions,
            measurements: [new PerformanceMeasurement("sqlStatementExecutions", statementExecutions, "statements")]);
        _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
            "workspace journal transaction flush",
            ToMicroseconds(transactionFlushTicks), artifactCount: transactionFlushes,
            measurements:
            [
                new PerformanceMeasurement("commitElapsedMicroseconds",
                    ToMicroseconds(Interlocked.Read(ref _journalCommitTicks)), "microseconds"),
                new PerformanceMeasurement("scratchHighWaterObservationElapsedMicroseconds",
                    ToMicroseconds(Interlocked.Read(ref _journalScratchObservationTicks)), "microseconds"),
                new PerformanceMeasurement("transactionDisposalElapsedMicroseconds",
                    ToMicroseconds(Interlocked.Read(ref _journalTransactionDisposeTicks)), "microseconds"),
                new PerformanceMeasurement("journalContainingTransactionFlushes", transactionFlushes, "transactions")
            ]);
    }

    private static long ToMicroseconds(long stopwatchTicks) =>
        checked((long)Math.Round(stopwatchTicks / (double)TimeSpan.TicksPerMicrosecond));

    private void ObservePartitionHighWater(int? onlyPartition = null)
    {
        var indexes = onlyPartition is { } selected ? [selected] : Enumerable.Range(0, _partitionCount);
        foreach (var index in indexes)
        {
            var path = _partitionCount == 1 ? _databasePath : Path.Combine(_directory, $"partition-{index:D3}.db");
            _partitionDbHighWater[index] = Math.Max(_partitionDbHighWater[index], File.Exists(path) ? new FileInfo(path).Length : 0);
            _partitionWalHighWater[index] = Math.Max(_partitionWalHighWater[index], File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
        }
    }

    private static long DirectorySize(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    private async Task CaptureQueryPlanAsync(string name, SqliteCommand query, CancellationToken cancellationToken)
    {
        RouteReadCommand(query);
        if (_performanceRecorder is null || _queryPlans.ContainsKey(name)) return;
        await using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + query.CommandText;
        foreach (SqliteParameter parameter in query.Parameters)
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
        var details = new List<string>();
        await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) details.Add(reader.GetString(3));
        _queryPlans.Add(name, details.ToArray());
    }

    private async Task WriteDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var stage = _performanceRecorder?.StartStage(PerformanceStageKind.Verification, "verification workspace diagnostics");
        ObservePartitionHighWater();
        var expectedRowsByPartition = new long[_partitionCount];
        await using (var expectedCounts = _connection.CreateCommand())
        {
            var expectedSchema = _sharedExpected is null ? string.Empty : "expected.";
            expectedCounts.CommandText = $"SELECT partition_bucket,COUNT(*) FROM {expectedSchema}expected_targets GROUP BY partition_bucket";
            await using var reader = await ExecuteReadAsync(expectedCounts, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                expectedRowsByPartition[reader.GetInt32(0) % _partitionCount] += reader.GetInt64(1);
        }
        AddPartitionDistribution(stage, "actualArtifacts", _partitionActualRows);
        AddPartitionDistribution(stage, "expectedTargetRows", expectedRowsByPartition);
        AddPartitionDistribution(stage, "journalEntries", _partitionJournalRows);
        for (var index = 0; index < _partitionCount; index++)
        {
            var schema = _partitionCount == 1 ? "main" : $"partition{index}";
            await using var indexCommand = _connection.CreateCommand();
            indexCommand.CommandText = $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type='index'";
            var indexCount = (long)(await indexCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            long largestIndexBytes = 0;
            var indexSizeAvailable = false;
            try
            {
                indexCommand.CommandText = $"SELECT COALESCE(MAX(bytes),0) FROM (SELECT name,SUM(pgsize) AS bytes FROM dbstat($schema) WHERE name IN (SELECT name FROM {schema}.sqlite_master WHERE type='index') GROUP BY name)";
                indexCommand.Parameters.AddWithValue("$schema", schema);
                largestIndexBytes = (long)(await indexCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                indexSizeAvailable = true;
            }
            catch (SqliteException) { }
            var writer = _partitionCount > 1 ? _partitionWriters[index] : null;
            _performanceRecorder?.RecordMeasuredStage(PerformanceStageKind.Verification, $"verification scratch partition {index:D3}", 0,
                artifactCount: _partitionActualRows[index], measurements:
                [
                    new PerformanceMeasurement("partitionCount", _partitionCount, "partitions"),
                    new PerformanceMeasurement("actualRows", _partitionActualRows[index], "rows"),
                    new PerformanceMeasurement("journalRows", _partitionJournalRows[index], "rows"),
                    new PerformanceMeasurement("databaseHighWaterBytes", _partitionDbHighWater[index], "bytes"),
                    new PerformanceMeasurement("walHighWaterBytes", _partitionWalHighWater[index], "bytes"),
                    new PerformanceMeasurement("indexes", indexCount, "indexes"),
                    new PerformanceMeasurement("largestIndexBytes", largestIndexBytes, "bytes"),
                    new PerformanceMeasurement("indexSizeAvailable", indexSizeAvailable ? 1 : 0, "available"),
                    new PerformanceMeasurement("dmlExecutions", writer?.DmlOperations ?? _referenceDmlOperations, "commands"),
                    new PerformanceMeasurement("dmlElapsedMicroseconds", writer?.DmlMicroseconds ?? _referenceDmlMicroseconds, "microseconds"),
                    new PerformanceMeasurement("committedTransactions", writer?.CommittedTransactions ?? _committedTransactions, "transactions"),
                    new PerformanceMeasurement("commitElapsedMicroseconds", writer?.CommitMicroseconds ?? ToMicroseconds(_journalCommitTicks), "microseconds")
                ]);
        }
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "source_artifacts", "expected_targets", "actual_targets", "expected_values", "actual_values", "artifact_order_keys", "journal_entries", "journal_sources" })
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            rows.Add(table, (long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!);
        }
        long logicalInputBytes = 0;
        foreach (var table in new[] { "source_artifacts", "expected_targets", "actual_targets" })
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(SUM(length(CAST(record_json AS BLOB))),0) FROM {table}";
            logicalInputBytes += (long)(await ExecuteScalarReadAsync(command, cancellationToken).ConfigureAwait(false))!;
        }
        var indexes = new List<string>();
        await using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='index' ORDER BY name";
            await using var reader = await ExecuteReadAsync(command, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) indexes.Add(reader.GetString(0));
        }
        var worksets = _worksetMetrics.OrderBy(pair => pair.Key.Role).ThenBy(pair => pair.Key.SemanticType, StringComparer.Ordinal)
            .Select(pair => new
            {
                role = pair.Key.Role.ToString(),
                semanticType = pair.Key.SemanticType,
                artifacts = pair.Value.Artifacts,
                physicalFieldsObserved = pair.Value.PhysicalFieldsObserved,
                fieldsRetained = pair.Value.FieldsRetained,
                fieldsOmitted = pair.Value.FieldsOmitted,
                fullSerializedArtifactBytesObserved = pair.Value.FullSerializedArtifactBytesObserved,
                projectedSerializedArtifactBytesWritten = pair.Value.ProjectedSerializedArtifactBytesWritten,
                availableFields = pair.Value.AvailableFields.Order(StringComparer.Ordinal).ToArray(),
                requiredFields = pair.Value.RequiredFields.Order(StringComparer.Ordinal).ToArray(),
                omittedFields = pair.Value.OmittedFields.Order(StringComparer.Ordinal).ToArray()
            }).ToArray();
        var document = new
        {
            rows,
            committedTransactions = _committedTransactions,
            writeBatchSize = WriteBatchSize,
            logicalInputBytes,
            databaseBytes = new FileInfo(_databasePath).Length,
            walBytes = File.Exists(_databasePath + "-wal") ? new FileInfo(_databasePath + "-wal").Length : 0,
            indexes,
            secondaryOrderingIndexBuiltAfterIngest = _orderingIndexCreated,
            queryPlans = _queryPlans,
            worksets
        };
        var partitionManifest = new
        {
            format = "proofshift-verification-partition-manifest-v1",
            partitionCount = _partitionCount,
            hashVersion = VerificationPartitioning.Version,
            ruleContractFingerprint = _executionPlan?.PartitionContractFingerprint,
            rules = _executionPlan?.Rules.Select(rule => new
            {
                id = rule.Rule.Id.Value,
                type = rule.RuleType,
                mode = rule.PartitionExecution.ToString(),
                basis = rule.PartitionKey?.Basis.ToString(),
                role = rule.PartitionKey?.Role.ToString(),
                fieldOptions = rule.PartitionKey?.FieldOptions.ToArray() ?? [],
                fields = rule.ResolvedPartitionKeyFields.Select(key => new { key.SemanticType, key.Field }).ToArray()
            }).ToArray() ?? [],
            partitions = Enumerable.Range(0, _partitionCount).Select(index => new
            {
                index,
                actualArtifacts = _partitionActualRows[index],
                expectedTargetRows = expectedRowsByPartition[index],
                journalEntries = _partitionJournalRows[index]
            }).ToArray()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(partitionManifest);
        var manifest = new
        {
            partitionManifest.format,
            partitionManifest.partitionCount,
            partitionManifest.hashVersion,
            partitionManifest.ruleContractFingerprint,
            partitionManifest.rules,
            partitionManifest.partitions,
            fingerprint = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant()
        };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_directory)!,
            $"{Path.GetFileName(_directory)}-partition-manifest.json"), JsonSerializer.Serialize(manifest), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_directory)!,
            Path.GetFileName(_directory) + "-diagnostics.json"), JsonSerializer.Serialize(document), cancellationToken).ConfigureAwait(false);
        RecordWorksetDiagnostics();
        RecordJournalWriteDiagnostics();
    }

    private static void AddPartitionDistribution(PerformanceRecorder.PerformanceStageScope? stage, string name,
        long[] counts)
    {
        if (counts.Length == 0) return;
        var minimum = counts.Min();
        var maximum = counts.Max();
        var average = counts.Average();
        stage?.AddMeasurement($"{name}.minimum", minimum, "artifacts");
        stage?.AddMeasurement($"{name}.maximum", maximum, "artifacts");
        stage?.AddMeasurement($"{name}.average", average, "artifacts");
        stage?.AddMeasurement($"{name}.maximumToAverageRatio", average == 0 ? 0 : maximum / average, "ratio");
    }

    private void RecordWorksetDiagnostics()
    {
        if (_performanceRecorder is null) return;
        foreach (var (key, metrics) in _worksetMetrics.OrderBy(pair => pair.Key.Role)
            .ThenBy(pair => pair.Key.SemanticType, StringComparer.Ordinal))
        {
            var measurements = new List<PerformanceMeasurement>
            {
                new("artifactsObserved", metrics.Artifacts, "artifacts"),
                new("totalPhysicalFieldsObserved", metrics.PhysicalFieldsObserved, "field-instances"),
                new("fieldsRetained", metrics.FieldsRetained, "field-instances"),
                new("fieldsOmitted", metrics.FieldsOmitted, "field-instances"),
                new("fullSerializedArtifactBytesObserved", metrics.FullSerializedArtifactBytesObserved, "bytes"),
                new("projectedSerializedArtifactBytesWritten", metrics.ProjectedSerializedArtifactBytesWritten, "bytes"),
                new("availableFieldCount", metrics.AvailableFields.Count, "fields"),
                new("requiredFieldCount", metrics.RequiredFields.Count, "fields"),
                new("omittedFieldCount", metrics.OmittedFields.Count, "fields")
            };
            measurements.AddRange(metrics.AvailableFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"availableField.{field}", 1, "fields")));
            measurements.AddRange(metrics.RequiredFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"requiredField.{field}", 1, "fields")));
            measurements.AddRange(metrics.OmittedFields.Order(StringComparer.Ordinal)
                .Select(field => new PerformanceMeasurement($"omittedField.{field}", 1, "fields")));
            _performanceRecorder.RecordMeasuredStage(PerformanceStageKind.Verification,
                $"verification workset {key.Role} {key.SemanticType}", elapsedMicroseconds: 0,
                artifactCount: metrics.Artifacts, byteCount: metrics.ProjectedSerializedArtifactBytesWritten,
                measurements: measurements);
        }
    }

    private readonly record struct NormalizedOrderingValue(int Kind, string? Text = null, long? Integer = null,
        string? Decimal = null, string? Temporal = null, int? Boolean = null);

    private sealed class WorksetMaterializationMetrics
    {
        public long Artifacts;
        public long PhysicalFieldsObserved;
        public long FieldsRetained;
        public long FieldsOmitted;
        public long FullSerializedArtifactBytesObserved;
        public long ProjectedSerializedArtifactBytesWritten;
        public HashSet<string> AvailableFields { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RequiredFields { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OmittedFields { get; } = new(StringComparer.Ordinal);
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
