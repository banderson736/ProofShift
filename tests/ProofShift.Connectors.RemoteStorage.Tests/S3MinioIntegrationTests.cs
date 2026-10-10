using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.S3;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Connectors.RemoteStorage.Tests;

public sealed class MinioFixture : IAsyncLifetime
{
    // Test-only fixture image, pinned by digest; not redistributed (see docs/CONNECTOR_TEST_RUNTIMES.md).
    public const string Image = "bitnamilegacy/minio@sha256:e8bf17d3fc5465b1e792d4296245a53135e23dbd8d63086d7529b8bfdb8283af";
    public const string AccessKey = "proofshift-test-user";
    public const string SecretKey = "Synthetic-Minio-Secret-2026!";
    private IContainer? _container;
    public string ServiceUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(9000, true)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPath("/minio/health/ready").ForPort(9000)))
            .Build();
        try
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for S3-compatible integration tests.");
        }

        ServiceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";
    }

    public AmazonS3Client Admin() => new(new Amazon.Runtime.BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[Collection("RemoteStorage")]
public sealed class S3MinioIntegrationTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private ConnectorContext Context(string bucket, string prefix = "", int pageSize = 1000, string accessKey = MinioFixture.AccessKey,
        string secretKey = MinioFixture.SecretKey, string? sessionToken = null)
    {
        var secrets = new Dictionary<string, string> { ["accessKeyId"] = accessKey, ["secretAccessKey"] = secretKey };
        if (sessionToken is not null) secrets["sessionToken"] = sessionToken;
        return CheckpointHarness.Context("s3",
            [new("bucket", bucket), new("prefix", prefix), new("serviceUrl", minio.ServiceUrl), new("allowInsecureHttp", "true"),
                new("region", "us-east-1"), new("authentication", "static"), new("pageSize", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            secrets);
    }

    private static ArtifactSelector Selector(string pattern) => new("object-pattern", [new("pattern", pattern)], ["relativePath"]);

    private async Task<string> NewBucketAsync(bool versioned = false)
    {
        var bucket = "ps-" + Guid.NewGuid().ToString("N")[..16];
        using var admin = minio.Admin();
        await admin.PutBucketAsync(bucket, TestContext.Current.CancellationToken);
        if (versioned)
            await admin.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
            }, TestContext.Current.CancellationToken);
        return bucket;
    }

    private async Task<string> PutAsync(string bucket, string key, byte[] data)
    {
        using var admin = minio.Admin();
        var response = await admin.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(data), UseChunkEncoding = false },
            TestContext.Current.CancellationToken);
        return response.VersionId;
    }

    [Fact]
    public async Task Pagination_special_names_prefix_boundaries_and_hashes_are_exact()
    {
        var bucket = await NewBucketAsync();
        var expected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < 250; index++)
            expected[$"data/item-{index:D4}.txt"] = Encoding.UTF8.GetBytes($"payload {index}");
        expected["data/ünï côdé ✓.txt"] = "unicode"u8.ToArray();
        expected["data/with space.txt"] = "space"u8.ToArray();
        expected["data/a%2Fb+c.txt"] = "encoded"u8.ToArray();
        expected["data/empty.txt"] = [];
        expected["data/nested/deep/er.txt"] = "deep"u8.ToArray();
        foreach (var pair in expected) await PutAsync(bucket, pair.Key, pair.Value);
        await PutAsync(bucket, "data-other/decoy.txt", "outside boundary"u8.ToArray());
        await PutAsync(bucket, "database.txt", "outside boundary"u8.ToArray());

        var connector = new S3SourceConnector();
        var context = Context(bucket, "data", pageSize: 100);
        var inspection = await connector.InspectAsync(context, Selector("**/*.txt"), TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
        Assert.Equal(expected.Count, inspection.Files);

        var records = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadAsync(context, Selector("**/*.txt"), new ReadOptions(), TestContext.Current.CancellationToken))
            records.Add(record);
        var byteOrdered = expected.Keys.Select(key => key["data/".Length..]).OrderBy(key => key, Comparer<string>.Create(RemoteKeyRules.CompareUtf8)).ToArray();
        Assert.Equal(byteOrdered, records.Select(record => record.Artifact.Identity).ToArray());
        foreach (var record in records)
        {
            var bytes = expected["data/" + record.Artifact.Identity];
            Assert.Equal(Sha(bytes), record.Provenance.SourceHash);
            Assert.Equal((long)bytes.Length, ((IntegerValue)record.Values["size"]).Value);
            Assert.True(record.Provenance.Metadata.ContainsKey("providerETag"));
        }

        Assert.DoesNotContain(records, record => record.Artifact.Identity.Contains("decoy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_bucket_and_bad_credentials_fail_closed_without_leaking_secrets()
    {
        var connector = new S3SourceConnector();
        var missing = await connector.InspectAsync(Context("ps-does-not-exist"), Selector("**/*"), TestContext.Current.CancellationToken);
        Assert.NotEqual(SourceInspectionStatus.Valid, missing.Status);

        const string badSecret = "Synthetic-Wrong-Secret-Value-123";
        const string badSessionToken = "Synthetic-Wrong-Session-Token-456";
        var bucket = await NewBucketAsync();
        await PutAsync(bucket, "x.txt", "x"u8.ToArray());
        var missingObject = await connector.InspectAsync(Context(bucket), Selector("not-present.txt"), TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Invalid, missingObject.Status);
        Assert.Equal(ConnectorIssueCodes.SourceObjectNotFound, Assert.Single(missingObject.Issues).Code);
        var bad = await connector.InspectAsync(Context(bucket, accessKey: "no-such-user", secretKey: badSecret, sessionToken: badSessionToken),
            Selector("**/*"), TestContext.Current.CancellationToken);
        var issue = Assert.Single(bad.Issues);
        Assert.Equal(ConnectorIssueCodes.RemoteAuthenticationFailed, issue.Code);
        Assert.DoesNotContain(badSecret, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(badSessionToken, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no-such-user", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Versioned_object_is_pinned_and_checkpoint_replays_offline_after_remote_mutation()
    {
        var bucket = await NewBucketAsync(versioned: true);
        var original = new byte[2 * 1024 * 1024 + 17];
        new Random(42).NextBytes(original);
        var firstVersion = await PutAsync(bucket, "ledger/original.bin", original);
        Assert.False(string.IsNullOrEmpty(firstVersion));

        var connector = new S3SourceConnector();
        var settings = new Dictionary<string, string>
        {
            ["bucket"] = bucket, ["prefix"] = "ledger", ["serviceUrl"] = minio.ServiceUrl, ["allowInsecureHttp"] = "true",
            ["region"] = "us-east-1", ["authentication"] = "static",
            ["accessKeyId"] = "secret:PS_TEST_S3_KEY", ["secretAccessKey"] = "secret:PS_TEST_S3_SECRET"
        };
        var environment = new Dictionary<string, string> { ["PS_TEST_S3_KEY"] = MinioFixture.AccessKey, ["PS_TEST_S3_SECRET"] = MinioFixture.SecretKey };
        var replay = await CheckpointHarness.CaptureAndReplayAsync(connector, "s3", settings, environment, Selector("**/*.bin"),
            "File.Artifact", async () =>
            {
                // Mutate the live object after capture: replay must not consult the transport.
                await PutAsync(bucket, "ledger/original.bin", "overwritten"u8.ToArray());
            }, TestContext.Current.CancellationToken);

        Assert.Equal(CheckpointStatus.Complete, replay.Capture.Status);
        Assert.False(replay.Capture.Checkpoint!.CrossSystemAtomic);
        var record = Assert.Single(replay.Records);
        Assert.Equal("original.bin", record.Artifact.Identity);
        Assert.Equal(firstVersion, record.Provenance.Metadata["providerVersionId"]);
        Assert.Equal(Sha(original), record.Provenance.SourceHash);
        Assert.Equal(original, replay.Binaries["original.bin"]);
    }

    [Fact]
    public async Task Binary_reference_for_a_pinned_version_reads_the_original_bytes_after_overwrite()
    {
        var bucket = await NewBucketAsync(versioned: true);
        await PutAsync(bucket, "v/doc.txt", "version-one"u8.ToArray());
        var connector = new S3SourceConnector();
        var context = Context(bucket, "v");
        RecordEnvelope? record = null;
        await foreach (var item in connector.ReadAsync(context, Selector("*.txt"), new ReadOptions(), TestContext.Current.CancellationToken)) record = item;
        Assert.NotNull(record);
        await PutAsync(bucket, "v/doc.txt", "version-two-longer"u8.ToArray());

        await using var stream = await connector.OpenBinaryReadAsync(context, Selector("*.txt"), record.Artifact,
            (BinaryReferenceValue)record.Values["content"], TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal("version-one", Encoding.UTF8.GetString(copy.ToArray()));
    }

    [Fact]
    public async Task Ranged_reads_return_exact_slices_and_large_objects_stream_with_matching_hash()
    {
        var bucket = await NewBucketAsync();
        var large = new byte[24 * 1024 * 1024];
        new Random(5).NextBytes(large);
        await PutAsync(bucket, "big/large.bin", large);
        var context = Context(bucket, "big");
        await using var store = new S3StoreFactory().Create(context);
        var info = await store.StatAsync("large.bin", null, TestContext.Current.CancellationToken);
        Assert.Equal(large.Length, info.Length);

        await using (var slice = await store.OpenReadAsync(info, 12_345_678, 1000, TestContext.Current.CancellationToken))
        {
            var buffer = new byte[1000];
            await slice.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(large.AsSpan(12_345_678, 1000).ToArray(), buffer);
        }

        await using var seekable = new SeekableRemoteStream(store, info, 256 * 1024);
        seekable.Seek(-1000, SeekOrigin.End);
        var tail = new byte[1000];
        await seekable.ReadExactlyAsync(tail, TestContext.Current.CancellationToken);
        Assert.Equal(large.AsSpan(large.Length - 1000).ToArray(), tail);

        var records = new List<RecordEnvelope>();
        await foreach (var record in new S3SourceConnector().ReadAsync(context, Selector("*.bin"), new ReadOptions(), TestContext.Current.CancellationToken))
            records.Add(record);
        Assert.Equal(Sha(large), Assert.Single(records).Provenance.SourceHash);

        var settings = new Dictionary<string, string>
        {
            ["bucket"] = bucket, ["prefix"] = "big", ["serviceUrl"] = minio.ServiceUrl, ["allowInsecureHttp"] = "true",
            ["region"] = "us-east-1", ["authentication"] = "static", ["accessKeyId"] = "secret:PS_TEST_S3_KEY",
            ["secretAccessKey"] = "secret:PS_TEST_S3_SECRET"
        };
        var replay = await CheckpointHarness.CaptureAndReplayAsync(new S3SourceConnector(), "s3", settings,
            new Dictionary<string, string> { ["PS_TEST_S3_KEY"] = MinioFixture.AccessKey, ["PS_TEST_S3_SECRET"] = MinioFixture.SecretKey },
            Selector("*.bin"), "File.Artifact", async () =>
            {
                using var admin = minio.Admin();
                await admin.DeleteObjectAsync(bucket, "big/large.bin", TestContext.Current.CancellationToken);
                await admin.DeleteBucketAsync(bucket, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointStatus.Complete, replay.Capture.Status);
        Assert.False(replay.Capture.Checkpoint!.CrossSystemAtomic);
        var replayed = Assert.Single(replay.Records);
        Assert.Equal(Sha(large), replayed.Provenance.SourceHash);
        Assert.Equal(large, replay.Binaries["large.bin"]);
    }

    [Fact]
    public async Task S3_objects_can_be_observed_as_a_read_only_target_with_provenance()
    {
        var bucket = await NewBucketAsync();
        await PutAsync(bucket, "target/out.csv", "id,amount\n1,10.00\n"u8.ToArray());
        var observer = new ReaderTargetObserver(new S3SourceConnector());
        var records = new List<RecordEnvelope>();
        await foreach (var record in observer.ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(Context(bucket, "target"), new RunId(Guid.NewGuid()), SystemRole.Target),
            Selector("*.csv"), new ReadOptions()), TestContext.Current.CancellationToken))
            records.Add(record);
        var observed = Assert.Single(records);
        Assert.Equal("out.csv", ((StringValue)observed.Values["relativePath"]).Value);
        Assert.Equal("s3", observed.Provenance.Metadata["transport"]);
        Assert.Equal($"s3://{bucket}/target/", observed.Provenance.Metadata["scope"]);
    }

    [Fact]
    public void Plain_http_endpoints_and_embedded_credentials_are_rejected_unless_explicitly_allowed()
    {
        var factory = new S3StoreFactory();
        ConnectorContext With(params (string Key, string Value)[] items) => CheckpointHarness.Context("s3",
            items.Select(item => new KeyValuePair<string, string>(item.Key, item.Value)), new Dictionary<string, string>());
        var plain = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(With(("bucket", "b"), ("serviceUrl", "http://example.invalid:9000"))));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, plain.Code);
        var embedded = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(With(("bucket", "b"), ("serviceUrl", "https://user:pass@example.invalid"), ("allowInsecureHttp", "true"))));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, embedded.Code);
        Assert.DoesNotContain("pass", embedded.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_errors_map_to_classified_failures()
    {
        Assert.Equal(RemoteFailureKind.Authorization, S3ObjectStore.Map(new AmazonS3Exception("denied") { StatusCode = System.Net.HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" }).Kind);
        Assert.Equal(RemoteFailureKind.Authentication, S3ObjectStore.Map(new AmazonS3Exception("bad") { StatusCode = System.Net.HttpStatusCode.Forbidden, ErrorCode = "SignatureDoesNotMatch" }).Kind);
        Assert.Equal(RemoteFailureKind.Transient, S3ObjectStore.Map(new AmazonS3Exception("busy") { StatusCode = System.Net.HttpStatusCode.ServiceUnavailable }).Kind);
        Assert.Equal(RemoteFailureKind.VersionUnavailable, S3ObjectStore.Map(new AmazonS3Exception("gone") { StatusCode = System.Net.HttpStatusCode.NotFound, ErrorCode = "NoSuchVersion" }).Kind);
    }
}
