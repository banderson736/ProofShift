using ProofShift.Domain;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationWorkspaceTests
{
    [Fact]
    public async Task TemporaryWorkspaceIndexesJournalAndTargetObservationsThenDeletesScratchDirectory()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            string scratchDirectory;
            await using (var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot, TestContext.Current.CancellationToken))
            {
                scratchDirectory = Directory.GetDirectories(temporaryRoot).Single();
                var source = Record("source-1", "source", "members", "member-1", "Pension.Member", "status", "A");
                var target = Record("target-1", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
                var edgeId = new MigrationEdgeId(Guid.NewGuid());
                var edge = new MigrationEdge(edgeId, "member-map", [new MigrationNodeId(Guid.NewGuid())],
                    [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Transform,
                        fields: [new TransformationFieldDefinition("status", "status",
                            [new TransformationStep(TransformationStepType.CodeMap, "1", [new KeyValuePair<string, string>("A", "ACTIVE")])])]),
                    "1", new RecoveryDefinition(RecoveryMode.Reverse));

                await workspace.AddSourceArtifactAsync("source-members", source, TestContext.Current.CancellationToken);
                await workspace.AddSourceArtifactAsync("source-archive", source, TestContext.Current.CancellationToken);
                await workspace.AddExpectedTargetAsync("participant", target, "source-members", source, edge, TestContext.Current.CancellationToken);
                await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "participant", target.Artifact,
                    [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null), TestContext.Current.CancellationToken);
                await workspace.AddTargetObservationAsync("participant", target, TestContext.Current.CancellationToken);
                var duplicate = Record("target-duplicate", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
                await workspace.AddTargetObservationAsync("participant", duplicate, TestContext.Current.CancellationToken);

                Assert.Equal(2, workspace.SourceArtifactCount);
                Assert.Equal(1, workspace.ExpectedTargetCount);
                Assert.Equal(2, workspace.ActualTargetCount);
                var sourceFacts = await CollectAsync(workspace.ReadSourceFactsAsync(TestContext.Current.CancellationToken));
                Assert.Equal(2, sourceFacts.Count);
                var sourceFact = Assert.Single(sourceFacts, fact => fact.NodeKey == "source-members");
                Assert.Equal(1, sourceFact.ProducedEntries);
                var targetFact = Assert.Single(await CollectAsync(workspace.ReadMaterializedJournalTargetsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, targetFact.ActualCount);
                var duplicateFact = Assert.Single(await CollectAsync(workspace.ReadDuplicateTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, duplicateFact.ActualCount);
                Assert.Empty(await CollectAsync(workspace.ReadMissingTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Empty(await CollectAsync(workspace.ReadUnexpectedTargetFactsAsync(TestContext.Current.CancellationToken)));
            }

            Assert.False(Directory.Exists(scratchDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static RecordEnvelope Record(string artifactId, string systemId, string nodeEndpoint, string identity,
        string semanticType, string field, string value) => new(
        new ArtifactReference(new ArtifactId(artifactId), new SystemId(systemId), new StorageEndpointId(nodeEndpoint), "row", identity),
        semanticType,
        [new KeyValuePair<string, ValueNode>(field, new StringValue(value))],
        new ProvenanceMetadata(new ConnectorId("synthetic"), new StorageEndpointId(nodeEndpoint), identity, DateTimeOffset.UnixEpoch));

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> values)
    {
        var result = new List<T>();
        await foreach (var value in values.WithCancellation(TestContext.Current.CancellationToken)) result.Add(value);
        return result;
    }
}
