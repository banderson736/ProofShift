using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Postgres;

public sealed class PostgresShadowTargetConnector : IShadowTargetRecoveryConnector, IShadowTargetWriteSessionProvider
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HashSet<string> _preparedSchemas = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedTables = new(StringComparer.Ordinal);
    private readonly object _prepareLock = new();

    public ConnectorId Id { get; } = new("postgres");
    public string Version => "0.1.0";

    public async ValueTask<IShadowTargetWriteSession> OpenWriteSessionAsync(ShadowTargetContext context,
        ArtifactSelector selector, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var (_, table) = ResolveTable(selector);
        var schema = ShadowSchema(context.RunId);
        var connection = await OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        return new PostgresShadowWriteSession(connection, context, selector, schema, table, batchSize);
    }

    public async Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        var (templateSchema, table) = ResolveTable(selector);
        var schema = ShadowSchema(context.RunId);
        var key = $"{context.ConnectorContext.SystemKey}|{context.ConnectorContext.EndpointKey}|{schema}";
        var tableKey = $"{key}|{table}";
        var createSchema = false;
        var createTable = false;
        lock (_prepareLock)
        {
            createSchema = _preparedSchemas.Add(key);
            createTable = _preparedTables.Add(tableKey);
        }

        if (!createSchema && !createTable)
        {
            return;
        }

        try
        {
            await using var connection = await OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            if (createSchema)
            {
                command.CommandText = $"CREATE SCHEMA {Quote(schema)}";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = $"CREATE TABLE {Quote(schema)}.{Quote("__ps_projection_identity")} (node_key text NOT NULL, artifact_identity bytea NOT NULL, PRIMARY KEY (node_key, artifact_identity))";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (createTable)
            {
                command.CommandText = $"CREATE TABLE {Quote(schema)}.{Quote(table)} (LIKE {Quote(templateSchema)}.{Quote(table)} INCLUDING CONSTRAINTS INCLUDING INDEXES)";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (createSchema)
            {
                lock (_prepareLock) _preparedSchemas.Remove(key);
                lock (_prepareLock) _preparedTables.Remove(tableKey);
            }

            throw;
        }
        catch (Exception exception) when (exception is not ProjectionConnectorException)
        {
            if (createSchema)
            {
                lock (_prepareLock) _preparedSchemas.Remove(key);
                lock (_prepareLock) _preparedTables.Remove(tableKey);
            }

            throw new ProjectionConnectorException("PostgreSQL shadow schema or template preparation failed.");
        }
    }

    public async Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (templateSchema, table) = ResolveTable(request.Selector);
        var schema = ShadowSchema(request.Context.RunId);
        var values = SelectWriteValues(request.Selector, request.Record);
        if (values.Count == 0)
        {
            throw new ProjectionConnectorException("PostgreSQL shadow write has no target values.");
        }

        if (values.Any(pair => pair.Value is BinaryReferenceValue or ObjectValue ||
            pair.Value is CollectionValue collection && collection.Values.Any(value => value is not StringValue and not NullValue)))
        {
            throw new ProjectionConnectorException("PostgreSQL PS-0.5 target writes support scalar values and text arrays; binary artifacts use filesystem shadow storage.");
        }

        await using var connection = await OpenConnectionAsync(request.Context, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var identityCommand = connection.CreateCommand())
            {
                identityCommand.Transaction = transaction;
                identityCommand.CommandText = $"INSERT INTO {Quote(schema)}.{Quote("__ps_projection_identity")} (node_key, artifact_identity) VALUES (@node, @identity)";
                identityCommand.Parameters.AddWithValue("node", request.NodeKey);
                identityCommand.Parameters.AddWithValue("identity", SHA256.HashData(Encoding.UTF8.GetBytes(request.Record.Artifact.Identity)));
                await identityCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var columnNames = values.Select(pair => Quote(pair.Key)).ToArray();
            var parameterNames = values.Select((_, index) => $"@v{index.ToString(CultureInfo.InvariantCulture)}").ToArray();
            await using (var writeCommand = connection.CreateCommand())
            {
                writeCommand.Transaction = transaction;
                writeCommand.CommandText = $"INSERT INTO {Quote(schema)}.{Quote(table)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", parameterNames)})";
                for (var index = 0; index < values.Count; index++)
                {
                    writeCommand.Parameters.AddWithValue(parameterNames[index][1..], ToDatabaseValue(values[index].Value));
                }

                await writeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new ProjectionConnectorException("PSPROJ_TARGET_DUPLICATE", "Duplicate target artifact identity or target uniqueness constraint violation.");
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new ProjectionConnectorException($"PSPROJ_POSTGRES_{exception.SqlState}", "PostgreSQL rejected a parameterized shadow artifact write.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new ProjectionConnectorException("PostgreSQL shadow artifact write failed.");
        }
    }

    public Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ReadRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (_, table) = ResolveTable(request.Selector);
        var schema = ShadowSchema(request.Context.RunId);
        var identityFields = request.Selector.IdentityFields.ToArray();
        if (identityFields.Length == 0 || identityFields.Any(field => !IdentifierPattern.IsMatch(field)))
        {
            throw new ProjectionConnectorException("PostgreSQL shadow read requires valid target identity fields.");
        }

        await using var connection = await OpenConnectionAsync(request.Context, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(schema)}.{Quote(table)} ORDER BY {string.Join(", ", identityFields.Select(Quote))}";
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new SortedDictionary<string, ValueNode>(StringComparer.Ordinal);
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                values.Add(reader.GetName(ordinal), await ReadValueAsync(reader, ordinal, cancellationToken).ConfigureAwait(false));
            }

            var identity = CreateIdentity(values, identityFields);
            var connectorContext = request.Context.ConnectorContext;
            var artifact = new ArtifactReference(
                new ArtifactId(ProjectionIdentityHash(connectorContext.SystemKey, connectorContext.EndpointKey,
                    StableArtifactIdentity.ArtifactTypeFor(request.Selector), identity)),
                new SystemId(connectorContext.SystemKey),
                new StorageEndpointId(connectorContext.EndpointKey),
                StableArtifactIdentity.ArtifactTypeFor(request.Selector),
                identity);
            yield return new RecordEnvelope(
                artifact,
                connectorContext.SemanticType,
                values,
                new ProvenanceMetadata(Id, new StorageEndpointId(connectorContext.EndpointKey), table,
                    DateTimeOffset.UnixEpoch, metadata: [new KeyValuePair<string, string>("observationKind", "projection-readback")]));
        }
    }

    public async Task<ShadowRecoveryCheckpoint> CaptureRecoveryCheckpointAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        var context = request.Targets[0].Context;
        var sourceSchema = ShadowSchema(request.ShadowRunId);
        var checkpointSchema = RecoverySchema(request.CheckpointId);
        await using var connection = await OpenConnectionAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            if (await SchemaExistsAsync(connection, (NpgsqlTransaction)transaction, checkpointSchema, cancellationToken).ConfigureAwait(false))
                throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "PostgreSQL recovery checkpoint ID already exists.");
            await CopySchemaAsync(connection, (NpgsqlTransaction)transaction, sourceSchema, checkpointSchema, cancellationToken).ConfigureAwait(false);
            var contentHash = await HashSchemaAsync(connection, (NpgsqlTransaction)transaction, checkpointSchema, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ShadowRecoveryCheckpoint(request.CheckpointId, request.ShadowRunId, request.SystemId,
                request.EndpointId, Id, Version, request.GraphHash, request.BaselineFingerprint,
                request.TargetArtifactCount, $"postgres-schema-v1:{checkpointSchema}", contentHash, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (ShadowRecoveryConnectorException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "PostgreSQL shadow recovery checkpoint could not be captured.");
        }
    }

    public async Task ValidateRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        ValidateCheckpointBinding(request, checkpoint);
        var schema = RecoverySchema(request.CheckpointId);
        if (checkpoint.Reference != $"postgres-schema-v1:{schema}")
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "PostgreSQL recovery checkpoint reference is invalid.");
        await using var connection = await OpenConnectionAsync(request.Targets[0].Context, cancellationToken).ConfigureAwait(false);
        if (!await SchemaExistsAsync(connection, transaction: null, schema, cancellationToken).ConfigureAwait(false))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "PostgreSQL shadow recovery checkpoint is unavailable.");
        var actualHash = await HashSchemaAsync(connection, transaction: null, schema, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, checkpoint.ContentSha256, StringComparison.Ordinal))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointIntegrityFailure, "PostgreSQL shadow recovery checkpoint failed integrity validation.");
    }

    public async Task<ShadowRecoveryMutation> ApplyControlledMutationAsync(ShadowRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRecoveryRequest(request);
        var schema = ShadowSchema(request.ShadowRunId);
        await using var connection = await OpenConnectionAsync(request.Targets[0].Context, cancellationToken).ConfigureAwait(false);
        foreach (var target in request.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (_, table) = ResolveTable(target.Selector);
            var identityFields = target.Selector.IdentityFields.ToArray();
            if (identityFields.Length == 0 || identityFields.Any(field => !IdentifierPattern.IsMatch(field)))
                throw RecoveryFailure(ShadowRecoveryIssueCodes.RehearsalMutationFailed, "PostgreSQL rehearsal requires valid target identity fields.");
            await using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {Quote(schema)}.{Quote(table)} WHERE ctid = (SELECT ctid FROM {Quote(schema)}.{Quote(table)} ORDER BY {string.Join(", ", identityFields.Select(Quote))} LIMIT 1) RETURNING 1";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                return new ShadowRecoveryMutation(1, "Deleted one deterministic row from the isolated PostgreSQL shadow target.");
        }

        throw RecoveryFailure(ShadowRecoveryIssueCodes.RehearsalMutationFailed, "PostgreSQL recovery rehearsal found no shadow row to mutate.");
    }

    public async Task RestoreRecoveryCheckpointAsync(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await ValidateRecoveryCheckpointAsync(request, checkpoint, cancellationToken).ConfigureAwait(false);
        var targetSchema = ShadowSchema(request.ShadowRunId);
        var checkpointSchema = RecoverySchema(request.CheckpointId);
        var restoreSchema = $"proofshift_restore_{Guid.NewGuid():N}";
        await using var connection = await OpenConnectionAsync(request.Targets[0].Context, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await CopySchemaAsync(connection, (NpgsqlTransaction)transaction, checkpointSchema, restoreSchema, cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (NpgsqlTransaction)transaction;
                command.CommandText = $"DROP SCHEMA {Quote(targetSchema)} CASCADE; ALTER SCHEMA {Quote(restoreSchema)} RENAME TO {Quote(targetSchema)}";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw RecoveryFailure(ShadowRecoveryIssueCodes.RehearsalMutationFailed, "PostgreSQL shadow recovery restore failed.");
        }
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync(ShadowTargetContext context, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = context.ConnectorContext.Configuration.GetRequired("connection")
                .UseValue(value => value);
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new ProjectionConnectorException("PostgreSQL shadow connection failed.");
        }
    }

    private sealed class PostgresShadowWriteSession : IShadowTargetWriteSession
    {
        private readonly NpgsqlConnection _connection;
        private readonly ShadowTargetContext _context;
        private readonly ArtifactSelector _selector;
        private readonly string _schema;
        private readonly string _table;
        private bool _disposed;

        public int BatchSize { get; }

        public PostgresShadowWriteSession(NpgsqlConnection connection, ShadowTargetContext context,
            ArtifactSelector selector, string schema, string table, int batchSize)
        {
            _connection = connection;
            _context = context;
            _selector = selector;
            _schema = schema;
            _table = table;
            BatchSize = batchSize;
        }

        public async Task WriteBatchAsync(IReadOnlyList<ShadowWriteRequest> requests, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(requests);
            if (requests.Count == 0 || requests.Count > BatchSize)
                throw new ArgumentOutOfRangeException(nameof(requests), "PostgreSQL shadow write batches must contain 1..BatchSize artifacts.");

            var selectedValues = new List<KeyValuePair<string, ValueNode>>[requests.Count];
            string[]? columns = null;
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index] ?? throw new ArgumentException("Write batch contains a null request.", nameof(requests));
                if (request.Context.RunId != _context.RunId ||
                    request.Context.ConnectorContext.SystemKey != _context.ConnectorContext.SystemKey ||
                    request.Context.ConnectorContext.EndpointKey != _context.ConnectorContext.EndpointKey ||
                    request.Context.ConnectorContext.NodeKey != _context.ConnectorContext.NodeKey ||
                    request.NodeKey != _context.ConnectorContext.NodeKey)
                    throw new ProjectionConnectorException("PSPROJ_BATCH_CONTEXT", "PostgreSQL write batch contains an artifact from a different target session.");

                var values = SelectWriteValues(_selector, request.Record);
                if (values.Any(pair => pair.Value is BinaryReferenceValue or ObjectValue ||
                    pair.Value is CollectionValue collection && collection.Values.Any(value => value is not StringValue and not NullValue)))
                    throw new ProjectionConnectorException("PostgreSQL PS-0.5 target writes support scalar values and text arrays; binary artifacts use filesystem shadow storage.");
                var currentColumns = values.Select(pair => pair.Key).ToArray();
                if (columns is null) columns = currentColumns;
                else if (!columns.SequenceEqual(currentColumns, StringComparer.Ordinal))
                    throw new ProjectionConnectorException("PostgreSQL write batch contains inconsistent target column sets.");
                selectedValues[index] = values;
            }

            await using var transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var batch = new NpgsqlBatch(_connection) { Transaction = transaction };
                for (var index = 0; index < requests.Count; index++)
                {
                    var request = requests[index];
                    var identityCommand = new NpgsqlBatchCommand(
                        $"INSERT INTO {Quote(_schema)}.{Quote("__ps_projection_identity")} (node_key, artifact_identity) VALUES (@node, @identity)");
                    identityCommand.Parameters.AddWithValue("node", request.NodeKey);
                    identityCommand.Parameters.AddWithValue("identity",
                        SHA256.HashData(Encoding.UTF8.GetBytes(request.Record.Artifact.Identity)));
                    batch.BatchCommands.Add(identityCommand);

                    var values = selectedValues[index];
                    var columnNames = values.Select(pair => Quote(pair.Key)).ToArray();
                    var parameterNames = values.Select((_, valueIndex) => $"@v{valueIndex.ToString(CultureInfo.InvariantCulture)}").ToArray();
                    var writeCommand = new NpgsqlBatchCommand(
                        $"INSERT INTO {Quote(_schema)}.{Quote(_table)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", parameterNames)})");
                    for (var valueIndex = 0; valueIndex < values.Count; valueIndex++)
                        writeCommand.Parameters.AddWithValue(parameterNames[valueIndex][1..], ToDatabaseValue(values[valueIndex].Value));
                    batch.BatchCommands.Add(writeCommand);
                }

                await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ProjectionConnectorException("PSPROJ_TARGET_DUPLICATE", "Duplicate target artifact identity or target uniqueness constraint violation.");
            }
            catch (PostgresException exception)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ProjectionConnectorException($"PSPROJ_POSTGRES_{exception.SqlState}", "PostgreSQL rejected an atomic parameterized shadow write batch.");
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ProjectionConnectorException("PostgreSQL shadow artifact write batch failed.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static (string Schema, string Table) ResolveTable(ArtifactSelector selector)
    {
        string? name = selector.Properties.GetValueOrDefault("name");
        var schema = selector.Properties.GetValueOrDefault("schema") ?? "public";
        var table = selector.Properties.GetValueOrDefault("table");
        if (name is not null)
        {
            var parts = name.Split('.', StringSplitOptions.None);
            if (parts.Length == 1)
            {
                table = parts[0];
            }
            else if (parts.Length == 2)
            {
                schema = parts[0];
                table = parts[1];
            }
            else
            {
                throw new ProjectionConnectorException("PostgreSQL target table identifier is invalid.");
            }
        }

        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table) ||
            !IdentifierPattern.IsMatch(schema) || !IdentifierPattern.IsMatch(table))
        {
            throw new ProjectionConnectorException("PostgreSQL target table identifier is invalid.");
        }

        return (schema, table);
    }

    private static List<KeyValuePair<string, ValueNode>> SelectWriteValues(ArtifactSelector selector, RecordEnvelope record)
    {
        var selected = selector.Properties.TryGetValue("columns", out var columns)
            ? columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : null;
        var values = record.Values.Where(pair => selected is null || selected.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        if (selected is not null && (selected.Count != values.Count || selected.Any(column => !record.Values.ContainsKey(column))))
        {
            throw new ProjectionConnectorException("Configured PostgreSQL target columns do not match projected fields.");
        }

        if (values.Any(pair => !IdentifierPattern.IsMatch(pair.Key)))
        {
            throw new ProjectionConnectorException("PostgreSQL target field contains an invalid identifier.");
        }

        return values;
    }

    private static object ToDatabaseValue(ValueNode value) => value switch
    {
        NullValue => DBNull.Value,
        StringValue text => text.Value,
        IntegerValue integer => integer.Value,
        DecimalValue number => number.Value,
        BooleanValue boolean => boolean.Value,
        DateValue date => date.Value,
        InstantValue instant => instant.Value,
        OffsetDateTimeValue offset => offset.Value,
        LocalDateTimeValue local => local.Value,
        CollectionValue collection => collection.Values.Select(value => value is NullValue ? null : ((StringValue)value).Value).ToArray(),
        _ => throw new ProjectionConnectorException("Unsupported PostgreSQL shadow scalar value.")
    };

    private static async ValueTask<ValueNode> ReadValueAsync(NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return new NullValue();
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            string text => new StringValue(text),
            bool boolean => new BooleanValue(boolean),
            byte or short or int or long => new IntegerValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            decimal number => new DecimalValue(number),
            DateOnly date => new DateValue(date),
            DateTime dateTime when dateTime.Kind == DateTimeKind.Utc => new InstantValue(new DateTimeOffset(dateTime)),
            DateTime dateTime => new LocalDateTimeValue(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified)),
            DateTimeOffset offset => new OffsetDateTimeValue(offset),
            Guid guid => new StringValue(guid.ToString("D", CultureInfo.InvariantCulture)),
            string[] strings => new CollectionValue(strings.Select(value => (ValueNode)new StringValue(value))),
            byte[] bytes => new BinaryReferenceValue("postgres-readback:unsupported", bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            _ => throw new ProjectionConnectorException("PostgreSQL shadow read encountered an unsupported value type.")
        };
    }

    private static string CreateIdentity(SortedDictionary<string, ValueNode> values, IEnumerable<string> identityFields)
    {
        var parts = new List<string>();
        foreach (var field in identityFields)
        {
            if (!values.TryGetValue(field, out var value) || value is NullValue)
            {
                throw new ProjectionConnectorException("PostgreSQL shadow identity is missing or NULL.");
            }

            var canonical = CanonicalIdentityValue(value);
            parts.Add($"{field.Length.ToString(CultureInfo.InvariantCulture)}:{field}={canonical.Length.ToString(CultureInfo.InvariantCulture)}:{canonical}");
        }

        return string.Join('|', parts);
    }

    private static string CanonicalIdentityValue(ValueNode value) => value switch
    {
        StringValue text => $"string:{text.Value.Normalize(NormalizationForm.FormC)}",
        IntegerValue integer => $"integer:{integer.Value.ToString(CultureInfo.InvariantCulture)}",
        DecimalValue number => $"decimal:{number.Value.ToString("G29", CultureInfo.InvariantCulture)}",
        BooleanValue boolean => boolean.Value ? "boolean:true" : "boolean:false",
        DateValue date => $"date:{date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        _ => throw new ProjectionConnectorException("PostgreSQL target identity type is unsupported.")
    };

    private static string ProjectionIdentityHash(string system, string endpoint, string type, string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(new[] { system, endpoint, type, identity }
            .Select(part => $"{part.Length.ToString(CultureInfo.InvariantCulture)}:{part}")))))
            .ToLowerInvariant();

    private static string ShadowSchema(RunId runId) => $"proofshift_shadow_{runId.Value:N}";
    private static string RecoverySchema(RecoveryCheckpointId checkpointId) => $"proofshift_recovery_{checkpointId.Value:N}";

    private void ValidateRecoveryRequest(ShadowRecoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConnectorId != Id || request.Targets.Any(target => target.Context.ConnectorContext.Connector != Id ||
            target.Context.Role != SystemRole.ShadowTarget || target.Context.RunId != request.ShadowRunId))
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "PostgreSQL recovery request does not match this shadow connector/run.");
    }

    private void ValidateCheckpointBinding(ShadowRecoveryRequest request, ShadowRecoveryCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Id != request.CheckpointId || checkpoint.ShadowRunId != request.ShadowRunId ||
            checkpoint.SystemId != request.SystemId || checkpoint.EndpointId != request.EndpointId ||
            checkpoint.ConnectorId != Id || checkpoint.ConnectorVersion != Version || checkpoint.GraphHash != request.GraphHash ||
            checkpoint.BaselineFingerprint != request.BaselineFingerprint || checkpoint.TargetArtifactCount != request.TargetArtifactCount)
            throw RecoveryFailure(ShadowRecoveryIssueCodes.ContextMismatch, "PostgreSQL recovery checkpoint binding does not match the requested shadow state.");
    }

    private static async Task<bool> SchemaExistsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string schema, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = @schema)";
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static async Task CopySchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceSchema, string targetSchema, CancellationToken cancellationToken)
    {
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"CREATE SCHEMA {Quote(targetSchema)}";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var list = connection.CreateCommand();
        list.Transaction = transaction;
        list.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = @schema ORDER BY tablename";
        list.Parameters.AddWithValue("schema", sourceSchema);
        var tables = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                tables.Add(reader.GetString(0));
        }
        if (tables.Count == 0)
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "PostgreSQL shadow schema contains no recovery tables.");

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText = $"CREATE TABLE {Quote(targetSchema)}.{Quote(table)} (LIKE {Quote(sourceSchema)}.{Quote(table)} INCLUDING ALL); INSERT INTO {Quote(targetSchema)}.{Quote(table)} SELECT * FROM {Quote(sourceSchema)}.{Quote(table)}";
            await copy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> HashSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string schema, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "proofshift-postgres-shadow-checkpoint-v1");
        await using var tablesCommand = connection.CreateCommand();
        tablesCommand.Transaction = transaction;
        tablesCommand.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = @schema ORDER BY tablename";
        tablesCommand.Parameters.AddWithValue("schema", schema);
        var tables = new List<string>();
        await using (var tablesReader = await tablesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await tablesReader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(tablesReader.GetString(0));
        if (tables.Count == 0)
            throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointUnavailable, "PostgreSQL recovery schema contains no tables.");

        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, table);
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.Transaction = transaction;
            columnsCommand.CommandText = "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table ORDER BY ordinal_position";
            columnsCommand.Parameters.AddWithValue("schema", schema);
            columnsCommand.Parameters.AddWithValue("table", table);
            var columns = new List<(string Name, string Type)>();
            await using (var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await columnsReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    columns.Add((columnsReader.GetString(0), columnsReader.GetString(1)));
            if (columns.Count == 0) throw RecoveryFailure(ShadowRecoveryIssueCodes.CheckpointIntegrityFailure, "PostgreSQL recovery table has no columns.");
            foreach (var column in columns)
            {
                Append(hash, column.Name);
                Append(hash, column.Type);
            }

            var ordering = string.Join(", ", columns.Select(column => Quote(column.Name)));
            await using var rowsCommand = connection.CreateCommand();
            rowsCommand.Transaction = transaction;
            rowsCommand.CommandText = $"SELECT * FROM {Quote(schema)}.{Quote(table)} ORDER BY {ordering}";
            await using var rows = await rowsCommand.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Append(hash, "row");
                for (var index = 0; index < columns.Count; index++)
                {
                    Append(hash, rows.IsDBNull(index) ? "null" : CanonicalDatabaseValue(rows.GetValue(index)));
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CanonicalDatabaseValue(object value) => value switch
    {
        byte[] bytes => "bytes:" + Convert.ToHexString(bytes),
        DateTimeOffset offset => "offset:" + offset.ToString("O", CultureInfo.InvariantCulture),
        DateTime dateTime => "datetime:" + dateTime.ToString("O", CultureInfo.InvariantCulture),
        Array array => "array:[" + string.Join(',', array.Cast<object?>().Select(item => item is null ? "null" : CanonicalDatabaseValue(item))) + "]",
        IFormattable formatted => value.GetType().FullName + ":" + formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> size = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
        hash.AppendData(size);
        hash.AppendData(bytes);
    }

    private static ShadowRecoveryConnectorException RecoveryFailure(string code, string message) => new(code, message);
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}