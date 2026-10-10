using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationRuleConfigurationTests
{
    [Fact]
    public void StructuredRuleDocumentRetainsNestedListsAndNumericTypes()
    {
        ConfigurationScalarNode Text(string value) => new(ConfigurationScalarKind.Text, value);
        ConfigurationMappingNode Map(params (string Key, ConfigurationDocumentNode Value)[] pairs) =>
            new(pairs.Select(pair => new KeyValuePair<string, ConfigurationDocumentNode>(pair.Key, pair.Value)));
        var document = Map(("version", new ConfigurationScalarNode(ConfigurationScalarKind.Number, "2")),
            ("rules", Map(("comparison", Map(("type", Text("attribute-comparison")),
                ("groupBy", new ConfigurationSequenceNode([Text("member_id"), Text("period")])),
                ("measure", Map(("field", Text("amount")), ("operation", Text("sum")))),
                ("tolerance", new ConfigurationScalarNode(ConfigurationScalarKind.Number, "0.01")),
                ("required", new ConfigurationScalarNode(ConfigurationScalarKind.Boolean, "true")))))));
        var configured = new LoadedProjectConfiguration(
            new RootConfigurationDto(1, new ProjectConfigurationDto("project-key", "Synthetic"), null, [], null, "rules.yaml", null),
            [], [], [new ReferencedConfigurationFile(ConfigurationFileKind.VerificationRules, "rules.yaml", "version: 2", document)],
            "canonical", new string('a', 64));
        var definition = Assert.Single(VerificationRuleConfigurationLoader.Load(configured));
        Assert.True(definition.UsesStructuredOptions);
        Assert.Empty(definition.Options);
        var groups = Assert.IsType<CollectionValue>(definition.StructuredOptions["groupBy"]);
        Assert.Equal(["member_id", "period"], groups.Values.Cast<StringValue>().Select(value => value.Value));
        Assert.Equal(0.01m, Assert.IsType<DecimalValue>(definition.StructuredOptions["tolerance"]).Value);
        Assert.True(Assert.IsType<BooleanValue>(definition.StructuredOptions["required"]).Value);
        Assert.IsType<ObjectValue>(definition.StructuredOptions["measure"]);
    }

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
        var changed = new VerificationRuleDefinition(new RuleId("status"), "attribute-comparison", "1",
            EvidenceSeverity.Warning, [new KeyValuePair<string, string>("attribute", "status")]);

        Assert.Equal(registry.Resolve([first]).Fingerprint, registry.Resolve([same]).Fingerprint);
        Assert.NotEqual(registry.Resolve([first]).Fingerprint, registry.Resolve([changed]).Fingerprint);
    }

    [Fact]
    public void DescriptorsRejectUnavailableVersionsUnknownOptionsAndIncorrectTypesBeforeRuleCreation()
    {
        var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]);
        var version = new VerificationRuleDefinition(new RuleId("comparison"), "attribute-comparison", "2", EvidenceSeverity.Error);
        Assert.Equal("PSRULE002", Assert.Throws<VerificationRuleException>(() => registry.Resolve([version])).Code);
        var unknown = new VerificationRuleDefinition(new RuleId("comparison"), "attribute-comparison", "1", EvidenceSeverity.Error,
            structuredOptions: [new("attribte", new StringValue("status"))]);
        Assert.Equal("PSRULE005", Assert.Throws<VerificationRuleException>(() => registry.Resolve([unknown])).Code);
        var wrongType = new VerificationRuleDefinition(new RuleId("comparison"), "attribute-comparison", "1", EvidenceSeverity.Error,
            structuredOptions: [new("attribute", new BooleanValue(true))]);
        Assert.Equal("PSRULE003", Assert.Throws<VerificationRuleException>(() => registry.Resolve([wrongType])).Code);
        var valid = new VerificationRuleDefinition(new RuleId("comparison"), "attribute-comparison", "1", EvidenceSeverity.Error,
            structuredOptions: [new("attribute", new StringValue("status"))]);
        Assert.Single(registry.Resolve([valid]).Rules);
        Assert.Contains(registry.Descriptors, descriptor => descriptor.Type == "attribute-comparison" && descriptor.Options.Count == 3);
    }

    [Fact]
    public void GenericRuleCapabilitiesAreExplicitAndFailClosed()
    {
        var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]);
        var rules = registry.Resolve([
            new VerificationRuleDefinition(new RuleId("accounting"), "source-artifact-accounting", "1", EvidenceSeverity.Error),
            new VerificationRuleDefinition(new RuleId("presence"), "target-presence", "1", EvidenceSeverity.Error),
            new VerificationRuleDefinition(new RuleId("unexpected"), "unexpected-target", "1", EvidenceSeverity.Error),
            new VerificationRuleDefinition(new RuleId("attributes"), "attribute-comparison", "1", EvidenceSeverity.Error,
                structuredOptions: [new("attribute", new StringValue("status"))]),
            new VerificationRuleDefinition(new RuleId("unique"), "entity-uniqueness", "1", EvidenceSeverity.Error)
        ]);
        var plan = VerificationExecutionPlan.Create(rules);

        Assert.Equal(VerificationPartitionExecution.Global, Assert.Single(plan.Rules, rule => rule.Rule.Id.Value == "accounting").PartitionExecution);
        Assert.Equal(VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
            Assert.Single(plan.Rules, rule => rule.Rule.Id.Value == "presence").PartitionExecution);
        Assert.Equal(VerificationPartitionExecution.PartitionLocal,
            Assert.Single(plan.Rules, rule => rule.Rule.Id.Value == "unexpected").PartitionExecution);
        Assert.Equal(VerificationPartitionExecution.PartitionPartialWithGlobalMerge,
            Assert.Single(plan.Rules, rule => rule.Rule.Id.Value == "attributes").PartitionExecution);
        Assert.Equal(VerificationPartitionExecution.PartitionLocal,
            Assert.Single(plan.Rules, rule => rule.Rule.Id.Value == "unique").PartitionExecution);
        Assert.All(plan.Rules.Where(rule => rule.PartitionExecution != VerificationPartitionExecution.Global),
            rule => Assert.Equal(VerificationPartitionBasis.ArtifactIdentity, rule.PartitionKey!.Basis));
    }

    [Fact]
    public void GeneratedRuleSchemaUsesDescriptorTypesAndRejectsUnregisteredOptionsByConstruction()
    {
        var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]);
        var schema = RuleConfigurationSchema.Generate(registry.Descriptors);
        var same = RuleConfigurationSchema.Generate(registry.Descriptors.Reverse());
        Assert.Equal(schema.ToJsonString(), same.ToJsonString());
        var alternatives = schema["properties"]!["rules"]!["additionalProperties"]!["oneOf"]!.AsArray();
        var comparison = alternatives.Single(node => node!["properties"]!["type"]!["const"]!.GetValue<string>() == "attribute-comparison")!;
        Assert.False(comparison["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("string", comparison["properties"]!["attribute"]!["type"]!.GetValue<string>());
        Assert.Equal(1, comparison["properties"]!["attribute"]!["minLength"]!.GetValue<int>());
        Assert.Equal(2, schema["properties"]!["version"]!["const"]!.GetValue<int>());
    }

    [Fact]
    public void StructuredRuleReferencesReportUnknownSemanticAndFieldWithConfigurationLocation()
    {
        var source = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source", MigrationNodeType.Source,
            "Generic.Record", new SystemId("legacy"), new StorageEndpointId("csv"), new ArtifactSelector("csv", identityFields: ["ID"]));
        var target = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "target", MigrationNodeType.Target,
            "Generic.Record", new SystemId("shadow"), new StorageEndpointId("postgres"), new ArtifactSelector("table", identityFields: ["id"]));
        var edge = new MigrationEdge(new MigrationEdgeId(Guid.NewGuid()), "mapping", [source.Id], [target.Id],
            new MigrationOperation(MigrationOperationType.Transform, fields: [new TransformationFieldDefinition("value", "VALUE")]),
            "1", new RecoveryDefinition(RecoveryMode.Reverse));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source, target], [edge], new string('a', 64), "test");
        var definition = new VerificationRuleDefinition(new RuleId("comparison"), "attribute-comparison", "1", EvidenceSeverity.Error,
            structuredOptions: [new("targetNode", new StringValue("target")), new("semanticType", new StringValue("Generic.Unknown")),
                new("attribute", new StringValue("valu"))])
        {
            SourceLocation = new RuleSourceLocation("rules/custom.yaml", "rules.comparison", 7, 3)
        };
        var registry = new VerificationRuleRegistry([new GenericVerificationRuleProvider()]);
        _ = registry.Resolve([definition]);
        var issues = RuleReferenceValidator.Validate([definition], registry, graph);
        Assert.Equal(["PSRULE011", "PSRULE009"], issues.Select(issue => issue.Code));
        Assert.All(issues, issue =>
        {
            Assert.Equal("rules/custom.yaml", issue.File);
            Assert.Equal(7, issue.Line);
            Assert.Equal(3, issue.Column);
            Assert.StartsWith("rules.comparison.", issue.Path);
        });
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
