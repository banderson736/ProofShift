using ProofShift.Domain;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class DomainAccountingFlowTests
{
    [Fact]
    public void AccountedSourceAndLineageBackedTargetPassCoverage()
    {
        var source = new ArtifactReference(
            new ArtifactId("source-member-1"), new SystemId("legacy"),
            new StorageEndpointId("member-store"), "record", "M-1");
        var target = new ArtifactReference(
            new ArtifactId("target-member-1"), new SystemId("modern"),
            new StorageEndpointId("participant-store"), "record", "M-1");
        var edgeId = new MigrationEdgeId(Guid.NewGuid());
        var dispositions = new ArtifactDispositionLedger(
        [
            new ArtifactDispositionRecord(source, ArtifactDisposition.Transformed, [target])
        ]);
        var lineages = new LineageLedger(
        [
            new LineageRecord(target, [source], [edgeId], "plan-fingerprint-v1")
        ]);

        Assert.Empty(dispositions.ValidateCoverage([source]));
        Assert.Empty(lineages.ValidateCoverage([target]));
    }
}
