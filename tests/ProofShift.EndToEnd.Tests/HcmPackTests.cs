using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Hcm;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class HcmPackTests
{
    [Fact]
    public void MetadataIsDeterministicAndContainsOnlyTheRepresentativeConceptSet()
    {
        var pack = new HcmPack();
        var other = new HcmPack();
        Assert.Equal("proofshift.hcm", pack.Id);
        Assert.Equal("0.10.0", pack.Version);
        Assert.Equal(JsonSerializer.Serialize(pack.Metadata), JsonSerializer.Serialize(other.Metadata));
        Assert.Equal("HCM.BenefitEnrollment,HCM.Compensation,HCM.CostCenter,HCM.Employment,HCM.LeaveBalance,HCM.Organization,HCM.PayrollResult,HCM.Position,HCM.Worker",
            string.Join(',', pack.Metadata.Concepts.Select(concept => concept.SemanticType).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void HcmRuleDescriptorsDeclareFieldsAndGlobalExecution()
    {
        var pack = new HcmPack();
        Assert.All(pack.RuleFactories, factory => Assert.Equal(VerificationPartitionExecution.Global,
            factory.Descriptor.PartitionExecution));
        var interval = Assert.Single(new GenericVerificationRuleProvider().RuleFactories,
            factory => factory.Type == "effective-dated-interval").Descriptor;
        Assert.Equal("endField,identityField,ownerField,startField",
            string.Join(',', interval.FieldRequirements.Select(requirement => requirement.OptionName).Order(StringComparer.Ordinal)));
        Assert.Contains(interval.FieldRequirements, requirement => requirement.OptionName == "ownerField" &&
            requirement.KeyRole == VerificationOrderingRole.Grouping);
        Assert.Contains(interval.FieldRequirements, requirement => requirement.OptionName == "startField" &&
            requirement.KeyRole == VerificationOrderingRole.Ordering);
        var payroll = Assert.Single(pack.RuleFactories, factory => factory.Type == "hcm-payroll-reconciliation").Descriptor;
        Assert.Equal(7, payroll.FieldRequirements.Count);
        Assert.All(payroll.Options.Where(option => option.Kind == RuleOptionKind.FieldReference), option =>
            Assert.Contains(payroll.FieldRequirements, requirement => requirement.OptionName == option.Name));
    }

    [Fact]
    public void HcmPackResolvesExplicitlyAndRejectsUnknownOptions()
    {
        var pack = new HcmPack();
        var registry = new PackRegistry([pack]).Resolve([new PackConfigurationDto(pack.Id, pack.Version)]);
        var definition = new VerificationRuleDefinition(new RuleId("hcm-invalid"), "hcm-current-employment", "1",
            EvidenceSeverity.Error, structuredOptions:
            [
                new("targetNode", new StringValue("target-employment")),
                new("semanticType", new StringValue("HCM.Employment")),
                new("workerField", new StringValue("worker_id")),
                new("startField", new StringValue("effective_from")),
                new("endField", new StringValue("effective_to")),
                new("asOfDate", new StringValue("2025-01-01")),
                new("unsupported", new StringValue("no"))
            ]);
        var error = Assert.Throws<VerificationRuleException>(() => registry.Resolve([definition]));
        Assert.Equal("PSRULE005", error.Code);
    }
}
