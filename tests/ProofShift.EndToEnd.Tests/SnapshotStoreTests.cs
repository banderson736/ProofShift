using System.Numerics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Snapshots;
using Xunit;

namespace ProofShift.EndToEnd.Tests;

public sealed class SnapshotStoreTests
{
    [Fact]
    public async Task CompleteCheckpointManifestPersistsEvaluationScopeIdentity()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var scope = new EvaluationScope("active-members", EvaluationScopeType.BoundedEvaluation,
                [new EvaluationPredicate("status", EvaluationPredicateOperator.Equals, ["ACTIVE"])]);
            var selector = new ArtifactSelector("table",
                [new KeyValuePair<string, string>("name", "member")], ["member_id"], scope);
            var record = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var (store, checkpointId, _) = await CreateCheckpointAsync(directory, record, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));

            var manifest = await store.ReadManifestAsync(checkpointId.Value.ToString("N"),
                TestContext.Current.CancellationToken);
            var endpoint = Assert.Single(manifest.Endpoints);
            Assert.Equal(scope.Id, endpoint.EvaluationScopeId);
            Assert.Equal("bounded-evaluation", endpoint.EvaluationScopeType);
            Assert.Equal(scope.Fingerprint, endpoint.EvaluationScopeFingerprint);
            Assert.Equal(1, endpoint.ArtifactCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HistoricalUnscopedManifestLoadsAndReplaysWithoutMutation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceRecord = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table",
                [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, checkpoint) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));
            var manifestPath = Path.Combine(directory, checkpointId.Value.ToString("N"), "manifest.json");
            var (historicalManifestBytes, historicalManifestHash) = EncodePreScopeManifest(checkpoint);
            Assert.Equal(historicalManifestBytes, EncodePreScopeManifest(checkpoint).Bytes);
            await File.WriteAllBytesAsync(manifestPath, historicalManifestBytes, TestContext.Current.CancellationToken);

            using (var manifestJson = JsonDocument.Parse(historicalManifestBytes))
            {
                var root = manifestJson.RootElement;
                Assert.Equal("proofshift-snapshot-manifest-v2", root.GetProperty("manifestFormatVersion").GetString());
                Assert.Equal(historicalManifestHash, root.GetProperty("manifestHash").GetString());
                var endpoint = Assert.Single(root.GetProperty("endpoints").EnumerateArray());
                Assert.False(endpoint.TryGetProperty("evaluationScopeId", out _));
                Assert.False(endpoint.TryGetProperty("evaluationScopeType", out _));
                Assert.False(endpoint.TryGetProperty("evaluationScopeFingerprint", out _));
            }

            var loadedManifest = await store.ReadManifestAsync(checkpointId.Value.ToString("N"),
                TestContext.Current.CancellationToken);
            Assert.Equal(historicalManifestHash, loadedManifest.ManifestHash);
            var loadedEndpoint = Assert.Single(loadedManifest.Endpoints);
            Assert.Null(loadedEndpoint.EvaluationScopeId);
            Assert.Null(loadedEndpoint.EvaluationScopeType);
            Assert.Null(loadedEndpoint.EvaluationScopeFingerprint);

            var scopedSelector = new ArtifactSelector("table",
                [new KeyValuePair<string, string>("name", "member")], ["member_id"],
                new EvaluationScope("later-scope", EvaluationScopeType.BoundedEvaluation));
            await using (var loaded = await store.OpenCompleteAsync(checkpointId.Value.ToString("N"),
                TestContext.Current.CancellationToken))
            {
                var exception = await Assert.ThrowsAsync<SnapshotStoreException>(async () =>
                {
                    await foreach (var _ in loaded.ReadAsync("members", scopedSelector, TestContext.Current.CancellationToken)) { }
                });
                Assert.Equal(SnapshotIssueCodes.SelectorMismatch, exception.Code);
            }

            var replayFingerprints = new List<string>();
            for (var replay = 0; replay < 2; replay++)
            {
                await using var loaded = await store.OpenCompleteAsync(checkpointId.Value.ToString("N"),
                    TestContext.Current.CancellationToken);
                var replayedRecords = new List<RecordEnvelope>();
                await foreach (var record in loaded.ReadAsync("members", selector, TestContext.Current.CancellationToken))
                    replayedRecords.Add(record);
                var replayed = Assert.Single(replayedRecords);
                Assert.Equal(sourceRecord.Artifact, replayed.Artifact);
                Assert.Equal(sourceRecord.Provenance.Connector, replayed.Provenance.Connector);
                Assert.Equal(sourceRecord.Provenance.Endpoint, replayed.Provenance.Endpoint);
                Assert.Equal(sourceRecord.Provenance.Location, replayed.Provenance.Location);
                Assert.Equal(sourceRecord.Provenance.ObservedAt, replayed.Provenance.ObservedAt);
                Assert.Equal(sourceRecord.Provenance.SourceHash, replayed.Provenance.SourceHash);
                Assert.Equal(sourceRecord.Provenance.Metadata["fixture"], replayed.Provenance.Metadata["fixture"]);
                Assert.Equal("fixture,snapshotCheckpointId,snapshotMaterialized,snapshotRecordFingerprint,snapshotSourceNode",
                    string.Join(",", replayed.Provenance.Metadata.Keys.OrderBy(key => key, StringComparer.Ordinal)));
                Assert.Equal(checkpointId.Value.ToString("N"), replayed.Provenance.Metadata["snapshotCheckpointId"]);
                Assert.Equal("true", replayed.Provenance.Metadata["snapshotMaterialized"]);
                Assert.Equal("members", replayed.Provenance.Metadata["snapshotSourceNode"]);
                Assert.Equal(SnapshotFingerprints.RecordFingerprint(sourceRecord),
                    replayed.Provenance.Metadata["snapshotRecordFingerprint"]);
                replayFingerprints.Add(SnapshotFingerprints.RecordFingerprint(replayed));
            }

            Assert.Equal(new[] { SnapshotFingerprints.RecordFingerprint(sourceRecord) }, replayFingerprints.Distinct());
            Assert.Equal(historicalManifestBytes,
                await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken));

            var corruptedManifest = Encoding.UTF8.GetString(historicalManifestBytes)
                .Replace(historicalManifestHash, new string('0', 64), StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, corruptedManifest, TestContext.Current.CancellationToken);
            var integrityException = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.OpenCompleteAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken));
            Assert.Equal(SnapshotIssueCodes.IntegrityFailure, integrityException.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

            var manifest = await store.ReadManifestAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken);
            Assert.Equal(checkpoint.Id, manifest.Id);
            Assert.Equal(checkpoint.ManifestHash, manifest.ManifestHash);
            Assert.Equal(checkpoint.SourceFingerprint, manifest.SourceFingerprint);
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

    [Fact]
    public async Task ReadManifestRejectsCorruptedManifestWithoutReplayingCheckpoint()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceRecord = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, checkpoint) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));
            var checkpointDirectory = Path.Combine(directory, checkpointId.Value.ToString("N"));
            var manifestPath = Path.Combine(checkpointDirectory, "manifest.json");
            var manifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
            manifest = manifest.Replace(checkpoint.ManifestHash!, new string('0', 64), StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, manifest, TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.ReadManifestAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken));
            Assert.Equal(SnapshotIssueCodes.IntegrityFailure, exception.Code);

        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadManifestDoesNotReplayOrReadCheckpointSegments()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sourceRecord = CreateRecord(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), 0);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "member")], ["member_id"]);
            var (store, checkpointId, checkpoint) = await CreateCheckpointAsync(directory, sourceRecord, selector,
                (_, _) => ValueTask.FromResult<Stream>(new MemoryStream()));
            var segment = Directory.GetFiles(Path.Combine(directory, checkpointId.Value.ToString("N"), "nodes"), "*.ndjson").Single();
            await File.AppendAllTextAsync(segment, "tamper", TestContext.Current.CancellationToken);

            var manifest = await store.ReadManifestAsync(checkpointId.Value.ToString("N"), TestContext.Current.CancellationToken);
            Assert.Equal(checkpoint.ManifestHash, manifest.ManifestHash);
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
            var manifestException = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.ReadManifestAsync(id.Value.ToString("N"), TestContext.Current.CancellationToken));
            Assert.Equal(SnapshotIssueCodes.CheckpointNotComplete, manifestException.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringCapturePersistsAnUnreplayableIncompleteCheckpoint()
    {
        var directory = CreateTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var system = new SystemDefinition(new SystemId("source"), "Synthetic Source", SystemRole.Source,
            [new StorageEndpointDefinition(new StorageEndpointId("source-store"), new ConnectorId("cancelling-source"), [])]);
            var selector = new ArtifactSelector("table", [new KeyValuePair<string, string>("name", "members")], ["member_id"]);
            var node = new MigrationNode(new MigrationNodeId(Guid.NewGuid()), "members", MigrationNodeType.Source,
                "Pension.Member", system.Id, system.StorageEndpoints[0].Id, selector);
            var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), [node], [], new string('a', 64), "test-v1");
            var configuration = new LoadedProjectConfiguration(new RootConfigurationDto(1,
                new ProjectConfigurationDto("snapshot-cancellation", "Snapshot Cancellation"), null, [], null, null, null), [],
                [system], [], "snapshot-cancellation", new string('b', 64));
            var store = new FileSystemSnapshotStore(directory);
            var service = new SnapshotCaptureService(new ConnectorRegistry([new CancellingSourceConnector(cancellation)]), store);

            var result = await service.CaptureAsync(configuration, graph, cancellation.Token);

            Assert.Equal(CheckpointStatus.Cancelled, result.Status);
            Assert.Null(result.Checkpoint);
            Assert.Equal("PSSNAP_CANCELLED", result.FailureCode);
            var exception = await Assert.ThrowsAsync<SnapshotStoreException>(
                () => store.OpenCompleteAsync(result.Id.Value.ToString("N"), TestContext.Current.CancellationToken));
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

    private static (byte[] Bytes, string Hash) EncodePreScopeManifest(SourceCheckpoint checkpoint)
    {
        // Independent wire DTO from accepted a095c21588cbd1608b8a543cdfaab71312b596d8,
        // SnapshotManifestCodec: v2 property order, null omission, and hash over the document without manifestHash.
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        object Document(string? hash) => new
        {
            manifestFormatVersion = "proofshift-snapshot-manifest-v2",
            materializedFormatVersion = "proofshift-materialized-snapshot-v1",
            manifestHash = hash,
            checkpointId = checkpoint.Id.Value.ToString("N"),
            status = "complete",
            projectId = checkpoint.ProjectId,
            configurationHash = checkpoint.ConfigurationHash,
            graphHash = checkpoint.GraphHash,
            captureStartedAt = checkpoint.CaptureStartedAt.ToString("O", CultureInfo.InvariantCulture),
            captureCompletedAt = checkpoint.CaptureCompletedAt.ToString("O", CultureInfo.InvariantCulture),
            maximumEndpointSkewTicks = checkpoint.MaximumEndpointSkew.Ticks,
            crossSystemAtomic = checkpoint.CrossSystemAtomic,
            replayable = checkpoint.Replayable,
            guarantee = "materialized",
            artifactCount = checkpoint.ArtifactCount,
            byteCount = checkpoint.ByteCount,
            sourceFingerprint = checkpoint.SourceFingerprint,
            endpoints = checkpoint.Endpoints.OrderBy(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal)
                .Select(endpoint => new
                {
                    sourceNodeKey = endpoint.SourceNodeKey,
                    systemId = endpoint.SystemId.Value,
                    endpointId = endpoint.EndpointId.Value,
                    connector = endpoint.Connector.Value,
                    connectorVersion = endpoint.ConnectorVersion,
                    selectorHash = endpoint.SelectorHash,
                    identityFields = endpoint.IdentityFields.ToArray(),
                    captureStartedAt = endpoint.CaptureStartedAt.ToString("O", CultureInfo.InvariantCulture),
                    captureCompletedAt = endpoint.CaptureCompletedAt.ToString("O", CultureInfo.InvariantCulture),
                    sourceConsistency = endpoint.SourceConsistency.ToString().ToLowerInvariant(),
                    requestedConsistencyStrategy = endpoint.RequestedConsistencyStrategy,
                    effectiveConsistencyStrategy = endpoint.EffectiveConsistencyStrategy,
                    consistencyDowngrade = endpoint.ConsistencyDowngrade,
                    guarantee = "materialized",
                    replayable = endpoint.Replayable,
                    artifactCount = endpoint.ArtifactCount,
                    byteCount = endpoint.ByteCount,
                    sourceFingerprint = endpoint.SourceFingerprint,
                    segmentReference = endpoint.SegmentReference,
                    segmentSha256 = endpoint.SegmentSha256,
                    segmentLength = endpoint.SegmentLength
                }).ToArray()
        };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Document(null), options)))
            .ToLowerInvariant();
        return (JsonSerializer.SerializeToUtf8Bytes(Document(hash), options), hash);
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
            requestedConsistencyStrategy, effectiveConsistencyStrategy, consistencyDowngrade,
            selector.EvaluationScope?.Id,
            selector.EvaluationScope is { } scope ? scope.Type switch
            {
                EvaluationScopeType.FullDeclaredSource => "full-declared-source",
                EvaluationScopeType.BoundedEvaluation => "bounded-evaluation",
                EvaluationScopeType.CustomerPreparedSubset => "customer-prepared-subset",
                _ => throw new ArgumentOutOfRangeException(nameof(selector))
            } : null,
            selector.EvaluationScope?.Fingerprint);
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

    private sealed class CancellingSourceConnector(CancellationTokenSource cancellation) : ISourceConnector
    {
        public ConnectorId Id { get; } = new("cancelling-source");
        public string Version => "test-v1";

        public Task<SourceInspection> InspectAsync(ConnectorContext context, ArtifactSelector selector, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceInspection(SourceInspectionStatus.Valid, identityFields: ["member_id"]));

        public async IAsyncEnumerable<RecordEnvelope> ReadAsync(ConnectorContext context, ArtifactSelector selector, ReadOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var system = new SystemId("source");
            var endpoint = new StorageEndpointId("source-store");
            yield return new RecordEnvelope(new ArtifactReference(new ArtifactId("member-1"), system, endpoint,
                "row", "member_id=1"), "Pension.Member", new Dictionary<string, ValueNode>
                {
                    ["member_id"] = new IntegerValue(1)
                }, new ProvenanceMetadata(Id, endpoint, "synthetic", DateTimeOffset.UnixEpoch));
            cancellation.Cancel();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"proofshift-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
