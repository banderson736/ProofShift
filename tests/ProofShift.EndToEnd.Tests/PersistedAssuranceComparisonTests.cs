using ProofShift.Domain;
using ProofShift.Reporting;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class PersistedAssuranceComparisonTests
{
    private static readonly DateTimeOffset CaptureStarted = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private const string SourceFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EndpointFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ManifestHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SegmentHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string SelectorHash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ConfigurationHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string GraphHash = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public void IncidentalCheckpointIdentityDoesNotChangeSemantics()
    {
        var before = CreateCheckpoint();
        var after = CreateCheckpoint(
            id: Guid.NewGuid(),
            captureStartedAt: CaptureStarted.AddHours(2),
            segmentReference: "nodes/another-materialization.ndjson",
            manifestHash: new string('2', 64),
            configurationHash: new string('6', 64),
            graphHash: new string('7', 64));

        var difference = CheckpointSemanticComparison.Compare(before, after);

        Assert.False(difference.CheckpointSemanticsChanged);
        Assert.False(difference.SourceFingerprintChanged);
    }

    [Fact]
    public void SourceFingerprintChangeIsReportedAsMaterialSourceAndCheckpointChange()
    {
        var before = CreateCheckpoint();
        var after = CreateCheckpoint(sourceFingerprint: new string('3', 64), endpointFingerprint: new string('4', 64));

        var difference = CheckpointSemanticComparison.Compare(before, after);

        Assert.True(difference.SourceFingerprintChanged);
        Assert.True(difference.CheckpointSemanticsChanged);
    }

    [Fact]
    public void SelectorAndConnectorChangesAreSemantic()
    {
        var baseline = CreateCheckpoint();

        Assert.True(CheckpointSemanticComparison.Compare(baseline,
            CreateCheckpoint(selectorHash: new string('5', 64))).CheckpointSemanticsChanged);
        Assert.True(CheckpointSemanticComparison.Compare(baseline,
            CreateCheckpoint(connector: "oracle", connectorVersion: "2")).CheckpointSemanticsChanged);
        Assert.True(CheckpointSemanticComparison.Compare(baseline,
            CreateCheckpoint(connectorVersion: "2")).CheckpointSemanticsChanged);
    }

    [Fact]
    public void ConsistencyReplayAndCrossSystemAtomicityChangesAreSemantic()
    {
        var baseline = CreateCheckpoint();

        Assert.True(CheckpointSemanticComparison.Compare(baseline,
            CreateCheckpoint(sourceConsistency: SourceConsistencyGuarantee.Observed,
                requestedStrategy: "observed", effectiveStrategy: "observed")).CheckpointSemanticsChanged);
        Assert.True(CheckpointSemanticComparison.Compare(baseline,
            CreateCheckpoint(crossSystemAtomic: false)).CheckpointSemanticsChanged);
    }

    private static SourceCheckpoint CreateCheckpoint(
        Guid? id = null,
        DateTimeOffset? captureStartedAt = null,
        string? sourceFingerprint = null,
        string? endpointFingerprint = null,
        string? manifestHash = null,
        string? segmentReference = null,
        string? configurationHash = null,
        string? graphHash = null,
        string? selectorHash = null,
        string connector = "postgres",
        string connectorVersion = "1",
        SourceConsistencyGuarantee sourceConsistency = SourceConsistencyGuarantee.Consistent,
        string requestedStrategy = "transaction-consistent",
        string effectiveStrategy = "repeatable-read",
        bool crossSystemAtomic = true)
    {
        var start = captureStartedAt ?? CaptureStarted;
        var sourceHash = sourceFingerprint ?? SourceFingerprint;
        var endpointHash = endpointFingerprint ?? EndpointFingerprint;
        var endpoint = new CheckpointEndpoint(
            "source-node",
            new SystemId("source"),
            new StorageEndpointId("source-db"),
            new ConnectorId(connector),
            connectorVersion,
            selectorHash ?? SelectorHash,
            ["record_id"],
            start,
            start.AddSeconds(5),
            sourceConsistency,
            CheckpointGuarantee.Materialized,
            replayable: true,
            artifactCount: 4,
            byteCount: 128,
            endpointHash,
            segmentReference ?? "nodes/source.ndjson",
            SegmentHash,
            segmentLength: 64,
            requestedConsistencyStrategy: requestedStrategy,
            effectiveConsistencyStrategy: effectiveStrategy);

        return new SourceCheckpoint(
            new CheckpointId(id ?? Guid.NewGuid()),
            CheckpointStatus.Complete,
            "project",
            configurationHash ?? ConfigurationHash,
            graphHash ?? GraphHash,
            start,
            start.AddSeconds(10),
            TimeSpan.FromSeconds(1),
            crossSystemAtomic,
            replayable: true,
            CheckpointGuarantee.Materialized,
            [endpoint],
            artifactCount: 4,
            byteCount: 128,
            sourceHash,
            manifestHash ?? ManifestHash);
    }
}
