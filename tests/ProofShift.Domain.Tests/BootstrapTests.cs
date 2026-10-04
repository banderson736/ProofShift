using Xunit;

namespace ProofShift.Domain.Tests;

public sealed class DomainFoundationTests
{
    [Fact]
    public void StrongIdentifiersRejectEmptyValuesAndUseValueEquality()
    {
        Assert.Throws<ArgumentException>(() => new ProjectId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ArtifactId("  "));
        Assert.Throws<ArgumentException>(() => new Project(default, "invalid", DateTimeOffset.UnixEpoch));
        Assert.Equal(new ProjectId(Guid.Parse("b4945774-ea90-48fc-a9b2-676f6d3e1af0")),
            new ProjectId(Guid.Parse("b4945774-ea90-48fc-a9b2-676f6d3e1af0")));
        Assert.False(new ProjectId(Guid.NewGuid()).Equals(new ProjectId(Guid.NewGuid())));
    }

    [Fact]
    public void DomainCollectionsCopyInputsAndCompareValuesStructurally()
    {
        var values = new List<ValueNode> { new StringValue("member") };
        var first = new DomainList<ValueNode>(values);
        values.Add(new IntegerValue(12));
        var second = new DomainList<ValueNode>([new StringValue("member")]);

        Assert.Single(first);
        Assert.Equal(first, second);

        var source = new Dictionary<string, ValueNode> { ["status"] = new StringValue("ACTIVE") };
        var firstMap = new DomainDictionary<ValueNode>(source);
        source["status"] = new StringValue("INACTIVE");
        var secondMap = new DomainDictionary<ValueNode>(
            new Dictionary<string, ValueNode> { ["status"] = new StringValue("ACTIVE") });

        Assert.Equal(new StringValue("ACTIVE"), firstMap["status"]);
        Assert.Equal(firstMap, secondMap);
        Assert.Equal(new ObjectValue(firstMap), new ObjectValue(secondMap));
    }

    [Fact]
    public void SystemAndSnapshotRequireEndpointsAndUniqueEndpointIds()
    {
        var endpoint = Endpoint("source-store");

        Assert.Throws<ArgumentException>(() => new SystemDefinition(
            new SystemId("source"), "Source", SystemRole.Source, Array.Empty<StorageEndpointDefinition>()));
        Assert.Throws<ArgumentException>(() => new SystemDefinition(
            new SystemId("source"), "Source", SystemRole.Source, [endpoint, endpoint]));

        var snapshot = new Snapshot(
            NewSnapshotId(),
            NewProjectId(),
            DateTimeOffset.UnixEpoch,
            [new EndpointSnapshot(endpoint.Id, endpoint.Connector, [], "endpoint-hash")],
            "snapshot-hash");

        Assert.Single(snapshot.Endpoints);
    }

    [Fact]
    public void GraphValidationReportsBothDanglingEndpoints()
    {
        var knownNodeId = NewNodeId();
        var missingFrom = NewNodeId();
        var missingTo = NewNodeId();
        var edge = new MigrationEdge(
            NewEdgeId(), missingFrom, missingTo,
            new MigrationOperation(MigrationOperationType.Copy), "1");
        var graph = new MigrationGraph(
            new MigrationGraphId(Guid.NewGuid()),
            [Node(knownNodeId)],
            [edge],
            "opaque-graph-hash");

        var issues = graph.Validate();

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, issue => issue.Code == MigrationGraphIssueCode.DanglingFromNode && issue.NodeId == missingFrom);
        Assert.Contains(issues, issue => issue.Code == MigrationGraphIssueCode.DanglingToNode && issue.NodeId == missingTo);
    }

    [Fact]
    public void DestructiveOperationsRequireRecoveryAndRestoreRequiresSnapshot()
    {
        var from = NewNodeId();
        var to = NewNodeId();
        var destructive = new MigrationOperation(MigrationOperationType.Exclude);

        Assert.True(destructive.IsDestructive);
        Assert.Throws<ArgumentException>(() => new MigrationEdge(
            NewEdgeId(), from, to, destructive, "1"));
        Assert.Throws<ArgumentException>(() => new RecoveryDefinition(RecoveryMode.Restore));

        var edge = new MigrationEdge(
            NewEdgeId(), from, to, destructive, "1",
            new RecoveryDefinition(RecoveryMode.Restore, "restore-source", requiresSnapshot: true));

        Assert.Equal(RecoveryMode.Restore, edge.Recovery!.Mode);
    }

    [Fact]
    public void DispositionLedgerIdentifiesMissingDuplicateAndUnaccountedSources()
    {
        var migrated = Artifact("source-1");
        var unaccounted = Artifact("source-2");
        var missing = Artifact("source-3");
        var target = Artifact("target-1", "target-system");
        var ledger = new ArtifactDispositionLedger(
        [
            new ArtifactDispositionRecord(migrated, ArtifactDisposition.Migrated, [target]),
            new ArtifactDispositionRecord(migrated, ArtifactDisposition.Migrated, [target]),
            new ArtifactDispositionRecord(unaccounted, ArtifactDisposition.Unaccounted, reason: "Not classified")
        ]);

        var issues = ledger.ValidateCoverage([migrated, unaccounted, missing]);

        Assert.Contains(issues, issue => issue.Code == ArtifactAccountingIssueCode.DuplicateDisposition);
        Assert.Contains(issues, issue => issue.Code == ArtifactAccountingIssueCode.UnaccountedArtifact);
        Assert.Contains(issues, issue => issue.Code == ArtifactAccountingIssueCode.MissingDisposition && issue.ArtifactId == missing.Id);
        Assert.Throws<ArgumentException>(() => new ArtifactDispositionRecord(
            unaccounted, ArtifactDisposition.Excluded));
    }

    [Fact]
    public void TargetLineageRequiresSourcesAndMigrationPath()
    {
        var target = Artifact("target", "target-system");
        var source = Artifact("source");
        var edgeId = NewEdgeId();

        Assert.Throws<ArgumentException>(() => new LineageRecord(target, [], [edgeId], "plan-hash"));
        Assert.Throws<ArgumentException>(() => new LineageRecord(target, [source], [], "plan-hash"));

        var lineage = new LineageRecord(target, [source], [edgeId], "plan-hash");
        Assert.Equal(source, Assert.Single(lineage.Sources));

        var ledger = new LineageLedger([lineage]);
        Assert.Contains(ledger.ValidateCoverage([target, Artifact("untracked-target", "target-system")]),
            issue => issue.Code == LineageCoverageIssueCode.MissingLineage);
    }

    [Fact]
    public void EvidenceReferencesMustIdentifyExactlyOneInput()
    {
        Assert.Throws<ArgumentException>(() => new EvidenceReference());
        Assert.Throws<ArgumentException>(() => new EvidenceReference(new ArtifactId("artifact"), new EvidenceId(Guid.NewGuid())));
        Assert.Equal(new ArtifactId("artifact"), new EvidenceReference(artifactId: new ArtifactId("artifact")).ArtifactId);
    }

    [Fact]
    public void MigrationRunRequiresCompletionTimeForTerminalStatus()
    {
        var runtime = new RuntimeFingerprint("0.1", "config-hash", "graph-hash");
        var startedAt = DateTimeOffset.UnixEpoch;

        Assert.Throws<ArgumentException>(() => new MigrationRun(
            new RunId(Guid.NewGuid()), new MigrationPlanId(Guid.NewGuid()), 1,
            RunType.Verification, RunStatus.Completed, startedAt, runtime));

        var run = new MigrationRun(
            new RunId(Guid.NewGuid()), new MigrationPlanId(Guid.NewGuid()), 1,
            RunType.Verification, RunStatus.Running, startedAt, runtime);
        Assert.Null(run.CompletedAt);
    }

    private static StorageEndpointDefinition Endpoint(string id) =>
        new(new StorageEndpointId(id), new ConnectorId("synthetic"));

    private static MigrationNode Node(MigrationNodeId id) =>
        new(id, "node", MigrationNodeType.Source, "Generic.Record", new SystemId("system"), new StorageEndpointId("endpoint"));

    private static ArtifactReference Artifact(string id, string systemId = "source-system") =>
        new(new ArtifactId(id), new SystemId(systemId), new StorageEndpointId("endpoint"), "record", id);

    private static ProjectId NewProjectId() => new(Guid.NewGuid());

    private static SnapshotId NewSnapshotId() => new(Guid.NewGuid());

    private static MigrationNodeId NewNodeId() => new(Guid.NewGuid());

    private static MigrationEdgeId NewEdgeId() => new(Guid.NewGuid());
}
