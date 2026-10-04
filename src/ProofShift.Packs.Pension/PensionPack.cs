using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Packs.Pension;

public sealed class PensionPack : IDomainPack
{
    public string Id => "proofshift.pension";
    public string Version => "0.9.0";

    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
    [
        new("pension-member-accounting", definition => new MemberAccountingRule(definition)),
        new("pension-member-uniqueness", definition => new MemberUniquenessRule(definition)),
        new("pension-member-attribute", definition => new AttributeComparisonRule(definition)),
        new("pension-member-presence", definition => new PensionMemberPresenceRule(definition)),
        new("pension-member-status", definition => new PensionMemberStatusRule(definition)),
        new("pension-employment-timeline", definition => new EmploymentTimelineRule(definition)),
        new("pension-contribution-accounting", definition => new ContributionAccountingRule(definition)),
        new("pension-contribution-total", definition => new ContributionTotalRule(definition)),
        new("pension-service-credit-total", definition => new ServiceCreditTotalRule(definition)),
        new("pension-beneficiary-relationship", definition => new BeneficiaryRelationshipRule(definition)),
        new("pension-retirement-election", definition => new RetirementElectionRule(definition)),
        new("pension-benefit-payment", definition => new BenefitPaymentRule(definition)),
        new("pension-benefit-payment-total", definition => new BenefitPaymentTotalRule(definition)),
        new("pension-document-accounting", definition => new DocumentAccountingRule(definition)),
        new("pension-document-relationship", definition => new DocumentRelationshipRule(definition)),
        new("pension-code-transformation", definition => new PensionCodeTransformationRule(definition))
    ];
}
