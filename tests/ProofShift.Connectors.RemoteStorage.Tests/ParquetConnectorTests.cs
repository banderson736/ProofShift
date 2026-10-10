using System.Security.Cryptography;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.Parquet;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Domain;

namespace ProofShift.Connectors.RemoteStorage.Tests;

/// <summary>Parquet format semantics over the local transport, using files written by Apache Arrow (an independent implementation).</summary>
public sealed class ParquetConnectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"proofshift-parquet-{Guid.NewGuid():N}");

    public ParquetConnectorTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "parquet")))
            File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ParquetSourceConnector Connector() => new([new LocalFileStoreFactory()]);

    private ConnectorContext Context() => CheckpointHarness.Context("parquet",
        [new("transport", "filesystem"), new("root", _root)], new Dictionary<string, string>(), "Generic.Record");

    private static ArtifactSelector Selector(string path, params string[] identity) =>
        new("parquet", [new("path", path)], identity.Length == 0 ? ["id"] : identity);

    private async Task<List<RecordEnvelope>> ReadAsync(string path, params string[] identity)
    {
        var records = new List<RecordEnvelope>();
        await foreach (var record in Connector().ReadAsync(Context(), Selector(path, identity), new ReadOptions(), TestContext.Current.CancellationToken))
            records.Add(record);
        return records;
    }

    [Theory]
    [InlineData("rich.parquet")]
    [InlineData("rich-gzip.parquet")]
    [InlineData("rich-nodict.parquet")]
    public async Task Exact_types_nulls_and_row_groups_are_preserved_across_codecs(string file)
    {
        var records = await ReadAsync(file);
        Assert.Equal(7, records.Count);
        var first = records[0].Values;
        Assert.Equal(new IntegerValue(1), first["id"]);
        Assert.Equal(new IntegerValue(1), first["small"]);
        Assert.Equal(new StringValue("alpha"), first["name"]);
        Assert.Equal(new DecimalValue(0.01m), first["amount"]);
        Assert.Equal(new BooleanValue(true), first["flag"]);
        Assert.Equal(new DateValue(new DateOnly(1980, 2, 29)), first["born"]);
        Assert.Equal(new InstantValue(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1234560)), first["seen_utc"]);
        Assert.Equal(new LocalDateTimeValue(new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified).AddTicks(1234560)), first["seen_local"]);
        Assert.Equal(new StringValue("0.1"), first["ratio64"]);
        Assert.Equal(new StringValue("0.1"), first["ratio32"]);
        var big = records[1].Values;
        Assert.Equal(new DecimalValue(12345678901234567890.123456m), big["amount"]);
        Assert.Equal(new StringValue("ünï ✓"), big["name"]);
        Assert.Equal(new IntegerValue(-2), big["small"]);
        var nulls = records[3].Values;
        foreach (var column in new[] { "small", "name", "amount", "flag" }) Assert.IsType<NullValue>(nulls[column]);
        foreach (var column in new[] { "ratio32", "ratio64", "blob", "born" }) Assert.IsType<NullValue>(records[2].Values[column]);
        Assert.Equal(new StringValue(""), records[4].Values["name"]);
        Assert.Equal(new DateValue(new DateOnly(1, 1, 1)), records[4].Values["born"]);
        Assert.Equal(new DateValue(new DateOnly(9999, 12, 31)), records[6].Values["born"]);
        Assert.Equal(new IntegerValue(long.MaxValue), records[6].Values["u64"]);
        Assert.Equal(new StringValue("1E+100"), records[4].Values["ratio64"]);
    }

    [Fact]
    public async Task Binary_columns_are_preserved_as_bytes_and_replayable_from_the_exact_cell()
    {
        var records = await ReadAsync("rich.parquet");
        var withBinary = records[0];
        var binary = Assert.IsType<BinaryReferenceValue>(withBinary.Values["blob"]);
        Assert.Equal(4, binary.ContentLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([0x00, 0x01, 0xfe, 0xff])).ToLowerInvariant(), binary.Sha256);
        await using var stream = await Connector().OpenBinaryReadAsync(Context(), Selector("rich.parquet"), withBinary.Artifact, binary, TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal([0x00, 0x01, 0xfe, 0xff], copy.ToArray());
        Assert.IsType<NullValue>(records[2].Values["blob"]);
        Assert.Equal(0, Assert.IsType<BinaryReferenceValue>(records[1].Values["blob"]).ContentLength);

        // A reference minted for one artifact cannot be redeemed for another column or file.
        var forged = new BinaryReferenceValue(binary.Reference.Replace("parquet-cell-v1:", "parquet-cell-v1:AA"), 4, binary.Sha256);
        await Assert.ThrowsAsync<ConnectorReadException>(async () => await Connector().OpenBinaryReadAsync(
            Context(), Selector("rich.parquet"), withBinary.Artifact, forged, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Identity_is_the_configured_semantic_key_and_a_null_component_fails_closed()
    {
        var byId = await ReadAsync("rich.parquet", "id");
        var byUnicodeName = new ArtifactSelector("parquet", [new("path", "rich.parquet"), new("columns", "id,name")], ["id", "name"]);
        var failure = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            await foreach (var _ in Connector().ReadAsync(Context(), byUnicodeName, new ReadOptions(), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(ConnectorIssueCodes.NonDeterministicIdentity, failure.Code);
        Assert.Equal(7, byId.Select(record => record.Artifact.Id.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal((await ReadAsync("rich-gzip.parquet", "id")).Select(record => record.Artifact.Id.Value), byId.Select(record => record.Artifact.Id.Value));
    }

    [Fact]
    public async Task Duplicate_and_null_identities_fail_closed()
    {
        var duplicate = await Assert.ThrowsAsync<ConnectorReadException>(async () => await ReadAsync("duplicate.parquet"));
        Assert.Equal(ConnectorIssueCodes.DuplicateArtifactIdentity, duplicate.Code);
        var missing = await Assert.ThrowsAsync<ConnectorReadException>(async () => await ReadAsync("nullid.parquet"));
        Assert.Equal(ConnectorIssueCodes.NonDeterministicIdentity, missing.Code);
    }

    [Theory]
    [InlineData("nested.parquet")]
    [InlineData("struct.parquet")]
    [InlineData("bigdecimal.parquet")]
    [InlineData("nanos.parquet")]
    public async Task Unsupported_logical_types_fail_closed_with_a_schema_diagnostic(string file)
    {
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () => await ReadAsync(file));
        Assert.Equal(ConnectorIssueCodes.UnsupportedColumnarSchema, exception.Code);
        var inspection = await Connector().InspectAsync(Context(), Selector(file), TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Invalid, inspection.Status);
        Assert.Equal(ConnectorIssueCodes.UnsupportedColumnarSchema, inspection.Issues.Single().Code);
    }

    [Fact]
    public async Task Column_projection_reads_only_selected_columns_and_rejects_unknown_ones()
    {
        var selector = new ArtifactSelector("parquet", [new("path", "rich.parquet"), new("columns", "id,name")], ["id"]);
        var records = new List<RecordEnvelope>();
        await foreach (var record in Connector().ReadAsync(Context(), selector, new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
        Assert.All(records, record => Assert.Equal(["id", "name"], record.Values.Keys.Order(StringComparer.Ordinal).ToArray()));
        var unknown = new ArtifactSelector("parquet", [new("path", "rich.parquet"), new("columns", "id,nope")], ["id"]);
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            await foreach (var _ in Connector().ReadAsync(Context(), unknown, new ReadOptions(), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(ConnectorIssueCodes.IdentityFieldNotFound, exception.Code);
    }

    [Fact]
    public async Task Truncated_and_garbage_objects_fail_closed_without_partial_records()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "rich.parquet"), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "truncated.parquet"), bytes[..(bytes.Length / 2)], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, "garbage.parquet"), "PAR1 this is not parquet PAR1"u8.ToArray(), TestContext.Current.CancellationToken);
        var flipped = (byte[])bytes.Clone();
        flipped[^6] ^= 0xFF;
        await File.WriteAllBytesAsync(Path.Combine(_root, "footer-damaged.parquet"), flipped, TestContext.Current.CancellationToken);
        foreach (var file in new[] { "truncated.parquet", "garbage.parquet", "footer-damaged.parquet" })
        {
            var emitted = new List<RecordEnvelope>();
            var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
            {
                await foreach (var record in Connector().ReadAsync(Context(), Selector(file), new ReadOptions(), TestContext.Current.CancellationToken)) emitted.Add(record);
            });
            Assert.Equal(ConnectorIssueCodes.CorruptColumnarFile, exception.Code);
            Assert.Empty(emitted);
        }
    }

    [Fact]
    public async Task Many_row_groups_stream_in_order_with_bounded_range_requests()
    {
        var count = 0;
        long previous = -1;
        await foreach (var record in Connector().ReadAsync(Context(), Selector("manygroups.parquet"), new ReadOptions(), TestContext.Current.CancellationToken))
        {
            var id = ((IntegerValue)record.Values["id"]).Value;
            Assert.Equal(previous + 1, id);
            previous = id;
            count++;
        }

        Assert.Equal(5000, count);
        var inspection = await Connector().InspectAsync(Context(), Selector("manygroups.parquet"), TestContext.Current.CancellationToken);
        Assert.Equal(5000, inspection.EstimatedRecords);
    }

    [Fact]
    public async Task Discovery_reports_schema_row_groups_and_a_structural_fingerprint_that_detects_drift()
    {
        var connector = Connector();
        var a = await connector.DiscoverAsync(Context(), [new ArtifactSelector("parquet", [new("path", "drift-a.parquet")])], TestContext.Current.CancellationToken);
        var b = await connector.DiscoverAsync(Context(), [new ArtifactSelector("parquet", [new("path", "drift-b.parquet")])], TestContext.Current.CancellationToken);
        var again = await connector.DiscoverAsync(Context(), [new ArtifactSelector("parquet", [new("path", "drift-a.parquet")])], TestContext.Current.CancellationToken);
        Assert.Equal(a.Fingerprint, again.Fingerprint);
        var objectA = Assert.Single(a.Objects);
        Assert.Equal(["id", "v"], objectA.Fields.Select(field => field.Name).ToArray());
        Assert.Equal(1, objectA.RecordCount);
        Assert.Equal("1", objectA.SelectorProperties!["rowGroups"]);
        Assert.NotEqual(Assert.Single(a.Objects).Fields[1].NativeType, Assert.Single(b.Objects).Fields[1].NativeType);

        var rich = await connector.DiscoverAsync(Context(), [new ArtifactSelector("parquet", [new("path", "rich.parquet")])], TestContext.Current.CancellationToken);
        var amount = Assert.Single(rich.Objects).Fields.Single(field => field.Name == "amount");
        Assert.Contains("(28,6)", amount.NativeType, StringComparison.Ordinal);
        Assert.True(amount.IsNullable);
    }

    [Fact]
    public async Task Checkpoint_capture_replays_offline_with_binary_cells_after_the_source_is_deleted()
    {
        var settings = new Dictionary<string, string> { ["transport"] = "filesystem", ["root"] = _root };
        var replay = await CheckpointHarness.CaptureAndReplayAsync(Connector(), "parquet", settings, new Dictionary<string, string>(),
            Selector("rich.parquet"), "Generic.Record", () =>
            {
                File.Delete(Path.Combine(_root, "rich.parquet"));
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointStatus.Complete, replay.Capture.Status);
        Assert.False(replay.Capture.Checkpoint!.CrossSystemAtomic);
        Assert.Equal(7, replay.Records.Count);
        Assert.Equal(new DecimalValue(12345678901234567890.123456m), replay.Records[1].Values["amount"]);
        var first = replay.Records[0];
        Assert.Equal([0x00, 0x01, 0xfe, 0xff], replay.Binaries[first.Artifact.Identity]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "parquet", "rich.parquet"), TestContext.Current.CancellationToken))).ToLowerInvariant(),
            first.Provenance.SourceHash);
    }

    [Fact]
    public async Task Mutation_during_capture_is_detected()
    {
        var path = Path.Combine(_root, "rich.parquet");
        var enumerator = Connector().ReadForCheckpointAsync(Context(), Selector("rich.parquet"), new ReadOptions(), TestContext.Current.CancellationToken).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1));
        var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
        {
            while (await enumerator.MoveNextAsync()) { }
        });
        Assert.Equal(ConnectorIssueCodes.ArtifactChangedDuringCapture, exception.Code);
    }

    [Fact]
    public void Schema_and_capabilities_describe_every_installed_transport()
    {
        var connector = Connector();
        Assert.Equal("parquet", connector.ConfigurationSchema.SelectorKind);
        Assert.Contains("filesystem", connector.ConfigurationSchema.EndpointProperties["transport"], StringComparison.Ordinal);
        Assert.True(connector.Capabilities.StructuredRead);
        Assert.True(connector.Capabilities.RangeRead);
        Assert.False(connector.Capabilities.ShadowWrite);
        Assert.NotNull(connector.ConfigurationSchema.ToJsonSchema());
    }
}
