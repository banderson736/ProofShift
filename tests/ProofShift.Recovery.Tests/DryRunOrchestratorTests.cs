using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Evidence;
using ProofShift.Graph;
using ProofShift.Recovery;
using ProofShift.Snapshots;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Recovery.Tests;

public sealed class DryRunOrchestratorTests
{
    [Fact]
    public async Task CancelledDryRunCannotQualifyOrCreateAProjection()
    {
        var fixture = CreateFixture();
        var root = TemporaryDirectory();
        try
        {
            var snapshotStore = new FileSystemSnapshotStore(Path.Combine(root, ".proofshift", "checkpoints"));
            var orchestrator = CreateOrchestrator(snapshotStore);
            var rules = CreateRules();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await orchestrator.RunAsync(fixture.Configuration, fixture.Graph, rules,
                new EffectiveRecoveryPolicy(), root, Path.Combine(root, ".proofshift", "temporary"), "0.1.0",
                new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications")),
                new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery")), cancellation.Token);

            Assert.Equal(DryRunExecutionOutcome.Cancelled, result.Outcome);
            Assert.Null(result.Projection);
            Assert.Null(result.Verification);
            Assert.Null(result.Recovery);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectionCannotStartWhenSourceCheckpointCaptureFails()
    {
        var fixture = CreateFixture();
        var root = TemporaryDirectory();
        try
        {
            var snapshotStore = new FileSystemSnapshotStore(Path.Combine(root, ".proofshift", "checkpoints"));
            var result = await CreateOrchestrator(snapshotStore).RunAsync(fixture.Configuration, fixture.Graph,
                CreateRules(), new EffectiveRecoveryPolicy(), root, Path.Combine(root, ".proofshift", "temporary"),
                "0.1.0", new FileSystemEvidenceStore(Path.Combine(root, ".proofshift", "verifications")),
                new FileSystemRecoveryArtifactStore(Path.Combine(root, ".proofshift", "recovery")),
                TestContext.Current.CancellationToken);

            Assert.Equal(DryRunExecutionOutcome.Failed, result.Outcome);
            Assert.Equal(CheckpointStatus.Failed, result.Checkpoint?.Status);
            Assert.Null(result.Projection);
            Assert.Null(result.Verification);
            Assert.Null(result.Recovery);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DryRunOrchestrator CreateOrchestrator(IMaterializedSnapshotStore store) => new(
        new ConnectorRegistry([]), new ShadowTargetConnectorRegistry([]), store,
        new VerificationService(store),
        new RecoveryService(store, new RecoveryCompensatorRegistry([])));

    private static VerificationRuleSet CreateRules() => new VerificationRuleRegistry(
        [new GenericVerificationRuleProvider()]).Resolve(
        [new VerificationRuleDefinition(new RuleId("source-accounting"), "source-artifact-accounting", "1", EvidenceSeverity.Error)]);

    private static Fixture CreateFixture()
    {
        var endpoint = new StorageEndpointDefinition(new StorageEndpointId("source-store"), new ConnectorId("missing-source"));
        var sourceSystem = new SystemDefinition(new SystemId("source-system"), "Synthetic Source", SystemRole.Source, [endpoint]);
        var root = new RootConfigurationDto(1, new ProjectConfigurationDto("dry-run-test", "Dry Run Test"), null, [], null, null, null);
        var systemDto = new SystemConfigurationDto(sourceSystem.Id.Value, sourceSystem.Name, "source",
            [new StorageEndpointConfigurationDto(endpoint.Id.Value, endpoint.Connector.Value, [])]);
        var configuration = new LoadedProjectConfiguration(root, [systemDto], [sourceSystem], [], "canonical-test", new string('a', 64));
        var source = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "source", MigrationNodeType.Source,
            "Synthetic.Record", sourceSystem.Id, endpoint.Id,
            new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "synthetic")], ["id"]));
        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [source], [], new string('b', 64), "test-v1");
        return new Fixture(configuration, graph);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-dry-run-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record Fixture(LoadedProjectConfiguration Configuration, MigrationGraph Graph);
}