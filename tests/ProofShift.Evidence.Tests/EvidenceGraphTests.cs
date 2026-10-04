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
            var store = new FileSystemEvidenceStore(root);
            var receipt = await store.SaveAsync(graph, TestContext.Current.CancellationToken);
            Assert.True(await store.VerifyIntegrityAsync(graph.VerificationRunId, TestContext.Current.CancellationToken));

            var path = Path.Combine(root, receipt.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(path, "tampered", TestContext.Current.CancellationToken);
            Assert.False(await store.VerifyIntegrityAsync(graph.VerificationRunId, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
