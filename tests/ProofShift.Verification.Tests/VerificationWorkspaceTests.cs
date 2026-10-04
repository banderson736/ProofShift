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
                var graphDerivedSource = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedSourceFactsAsync(TestContext.Current.CancellationToken)),
                    fact => fact.NodeKey == "source-members");
                Assert.Equal(1, graphDerivedSource.ProducedEntries);
                var graphDerivedTarget = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedTargetFactsAsync(TestContext.Current.CancellationToken)));
                Assert.Equal(2, graphDerivedTarget.ActualCount);
                Assert.Empty(await CollectAsync(workspace.ReadMissingGraphDerivedTargetFactsAsync(TestContext.Current.CancellationToken)));
                var graphNodes = new Dictionary<string, MigrationNodeId>
                {
                    ["source-members"] = new MigrationNodeId(Guid.NewGuid()),
                    ["participant"] = new MigrationNodeId(Guid.NewGuid())
                };
                var expectedLineage = Assert.Single(await CollectAsync(workspace.ReadGraphDerivedLineageAsync(
                    new string('a', 64), graphNodes, TestContext.Current.CancellationToken)));
                Assert.Equal(LineageBasis.GraphDerivedExpected, expectedLineage.Basis);
                Assert.Empty(await CollectAsync(workspace.ReadTargetsWithoutGraphDerivedLineageAsync(TestContext.Current.CancellationToken)));
            }

            Assert.False(Directory.Exists(scratchDirectory));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactRecordStreamPreservesTypedValuesRelationshipsAndTemporalMetadata()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot, TestContext.Current.CancellationToken);
            var member = Record("member-1", "source", "members", "7", "Pension.Member", "status", "ACTIVE");
            var employmentArtifact = new ArtifactReference(new ArtifactId("employment-1"), new SystemId("source"),
                new StorageEndpointId("employment"), "row", "7|2020-01-01");
            var employment = new RecordEnvelope(employmentArtifact, "Pension.Employment",
                [new KeyValuePair<string, ValueNode>("member_id", new IntegerValue(7)),
                 new KeyValuePair<string, ValueNode>("credit", new DecimalValue(0.875m)),
                 new KeyValuePair<string, ValueNode>("effective_from", new DateValue(new DateOnly(2020, 1, 1)))],
                member.Provenance,
                [new RelationshipReference("HAS_EMPLOYMENT", member.Artifact, RelationshipDirection.Outgoing)],
                new TemporalMetadata(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null));

            await workspace.AddSourceArtifactAsync("employment-history", employment, TestContext.Current.CancellationToken);

            var actual = Assert.Single(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.Source, "employment-history", "Pension.Employment", TestContext.Current.CancellationToken)));
            Assert.Equal(employment.Artifact, actual.Artifact);
            Assert.Equal(employment.Values, actual.Values);
            Assert.Equal(employment.Relationships, actual.Relationships);
            Assert.Equal(employment.Temporal, actual.Temporal);
            Assert.Empty(await CollectAsync(workspace.ReadArtifactRecordsAsync(
                VerificationArtifactRole.ActualTarget, null, null, TestContext.Current.CancellationToken)));

            await workspace.AddSourceArtifactAsync("ordered-members",
                Record("source-z", "source", "members", "z", "Pension.Member", "member_id", "z"), TestContext.Current.CancellationToken);
            await workspace.AddSourceArtifactAsync("ordered-members",
                Record("source-a", "source", "members", "a", "Pension.Member", "member_id", "a"), TestContext.Current.CancellationToken);
            var ordered = await CollectAsync(workspace.ReadArtifactRecordsAsync(VerificationArtifactRole.Source,
                "ordered-members", "Pension.Member", TestContext.Current.CancellationToken, ["member_id"]));
            Assert.Equal(["a", "z"], ordered.Select(record => record.Artifact.Identity));
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
