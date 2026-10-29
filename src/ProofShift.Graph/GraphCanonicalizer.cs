using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Graph;

public static class GraphCanonicalizer
{
    public const string FormatVersion = "proofshift-graph-canonical-v1";
    public const string EvaluationScopeFormatVersion = "proofshift-graph-canonical-v2";

    public static string Canonicalize(
        int graphVersion,
        IEnumerable<MigrationNode> nodes,
        IEnumerable<MigrationEdge> edges,
        IReadOnlyDictionary<MigrationNodeId, string> externalNodeKeys)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(externalNodeKeys);
        var nodeList = nodes.ToArray();
        var edgeList = edges.ToArray();
        var formatVersion = nodeList.Any(node => node.Selector.EvaluationScope is not null)
            ? EvaluationScopeFormatVersion
            : FormatVersion;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", graphVersion);
            writer.WriteStartArray("nodes");
            foreach (var node in nodeList.OrderBy(node => node.Name, StringComparer.Ordinal))
            {
                WriteNode(writer, node);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("edges");
            foreach (var edge in edgeList.OrderBy(edge => edge.Name, StringComparer.Ordinal))
            {
                WriteEdge(writer, edge, externalNodeKeys);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return $"{formatVersion}\n{Encoding.UTF8.GetString(stream.ToArray())}";
    }

    private static void WriteNode(Utf8JsonWriter writer, MigrationNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("key", node.Name);
        writer.WriteString("type", Kebab(node.Type.ToString()));
        writer.WriteString("system", node.SystemId.Value);
        writer.WriteString("storage", node.EndpointId.Value);
        writer.WriteString("semanticType", node.SemanticType);
        writer.WriteStartObject("selector");
        writer.WriteString("kind", node.Selector.Kind);
        writer.WriteStartObject("properties");
        foreach (var property in node.Selector.Properties)
        {
            writer.WriteString(property.Key, property.Value);
        }

        writer.WriteEndObject();
        writer.WriteStartArray("identity");
        foreach (var identity in node.Selector.IdentityFields)
        {
            writer.WriteStringValue(identity);
        }

        writer.WriteEndArray();
        if (node.Selector.EvaluationScope is { } scope)
        {
            writer.WriteStartObject("evaluationScope");
            writer.WriteString("id", scope.Id);
            writer.WriteString("type", ScopeType(scope.Type));
            writer.WriteString("fingerprint", scope.Fingerprint);
            if (scope.Description is not null) writer.WriteString("description", scope.Description);
            writer.WriteStartArray("predicates");
            foreach (var predicate in scope.Predicates.OrderBy(EvaluationScope.CanonicalPredicateOrderKey, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("field", predicate.Field);
                writer.WriteString("operator", EvaluationPredicate.OperatorName(predicate.Operator));
                writer.WriteStartArray("values");
                foreach (var value in predicate.Values) writer.WriteStringValue(value);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ScopeType(EvaluationScopeType type) => type switch
    {
        EvaluationScopeType.FullDeclaredSource => "full-declared-source",
        EvaluationScopeType.BoundedEvaluation => "bounded-evaluation",
        EvaluationScopeType.CustomerPreparedSubset => "customer-prepared-subset",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void WriteEdge(
        Utf8JsonWriter writer,
        MigrationEdge edge,
        IReadOnlyDictionary<MigrationNodeId, string> externalNodeKeys)
    {
        writer.WriteStartObject();
        writer.WriteString("key", edge.Name);
        writer.WriteString("version", edge.Version);
        WriteNodeKeys(writer, "from", edge.Sources, externalNodeKeys);
        WriteNodeKeys(writer, "to", edge.Targets, externalNodeKeys);
        writer.WriteStartObject("operation");
        writer.WriteString("type", Kebab(edge.Operation.Type.ToString()));
        writer.WriteBoolean("destructive", edge.Operation.IsDestructive);
        WriteStringMap(writer, "parameters", edge.Operation.Parameters);
        writer.WriteStartArray("steps");
        foreach (var step in edge.Operation.Steps)
        {
            WriteStep(writer, step);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("fields");
        foreach (var field in edge.Operation.Fields.OrderBy(field => field.Target, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("target", field.Target);
            WriteNullableString(writer, "source", field.Source);
            writer.WriteStartArray("pipeline");
            foreach (var step in field.Pipeline)
            {
                WriteStep(writer, step);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        if (edge.Recovery is null)
        {
            writer.WriteNull("recovery");
        }
        else
        {
            writer.WriteStartObject("recovery");
            writer.WriteString("mode", Kebab(edge.Recovery.Mode.ToString()));
            WriteNullableString(writer, "strategy", edge.Recovery.Strategy);
            writer.WriteBoolean("requiresSnapshot", edge.Recovery.RequiresSnapshot);
            WriteNullableString(writer, "justification", edge.Recovery.Justification);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteNodeKeys(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<MigrationNodeId> nodeIds,
        IReadOnlyDictionary<MigrationNodeId, string> externalNodeKeys)
    {
        writer.WriteStartArray(propertyName);
        foreach (var key in nodeIds.Select(nodeId => externalNodeKeys[nodeId]).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(key);
        }

        writer.WriteEndArray();
    }

    private static void WriteStep(Utf8JsonWriter writer, TransformationStep step)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Kebab(step.Type.ToString()));
        writer.WriteString("version", step.Version);
        WriteStringMap(writer, "parameters", step.Parameters);
        writer.WriteEndObject();
    }

    private static void WriteStringMap(Utf8JsonWriter writer, string name, IEnumerable<KeyValuePair<string, string>> values)
    {
        writer.WriteStartObject(name);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string Kebab(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
