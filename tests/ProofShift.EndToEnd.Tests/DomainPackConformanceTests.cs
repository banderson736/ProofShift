using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Packs.Abstractions;
using ProofShift.Packs.Pension;
using ProofShift.Packs.Utility;
using ProofShift.Packs.Justice;
using ProofShift.Packs.Hcm;
using ProofShift.Packs.Fhir;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class DomainPackConformanceTests
{
    private static readonly string[] GenericAssemblies =
    [
        "ProofShift.Domain",
        "ProofShift.Configuration",
        "ProofShift.Graph",
        "ProofShift.Engine",
        "ProofShift.Verification",
        "ProofShift.Evidence",
        "ProofShift.Recovery",
        "ProofShift.Reporting",
        "ProofShift.Connectors.Abstractions"
    ];

    [Fact]
    public void PensionPackSatisfiesTheReusableConformanceContract()
    {
        DomainPackConformanceHarness.AssertConforms(() => new PensionPack());
    }

    [Fact]
    public void UtilityPackSatisfiesTheReusableConformanceContract()
    {
        DomainPackConformanceHarness.AssertConforms(() => new UtilityPack());
    }

    [Fact]
    public void JusticePackSatisfiesTheReusableConformanceContract()
    {
        DomainPackConformanceHarness.AssertConforms(() => new JusticePack());
    }

    [Fact]
    public void HcmPackSatisfiesTheReusableConformanceContract()
    {
        DomainPackConformanceHarness.AssertConforms(() => new HcmPack());
    }

    [Fact]
    public void FhirPackSatisfiesTheReusableConformanceContract()
    {
        DomainPackConformanceHarness.AssertConforms(() => new FhirPack());
    }

    [Fact]
    public void PackRegistryRejectsMetadataThatDoesNotMatchTheInstalledProvider()
    {
        var pack = new PensionPack();
        var metadata = pack.Metadata;
        var inconsistent = new MetadataMismatchPack(pack, new DomainPackMetadata(metadata.Id, metadata.Version,
            metadata.DisplayName, metadata.Concepts,
            [new DomainPackRuleProviderMetadata(pack.Id, pack.Version,
                pack.RuleFactories.Select(factory => new DomainPackRuleMetadata(factory.Type, "unavailable",
                    factory.Descriptor.Description)))],
            metadata.ConfigurationSchemaContributions, metadata.Capabilities, metadata.AuthoringMetadata));

        Assert.Throws<ArgumentException>(() => new PackRegistry([inconsistent]));
    }

    [Fact]
    public void PackRegistryRejectsConfigurationSchemasThatOmitProviderRules()
    {
        var pack = new PensionPack();
        var metadata = pack.Metadata;
        var contribution = Assert.Single(metadata.ConfigurationSchemaContributions);
        var incomplete = new DomainPackMetadata(metadata.Id, metadata.Version, metadata.DisplayName,
            metadata.Concepts, metadata.RuleProviders,
            [new PackConfigurationSchemaContribution(contribution.Id, contribution.SchemaJson,
                contribution.RuleTypes.Take(contribution.RuleTypes.Count - 1))],
            metadata.Capabilities, metadata.AuthoringMetadata);
        var inconsistent = new MetadataMismatchPack(pack, incomplete);

        Assert.Throws<ArgumentException>(() => new PackRegistry([inconsistent]));
    }

    [Fact]
    public void GenericAssembliesDoNotReferenceOrEmbedInstalledPackSemantics()
    {
        var installedPacks = InstalledPacks();
        var packTerms = installedPacks.SelectMany(pack => pack.Metadata.Concepts.Select(concept => concept.SemanticType)
            .Concat(pack.Metadata.RuleProviders.SelectMany(provider => provider.Rules.Select(rule => rule.Type)))
            .Concat(pack.RuleFactories.SelectMany(factory => factory.Descriptor.FieldRequirements
                .SelectMany(requirement => requirement.DefaultFields).Where(field => field.Contains('_'))))
            .Append(pack.Id)
            .Append(pack.Id[(pack.Id.LastIndexOf('.') + 1)..]))
            .Distinct(StringComparer.Ordinal)
            .Where(term => term.Length >= 5)
            .ToArray();

        foreach (var assemblyName in GenericAssemblies)
        {
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name?.StartsWith("ProofShift.Packs.", StringComparison.Ordinal) == true);

            var assemblyBytes = File.ReadAllBytes(assembly.Location);
            foreach (var term in packTerms)
            {
                Assert.False(ContainsBytes(assemblyBytes, Encoding.UTF8.GetBytes(term)),
                    $"Generic assembly '{assemblyName}' embeds pack term '{term}'.");
                Assert.False(ContainsBytes(assemblyBytes, Encoding.Unicode.GetBytes(term)),
                    $"Generic assembly '{assemblyName}' embeds pack term '{term}'.");
            }
        }

        var concretePacks = installedPacks.Select(pack => pack.GetType().Assembly).Distinct().ToArray();
        var concretePackNames = concretePacks.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var assembly in concretePacks)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name is { } name && concretePackNames.Contains(name) && name != assembly.GetName().Name);
        }
    }

    private static IDomainPack[] InstalledPacks() => [new PensionPack(), new UtilityPack(), new JusticePack(), new HcmPack(), new FhirPack()];

    private static bool ContainsBytes(byte[] source, byte[] value)
    {
        if (value.Length == 0 || value.Length > source.Length) return false;
        return source.AsSpan().IndexOf(value) >= 0;
    }

    private sealed class MetadataMismatchPack(IDomainPack inner, DomainPackMetadata metadata) : IDomainPack
    {
        public string Id => inner.Id;
        public string Version => inner.Version;
        public IReadOnlyCollection<VerificationRuleFactory> RuleFactories => inner.RuleFactories;
        public DomainPackMetadata Metadata { get; } = metadata;
    }
}

internal static class DomainPackConformanceHarness
{
    private static readonly Regex SecretOptionName = new(
        "secret|password|token|credential|private.?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void AssertConforms(Func<IDomainPack> createPack)
    {
        var pack = createPack();
        var secondPack = createPack();
        var metadata = pack.Metadata;
        Assert.Equal(JsonSerializer.Serialize(metadata), JsonSerializer.Serialize(secondPack.Metadata));
        Assert.Equal(pack.Id, metadata.Id);
        Assert.Equal(pack.Version, metadata.Version);
        Assert.False(string.IsNullOrWhiteSpace(metadata.DisplayName));
        Assert.NotEmpty(metadata.Concepts);
        Assert.NotEmpty(metadata.RuleProviders);
        Assert.NotEmpty(metadata.ConfigurationSchemaContributions);
        Assert.NotEmpty(metadata.Capabilities);
        Assert.NotEmpty(metadata.AuthoringMetadata);

        var providerMetadata = Assert.Single(metadata.RuleProviders);
        Assert.Equal(pack.Id, providerMetadata.Id);
        Assert.Equal(pack.Version, providerMetadata.Version);
        var factories = pack.RuleFactories.OrderBy(factory => factory.Type, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(factories);
        Assert.Equal(factories.Select(factory => factory.Type), providerMetadata.Rules.Select(rule => rule.Type));
        Assert.Equal(factories.Select(factory => factory.Descriptor.Version), providerMetadata.Rules.Select(rule => rule.Version));
        Assert.All(factories, factory => AssertDescriptorComplete(factory.Descriptor, metadata));

        var registry = new PackRegistry([pack]);
        var rules = registry.Resolve([new PackConfigurationDto(pack.Id, pack.Version)]);
        var definitions = factories.Select((factory, index) => CreateDefinition(factory, index)).ToArray();
        Assert.Equal(definitions.Length, definitions.Select(definition => definition.Id).Distinct().Count());
        var ruleSet = rules.Resolve(definitions);
        Assert.Equal(factories.Length, ruleSet.Rules.Count);
        Assert.Equal(factories.Length, ruleSet.DescriptorsByRuleId.Count);

        var graph = CreateGraph(factories, definitions);
        var plan = VerificationExecutionPlan.Create(ruleSet, graph);
        Assert.Equal(factories.Length, plan.Rules.Count);
        AssertRequiredFieldsResolve(factories, definitions, plan);

        var schemaRuleVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var contribution in metadata.ConfigurationSchemaContributions)
        {
            using var schema = JsonDocument.Parse(contribution.SchemaJson);
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                schema.RootElement.GetProperty("$schema").GetString());
            if (contribution.RuleTypes.Count == 0) continue;

            var schemaEntries = schema.RootElement.GetProperty("properties").GetProperty("rules")
                .GetProperty("additionalProperties").GetProperty("oneOf").EnumerateArray()
                .Select(item => new
                {
                    Type = item.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!,
                    Version = item.GetProperty("properties").GetProperty("version").GetProperty("const").GetString()!
                }).ToDictionary(item => item.Type, item => item.Version, StringComparer.Ordinal);
            foreach (var type in contribution.RuleTypes)
            {
                Assert.True(schemaEntries.TryGetValue(type, out var version),
                    $"Schema contribution '{contribution.Id}' declares missing rule type '{type}'.");
                Assert.True(schemaRuleVersions.TryAdd(type, version!),
                    $"Rule type '{type}' is contributed more than once.");
            }
        }
        Assert.Equal(factories.Select(factory => factory.Type).Order(StringComparer.Ordinal),
            schemaRuleVersions.Keys.Order(StringComparer.Ordinal));
        Assert.All(factories, factory => Assert.Equal(factory.Descriptor.Version, schemaRuleVersions[factory.Type]));

        var unknownRule = new VerificationRuleDefinition(new RuleId("conformance-unknown"), "conformance.unknown",
            "1", EvidenceSeverity.Error);
        var unknownRuleError = Assert.Throws<VerificationRuleException>(() => rules.Resolve([unknownRule]));
        Assert.Equal(VerificationIssueCodes.UnknownRuleType, unknownRuleError.Code);

        var first = factories[0];
        var unknownVersion = new VerificationRuleDefinition(new RuleId("conformance-version"), first.Type,
            first.Descriptor.Version + ".unknown", EvidenceSeverity.Error);
        Assert.Throws<VerificationRuleException>(() => rules.Resolve([unknownVersion]));
        Assert.Throws<VerificationRuleException>(() => registry.Resolve([new PackConfigurationDto("unavailable", "1")]));
        Assert.Throws<VerificationRuleException>(() => registry.Resolve([new PackConfigurationDto(pack.Id, pack.Version + ".unknown")]));
    }

    private static void AssertDescriptorComplete(RuleDescriptor descriptor, DomainPackMetadata metadata)
    {
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Type));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Version));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
        Assert.Equal(descriptor.Type, metadata.RuleProviders.Single().Rules.Single(rule => rule.Type == descriptor.Type).Type);
        Assert.Equal(descriptor.Version, metadata.RuleProviders.Single().Rules.Single(rule => rule.Type == descriptor.Type).Version);
        Assert.Contains(metadata.RuleProviders.Single().Rules, rule => rule.Type == descriptor.Type);
        Assert.Equal(descriptor.Options.Count, descriptor.Options.Select(option => option.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Flatten(descriptor.Options), option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Name));
            Assert.False(string.IsNullOrWhiteSpace(option.Description));
            Assert.False(SecretOptionName.IsMatch(option.Name), $"Rule option '{option.Name}' must not carry secret material.");
        });

        var conceptTypes = metadata.Concepts.Select(concept => concept.SemanticType).ToHashSet(StringComparer.Ordinal);
        Assert.All(descriptor.RequiredSemanticTypes.Concat(descriptor.FieldRequirements
            .Select(requirement => requirement.DefaultSemanticType).OfType<string>()),
            semanticType => Assert.Contains(semanticType, conceptTypes));

        foreach (var requirement in descriptor.FieldRequirements)
        {
            var option = Assert.Single(descriptor.Options, option => option.Name == requirement.OptionName);
            Assert.Contains(option.Kind, new[] { RuleOptionKind.FieldReference, RuleOptionKind.Sequence });
            if (option.Kind == RuleOptionKind.Sequence)
                Assert.Equal(RuleOptionKind.FieldReference, option.ItemKind);
        }
        Assert.All(descriptor.Options.Where(option => option.Kind == RuleOptionKind.FieldReference ||
                option.Kind == RuleOptionKind.Sequence && option.ItemKind == RuleOptionKind.FieldReference), option =>
            Assert.Contains(descriptor.FieldRequirements, requirement => requirement.OptionName == option.Name));
    }

    private static IEnumerable<RuleOptionDescriptor> Flatten(IEnumerable<RuleOptionDescriptor> options)
    {
        foreach (var option in options)
        {
            yield return option;
            foreach (var nested in Flatten(option.Properties ?? [])) yield return nested;
        }
    }

    private static VerificationRuleDefinition CreateDefinition(VerificationRuleFactory factory, int index)
    {
        var definitionId = new RuleId($"conformance-{index:D3}");
        var options = factory.Descriptor.Options.Select(option => new KeyValuePair<string, ValueNode>(option.Name,
            CreateOptionValue(factory.Descriptor, option)));
        return new VerificationRuleDefinition(definitionId, factory.Type, factory.Descriptor.Version,
            EvidenceSeverity.Error, structuredOptions: options);
    }

    private static ValueNode CreateOptionValue(RuleDescriptor descriptor, RuleOptionDescriptor option)
    {
        if (option.Default is not null) return option.Default;
        var requirement = descriptor.FieldRequirements.SingleOrDefault(item => item.OptionName == option.Name);
        return option.Kind switch
        {
            RuleOptionKind.Logical => new BooleanValue(false),
            RuleOptionKind.WholeNumber => new IntegerValue(0),
            RuleOptionKind.Number => new DecimalValue(option.Minimum ?? 0),
            RuleOptionKind.FieldReference => new StringValue(FieldsFor(descriptor, requirement).FirstOrDefault() ?? "field"),
            RuleOptionKind.SemanticTypeReference => new StringValue(SemanticTypeFor(descriptor, requirement)),
            RuleOptionKind.Sequence => new CollectionValue(SequenceValues(descriptor, option, requirement)),
            RuleOptionKind.Mapping => new ObjectValue((option.Properties ?? []).Select(property =>
                new KeyValuePair<string, ValueNode>(property.Name, CreateOptionValue(descriptor, property)))),
            RuleOptionKind.Text => new StringValue(option.Name switch
            {
                "sourceNode" => $"source-{descriptor.Type}",
                "targetNode" => $"target-{descriptor.Type}",
                _ => option.AllowedValues?.FirstOrDefault() ?? "configured-value"
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
    }

    private static IEnumerable<ValueNode> SequenceValues(RuleDescriptor descriptor, RuleOptionDescriptor option,
        RuleFieldRequirement? requirement)
    {
        if (option.ItemKind == RuleOptionKind.FieldReference)
            return FieldsFor(descriptor, requirement).DefaultIfEmpty("field").Select(field => (ValueNode)new StringValue(field));
        return [CreateOptionValue(descriptor, new RuleOptionDescriptor(option.Name + "Item", option.ItemKind ?? RuleOptionKind.Text,
            "Conformance sequence item."))];
    }

    private static IEnumerable<string> FieldsFor(RuleDescriptor descriptor, RuleFieldRequirement? requirement) =>
        requirement?.DefaultFields ?? descriptor.RequiredTargetFields.Concat(descriptor.RequiredSourceFields).Take(1);

    private static string SemanticTypeFor(RuleDescriptor descriptor, RuleFieldRequirement? requirement)
    {
        if (requirement?.DefaultSemanticType is { Length: > 0 } semanticType) return semanticType;
        return descriptor.RequiredSemanticTypes.Count > 0 ? descriptor.RequiredSemanticTypes[0] : "Conformance.Semantic";
    }

    private static MigrationGraph CreateGraph(IReadOnlyCollection<VerificationRuleFactory> factories,
        IReadOnlyCollection<VerificationRuleDefinition> definitions)
    {
        var nodes = new List<MigrationNode>();
        var nodeIndex = 0;
        foreach (var (factory, definition) in factories.Zip(definitions))
        {
            var descriptor = factory.Descriptor;
            var targetName = OptionText(definition, "targetNode") ?? $"target-{factory.Type}";
            var sourceName = OptionText(definition, "sourceNode") ?? $"source-{factory.Type}";
            var targetRequirements = descriptor.FieldRequirements.Where(requirement => requirement.Side == VerificationFieldSide.Target).ToArray();
            var sourceRequirements = descriptor.FieldRequirements.Where(requirement => requirement.Side == VerificationFieldSide.Source).ToArray();
            var primaryTargetRequirement = descriptor.PartitionKey is { Basis: not VerificationPartitionBasis.ArtifactIdentity } partition
                ? targetRequirements.FirstOrDefault(requirement => partition.FieldOptions.Contains(requirement.OptionName))
                : targetRequirements.FirstOrDefault();
            var primaryTargetSemantic = primaryTargetRequirement is null
                ? descriptor.RequiredSemanticTypes.Count > 0 ? descriptor.RequiredSemanticTypes[0] : "Conformance.Semantic"
                : SemanticTypeFor(descriptor, primaryTargetRequirement);
            var targetIdentity = descriptor.PartitionKey is { Basis: not VerificationPartitionBasis.ArtifactIdentity } partitionKey
                ? partitionKey.FieldOptions.SelectMany(optionName => FieldsFor(descriptor,
                    targetRequirements.SingleOrDefault(requirement => requirement.OptionName == optionName))).ToArray()
                : descriptor.RequiredTargetFields.Take(1).ToArray();
            if (targetIdentity.Length == 0) targetIdentity = ["id"];

            nodes.Add(CreateNode(nodeIndex++, sourceName, MigrationNodeType.Source,
                sourceRequirements.Select(requirement => SemanticTypeFor(descriptor, requirement)).DefaultIfEmpty("Conformance.Semantic").First(), ["id"]));
            nodes.Add(CreateNode(nodeIndex++, targetName, MigrationNodeType.Target, primaryTargetSemantic, targetIdentity));

            foreach (var semanticType in sourceRequirements.Select(requirement => SemanticTypeFor(descriptor, requirement))
                .Distinct(StringComparer.Ordinal).Where(semanticType => semanticType != nodes[^2].SemanticType))
                nodes.Add(CreateNode(nodeIndex++, $"{sourceName}-{nodeIndex}", MigrationNodeType.Source, semanticType, ["id"]));
            foreach (var semanticType in targetRequirements.Select(requirement => SemanticTypeFor(descriptor, requirement))
                .Concat(descriptor.RequiredSemanticTypes).Distinct(StringComparer.Ordinal).Where(semanticType => semanticType != primaryTargetSemantic))
                nodes.Add(CreateNode(nodeIndex++, $"{targetName}-{nodeIndex}", MigrationNodeType.Target, semanticType, ["id"]));
        }

        return new MigrationGraph(new MigrationGraphId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            nodes, [], new string('a', 64), "pack-conformance-v1");
    }

    private static MigrationNode CreateNode(int index, string name, MigrationNodeType type, string semanticType,
        IEnumerable<string> identityFields) => new(new MigrationNodeId(GuidFromInteger(index + 1)), name, type,
        semanticType, new SystemId(type == MigrationNodeType.Source ? "conformance-source" : "conformance-target"),
        new StorageEndpointId(type == MigrationNodeType.Source ? "conformance-source-endpoint" : "conformance-target-endpoint"),
        new ArtifactSelector("table", identityFields: identityFields));

    private static Guid GuidFromInteger(int value) => new(value, 0, 0, new byte[8]);

    private static string? OptionText(VerificationRuleDefinition definition, string name) =>
        definition.StructuredOptions.TryGetValue(name, out var value) && value is StringValue text ? text.Value : null;

    private static void AssertRequiredFieldsResolve(IReadOnlyCollection<VerificationRuleFactory> factories,
        IReadOnlyCollection<VerificationRuleDefinition> definitions, VerificationExecutionPlan plan)
    {
        foreach (var (factory, definition) in factories.Zip(definitions))
        {
            Assert.All(factory.Descriptor.RequiredSourceFields, field =>
                Assert.Contains(field, plan.GetRequiredFields(VerificationArtifactRole.Source, "*")));
            Assert.All(factory.Descriptor.RequiredTargetFields, field =>
                Assert.Contains(field, plan.GetRequiredFields(VerificationArtifactRole.ExpectedTarget, "*")));
            Assert.All(factory.Descriptor.GroupingKeys.Concat(factory.Descriptor.OrderingKeys).Concat(factory.Descriptor.LookupKeys), key =>
                Assert.Contains(key, plan.RequiredOrderingKeys));
            foreach (var requirement in factory.Descriptor.FieldRequirements)
            {
                var fields = definition.StructuredOptions[requirement.OptionName] switch
                {
                    StringValue field => [field.Value],
                    CollectionValue values => values.Values.Cast<StringValue>().Select(value => value.Value).ToArray(),
                    _ => []
                };
                Assert.NotEmpty(fields);
                var semanticType = requirement.SemanticTypeOption is { } optionName &&
                    definition.StructuredOptions.TryGetValue(optionName, out var semanticValue) && semanticValue is StringValue semanticText
                        ? semanticText.Value
                        : requirement.DefaultSemanticType ?? (factory.Descriptor.RequiredSemanticTypes.Count > 0
                            ? factory.Descriptor.RequiredSemanticTypes[0] : "*");
                var role = requirement.Side == VerificationFieldSide.Source
                    ? VerificationArtifactRole.Source : VerificationArtifactRole.ExpectedTarget;
                var availableFields = plan.GetRequiredFields(role, semanticType);
                Assert.All(fields, field => Assert.Contains(field, availableFields));
            }
        }
    }
}