using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Snapshots;

internal static class SnapshotRecordCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static byte[] Encode(RecordEnvelope record, CheckpointId checkpointId, string sourceNodeKey, string recordFingerprint)
    {
        var provenance = record.Provenance.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        provenance["snapshotCheckpointId"] = checkpointId.Value.ToString("N", CultureInfo.InvariantCulture);
        provenance["snapshotSourceNode"] = sourceNodeKey;
        provenance["snapshotRecordFingerprint"] = recordFingerprint;
        return JsonSerializer.SerializeToUtf8Bytes(ToDocument(record, recordFingerprint, provenance), JsonOptions);
    }

    public static (RecordEnvelope Record, string Fingerprint) Decode(ReadOnlySpan<byte> bytes)
    {
        RecordDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RecordDocument>(bytes, JsonOptions)
                ?? throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Materialized record is empty.");
        }
        catch (SnapshotStoreException)
        {
            throw;
        }
        catch
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Materialized record could not be decoded.");
        }

        if (!string.Equals(document.FormatVersion, SnapshotFingerprints.MaterializedFormatVersion, StringComparison.Ordinal))
        {
            throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Materialized snapshot record format is unsupported.");
        }

        var artifact = new ArtifactReference(
            new ArtifactId(document.Artifact.Id),
            new SystemId(document.Artifact.SystemId),
            new StorageEndpointId(document.Artifact.EndpointId),
            document.Artifact.ArtifactType,
            document.Artifact.Identity);
        var values = document.Values.Select(pair => new KeyValuePair<string, ValueNode>(pair.Key, FromValue(pair.Value)));
        var relationships = document.Relationships.Select(relationship => new RelationshipReference(
            relationship.Type,
            new ArtifactReference(new ArtifactId(relationship.Target.Id), new SystemId(relationship.Target.SystemId),
                new StorageEndpointId(relationship.Target.EndpointId), relationship.Target.ArtifactType, relationship.Target.Identity),
            relationship.Direction));
        var provenance = new ProvenanceMetadata(
            new ConnectorId(document.Provenance.Connector),
            new StorageEndpointId(document.Provenance.Endpoint),
            document.Provenance.Location,
            DateTimeOffset.ParseExact(document.Provenance.ObservedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            document.Provenance.SourceHash,
            document.Provenance.Metadata);
        var temporal = document.Temporal is null
            ? null
            : new TemporalMetadata(
                ParseNullableDateTimeOffset(document.Temporal.EffectiveFrom),
                ParseNullableDateTimeOffset(document.Temporal.EffectiveTo),
                ParseNullableDateTimeOffset(document.Temporal.RecordedAt),
                document.Temporal.Version);
        return (new RecordEnvelope(artifact, document.SemanticType, values, provenance, relationships, temporal), document.RecordFingerprint);
    }

    private static RecordDocument ToDocument(
        RecordEnvelope record,
        string recordFingerprint,
        IDictionary<string, string> provenanceMetadata) =>
        new(
            SnapshotFingerprints.MaterializedFormatVersion,
            recordFingerprint,
            new ArtifactDocument(record.Artifact.Id.Value, record.Artifact.SystemId.Value, record.Artifact.EndpointId.Value,
                record.Artifact.ArtifactType, record.Artifact.Identity),
            record.SemanticType,
            new SortedDictionary<string, ValueDocument>(record.Values.ToDictionary(pair => pair.Key, pair => ToValue(pair.Value), StringComparer.Ordinal), StringComparer.Ordinal),
            record.Relationships.Select(relationship => new RelationshipDocument(
                relationship.Type,
                new ArtifactDocument(relationship.Target.Id.Value, relationship.Target.SystemId.Value,
                    relationship.Target.EndpointId.Value, relationship.Target.ArtifactType, relationship.Target.Identity),
                relationship.Direction)).ToArray(),
            record.Temporal is null ? null : new TemporalDocument(
                Format(record.Temporal.EffectiveFrom), Format(record.Temporal.EffectiveTo), Format(record.Temporal.RecordedAt), record.Temporal.Version),
            new ProvenanceDocument(record.Provenance.Connector.Value, record.Provenance.Endpoint.Value,
                record.Provenance.Location, record.Provenance.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                record.Provenance.SourceHash,
                new SortedDictionary<string, string>(provenanceMetadata, StringComparer.Ordinal)));

    private static ValueDocument ToValue(ValueNode value) => value switch
    {
        NullValue => new ValueDocument("null"),
        StringValue text => new ValueDocument("string", Text: text.Value),
        IntegerValue integer => new ValueDocument("integer", Integer: integer.Value),
        DecimalValue number => new ValueDocument("decimal", Text: number.Value.ToString("G29", CultureInfo.InvariantCulture)),
        BooleanValue boolean => new ValueDocument("boolean", Boolean: boolean.Value),
        DateValue date => new ValueDocument("date", Text: date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        InstantValue instant => new ValueDocument("instant", Text: instant.Value.ToString("O", CultureInfo.InvariantCulture)),
        OffsetDateTimeValue offset => new ValueDocument("offsetDateTime", Text: offset.Value.ToString("O", CultureInfo.InvariantCulture)),
        LocalDateTimeValue local => new ValueDocument("localDateTime", Text: local.Value.ToString("O", CultureInfo.InvariantCulture)),
        BinaryReferenceValue binary => new ValueDocument("binaryReference", Reference: binary.Reference,
            ContentLength: binary.ContentLength, Sha256: binary.Sha256),
        CollectionValue collection => new ValueDocument("collection", Items: collection.Values.Select(ToValue).ToArray()),
        ObjectValue obj => new ValueDocument("object", Properties: new SortedDictionary<string, ValueDocument>(
            obj.Values.ToDictionary(pair => pair.Key, pair => ToValue(pair.Value), StringComparer.Ordinal), StringComparer.Ordinal)),
        _ => throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Source contains an unsupported ValueNode type.")
    };

    private static ValueNode FromValue(ValueDocument value) => value.Kind switch
    {
        "null" => new NullValue(),
        "string" => new StringValue(value.Text ?? string.Empty),
        "integer" => new IntegerValue(value.Integer ?? throw InvalidValue()),
        "decimal" => new DecimalValue(decimal.Parse(value.Text ?? throw InvalidValue(), NumberStyles.Number, CultureInfo.InvariantCulture)),
        "boolean" => new BooleanValue(value.Boolean ?? throw InvalidValue()),
        "date" => new DateValue(DateOnly.ParseExact(value.Text ?? throw InvalidValue(), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
        "instant" => new InstantValue(DateTimeOffset.ParseExact(value.Text ?? throw InvalidValue(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None)),
        "offsetDateTime" => new OffsetDateTimeValue(DateTimeOffset.ParseExact(value.Text ?? throw InvalidValue(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None)),
        "localDateTime" => new LocalDateTimeValue(DateTime.SpecifyKind(DateTime.ParseExact(value.Text ?? throw InvalidValue(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified)),
        "binaryReference" => new BinaryReferenceValue(value.Reference ?? throw InvalidValue(), value.ContentLength ?? throw InvalidValue(), value.Sha256 ?? throw InvalidValue()),
        "collection" => new CollectionValue((value.Items ?? []).Select(FromValue)),
        "object" => new ObjectValue((value.Properties ?? new SortedDictionary<string, ValueDocument>(StringComparer.Ordinal))
            .Select(pair => new KeyValuePair<string, ValueNode>(pair.Key, FromValue(pair.Value)))),
        _ => throw new SnapshotStoreException(SnapshotIssueCodes.UnsupportedFormat, "Materialized snapshot contains an unknown value kind.")
    };

    private static DateTimeOffset? ParseNullableDateTimeOffset(string? value) =>
        value is null ? null : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static string? Format(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static SnapshotStoreException InvalidValue() =>
        new(SnapshotIssueCodes.UnsupportedFormat, "Materialized snapshot value is incomplete or invalid.");

    private sealed record ArtifactDocument(string Id, string SystemId, string EndpointId, string ArtifactType, string Identity);
    private sealed record RelationshipDocument(string Type, ArtifactDocument Target, RelationshipDirection Direction);
    private sealed record TemporalDocument(string? EffectiveFrom, string? EffectiveTo, string? RecordedAt, long? Version);
    private sealed record ProvenanceDocument(string Connector, string Endpoint, string Location, string ObservedAt,
        string? SourceHash, SortedDictionary<string, string> Metadata);
    private sealed record RecordDocument(string FormatVersion, string RecordFingerprint, ArtifactDocument Artifact,
        string SemanticType, SortedDictionary<string, ValueDocument> Values, RelationshipDocument[] Relationships,
        TemporalDocument? Temporal, ProvenanceDocument Provenance);
    private sealed record ValueDocument(string Kind, string? Text = null, long? Integer = null, bool? Boolean = null,
        string? Reference = null, long? ContentLength = null, string? Sha256 = null,
        ValueDocument[]? Items = null, SortedDictionary<string, ValueDocument>? Properties = null);
}