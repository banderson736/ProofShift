using System.Text.Json;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class RuleAuthoringCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] ConnectorNames = ["sqlserver", "postgres", "csv", "filesystem"];

    internal static int Run(string[] args)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        var arguments = args.Where(argument => argument != "--json").ToArray();
        var descriptors = CliComposition.InstalledRuleDescriptors();
        if (arguments is ["capabilities"])
        {
            var packs = CliComposition.Packs().Installed.Select(pack => new { pack.Id, pack.Version }).ToArray();
            if (json) Console.WriteLine(JsonSerializer.Serialize(new
            {
                connectors = ConnectorNames,
                discoverySupport = ConnectorNames,
                importFormats = "csv",
                ruleSchemaFormat = "JSON Schema 2020-12",
                packs,
                ruleProviders = descriptors.Select(descriptor => CliComposition.ProviderFor(descriptor.Type)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            }, JsonOptions));
            else
            {
                Console.WriteLine("Connectors: sqlserver, postgres, csv, filesystem");
                foreach (var pack in packs) Console.WriteLine($"Pack: {pack.Id} {pack.Version}");
                Console.WriteLine("Rule provider: proofshift.verification.generic 1");
            }
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
        Console.Error.WriteLine("Usage: proofshift rules list|describe <type>|schema [--json]; proofshift capabilities [--json]");
        return 2;
    }
}