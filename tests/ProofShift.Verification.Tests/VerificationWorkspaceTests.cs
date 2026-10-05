using System.Text;
using ProofShift.Domain;
using ProofShift.Verification;
using Xunit;

namespace ProofShift.Verification.Tests;

public sealed class VerificationWorkspaceTests
{
    [Fact]
    public async Task JournalValidationUsesIndexedBindingsAndRejectsMissingAndDuplicateAncestry()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"proofshift-verification-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await using var workspace = await SqliteVerificationWorkspace.CreateAsync(temporaryRoot,
                TestContext.Current.CancellationToken);
            var source = Record("source-1", "source", "members", "member-1", "Pension.Member", "status", "A");
            var target = Record("target-1", "shadow", "participant", "member-1", "Pension.Member", "status", "ACTIVE");
            var edgeId = new MigrationEdgeId(Guid.NewGuid());
            var edge = new MigrationEdge(edgeId, "member-map", [new MigrationNodeId(Guid.NewGuid())],
                [new MigrationNodeId(Guid.NewGuid())], new MigrationOperation(MigrationOperationType.Transform),
                "1", new RecoveryDefinition(RecoveryMode.Reverse));

            await workspace.AddSourceArtifactAsync("source-members", source, TestContext.Current.CancellationToken);
            await workspace.AddExpectedTargetAsync("participant", target, "source-members", source, edge,
                TestContext.Current.CancellationToken);
            var validEntry = new VerificationJournalEntry("produced", "participant", target.Artifact,
                [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null);
            await workspace.AddJournalEntryAsync(validEntry, TestContext.Current.CancellationToken);

            var valid = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, valid.ProducedEntryCount);
            Assert.True(valid.SourceArtifactsMatch);
            Assert.True(valid.ProducedTargetsMatchExpectations);
            Assert.True(valid.ProducedAncestryIsUnique);

            var missingSource = Record("missing-source", "source", "members", "missing", "Pension.Member", "status", "A");
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("excluded", null, null,
                [new VerificationGraphArtifact("source-members", missingSource.Artifact)], edgeId, "member-map", "1", null),
                TestContext.Current.CancellationToken);
            var missingBinding = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(missingBinding.SourceArtifactsMatch);

            await workspace.AddJournalEntryAsync(validEntry, TestContext.Current.CancellationToken);
            var duplicate = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(duplicate.ProducedAncestryIsUnique);

            var mismatchedTarget = Record("target-other", "shadow", "participant", "member-other", "Pension.Member", "status", "ACTIVE");
            await workspace.AddJournalEntryAsync(new VerificationJournalEntry("produced", "participant", mismatchedTarget.Artifact,
                [new VerificationGraphArtifact("source-members", source.Artifact)], edgeId, "member-map", "1", null),
                TestContext.Current.CancellationToken);
            var mismatch = await workspace.ValidateJournalEntriesAsync(TestContext.Current.CancellationToken);
            Assert.False(mismatch.ProducedTargetsMatchExpectations);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedVerificationLedgerStreamsOpaqueRecordsAndFingerprintsIndependentOfAppendOrder()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-test-{Guid.NewGuid():N}");
        var secondRoot = Path.Combine(Path.GetTempPath(), $"proofshift-ledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var sourceNodeId = new MigrationNodeId(Guid.Parse("10000000-0000-0000-0000-000000000001"));
            var targetNodeId = new MigrationNodeId(Guid.Parse("20000000-0000-0000-0000-000000000002"));
            var edgeId = new MigrationEdgeId(Guid.Parse("30000000-0000-0000-0000-000000000003"));
            var sourceOne = Record("source-ledger-1", "source", "members", "private-source-identity-1", "Pension.Member", "status", "A");
            var sourceTwo = Record("source-ledger-2", "source", "members", "private-source-identity-2", "Pension.Member", "status", "A");
            var targetOne = Record("target-ledger-1", "shadow", "participant", "private-target-identity-1", "Pension.Member", "status", "ACTIVE");
            var targetTwo = Record("target-ledger-2", "shadow", "participant", "private-target-identity-2", "Pension.Member", "status", "ACTIVE");
            var edge = new MigrationEdge(edgeId, "member-map", [sourceNodeId], [targetNodeId],
                new MigrationOperation(MigrationOperationType.Transform), "1", new RecoveryDefinition(RecoveryMode.Reverse));
            var dispositions = new[]
            {
                new ArtifactDispositionRecord(sourceOne.Artifact, ArtifactDisposition.Transformed, [targetOne.Artifact],
                    sourceNodeId: sourceNodeId, targetNodeIds: [targetNodeId]),
                new ArtifactDispositionRecord(sourceTwo.Artifact, ArtifactDisposition.Transformed, [targetTwo.Artifact],
                    sourceNodeId: sourceNodeId, targetNodeIds: [targetNodeId])
            };
            var lineages = new[]
            {
                new LineageRecord(targetOne.Artifact, [sourceOne.Artifact], [edgeId], "graph-hash", targetNodeId,
                    [sourceNodeId], LineageBasis.ExecutionObserved),
                new LineageRecord(targetTwo.Artifact, [sourceTwo.Artifact], [edgeId], "graph-hash", targetNodeId,
                    [sourceNodeId], LineageBasis.ExecutionObserved)
            };
            var journal = new[]
            {
                new VerificationJournalEntry("produced", "participant", targetOne.Artifact,
                    [new VerificationGraphArtifact("source-members", sourceOne.Artifact)], edgeId, edge.Name, edge.Version, null),
                new VerificationJournalEntry("produced", "participant", targetTwo.Artifact,
                    [new VerificationGraphArtifact("source-members", sourceTwo.Artifact)], edgeId, edge.Name, edge.Version, null)
            };

            await using var first = await SqliteVerificationLedgerStore.CreateAsync(firstRoot,
                new RunId(Guid.NewGuid()), TestContext.Current.CancellationToken);
            await using var second = await SqliteVerificationLedgerStore.CreateAsync(secondRoot,
                new RunId(Guid.NewGuid()), TestContext.Current.CancellationToken);
            foreach (var store in new[] { first, second })
            {
                await store.RegisterGraphNodeAsync("source-members", sourceNodeId, TestContext.Current.CancellationToken);
                await store.RegisterGraphNodeAsync("participant", targetNodeId, TestContext.Current.CancellationToken);
                await store.RegisterSourceAsync("source-members", sourceNodeId, sourceOne.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterSourceAsync("source-members", sourceNodeId, sourceTwo.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterTargetAsync("participant", targetNodeId, targetOne.Artifact, TestContext.Current.CancellationToken);
                await store.RegisterTargetAsync("participant", targetNodeId, targetTwo.Artifact, TestContext.Current.CancellationToken);
            }

            foreach (var item in dispositions)
            {
                await first.AppendDispositionAsync(item, TestContext.Current.CancellationToken);
                await first.AppendLineageAsync(lineages.Single(lineage => lineage.Target.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
                await first.AppendJournalEntryAsync(journal.Single(entry => entry.Target!.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
            }
            foreach (var item in dispositions.Reverse())
            {
                await second.AppendDispositionAsync(item, TestContext.Current.CancellationToken);
                await second.AppendLineageAsync(lineages.Single(lineage => lineage.Target.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
                await second.AppendJournalEntryAsync(journal.Single(entry => entry.Target!.Id == item.Targets[0].Id), TestContext.Current.CancellationToken);
            }

            var firstSummary = await first.CompleteAsync(TestContext.Current.CancellationToken);
            var secondSummary = await second.CompleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(firstSummary.Fingerprint, secondSummary.Fingerprint);
            Assert.Equal(2, firstSummary.SourceCount);
            Assert.Equal(2, firstSummary.TargetCount);
            Assert.Equal(2, firstSummary.DispositionCount);
            Assert.Equal(2, firstSummary.LineageCount);
            Assert.Equal(2, firstSummary.JournalEntryCount);
            Assert.True((await first.ValidateCoverageAsync(TestContext.Current.CancellationToken)).IsValid);
            Assert.True(await first.HasJournalEntryForEdgeAsync(edgeId, "produced", TestContext.Current.CancellationToken));
            var edgeScopeCounts = await first.ReadJournalScopeCountsAsync(edgeId, TestContext.Current.CancellationToken);
            Assert.Equal(2, edgeScopeCounts.SourceCount);
            Assert.Equal(2, edgeScopeCounts.TargetCount);
            Assert.Equal(4, (await CollectAsync(first.ReadDistinctJournalScopeAsync(edgeId,
                TestContext.Current.CancellationToken))).Count);
            var groupedLineage = await CollectAsync(first.ReadLineageTargetsBySourceAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, groupedLineage.Count);
            Assert.All(groupedLineage, item => Assert.Single(item.Targets));

            var disposition = Assert.IsType<ArtifactDispositionRecord>(await first.FindDispositionAsync(sourceNodeId,
                sourceOne.Artifact.Id, TestContext.Current.CancellationToken));
            Assert.Equal($"opaque:{sourceOne.Artifact.Id.Value}", disposition.Source.Identity);
            var lineage = Assert.Single(await CollectAsync(first.FindLineageBySourceAsync(sourceNodeId,
                sourceOne.Artifact.Id, TestContext.Current.CancellationToken)));
            Assert.Equal(targetOne.Artifact.Id, lineage.Target.Id);
            Assert.Equal(2, (await CollectAsync(first.ReadJournalEntriesAsync(TestContext.Current.CancellationToken))).Count);
            await first.DisposeAsync();
            await second.DisposeAsync();
            await using (var reopened = await SqliteVerificationLedgerStore.OpenAsync(firstRoot, first.Receipt,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(firstSummary, await reopened.ReadSummaryAsync(TestContext.Current.CancellationToken));
                Assert.True((await reopened.ValidateCoverageAsync(TestContext.Current.CancellationToken)).IsValid);
            }

            var files = Directory.GetFiles(Path.Combine(firstRoot, ".proofshift", "verification-ledgers"), "*", SearchOption.AllDirectories);
            var serializedFiles = string.Join("\n", files.Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
            Assert.DoesNotContain("private-source-identity", serializedFiles, StringComparison.Ordinal);
            Assert.DoesNotContain("private-target-identity", serializedFiles, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
        }
    }

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

            RecordEnvelope OrderedRecord(string id, string group, long sequence, DateOnly effectiveDate, decimal amount)
            {
                var artifact = new ArtifactReference(new ArtifactId($"order-{id}"), new SystemId("source"),
                    new StorageEndpointId("members"), "row", id);
                return new RecordEnvelope(artifact, "Generic.OrderedRecord",
                    [new KeyValuePair<string, ValueNode>("group", new StringValue(group)),
                     new KeyValuePair<string, ValueNode>("sequence", new IntegerValue(sequence)),
                     new KeyValuePair<string, ValueNode>("effective_date", new DateValue(effectiveDate)),
                     new KeyValuePair<string, ValueNode>("amount", new DecimalValue(amount))],
                    member.Provenance);
            }

            foreach (var record in new[]
            {
                OrderedRecord("a-sequence-1-low", "A", 1, new DateOnly(2025, 1, 1), 2.5m),
                OrderedRecord("a-sequence-1-high", "A", 1, new DateOnly(2025, 1, 1), 10m),
                OrderedRecord("a-sequence-2", "A", 2, new DateOnly(2025, 1, 1), 5m),
                OrderedRecord("a-next-day", "A", 1, new DateOnly(2025, 1, 2), 1m),
                OrderedRecord("b-first", "B", 1, new DateOnly(2025, 1, 1), 100m)
            })
                await workspace.AddSourceArtifactAsync("typed-order", record, TestContext.Current.CancellationToken);

            var typedOrder = await CollectAsync(workspace.ReadArtifactRecordsByKeysAsync(VerificationArtifactRole.Source,
                "typed-order", "Generic.OrderedRecord",
                [new VerificationOrderingKey("Generic.OrderedRecord", "group", VerificationOrderingRole.Grouping),
                 new VerificationOrderingKey("Generic.OrderedRecord", "effective_date", VerificationOrderingRole.Ordering),
                 new VerificationOrderingKey("Generic.OrderedRecord", "sequence", VerificationOrderingRole.Ordering),
                 new VerificationOrderingKey("Generic.OrderedRecord", "amount", VerificationOrderingRole.Ordering, Descending: true)],
                TestContext.Current.CancellationToken));
            Assert.Equal(["a-sequence-1-high", "a-sequence-1-low", "a-sequence-2", "a-next-day", "b-first"],
                typedOrder.Select(record => record.Artifact.Identity));
            Assert.True(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "typed-order",
                "Generic.OrderedRecord", "group", new StringValue("A"), TestContext.Current.CancellationToken));
            Assert.False(await workspace.ContainsFieldValueAsync(VerificationArtifactRole.Source, "typed-order",
                "Generic.OrderedRecord", "group", new StringValue("missing"), TestContext.Current.CancellationToken));
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
