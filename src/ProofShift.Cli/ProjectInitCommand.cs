using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Graph;
using ProofShift.Verification;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ProofShift.Cli;

internal static class ProjectInitCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (2 or 4) || (args.Length == 4 && args[2] != "--template"))
        {
            Console.Error.WriteLine("Usage: proofshift init <directory> [--template generic|pension]");
            return 2;
        }
        var template = args.Length == 4 ? args[3] : "generic";
        if (template is not ("generic" or "pension"))
        {
            Console.Error.WriteLine("PSAUTHOR010: Template must be generic or pension.");
            return 1;
        }
        var directory = Path.GetFullPath(args[1]);
        if (File.Exists(directory) || (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
        {
            Console.Error.WriteLine("PSAUTHOR011: Initialization requires a new or empty directory; nothing was overwritten.");
            return 1;
        }
        Directory.CreateDirectory(directory);
        var pension = template == "pension";
        var identity = pension ? "member_id" : "id";
        var attribute = pension ? "status" : "value";
        var semanticType = pension ? "Pension.Member" : "Generic.Record";
        var projectName = Path.GetFileName(directory);
        var projectId = new string(projectName.ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-' ? character : '-').ToArray()).Trim('-');
        if (projectId.Length == 0) projectId = "project";
        var root = new
        {
            proofshift = 1,
            project = new { id = projectId, name = projectName },
            packs = pension ? new[] { new { id = "pension", version = "0.9.0" } } : [],
            systems = new { source = new { file = "systems/source.yaml" }, target = new { file = "systems/target.yaml" } },
            migration = new { graph = "migration/graph.yaml" },
            verification = new { rules = "rules/rules.yaml" },
            recovery = new { policy = "recovery/policy.yaml" }
        };
        var source = new { id = "source", name = "Source", role = "source", storage = new
        {
            records = new { connector = "csv", root = "data", delimiter = ";" }
        } };
        var target = new { id = "target", name = "Isolated Target", role = "shadow-target", storage = new
        {
            records = new { connector = "postgres", connection = new { secret = "PROOFSHIFT_TARGET_CONNECTION" } }
        } };
        var nodes = new Dictionary<string, object>
        {
            ["source-records"] = new
            {
                type = "source", system = "source", storage = "records", semanticType,
                selector = new { kind = "csv", properties = new { path = "source.csv", delimiter = ";" }, identity = new[] { identity } }
            },
            ["target-records"] = new
            {
                type = "target", system = "target", storage = "records", semanticType,
                selector = new { kind = "table", properties = new { name = "public.records", columns = $"{identity},{attribute}" }, identity = new[] { identity } }
            }
        };
        var fields = new Dictionary<string, object>
        {
            [identity] = new { source = identity }, [attribute] = new { source = attribute }
        };
        var edges = new Dictionary<string, object>
        {
            ["record-copy"] = new
            {
                from = new[] { "source-records" }, to = new[] { "target-records" },
                operation = new { type = "transform", fields },
                recovery = new { mode = "reverse" }
            }
        };
        var rules = new Dictionary<string, object>
        {
            ["accounting"] = new { type = "source-artifact-accounting", version = "1", severity = "critical" },
            ["lineage"] = new { type = "target-lineage", version = "1", severity = "critical" },
            ["presence"] = new { type = "target-presence", version = "1", severity = "error" },
            ["unexpected"] = new { type = "unexpected-target", version = "1", severity = "error" },
            ["attributes"] = new { type = "attribute-comparison", version = "1", severity = "error", targetNode = "target-records", semanticType }
        };
        await WriteAsync(directory, "proofshift.yaml", root).ConfigureAwait(false);
        await WriteAsync(directory, "systems/source.yaml", source).ConfigureAwait(false);
        await WriteAsync(directory, "systems/target.yaml", target).ConfigureAwait(false);
        await WriteAsync(directory, "migration/graph.yaml", new { version = 1, nodes, edges }).ConfigureAwait(false);
        await WriteAsync(directory, "rules/rules.yaml", new { version = 2, rules }).ConfigureAwait(false);
        await WriteAsync(directory, "recovery/policy.yaml", new
        {
            requireRecoveryForDestructiveOperations = true,
            allowIrreversible = new { @default = false },
            requireValidatedRestore = true,
            requireRecoveryRehearsal = true,
            maximumIrreversibleArtifacts = 0
        }).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(directory, "data", "source.csv"), $"{identity};{attribute}\n").ConfigureAwait(false);
        var loaded = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(Path.Combine(directory, "proofshift.yaml")).ConfigureAwait(false);
        if (!loaded.IsValid || loaded.Configuration is null)
            throw new InvalidOperationException("Generated project failed configuration validation.");
        var graph = MigrationGraphCompiler.Compile(loaded.Configuration);
        if (!graph.IsValid) throw new InvalidOperationException("Generated project failed graph validation.");
        var definitions = VerificationRuleConfigurationLoader.Load(loaded.Configuration);
        _ = CliComposition.RulesFor(loaded.Configuration, definitions).Resolve(definitions);
        Console.WriteLine($"Initialized {template} project: {directory}");
        return 0;
    }

    internal static async Task WriteAsync(string directory, string relativePath, object value)
    {
        var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var yaml = new YamlStream();
        using var input = new StringReader(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
        yaml.Load(input);
        SetBlockStyle(yaml.Documents[0].RootNode);
        using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(text, assignAnchors: false);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text.ToString()).ConfigureAwait(false);
    }

    private static void SetBlockStyle(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            mapping.Style = MappingStyle.Block;
            foreach (var child in mapping.Children.Values) SetBlockStyle(child);
        }
        if (node is YamlSequenceNode sequence)
        {
            sequence.Style = SequenceStyle.Block;
            foreach (var child in sequence.Children) SetBlockStyle(child);
        }
    }
}