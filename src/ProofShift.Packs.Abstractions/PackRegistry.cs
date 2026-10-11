using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Verification;

namespace ProofShift.Packs.Abstractions;

public sealed class PackRegistry
{
    private readonly DomainList<IDomainPack> _packs;

    public PackRegistry(IEnumerable<IDomainPack> packs)
    {
        _packs = new DomainList<IDomainPack>(packs.OrderBy(pack => pack.Id, StringComparer.Ordinal));
        if (_packs.Select(pack => pack.Id).Distinct(StringComparer.Ordinal).Count() != _packs.Count)
            throw new ArgumentException("Installed pack IDs must be unique.", nameof(packs));
        foreach (var pack in _packs) ValidateMetadata(pack);
    }

    public IReadOnlyCollection<IDomainPack> Installed => _packs;

    public VerificationRuleRegistry Resolve(IEnumerable<PackConfigurationDto> selections)
    {
        var providers = new List<IVerificationRuleProvider> { new GenericVerificationRuleProvider() };
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            var matches = _packs.Where(pack => pack.Id == selection.Id || pack.Id[(pack.Id.LastIndexOf('.') + 1)..] == selection.Id).ToArray();
            if (matches.Length != 1)
                throw new VerificationRuleException("PSPACK001", "The configured pack ID is unavailable or ambiguous.");
            var pack = matches[0];
            if (selection.Version != pack.Version)
                throw new VerificationRuleException("PSPACK002", $"Pack '{pack.Id}' requires installed version '{pack.Version}'. No version substitution is permitted.");
            if (!selected.Add(pack.Id))
                throw new VerificationRuleException("PSPACK003", "A pack is selected more than once.");
            providers.Add(pack);
        }
        return new VerificationRuleRegistry(providers);
    }

    private static void ValidateMetadata(IDomainPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack.Metadata);
        if (pack.Metadata.Id != pack.Id || pack.Metadata.Version != pack.Version)
            throw new ArgumentException($"Pack '{pack.Id}' metadata identity/version does not match its provider.", nameof(pack));

        var provider = pack.Metadata.RuleProviders.SingleOrDefault(item => item.Id == pack.Id);
        if (pack.Metadata.Concepts.Count == 0 || pack.Metadata.RuleProviders.Count != 1 ||
            provider is null || provider.Version != pack.Version)
            throw new ArgumentException($"Pack '{pack.Id}' metadata must describe its installed rule provider and version.", nameof(pack));

        var declaredRules = provider.Rules.Select(rule => (rule.Type, rule.Version, rule.Description)).ToArray();
        var actualRules = pack.RuleFactories.Select(factory =>
            (factory.Type, factory.Descriptor.Version, factory.Descriptor.Description)).OrderBy(rule => rule.Type, StringComparer.Ordinal).ToArray();
        if (!declaredRules.SequenceEqual(actualRules))
            throw new ArgumentException($"Pack '{pack.Id}' rule metadata does not match its provider descriptors.", nameof(pack));

        var schemaRuleTypes = pack.Metadata.ConfigurationSchemaContributions.SelectMany(contribution => contribution.RuleTypes).ToArray();
        var providerRuleTypes = actualRules.Select(rule => rule.Type).ToArray();
        if (schemaRuleTypes.Distinct(StringComparer.Ordinal).Count() != schemaRuleTypes.Length ||
            !schemaRuleTypes.Order(StringComparer.Ordinal).SequenceEqual(providerRuleTypes, StringComparer.Ordinal))
            throw new ArgumentException($"Pack '{pack.Id}' configuration schemas do not cover its rule provider exactly once.", nameof(pack));

        if (pack.Metadata.ConfigurationSchemaContributions.Count == 0 ||
            pack.Metadata.Capabilities.Count == 0 || pack.Metadata.AuthoringMetadata.Count == 0)
            throw new ArgumentException($"Pack '{pack.Id}' must declare configuration schema, capabilities, and authoring metadata.", nameof(pack));
    }
}