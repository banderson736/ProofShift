using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public abstract class RelationalSourceConnectorBase : ICheckpointSourceConnector, ISourceBinaryContentResolver, IPhysicalDiscoveryConnector
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    protected RelationalSourceConnectorBase(string connectorId, string version)
    {
        Id = new ConnectorId(connectorId);
        Version = version;
    }

    public ConnectorId Id { get; }
    public string Version { get; }
    public ConnectorCapabilityDescriptor Capabilities => new(Id.Value, Version, true, true, true, true,
        false, true, true, CheckpointConsistency.ToString(), false,
        EvaluationScopePredicates: new DomainList<string>(
        [
            "equals", "not-equals", "in", "range", "date-time-range", "is-null", "is-not-null"
        ]));

    protected abstract DbConnection CreateConnection(string connectionString);
    protected abstract string ColumnsSql { get; }
    protected abstract string PrimaryKeySql { get; }
    protected abstract string ApproximateRowCountSql { get; }
    protected abstract string QuoteIdentifier(string identifier);
    protected abstract string DiscoveryColumnsSql { get; }
    protected abstract string DiscoveryKeysSql { get; }
    protected abstract string DiscoveryRelationshipsSql { get; }
    protected virtual void ConfigureDiscoveryCommand(DbCommand command, ConnectorContext context) { }

    public async Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context,
        IReadOnlyCollection<ArtifactSelector> selectors, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var connection = CreateConnection(GetConnectionString(context));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var objects = new Dictionary<(string Schema, string Name), (string Kind, List<PhysicalField> Fields)>();
        var keys = new Dictionary<(string Schema, string Name, string Key, bool Primary), List<string>>();
        var relations = new Dictionary<(string Schema, string Name, string Key, string TargetSchema, string Target), (List<string> Fields, List<string> Targets)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = DiscoveryColumnsSql;
            ConfigureDiscoveryCommand(command, context);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!objects.TryGetValue(key, out var item)) item = (reader.GetString(2), []);
                item.Fields.Add(new(reader.GetString(3), reader.GetString(4), ReadNullableState(reader.GetValue(5)), reader.GetInt32(6)));
                objects[key] = item;
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = DiscoveryKeysSql;
            ConfigureDiscoveryCommand(command, context);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2), ReadNullableState(reader.GetValue(3)));
                if (!keys.TryGetValue(key, out var fields)) keys.Add(key, fields = []);
                fields.Add(reader.GetString(4));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = DiscoveryRelationshipsSql;
            ConfigureDiscoveryCommand(command, context);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(4), reader.GetString(5));
                if (!relations.TryGetValue(key, out var fields)) relations.Add(key, fields = ([], []));
                fields.Fields.Add(reader.GetString(3));
                fields.Targets.Add(reader.GetString(6));
            }
        }
        return PhysicalDiscovery.Create(context, Id.Value, Version, objects.Select(pair => new PhysicalObject(
            pair.Key.Schema, pair.Key.Name, pair.Value.Kind, pair.Value.Fields,
            keys.Where(key => key.Key.Schema == pair.Key.Schema && key.Key.Name == pair.Key.Name)
                .Select(key => new PhysicalKey(key.Key.Key, key.Key.Primary, key.Value)).ToArray(),
            relations.Where(relation => relation.Key.Schema == pair.Key.Schema && relation.Key.Name == pair.Key.Name)
                .Select(relation => new PhysicalRelationship(relation.Key.Key, relation.Value.Fields,
                    relation.Key.TargetSchema, relation.Key.Target, relation.Value.Targets)).ToArray())));
    }
    protected virtual IsolationLevel? CheckpointIsolationLevel => null;

    public SourceConsistencyGuarantee CheckpointConsistency => CheckpointIsolationLevel is null
        ? SourceConsistencyGuarantee.Observed
        : SourceConsistencyGuarantee.Consistent;

    public virtual CheckpointConsistencyDecision ResolveCheckpointConsistency(ConnectorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var isolation = CheckpointIsolationLevel;
        var guarantee = isolation is null ? SourceConsistencyGuarantee.Observed : SourceConsistencyGuarantee.Consistent;
        return new CheckpointConsistencyDecision(guarantee == SourceConsistencyGuarantee.Consistent ? "transaction-consistent" : "observed",
            isolation is null ? "observed" : NormalizeIsolationName(isolation.Value), guarantee, null, isolation);
    }

    protected static string NormalizeIsolationName(IsolationLevel isolation) => isolation switch
    {
        IsolationLevel.ReadCommitted => "read-committed",
        IsolationLevel.RepeatableRead => "repeatable-read",
        IsolationLevel.Serializable => "serializable",
        IsolationLevel.Snapshot => "snapshot",
        IsolationLevel.ReadUncommitted => "read-uncommitted",
        _ => isolation.ToString().ToLowerInvariant()
    };

    public async Task<SourceInspection> InspectAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        if (!string.Equals(selector.Kind, "table", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(context, ConnectorIssueCodes.UnsupportedSelector, "Relational connectors support table selectors only.");
        }

        if (!TryResolveTable(selector, out var schema, out var table, out var identifierError))
        {
            return Invalid(context, ConnectorIssueCodes.InvalidPhysicalIdentifier, identifierError);
        }

        string connectionString;
        try
        {
            connectionString = GetConnectionString(context);
        }
        catch (ConnectorConfigurationException exception)
        {
            return Failed(context, exception.Code, exception.Message);
        }

        try
        {
            await using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var columns = await ReadColumnsAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
            if (columns.Count == 0)
            {
                return Invalid(context, ConnectorIssueCodes.SourceObjectNotFound, "Configured source table was not found.", FormatTable(schema, table));
            }

            var primaryKey = await ReadPrimaryKeyAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
            var identity = selector.IdentityFields.Count > 0 ? selector.IdentityFields.ToArray() : primaryKey.ToArray();
            if (identity.Length == 0)
            {
                return Invalid(context, ConnectorIssueCodes.NonDeterministicIdentity,
                    "No identity fields are configured and the source table has no discoverable primary key.", FormatTable(schema, table));
            }

            var columnsByName = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
            foreach (var identityField in identity)
            {
                if (!IsValidIdentifier(identityField))
                {
                    return Invalid(context, ConnectorIssueCodes.InvalidPhysicalIdentifier,
                        "Identity field contains an invalid physical identifier.", FormatTable(schema, table));
                }

                if (!columnsByName.TryGetValue(identityField, out var column))
                {
                    return Invalid(context, ConnectorIssueCodes.IdentityFieldNotFound,
                        "Configured or discovered identity field was not found in the source table.", FormatTable(schema, table));
                }

                if (column.IsNullable)
                {
                    return Invalid(context, ConnectorIssueCodes.NonDeterministicIdentity,
                        "Identity fields must be non-nullable.", FormatTable(schema, table));
                }

                if (IsBinaryType(column.DataType))
                {
                    return Invalid(context, ConnectorIssueCodes.NonDeterministicIdentity,
                        "Binary columns cannot be used as relational artifact identity fields.", FormatTable(schema, table));
                }
            }

            try
            {
                _ = ResolveSelectedColumns(selector, columns, identity);
            }
            catch (ConnectorReadException exception)
            {
                return Invalid(context, exception.Code, exception.Message, FormatTable(schema, table));
            }

            var approximateCount = await TryReadApproximateCountAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
            return new SourceInspection(
                SourceInspectionStatus.Valid,
                columns,
                primaryKey,
                identity,
                estimatedRecords: approximateCount,
                physicalObject: FormatTable(schema, table));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(context, ConnectorIssueCodes.SourceConnectionFailed,
                $"Source inspection failed ({InspectionFailureDiagnostic(exception)}).");
        }
    }

    public async ValueTask<Stream> OpenBinaryReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ArtifactReference artifact,
        BinaryReferenceValue binaryReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(binaryReference);
        if (artifact.SystemId.Value != context.SystemKey || artifact.EndpointId.Value != context.EndpointKey)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound,
                "Binary reference does not belong to the supplied source endpoint.");
        }

        if (!TryDecodeBinaryLocator(binaryReference.Reference, out var locator) ||
            !TryResolveTable(selector, out var schema, out var table, out _) ||
            !string.Equals(locator.Schema, schema, StringComparison.Ordinal) ||
            !string.Equals(locator.Table, table, StringComparison.Ordinal))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound,
                "Binary content reference is invalid for the configured source selector.");
        }

        if (locator.Identity.Length == 0 || locator.Identity.Any(part => !IsValidIdentifier(part.Name)) ||
            !IsValidIdentifier(locator.Column))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier,
                "Binary content reference contains an invalid physical identifier.");
        }

        var connectionString = GetConnectionString(context);
        var connection = CreateConnectionForRead(connectionString);
        DbCommand? command = null;
        DbDataReader? reader = null;
        try
        {
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            command = connection.CreateCommand();
            var predicates = new List<string>(locator.Identity.Length);
            for (var index = 0; index < locator.Identity.Length; index++)
            {
                var part = locator.Identity[index];
                var parameterName = $"identity{index.ToString(CultureInfo.InvariantCulture)}";
                predicates.Add($"{QuoteIdentifier(part.Name)} = {ParameterPlaceholder(parameterName)}");
                AddParameter(command, parameterName, ParseLocatorValue(part));
            }

            command.CommandText = $"SELECT {QuoteIdentifier(locator.Column)} FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} WHERE {string.Join(" AND ", predicates)}";
            reader = await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false);
            if (!await ReadNextAsync(reader, cancellationToken).ConfigureAwait(false) || await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound,
                    "Referenced binary source content is unavailable.");
            }

            var stream = reader.GetStream(0);
            return new OwnedRelationalStream(stream, reader, command, connection);
        }
        catch (OperationCanceledException)
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (ConnectorReadException)
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed,
                "Referenced binary content could not be opened.");
        }
    }

    public IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        CancellationToken cancellationToken) =>
        ReadRecordsAsync(context, selector, options, isolationLevel: null, cancellationToken);

    public IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        CancellationToken cancellationToken) =>
        ReadRecordsAsync(context, selector, options, ResolveCheckpointConsistency(context).IsolationLevel, cancellationToken);

    private async IAsyncEnumerable<RecordEnvelope> ReadRecordsAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        IsolationLevel? isolationLevel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        EvaluationScopeCapability.Validate(this, selector);
        if (options.Partition.Count > 0)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.PartitioningUnsupported,
                "Partitioned source reads are not implemented yet.");
        }
        if (!string.Equals(selector.Kind, "table", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "Relational connectors support table selectors only.");
        }

        var inspection = await InspectAsync(context, selector, cancellationToken).ConfigureAwait(false);
        if (inspection.Status != SourceInspectionStatus.Valid)
        {
            var issue = inspection.Issues.Count > 0 ? inspection.Issues[0] : new ConnectorIssue(
                ConnectorIssueCodes.SourceObjectNotFound,
                ConnectorIssueSeverity.Error,
                "Source table could not be inspected.");
            throw new ConnectorReadException(issue.Code, issue.Message);
        }

        if (!TryResolveTable(selector, out var schema, out var table, out var identifierError))
        {
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier, identifierError);
        }

        var identity = inspection.IdentityFields.ToArray();
        var selectedColumns = ResolveSelectedColumns(selector, inspection.Columns, identity);
        string connectionString;
        try
        {
            connectionString = GetConnectionString(context);
        }
        catch (ConnectorConfigurationException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }

        await using var connection = CreateConnectionForRead(connectionString);
        await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = isolationLevel is { } isolation
            ? await connection.BeginTransactionAsync(isolation, cancellationToken).ConfigureAwait(false)
            : null;
        string? previousArtifactId = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = BuildSelectSql(schema, table, selectedColumns, identity, selector,
                inspection.Columns, command);
            await using (var reader = await ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false))
            {
                while (await ReadNextAsync(reader, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = await CreateRecordEnvelopeAsync(context, reader, schema, table, identity, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(previousArtifactId, record.Artifact.Id.Value, StringComparison.Ordinal))
                    {
                        throw new ConnectorReadException(ConnectorIssueCodes.DuplicateArtifactIdentity,
                            "Source read encountered duplicate artifact identities.");
                    }

                    previousArtifactId = record.Artifact.Id.Value;
                    yield return record;
                }
            }
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task OpenConnectionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Source connection was canceled.", exception, cancellationToken);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceConnectionFailed, "Source connection failed.");
        }
    }

    private DbConnection CreateConnectionForRead(string connectionString)
    {
        try
        {
            return CreateConnection(connectionString);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceConnectionFailed, "Source connection could not be initialized.");
        }
    }

    private static async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Source query was canceled.", exception, cancellationToken);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Source query failed.");
        }
    }

    private static async Task<bool> ReadNextAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Source row read was canceled.", exception, cancellationToken);
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Source row read failed.");
        }
    }

    private async Task<RecordEnvelope> CreateRecordEnvelopeAsync(
        ConnectorContext context,
        DbDataReader reader,
        string schema,
        string table,
        IReadOnlyCollection<string> identityFields,
        CancellationToken cancellationToken)
    {
        try
        {
            var identitySet = identityFields.ToHashSet(StringComparer.Ordinal);
            var values = new List<KeyValuePair<string, ValueNode>>(reader.FieldCount);
            var identityValues = new Dictionary<string, object?>(StringComparer.Ordinal);
            var binaryValues = new List<(string Column, long Length, string Hash)>();
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                var name = reader.GetName(ordinal);
                if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
                {
                    values.Add(new KeyValuePair<string, ValueNode>(name, new NullValue()));
                    if (identitySet.Contains(name))
                    {
                        identityValues[name] = null;
                    }

                    continue;
                }

                if (IsBinaryField(reader, ordinal))
                {
                    var (binaryLength, binaryHash) = await HashBinaryFieldAsync(reader, ordinal, cancellationToken).ConfigureAwait(false);
                    binaryValues.Add((name, binaryLength, binaryHash));
                    if (identitySet.Contains(name))
                    {
                        identityValues[name] = $"sha256:{binaryHash}";
                    }

                    continue;
                }

                var rawValue = ReadProviderValue(reader, ordinal);
                values.Add(new KeyValuePair<string, ValueNode>(name, NormalizeProviderValue(rawValue, reader.GetDataTypeName(ordinal))));
                if (identitySet.Contains(name))
                {
                    identityValues[name] = rawValue;
                }
            }

            var physicalIdentity = FormatIdentity(schema, table, identityFields, identityValues);
            var identityLocator = identityFields.Select(field => CreateLocatorPart(field, identityValues[field])).ToArray();
            foreach (var (column, length, hash) in binaryValues)
            {
                var locator = EncodeBinaryLocator(new RelationalBinaryLocator(schema, table, column, identityLocator));
                values.Add(new KeyValuePair<string, ValueNode>(column,
                    new BinaryReferenceValue(locator, length, hash)));
            }

            var artifact = new ArtifactReference(
                new ArtifactId(ProofShift.Connectors.Abstractions.ArtifactIdentity.Create(context.SystemKey, context.EndpointKey, "row", physicalIdentity)),
                new SystemId(context.SystemKey),
                new StorageEndpointId(context.EndpointKey),
                "row",
                physicalIdentity);
            var provenance = new ProvenanceMetadata(
                Id,
                new StorageEndpointId(context.EndpointKey),
                FormatTable(schema, table),
                DateTimeOffset.UtcNow,
                metadata:
                [
                    new KeyValuePair<string, string>("identityFields", string.Join(',', identityFields)),
                    new KeyValuePair<string, string>("observationKind", "read")
                ]);
            return new RecordEnvelope(artifact, context.SemanticType, values, provenance);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Source row normalization was canceled.", exception, cancellationToken);
        }
        catch (ConnectorReadException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Source row could not be normalized.");
        }
    }

    private async Task<List<SourceColumn>> ReadColumnsAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ColumnsSql;
        AddParameter(command, "schema", schema);
        AddParameter(command, "table", table);
        var columns = new List<SourceColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new SourceColumn(
                reader.GetString(0),
                reader.GetString(1),
                ReadNullableState(reader.GetValue(2)),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }

        return columns;
    }

    private static bool ReadNullableState(object value) => value switch
    {
        bool nullable => nullable,
        string text => !string.Equals(text, "N", StringComparison.OrdinalIgnoreCase) && !string.Equals(text, "NO", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };

    private async Task<List<string>> ReadPrimaryKeyAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PrimaryKeySql;
        AddParameter(command, "schema", schema);
        AddParameter(command, "table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<long?> TryReadApproximateCountAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ApproximateRowCountSql;
            AddParameter(command, "schema", schema);
            AddParameter(command, "table", table);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private string BuildSelectSql(string schema, string table, IReadOnlyCollection<string> columns,
        IReadOnlyCollection<string> identity, ArtifactSelector selector, IReadOnlyCollection<SourceColumn> sourceColumns,
        DbCommand command)
    {
        var selected = string.Join(", ", columns.Select(QuoteIdentifier));
        var order = string.Join(", ", identity.Select(QuoteIdentifier));
        var predicates = new List<string>();
        if (selector.EvaluationScope is { } scope)
        {
            for (var index = 0; index < scope.Predicates.Count; index++)
            {
                var predicate = scope.Predicates[index];
                var column = sourceColumns.SingleOrDefault(item =>
                    string.Equals(item.Name, predicate.Field, StringComparison.Ordinal));
                if (column is null || !IsValidIdentifier(predicate.Field) || IsBinaryType(column.DataType))
                    throw new ConnectorReadException(ConnectorIssueCodes.InvalidEvaluationPredicate,
                        "Evaluation predicate field is absent from discovery or has an unsupported physical type.");
                var field = QuoteIdentifier(predicate.Field);
                var parameterRoot = $"scope{index.ToString(CultureInfo.InvariantCulture)}";
                switch (predicate.Operator)
                {
                    case EvaluationPredicateOperator.IsNull:
                        predicates.Add($"{field} IS NULL");
                        break;
                    case EvaluationPredicateOperator.IsNotNull:
                        predicates.Add($"{field} IS NOT NULL");
                        break;
                    case EvaluationPredicateOperator.Equals:
                    case EvaluationPredicateOperator.NotEquals:
                        predicates.Add($"{field} {(predicate.Operator == EvaluationPredicateOperator.Equals ? "=" : "<>")} {ParameterPlaceholder(parameterRoot)}");
                        AddParameter(command, parameterRoot, ParseScopeValue(column.DataType, predicate.Values[0],
                            predicate.Operator));
                        break;
                    case EvaluationPredicateOperator.In:
                    {
                        var placeholders = new List<string>(predicate.Values.Count);
                        for (var valueIndex = 0; valueIndex < predicate.Values.Count; valueIndex++)
                        {
                            var parameterName = $"{parameterRoot}_{valueIndex.ToString(CultureInfo.InvariantCulture)}";
                            placeholders.Add(ParameterPlaceholder(parameterName));
                            AddParameter(command, parameterName, ParseScopeValue(column.DataType,
                                predicate.Values[valueIndex], predicate.Operator));
                        }
                        predicates.Add($"{field} IN ({string.Join(", ", placeholders)})");
                        break;
                    }
                    case EvaluationPredicateOperator.Range:
                    case EvaluationPredicateOperator.DateTimeRange:
                    {
                        var numeric = IsNumericType(column.DataType);
                        var temporal = IsTemporalType(column.DataType);
                        if (predicate.Operator == EvaluationPredicateOperator.Range && !numeric ||
                            predicate.Operator == EvaluationPredicateOperator.DateTimeRange && !temporal)
                            throw new ConnectorReadException(ConnectorIssueCodes.InvalidEvaluationPredicate,
                                "Evaluation range predicate does not match the discovered field type.");
                        var lowerParameter = $"{parameterRoot}_from";
                        var upperParameter = $"{parameterRoot}_to";
                        predicates.Add($"{field} >= {ParameterPlaceholder(lowerParameter)} AND {field} <= {ParameterPlaceholder(upperParameter)}");
                        var lower = ParseScopeValue(column.DataType, predicate.Values[0], predicate.Operator);
                        var upper = ParseScopeValue(column.DataType, predicate.Values[1], predicate.Operator);
                        if (((IComparable)lower).CompareTo(upper) > 0)
                            throw new ConnectorReadException(ConnectorIssueCodes.InvalidEvaluationPredicate,
                                "Evaluation range lower bound must not exceed its upper bound.");
                        AddParameter(command, lowerParameter, lower);
                        AddParameter(command, upperParameter, upper);
                        break;
                    }
                    default:
                        throw new ConnectorReadException(ConnectorIssueCodes.InvalidEvaluationPredicate,
                            "Evaluation predicate operator is unsupported.");
                }
            }
        }

        var where = predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
        return $"SELECT {selected} FROM {QuoteIdentifier(schema)}.{QuoteIdentifier(table)}{where} ORDER BY {order}";
    }

    private static object ParseScopeValue(string dataType, string value, EvaluationPredicateOperator operation)
    {
        var type = dataType.ToLowerInvariant();
        if (IsTemporalType(type))
        {
            if (type.Contains("with time zone", StringComparison.Ordinal) ||
                type.Contains("timestamptz", StringComparison.Ordinal) ||
                type.Contains("datetimeoffset", StringComparison.Ordinal))
            {
                if (!HasExplicitOffset(value) ||
                    !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
                    throw InvalidScopeValue();
                return offset;
            }
            if (type == "date" || type.StartsWith("date ", StringComparison.Ordinal))
            {
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw InvalidScopeValue();
                return date.ToDateTime(TimeOnly.MinValue);
            }
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var local) ||
                local.Kind != DateTimeKind.Unspecified)
                throw InvalidScopeValue();
            return local;
        }

        if (IsNumericType(type))
        {
            if (type.Contains("real", StringComparison.Ordinal) || type.Contains("float", StringComparison.Ordinal) ||
                type.Contains("double", StringComparison.Ordinal))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatingPoint) ||
                    !double.IsFinite(floatingPoint))
                    throw InvalidScopeValue();
                return floatingPoint;
            }
            if (type.Contains("int", StringComparison.Ordinal) || type.Contains("serial", StringComparison.Ordinal))
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw InvalidScopeValue();
                return integer;
            }
            if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var number))
                throw InvalidScopeValue();
            return number;
        }

        if (type is "uuid" or "uniqueidentifier")
        {
            if (!Guid.TryParse(value, out var identifier)) throw InvalidScopeValue();
            return identifier;
        }

        if (type is "bit" or "bool" or "boolean")
        {
            if (bool.TryParse(value, out var boolean)) return boolean;
            if (value is "0" or "1") return value == "1";
            throw InvalidScopeValue();
        }

        if (operation is EvaluationPredicateOperator.Range or EvaluationPredicateOperator.DateTimeRange)
            throw InvalidScopeValue();
        if (IsTextType(type)) return value;
        throw InvalidScopeValue();
    }

    private static bool IsNumericType(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type is "int" or "integer" or "smallint" or "bigint" or "tinyint" or "serial" or
                "bigserial" or "smallserial" or "money" or "real" or "float" or "double" ||
            type.StartsWith("int", StringComparison.Ordinal) && !type.StartsWith("interval", StringComparison.Ordinal) ||
            type.StartsWith("decimal", StringComparison.Ordinal) || type.StartsWith("numeric", StringComparison.Ordinal) ||
            type.StartsWith("number", StringComparison.Ordinal) || type.StartsWith("money", StringComparison.Ordinal) ||
            type.StartsWith("float", StringComparison.Ordinal) || type.StartsWith("double", StringComparison.Ordinal) ||
            type.StartsWith("real", StringComparison.Ordinal) || type.StartsWith("serial", StringComparison.Ordinal);
    }

    private static bool IsTemporalType(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type == "date" || type.StartsWith("date ", StringComparison.Ordinal) ||
            type.Contains("timestamp", StringComparison.Ordinal) || type.Contains("datetime", StringComparison.Ordinal) ||
            type.StartsWith("time stamp", StringComparison.Ordinal);
    }

    private static bool IsTextType(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type is "text" or "ntext" or "string" or "citext" or "name" or "bpchar" ||
            type.Contains("char", StringComparison.Ordinal) || type.Contains("clob", StringComparison.Ordinal);
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z')) return true;
        if (value.Length < 6 || value[^6] is not ('+' or '-') || value[^3] != ':') return false;
        return char.IsAsciiDigit(value[^5]) && char.IsAsciiDigit(value[^4]) &&
            char.IsAsciiDigit(value[^2]) && char.IsAsciiDigit(value[^1]);
    }

    private static ConnectorReadException InvalidScopeValue() => new(ConnectorIssueCodes.InvalidEvaluationPredicate,
        "Evaluation predicate value is incompatible with the discovered field type or invariant format.");

    private static string[] ResolveSelectedColumns(
        ArtifactSelector selector,
        IReadOnlyCollection<SourceColumn> available,
        IEnumerable<string> identity)
    {
        if (!selector.Properties.TryGetValue("columns", out var configuredColumns) || string.IsNullOrWhiteSpace(configuredColumns))
        {
            return available.Select(column => column.Name).ToArray();
        }

        var selected = configuredColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        selected.UnionWith(identity);
        var missingColumn = false;
        foreach (var selectedColumn in selected)
        {
            if (available.All(candidate => !string.Equals(candidate.Name, selectedColumn, StringComparison.Ordinal)))
            {
                missingColumn = true;
                break;
            }
        }

        if (selected.Count == 0 || missingColumn)
        {
            throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound, "Configured selected column was not found in the source table.");
        }

        return available.Where(column => selected.Contains(column.Name)).Select(column => column.Name).ToArray();
    }

    private bool TryResolveTable(ArtifactSelector selector, out string schema, out string table, out string error)
    {
        schema = string.Empty;
        table = string.Empty;
        error = "Table selector must provide a valid name, schema/table pair, or schema and table properties.";
        if (selector.Properties.TryGetValue("schema", out var configuredSchema) &&
            selector.Properties.TryGetValue("table", out var configuredTable))
        {
            schema = configuredSchema;
            table = configuredTable;
        }
        else if (selector.Properties.TryGetValue("name", out var name))
        {
            var separator = name.IndexOf('.');
            if (separator < 0)
            {
                schema = DefaultSchema;
                table = name;
            }
            else if (separator > 0 && separator < name.Length - 1 && name.IndexOf('.', separator + 1) < 0)
            {
                schema = name[..separator];
                table = name[(separator + 1)..];
            }
        }

        if (IsValidIdentifier(schema) && IsValidIdentifier(table))
        {
            error = string.Empty;
            return true;
        }

        return false;
    }

    protected abstract string DefaultSchema { get; }

    private static string GetConnectionString(ConnectorContext context)
    {
        var key = context.Configuration.TryGet("connection", out _) ? "connection" : "connectionString";
        return context.Configuration.GetRequired(key).UseValue(value => value);
    }

    protected virtual object ReadProviderValue(DbDataReader reader, int ordinal) => reader.GetValue(ordinal);
    protected virtual ValueNode NormalizeProviderValue(object value, string providerType) => ToValueNode(value, providerType);
    protected virtual string InspectionFailureDiagnostic(Exception exception) => exception.GetType().Name;

    private static ValueNode ToValueNode(object value, string providerType)
    {
        if (value is DateTime calendarDateTime && string.Equals(providerType, "date", StringComparison.OrdinalIgnoreCase))
            return new DateValue(DateOnly.FromDateTime(calendarDateTime));

        return value switch
    {
        string text => new StringValue(text),
        char character => new StringValue(character.ToString()),
        bool boolean => new BooleanValue(boolean),
        byte or sbyte or short or ushort or int or uint or long => new IntegerValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        decimal number => new DecimalValue(number),
        float or double => new StringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        DateOnly date => new DateValue(date),
        DateTimeOffset dateTimeOffset => new OffsetDateTimeValue(dateTimeOffset),
        DateTime { Kind: DateTimeKind.Utc } dateTime => new InstantValue(new DateTimeOffset(dateTime)),
        DateTime { Kind: DateTimeKind.Unspecified } dateTime => new LocalDateTimeValue(dateTime),
        DateTime => throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType,
            "Provider returned a machine-local timestamp without source timezone semantics."),
        Guid guid => new StringValue(guid.ToString("D", CultureInfo.InvariantCulture)),
        byte[] => throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType,
            "Binary source values must be read sequentially as streams."),
        TimeSpan time => new StringValue(time.ToString("c", CultureInfo.InvariantCulture)),
        _ => throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType, "Source column uses an unsupported physical type.")
    };
    }

    private static async Task<(long Length, string Hash)> HashBinaryFieldAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        await using var stream = reader.GetStream(ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string FormatIdentity(
        string schema,
        string table,
        IReadOnlyCollection<string> identityFields,
        Dictionary<string, object?> values)
    {
        var builder = new StringBuilder();
        builder.Append(schema).Append('.').Append(table);
        foreach (var field in identityFields)
        {
            if (!values.TryGetValue(field, out var value) || value is null or DBNull)
            {
                throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity, "Source row contains a null identity value.");
            }

            var text = FormatIdentityValue(value);
            builder.Append('|').Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field)
                .Append('=').Append(Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);
        }

        return builder.ToString();
    }

    private static string FormatIdentityValue(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes).ToLowerInvariant(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static BinaryLocatorPart CreateLocatorPart(string name, object? value)
    {
        if (value is null or DBNull or byte[])
        {
            throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity,
                "Binary content requires stable scalar identity fields.");
        }

        var (type, text) = value switch
        {
            string textValue => ("string", textValue),
            char character => ("char", character.ToString()),
            bool boolean => ("boolean", boolean ? "true" : "false"),
            byte number => ("byte", number.ToString(CultureInfo.InvariantCulture)),
            sbyte number => ("sbyte", number.ToString(CultureInfo.InvariantCulture)),
            short number => ("int16", number.ToString(CultureInfo.InvariantCulture)),
            ushort number => ("uint16", number.ToString(CultureInfo.InvariantCulture)),
            int number => ("int32", number.ToString(CultureInfo.InvariantCulture)),
            uint number => ("uint32", number.ToString(CultureInfo.InvariantCulture)),
            long number => ("int64", number.ToString(CultureInfo.InvariantCulture)),
            ulong number => ("uint64", number.ToString(CultureInfo.InvariantCulture)),
            decimal number => ("decimal", number.ToString(CultureInfo.InvariantCulture)),
            float number => ("single", number.ToString("R", CultureInfo.InvariantCulture)),
            double number => ("double", number.ToString("R", CultureInfo.InvariantCulture)),
            Guid guid => ("guid", guid.ToString("D", CultureInfo.InvariantCulture)),
            DateTimeOffset dateTimeOffset => ("datetimeoffset", dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)),
            DateTime { Kind: DateTimeKind.Utc } dateTime => ("instant", dateTime.ToString("O", CultureInfo.InvariantCulture)),
            DateTime { Kind: DateTimeKind.Unspecified } dateTime => ("localdatetime", dateTime.ToString("O", CultureInfo.InvariantCulture)),
            DateOnly date => ("date", date.ToString("O", CultureInfo.InvariantCulture)),
            _ => throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType,
                "Identity field uses a type that cannot be represented in a binary content locator.")
        };

        return new BinaryLocatorPart(name, type, text);
    }

    private static string EncodeBinaryLocator(RelationalBinaryLocator locator)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(locator);
        return $"relational-binary:v1:{Base64UrlEncode(json)}";
    }

    private static bool TryDecodeBinaryLocator(string reference, out RelationalBinaryLocator locator)
    {
        const string prefix = "relational-binary:v1:";
        locator = default!;
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var bytes = Base64UrlDecode(reference[prefix.Length..]);
            locator = JsonSerializer.Deserialize<RelationalBinaryLocator>(bytes)!;
            return locator is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private static object ParseLocatorValue(BinaryLocatorPart part) => part.Type switch
    {
        "string" => part.Value,
        "char" when part.Value.Length == 1 => part.Value[0],
        "boolean" => bool.Parse(part.Value),
        "byte" => byte.Parse(part.Value, CultureInfo.InvariantCulture),
        "sbyte" => sbyte.Parse(part.Value, CultureInfo.InvariantCulture),
        "int16" => short.Parse(part.Value, CultureInfo.InvariantCulture),
        "uint16" => ushort.Parse(part.Value, CultureInfo.InvariantCulture),
        "int32" => int.Parse(part.Value, CultureInfo.InvariantCulture),
        "uint32" => uint.Parse(part.Value, CultureInfo.InvariantCulture),
        "int64" => long.Parse(part.Value, CultureInfo.InvariantCulture),
        "uint64" => ulong.Parse(part.Value, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(part.Value, CultureInfo.InvariantCulture),
        "single" => float.Parse(part.Value, CultureInfo.InvariantCulture),
        "double" => double.Parse(part.Value, CultureInfo.InvariantCulture),
        "guid" => Guid.Parse(part.Value),
        "datetimeoffset" => DateTimeOffset.Parse(part.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "instant" => DateTime.Parse(part.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
        "localdatetime" => DateTime.SpecifyKind(DateTime.Parse(part.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Unspecified),
        "date" => DateOnly.Parse(part.Value, CultureInfo.InvariantCulture),
        _ => throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Binary reference identity is invalid.")
    };

    private static bool IsBinaryType(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type.Contains("binary", StringComparison.Ordinal) || type is "bytea" or "image";
    }

    private sealed record RelationalBinaryLocator(
        string Schema,
        string Table,
        string Column,
        BinaryLocatorPart[] Identity);

    private sealed record BinaryLocatorPart(string Name, string Type, string Value);

    private static string FormatTable(string schema, string table) => $"{schema}.{table}";

    private static bool IsValidIdentifier(string identifier) => IdentifierPattern.IsMatch(identifier);

    protected virtual bool IsBinaryField(DbDataReader reader, int ordinal) => reader.GetFieldType(ordinal) == typeof(byte[]);
    protected virtual string ParameterName(string name) => $"@{name}";
    protected virtual string ParameterPlaceholder(string name) => $"@{name}";

    private void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = ParameterName(name);
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static SourceInspection Invalid(ConnectorContext context, string code, string message, string? location = null) =>
        new(SourceInspectionStatus.Invalid, physicalObject: location, issues:
        [
            new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey, location)
        ]);

    private static SourceInspection Failed(ConnectorContext context, string code, string message) =>
        new(SourceInspectionStatus.Failed, issues:
        [
            new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey)
        ]);
}

internal sealed class OwnedRelationalStream : Stream
{
    private readonly Stream _content;
    private DbDataReader? _reader;
    private DbCommand? _command;
    private DbConnection? _connection;

    public OwnedRelationalStream(Stream content, DbDataReader reader, DbCommand command, DbConnection connection)
    {
        _content = content;
        _reader = reader;
        _command = command;
        _connection = connection;
    }

    public override bool CanRead => _content.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _content.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _content.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
            _reader?.Dispose();
            _command?.Dispose();
            _connection?.Dispose();
            _reader = null;
            _command = null;
            _connection = null;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _content.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null) await _reader.DisposeAsync().ConfigureAwait(false);
        if (_command is not null) await _command.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _reader = null;
        _command = null;
        _connection = null;
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

public sealed class ConnectorReadException : Exception
{
    public string Code { get; }

    public ConnectorReadException(string code, string message) : base(message) => Code = code;
}

public static class ArtifactIdentity
{
    public static string Create(string system, string endpoint, string artifactType, string physicalIdentity)
    {
        var fields = new[] { system, endpoint, artifactType, physicalIdentity };
        var builder = new StringBuilder("proofshift-artifact-id-v1\n");
        foreach (var field in fields)
        {
            builder.Append(Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(field);
        }

        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant()}";
    }
}
