using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProofShift.Connectors.Abstractions;

public sealed record ConnectorConfigurationSchema(
    string ConnectorId,
    string SelectorKind,
    IReadOnlyDictionary<string, string> EndpointProperties,
    IReadOnlyCollection<string> RequiredEndpointProperties,
    IReadOnlyDictionary<string, string> SelectorProperties,
    IReadOnlyDictionary<string, string> SelectorPropertyPatterns,
    IReadOnlyCollection<IReadOnlyCollection<string>> RequiredSelectorPropertySets)
{
    public bool AllowsSelectorProperty(string name) => SelectorProperties.ContainsKey(name) ||
        SelectorPropertyPatterns.Keys.Any(pattern => Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant));

    public string? SelectorPropertyKind(string name)
    {
        if (SelectorProperties.TryGetValue(name, out var kind)) return kind;
        return SelectorPropertyPatterns.FirstOrDefault(pair => Regex.IsMatch(name, pair.Key, RegexOptions.CultureInvariant)).Value;
    }

    public bool IsSelectorPropertyValueValid(string name, string value)
    {
        var kind = SelectorPropertyKind(name);
        if (kind is null) return false;
        if (kind.StartsWith("enum:", StringComparison.Ordinal))
            return kind[5..].Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal);
        if (kind == "positive-integer-string")
            return int.TryParse(value, out var positive) && positive > 0;
        if (kind == "scale-string")
            return int.TryParse(value, out var scale) && scale is >= 0 and <= 28;
        if (kind == "date-time-format")
            return value.Contains('z') || value.Contains('K');
        return kind is "string" or "string-or-reference";
    }

    public JsonObject ToJsonSchema()
    {
        var endpointProperties = new JsonObject();
        foreach (var pair in EndpointProperties) endpointProperties.Add(pair.Key, PropertySchema(pair.Value));
        var endpoint = new JsonObject { ["type"] = "object", ["properties"] = endpointProperties, ["additionalProperties"] = false };
        if (RequiredEndpointProperties.Count > 0) endpoint["required"] = StringArray(RequiredEndpointProperties);

        var selectorPropertySchemas = new JsonObject();
        foreach (var pair in SelectorProperties) selectorPropertySchemas.Add(pair.Key, PropertySchema(pair.Value));
        var selectorPatterns = new JsonObject();
        foreach (var pattern in SelectorPropertyPatterns) selectorPatterns.Add(pattern.Key, PropertySchema(pattern.Value));
        var selectorProperties = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = selectorPropertySchemas,
            ["patternProperties"] = selectorPatterns,
            ["additionalProperties"] = false
        };
        var selector = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["const"] = SelectorKind },
                ["properties"] = selectorProperties,
                ["identity"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1 }
            },
            ["required"] = new JsonArray("kind", "properties", "identity"),
            ["additionalProperties"] = false
        };
        if (RequiredSelectorPropertySets.Count > 0)
        {
            var alternatives = new JsonArray();
            foreach (var required in RequiredSelectorPropertySets)
                alternatives.Add(new JsonObject { ["required"] = StringArray(required) });
            selector["properties"]!["properties"]!["anyOf"] = alternatives;
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = $"ProofShift {ConnectorId} connector configuration",
            ["type"] = "object",
            ["properties"] = new JsonObject { ["endpoint"] = endpoint, ["selector"] = selector },
            ["required"] = new JsonArray("endpoint", "selector"),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject PropertySchema(string kind) => kind switch
    {
        "string" => new JsonObject { ["type"] = "string" },
        "positive-integer-string" => new JsonObject { ["type"] = "string", ["pattern"] = "^[1-9][0-9]*$" },
        "scale-string" => new JsonObject { ["type"] = "string", ["pattern"] = "^(0|[1-9]|1[0-9]|2[0-8])$" },
        "date-time-format" => new JsonObject { ["type"] = "string", ["pattern"] = "(z|K)" },
        _ when kind.StartsWith("enum:", StringComparison.Ordinal) => new JsonObject
        {
            ["type"] = "string",
            ["enum"] = StringArray(kind[5..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        },
        "string-or-reference" => new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["secret"] = new JsonObject { ["type"] = "string" },
                        ["env"] = new JsonObject { ["type"] = "string" }
                    },
                    ["minProperties"] = 1,
                    ["maxProperties"] = 1,
                    ["additionalProperties"] = false
                })
        },
        _ => throw new InvalidOperationException($"Unsupported connector schema property kind '{kind}'.")
    };

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}

public interface IConnectorConfigurationSchemaProvider
{
    ConnectorConfigurationSchema ConfigurationSchema { get; }
}

public static class StructuredFileConfigurationSchemas
{
    private static readonly Dictionary<string, ConnectorConfigurationSchema> Schemas =
        new(StringComparer.Ordinal)
        {
            ["fixed-width"] = Create("fixed-width", ["path", "width"]),
            ["json"] = Create("json", ["path"]),
            ["ndjson"] = Create("ndjson", ["path"]),
            ["xml"] = Create("xml", ["path", "recordElement"])
        };

    public static ConnectorConfigurationSchema Get(string format) => Schemas.TryGetValue(format, out var schema)
        ? schema : throw new ArgumentException("Unsupported structured-file format.", nameof(format));

    private static ConnectorConfigurationSchema Create(string id, IReadOnlyCollection<string> required)
    {
        var selectorProperties = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "string" };
        if (id is "fixed-width" or "json" or "ndjson")
            selectorProperties.Add("encoding", id == "json" ? "enum:utf-8" : "enum:utf-8,utf-16");
        if (id == "fixed-width") selectorProperties.Add("width", "positive-integer-string");
        if (id == "xml") selectorProperties.Add("recordElement", "string");
        var fieldPatterns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["^fields\\.[A-Za-z_][A-Za-z0-9_-]*\\.(path|format|null|true|false)$"] = "string",
            ["^fields\\.[A-Za-z_][A-Za-z0-9_-]*\\.type$"] = "enum:string,integer,decimal,boolean,date,datetime,offset-datetime,object,collection",
            ["^fields\\.[A-Za-z_][A-Za-z0-9_-]*\\.(start|length)$"] = "positive-integer-string",
            ["^fields\\.[A-Za-z_][A-Za-z0-9_-]*\\.scale$"] = "scale-string",
            ["^fields\\.[A-Za-z_][A-Za-z0-9_-]*\\.trim$"] = "enum:none,left,right,both"
        };
        if (id == "xml") fieldPatterns["^namespaces\\.[A-Za-z_][A-Za-z0-9_-]*$"] = "string";
        return new ConnectorConfigurationSchema(id, id,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = "string-or-reference" }, ["root"],
            selectorProperties, fieldPatterns, [required]);
    }
}
