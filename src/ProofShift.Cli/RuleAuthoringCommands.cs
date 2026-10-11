using System.Text.Json;
using ProofShift.Packs.Abstractions;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class RuleAuthoringCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        var arguments = args.Where(argument => argument != "--json").ToArray();
        var descriptors = CliComposition.InstalledRuleDescriptors();
        var installedPacks = CliComposition.Packs().Installed;
        if (arguments is ["packs", "list"])
        {
            var entries = installedPacks.OrderBy(pack => pack.Id, StringComparer.Ordinal).Select(pack => new
            {
                pack.Id, pack.Version, pack.Metadata.DisplayName,
                conceptCount = pack.Metadata.Concepts.Count,
                ruleTypes = pack.RuleFactories.Select(factory => factory.Type).Order(StringComparer.Ordinal)
            }).ToArray();
            if (json) Console.WriteLine(JsonSerializer.Serialize(entries, JsonOptions));
            else foreach (var entry in entries) Console.WriteLine($"{entry.Id} {entry.Version}: {entry.DisplayName} ({entry.conceptCount} concepts)");
            return 0;
        }
        if (arguments is ["packs", "describe", var packId])
        {
            var pack = FindPack(installedPacks, packId);
            if (pack is null)
            {
                Console.Error.WriteLine("PSPACK001: Domain pack is not installed. Use packs list to inspect available packs.");
                return 1;
            }
            if (json) Console.WriteLine(JsonSerializer.Serialize(pack.Metadata, JsonOptions));
            else
            {
                Console.WriteLine($"{pack.Id} version {pack.Version}: {pack.Metadata.DisplayName}");
                foreach (var concept in pack.Metadata.Concepts)
                    Console.WriteLine($"Concept: {concept.SemanticType} ({concept.DisplayName})");
                foreach (var rule in pack.Metadata.RuleProviders.SelectMany(provider => provider.Rules))
                    Console.WriteLine($"Rule: {rule.Type} {rule.Version}: {rule.Description}");
                foreach (var capability in pack.Metadata.Capabilities)
                    Console.WriteLine($"Capability: {capability.Id}: {capability.Description}");
            }
            return 0;
        }
        if (arguments is ["packs", "schema", var schemaPackId])
        {
            var pack = FindPack(installedPacks, schemaPackId);
            if (pack is null)
            {
                Console.Error.WriteLine("PSPACK001: Domain pack is not installed. Use packs list to inspect available packs.");
                return 1;
            }
            Console.WriteLine(RuleConfigurationSchema.Generate(pack.RuleFactories.Select(factory => factory.Descriptor))
                .ToJsonString(JsonOptions));
            return 0;
        }
        if (arguments is ["capabilities"])
        {
            var connectors = CliComposition.Connectors().Capabilities;
            var connectorSchemas = CliComposition.Connectors().ConfigurationSchemas;
            var packs = CliComposition.Packs().Installed.Select(pack => new { pack.Id, pack.Version }).ToArray();
            if (json) Console.WriteLine(JsonSerializer.Serialize(new
            {
                connectors,
                connectorSchemas = connectorSchemas.Select(schema => schema.ConnectorId),
                discoverySupport = connectors.Where(connector => connector.Discovery).Select(connector => connector.Id),
                importFormats = "csv",
                ruleSchemaFormat = "JSON Schema 2020-12",
                packs,
                ruleProviders = descriptors.Select(descriptor => CliComposition.ProviderFor(descriptor.Type)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            }, JsonOptions));
            else
            {
                foreach (var connector in connectors)
                    Console.WriteLine($"{connector.Id} {connector.Version}: discover={connector.Discovery}, read={connector.SourceRead}, checkpoint={connector.CheckpointCapture}, observe={connector.TargetObservation}, shadow-write={connector.ShadowWrite}, binary={connector.BinaryStreaming}, consistency={connector.Consistency}, partitioning={connector.Partitioning}");
                foreach (var pack in packs) Console.WriteLine($"Pack: {pack.Id} {pack.Version}");
                Console.WriteLine("Rule provider: proofshift.verification.generic 1");
            }
            return 0;
        }
        if (arguments is ["connectors", "schema", var connectorId])
        {
            Console.WriteLine(CliComposition.Connectors().ConfigurationSchema(connectorId).ToJsonSchema().ToJsonString(JsonOptions));
            return 0;
        }
        if (arguments is ["rules", "schema"])
        {
            Console.WriteLine(RuleConfigurationSchema.Generate(descriptors).ToJsonString(JsonOptions));
            return 0;
        }
        if (arguments is ["rules", "list"])
        {
            var entries = descriptors.Select(descriptor => new
            {
                descriptor.Type, descriptor.Version, descriptor.Description, provider = CliComposition.ProviderFor(descriptor.Type)
            }).ToArray();
            if (json) Console.WriteLine(JsonSerializer.Serialize(entries, JsonOptions));
            else foreach (var entry in entries) Console.WriteLine($"{entry.Type} {entry.Version} [{entry.provider}] {entry.Description}");
            return 0;
        }
        if (arguments is ["rules", "describe", var type])
        {
            var descriptor = descriptors.SingleOrDefault(item => item.Type == type);
            if (descriptor is null)
            {
                Console.Error.WriteLine("PSRULE008: Rule type is not installed. Use rules list to inspect available types.");
                return 1;
            }
            var optionSchemas = RuleConfigurationSchema.Generate([descriptor])["properties"]!["rules"]!["additionalProperties"]!["oneOf"]![0]!["properties"]!;
            if (json) Console.WriteLine(JsonSerializer.Serialize(new
            {
                provider = CliComposition.ProviderFor(type), descriptor.Type, descriptor.Version, descriptor.Description,
                scope = descriptor.Scope.ToString(), descriptor.ExampleYaml,
                options = descriptor.Options.Select(option => new
                {
                    option.Name, kind = option.Kind.ToString(), option.Description, option.Required,
                    schema = optionSchemas[option.Name]!.DeepClone()
                })
            }, JsonOptions));
            else
            {
                Console.WriteLine($"{descriptor.Type} version {descriptor.Version} [{CliComposition.ProviderFor(type)}]");
                Console.WriteLine(descriptor.Description);
                Console.WriteLine($"Scope: {descriptor.Scope}");
                foreach (var option in descriptor.Options)
                {
                    Console.WriteLine($"{option.Name}: {option.Kind}, {(option.Required ? "required" : "optional")}. {option.Description}");
                    if (optionSchemas[option.Name]!["default"] is { } defaultValue)
                        Console.WriteLine($"  Default: {defaultValue.ToJsonString()}");
                }
                Console.WriteLine("Example YAML:");
                Console.WriteLine(descriptor.ExampleYaml);
            }
            return 0;
        }
        Console.Error.WriteLine("Usage: proofshift packs list|describe <id>|schema <id> [--json]; proofshift rules list|describe <type>|schema [--json]; proofshift connectors schema <id>; proofshift capabilities [--json]");
        return 2;
    }

    private static IDomainPack? FindPack(IReadOnlyCollection<IDomainPack> packs, string requestedId)
    {
        var matches = packs.Where(pack => pack.Id == requestedId ||
            pack.Id[(pack.Id.LastIndexOf('.') + 1)..] == requestedId).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}