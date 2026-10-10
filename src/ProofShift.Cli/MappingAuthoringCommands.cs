using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Engine;
using ProofShift.Graph;

namespace ProofShift.Cli;

internal static class MappingAuthoringCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var import = args.Length >= 2 && args[0] == "mapping" && args[1] == "import";
            string? sourcePath = null, targetPath = null, output = null, csvPath = null;
            for (var index = import ? 2 : 1; index < args.Length; index++)
            {
                if (index + 1 >= args.Length) return Usage();
                switch (args[index])
                {
                    case "--source-discovery": sourcePath = args[++index]; break;
                    case "--target-discovery": targetPath = args[++index]; break;
                    case "--output": output = args[++index]; break;
                    case "--csv": csvPath = args[++index]; break;
                    default: return Usage();
                }
            }
            if (sourcePath is null || targetPath is null || output is null || import && csvPath is null) return Usage();
            var source = await PhysicalDiscoveryStore.ReadAsync(sourcePath, CancellationToken.None);
            var target = await PhysicalDiscoveryStore.ReadAsync(targetPath, CancellationToken.None);
            var review = MappingScaffold.Create(source, target);
            if (import)
            {
                using var reader = File.OpenText(csvPath!);
                var imported = await new CsvMappingImporter().ImportAsync(reader, source, target, CancellationToken.None);
                if (imported.Issues.Count > 0)
                {
                    foreach (var issue in imported.Issues) Console.Error.WriteLine($"{csvPath}:{issue.Row} {issue.Code} [{issue.Column}]: {issue.Message}");
                    return 1;
                }
                var importedFields = imported.Mappings.Select(mapping => (mapping.SourceEntity, mapping.SourceField)).ToHashSet();
                review = review with { Mappings = imported.Mappings.Concat(review.Mappings
                    .Where(mapping => !importedFields.Contains((mapping.SourceEntity, mapping.SourceField)))
                    .Select(mapping => mapping with { ReviewStatus = "unmapped", TargetEntity = null, TargetField = null, Reasons = [] })).ToArray() };
            }
            await WriteProjectAsync(Path.GetFullPath(output), source, target, review, import).ConfigureAwait(false);
            var unresolved = review.Mappings.Count(mapping => mapping.Required && mapping.ReviewStatus != "confirmed");
            Console.WriteLine($"{MappingScaffold.Format}: {review.Mappings.Count} mappings; {unresolved} required mappings await review. Output: {Path.GetFullPath(output)}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException or
            CsvHelper.CsvHelperException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"PSAUTHOR030: Mapping authoring failed ({exception.GetType().Name}); check discovery bindings, CSV and output boundaries.");
            return 1;
        }
    }

    private static async Task WriteProjectAsync(string directory, PhysicalDiscoveryArtifact source,
        PhysicalDiscoveryArtifact target, MappingReview review, bool requireCompiled)
    {
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Mapping authoring requires a new or empty output directory.");
        Directory.CreateDirectory(directory);
        var nodes = new Dictionary<string, object>(StringComparer.Ordinal);
        var sourceNames = source.Objects.Select((entity, index) => (Name: MappingScaffold.Name(entity), Node: $"source-{index}"))
            .ToDictionary(item => item.Name, item => item.Node, StringComparer.Ordinal);
        var targetNames = target.Objects.Select((entity, index) => (Name: MappingScaffold.Name(entity), Node: $"target-{index}"))
            .ToDictionary(item => item.Name, item => item.Node, StringComparer.Ordinal);
        foreach (var entity in source.Objects) nodes[sourceNames[MappingScaffold.Name(entity)]] = Node(entity, "source", "source", source.ConnectorId);
        foreach (var entity in target.Objects) nodes[targetNames[MappingScaffold.Name(entity)]] = Node(entity, "target", "target", target.ConnectorId);
        var edges = new Dictionary<string, object>(StringComparer.Ordinal);
        var edgeIndex = 0;
        foreach (var group in review.Mappings.Where(mapping => mapping.TargetEntity is not null && mapping.TargetField is not null)
            .GroupBy(mapping => (mapping.SourceEntity, mapping.TargetEntity)))
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var mapping in group)
                fields.Add(mapping.TargetField!, new
                {
                    source = mapping.SourceField,
                    pipeline = mapping.Transformation == "copy" ? Array.Empty<object>() : [new { type = mapping.Transformation }],
                    approval = mapping.ReviewStatus,
                    mapping.Required,
                    mapping.Notes,
                    reasons = mapping.Reasons
                });
            edges.Add($"mapping-{edgeIndex++}", new
            {
                from = new[] { sourceNames[group.Key.SourceEntity] }, to = new[] { targetNames[group.Key.TargetEntity!] },
                operation = new { type = "transform", fields }, recovery = new { mode = "restore", requiresSnapshot = true }
            });
        }
        var root = new
        {
            proofshift = 1, project = new { id = "authored-project", name = "Reviewed Structural Mapping" }, packs = Array.Empty<object>(),
            systems = new { source = new { file = "systems/source.yaml" }, target = new { file = "systems/target.yaml" } },
            migration = new { graph = "migration/graph.yaml" }, verification = new { rules = "rules/rules.yaml" }, recovery = new { policy = "recovery/policy.yaml" }
        };
        await ProjectInitCommand.WriteAsync(directory, "proofshift.yaml", root);
        await ProjectInitCommand.WriteAsync(directory, "systems/source.yaml", SystemView(source.ConnectorId, "source", "source"));
        await ProjectInitCommand.WriteAsync(directory, "systems/target.yaml", SystemView(target.ConnectorId, "target", "shadow-target"));
        await ProjectInitCommand.WriteAsync(directory, "migration/graph.yaml", new
        {
            version = 1, nodes, edges,
            authoring = new { version = MappingScaffold.Format, review.SourceFingerprint, review.TargetFingerprint,
                mappings = review.Mappings.Select(mapping => new { source = mapping.SourceEntity + "." + mapping.SourceField,
                    target = mapping.TargetEntity is null ? null : mapping.TargetEntity + "." + mapping.TargetField,
                    approval = mapping.ReviewStatus, mapping.Required, mapping.Reasons }) }
        });
        await ProjectInitCommand.WriteAsync(directory, "rules/rules.yaml", new
        {
            version = 2, rules = new Dictionary<string, object>
            {
                ["accounting"] = new { type = "source-artifact-accounting", version = "1", severity = "critical" },
                ["lineage"] = new { type = "target-lineage", version = "1", severity = "critical" }
            }
        });
        await ProjectInitCommand.WriteAsync(directory, "recovery/policy.yaml", new
        {
            requireRecoveryForDestructiveOperations = true, allowIrreversible = new { @default = false },
            requireValidatedRestore = true, requireRecoveryRehearsal = true, maximumIrreversibleArtifacts = 0
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "mapping-review.json"), JsonSerializer.Serialize(review, PhysicalDiscovery.JsonOptions));
        var loaded = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(Path.Combine(directory, "proofshift.yaml"));
        if (loaded.Configuration is null) throw new InvalidDataException("Imported/generated project failed normal configuration validation.");
        var compiled = MigrationGraphCompiler.Compile(loaded.Configuration);
        if (!compiled.IsValid)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "authoring-diagnostics.json"), JsonSerializer.Serialize(compiled.Issues, PhysicalDiscovery.JsonOptions));
            if (requireCompiled) throw new InvalidDataException("Imported project failed normal graph compilation; resolve identity/coverage requirements.");
            Console.WriteLine("Scaffold is a review draft: normal graph validation found unresolved identity/coverage requirements; inspect authoring-diagnostics.json.");
        }
        _ = CliComposition.RulesFor(loaded.Configuration, Verification.VerificationRuleConfigurationLoader.Load(loaded.Configuration))
            .Resolve(Verification.VerificationRuleConfigurationLoader.Load(loaded.Configuration));
    }

    private static object Node(PhysicalObject entity, string type, string system, string connector) => new
    {
        type, system, storage = "records", semanticType = "Physical.Uninterpreted",
        selector = new
        {
            kind = connector is "postgres" or "sqlserver" or "oracle" or "db2"
                ? "table" : connector == "csv" ? "csv" : connector is "json" or "ndjson" or "xml" or "fixed-width" or "parquet" ? connector
                    : connector is "s3" or "azure-blob" or "sftp" ? "object-pattern" : "file-pattern",
            properties = connector is "postgres" or "sqlserver" or "oracle" or "db2"
                ? (entity.SelectorProperties ?? new Dictionary<string, string>())
                    .Append(new KeyValuePair<string, string>("name", MappingScaffold.Name(entity)))
                    .Append(new KeyValuePair<string, string>("columns", string.Join(',', entity.Fields.Select(field => field.Name))))
                    .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)
                : entity.SelectorProperties ?? new Dictionary<string, string> { [connector == "csv" ? "path" : "pattern"] = entity.Name },
            identity = entity.Keys.FirstOrDefault(key => key.Primary)?.Fields ?? []
        }
    };

    private static object SystemView(string connector, string id, string role) => new
    {
        id, name = id, role, storage = new Dictionary<string, object>
        {
            ["records"] = connector is "sqlserver" or "postgres" or "oracle" or "db2"
                ? (object)new { connector, connection = new { secret = id == "source" ? "PROOFSHIFT_SOURCE_CONNECTION" : "PROOFSHIFT_TARGET_CONNECTION" } }
                : connector is "s3" or "azure-blob" or "sftp" or "parquet" ? (object)RemoteEndpoint(connector, id == "source" ? "SOURCE" : "TARGET")
                : (object)new { connector, root = new { env = id == "source" ? "PROOFSHIFT_SOURCE_ROOT" : "PROOFSHIFT_TARGET_ROOT" } }
        }
    };

    // Credential-free skeletons: every location comes from the environment and every credential is a secret reference to fill in.
    private static Dictionary<string, object> RemoteEndpoint(string connector, string role) => connector switch
    {
        "s3" => new Dictionary<string, object>
        {
            ["connector"] = connector, ["bucket"] = new { env = $"PROOFSHIFT_{role}_BUCKET" }, ["region"] = new { env = $"PROOFSHIFT_{role}_REGION" },
            ["authentication"] = "default-chain"
        },
        "azure-blob" => new Dictionary<string, object>
        {
            ["connector"] = connector, ["account"] = new { env = $"PROOFSHIFT_{role}_ACCOUNT" }, ["container"] = new { env = $"PROOFSHIFT_{role}_CONTAINER" },
            ["authentication"] = "default-credential"
        },
        "sftp" => new Dictionary<string, object>
        {
            ["connector"] = connector, ["host"] = new { env = $"PROOFSHIFT_{role}_HOST" }, ["username"] = new { env = $"PROOFSHIFT_{role}_USER" },
            ["remoteRoot"] = new { env = $"PROOFSHIFT_{role}_REMOTE_ROOT" }, ["hostKeyFingerprint"] = new { env = $"PROOFSHIFT_{role}_HOST_KEY_FINGERPRINT" },
            ["authentication"] = "private-key", ["privateKey"] = new { secret = $"PROOFSHIFT_{role}_PRIVATE_KEY" }
        },
        _ => new Dictionary<string, object>
        {
            ["connector"] = connector, ["transport"] = "filesystem", ["root"] = new { env = $"PROOFSHIFT_{role}_ROOT" }
        }
    };

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: proofshift scaffold --source-discovery <dir> --target-discovery <dir> --output <new-dir>; proofshift mapping import --csv <file> --source-discovery <dir> --target-discovery <dir> --output <new-dir>");
        return 2;
    }
}