using System.Globalization;
using ProofShift.Domain;

namespace ProofShift.Verification;

public enum RuleOptionKind
{
    Text, Logical, WholeNumber, Number, FieldReference, SemanticTypeReference, Sequence, Mapping
}

public enum VerificationFieldSide
{
    Source,
    Target
}

public sealed record RuleFieldRequirement
{
    public string OptionName { get; }
    public VerificationFieldSide Side { get; }
    public string? SemanticTypeOption { get; }
    public string? DefaultSemanticType { get; }
    public DomainList<string> DefaultFields { get; }
    public VerificationOrderingRole? KeyRole { get; }
    public bool IncludeMappedTargetFieldsWhenUnset { get; }
    public string? TargetNodeOption { get; }

    public RuleFieldRequirement(string optionName, VerificationFieldSide side,
        string? semanticTypeOption = "semanticType", string? defaultSemanticType = null,
        IEnumerable<string>? defaultFields = null, VerificationOrderingRole? keyRole = null,
        bool includeMappedTargetFieldsWhenUnset = false, string? targetNodeOption = null)
    {
        OptionName = string.IsNullOrWhiteSpace(optionName)
            ? throw new ArgumentException("A field requirement option name is required.", nameof(optionName))
            : optionName.Trim();
        Side = side;
        SemanticTypeOption = string.IsNullOrWhiteSpace(semanticTypeOption) ? null : semanticTypeOption.Trim();
        DefaultSemanticType = string.IsNullOrWhiteSpace(defaultSemanticType) ? null : defaultSemanticType.Trim();
        DefaultFields = new DomainList<string>((defaultFields ?? []).Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        KeyRole = keyRole;
        IncludeMappedTargetFieldsWhenUnset = includeMappedTargetFieldsWhenUnset;
        TargetNodeOption = string.IsNullOrWhiteSpace(targetNodeOption) ? null : targetNodeOption.Trim();
        if (includeMappedTargetFieldsWhenUnset && side != VerificationFieldSide.Target)
            throw new ArgumentException("Graph-mapped field requirements must target the target workset.", nameof(side));
    }
}

public sealed record VerificationPartitionKeyDefinition
{
    public VerificationPartitionBasis Basis { get; }
    public VerificationArtifactRole Role { get; }
    public DomainList<string> FieldOptions { get; }

    public VerificationPartitionKeyDefinition(VerificationPartitionBasis basis, VerificationArtifactRole role,
        IEnumerable<string>? fieldOptions = null)
    {
        Basis = basis;
        Role = role;
        FieldOptions = new DomainList<string>((fieldOptions ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (basis == VerificationPartitionBasis.ArtifactIdentity && FieldOptions.Count != 0)
            throw new ArgumentException("Artifact-identity partition keys do not accept field options.", nameof(fieldOptions));
        if (basis != VerificationPartitionBasis.ArtifactIdentity && FieldOptions.Count == 0)
            throw new ArgumentException("Field-based partition keys require descriptor field options.", nameof(fieldOptions));
    }
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
    public DomainList<string> RequiredSemanticTypes { get; }
    public DomainList<string> RequiredSourceFields { get; }
    public DomainList<string> RequiredTargetFields { get; }
    public DomainList<RuleFieldRequirement> FieldRequirements { get; }
    public DomainList<VerificationOrderingKey> GroupingKeys { get; }
    public DomainList<VerificationOrderingKey> OrderingKeys { get; }
    public DomainList<VerificationOrderingKey> LookupKeys { get; }
    public VerificationPartitionExecution PartitionExecution { get; }
    public VerificationPartitionKeyDefinition? PartitionKey { get; }

    public RuleDescriptor(string type, string version, string description, VerificationScope scope,
        IEnumerable<RuleOptionDescriptor>? options = null, string? exampleYaml = null,
        IEnumerable<string>? requiredSemanticTypes = null, IEnumerable<string>? requiredSourceFields = null,
        IEnumerable<string>? requiredTargetFields = null, IEnumerable<VerificationOrderingKey>? groupingKeys = null,
        IEnumerable<VerificationOrderingKey>? orderingKeys = null, IEnumerable<VerificationOrderingKey>? lookupKeys = null,
        IEnumerable<RuleFieldRequirement>? fieldRequirements = null,
        VerificationPartitionExecution partitionExecution = VerificationPartitionExecution.Global,
        VerificationPartitionKeyDefinition? partitionKey = null)
    {
        Type = type;
        Version = version;
        Description = description;
        Scope = scope;
        Options = new DomainList<RuleOptionDescriptor>(options ?? []);
        ExampleYaml = exampleYaml ?? $"type: {type}\nversion: \"{version}\"\nseverity: error\n";
        RequiredSemanticTypes = OrderedValues(requiredSemanticTypes);
        RequiredSourceFields = OrderedValues(requiredSourceFields);
        RequiredTargetFields = OrderedValues(requiredTargetFields);
        FieldRequirements = new DomainList<RuleFieldRequirement>(fieldRequirements ?? []);
        GroupingKeys = OrderedKeys(groupingKeys);
        OrderingKeys = OrderedKeys(orderingKeys);
        LookupKeys = OrderedKeys(lookupKeys);
        PartitionExecution = partitionExecution;
        PartitionKey = partitionKey;
        if ((partitionExecution == VerificationPartitionExecution.Global) == (partitionKey is not null))
            throw new ArgumentException("Non-global rule execution requires exactly one declared partition key.", nameof(partitionKey));
    }

    private static DomainList<string> OrderedValues(IEnumerable<string>? values) =>
        new((values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static DomainList<VerificationOrderingKey> OrderedKeys(IEnumerable<VerificationOrderingKey>? keys) =>
        new((keys ?? []).Distinct().OrderBy(key => key.SemanticType, StringComparer.Ordinal)
            .ThenBy(key => key.Field, StringComparer.Ordinal).ThenBy(key => key.Role).ThenBy(key => key.Descending));

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