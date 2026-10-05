using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Verification;

public sealed class VerificationRuleFactory
{
    private readonly Func<VerificationRuleDefinition, IVerificationRule> _create;
    public string Type { get; }
    public RuleDescriptor Descriptor { get; }

    public VerificationRuleFactory(string type, Func<VerificationRuleDefinition, IVerificationRule> create,
        RuleDescriptor? descriptor = null)
    {
        Type = Required(type, nameof(type));
        _create = create ?? throw new ArgumentNullException(nameof(create));
        Descriptor = descriptor ?? new RuleDescriptor(Type, "1", $"Verification rule {Type}.", VerificationScope.Entity);
    }

    public IVerificationRule Create(VerificationRuleDefinition definition)
    {
        try { Descriptor.Validate(definition); }
        catch (VerificationRuleException exception)
        {
            throw new VerificationRuleException(exception.Code, exception.Message, definition.SourceLocation);
        }
        var rule = _create(definition) ?? throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
            "Rule factory returned no implementation.");
        if (rule.Id != definition.Id || rule.Version != definition.Version)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
                "Rule factory implementation identity/version does not match its configuration.");
        return rule;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public interface IVerificationRuleProvider
{
    string Id { get; }
    string Version { get; }
    IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; }
}

public sealed record VerificationRuleSet
{
    public DomainList<VerificationRuleDefinition> Definitions { get; }
    public DomainList<IVerificationRule> Rules { get; }
    public DomainList<VerificationOrderingKey> RequiredOrderingKeys { get; }
    public IReadOnlyDictionary<string, string> ProviderVersions { get; }
    public string Fingerprint { get; }

    internal VerificationRuleSet(IEnumerable<VerificationRuleDefinition> definitions,
        IEnumerable<IVerificationRule> rules, IEnumerable<KeyValuePair<string, string>> providerVersions, string fingerprint)
    {
        Definitions = new DomainList<VerificationRuleDefinition>(definitions.OrderBy(item => item.Id.Value, StringComparer.Ordinal));
        Rules = new DomainList<IVerificationRule>(rules.OrderBy(item => item.Id.Value, StringComparer.Ordinal));
        RequiredOrderingKeys = new DomainList<VerificationOrderingKey>(Rules.SelectMany(rule => rule.RequiredOrderingKeys)
            .Distinct().OrderBy(key => key.SemanticType, StringComparer.Ordinal)
            .ThenBy(key => key.Field, StringComparer.Ordinal).ThenBy(key => key.Role));
        ProviderVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(providerVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
        Fingerprint = fingerprint;
    }
}

public sealed class VerificationRuleRegistry
{
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<string, VerificationRuleFactory> _factories;
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<string, string> _providerVersions;
    public IReadOnlyCollection<RuleDescriptor> Descriptors => _factories.Values.Select(factory => factory.Descriptor)
        .OrderBy(descriptor => descriptor.Type, StringComparer.Ordinal).ToArray();

    public VerificationRuleRegistry(IEnumerable<IVerificationRuleProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var factories = new Dictionary<string, VerificationRuleFactory>(StringComparer.Ordinal);
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in providers.OrderBy(provider => provider.Id, StringComparer.Ordinal))
        {
            if (!versions.TryAdd(provider.Id, provider.Version))
                throw new ArgumentException($"Verification rule provider '{provider.Id}' is registered more than once.", nameof(providers));
            foreach (var factory in provider.RuleFactories.OrderBy(factory => factory.Type, StringComparer.Ordinal))
            {
                if (!factories.TryAdd(factory.Type, factory))
                    throw new ArgumentException($"Verification rule type '{factory.Type}' is registered more than once.", nameof(providers));
            }
        }

        _factories = new System.Collections.ObjectModel.ReadOnlyDictionary<string, VerificationRuleFactory>(factories);
        _providerVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(versions);
    }

    public VerificationRuleSet Resolve(IEnumerable<VerificationRuleDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var configured = definitions.OrderBy(definition => definition.Id.Value, StringComparer.Ordinal).ToArray();
        if (configured.Length == 0)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Verification rule configuration is empty.");
        if (configured.Select(definition => definition.Id).Distinct().Count() != configured.Length)
            throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType, "Verification rule IDs must be unique.");

        var rules = new List<IVerificationRule>(configured.Length);
        foreach (var definition in configured)
        {
            if (!_factories.TryGetValue(definition.Type, out var factory))
                throw new VerificationRuleException(VerificationIssueCodes.UnknownRuleType,
                    $"Verification rule type '{definition.Type}' is not registered.", definition.SourceLocation);
            rules.Add(factory.Create(definition));
        }

        return new VerificationRuleSet(configured, rules, _providerVersions, Fingerprint(configured, _providerVersions));
    }

    private static string Fingerprint(IEnumerable<VerificationRuleDefinition> definitions,
        IReadOnlyDictionary<string, string> providerVersions)
    {
        var canonical = new StringBuilder();
        Append(canonical, "proofshift-verification-rule-set-v1");
        foreach (var provider in providerVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(canonical, provider.Key);
            Append(canonical, provider.Value);
        }
        foreach (var definition in definitions.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            Append(canonical, definition.Id.Value);
            Append(canonical, definition.Type);
            Append(canonical, definition.Version);
            Append(canonical, definition.Severity.ToString());
            if (definition.UsesStructuredOptions)
            {
                Append(canonical, "structured-options-v2");
                foreach (var option in definition.StructuredOptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    Append(canonical, option.Key);
                    Append(canonical, GraphTargetIdentity.CanonicalValue(option.Value));
                }
            }
            foreach (var option in definition.Options.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Append(canonical, option.Key);
                Append(canonical, option.Value);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}

public sealed class VerificationRuleException(string code, string message, RuleSourceLocation? location = null) : Exception(message)
{
    public string Code { get; } = code;
    public RuleSourceLocation? Location { get; } = location;
}

