using System.Globalization;
using System.Text;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class GraphTransformationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class GraphTargetIdentity
{
    public static string Create(RecordEnvelope record, ArtifactSelector selector)
    {
        if (selector.IdentityFields.Count == 0)
            throw new GraphTransformationException("PSPROJ_IDENTITY", "Target selector must declare identity fields.");

        var parts = new List<string>(selector.IdentityFields.Count);
        foreach (var field in selector.IdentityFields)
        {
            if (!record.Values.TryGetValue(field, out var value) || value is NullValue)
                throw new GraphTransformationException("PSPROJ_IDENTITY", "Target identity field is missing or NULL.");
            var encoded = CanonicalValue(value);
            parts.Add($"{field.Length.ToString(CultureInfo.InvariantCulture)}:{field}={encoded.Length.ToString(CultureInfo.InvariantCulture)}:{encoded}");
        }

        return string.Join('|', parts);
    }

    public static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue text => $"string:{text.Value.Normalize(NormalizationForm.FormC)}",
        IntegerValue integer => $"integer:{integer.Value.ToString(CultureInfo.InvariantCulture)}",
        DecimalValue number => $"decimal:{number.Value.ToString("G29", CultureInfo.InvariantCulture)}",
        BooleanValue boolean => boolean.Value ? "boolean:true" : "boolean:false",
        DateValue date => $"date:{date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        InstantValue instant => $"instant:{instant.Value.ToString("O", CultureInfo.InvariantCulture)}",
        OffsetDateTimeValue offset => $"offset:{offset.Value.ToString("O", CultureInfo.InvariantCulture)}",
        LocalDateTimeValue local => $"local:{local.Value.ToString("O", CultureInfo.InvariantCulture)}",
        BinaryReferenceValue binary => $"binary:{binary.ContentLength.ToString(CultureInfo.InvariantCulture)}:{binary.Sha256}",
        CollectionValue collection => "collection:[" + string.Concat(collection.Values.Select(item => CanonicalPart(CanonicalValue(item)))) + "]",
        ObjectValue obj => "object:{" + string.Concat(obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => CanonicalPart(pair.Key) + CanonicalPart(CanonicalValue(pair.Value)))) + "}",
        _ => throw new GraphTransformationException("PSPROJ_VALUE", "Unsupported normalized value type.")
    };

    private static string CanonicalPart(string value) =>
        $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value}";
}

public static class GraphTransformationRuntime
{
    public static RecordEnvelope Transform(RecordEnvelope source, MigrationEdge edge, MigrationNode target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(target);

        var operation = edge.Operation;
        var output = new SortedDictionary<string, ValueNode>(StringComparer.Ordinal);
        if (operation.Type == MigrationOperationType.Copy ||
            operation.Fields.Count == 0 && operation.Type is MigrationOperationType.Archive or MigrationOperationType.Split)
        {
            foreach (var pair in source.Values) output.Add(pair.Key, pair.Value);
        }

        foreach (var field in operation.Fields)
        {
            if (field.Source is null || !source.Values.TryGetValue(field.Source, out var value))
                throw new GraphTransformationException("PSPROJ_FIELD", $"Source field for mapped target '{field.Target}' is unavailable.");
            foreach (var step in field.Pipeline) value = Apply(value, step, source.Values);
            output[field.Target] = value;
        }

        foreach (var step in operation.Steps)
        {
            if (step.Type == TransformationStepType.Copy) continue;
            if (step.Type == TransformationStepType.Rename)
            {
                if (!step.Parameters.TryGetValue("from", out var from) || !step.Parameters.TryGetValue("to", out var to) ||
                    !output.Remove(from, out var renamedValue))
                    throw new GraphTransformationException("PSPROJ_RENAME", "rename requires existing from and configured to parameters.");
                output[to] = renamedValue;
                continue;
            }

            if (step.Type is TransformationStepType.Concatenate or TransformationStepType.Split)
            {
                if (!step.Parameters.TryGetValue("target", out var targetField))
                    throw new GraphTransformationException("PSPROJ_TRANSFORM_TARGET", "Operation-level concatenate/split requires a target field parameter.");
                if (step.Type == TransformationStepType.Concatenate)
                {
                    output[targetField] = Concatenate(step, source.Values);
                }
                else
                {
                    if (!step.Parameters.TryGetValue("source", out var sourceField) ||
                        !source.Values.TryGetValue(sourceField, out var sourceValue) || sourceValue is not StringValue sourceText)
                        throw new GraphTransformationException("PSPROJ_SPLIT", "Operation-level split requires a string source field.");
                    output[targetField] = Split(sourceText.Value, step);
                }
                continue;
            }

            foreach (var outputField in output.Keys.ToArray())
                output[outputField] = Apply(output[outputField], step, source.Values);
        }

        var identity = GraphTargetIdentity.Create(
            new RecordEnvelope(source.Artifact, target.SemanticType, output, source.Provenance, source.Relationships, source.Temporal),
            target.Selector);
        var artifactType = StableArtifactIdentity.ArtifactTypeFor(target.Selector);
        var artifact = new ArtifactReference(
            new ArtifactId(StableArtifactIdentity.CreateArtifactId(target.SystemId.Value, target.EndpointId.Value, artifactType, identity)),
            target.SystemId, target.EndpointId, artifactType, identity);
        return new RecordEnvelope(
            artifact,
            target.SemanticType,
            output,
            new ProvenanceMetadata(
                source.Provenance.Connector,
                target.EndpointId,
                identity,
                source.Provenance.ObservedAt,
                source.Provenance.SourceHash,
                [new KeyValuePair<string, string>("projectionSource", source.Artifact.Id.Value),
                 new KeyValuePair<string, string>("projectionEdge", edge.Name)]),
            source.Relationships,
            source.Temporal);
    }

    private static ValueNode Apply(ValueNode value, TransformationStep step, IReadOnlyDictionary<string, ValueNode> sourceValues)
    {
        if (step.Version != "1") throw Unsupported($"{step.Type} version {step.Version}");
        if (value is NullValue) return value;
        return step.Type switch
        {
            TransformationStepType.Copy or TransformationStepType.Rename => value,
            TransformationStepType.Trim when value is StringValue text => new StringValue(text.Value.Trim()),
            TransformationStepType.NormalizeString when value is StringValue text => new StringValue(text.Value.Normalize(NormalizationForm.FormKC).ToUpperInvariant()),
            TransformationStepType.NormalizeDate => NormalizeDate(value, step),
            TransformationStepType.CodeMap when value is StringValue text => CodeMap(text.Value, step),
            TransformationStepType.Concatenate => Concatenate(step, sourceValues),
            TransformationStepType.Split when value is StringValue text => Split(text.Value, step),
            _ => throw Unsupported(step.Type.ToString())
        };
    }

    private static ValueNode NormalizeDate(ValueNode value, TransformationStep step)
    {
        if (value is DateValue) return value;
        if (value is not StringValue text || !step.Parameters.TryGetValue("format", out var format) || string.IsNullOrWhiteSpace(format))
            throw new GraphTransformationException("PSPROJ_DATE", "normalize-date requires a string value and an explicit invariant format.");
        if (!DateOnly.TryParseExact(text.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new GraphTransformationException("PSPROJ_DATE", "Date value does not match the configured invariant format.");
        return new DateValue(date);
    }

    private static StringValue CodeMap(string value, TransformationStep step)
    {
        foreach (var pair in step.Parameters)
        {
            if (pair.Key is "unknown" or "default") continue;
            if (string.Equals(pair.Key, value, StringComparison.Ordinal)) return new StringValue(pair.Value);
        }

        return step.Parameters.GetValueOrDefault("unknown") switch
        {
            "pass-through" => new StringValue(value),
            "default" when step.Parameters.TryGetValue("defaultValue", out var defaultValue) => new StringValue(defaultValue),
            _ => throw new GraphTransformationException("PSPROJ_CODE_MAP", "Code-map encountered an unmapped value and no pass-through/default policy was configured.")
        };
    }

    private static StringValue Concatenate(TransformationStep step, IReadOnlyDictionary<string, ValueNode> values)
    {
        if (!step.Parameters.TryGetValue("fields", out var fieldList))
            throw new GraphTransformationException("PSPROJ_CONCAT", "concatenate requires an ordered comma-separated fields parameter.");
        var separator = step.Parameters.GetValueOrDefault("separator") ?? string.Empty;
        var parts = fieldList.Split(',', StringSplitOptions.TrimEntries);
        var strings = parts.Select(field => values.TryGetValue(field, out var value) && value is StringValue text
            ? text.Value
            : throw new GraphTransformationException("PSPROJ_CONCAT", "concatenate fields must exist and contain strings."));
        return new StringValue(string.Join(separator, strings));
    }

    private static CollectionValue Split(string value, TransformationStep step)
    {
        if (!step.Parameters.TryGetValue("separator", out var separator) || separator.Length == 0)
            throw new GraphTransformationException("PSPROJ_SPLIT", "split requires a non-empty separator parameter.");
        return new CollectionValue(value.Split(separator, StringSplitOptions.None).Select(part => (ValueNode)new StringValue(part)));
    }

    private static GraphTransformationException Unsupported(string transformation) =>
        new("PSPROJ_UNSUPPORTED_TRANSFORM", $"Transformation '{transformation}' is not supported by PS-0.5.");
}
