using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;

namespace ProofShift.Cli;

internal static class ConnectorConfigurationValidator
{
    public static IReadOnlyList<ConfigurationValidationIssue> Validate(LoadedProjectConfiguration configuration,
        MigrationGraph graph, ConnectorCatalog connectors)
    {
        var issues = new List<ConfigurationValidationIssue>();
        foreach (var system in configuration.SystemConfigurations)
        {
            if (system.Id is null) continue;
            foreach (var endpoint in system.StorageEndpoints)
            {
                if (endpoint.Connector is null || !TryGetSchema(connectors, endpoint.Connector, out var schema)) continue;
                var file = configuration.Root.SystemFiles.TryGetValue(system.Id, out var systemFile) ? systemFile : null;
                foreach (var setting in endpoint.Settings)
                {
                    if (!schema.EndpointProperties.ContainsKey(setting.Key))
                        issues.Add(Issue("PSCONN021", $"Connector '{endpoint.Connector}' does not define endpoint property '{setting.Key}'.",
                            file, $"systems.{system.Id}.storage.{endpoint.Id}.{setting.Key}"));
                }
                foreach (var required in schema.RequiredEndpointProperties)
                {
                    if (!endpoint.Settings.ContainsKey(required))
                        issues.Add(Issue("PSCONN022", $"Connector '{endpoint.Connector}' requires endpoint property '{required}'.",
                            file, $"systems.{system.Id}.storage.{endpoint.Id}.{required}"));
                }
            }
        }

        foreach (var node in graph.Nodes)
        {
            var system = configuration.Systems.SingleOrDefault(item => item.Id == node.SystemId);
            var endpoint = system?.StorageEndpoints.SingleOrDefault(item => item.Id == node.EndpointId);
            if (endpoint is null || !TryGetSchema(connectors, endpoint.Connector.Value, out var schema)) continue;
            var file = configuration.MigrationGraphConfiguration?.RelativePath;
            var selectorPath = $"nodes.{node.Name}.selector";
            if (node.Selector.Kind != schema.SelectorKind)
            {
                issues.Add(Issue("PSCONN023", $"Connector '{schema.ConnectorId}' requires selector kind '{schema.SelectorKind}'.",
                    file, $"{selectorPath}.kind"));
                continue;
            }
            foreach (var property in node.Selector.Properties.Keys)
            {
                if (!schema.AllowsSelectorProperty(property))
                    issues.Add(Issue("PSCONN021", $"Connector '{schema.ConnectorId}' does not define selector property '{property}'.",
                        file, $"{selectorPath}.properties.{property}"));
                else if (!schema.IsSelectorPropertyValueValid(property, node.Selector.Properties[property]))
                    issues.Add(Issue("PSCONN024", $"Connector selector property '{property}' has an unsupported value.",
                        file, $"{selectorPath}.properties.{property}"));
            }
            if (node.Selector.IdentityFields.Count == 0)
                issues.Add(Issue("PSCONN022", $"Connector '{schema.ConnectorId}' requires explicit identity fields.",
                    file, $"{selectorPath}.identity"));
            if (schema.RequiredSelectorPropertySets.Count > 0 &&
                !schema.RequiredSelectorPropertySets.Any(required => required.All(node.Selector.Properties.ContainsKey)))
            {
                var alternatives = string.Join(" or ", schema.RequiredSelectorPropertySets.Select(set => string.Join(" and ", set)));
                issues.Add(Issue("PSCONN022", $"Connector '{schema.ConnectorId}' requires selector properties: {alternatives}.",
                    file, $"{selectorPath}.properties"));
            }
            if (schema.ConnectorId == "fixed-width")
            {
                var fields = node.Selector.Properties.Keys.Where(key => key.StartsWith("fields.", StringComparison.Ordinal))
                    .Select(key => key.Split('.')[1]).Distinct(StringComparer.Ordinal).ToArray();
                if (fields.Length == 0)
                    issues.Add(Issue("PSCONN022", "Fixed-width selectors require at least one declared field boundary.",
                        file, $"{selectorPath}.properties.fields"));
                foreach (var field in fields)
                {
                    foreach (var boundary in new[] { "start", "length" })
                    {
                        if (!node.Selector.Properties.ContainsKey($"fields.{field}.{boundary}"))
                            issues.Add(Issue("PSCONN022", $"Fixed-width field '{field}' requires '{boundary}'.",
                                file, $"{selectorPath}.properties.fields.{field}.{boundary}"));
                    }
                }
            }
        }
        return issues;
    }

    private static bool TryGetSchema(ConnectorCatalog connectors, string id, out ConnectorConfigurationSchema schema)
    {
        var found = connectors.ConfigurationSchemas.FirstOrDefault(item => item.ConnectorId == id);
        schema = found!;
        return found is not null;
    }

    private static ConfigurationValidationIssue Issue(string code, string message, string? file, string path) =>
        new(code, ValidationSeverity.Error, message, file, path);
}
