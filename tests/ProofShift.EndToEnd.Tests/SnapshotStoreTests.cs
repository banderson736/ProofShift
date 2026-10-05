using System.Numerics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Snapshots;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class SnapshotStoreTests
{
    [Fact]
    public async Task MaterializedCheckpointPreservesSourceIdentityAndBinaryContent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var binary = Encoding.UTF8.GetBytes("synthetic-attachment");
            var binaryHash = Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
            var sourceRecord = CreateRecord(binaryHash, binary.LongLength);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, checkpoint) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(binary)));

            await using var loaded = await store.OpenCompleteAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken);
            var records = new List<RecordEnvelope>();
            await foreach (var record in loaded.ReadAsync("members", selector, TestContext.Current.CancellationToken)) records.Add(record);

            var replayed = Assert.Single(records);
            Assert.Equal(sourceRecord.Artifact, replayed.Artifact);
            Assert.Equal(sourceRecord.SemanticType, replayed.SemanticType);
            Assert.Equal(sourceRecord.Values.Where(pair => pair.Key != "attachment"),
                replayed.Values.Where(pair => pair.Key != "attachment"));
            Assert.Equal(sourceRecord.Relationships, replayed.Relationships);
            Assert.Equal(sourceRecord.Temporal, replayed.Temporal);
            Assert.Equal("member-7", Assert.IsType<StringValue>(replayed.Values["member_id"]).Value);
            Assert.Equal("source-origin", replayed.Provenance.Location);
            Assert.Equal("true", replayed.Provenance.Metadata["snapshotMaterialized"]);
            var replayedBinary = Assert.IsType<BinaryReferenceValue>(replayed.Values["attachment"]);
            Assert.NotEqual(Assert.IsType<BinaryReferenceValue>(sourceRecord.Values["attachment"]).Reference, replayedBinary.Reference);
            Assert.Equal(Assert.IsType<BinaryReferenceValue>(sourceRecord.Values["attachment"]).Sha256, replayedBinary.Sha256);
            await using var binaryStream = await loaded.OpenBinaryReadAsync("members", replayed.Artifact, replayedBinary, TestContext.Current.CancellationToken);
            using var content = new MemoryStream();
            await binaryStream.CopyToAsync(content, TestContext.Current.CancellationToken);
            Assert.Equal(binary, content.ToArray());
            Assert.Equal(checkpoint.ManifestHash, loaded.Manifest.ManifestHash);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteCheckpointRejectsTamperedSegment()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceRecord = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, _) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));
            var segment = Directory.GetFiles(Path.Combine(directory, checkpointId.Value.ToString("N"), "nodes"), "*.ndjson").Single();
            await File.AppendAllTextAsync(segment, "tamper", TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.OpenCompleteAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken));
            Assert.Equal(SnapshotIssueCodes.IntegrityFailure, exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(CheckpointStatus.Failed)]
    [InlineData(CheckpointStatus.Cancelled)]
    public async Task IncompleteCheckpointCannotBeOpenedForReplay(CheckpointStatus status)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new FileSystemSnapshotStore(directory);
            var id = new CheckpointId(Guid.NewGuid());
            await using (var session = await store.CreateAsync(id, TestContext.Current.CancellationToken))
            {
                await session.BeginSourceNodeAsync("members", TestContext.Current.CancellationToken);
                await session.MarkIncompleteAsync(status, "PSSNAP_TEST_FAILURE", TestContext.Current.CancellationToken);
            }

            var exception = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.OpenCompleteAsync(id.Value.ToString("N"), TestContext.Current.CancellationToken));
            Assert.Equal(SnapshotIssueCodes.CheckpointNotComplete, exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckpointReplayRejectsChangedSelector()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var record = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, id, _) = await CreateCheckpointAsync(directory, record, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));
            await using var checkpoint = await store.OpenCompleteAsync(id.Value.ToString("N"), TestContext.Current.CancellationToken);
            var changedSelector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "other")], ["member_id"]);
            var exception = await Assert.ThrowsAsync<SnapshotStoreException>(async () =>
            {
                await foreach (var _ in checkpoint.ReadAsync("members", changedSelector, TestContext.Current.CancellationToken)) { }
            });
            Assert.Equal(SnapshotIssueCodes.SelectorMismatch, exception.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckpointManifestPreservesConsistencyStrategyAndDowngradeDetails()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceRecord = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, _) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()),
                SourceConsistencyGuarantee.Observed, "transaction-consistent", "read-committed",
                "Requested transaction consistency was explicitly downgraded to observed.");

            await using var loaded = await store.OpenCompleteAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken);
            var endpoint = Assert.Single(loaded.Manifest.Endpoints);
            Assert.Equal("transaction-consistent", endpoint.RequestedConsistencyStrategy);
            Assert.Equal("read-committed", endpoint.EffectiveConsistencyStrategy);
            Assert.Equal(SourceConsistencyGuarantee.Observed, endpoint.SourceConsistency);
            Assert.Contains("downgraded", endpoint.ConsistencyDowngrade, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(FileSystemSnapshotStore Store, CheckpointId Id, SourceCheckpoint Checkpoint)> CreateCheckpointAsync(
        string root,
        RecordEnvelope record,
        ArtifactSelector selector,
        Func<string, CancellationToken, ValueTask<Stream>>? openBinary,
        SourceConsistencyGuarantee sourceConsistency = SourceConsistencyGuarantee.Observed,
        string requestedConsistencyStrategy = "observed",
        string effectiveConsistencyStrategy = "observed",
        string? consistencyDowngrade = null)
    {
        var store = new FileSystemSnapshotStore(root);
        var id = new CheckpointId(Guid.NewGuid());
        var start = DateTimeOffset.UtcNow;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var session = await store.CreateAsync(id, cancellationToken);
        await session.BeginSourceNodeAsync("members", cancellationToken);
        var byteCount = await session.WriteRecordAsync("members", record, openBinary, cancellationToken);
        var segment = await session.CompleteSourceNodeAsync("members", cancellationToken);
        var end = DateTimeOffset.UtcNow;
        var recordHash = SnapshotFingerprints.RecordFingerprint(record);
        var sum = new BigInteger(Convert.FromHexString(recordHash), isUnsigned: true, isBigEndian: true)
            .ToString("x64", CultureInfo.InvariantCulture);
        var endpoint = new CheckpointEndpoint(
            "members", new SystemId("source"), new StorageEndpointId("source-db"), new ConnectorId("synthetic"), "1.0",
            SnapshotFingerprints.SelectorHash(selector), ["member_id"], start, end,
            sourceConsistency, CheckpointGuarantee.Materialized, true, 1, byteCount,
            SnapshotFingerprints.EndpointFingerprint("members", 1, sum), segment.Reference, segment.Sha256, segment.Length,
            requestedConsistencyStrategy, effectiveConsistencyStrategy, consistencyDowngrade);
        var graphHash = new string('b', 64);
        var sourceFingerprint = SnapshotFingerprints.AggregateSourceFingerprint(graphHash, [endpoint]);
        var draft = new SourceCheckpoint(id, CheckpointStatus.Complete, "test-project", new string('c', 64), graphHash,
            start, end, end - start, false, true, CheckpointGuarantee.Materialized, [endpoint], 1, byteCount,
            sourceFingerprint, new string('0', 64));
        var manifestHash = await session.FinalizeAsync(draft, cancellationToken);
        var finalized = new SourceCheckpoint(id, CheckpointStatus.Complete, "test-project", new string('c', 64), graphHash,
            start, end, end - start, false, true, CheckpointGuarantee.Materialized, [endpoint], 1, byteCount,
            sourceFingerprint, manifestHash);
        return (store, id, finalized);
    }

    private static RecordEnvelope CreateRecord(string binaryHash, long binaryLength)
    {
        var target = new ArtifactReference(new ArtifactId("original-employer-3"), new SystemId("source"),
            new StorageEndpointId("source-db"), "employer", "employer-3");
        return new RecordEnvelope(
            new ArtifactReference(new ArtifactId("original-artifact-7"), new SystemId("source"),
                new StorageEndpointId("source-db"), "row", "member-7"),
            "Pension.Member",
            new Dictionary<string, ValueNode>
            {
                ["member_id"] = new StringValue("member-7"),
                ["active"] = new BooleanValue(true),
                ["attachment"] = new BinaryReferenceValue("source-file-v1:attachment", binaryLength, binaryHash),
                ["null"] = new NullValue(),
                ["integer"] = new IntegerValue(long.MaxValue),
                ["decimal"] = new DecimalValue(12345678901234567890.12345678m),
                ["date"] = new DateValue(new DateOnly(2025, 3, 4)),
                ["instant"] = new InstantValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(3))),
                ["offset"] = new OffsetDateTimeValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.FromHours(-4))),
                ["local"] = new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified)),
                ["collection"] = new CollectionValue([new StringValue("a,b"), new IntegerValue(2)]),
                ["object"] = new ObjectValue([new KeyValuePair<string, ValueNode>("nested", new BooleanValue(false))])
            },
            new ProvenanceMetadata(new ConnectorId("synthetic"), new StorageEndpointId("source-db"), "source-origin",
                DateTimeOffset.UnixEpoch, metadata: new Dictionary<string, string> { ["fixture"] = "ps06" }),
            [new RelationshipReference("employedBy", target, RelationshipDirection.Outgoing)],
            new TemporalMetadata(
                effectiveFrom: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                recordedAt: DateTimeOffset.UnixEpoch,
                version: 7));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proofshift-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
