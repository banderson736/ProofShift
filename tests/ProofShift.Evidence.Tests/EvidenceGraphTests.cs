using ProofShift.Domain;
using ProofShift.Evidence;
using Xunit;

namespace ProofShift.Evidence.Tests;

public sealed class EvidenceGraphTests
{
    [Fact]
    public void EquivalentEvidenceGraphsHaveTheSameFingerprintRegardlessOfRunIdsAndTimestamps()
    {
        var first = CreateGraph(new RunId(Guid.NewGuid()), DateTimeOffset.UtcNow);
        var second = CreateGraph(new RunId(Guid.NewGuid()), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(EvidenceFormat.CanonicalizationVersion, first.CanonicalizationVersion);
    }

    [Fact]
    public void EvidenceGraphRejectsDanglingAndCyclicEvidenceReferences()
    {
        var runId = new RunId(Guid.NewGuid());
        var firstId = new EvidenceId(Guid.NewGuid());
        var missingId = new EvidenceId(Guid.NewGuid());
        var dangling = Record(firstId, runId, [new EvidenceReference(evidenceId: missingId)]);
        Assert.Throws<ArgumentException>(() => new EvidenceGraph(runId, [dangling]));

        var secondId = new EvidenceId(Guid.NewGuid());
        var first = Record(firstId, runId, [new EvidenceReference(evidenceId: secondId)]);
        var second = Record(secondId, runId, [new EvidenceReference(evidenceId: firstId)]);
        Assert.Throws<ArgumentException>(() => new EvidenceGraph(runId, [first, second]));
    }

    [Fact]
    public async Task FileSystemStoreDetectsEvidenceFileTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-test-{Guid.NewGuid():N}");
        try
        {
            var graph = CreateGraph(new RunId(Guid.NewGuid()), DateTimeOffset.UtcNow);
            graph = new EvidenceGraph(graph.VerificationRunId, graph.Records,
                new EvidenceVerificationContext(new string('a', 64), new string('b', 64), Guid.NewGuid().ToString("D"),
                    new string('c', 64), new string('d', 64), new string('e', 64)));
            var store = new FileSystemEvidenceStore(root);
            var receipt = await store.SaveAsync(graph, TestContext.Current.CancellationToken);
            Assert.True(await store.VerifyIntegrityAsync(graph.VerificationRunId, TestContext.Current.CancellationToken));
            var manifest = await store.ReadManifestAsync(graph.VerificationRunId, TestContext.Current.CancellationToken);
            Assert.True(manifest.Complete);
            Assert.Equal(EvidenceFormat.CanonicalizationVersion, manifest.CanonicalizationVersion);
            Assert.Equal(2, manifest.RecordCount);
            Assert.Equal(graph.VerificationContext, manifest.VerificationContext);
            var streamedRecords = 0;
            await foreach (var _ in store.ReadRecordsAsync(graph.VerificationRunId, TestContext.Current.CancellationToken)
                .WithCancellation(TestContext.Current.CancellationToken))
                streamedRecords++;
            Assert.Equal(2, streamedRecords);

            var path = Path.Combine(root, graph.VerificationRunId.Value.ToString("N"), "evidence.ndjson");
            await File.AppendAllTextAsync(path, "tampered", TestContext.Current.CancellationToken);
            Assert.False(await store.VerifyIntegrityAsync(graph.VerificationRunId, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StreamingWriterPreservesFingerprintAndRejectsUnorderedIncompleteOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-evidence-stream-test-{Guid.NewGuid():N}");
        try
        {
            var graph = CreateGraph(new RunId(Guid.NewGuid()), DateTimeOffset.UtcNow);
            var store = new FileSystemEvidenceStore(root);
            var receipt = await store.SaveAsync(graph.VerificationRunId, graph.CanonicalizationVersion,
                graph.Fingerprint, graph.Records.Count, graph.VerificationContext, StreamRecords(graph.Records), TestContext.Current.CancellationToken);

            Assert.Equal(graph.Fingerprint, receipt.Fingerprint);
            Assert.Equal(graph.Records.Count, receipt.RecordCount);
            Assert.True(await store.VerifyIntegrityAsync(graph.VerificationRunId, TestContext.Current.CancellationToken));
            var streamedIds = new List<string>();
            await foreach (var record in store.ReadRecordsAsync(graph.VerificationRunId, TestContext.Current.CancellationToken)
                .WithCancellation(TestContext.Current.CancellationToken))
                streamedIds.Add(record.GetProperty("id").GetString()!);
            Assert.Equal(graph.Records.Select(record => record.Id.Value.ToString("D")).Order(StringComparer.Ordinal), streamedIds);

            var badRun = new RunId(Guid.NewGuid());
            var badRoot = Path.Combine(root, "bad");
            var descending = StreamRecords(graph.Records.Reverse());
            await Assert.ThrowsAsync<InvalidDataException>(() => new FileSystemEvidenceStore(badRoot).SaveAsync(
                badRun, graph.CanonicalizationVersion, graph.Fingerprint, graph.Records.Count, graph.VerificationContext, descending,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(badRoot, badRun.Value.ToString("N"), "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async IAsyncEnumerable<EvidenceRecord> StreamRecords(IEnumerable<EvidenceRecord> records)
    {
        foreach (var record in records)
        {
            yield return record;
            await Task.Yield();
        }
    }

    private static EvidenceGraph CreateGraph(RunId runId, DateTimeOffset evaluatedAt)
    {
        var observationId = new EvidenceId(Guid.Parse("d5905032-fd7e-4617-aa1a-000000000001"));
        var comparisonId = new EvidenceId(Guid.Parse("d5905032-fd7e-4617-aa1a-000000000002"));
        var observation = new EvidenceRecord(observationId, runId, EvidenceType.Observation, new RuleId("target-observation"), "1",
            EvidenceResult.Pass, [new EvidenceReference(artifactId: new ArtifactId("target-1"))], "Target observed.", evaluatedAt,
            actual: new EvidenceValue(new StringValue("ACTIVE")), severity: EvidenceSeverity.Info);
        var comparison = new EvidenceRecord(comparisonId, runId, EvidenceType.Comparison, new RuleId("member-status"), "1",
            EvidenceResult.Pass, [new EvidenceReference(evidenceId: observationId), new EvidenceReference(ruleId: new RuleId("member-status"))],
            "Expected and actual values match.", evaluatedAt.AddSeconds(1), new EvidenceValue(new StringValue("ACTIVE")),
            new EvidenceValue(new StringValue("ACTIVE")), EvidenceSeverity.Critical, "AttributeMismatch");
        return new EvidenceGraph(runId, [comparison, observation]);
    }

    private static EvidenceRecord Record(EvidenceId id, RunId runId, IEnumerable<EvidenceReference> inputs) => new(
        id, runId, EvidenceType.Comparison, new RuleId("test-rule"), "1", EvidenceResult.Pass,
        inputs, "A test finding.", DateTimeOffset.UnixEpoch);
}
