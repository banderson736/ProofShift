using ProofShift.Configuration;
using ProofShift.Domain;

namespace ProofShift.Verification;

public static class RuleReferenceValidator
{
    public static IReadOnlyList<ConfigurationValidationIssue> Validate(IEnumerable<VerificationRuleDefinition> definitions,
        VerificationRuleRegistry registry, MigrationGraph graph)
    {
        var issues = new List<ConfigurationValidationIssue>();
        var semanticTypes = graph.Nodes.Select(node => node.SemanticType).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in definitions.Where(definition => definition.UsesStructuredOptions))
        {
            var descriptor = registry.Descriptors.Single(item => item.Type == definition.Type);
            foreach (var option in descriptor.Options)
            {
                if (!definition.StructuredOptions.TryGetValue(option.Name, out var value)) continue;
                var location = definition.SourceLocation;
                var path = $"rules.{definition.Id.Value}.{option.Name}";
                if (value is StringValue text && option.Kind == RuleOptionKind.SemanticTypeReference && !semanticTypes.Contains(text.Value))
                    issues.Add(new("PSRULE009", ValidationSeverity.Error, "Semantic type is not declared in the migration graph.",
                        location?.File, path, location?.Line, location?.Column));
                if (value is StringValue nodeText && option.Name.EndsWith("Node", StringComparison.Ordinal) &&
                    !graph.Nodes.Any(node => node.Name == nodeText.Value))
                    issues.Add(new("PSRULE010", ValidationSeverity.Error, "Graph-node reference is unknown.",
                        location?.File, path, location?.Line, location?.Column));
                if (option.Kind != RuleOptionKind.FieldReference && option.ItemKind != RuleOptionKind.FieldReference) continue;
                var nodeOption = option.Name.StartsWith("source", StringComparison.Ordinal) ? "sourceNode" :
                    option.Name == "memberBusinessKey" ? "memberNode" : "targetNode";
                if (!definition.StructuredOptions.TryGetValue(nodeOption, out var scoped) || scoped is not StringValue scopedName) continue;
                var node = graph.Nodes.SingleOrDefault(candidate => candidate.Name == scopedName.Value);
                if (node is null) continue;
                var fields = node.Selector.IdentityFields.Concat(graph.Edges.Where(edge => edge.Targets.Contains(node.Id))
                        .SelectMany(edge => edge.Operation.Fields.Select(field => field.Target)))
                    .Concat(graph.Edges.Where(edge => edge.Sources.Contains(node.Id)).SelectMany(edge => edge.Operation.Fields
                        .Where(field => field.Source is not null).Select(field => field.Source!))).ToHashSet(StringComparer.Ordinal);
                var completeSourceColumns = node.Selector.Properties.TryGetValue("columns", out var columns);
                if (completeSourceColumns)
                    fields.UnionWith(columns!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var requested = value is CollectionValue collection ? collection.Values.Cast<StringValue>().Select(item => item.Value) :
                    value is StringValue fieldName ? [fieldName.Value] : [];
                foreach (var requestedField in requested)
                    if (fields.Count > 0 && !fields.Contains(requestedField))
                        issues.Add(new(node.Type == MigrationNodeType.Source && !completeSourceColumns ? "PSRULE012" : "PSRULE011",
                            node.Type == MigrationNodeType.Source && !completeSourceColumns ? ValidationSeverity.Warning : ValidationSeverity.Error,
                            node.Type == MigrationNodeType.Source && !completeSourceColumns
                                ? "Source field is outside mapped/identity columns; validate it against physical discovery before execution."
                                : "Field is not declared by this graph node's identities, selected columns or mappings.",
                            location?.File, path, location?.Line, location?.Column));
            }
        }
        return issues;
    }
}