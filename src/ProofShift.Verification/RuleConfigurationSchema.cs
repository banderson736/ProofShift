using System.Text.Json.Nodes;
using ProofShift.Domain;

namespace ProofShift.Verification;

public static class RuleConfigurationSchema
{
    public static JsonObject Generate(IEnumerable<RuleDescriptor> descriptors)
    {
        var alternatives = new JsonArray();
        foreach (var descriptor in descriptors.OrderBy(item => item.Type, StringComparer.Ordinal))
        {
            var properties = new JsonObject
            {
                ["type"] = new JsonObject { ["const"] = descriptor.Type },
                ["version"] = new JsonObject { ["type"] = "string", ["const"] = descriptor.Version },
                ["severity"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("critical", "error", "warning", "info")
                }
            };
            var required = new JsonArray("type");
            foreach (var option in descriptor.Options)
            {
                properties[option.Name] = OptionSchema(option);
                if (option.Required && option.Default is null) required.Add(option.Name);
            }
            alternatives.Add(new JsonObject
            {
                ["type"] = "object",
                ["description"] = descriptor.Description,
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            });
        }
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "ProofShift Structured Verification Rules",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["version"] = new JsonObject { ["const"] = 2 },
                ["rules"] = new JsonObject
                {
                    ["type"] = "object",
                    ["minProperties"] = 1,
                    ["additionalProperties"] = new JsonObject { ["oneOf"] = alternatives }
                }
            },
            ["required"] = new JsonArray("version", "rules"),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject OptionSchema(RuleOptionDescriptor option)
    {
        var schema = new JsonObject
        {
            ["type"] = option.Kind switch
            {
                RuleOptionKind.Logical => "boolean",
                RuleOptionKind.WholeNumber => "integer",
                RuleOptionKind.Number => "number",
                RuleOptionKind.Sequence => "array",
                RuleOptionKind.Mapping => "object",
                _ => "string"
            },
            ["description"] = option.Description
        };
        if (option.Kind is RuleOptionKind.FieldReference or RuleOptionKind.SemanticTypeReference) schema["minLength"] = 1;
        if (option.Minimum is { } minimum) schema["minimum"] = minimum;
        if (option.Default is { } defaultValue) schema["default"] = ToJson(defaultValue);
        if (option.AllowedValues is { Count: > 0 } allowed)
            schema["enum"] = new JsonArray(allowed.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (option.Kind == RuleOptionKind.Sequence)
            schema["items"] = OptionSchema(new RuleOptionDescriptor("item", option.ItemKind ?? RuleOptionKind.Text, "Configured list item."));
        if (option.Kind == RuleOptionKind.Mapping)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var property in option.Properties ?? [])
            {
                properties[property.Name] = OptionSchema(property);
                if (property.Required && property.Default is null) required.Add(property.Name);
            }
            schema["properties"] = properties;
            schema["required"] = required;
            schema["additionalProperties"] = false;
        }
        return schema;
    }

    private static JsonNode? ToJson(ValueNode value) => value switch
    {
        StringValue text => JsonValue.Create(text.Value),
        BooleanValue boolean => JsonValue.Create(boolean.Value),
        IntegerValue integer => JsonValue.Create(integer.Value),
        DecimalValue number => JsonValue.Create(number.Value),
        NullValue => null,
        CollectionValue collection => new JsonArray(collection.Values.Select(ToJson).ToArray()),
        ObjectValue mapping => new JsonObject(mapping.Values.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ToJson(pair.Value)))),
        _ => throw new ArgumentException("Unsupported rule schema default value.", nameof(value))
    };
}