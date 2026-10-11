using ProofShift.Domain;
using ProofShift.Configuration;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Justice;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class JusticePackTests
{
    [Fact]
    public void MetadataIsDeterministicAndUsesTheFrozenConceptSet()
    {
        var pack = new JusticePack();
        var other = new JusticePack();
        Assert.Equal("proofshift.justice", pack.Id);
        Assert.Equal("0.10.0", pack.Version);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(pack.Metadata), System.Text.Json.JsonSerializer.Serialize(other.Metadata));
        Assert.Equal("Justice.Case,Justice.CaseParty,Justice.Charge,Justice.Disposition,Justice.Document,Justice.Filing,Justice.Hearing,Justice.Person,Justice.Sentence",
            string.Join(',', pack.Metadata.Concepts.Select(concept => concept.SemanticType).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void JusticeRulesDeclareRequiredFieldsAndGlobalExecution()
    {
        var pack = new JusticePack();
        var reference = Assert.Single(pack.RuleFactories, factory => factory.Type == "justice-reference-integrity").Descriptor;
        Assert.Equal(VerificationPartitionExecution.Global, reference.PartitionExecution);
        Assert.Equal("referenceField,referenceKeyField",
            string.Join(',', reference.FieldRequirements.Select(requirement => requirement.OptionName).Order(StringComparer.Ordinal)));
        Assert.All(reference.Options.Where(option => option.Name is "sourceNode" or "semanticType" or "referenceField" or
            "referenceNode" or "referenceSemanticType" or "referenceKeyField"), option => Assert.True(option.Required));
        Assert.Contains(reference.FieldRequirements, requirement => requirement.OptionName == "referenceField" &&
            requirement.Side == VerificationFieldSide.Target && requirement.KeyRole == VerificationOrderingRole.Grouping);
        Assert.Contains(reference.FieldRequirements, requirement => requirement.OptionName == "referenceKeyField" &&
            requirement.Side == VerificationFieldSide.Target && requirement.KeyRole == VerificationOrderingRole.Lookup);

        Assert.All(pack.RuleFactories.Where(factory => factory.Type is "justice-attribute-equality" or "justice-event-order"),
            factory => Assert.Equal(VerificationPartitionExecution.Global, factory.Descriptor.PartitionExecution));
    }

    [Fact]
    public void JusticeOptionsFailClosedWhenUnknown()
    {
        var pack = new JusticePack();
        var registry = new PackRegistry([pack]).Resolve([new PackConfigurationDto(pack.Id, pack.Version)]);
        var definition = new VerificationRuleDefinition(new RuleId("justice-invalid"), "justice-reference-integrity", "1",
            EvidenceSeverity.Error, structuredOptions:
            [
                new("sourceNode", new StringValue("target-party")),
                new("semanticType", new StringValue("Justice.CaseParty")),
                new("referenceField", new StringValue("case_id")),
                new("referenceNode", new StringValue("target-case")),
                new("referenceSemanticType", new StringValue("Justice.Case")),
                new("referenceKeyField", new StringValue("case_id")),
                new("unrecognized", new StringValue("no"))
            ]);
        var error = Assert.Throws<VerificationRuleException>(() => registry.Resolve([definition]));
        Assert.Equal("PSRULE005", error.Code);
    }
}
