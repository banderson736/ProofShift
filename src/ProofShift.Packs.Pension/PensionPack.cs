using ProofShift.Packs.Abstractions;
using ProofShift.Verification;
using ProofShift.Domain;

namespace ProofShift.Packs.Pension;

public sealed class PensionPack : IDomainPack
{
    private static readonly string[] ScopeOptionNames = ["sourceNode", "targetNode", "semanticType"];
    public string Id => "proofshift.pension";
    public string Version => "0.9.0";

    public IReadOnlyCollection<VerificationRuleFactory> RuleFactories { get; } =
    [
        Factory("pension-member-accounting", definition => new MemberAccountingRule(definition), "businessKey"),
        Factory("pension-member-uniqueness", definition => new MemberUniquenessRule(definition), "businessKey"),
        Factory("pension-member-attribute", definition => new AttributeComparisonRule(definition), "attribute"),
        Factory("pension-member-presence", definition => new PensionMemberPresenceRule(definition), "businessKey"),
        Factory("pension-member-status", definition => new PensionMemberStatusRule(definition), "businessKey", "attribute"),
        Factory("pension-employment-timeline", definition => new EmploymentTimelineRule(definition),
            "sourceMemberField", "targetMemberField", "targetSemanticType", "allowGaps", "sourceStartField", "sourceEndField",
            "sourceStatusField", "targetDateField", "targetEventField", "joinedEvent", "terminatedEvent", "reinstatedEvent"),
        Factory("pension-contribution-accounting", definition => new ContributionAccountingRule(definition), "transactionField", "amountField", "compareFields", "tolerance"),
        Factory("pension-contribution-total", definition => new ContributionTotalRule(definition), "amountField", "groupBy", "tolerance"),
        Factory("pension-service-credit-total", definition => new ServiceCreditTotalRule(definition), "amountField", "groupBy", "tolerance"),
        Factory("pension-beneficiary-relationship", definition => new BeneficiaryRelationshipRule(definition),
            "beneficiaryField", "memberField", "relationshipField", "allocationField", "memberNode", "memberSemanticType", "memberBusinessKey", "allocationTolerance"),
        Factory("pension-retirement-election", definition => new RetirementElectionRule(definition), "electionField", "compareFields"),
        Factory("pension-benefit-payment", definition => new BenefitPaymentRule(definition), "paymentField", "amountField", "compareFields", "tolerance"),
        Factory("pension-benefit-payment-total", definition => new BenefitPaymentTotalRule(definition), "amountField", "groupBy", "tolerance"),
        Factory("pension-document-accounting", definition => new DocumentAccountingRule(definition), "documentField", "contentHashField", "missingCode"),
        Factory("pension-document-relationship", definition => new DocumentRelationshipRule(definition), "documentField", "memberField"),
        Factory("pension-code-transformation", definition => new PensionCodeTransformationRule(definition), "attribute", "businessKey")
    ];

    private static VerificationRuleFactory Factory(string type, Func<VerificationRuleDefinition, ProofShift.Verification.IVerificationRule> create,
        params string[] optionNames)
    {
        var names = ScopeOptionNames.Concat(optionNames).Distinct(StringComparer.Ordinal);
        var options = names.Select(name => name switch
        {
            "groupBy" or "compareFields" => new RuleOptionDescriptor(name, RuleOptionKind.Sequence,
                "Ordered field references; use a YAML sequence.", ItemKind: RuleOptionKind.FieldReference),
            "tolerance" or "allocationTolerance" => new RuleOptionDescriptor(name, RuleOptionKind.Number, "Absolute decimal comparison tolerance.",
                Default: new DecimalValue(0.01m), Minimum: 0),
            "allowGaps" => new RuleOptionDescriptor(name, RuleOptionKind.Logical, "Allow gaps in the employment timeline.", Default: new BooleanValue(false)),
            _ when name.EndsWith("SemanticType", StringComparison.Ordinal) || name == "semanticType" =>
                new RuleOptionDescriptor(name, RuleOptionKind.SemanticTypeReference, "Explicit pension semantic type."),
            _ when name.EndsWith("Field", StringComparison.Ordinal) || name.EndsWith("Key", StringComparison.Ordinal) || name == "attribute" =>
                new RuleOptionDescriptor(name, RuleOptionKind.FieldReference, "Configured field reference."),
            _ => new RuleOptionDescriptor(name, RuleOptionKind.Text, "Explicit graph scope or configured code.")
        });
        return new VerificationRuleFactory(type, create,
            new RuleDescriptor(type, "1", $"Pension assurance: {type[8..].Replace('-', ' ')}.",
                create(new VerificationRuleDefinition(new RuleId("descriptor"), type, "1", EvidenceSeverity.Error)).Scope, options));
    }
}
