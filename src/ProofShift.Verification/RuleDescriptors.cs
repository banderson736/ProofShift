using System.Globalization;
using ProofShift.Domain;

namespace ProofShift.Verification;

public enum RuleOptionKind
{
    Text, Logical, WholeNumber, Number, FieldReference, SemanticTypeReference, Sequence, Mapping
}

public sealed record RuleOptionDescriptor(
    string Name,
    RuleOptionKind Kind,
    string Description,
    bool Required = false,
    ValueNode? Default = null,
    IReadOnlyCollection<RuleOptionDescriptor>? Properties = null,
    RuleOptionKind? ItemKind = null,
    IReadOnlyCollection<string>? AllowedValues = null,
    decimal? Minimum = null);

public sealed record RuleDescriptor
{
    public string Type { get; }
    public string Version { get; }
    public string Description { get; }
    public VerificationScope Scope { get; }
    public DomainList<RuleOptionDescriptor> Options { get; }
    public string ExampleYaml { get; }

    public RuleDescriptor(string type, string version, string description, VerificationScope scope,
        IEnumerable<RuleOptionDescriptor>? options = null, string? exampleYaml = null)
    {
        Type = type;
        Version = version;
        Description = description;
        Scope = scope;
        Options = new DomainList<RuleOptionDescriptor>(options ?? []);
        ExampleYaml = exampleYaml ?? $"type: {type}\nversion: \"{version}\"\nseverity: error\n";
    }

    public void Validate(VerificationRuleDefinition definition)
    {
        if (definition.Version != Version)
            throw new VerificationRuleException("PSRULE002", $"Rule '{definition.Id.Value}' requests an unavailable version. Supported version: {Version}.");
        if (!definition.UsesStructuredOptions) return;
        ValidateObject(definition.StructuredOptions, Options, $"rules.{definition.Id.Value}");
    }

    private static void ValidateObject(DomainDictionary<ValueNode> values,
        IReadOnlyCollection<RuleOptionDescriptor> properties, string path)
    {
        foreach (var key in values.Keys.Order(StringComparer.Ordinal))
            if (!properties.Any(property => property.Name == key))
                throw new VerificationRuleException("PSRULE005", $"Unsupported rule option '{path}.{key}'.");
        foreach (var property in properties)
        {
            var optionPath = $"{path}.{property.Name}";
            if (!values.TryGetValue(property.Name, out var value))
            {
                if (property.Required && property.Default is null)
                    throw new VerificationRuleException("PSRULE004", $"Rule configuration requires '{optionPath}'.");
                continue;
            }
            ValidateValue(value, property.Kind, optionPath);
            if (value is ObjectValue nested)
                ValidateObject(nested.Values, property.Properties ?? [], optionPath);
            if (value is CollectionValue sequence)
                foreach (var item in sequence.Values)
                    ValidateValue(item, property.ItemKind ?? RuleOptionKind.Text, optionPath + "[]");
            if (property.AllowedValues is { Count: > 0 } allowed && value is StringValue text && !allowed.Contains(text.Value, StringComparer.Ordinal))
                throw new VerificationRuleException("PSRULE006", $"Rule option '{optionPath}' requires one of: {string.Join(", ", allowed)}.");
            var number = value switch { DecimalValue decimalValue => decimalValue.Value, IntegerValue integer => integer.Value, _ => (decimal?)null };
            if (property.Minimum is { } minimum && number is { } actual && actual < minimum)
                throw new VerificationRuleException("PSRULE007", $"Rule option '{optionPath}' must be at least {minimum.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void ValidateValue(ValueNode value, RuleOptionKind kind, string path)
    {
        var valid = kind switch
        {
            RuleOptionKind.Text => value is StringValue,
            RuleOptionKind.FieldReference or RuleOptionKind.SemanticTypeReference => value is StringValue { Value.Length: > 0 },
            RuleOptionKind.Logical => value is BooleanValue,
            RuleOptionKind.WholeNumber => value is IntegerValue,
            RuleOptionKind.Number => value is DecimalValue or IntegerValue,
            RuleOptionKind.Sequence => value is CollectionValue,
            RuleOptionKind.Mapping => value is ObjectValue,
            _ => false
        };
        if (!valid)
            throw new VerificationRuleException("PSRULE003", $"Rule option '{path}' requires {kind}.");
    }
}