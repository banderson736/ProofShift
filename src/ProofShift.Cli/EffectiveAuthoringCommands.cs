using System.Text.Json;
using System.Text.Json.Nodes;
using ProofShift.Configuration;
using ProofShift.Domain;
using ProofShift.Graph;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal static class EffectiveAuthoringCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var json = args.Contains("--json", StringComparer.Ordinal);
            var arguments = args.Where(argument => argument is not ("--json" or "--effective")).ToArray();
            string path, kind;
            string? selected = null;
            if (arguments is ["config", "show", var effectivePath]) { path = effectivePath; kind = "effective"; }
            else if (arguments is ["explain", var projectPath, "project"]) { path = projectPath; kind = "project"; }
            else if (arguments is ["explain", var selectionPath, var requested, var id] && requested is "rule" or "mapping")
            { path = selectionPath; kind = requested; selected = id; }
            else
            {
                Console.Error.WriteLine("Usage: proofshift explain <config> project|rule <id>|mapping <id>; proofshift config show <config> --effective [--json]");
                return 2;
            }
            var loaded = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(path).ConfigureAwait(false);
            var configuration = loaded.Configuration ?? throw new InvalidDataException("Configuration cannot be explained until it loads successfully.");
            var compilation = MigrationGraphCompiler.Compile(configuration);
            var graph = compilation.Graph ?? throw new InvalidDataException("Migration graph must compile before effective explanation.");
            var definitions = VerificationRuleConfigurationLoader.Load(configuration);
            var registry = CliComposition.RulesFor(configuration, definitions);
            var resolved = registry.Resolve(definitions);
            object output;
            if (kind == "rule")
            {
                var definition = definitions.SingleOrDefault(item => item.Id.Value == selected)
                    ?? throw new InvalidDataException("Configured rule ID is unknown.");
                var descriptor = registry.Descriptors.Single(item => item.Type == definition.Type);
                var effective = EffectiveOptions(definition, descriptor);
                output = new
                {
                    ruleId = definition.Id.Value, definition.Type, definition.Version,
                    provider = CliComposition.ProviderFor(definition.Type), scope = descriptor.Scope.ToString(),
                    severity = definition.Severity.ToString(), description = descriptor.Description,
                    options = effective,
                    defaultsApplied = descriptor.Options.Where(option => option.Default is not null && !definition.StructuredOptions.ContainsKey(option.Name)).Select(option => option.Name),
                    meaning = descriptor.Scope == VerificationScope.Aggregate
                        ? $"For each configured group {effective.GetValueOrDefault("groupBy")?.ToJsonString() ?? "(provider default)"}, sum graph-derived expected and observed values of {effective.GetValueOrDefault("amountField")?.ToJsonString() ?? "the provider's measure field"}. Fail at severity {definition.Severity} when the absolute difference exceeds {effective.GetValueOrDefault("tolerance")?.ToJsonString() ?? "the provider's tolerance"}."
                        : $"{descriptor.Description} Evaluate the displayed effective scope and comparison options at severity {definition.Severity}."
                };
            }
            else if (kind == "mapping")
            {
                var edges = graph.Edges.Where(edge => edge.Name == selected || edge.Sources.Concat(edge.Targets)
                    .Any(nodeId => graph.Nodes.Any(node => node.Id == nodeId && node.Name == selected))).ToArray();
                if (edges.Length == 0) throw new InvalidDataException("Mapping/graph-node ID is unknown.");
                output = edges.Select(edge => new
                {
                    mapping = edge.Name,
                    sources = graph.Nodes.Where(node => edge.Sources.Contains(node.Id)).Select(NodeView),
                    targets = graph.Nodes.Where(node => edge.Targets.Contains(node.Id)).Select(NodeView),
                    operation = edge.Operation.Type.ToString(), fields = edge.Operation.Fields,
                    recovery = edge.Recovery,
                    dependentRules = definitions.Where(definition => definition.StructuredOptions.Values.OfType<StringValue>().Any(value =>
                        graph.Nodes.Any(node => edge.Targets.Contains(node.Id) && (node.Name == value.Value || node.SemanticType == value.Value))))
                        .Select(definition => definition.Id.Value)
                }).ToArray();
            }
            else
            {
                var effectiveRules = definitions.Select(definition => new
                {
                    id = definition.Id.Value, definition.Type, definition.Version,
                    severity = definition.Severity.ToString(),
                    options = EffectiveOptions(definition, registry.Descriptors.Single(descriptor => descriptor.Type == definition.Type))
                }).ToArray();
                output = new
                {
                    configuration.Root.Project, configurationVersion = configuration.Root.Version,
                    configuration.ConfigurationHash, graph.GraphHash, ruleSetFingerprint = resolved.Fingerprint,
                    systems = configuration.Systems.Select(system => new
                    {
                        id = system.Id.Value, system.Name, role = system.Role.ToString(),
                        endpoints = system.StorageEndpoints.Select(endpoint => new { id = endpoint.Id.Value, connector = endpoint.Connector.Value })
                    }),
                    packs = resolved.ProviderVersions,
                    nodes = graph.Nodes.Select(NodeView), edges = graph.Edges.Select(edge => new { edge.Name, operation = edge.Operation.Type.ToString(), edge.Recovery }),
                    rules = effectiveRules,
                    warnings = loaded.Issues.Concat(compilation.Issues.Select(issue => new ConfigurationValidationIssue(issue.Code,
                        issue.Severity == GraphValidationSeverity.Error ? ValidationSeverity.Error : ValidationSeverity.Warning, issue.Message, issue.File, issue.Path))),
                    unresolvedMappings = StrictAuthoringValidation.UnapprovedMappings(configuration),
                    referencedFiles = kind == "effective" ? configuration.ReferencedFiles.Select(file => new
                    {
                        file.RelativePath, kind = file.Kind.ToString(),
                        document = DocumentView(file.Kind == ConfigurationFileKind.MigrationGraph && file.Document is ConfigurationMappingNode mapping
                            ? ReusableCodeMaps.Resolve(mapping) : file.Document)
                    }).ToArray() : null
                };
            }
            var serialized = JsonSerializer.Serialize(output, output.GetType(), JsonOptions);
            if (json || kind == "effective") Console.WriteLine(serialized);
            else { Console.WriteLine($"ProofShift {kind} explanation"); Console.WriteLine(serialized); }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or VerificationRuleException)
        {
            Console.Error.WriteLine($"PSAUTHOR020: Effective explanation could not complete ({exception.GetType().Name}); validate configuration and selected IDs.");
            return 1;
        }
    }

    private static object NodeView(MigrationNode node) => new { node.Name, node.SemanticType, system = node.SystemId.Value,
        endpoint = node.EndpointId.Value, node.Selector };

    private static Dictionary<string, JsonNode?> EffectiveOptions(VerificationRuleDefinition definition, RuleDescriptor descriptor)
    {
        var options = definition.StructuredOptions.ToDictionary(pair => pair.Key, pair => ValueView(pair.Value), StringComparer.Ordinal);
        foreach (var option in descriptor.Options)
            if (option.Default is not null && !options.ContainsKey(option.Name)) options.Add(option.Name, ValueView(option.Default));
        return options;
    }

    private static JsonNode? ValueView(ValueNode value) => value switch
    {
        StringValue text => JsonValue.Create(text.Value), BooleanValue boolean => JsonValue.Create(boolean.Value),
        IntegerValue integer => JsonValue.Create(integer.Value), DecimalValue number => JsonValue.Create(number.Value),
        CollectionValue sequence => new JsonArray(sequence.Values.Select(ValueView).ToArray()),
        ObjectValue mapping => new JsonObject(mapping.Values.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ValueView(pair.Value)))),
        _ => null
    };

    private static JsonNode? DocumentView(ConfigurationDocumentNode node) => node switch
    {
        ConfigurationMappingNode mapping => new JsonObject(mapping.Values.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, DocumentView(pair.Value)))),
        ConfigurationSequenceNode sequence => new JsonArray(sequence.Values.Select(DocumentView).ToArray()),
        ConfigurationScalarNode scalar when scalar.Kind == ConfigurationScalarKind.Null => null,
        ConfigurationScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null
    };
}