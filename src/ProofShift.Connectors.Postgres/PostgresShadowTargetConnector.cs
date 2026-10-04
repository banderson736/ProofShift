using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Connectors.Postgres;

public sealed class PostgresShadowTargetConnector : IShadowTargetConnector
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HashSet<string> _preparedSchemas = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedTables = new(StringComparer.Ordinal);
    private readonly object _prepareLock = new();

    public ConnectorId Id { get; } = new("postgres");
    public string Version => "0.1.0";

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
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}