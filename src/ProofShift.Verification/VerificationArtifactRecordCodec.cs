using System.Globalization;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Verification;

internal static class VerificationArtifactRecordCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Encode(RecordEnvelope record)
    {
        var document = new RecordDocument(
            record.Artifact.Id.Value, record.Artifact.SystemId.Value, record.Artifact.EndpointId.Value,
            record.Artifact.ArtifactType, record.Artifact.Identity, record.SemanticType,
            record.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ValueEntry(pair.Key, EncodeValue(pair.Value))).ToArray(),
            record.Relationships.Select(relationship => new RelationshipDocument(relationship.Type,
                relationship.Target.Id.Value, relationship.Target.SystemId.Value, relationship.Target.EndpointId.Value,
                relationship.Target.ArtifactType, relationship.Target.Identity, relationship.Direction.ToString())).ToArray(),
            record.Temporal is null ? null : new TemporalDocument(
                Format(record.Temporal.EffectiveFrom), Format(record.Temporal.EffectiveTo),
                Format(record.Temporal.RecordedAt), record.Temporal.Version));
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static VerificationArtifactRecord Decode(string json, string nodeKey, VerificationArtifactRole role,
        string semanticType)
    {
        var document = JsonSerializer.Deserialize<RecordDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Verification workspace record is empty.");
        var artifact = new ArtifactReference(new ArtifactId(document.ArtifactId), new SystemId(document.SystemId),
            new StorageEndpointId(document.EndpointId), document.ArtifactType, document.Identity);
        var values = document.Values.Select(entry => new KeyValuePair<string, ValueNode>(entry.Name, DecodeValue(entry.Value)));
        var relationships = document.Relationships.Select(item => new RelationshipReference(item.Type,
            new ArtifactReference(new ArtifactId(item.TargetId), new SystemId(item.TargetSystemId),
                new StorageEndpointId(item.TargetEndpointId), item.TargetArtifactType, item.TargetIdentity),
            Enum.Parse<RelationshipDirection>(item.Direction, ignoreCase: false)));
        var temporal = document.Temporal is null ? null : new TemporalMetadata(
            ParseDateTimeOffset(document.Temporal.EffectiveFrom), ParseDateTimeOffset(document.Temporal.EffectiveTo),
            ParseDateTimeOffset(document.Temporal.RecordedAt), document.Temporal.Version);
        return new VerificationArtifactRecord(nodeKey, role, semanticType, artifact, values, relationships, temporal);
    }

    private static ValueDocument EncodeValue(ValueNode value) => value switch
    {
        NullValue => new("null"),
        StringValue item => new("string", Text: item.Value),
        IntegerValue item => new("integer", Integer: item.Value),
        DecimalValue item => new("decimal", Text: item.Value.ToString("G29", CultureInfo.InvariantCulture)),
        BooleanValue item => new("boolean", Boolean: item.Value),
        DateValue item => new("date", Text: item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        InstantValue item => new("instant", Text: item.Value.ToString("O", CultureInfo.InvariantCulture)),
        OffsetDateTimeValue item => new("offsetDateTime", Text: item.Value.ToString("O", CultureInfo.InvariantCulture)),
        LocalDateTimeValue item => new("localDateTime", Text: item.Value.ToString("O", CultureInfo.InvariantCulture)),
        BinaryReferenceValue item => new("binaryReference", Reference: item.Reference, ContentLength: item.ContentLength, Sha256: item.Sha256),
        CollectionValue item => new("collection", Items: item.Values.Select(EncodeValue).ToArray()),
        ObjectValue item => new("object", Properties: item.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ValueEntry(pair.Key, EncodeValue(pair.Value))).ToArray()),
        _ => throw new InvalidDataException("Verification workspace encountered an unsupported normalized value.")
    };

    private static ValueNode DecodeValue(ValueDocument value) => value.Kind switch
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
        "collection" => new CollectionValue((value.Items ?? []).Select(DecodeValue)),
        "object" => new ObjectValue((value.Properties ?? []).Select(item => new KeyValuePair<string, ValueNode>(item.Name, DecodeValue(item.Value)))),
        _ => throw new InvalidDataException("Verification workspace record contains an unsupported value kind.")
    };

    private static string? Format(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDateTimeOffset(string? value) => value is null
        ? null
        : DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static InvalidDataException InvalidValue() => new("Verification workspace record contains an incomplete value.");

    private sealed record RecordDocument(string ArtifactId, string SystemId, string EndpointId, string ArtifactType,
        string Identity, string SemanticType, ValueEntry[] Values, RelationshipDocument[] Relationships, TemporalDocument? Temporal);
    private sealed record ValueEntry(string Name, ValueDocument Value);
    private sealed record ValueDocument(string Kind, string? Text = null, long? Integer = null, bool? Boolean = null,
        string? Reference = null, long? ContentLength = null, string? Sha256 = null, ValueDocument[]? Items = null,
        ValueEntry[]? Properties = null);
    private sealed record RelationshipDocument(string Type, string TargetId, string TargetSystemId,
        string TargetEndpointId, string TargetArtifactType, string TargetIdentity, string Direction);
    private sealed record TemporalDocument(string? EffectiveFrom, string? EffectiveTo, string? RecordedAt, long? Version);
}