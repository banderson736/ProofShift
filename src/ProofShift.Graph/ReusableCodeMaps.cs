using ProofShift.Configuration;

namespace ProofShift.Graph;

public static class ReusableCodeMaps
{
    public static ConfigurationMappingNode Resolve(ConfigurationMappingNode root)
    {
        var maps = new Dictionary<string, ConfigurationMappingNode>(StringComparer.Ordinal);
        if (root.Values.TryGetValue("codeMaps", out var declared))
        {
            if (declared is not ConfigurationMappingNode definitions) throw new InvalidDataException("codeMaps must be a mapping.");
            foreach (var pair in definitions.Values)
            {
                if (pair.Value is not ConfigurationMappingNode definition ||
                    !definition.Values.TryGetValue("values", out var values) || values is not ConfigurationMappingNode valueMap || valueMap.Values.Count == 0)
                    throw new InvalidDataException("Named code maps require a nonempty values mapping.");
                if (!definition.Values.TryGetValue("unmapped", out var policy) || policy is not ConfigurationScalarNode { Value: "fail" or "pass-through" or "default" })
                    throw new InvalidDataException("Named code maps must declare unmapped: fail, pass-through, or default.");
                if (valueMap.Values.Any(value => string.IsNullOrWhiteSpace(value.Key) || value.Key is "unknown" or "default" ||
                    value.Value is not ConfigurationScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value)))
                    throw new InvalidDataException("Code-map keys/targets must be nonempty scalars and may not use reserved policy keys.");
                if (definition.Values.TryGetValue("allowedTargets", out var allowedNode))
                {
                    if (allowedNode is not ConfigurationSequenceNode allowed || allowed.Values.Any(value => value is not ConfigurationScalarNode))
                        throw new InvalidDataException("allowedTargets must be a scalar sequence.");
                    var targets = allowed.Values.Cast<ConfigurationScalarNode>().Select(value => value.Value).ToHashSet(StringComparer.Ordinal);
                    if (valueMap.Values.Values.Cast<ConfigurationScalarNode>().Any(value => !targets.Contains(value.Value)))
                        throw new InvalidDataException("Code map has a target outside its declared allowedTargets.");
                    if (definition.Values.TryGetValue("default", out var fallbackTarget) && fallbackTarget is ConfigurationScalarNode { Value: not null } constrainedFallback &&
                        !targets.Contains(constrainedFallback.Value))
                        throw new InvalidDataException("Code-map default is outside its declared allowedTargets.");
                }
                if (policy is ConfigurationScalarNode { Value: "default" } &&
                    (!definition.Values.TryGetValue("default", out var fallback) || fallback is not ConfigurationScalarNode { Value.Length: > 0 }))
                    throw new InvalidDataException("Default code-map policy requires a nonempty default value.");
                maps.Add(pair.Key, definition);
            }
        }
        return (ConfigurationMappingNode)Expand(root, maps);
    }

    private static ConfigurationDocumentNode Expand(ConfigurationDocumentNode node,
        Dictionary<string, ConfigurationMappingNode> maps)
    {
        if (node is ConfigurationSequenceNode sequence)
            return new ConfigurationSequenceNode(sequence.Values.Select(value => Expand(value, maps))) { Line = node.Line, Column = node.Column };
        if (node is not ConfigurationMappingNode mapping) return node;
        if (mapping.Values.TryGetValue("type", out var type) && type is ConfigurationScalarNode { Value: "code-map" } &&
            mapping.Values.TryGetValue("map", out var reference))
        {
            if (reference is not ConfigurationScalarNode { Value: not null } name || !maps.TryGetValue(name.Value, out var definition))
                throw new InvalidDataException("Code-map reference does not resolve to a named definition.");
            if (mapping.Values.ContainsKey("values") || mapping.Values.ContainsKey("unknown"))
                throw new InvalidDataException("A named code-map reference may not override values or policy.");
            var properties = mapping.Values.Where(pair => pair.Key != "map").ToList();
            properties.Add(new("values", definition.Values["values"]));
            if (definition.Values["unmapped"] is not ConfigurationScalarNode { Value: "fail" })
                properties.Add(new("unknown", definition.Values["unmapped"]));
            if (definition.Values.TryGetValue("default", out var fallback) && fallback is ConfigurationScalarNode { Value: not null })
                properties.Add(new("default", fallback));
            return new ConfigurationMappingNode(properties) { Line = node.Line, Column = node.Column };
        }
        return new ConfigurationMappingNode(mapping.Values.Select(pair => new KeyValuePair<string, ConfigurationDocumentNode>(
            pair.Key, pair.Key == "codeMaps" ? pair.Value : Expand(pair.Value, maps)))) { Line = node.Line, Column = node.Column };
    }
}