using ProofShift.Domain;
using Xunit;

namespace ProofShift.Graph.Tests;

public sealed class MigrationGraphTests
{
    [Fact]
    public void GraphValidationReportsDanglingNodeReferences()
    {
        var knownNodeId = new MigrationNodeId(Guid.NewGuid());
        var danglingNodeId = new MigrationNodeId(Guid.NewGuid());
        var edge = new MigrationEdge(
            new MigrationEdgeId(Guid.NewGuid()),
            knownNodeId,
            danglingNodeId,
            new MigrationOperation(MigrationOperationType.Copy),
            "1");
        var graph = new MigrationGraph(
            new MigrationGraphId(Guid.NewGuid()),
            [new MigrationNode(knownNodeId, "source", MigrationNodeType.Source, "Generic.Record", new SystemId("source"), new StorageEndpointId("store"))],
            [edge],
            "opaque-hash");

        var issue = Assert.Single(graph.Validate());

        Assert.Equal(MigrationGraphIssueCode.DanglingToNode, issue.Code);
        Assert.Equal(danglingNodeId, issue.NodeId);
    }
}
