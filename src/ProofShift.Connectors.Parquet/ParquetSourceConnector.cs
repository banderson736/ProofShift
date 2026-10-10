using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Parquet;
using Parquet.Schema;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Domain;

namespace ProofShift.Connectors.Parquet;

/// <summary>
/// Apache Parquet record reader that is independent of the transport (local files, S3, Azure Blob, SFTP).
/// Flat schemas only; nested, repeated and legacy INT96 / time-of-day columns fail closed. One artifact per row.
/// </summary>
public sealed class ParquetSourceConnector : ICheckpointSourceConnector, ISourceBinaryContentResolver,
    IPhysicalDiscoveryConnector, IConnectorConfigurationSchemaProvider
{
    public const string SelectorKind = "parquet";
    private const string CellPrefix = "parquet-cell-v1:";
    private readonly IReadOnlyDictionary<string, IRemoteObjectStoreFactory> _transports;

    public ParquetSourceConnector(IEnumerable<IRemoteObjectStoreFactory> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        _transports = transports.ToDictionary(factory => factory.Transport, StringComparer.Ordinal);
    }

    public ConnectorId Id { get; } = new("parquet");
    public string Version => "0.10.0";
    public SourceConsistencyGuarantee CheckpointConsistency => SourceConsistencyGuarantee.Observed;

    public ConnectorCapabilityDescriptor Capabilities => new(Id.Value, Version, true, true, true, true, false, true, true,
        "Observed", false, string.Join('|', _transports.Keys.Order(StringComparer.Ordinal)), true,
        _transports.Values.All(factory => factory.Capabilities.RangeRead), _transports.Values.Any(factory => factory.Capabilities.VersionPinning), true);

    public ConnectorConfigurationSchema ConfigurationSchema
    {
        get
        {
            var endpoint = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["transport"] = "enum:" + string.Join(',', _transports.Keys.Order(StringComparer.Ordinal)),
                ["rowGroupBlockBytes"] = "positive-integer-string"
            };
            foreach (var factory in _transports.Values.OrderBy(factory => factory.Transport, StringComparer.Ordinal))
            {
                foreach (var pair in factory.EndpointProperties)
                {
                    endpoint[pair.Key] = endpoint.TryGetValue(pair.Key, out var existing) && existing.StartsWith("enum:", StringComparison.Ordinal) && pair.Value.StartsWith("enum:", StringComparison.Ordinal)
                        ? "enum:" + string.Join(',', existing[5..].Split(',').Concat(pair.Value[5..].Split(',')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        : pair.Value;
                }
            }

            return new ConnectorConfigurationSchema(Id.Value, SelectorKind, endpoint, ["transport"],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "string", ["pattern"] = "string", ["columns"] = "string" },
                new Dictionary<string, string>(StringComparer.Ordinal), [["path"], ["pattern"]]);
        }
    }

    public async Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken)
    {
        try
        {
            ValidateSelector(selector);
            await using var store = CreateStore(context);
            long files = 0, bytes = 0, rows = 0;
            var columns = new List<SourceColumn>();
            await foreach (var info in MatchAsync(store, selector, cancellationToken).ConfigureAwait(false))
            {
                files++;
                bytes = checked(bytes + info.Length);
                await using var reader = await OpenReaderAsync(store, info, cancellationToken).ConfigureAwait(false);
                var fields = Fields(reader, selector);
                rows = checked(rows + reader.RowGroups.Sum(group => group.RowCount));
                if (columns.Count == 0)
                    columns.AddRange(fields.Select((field, index) => new SourceColumn(field.Name, NativeType(field), field.IsNullable, index + 1)));
            }

            return files == 0
                ? Invalid(context, ConnectorIssueCodes.SourceObjectNotFound, "Parquet selector did not match any objects.")
                : new SourceInspection(SourceInspectionStatus.Valid, columns, identityFields: selector.IdentityFields, estimatedRecords: rows,
                    physicalObject: selector.Properties.GetValueOrDefault("path") ?? selector.Properties["pattern"], files: files, bytes: bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (ConnectorConfigurationException exception) { return Invalid(context, exception.Code, exception.Message); }
        catch (ConnectorReadException exception) { return Invalid(context, exception.Code, exception.Message); }
        catch (RemoteStoreException exception) { return Invalid(context, exception.Code, exception.Message); }
        catch
        {
            return new SourceInspection(SourceInspectionStatus.Failed, issues:
                [new ConnectorIssue(ConnectorIssueCodes.SourceReadFailed, ConnectorIssueSeverity.Error, "Parquet inspection failed.", context.NodeKey)]);
        }
    }

    public IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
        CancellationToken cancellationToken) => Translate(ReadCoreAsync(context, selector, options, cancellationToken), cancellationToken);

    public async IAsyncEnumerable<RecordEnvelope> ReadForCheckpointAsync(ConnectorContext context, ArtifactSelector selector,
        ReadOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateSelector(selector);
        var pattern = PatternOf(selector);
        var before = await InventoryAsync(context, pattern, cancellationToken).ConfigureAwait(false);
        await foreach (var record in ReadAsync(context, selector, options, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return record;
        if (before != await InventoryAsync(context, pattern, cancellationToken).ConfigureAwait(false))
            throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture, "Matched Parquet objects changed while the checkpoint was being captured.");
    }

    public async Task<PhysicalDiscoveryArtifact> DiscoverAsync(ConnectorContext context,
        IReadOnlyCollection<ArtifactSelector> selectors, CancellationToken cancellationToken)
    {
        try
        {
            await using var store = CreateStore(context);
            var targets = selectors.Count > 0 ? selectors : [new ArtifactSelector(SelectorKind, [new("pattern", "**/*.parquet")])];
            var objects = new List<PhysicalObject>();
            foreach (var selector in targets)
            {
                ValidateSelector(selector, requireIdentity: false);
                await foreach (var info in MatchAsync(store, selector, cancellationToken).ConfigureAwait(false))
                {
                    await using var reader = await OpenReaderAsync(store, info, cancellationToken).ConfigureAwait(false);
                    var fields = Fields(reader, selector, strict: false);
                    objects.Add(new PhysicalObject("", info.Key, SelectorKind,
                        fields.Select((field, index) => new PhysicalField(field.Name, NativeType(field), field.IsNullable, index + 1)).ToArray(),
                        selector.IdentityFields.Count == 0 ? [] : [new PhysicalKey("configured-identity", true, selector.IdentityFields.ToArray())], [],
                        reader.RowGroups.Sum(group => group.RowCount), info.Length,
                        new Dictionary<string, string> { ["path"] = info.Key }));
                }
            }

            return PhysicalDiscovery.Create(context, Id.Value, Version, objects.DistinctBy(item => item.Name));
        }
        catch (RemoteStoreException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }
    }

    public async ValueTask<Stream> OpenBinaryReadAsync(ConnectorContext context, ArtifactSelector selector, ArtifactReference artifact,
        BinaryReferenceValue binaryReference, CancellationToken cancellationToken)
    {
        if (!TryDecodeCell(binaryReference.Reference, out var cell) || artifact.SystemId.Value != context.SystemKey || artifact.EndpointId.Value != context.EndpointKey)
            throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Binary reference does not belong to the supplied source artifact.");
        try
        {
            await using var store = CreateStore(context);
            var info = await store.StatAsync(cell.Key, cell.Version, cancellationToken).ConfigureAwait(false);
            await using var reader = await OpenReaderAsync(store, info, cancellationToken).ConfigureAwait(false);
            var field = reader.Schema.DataFields.SingleOrDefault(candidate => candidate.Name == cell.Column)
                ?? throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Referenced Parquet column no longer exists.");
            using var group = reader.OpenRowGroupReader(cell.RowGroup);
            var values = await ReadColumnAsync(group, field, cancellationToken).ConfigureAwait(false);
            if (cell.Row >= values.Length || values[cell.Row] is not byte[] bytes)
                throw new ConnectorReadException(ConnectorIssueCodes.SourceObjectNotFound, "Referenced Parquet cell is unavailable.");
            if (bytes.LongLength != binaryReference.ContentLength)
                throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture, "Referenced Parquet cell no longer matches its recorded length.");
            return new MemoryStream(bytes, writable: false);
        }
        catch (RemoteStoreException exception) { throw new ConnectorReadException(exception.Code, exception.Message); }
        catch (Exception exception) when (IsCorruption(exception)) { throw Corrupt(); }
    }

    private async IAsyncEnumerable<RecordEnvelope> ReadCoreAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Partition.Count != 0)
            throw new ConnectorReadException(ConnectorIssueCodes.PartitioningUnsupported, "Partitioned Parquet observation is unsupported.");
        ValidateSelector(selector);
        await using var store = CreateStore(context);
        await using var identities = await DiskBackedIdentityIndex.CreateAsync(cancellationToken).ConfigureAwait(false);
        long recordIndex = 0;
        await foreach (var listed in MatchAsync(store, selector, cancellationToken).ConfigureAwait(false))
        {
            var info = await RemoteRetryPolicy.Default.ExecuteAsync(token => store.StatAsync(listed.Key, listed.VersionId, token), cancellationToken).ConfigureAwait(false);
            var hash = await HashObjectAsync(store, info, cancellationToken).ConfigureAwait(false);
            await using var reader = await OpenReaderAsync(store, info, cancellationToken).ConfigureAwait(false);
            var fields = Fields(reader, selector);
            for (var groupIndex = 0; groupIndex < reader.RowGroupCount; groupIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var columns = new object?[fields.Count][];
                try
                {
                    using var group = reader.OpenRowGroupReader(groupIndex);
                    for (var column = 0; column < fields.Count; column++)
                        columns[column] = await ReadColumnAsync(group, fields[column], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsCorruption(exception)) { throw Corrupt(); }

                var rows = columns.Length == 0 ? 0 : columns[0].Length;
                for (var row = 0; row < rows; row++)
                {
                    recordIndex++;
                    var values = new Dictionary<string, ValueNode>(StringComparer.Ordinal);
                    for (var column = 0; column < fields.Count; column++)
                        values[fields[column].Name] = ToValue(fields[column], columns[column][row], info, groupIndex, row);
                    var identityParts = new List<string>();
                    foreach (var field in selector.IdentityFields)
                    {
                        if (!values.TryGetValue(field, out var value) || value is NullValue)
                            throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity, $"Record {recordIndex}: configured identity is missing or NULL.");
                        var encoded = JsonSerializer.Serialize(IdentityDocument(value));
                        identityParts.Add($"{field.Length}:{field}={encoded.Length}:{encoded}");
                    }

                    var identity = string.Join('|', identityParts);
                    await identities.AddAsync(identity, cancellationToken).ConfigureAwait(false);
                    var metadata = new List<KeyValuePair<string, string>>
                    {
                        new("observationKind", "read"), new("transport", store.Provider), new("scope", store.ScopeIdentity),
                        new("object", info.Key), new("rowGroup", groupIndex.ToString(CultureInfo.InvariantCulture)),
                        new("rowInGroup", row.ToString(CultureInfo.InvariantCulture))
                    };
                    if (info.ETag is not null) metadata.Add(new("providerETag", info.ETag));
                    if (info.VersionId is not null) metadata.Add(new("providerVersionId", info.VersionId));
                    var artifact = new ArtifactReference(new ArtifactId(StableArtifactIdentity.CreateArtifactId(context.SystemKey, context.EndpointKey,
                        selector.Kind, identity)), new SystemId(context.SystemKey), new StorageEndpointId(context.EndpointKey), selector.Kind, identity);
                    yield return new RecordEnvelope(artifact, context.SemanticType, values, new ProvenanceMetadata(Id,
                        new StorageEndpointId(context.EndpointKey), $"{info.Key}#{recordIndex.ToString(CultureInfo.InvariantCulture)}",
                        DateTimeOffset.UtcNow, hash, metadata));
                }
            }

            var after = await RemoteRetryPolicy.Default.ExecuteAsync(token => store.StatAsync(info.Key, info.VersionId, token), cancellationToken).ConfigureAwait(false);
            if (!info.SameState(after))
                throw new ConnectorReadException(ConnectorIssueCodes.ArtifactChangedDuringCapture, "Parquet object changed while it was being read.");
        }
    }

    private static async Task<string> HashObjectAsync(IRemoteObjectStore store, RemoteObjectInfo info, CancellationToken cancellationToken)
    {
        return await RemoteRetryPolicy.Default.ExecuteAsync(async token =>
        {
            await using var stream = await store.OpenReadAsync(info, 0, null, token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }
            }
            catch (IOException)
            {
                throw new RemoteStoreException(RemoteFailureKind.Transient, "Remote object read was interrupted.");
            }

            if (total != info.Length) throw new RemoteStoreException(RemoteFailureKind.Changed, "Remote object length changed while it was being read.");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ParquetReader> OpenReaderAsync(IRemoteObjectStore store, RemoteObjectInfo info, CancellationToken cancellationToken)
    {
        Stream stream = store.Capabilities.RangeRead
            ? new SeekableRemoteStream(store, info, 1024 * 1024)
            : throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "Parquet requires a transport with ranged reads.");
        try
        {
            return await ParquetReader.CreateAsync(stream, new ParquetOptions { UseDateOnlyTypeForDates = true, TreatByteArrayAsString = false },
                leaveStreamOpen: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCorruption(exception))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw Corrupt();
        }
    }

    private static bool IsCorruption(Exception exception) =>
        exception is not (OperationCanceledException or RemoteStoreException or ConnectorReadException);

    private static ConnectorReadException Corrupt() => new(ConnectorIssueCodes.CorruptColumnarFile,
        "Parquet object is corrupt, truncated, or uses an unsupported encoding; no record values were emitted.");

    private static IReadOnlyList<DataField> Fields(ParquetReader reader, ArtifactSelector selector, bool strict = true)
    {
        var all = reader.Schema.DataFields;
        if (strict)
            foreach (var field in all)
                _ = Classify(field);
        if (reader.Schema.Fields.Any(field => field is not DataField))
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                "Nested Parquet structures (struct, list, map) are not supported; configure a flat projection upstream.");
        if (!selector.Properties.TryGetValue("columns", out var configured) || string.IsNullOrWhiteSpace(configured)) return all;
        var wanted = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var selected = new List<DataField>();
        foreach (var name in wanted)
            selected.Add(all.SingleOrDefault(field => field.Name == name) ??
                throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound, $"Configured column '{name}' does not exist in the Parquet schema."));
        foreach (var identity in selector.IdentityFields)
            if (!selected.Any(field => field.Name == identity))
                throw new ConnectorReadException(ConnectorIssueCodes.IdentityFieldNotFound, $"Identity column '{identity}' is not among the selected columns.");
        return selected;
    }

    private enum Kind { Boolean, Integer, UnsignedLong, Decimal, Float, Double, String, Binary, Date, Instant, LocalDateTime, Guid }

    private static Kind Classify(DataField field)
    {
        if (field.IsArray || field.MaxRepetitionLevel > 0)
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema, $"Column '{field.Name}' is repeated; repeated and nested columns are not supported.");
        if (field is DecimalDataField decimalField)
        {
            if (decimalField.Precision > 28)
                throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                    $"Column '{field.Name}' decimal precision exceeds the 28 digits of the exact model.");
            return Kind.Decimal;
        }

        if (field is TimeDataField)
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                $"Column '{field.Name}' uses a time-of-day Parquet logical type, which is not supported.");

        if (field is DateTimeDataField dateTime)
        {
            if (dateTime.DateTimeFormat == DateTimeFormat.Impala)
                throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                    $"Column '{field.Name}' is a legacy INT96 timestamp with ambiguous time zone semantics.");
            if (dateTime.DateTimeFormat == DateTimeFormat.Date) return Kind.Date;
            if (dateTime.DateTimeFormat is not (DateTimeFormat.DateAndTime or DateTimeFormat.DateAndTimeMicros or DateTimeFormat.Timestamp))
                throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                    $"Column '{field.Name}' uses a time-of-day or otherwise unsupported Parquet timestamp format.");
            if (dateTime.Unit == DateTimeTimeUnit.Nanos)
                throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
                    $"Column '{field.Name}' uses nanosecond precision, which exceeds the supported exact model.");
            return dateTime.IsAdjustedToUTC ? Kind.Instant : Kind.LocalDateTime;
        }

        var type = field.ClrType;
        if (type == typeof(bool)) return Kind.Boolean;
        if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long)) return Kind.Integer;
        if (type == typeof(ulong)) return Kind.UnsignedLong;
        if (type == typeof(float)) return Kind.Float;
        if (type == typeof(double)) return Kind.Double;
        if (type == typeof(string) || type == typeof(ReadOnlyMemory<char>)) return Kind.String;
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>)) return Kind.Binary;
        if (type == typeof(DateOnly)) return Kind.Date;
        if (type == typeof(DateTime)) return Kind.LocalDateTime;
        if (type == typeof(Guid)) return Kind.Guid;
        throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema,
            $"Column '{field.Name}' uses an unsupported Parquet type ({type.Name}).");
    }

    private static string NativeType(DataField field)
    {
        var suffix = field is DecimalDataField decimalField ? $"({decimalField.Precision},{decimalField.Scale})"
            : field is DateTimeDataField dateTime ? $"({dateTime.DateTimeFormat},{(dateTime.IsAdjustedToUTC ? "utc" : "local")},{dateTime.Unit})" : "";
        return (field.IsArray ? "list<" : "") + field.ClrType.Name + suffix + (field.IsArray ? ">" : "");
    }

    private static async Task<object?[]> ReadColumnAsync(ParquetRowGroupReader group, DataField field, CancellationToken cancellationToken)
    {
        var rows = checked((int)group.RowCount);
        var type = field.ClrType;
        if (type == typeof(string) || type == typeof(ReadOnlyMemory<char>))
        {
            var data = new string?[rows];
            await group.ReadAsync(field, data.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            return data.Select(value => (object?)value).ToArray();
        }

        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>))
        {
            var data = new byte[]?[rows];
            await group.ReadAsync(field, data.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            return data.Select(value => (object?)value).ToArray();
        }

        if (type == typeof(bool)) return await Typed<bool>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(sbyte)) return await Typed<sbyte>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(byte)) return await Typed<byte>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(short)) return await Typed<short>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(ushort)) return await Typed<ushort>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(int)) return await Typed<int>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(uint)) return await Typed<uint>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(long)) return await Typed<long>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(ulong)) return await Typed<ulong>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(float)) return await Typed<float>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(double)) return await Typed<double>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(decimal)) return await Typed<decimal>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(DateOnly)) return await Typed<DateOnly>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(DateTime)) return await Typed<DateTime>(group, field, rows, cancellationToken).ConfigureAwait(false);
        if (type == typeof(Guid)) return await Typed<Guid>(group, field, rows, cancellationToken).ConfigureAwait(false);
        throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedColumnarSchema, $"Column '{field.Name}' uses an unsupported Parquet type.");
    }

    private static async Task<object?[]> Typed<T>(ParquetRowGroupReader group, DataField field, int rows, CancellationToken cancellationToken) where T : struct
    {
        if (field.IsNullable)
        {
            var nullable = new T?[rows];
            await group.ReadAsync(field, nullable.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            return nullable.Select(value => (object?)value).ToArray();
        }

        var data = new T[rows];
        await group.ReadAsync(field, data.AsMemory(), null, cancellationToken).ConfigureAwait(false);
        return data.Select(value => (object?)value).ToArray();
    }

    private static ValueNode ToValue(DataField field, object? raw, RemoteObjectInfo info, int rowGroup, int row)
    {
        if (raw is null) return new NullValue();
        return Classify(field) switch
        {
            Kind.Boolean => new BooleanValue((bool)raw),
            Kind.Integer => new IntegerValue(Convert.ToInt64(raw, CultureInfo.InvariantCulture)),
            Kind.UnsignedLong => (ulong)raw <= long.MaxValue ? new IntegerValue((long)(ulong)raw)
                : throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedPhysicalType, $"Column '{field.Name}' holds an unsigned value beyond the exact integer range."),
            Kind.Decimal => new DecimalValue((decimal)raw),
            Kind.Float => new StringValue(((float)raw).ToString("R", CultureInfo.InvariantCulture)),
            Kind.Double => new StringValue(((double)raw).ToString("R", CultureInfo.InvariantCulture)),
            Kind.String => new StringValue((string)raw),
            Kind.Binary => BinaryCell((byte[])raw, info, rowGroup, row, field.Name),
            Kind.Date => new DateValue(raw is DateOnly date ? date : DateOnly.FromDateTime((DateTime)raw)),
            Kind.Instant => new InstantValue(new DateTimeOffset(DateTime.SpecifyKind((DateTime)raw, DateTimeKind.Utc))),
            Kind.LocalDateTime => new LocalDateTimeValue(DateTime.SpecifyKind((DateTime)raw, DateTimeKind.Unspecified)),
            _ => new StringValue(((Guid)raw).ToString("D", CultureInfo.InvariantCulture))
        };
    }

    private static BinaryReferenceValue BinaryCell(byte[] bytes, RemoteObjectInfo info, int rowGroup, int row, string column) =>
        new(EncodeCell(info, rowGroup, row, column), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private sealed record CellReference(string Key, string? Version, int RowGroup, int Row, string Column);

    private static string EncodeCell(RemoteObjectInfo info, int rowGroup, int row, string column)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CellReference(info.Key, info.VersionId, rowGroup, row, column));
        return CellPrefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeCell(string reference, out CellReference cell)
    {
        cell = null!;
        if (!reference.StartsWith(CellPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var encoded = reference[CellPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            cell = JsonSerializer.Deserialize<CellReference>(Convert.FromBase64String(encoded))!;
            RemoteKeyRules.ValidateRelativeKey(cell.Key);
            return cell.RowGroup >= 0 && cell.Row >= 0 && !string.IsNullOrEmpty(cell.Column);
        }
        catch
        {
            return false;
        }
    }

    private static object? IdentityDocument(ValueNode value) => value switch
    {
        StringValue text => text.Value,
        IntegerValue integer => integer.Value,
        DecimalValue number => number.Value,
        BooleanValue boolean => boolean.Value,
        DateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LocalDateTimeValue local => local.Value.ToString("O", CultureInfo.InvariantCulture),
        InstantValue instant => instant.Value.ToString("O", CultureInfo.InvariantCulture),
        _ => throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity, "Identity must be a declared primitive value.")
    };

    private IRemoteObjectStore CreateStore(ConnectorContext context)
    {
        var transport = RemoteSettings.Optional(context, "transport");
        return _transports.TryGetValue(transport, out var factory)
            ? factory.Create(context)
            : throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration,
                $"Parquet endpoint property 'transport' must be one of: {string.Join(", ", _transports.Keys.Order(StringComparer.Ordinal))}.");
    }

    private static void ValidateSelector(ArtifactSelector selector, bool requireIdentity = true)
    {
        if (!string.Equals(selector.Kind, SelectorKind, StringComparison.OrdinalIgnoreCase))
            throw new ConnectorReadException(ConnectorIssueCodes.UnsupportedSelector, "Parquet connector requires a parquet selector.");
        if (selector.Properties.ContainsKey("path") == selector.Properties.ContainsKey("pattern"))
            throw new ConnectorReadException(ConnectorIssueCodes.MissingConfiguration, "Exactly one of selector properties 'path' or 'pattern' is required.");
        if (!RemoteObjectSourceConnector.TryGetPattern(selector.Properties.ContainsKey("path")
                ? new ArtifactSelector(SelectorKind, [new("pattern", selector.Properties["path"])]) : selector, out _))
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier, "Parquet path or pattern must remain relative to the endpoint scope.");
        if (selector.Properties.TryGetValue("path", out var literal) && literal.IndexOfAny(['*', '?']) >= 0)
            throw new ConnectorReadException(ConnectorIssueCodes.InvalidPhysicalIdentifier, "Selector property 'path' is a literal object key; use 'pattern' for wildcards.");
        if (requireIdentity && selector.IdentityFields.Count == 0)
            throw new ConnectorReadException(ConnectorIssueCodes.NonDeterministicIdentity, "Selector kind and explicit semantic identity are required.");
    }

    private static string PatternOf(ArtifactSelector selector) =>
        selector.Properties.TryGetValue("path", out var path) ? Regex(path) : selector.Properties["pattern"];

    private static string Regex(string literalPath) => literalPath.Replace("*", "[*]", StringComparison.Ordinal);

    private static IAsyncEnumerable<RemoteObjectInfo> MatchAsync(IRemoteObjectStore store, ArtifactSelector selector, CancellationToken cancellationToken) =>
        RemoteObjectSourceConnector.MatchAsync(store, selector.Properties.TryGetValue("path", out var path) ? EscapeLiteral(path) : selector.Properties["pattern"], cancellationToken);

    private static string EscapeLiteral(string path) => path;

    private async Task<string> InventoryAsync(ConnectorContext context, string pattern, CancellationToken cancellationToken)
    {
        try
        {
            await using var store = CreateStore(context);
            return await RemoteObjectSourceConnector.InventoryAsync(store, pattern, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteStoreException exception)
        {
            throw new ConnectorReadException(exception.Code, exception.Message);
        }
    }

    private static async IAsyncEnumerable<T> Translate<T>(IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool has;
            try
            {
                has = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (RemoteStoreException exception) { throw new ConnectorReadException(exception.Code, exception.Message); }
            catch (ConnectorConfigurationException exception) { throw new ConnectorReadException(exception.Code, exception.Message); }
            catch (Exception exception) when (exception is not (OperationCanceledException or ConnectorReadException))
            {
                throw new ConnectorReadException(ConnectorIssueCodes.SourceReadFailed, "Parquet read failed.");
            }

            if (!has) yield break;
            yield return enumerator.Current;
        }
    }

    private static SourceInspection Invalid(ConnectorContext context, string code, string message) =>
        new(SourceInspectionStatus.Invalid, issues: [new ConnectorIssue(code, ConnectorIssueSeverity.Error, message, context.NodeKey)]);
}
