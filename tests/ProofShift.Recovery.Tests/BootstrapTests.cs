using ProofShift.Domain;
using Xunit;

namespace ProofShift.Recovery.Tests;

public sealed class RecoveryDefinitionTests
{
    [Fact]
    public void DestructiveEdgeRequiresExplicitRecoveryDefinition()
    {
        var operation = new MigrationOperation(MigrationOperationType.Transform, isDestructive: true);
        var from = new MigrationNodeId(Guid.NewGuid());
        var to = new MigrationNodeId(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new MigrationEdge(
            new MigrationEdgeId(Guid.NewGuid()), from, to, operation, "1"));

        var edge = new MigrationEdge(
            new MigrationEdgeId(Guid.NewGuid()), from, to, operation, "1",
            new RecoveryDefinition(RecoveryMode.Compensate, "rebuild-from-audit"));

        Assert.Equal(RecoveryMode.Compensate, edge.Recovery!.Mode);
    }
}
