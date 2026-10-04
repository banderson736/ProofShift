using ProofShift.Configuration;
using ProofShift.Domain;

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

        var definitions = new List<VerificationRuleDefinition>();
        foreach (var (id, node) in rules.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (node is not ConfigurationMappingNode rule)
                throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, $"Rule '{id}' must be a mapping.");
            var type = RequiredScalar(rule, "type", id);
            var version = OptionalScalar(rule, "version") ?? "1";
            var severity = ParseSeverity(OptionalScalar(rule, "severity") ?? "error", id);
            var options = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in rule.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (key is "type" or "version" or "severity") continue;
                options.Add(new KeyValuePair<string, string>(key, Flatten(value, $"{id}.{key}")));
            }
            definitions.Add(new VerificationRuleDefinition(new RuleId(id), type, version, severity, options));
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
