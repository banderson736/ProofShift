using System.Text;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Snapshots;

namespace ProofShift.Connectors.RemoteStorage.Tests;

[CollectionDefinition("RemoteStorage", DisableParallelization = true)]
public sealed class RemoteStorageFixtureGroup;

internal sealed class DictionaryEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentVariableProvider
{
    public string? GetValue(string name) => values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Drives the real checkpoint capture and offline replay path for any source connector.</summary>
internal static class CheckpointHarness
{
    public static ConnectorContext Context(string connector, IEnumerable<KeyValuePair<string, string>> plainSettings,
        IReadOnlyDictionary<string, string> secrets, string semanticType = "File.Artifact") =>
        new("remote-system", "remote-endpoint", new ConnectorId(connector), "remote-node", semanticType, new RuntimeConfiguration(
            plainSettings.Select(pair => new KeyValuePair<string, RuntimeSetting>(pair.Key, RuntimeSetting.FromRuntimeValue(pair.Value)))
                .Concat(secrets.Select(pair => new KeyValuePair<string, RuntimeSetting>(pair.Key, RuntimeSetting.FromRuntimeValue(pair.Value, true))))));

    public sealed record Replay(SnapshotCaptureResult Capture, IReadOnlyList<RecordEnvelope> Records,
        IReadOnlyDictionary<string, byte[]> Binaries);

    public static async Task<Replay> CaptureAndReplayAsync(ISourceConnector connector, string connectorId,
        IReadOnlyDictionary<string, string> endpointSettings, IReadOnlyDictionary<string, string> environment,
        ArtifactSelector selector, string semanticType, Func<Task>? afterCapture, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-remote-checkpoint-{Guid.NewGuid():N}");
        try
        {
            var system = new SystemDefinition(new SystemId("remote-system"), "Remote Source", SystemRole.Source,
                [new StorageEndpointDefinition(new StorageEndpointId("remote-endpoint"), new ConnectorId(connectorId), endpointSettings)]);
            var node = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "remote-node", MigrationNodeType.Source,
                semanticType, system.Id, system.StorageEndpoints[0].Id, selector);
            var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [node], [], new string('a', 64), "remote-test-v1");
            var configuration = new LoadedProjectConfiguration(new RootConfigurationDto(1,
                new ProjectConfigurationDto("remote", "Remote"), null, [], null, null, null), [], [system], [], "remote", new string('b', 64));
            var factory = new RuntimeConnectorContextFactory(new DictionaryEnvironment(environment));
            var store = new FileSystemSnapshotStore(root);
            var capture = await new SnapshotCaptureService(new ConnectorRegistry([connector]), store, factory)
                .CaptureAsync(configuration, graph, cancellationToken);
            if (capture.Status != CheckpointStatus.Complete || afterCapture is null && capture.Checkpoint is null)
                return new Replay(capture, [], new Dictionary<string, byte[]>());
            if (afterCapture is not null) await afterCapture();
            await using var loaded = await store.OpenCompleteAsync(capture.Id.Value.ToString("N"), cancellationToken);
            var replay = new CheckpointSourceArtifactStreamProvider(loaded);
            var context = factory.Create(configuration, node);
            _ = await replay.ValidateAsync(node.Name, context, selector, cancellationToken);
            var records = new List<RecordEnvelope>();
            var binaries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            await foreach (var record in replay.ReadAsync(node.Name, context, selector, cancellationToken))
            {
                records.Add(record);
                foreach (var binary in record.Values.Values.OfType<BinaryReferenceValue>())
                {
                    await using var stream = await replay.OpenBinaryReadAsync(node.Name, context, selector, record.Artifact, binary, cancellationToken);
                    using var copy = new MemoryStream();
                    await stream.CopyToAsync(copy, cancellationToken);
                    binaries[record.Artifact.Identity] = copy.ToArray();
                }
            }

            return new Replay(capture, records, binaries);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
