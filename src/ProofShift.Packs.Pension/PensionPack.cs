using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Packs.Pension;

public sealed class PensionPack : IDomainPack
{
    public string Id => "proofshift.pension";
    public string Version => "0.1";

    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
    [
        new("pension-member-accounting", definition => new MemberAccountingRule(definition)),
        new("pension-member-uniqueness", definition => new EntityUniquenessRule(definition)),
        new("pension-member-attribute", definition => new AttributeComparisonRule(definition))
    ];
}
