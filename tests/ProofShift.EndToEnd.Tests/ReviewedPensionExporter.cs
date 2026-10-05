using System.Text.Json;
using ProofShift.Domain;
using ProofShift.Verification;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ProofShift.EndToEnd.Tests;

internal static class ReviewedPensionExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static async Task WriteAsync(string directory, IReadOnlyCollection<SystemDefinition> systems,
        MigrationGraph corrected, MigrationGraph falseReverse, IReadOnlyCollection<VerificationRuleDefinition> rules)
    {
        var references = systems.ToDictionary(system => system.Id.Value, system => new { file = $"systems/{system.Id.Value}.yaml" }, StringComparer.Ordinal);
        foreach (var system in systems)
        {
            var storage = new Dictionary<string, object>();
            foreach (var endpoint in system.StorageEndpoints)
            {
                var settings = new Dictionary<string, object> { ["connector"] = endpoint.Connector.Value };
                foreach (var pair in endpoint.Configuration)
                {
                    var segments = pair.Key.Split('.');
                    var parent = settings;
                    foreach (var segment in segments.SkipLast(1))
                    {
                        if (!parent.TryGetValue(segment, out var nested)) parent.Add(segment, nested = new Dictionary<string, object>());
                        parent = (Dictionary<string, object>)nested;
                    }
                    parent[segments[^1]] = pair.Value.StartsWith("secret:", StringComparison.Ordinal)
                        ? new { secret = pair.Value[7..] } : pair.Value.StartsWith("env:", StringComparison.Ordinal)
                            ? new { env = pair.Value[4..] } : (object)pair.Value;
                }
                storage.Add(endpoint.Id.Value, settings);
            }
            await SaveAsync(directory, $"systems/{system.Id.Value}.yaml", new
            {
                id = system.Id.Value, system.Name, role = system.Role == SystemRole.ShadowTarget ? "shadow-target" : "source", storage
            });
        }
        await SaveAsync(directory, "migration/graph.yaml", GraphView(corrected));
        await SaveAsync(directory, "migration/graph-false-reverse.yaml", GraphView(falseReverse));
        var descriptors = new ProofShift.Packs.Pension.PensionPack().RuleFactories.ToDictionary(factory => factory.Type, factory => factory.Descriptor);
        var configuredRules = new Dictionary<string, object>();
        foreach (var rule in rules)
        {
            var values = new Dictionary<string, object> { ["type"] = rule.Type, ["version"] = rule.Version, ["severity"] = rule.Severity.ToString().ToLowerInvariant() };
            foreach (var pair in rule.Options)
            {
                var kind = descriptors[rule.Type].Options.Single(option => option.Name == pair.Key).Kind;
                values[pair.Key] = kind switch
                {
                    RuleOptionKind.Sequence => pair.Value.Split(',', StringSplitOptions.TrimEntries),
                    RuleOptionKind.Number => decimal.Parse(pair.Value, System.Globalization.CultureInfo.InvariantCulture),
                    RuleOptionKind.Logical => bool.Parse(pair.Value),
                    _ => pair.Value
                };
            }
            configuredRules.Add(rule.Id.Value, values);
        }
        await SaveAsync(directory, "rules/pension.yaml", new { version = 2, rules = configuredRules });
        await SaveAsync(directory, "recovery/policy.yaml", new
        {
            requireRecoveryForDestructiveOperations = true, allowIrreversible = new { @default = false },
            requireValidatedRestore = true, requireRecoveryRehearsal = true, maximumIrreversibleArtifacts = 0
        });
        foreach (var variant in new[] { "", "-false-reverse" })
            await SaveAsync(directory, $"proofshift{variant}.yaml", new
            {
                proofshift = 1, project = new { id = "pension-assurance", name = "Synthetic Configured Pension Assurance" },
                packs = new[] { new { id = "pension", version = "0.9.0" } }, systems = references,
                migration = new { graph = $"migration/graph{variant}.yaml" }, verification = new { rules = "rules/pension.yaml" },
                recovery = new { policy = "recovery/policy.yaml" }
            });
    }

    private static object GraphView(MigrationGraph graph)
    {
        var codeMaps = new Dictionary<string, object>(StringComparer.Ordinal);
        var names = graph.Nodes.ToDictionary(node => node.Id, node => node.Name);
        var nodes = graph.Nodes.ToDictionary(node => node.Name, node => (object)new
        {
            type = node.Type.ToString().ToLowerInvariant(), system = node.SystemId.Value, storage = node.EndpointId.Value,
            node.SemanticType, selector = new { node.Selector.Kind, properties = node.Selector.Properties, identity = node.Selector.IdentityFields }
        });
        var edges = graph.Edges.ToDictionary(edge => edge.Name, edge => (object)new
        {
            from = edge.Sources.Select(id => names[id]).ToArray(), to = edge.Targets.Select(id => names[id]).ToArray(),
            operation = new
            {
                type = edge.Operation.Type.ToString().ToLowerInvariant(), version = edge.Version,
                parameters = edge.Operation.Parameters,
                fields = edge.Operation.Fields.ToDictionary(field => field.Target, field => new
                {
                    field.Source, pipeline = field.Pipeline.Select((step, index) => StepView(edge.Name, field.Target, index, step, codeMaps)).ToArray()
                })
            },
            recovery = new { mode = edge.Recovery!.Mode.ToString().ToLowerInvariant(), edge.Recovery.RequiresSnapshot }
        });
        return new { version = 1, codeMaps, nodes, edges };
    }

    private static object StepView(string edge, string field, int index, TransformationStep step, Dictionary<string, object> maps)
    {
        if (step.Type != TransformationStepType.CodeMap) return new { type = step.Type.ToString(), step.Version, parameters = step.Parameters };
        var name = $"{edge}-{field}-{index}";
        maps.Add(name, new
        {
            values = step.Parameters.Where(pair => pair.Key is not ("unknown" or "default")).ToDictionary(pair => pair.Key, pair => pair.Value),
            unmapped = step.Parameters.GetValueOrDefault("unknown") ?? "fail",
            @default = step.Parameters.GetValueOrDefault("default")
        });
        return new { type = "code-map", step.Version, map = name };
    }

    private static async Task SaveAsync(string directory, string relativePath, object value)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new YamlStream();
        using var input = new StringReader(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
        stream.Load(input);
        Block(stream.Documents[0].RootNode);
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(output, assignAnchors: false);
        await File.WriteAllTextAsync(path, output.ToString());
    }

    private static void Block(YamlNode node)
    {
        if (node is YamlMappingNode mapping) { mapping.Style = MappingStyle.Block; foreach (var child in mapping.Children.Values) Block(child); }
        if (node is YamlSequenceNode sequence) { sequence.Style = SequenceStyle.Block; foreach (var child in sequence.Children) Block(child); }
    }
}