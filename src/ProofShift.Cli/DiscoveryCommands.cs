using System.Text.Json;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Csv;
using ProofShift.Connectors.Files;
using ProofShift.Connectors.Postgres;
using ProofShift.Connectors.SqlServer;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;

namespace ProofShift.Cli;

internal static class DiscoveryCommands
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "discovery" && args[1] == "diff")
            {
                var changes = PhysicalDiscoveryDiff.Compare(await PhysicalDiscoveryStore.ReadAsync(args[2], cancellationToken),
                    await PhysicalDiscoveryStore.ReadAsync(args[3], cancellationToken));
                if (args.Contains("--json", StringComparer.Ordinal)) Console.WriteLine(JsonSerializer.Serialize(changes, PhysicalDiscovery.JsonOptions));
                else foreach (var change in changes) Console.WriteLine($"{change.Kind}: {change.ObjectName}{(change.Field is null ? "" : "." + change.Field)}");
                return 0;
            }
            if (args.Length < 2) return Usage();
            string? systemName = null, endpointName = null, output = null;
            for (var index = 2; index < args.Length; index++)
            {
                if (args[index] == "--json") continue;
                if (index + 1 >= args.Length) return Usage();
                switch (args[index])
                {
                    case "--system": systemName = args[++index]; break;
                    case "--endpoint": endpointName = args[++index]; break;
                    case "--output": output = args[++index]; break;
                    default: return Usage();
                }
            }
            if (systemName is null || endpointName is null || output is null) return Usage();
            var loaded = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(args[1], cancellationToken);
            if (loaded.Configuration is not { } configuration) throw new InvalidDataException("Configuration must validate before discovery.");
            var system = configuration.Systems.SingleOrDefault(item => item.Id.Value == systemName)
                ?? throw new InvalidDataException("Unknown configured logical system.");
            var endpoint = system.StorageEndpoints.SingleOrDefault(item => item.Id.Value == endpointName)
                ?? throw new InvalidDataException("Unknown configured endpoint.");
            var graph = MigrationGraphCompiler.Compile(configuration);
            var nodes = graph.Graph?.Nodes.Where(node => node.SystemId == system.Id && node.EndpointId == endpoint.Id).ToArray() ?? [];
            var contextNode = nodes.FirstOrDefault() ?? new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "discovery",
                MigrationNodeType.Source, "Physical.Uninterpreted", system.Id, endpoint.Id, new ArtifactSelector("discovery"));
            var context = new RuntimeConnectorContextFactory().Create(configuration, contextNode);
            IPhysicalDiscoveryConnector connector = endpoint.Connector.Value switch
            {
                "sqlserver" => new SqlServerSourceConnector(), "postgres" => new PostgresSourceConnector(),
                "csv" => new CsvSourceConnector(), "files" => new FilesystemSourceConnector(),
                _ => throw new InvalidDataException("Configured connector has no physical discovery capability.")
            };
            if (endpoint.Connector.Value == "csv" && nodes.Length == 0)
                throw new InvalidDataException("CSV discovery requires a configured CSV selector.");
            var artifact = await connector.DiscoverAsync(context, nodes.Select(node => node.Selector).ToArray(), cancellationToken);
            await PhysicalDiscoveryStore.SaveAsync(Path.GetFullPath(output), artifact, cancellationToken);
            if (args.Contains("--json", StringComparer.Ordinal)) Console.WriteLine(JsonSerializer.Serialize(artifact, PhysicalDiscovery.JsonOptions));
            else Console.WriteLine($"Discovered {artifact.Objects.Count} physical objects [{artifact.ConnectorId} {artifact.ConnectorVersion}]; fingerprint {artifact.Fingerprint}");
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or
            ConnectorConfigurationException or ConnectorReadException or System.Data.Common.DbException or UnauthorizedAccessException or CsvHelper.CsvHelperException)
        {
            Console.Error.WriteLine($"PSDISC001: Physical discovery could not complete ({exception.GetType().Name}); verify configuration, endpoint access and artifact paths.");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: proofshift discover <config> --system <id> --endpoint <id> --output <new-directory> [--json]; proofshift discovery diff <before-directory> <after-directory> [--json]");
        return 2;
    }
}