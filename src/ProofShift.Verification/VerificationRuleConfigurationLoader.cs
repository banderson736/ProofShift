using ProofShift.Configuration;
using ProofShift.Domain;
using System.Globalization;

namespace ProofShift.Verification;

public sealed record ExpectedExceptionAnnotation
{
    public string Id { get; }
    public RuleId RuleId { get; }
    public string FindingCode { get; }
    public string TargetNode { get; }
    public string ArtifactIdSha256 { get; }
    public string EvaluationScopeId { get; }
    public string Reason { get; }
    public string Owner { get; }

    public ExpectedExceptionAnnotation(string id, RuleId ruleId, string findingCode, string targetNode,
        string artifactIdSha256, string evaluationScopeId, string reason, string owner)
    {
        Id = Required(id, nameof(id));
        RuleId = ruleId;
        FindingCode = Required(findingCode, nameof(findingCode));
        TargetNode = Required(targetNode, nameof(targetNode));
        ArtifactIdSha256 = Required(artifactIdSha256, nameof(artifactIdSha256)).ToLowerInvariant();
        if (ArtifactIdSha256.Length != 64 || !ArtifactIdSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected exception artifact identity must be a SHA-256 digest.", nameof(artifactIdSha256));
        EvaluationScopeId = Required(evaluationScopeId, nameof(evaluationScopeId));
        Reason = Required(reason, nameof(reason));
        Owner = Required(owner, nameof(owner));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed class VerificationRuleConfiguration : IReadOnlyList<VerificationRuleDefinition>
{
    private readonly VerificationRuleDefinition[] _definitions;
    public IReadOnlyList<ExpectedExceptionAnnotation> ExpectedExceptions { get; }
    public int Count => _definitions.Length;
    public VerificationRuleDefinition this[int index] => _definitions[index];

    internal VerificationRuleConfiguration(IEnumerable<VerificationRuleDefinition> definitions,
        IEnumerable<ExpectedExceptionAnnotation> expectedExceptions)
    {
        _definitions = definitions.ToArray();
        ExpectedExceptions = Array.AsReadOnly(expectedExceptions.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    public IEnumerator<VerificationRuleDefinition> GetEnumerator() =>
        ((IEnumerable<VerificationRuleDefinition>)_definitions).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class VerificationRuleConfigurationLoader
{
    public static VerificationRuleConfiguration Load(LoadedProjectConfiguration configuration)
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
        var expectedExceptions = ParseExpectedExceptions(root);
        return new VerificationRuleConfiguration(definitions, expectedExceptions);
    }

    private static List<ExpectedExceptionAnnotation> ParseExpectedExceptions(ConfigurationMappingNode root)
    {
        if (!root.Values.TryGetValue("expectedExceptions", out var node)) return [];
        if (node is not ConfigurationSequenceNode sequence)
            throw new VerificationRuleException("PSRULE013", "Expected exceptions must be a sequence.");
        var exceptions = new List<ExpectedExceptionAnnotation>();
        foreach (var item in sequence.Values)
        {
            if (item is not ConfigurationMappingNode mapping)
                throw new VerificationRuleException("PSRULE013", "Each expected exception must be a mapping.");
            try
            {
                var exception = new ExpectedExceptionAnnotation(
                    RequiredScalar(mapping, "id", "expected exception"),
                    new RuleId(RequiredScalar(mapping, "ruleId", "expected exception")),
                    RequiredScalar(mapping, "findingCode", "expected exception"),
                    RequiredScalar(mapping, "targetNode", "expected exception"),
                    RequiredScalar(mapping, "artifactIdSha256", "expected exception"),
                    RequiredScalar(mapping, "evaluationScopeId", "expected exception"),
                    RequiredScalar(mapping, "reason", "expected exception"),
                    RequiredScalar(mapping, "owner", "expected exception"));
                if (mapping.Values.Keys.Except(
                    ["id", "ruleId", "findingCode", "targetNode", "artifactIdSha256", "evaluationScopeId", "reason", "owner"],
                    StringComparer.Ordinal).Any())
                    throw new VerificationRuleException("PSRULE013", "Expected exception contains an unsupported property.");
                exceptions.Add(exception);
            }
            catch (ArgumentException exception)
            {
                throw new VerificationRuleException("PSRULE013",
                    $"Expected exception declaration is invalid ({exception.ParamName ?? "value"}).");
            }
        }
        if (exceptions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != exceptions.Count)
            throw new VerificationRuleException("PSRULE013", "Expected exception IDs must be unique.");
        if (exceptions.Select(item => (item.RuleId, item.FindingCode, item.TargetNode,
                item.ArtifactIdSha256, item.EvaluationScopeId)).Distinct().Count() != exceptions.Count)
            throw new VerificationRuleException("PSRULE013", "Expected exception match keys must be unique.");
        return exceptions;
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
