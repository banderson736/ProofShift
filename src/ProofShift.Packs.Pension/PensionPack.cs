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
        Factory("pension-member-accounting", definition => new MemberAccountingRule(definition), [], []),
        Factory("pension-member-uniqueness", definition => new MemberUniquenessRule(definition), ["businessKey"],
            [Field("businessKey", VerificationFieldSide.Target, "Pension.Member", keyRole: VerificationOrderingRole.Grouping,
                defaultFields: ["member_id"])],
            VerificationPartitionExecution.PartitionLocal,
            new VerificationPartitionKeyDefinition(VerificationPartitionBasis.GroupingKey, VerificationArtifactRole.ActualTarget,
                ["businessKey"])),
        Factory("pension-member-attribute", definition => new AttributeComparisonRule(definition), ["attribute"],
            [new RuleFieldRequirement("attribute", VerificationFieldSide.Target,
                includeMappedTargetFieldsWhenUnset: true, targetNodeOption: "targetNode")],
            VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
            new VerificationPartitionKeyDefinition(VerificationPartitionBasis.ArtifactIdentity, VerificationArtifactRole.ActualTarget)),
        Factory("pension-member-presence", definition => new PensionMemberPresenceRule(definition), ["businessKey"],
            [Field("businessKey", VerificationFieldSide.Target, "Pension.Member", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["member_id"])],
            VerificationPartitionExecution.PartitionLocal,
            new VerificationPartitionKeyDefinition(VerificationPartitionBasis.GroupingKey, VerificationArtifactRole.ActualTarget,
                ["businessKey"])),
        Factory("pension-member-status", definition => new PensionMemberStatusRule(definition), ["businessKey", "attribute"],
            [Field("businessKey", VerificationFieldSide.Target, "Pension.Member", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["member_id"]),
             Field("attribute", VerificationFieldSide.Target, "Pension.Member", defaultFields: ["status"])],
            VerificationPartitionExecution.PartitionLocal,
            new VerificationPartitionKeyDefinition(VerificationPartitionBasis.GroupingKey, VerificationArtifactRole.ActualTarget,
                ["businessKey"])),
        Factory("pension-employment-timeline", definition => new EmploymentTimelineRule(definition),
            ["sourceMemberField", "targetMemberField", "targetSemanticType", "allowGaps", "sourceStartField", "sourceEndField",
             "sourceStatusField", "targetDateField", "targetEventField", "joinedEvent", "terminatedEvent", "reinstatedEvent"],
            [Field("sourceMemberField", VerificationFieldSide.Source, "Pension.Employment", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["member_id"]),
             Field("sourceStartField", VerificationFieldSide.Source, "Pension.Employment", defaultFields: ["effective_from"]),
             Field("sourceEndField", VerificationFieldSide.Source, "Pension.Employment", defaultFields: ["effective_to"]),
             Field("sourceStatusField", VerificationFieldSide.Source, "Pension.Employment", defaultFields: ["status"]),
                 Field("targetMemberField", VerificationFieldSide.Target, "Pension.Employment", keyRole: VerificationOrderingRole.Grouping,
                     defaultFields: ["participant_id"], semanticTypeOption: "targetSemanticType"),
                 Field("targetDateField", VerificationFieldSide.Target, "Pension.Employment", defaultFields: ["event_date"],
                     semanticTypeOption: "targetSemanticType"),
                 Field("targetEventField", VerificationFieldSide.Target, "Pension.Employment", defaultFields: ["event_code"],
                     semanticTypeOption: "targetSemanticType")]),
        Factory("pension-contribution-accounting", definition => new ContributionAccountingRule(definition),
            ["transactionField", "amountField", "compareFields", "tolerance"],
            [Field("transactionField", VerificationFieldSide.Target, "Pension.Contribution", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["transaction_id"]),
             Field("amountField", VerificationFieldSide.Target, "Pension.Contribution", defaultFields: ["amount"]),
             Field("compareFields", VerificationFieldSide.Target, "Pension.Contribution", defaultFields: ["member_id", "period", "category"])]),
        Factory("pension-contribution-total", definition => new ContributionTotalRule(definition),
            ["amountField", "groupBy", "tolerance"],
            [Field("amountField", VerificationFieldSide.Target, "Pension.Contribution", defaultFields: ["amount"]),
                 Field("groupBy", VerificationFieldSide.Target, "Pension.Contribution", keyRole: VerificationOrderingRole.Grouping,
                defaultFields: ["member_id", "period", "category"])]),
        Factory("pension-service-credit-total", definition => new ServiceCreditTotalRule(definition),
            ["amountField", "groupBy", "tolerance"],
            [Field("amountField", VerificationFieldSide.Target, "Pension.ServiceCredit", defaultFields: ["credit"]),
                 Field("groupBy", VerificationFieldSide.Target, "Pension.ServiceCredit", keyRole: VerificationOrderingRole.Grouping,
                defaultFields: ["member_id"])]),
        Factory("pension-beneficiary-relationship", definition => new BeneficiaryRelationshipRule(definition),
            ["beneficiaryField", "memberField", "relationshipField", "allocationField", "memberNode", "memberSemanticType", "memberBusinessKey", "allocationTolerance"],
            [Field("beneficiaryField", VerificationFieldSide.Target, "Pension.Beneficiary", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["beneficiary_id"]),
             Field("memberField", VerificationFieldSide.Target, "Pension.Beneficiary", defaultFields: ["member_id"]),
             Field("relationshipField", VerificationFieldSide.Target, "Pension.Beneficiary", defaultFields: ["relationship"]),
             Field("allocationField", VerificationFieldSide.Target, "Pension.Beneficiary", defaultFields: ["allocation"]),
                 Field("memberBusinessKey", VerificationFieldSide.Target, "Pension.Member", keyRole: VerificationOrderingRole.Lookup,
                     defaultFields: ["member_id"], semanticTypeOption: "memberSemanticType")]),
        Factory("pension-retirement-election", definition => new RetirementElectionRule(definition), ["electionField", "compareFields"],
            [Field("electionField", VerificationFieldSide.Target, "Pension.RetirementElection", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["election_id"]),
             Field("compareFields", VerificationFieldSide.Target, "Pension.RetirementElection",
                defaultFields: ["member_id", "election_code", "effective_date"])]),
        Factory("pension-benefit-payment", definition => new BenefitPaymentRule(definition),
            ["paymentField", "amountField", "compareFields", "tolerance"],
            [Field("paymentField", VerificationFieldSide.Target, "Pension.BenefitPayment", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["payment_id"]),
             Field("amountField", VerificationFieldSide.Target, "Pension.BenefitPayment", defaultFields: ["amount"]),
             Field("compareFields", VerificationFieldSide.Target, "Pension.BenefitPayment",
                defaultFields: ["participant_id", "payment_period", "paid_on"])]),
        Factory("pension-benefit-payment-total", definition => new BenefitPaymentTotalRule(definition),
            ["amountField", "groupBy", "tolerance"],
            [Field("amountField", VerificationFieldSide.Target, "Pension.BenefitPayment", defaultFields: ["amount"]),
                 Field("groupBy", VerificationFieldSide.Target, "Pension.BenefitPayment", keyRole: VerificationOrderingRole.Grouping,
                defaultFields: ["member_id", "period"])]),
        Factory("pension-document-accounting", definition => new DocumentAccountingRule(definition),
            ["documentField", "contentHashField", "missingCode"],
            [Field("documentField", VerificationFieldSide.Target, "Pension.Document", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["document_id"]),
             Field("contentHashField", VerificationFieldSide.Target, "Pension.Document", defaultFields: ["content_hash"])]),
        Factory("pension-document-relationship", definition => new DocumentRelationshipRule(definition), ["documentField", "memberField"],
            [Field("documentField", VerificationFieldSide.Target, "Pension.Document", keyRole: VerificationOrderingRole.Grouping, defaultFields: ["document_id"]),
             Field("memberField", VerificationFieldSide.Target, "Pension.Document", defaultFields: ["member_id"])]),
        Factory("pension-code-transformation", definition => new PensionCodeTransformationRule(definition), ["attribute", "businessKey"],
            [Field("attribute", VerificationFieldSide.Target, defaultFields: ["code"]),
             Field("businessKey", VerificationFieldSide.Target, keyRole: VerificationOrderingRole.Grouping, defaultFields: ["id"])])
    ];

    private static VerificationRuleFactory Factory(string type, Func<VerificationRuleDefinition, ProofShift.Verification.IVerificationRule> create,
        string[] optionNames, RuleFieldRequirement[] fieldRequirements,
        VerificationPartitionExecution partitionExecution = VerificationPartitionExecution.Global,
        VerificationPartitionKeyDefinition? partitionKey = null)
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
                create(new VerificationRuleDefinition(new RuleId("descriptor"), type, "1", EvidenceSeverity.Error)).Scope,
                options, fieldRequirements: fieldRequirements, partitionExecution: partitionExecution, partitionKey: partitionKey));
    }

    private static RuleFieldRequirement Field(string optionName, VerificationFieldSide side,
        string? defaultSemanticType = null, VerificationOrderingRole? keyRole = null,
        string[]? defaultFields = null, string semanticTypeOption = "semanticType") =>
        new(optionName, side, semanticTypeOption, defaultSemanticType, defaultFields, keyRole);
}
