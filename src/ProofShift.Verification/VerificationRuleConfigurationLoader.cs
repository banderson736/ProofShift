using ProofShift.Configuration;
using ProofShift.Domain;
using System.Globalization;

namespace ProofShift.Verification;

public sealed class VerificationRuleConfigurationLoader
{
    public static IReadOnlyList<VerificationRuleDefinition> Load(LoadedProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var file = configuration.ReferencedFiles.FirstOrDefault(item => item.Kind == ConfigurationFileKind.VerificationRules)
            ?? throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Configuration does not reference a verification rule file.");
        if (file.Document is not ConfigurationMappingNode root ||
            !root.Values.TryGetValue("rules", out var rulesNode) || rulesNode is not ConfigurationMappingNode rules)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Verification rule file must contain a rules mapping.");

        var documentVersion = OptionalScalar(root, "version") ?? "1";
        if (documentVersion is not ("1" or "2"))
            throw new VerificationRuleException("PSRULE001", "Rule document version must be 1 or 2.");
        var definitions = new List<VerificationRuleDefinition>();
        foreach (var (id, node) in rules.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (node is not ConfigurationMappingNode rule)
                throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, $"Rule '{id}' must be a mapping.");
            var type = RequiredScalar(rule, "type", id);
            var version = OptionalScalar(rule, "version") ?? "1";
            var severity = ParseSeverity(OptionalScalar(rule, "severity") ?? "error", id);
            var options = new List<KeyValuePair<string, string>>();
            var structuredOptions = new List<KeyValuePair<string, ValueNode>>();
            foreach (var (key, value) in rule.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (key is "type" or "version" or "severity") continue;
                if (documentVersion == "2")
                    structuredOptions.Add(new(key, StructuredValue(value, $"rules.{id}.{key}")));
                else
                    options.Add(new KeyValuePair<string, string>(key, Flatten(value, $"{id}.{key}")));
            }
            var definition = documentVersion == "2"
                ? new VerificationRuleDefinition(new RuleId(id), type, version, severity, structuredOptions: structuredOptions)
                : new VerificationRuleDefinition(new RuleId(id), type, version, severity, options);
            definitions.Add(definition with { SourceLocation = new RuleSourceLocation(file.RelativePath, $"rules.{id}", rule.Line, rule.Column) });
        }
        return definitions;
    }

    private static string RequiredScalar(ConfigurationMappingNode mapping, string key, string ruleId) =>
        OptionalScalar(mapping, key) ?? throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
            $"Rule '{ruleId}' is missing required scalar '{key}'.");

    private static string? OptionalScalar(ConfigurationMappingNode mapping, string key) =>
        mapping.Values.TryGetValue(key, out var node) && node is ConfigurationScalarNode { Value: not null } scalar
            ? scalar.Value
            : null;

    private static string Flatten(ConfigurationDocumentNode node, string path) => node switch
    {
        ConfigurationScalarNode { Value: not null } scalar => scalar.Value,
        ConfigurationSequenceNode sequence => string.Join(',', sequence.Values.Select(value => Flatten(value, path))),
        _ => throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
            $"Rule option '{path}' must be a scalar or scalar sequence.")
    };

    private static ValueNode StructuredValue(ConfigurationDocumentNode node, string path) => node switch
    {
        ConfigurationScalarNode { Kind: ConfigurationScalarKind.Null } => new NullValue(),
        ConfigurationScalarNode { Kind: ConfigurationScalarKind.Boolean, Value: not null } scalar =>
            new BooleanValue(bool.Parse(scalar.Value)),
        ConfigurationScalarNode { Kind: ConfigurationScalarKind.Number, Value: not null } scalar when
            long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => new IntegerValue(integer),
        ConfigurationScalarNode { Kind: ConfigurationScalarKind.Number, Value: not null } scalar when
            decimal.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => new DecimalValue(number),
        ConfigurationScalarNode { Kind: ConfigurationScalarKind.Text, Value: not null } scalar => new StringValue(scalar.Value),
        ConfigurationSequenceNode sequence => new CollectionValue(sequence.Values.Select(value => StructuredValue(value, path))),
        ConfigurationMappingNode mapping => new ObjectValue(mapping.Values.Select(pair =>
            new KeyValuePair<string, ValueNode>(pair.Key, StructuredValue(pair.Value, $"{path}.{pair.Key}")))),
        _ => throw new VerificationRuleException("PSRULE003", $"Rule option '{path}' contains an unsupported value type.")
    };

    private static EvidenceSeverity ParseSeverity(string value, string ruleId) => value.Trim().ToLowerInvariant() switch
    {
        "critical" => EvidenceSeverity.Critical,
        "error" => EvidenceSeverity.Error,
        "warning" => EvidenceSeverity.Warning,
        "info" => EvidenceSeverity.Info,
        _ => throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
            $"Rule '{ruleId}' has unsupported severity '{value}'.")
    };
}
