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
}