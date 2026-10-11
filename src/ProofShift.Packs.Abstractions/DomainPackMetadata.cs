using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Packs.Abstractions;

public sealed record DomainPackMetadata
{
    public string Id { get; }
    public string Version { get; }
    public string DisplayName { get; }
    public DomainList<DomainPackConceptMetadata> Concepts { get; }
    public DomainList<DomainPackRuleProviderMetadata> RuleProviders { get; }
    public DomainList<PackConfigurationSchemaContribution> ConfigurationSchemaContributions { get; }
    public DomainList<DomainPackFeatureMetadata> Capabilities { get; }
    public DomainList<DomainPackFeatureMetadata> AuthoringMetadata { get; }

    public DomainPackMetadata(string id, string version, string displayName,
        IEnumerable<DomainPackConceptMetadata> concepts,
        IEnumerable<DomainPackRuleProviderMetadata> ruleProviders,
        IEnumerable<PackConfigurationSchemaContribution> configurationSchemaContributions,
        IEnumerable<DomainPackFeatureMetadata> capabilities,
        IEnumerable<DomainPackFeatureMetadata> authoringMetadata)
    {
        Id = Required(id, nameof(id));
        Version = Required(version, nameof(version));
        DisplayName = Required(displayName, nameof(displayName));
        Concepts = new DomainList<DomainPackConceptMetadata>((concepts ?? throw new ArgumentNullException(nameof(concepts)))
            .OrderBy(concept => concept.SemanticType, StringComparer.Ordinal));
        RuleProviders = new DomainList<DomainPackRuleProviderMetadata>((ruleProviders ?? throw new ArgumentNullException(nameof(ruleProviders)))
            .OrderBy(provider => provider.Id, StringComparer.Ordinal));
        ConfigurationSchemaContributions = new DomainList<PackConfigurationSchemaContribution>(
            (configurationSchemaContributions ?? throw new ArgumentNullException(nameof(configurationSchemaContributions)))
            .OrderBy(contribution => contribution.Id, StringComparer.Ordinal));
        Capabilities = new DomainList<DomainPackFeatureMetadata>((capabilities ?? throw new ArgumentNullException(nameof(capabilities)))
            .OrderBy(feature => feature.Id, StringComparer.Ordinal));
        AuthoringMetadata = new DomainList<DomainPackFeatureMetadata>((authoringMetadata ?? throw new ArgumentNullException(nameof(authoringMetadata)))
            .OrderBy(feature => feature.Id, StringComparer.Ordinal));

        RequireUnique(Concepts.Select(concept => concept.SemanticType), "concept semantic types");
        RequireUnique(RuleProviders.Select(provider => provider.Id), "rule provider IDs");
        RequireUnique(ConfigurationSchemaContributions.Select(contribution => contribution.Id), "configuration schema contribution IDs");
        RequireUnique(Capabilities.Select(feature => feature.Id), "capability IDs");
        RequireUnique(AuthoringMetadata.Select(feature => feature.Id), "authoring metadata IDs");
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();

    private static void RequireUnique(IEnumerable<string> values, string name)
    {
        var items = values.ToArray();
        if (items.Any(string.IsNullOrWhiteSpace) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException($"Pack metadata {name} must be non-empty and unique.");
    }
}

public sealed record DomainPackConceptMetadata
{
    public string SemanticType { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public DomainPackConceptMetadata(string semanticType, string displayName, string description)
    {
        SemanticType = Required(semanticType, nameof(semanticType));
        DisplayName = Required(displayName, nameof(displayName));
        Description = Required(description, nameof(description));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record DomainPackRuleMetadata
{
    public string Type { get; }
    public string Version { get; }
    public string Description { get; }

    public DomainPackRuleMetadata(string type, string version, string description)
    {
        Type = Required(type, nameof(type));
        Version = Required(version, nameof(version));
        Description = Required(description, nameof(description));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record DomainPackRuleProviderMetadata
{
    public string Id { get; }
    public string Version { get; }
    public DomainList<DomainPackRuleMetadata> Rules { get; }

    public DomainPackRuleProviderMetadata(string id, string version, IEnumerable<DomainPackRuleMetadata> rules)
    {
        Id = Required(id, nameof(id));
        Version = Required(version, nameof(version));
        Rules = new DomainList<DomainPackRuleMetadata>((rules ?? throw new ArgumentNullException(nameof(rules)))
            .OrderBy(rule => rule.Type, StringComparer.Ordinal));
        var types = Rules.Select(rule => rule.Type).ToArray();
        if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length)
            throw new ArgumentException("Pack rule provider metadata requires unique rule types.", nameof(rules));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record PackConfigurationSchemaContribution
{
    public string Id { get; }
    public string SchemaJson { get; }
    public DomainList<string> RuleTypes { get; }

    public PackConfigurationSchemaContribution(string id, string schemaJson, IEnumerable<string>? ruleTypes = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? throw new ArgumentException("Schema contribution ID is required.", nameof(id)) : id.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        using var document = JsonDocument.Parse(schemaJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Configuration schema contributions must be JSON objects.", nameof(schemaJson));
        SchemaJson = JsonSerializer.Serialize(document.RootElement);
        var types = (ruleTypes ?? []).Select(type => string.IsNullOrWhiteSpace(type)
            ? throw new ArgumentException("Schema contribution rule types must be non-empty.", nameof(ruleTypes))
            : type.Trim()).ToArray();
        if (types.Distinct(StringComparer.Ordinal).Count() != types.Length)
            throw new ArgumentException("Schema contribution rule types must be unique.", nameof(ruleTypes));
        RuleTypes = new DomainList<string>(types.Order(StringComparer.Ordinal));
    }
}

public sealed record DomainPackFeatureMetadata
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public DomainPackFeatureMetadata(string id, string displayName, string description)
    {
        Id = Required(id, nameof(id));
        DisplayName = Required(displayName, nameof(displayName));
        Description = Required(description, nameof(description));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}