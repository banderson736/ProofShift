using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationRuleConfigurationTests
{
    [Fact]
    public void RuleConfigurationUsesGenericDocumentTreeAndPreservesSeverityAndOptions()
    {
        var configured = new LoadedProjectConfiguration(
            new RootConfigurationDto(1, new ProjectConfigurationDto("project-key", "Synthetic"), null, [], null, "rules.yaml", null),
            [], [], [RuleFile()], "canonical", new string('a', 64));

        var definition = Assert.Single(VerificationRuleConfigurationLoader.Load(configured));

        Assert.Equal("member-status", definition.Id.Value);
        Assert.Equal("attribute-comparison", definition.Type);
        Assert.Equal("1", definition.Version);
        Assert.Equal(EvidenceSeverity.Critical, definition.Severity);
        Assert.Equal("status", definition.Options["attribute"]);
        Assert.Equal("Pension.Member", definition.Options["semanticType"]);
    }

    [Fact]
    public void RuleSetFingerprintIsDeterministicAndChangesWhenRuleSemanticsChange()
    {
        var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]);
        var first = new VerificationRuleDefinition(new RuleId("status"), "attribute-comparison", "1",
            EvidenceSeverity.Critical, [new KeyValuePair<string, string>("attribute", "status")]);
        var same = new VerificationRuleDefinition(new RuleId("status"), "attribute-comparison", "1",
            EvidenceSeverity.Critical, [new KeyValuePair<string, string>("attribute", "status")]);
        var changed = new VerificationRuleDefinition(new RuleId("status"), "attribute-comparison", "2",
            EvidenceSeverity.Critical, [new KeyValuePair<string, string>("attribute", "status")]);

        Assert.Equal(registry.Resolve([first]).Fingerprint, registry.Resolve([same]).Fingerprint);
        Assert.NotEqual(registry.Resolve([first]).Fingerprint, registry.Resolve([changed]).Fingerprint);
    }

    private static ReferencedConfigurationFile RuleFile()
    {
        ConfigurationScalarNode Scalar(string value) => new(ConfigurationScalarKind.Text, value);
        ConfigurationMappingNode Mapping(params (string Key, ConfigurationDocumentNode Value)[] pairs) =>
            new(pairs.Select(pair => new KeyValuePair<string, ConfigurationDocumentNode>(pair.Key, pair.Value)));
        var rule = Mapping(
            ("type", Scalar("attribute-comparison")),
            ("version", Scalar("1")),
            ("severity", Scalar("critical")),
            ("attribute", Scalar("status")),
            ("semanticType", Scalar("Pension.Member")));
        var rules = Mapping(("member-status", rule));
        var root = Mapping(("rules", rules));
        return new ReferencedConfigurationFile(ConfigurationFileKind.VerificationRules, "rules.yaml", "rules: {}", root);
    }
}
