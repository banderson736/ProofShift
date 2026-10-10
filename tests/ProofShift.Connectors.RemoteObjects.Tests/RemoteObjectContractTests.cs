using System.Security.Cryptography;
using System.Text;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Domain;

namespace ProofShift.Connectors.RemoteObjects.Tests;

public sealed class RemoteObjectContractTests
{
    private static ConnectorContext Context() => new("legacy", "bucket", new ConnectorId("memory"), "files", "File.Artifact",
        new RuntimeConfiguration([new KeyValuePair<string, RuntimeSetting>("scope", RuntimeSetting.FromRuntimeValue("x"))]));

    private static ArtifactSelector Selector(string pattern) => new(RemoteObjectSourceConnector.SelectorKind,
        [new("pattern", pattern)], ["relativePath"]);

    private static (RemoteObjectSourceConnector Connector, InMemoryObjectStore Store) Create()
    {
        var store = new InMemoryObjectStore();
        foreach (var key in new[] { "a/2.txt", "a/10.txt", "a/z/deep.csv", "b/x.txt", "ünï/ç.txt", "a b/space.txt", "a%2Fenc.txt" })
            store.Put(key, Encoding.UTF8.GetBytes("payload:" + key));
        return (new RemoteObjectSourceConnector("memory", new InMemoryFactory(store)), store);
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public async Task Reads_scoped_objects_in_utf8_order_with_computed_hash_and_provider_metadata()
    {
        var (connector, store) = Create();
        var records = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadAsync(Context(), Selector("**/*.txt"), new ReadOptions(), default)) records.Add(record);

        var keys = records.Select(record => record.Artifact.Identity).ToArray();
        Assert.Equal(["a b/space.txt", "a%2Fenc.txt", "a/10.txt", "a/2.txt", "b/x.txt", "ünï/ç.txt"],
            keys);
        var ten = records.Single(record => record.Artifact.Identity == "a/10.txt");
        Assert.Equal(Sha("payload:a/10.txt"), ((StringValue)ten.Values["sha256"]).Value);
        Assert.Equal(Sha("payload:a/10.txt"), ten.Provenance.SourceHash);
        Assert.Equal("etag-1", ten.Provenance.Metadata["providerETag"]);
        Assert.Equal("v1", ten.Provenance.Metadata["providerVersionId"]);
        Assert.Equal("memory://scope/", ten.Provenance.Metadata["scope"]);
        Assert.True(store.Pages >= 2);
    }

    [Fact]
    public async Task Artifact_identity_is_independent_of_listing_provider_state()
    {
        var (connector, store) = Create();
        var first = await ReadIds(connector);
        store.Put("a/2.txt", Encoding.UTF8.GetBytes("changed"));
        var second = await ReadIds(connector);
        Assert.Equal(first, second);
    }

    private static async Task<string[]> ReadIds(RemoteObjectSourceConnector connector)
    {
        var ids = new List<string>();
        await foreach (var record in connector.ReadAsync(Context(), Selector("**/*.txt"), new ReadOptions(), default)) ids.Add(record.Artifact.Id.Value);
        return ids.ToArray();
    }

    [Fact]
    public async Task Unordered_listing_fails_closed_instead_of_producing_nondeterministic_output()
    {
        var (connector, store) = Create();
        store.ReorderListing = true;
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            await foreach (var _ in connector.ReadAsync(Context(), Selector("**/*.txt"), new ReadOptions(), default)) { }
        });
        Assert.Equal(ConnectorIssueCodes.NonDeterministicIdentity, exception.Code);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/abs")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    public async Task Selector_patterns_cannot_escape_the_scope(string pattern)
    {
        var (connector, _) = Create();
        var inspection = await connector.InspectAsync(Context(), Selector(pattern), default);
        Assert.Equal(SourceInspectionStatus.Invalid, inspection.Status);
        Assert.Equal(ConnectorIssueCodes.InvalidPhysicalIdentifier, inspection.Issues.Single().Code);
    }

    [Fact]
    public void Prefix_boundaries_and_dot_segments_are_enforced()
    {
        Assert.Equal("data/", RemoteKeyRules.NormalizePrefix("/data"));
        Assert.Equal("", RemoteKeyRules.NormalizePrefix(null));
        Assert.Throws<RemoteStoreException>(() => RemoteKeyRules.NormalizePrefix("data/../other"));
        Assert.Equal("x.txt", RemoteKeyRules.ToRelative("data/", "data/x.txt"));
        Assert.Throws<RemoteStoreException>(() => RemoteKeyRules.ToRelative("data/", "data-other/x.txt"));
        Assert.Throws<RemoteStoreException>(() => RemoteKeyRules.ToRelative("data/", "data/"));
        Assert.Throws<RemoteStoreException>(() => RemoteKeyRules.ValidateRelativeKey("a/../b"));
        Assert.Throws<RemoteStoreException>(() => RemoteKeyRules.ValidateRelativeKey("a\\b"));
    }

    [Fact]
    public async Task Transient_failures_are_retried_within_bounds_and_permanent_failures_are_not()
    {
        var (connector, store) = Create();
        store.FailNextOpens = 2;
        var count = 0;
        await foreach (var _ in connector.ReadAsync(Context(), Selector("b/*.txt"), new ReadOptions(), default)) count++;
        Assert.Equal(1, count);

        var delays = new List<TimeSpan>();
        var policy = new RemoteRetryPolicy(3, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15), (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var attempts = 0;
        await Assert.ThrowsAsync<RemoteStoreException>(() => policy.ExecuteAsync<int>(_ => { attempts++; throw new RemoteStoreException(RemoteFailureKind.Transient, "x"); }, default));
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(15)], delays);

        attempts = 0;
        await Assert.ThrowsAsync<RemoteStoreException>(() => policy.ExecuteAsync<int>(_ => { attempts++; throw new RemoteStoreException(RemoteFailureKind.Authorization, "x"); }, default));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Cancellation_is_not_retried()
    {
        var policy = new RemoteRetryPolicy(5, delay: (_, token) => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            cts.Cancel();
            throw new RemoteStoreException(RemoteFailureKind.Transient, "x");
        }, cts.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Short_reads_are_detected_as_a_changed_object()
    {
        var (connector, store) = Create();
        store.TruncateReads = true;
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            await foreach (var _ in connector.ReadAsync(Context(), Selector("b/*.txt"), new ReadOptions(), default)) { }
        });
        Assert.Equal(ConnectorIssueCodes.ArtifactChangedDuringCapture, exception.Code);
    }

    [Fact]
    public async Task Checkpoint_capture_detects_mutation_between_inventories()
    {
        var (connector, store) = Create();
        var enumerator = connector.ReadForCheckpointAsync(Context(), Selector("**/*.txt"), new ReadOptions(), default).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        store.Put("b/x.txt", Encoding.UTF8.GetBytes("mutated mid-capture"));
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            while (await enumerator.MoveNextAsync()) { }
        });
        Assert.Equal(ConnectorIssueCodes.ArtifactChangedDuringCapture, exception.Code);
    }

    [Fact]
    public async Task Binary_reference_reopens_the_exact_recorded_version_and_rejects_changed_content()
    {
        var (connector, store) = Create();
        var context = Context();
        var record = await First(connector, "b/*.txt");
        var binary = (BinaryReferenceValue)record.Values["content"];
        await using (var stream = await connector.OpenBinaryReadAsync(context, Selector("b/*.txt"), record.Artifact, binary, default))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal("payload:b/x.txt", Encoding.UTF8.GetString(copy.ToArray()));
        }

        store.Put("b/x.txt", Encoding.UTF8.GetBytes("different"));
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            await connector.OpenBinaryReadAsync(context, Selector("b/*.txt"), record.Artifact, binary, default));
        Assert.True(exception.Code is ConnectorIssueCodes.RemoteVersionUnavailable or ConnectorIssueCodes.ArtifactChangedDuringCapture);
    }

    [Fact]
    public async Task Binary_reference_for_another_artifact_is_rejected()
    {
        var (connector, _) = Create();
        var first = await First(connector, "b/*.txt");
        var other = await First(connector, "a/2.txt");
        await Assert.ThrowsAsync<ConnectorReadException>(async () => await connector.OpenBinaryReadAsync(Context(),
            Selector("b/*.txt"), first.Artifact, (BinaryReferenceValue)other.Values["content"], default));
    }

    private static async Task<RecordEnvelope> First(RemoteObjectSourceConnector connector, string pattern)
    {
        await foreach (var record in connector.ReadAsync(Context(), Selector(pattern), new ReadOptions(), default)) return record;
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task Seekable_stream_serves_footer_style_access_with_bounded_range_requests()
    {
        var store = new InMemoryObjectStore();
        var data = new byte[5 * 1024 * 1024];
        new Random(7).NextBytes(data);
        store.Put("big.bin", data);
        var info = await store.StatAsync("big.bin", null, default);
        await using var stream = new SeekableRemoteStream(store, info, 64 * 1024);
        stream.Seek(-8, SeekOrigin.End);
        var tail = new byte[8];
        await stream.ReadExactlyAsync(tail);
        Assert.Equal(data[^8..], tail);
        Assert.Equal(1, stream.RangeRequests);
        stream.Seek(1_000_000, SeekOrigin.Begin);
        var middle = new byte[100_000];
        await stream.ReadExactlyAsync(middle);
        Assert.Equal(data.AsSpan(1_000_000, 100_000).ToArray(), middle);
        Assert.True(stream.BytesFetched <= 3 * 64 * 1024, "range reads must stay bounded to blocks");
    }

    [Fact]
    public void Capabilities_report_transport_and_range_support_without_shadow_write()
    {
        var (connector, _) = Create();
        var capabilities = connector.Capabilities;
        Assert.Equal("memory", capabilities.Transport);
        Assert.True(capabilities.RangeRead && capabilities.VersionPinning && capabilities.OfflineReplay && capabilities.CheckpointCapture);
        Assert.False(capabilities.ShadowWrite);
        Assert.Equal("object-pattern", connector.ConfigurationSchema.SelectorKind);
    }

    [Fact]
    public async Task Reference_round_trips_and_malformed_references_are_rejected()
    {
        var encoded = RemoteObjectSourceConnector.EncodeReference("a/b.txt", "v9");
        Assert.True(RemoteObjectSourceConnector.TryDecodeReference(encoded, out var key, out var version));
        Assert.Equal(("a/b.txt", "v9"), (key, version));
        Assert.False(RemoteObjectSourceConnector.TryDecodeReference("object-v1:@@@", out _, out _));
        Assert.False(RemoteObjectSourceConnector.TryDecodeReference(RemoteObjectSourceConnector.EncodeReference("../x", null), out _, out _));
        await Task.CompletedTask;
    }
}
