using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.AzureBlob;
using ProofShift.Connectors.Parquet;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.S3;
using ProofShift.Connectors.Sftp;
using ProofShift.Domain;
using ProofShift.Engine;
using ProofShift.Graph;
using ProofShift.Snapshots;

namespace ProofShift.Connectors.RemoteStorage.Tests;

/// <summary>
/// E6: the same logical artifacts delivered through different transports must yield the same semantic values and the
/// same content hash, and a single checkpoint may mix transports without claiming cross-system atomicity.
/// </summary>
[Collection("RemoteStorage")]
public sealed class CrossProviderAssuranceTests(MinioFixture minio, AzuriteFixture azurite, SftpFixture sftp)
    : IClassFixture<MinioFixture>, IClassFixture<AzuriteFixture>, IClassFixture<SftpFixture>
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "parquet", name));
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ParquetSourceConnector Parquet() => new([new LocalFileStoreFactory(), new S3StoreFactory(), new AzureBlobStoreFactory(), new SftpStoreFactory()]);

    private sealed record Delivery(string Name, ConnectorContext Context, Dictionary<string, string> Endpoint, Dictionary<string, string> Environment);

    private async Task<IReadOnlyList<Delivery>> DeliverAsync(string objectName, byte[] data, string transportSuffix)
    {
        var run = "x-" + Guid.NewGuid().ToString("N")[..12];
        var deliveries = new List<Delivery>();

        var bucket = "ps-" + Guid.NewGuid().ToString("N")[..16];
        using (var admin = minio.Admin())
        {
            await admin.PutBucketAsync(bucket, TestContext.Current.CancellationToken);
            await admin.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = $"d/{objectName}", InputStream = new MemoryStream(data), UseChunkEncoding = false }, TestContext.Current.CancellationToken);
        }

        var s3 = new Dictionary<string, string>
        {
            ["bucket"] = bucket, ["prefix"] = "d", ["serviceUrl"] = minio.ServiceUrl, ["allowInsecureHttp"] = "true", ["region"] = "us-east-1",
            ["authentication"] = "static", ["accessKeyId"] = "secret:PS_X_S3_KEY", ["secretAccessKey"] = "secret:PS_X_S3_SECRET"
        };
        var s3Env = new Dictionary<string, string> { ["PS_X_S3_KEY"] = MinioFixture.AccessKey, ["PS_X_S3_SECRET"] = MinioFixture.SecretKey };
        deliveries.Add(new Delivery("s3", Ctx("s3", s3, s3Env), s3, s3Env));

        var container = azurite.Admin().GetBlobContainerClient("ps-" + Guid.NewGuid().ToString("N")[..16]);
        await container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await container.GetBlobClient($"d/{objectName}").UploadAsync(new MemoryStream(data), overwrite: true, TestContext.Current.CancellationToken);
        var azure = new Dictionary<string, string>
        {
            ["account"] = AzuriteFixture.Account, ["serviceUrl"] = azurite.ServiceUrl, ["allowInsecureHttp"] = "true", ["container"] = container.Name,
            ["prefix"] = "d", ["authentication"] = "account-key", ["accountKey"] = "secret:PS_X_AZ_KEY"
        };
        var azureEnv = new Dictionary<string, string> { ["PS_X_AZ_KEY"] = AzuriteFixture.Key };
        deliveries.Add(new Delivery("azure-blob", Ctx("azure-blob", azure, azureEnv), azure, azureEnv));

        await sftp.ShellAsync($"mkdir -p /home/{SftpFixture.User}/upload/{run}/d && chown -R 1001:100 /home/{SftpFixture.User}/upload/{run}");
        await sftp.PutAsync($"{run}/d/{objectName}", data);
        var sftpSettings = new Dictionary<string, string>
        {
            ["host"] = sftp.Host, ["port"] = sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["username"] = SftpFixture.User,
            ["remoteRoot"] = $"/upload/{run}/d", ["hostKeyFingerprint"] = sftp.Fingerprint, ["authentication"] = "password", ["password"] = "secret:PS_X_SFTP_PASSWORD"
        };
        var sftpEnv = new Dictionary<string, string> { ["PS_X_SFTP_PASSWORD"] = SftpFixture.Password };
        deliveries.Add(new Delivery("sftp", Ctx("sftp", sftpSettings, sftpEnv), sftpSettings, sftpEnv));

        var local = Path.Combine(Path.GetTempPath(), $"proofshift-x-{run}");
        Directory.CreateDirectory(local);
        await File.WriteAllBytesAsync(Path.Combine(local, objectName), data, TestContext.Current.CancellationToken);
        var localSettings = new Dictionary<string, string> { ["root"] = local };
        deliveries.Add(new Delivery("filesystem", Ctx("filesystem", localSettings, new Dictionary<string, string>()), localSettings, new Dictionary<string, string>()));
        _ = transportSuffix;
        return deliveries;
    }

    private static ConnectorContext Ctx(string transport, Dictionary<string, string> endpoint, Dictionary<string, string> env)
    {
        var plain = endpoint.Where(pair => !pair.Value.StartsWith("secret:", StringComparison.Ordinal)).ToList();
        plain.Add(new("transport", transport));
        var secrets = endpoint.Where(pair => pair.Value.StartsWith("secret:", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => env[pair.Value["secret:".Length..]]);
        return CheckpointHarness.Context("parquet", plain, secrets, "Generic.Record");
    }

    private static string Canonical(RecordEnvelope record) => string.Join('|', record.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key + "=" + (pair.Value is BinaryReferenceValue binary ? $"bin:{binary.ContentLength}:{binary.Sha256}" : pair.Value.ToString())));

    [Fact]
    public async Task Same_parquet_delivered_over_every_transport_yields_identical_records_hashes_and_identities()
    {
        var bytes = Fixture("rich.parquet");
        var deliveries = await DeliverAsync("rich.parquet", bytes, "");
        var selector = new ArtifactSelector("parquet", [new("path", "rich.parquet")], ["id"]);
        List<string>? baseline = null;
        List<string>? baselineIds = null;
        foreach (var delivery in deliveries)
        {
            var records = new List<RecordEnvelope>();
            await foreach (var record in Parquet().ReadAsync(delivery.Context, selector, new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
            Assert.Equal(7, records.Count);
            Assert.All(records, record => Assert.Equal(Sha(bytes), record.Provenance.SourceHash));
            Assert.All(records, record => Assert.NotEqual("", record.Provenance.Metadata["scope"]));
            var canonical = records.Select(Canonical).ToList();
            var ids = records.Select(record => record.Artifact.Id.Value).ToList();
            baseline ??= canonical;
            baselineIds ??= ids;
            Assert.Equal(baseline, canonical);
            Assert.Equal(baselineIds, ids);
            Assert.True(records[0].Provenance.Metadata["transport"] == (delivery.Name == "filesystem" ? "filesystem" : delivery.Name), delivery.Name);
        }
    }

    [Fact]
    public async Task Parquet_over_each_transport_discovers_the_same_structural_fingerprint()
    {
        var deliveries = await DeliverAsync("rich.parquet", Fixture("rich.parquet"), "");
        var selector = new ArtifactSelector("parquet", [new("path", "rich.parquet")], ["id"]);
        var fingerprints = new List<string>();
        foreach (var delivery in deliveries)
            fingerprints.Add((await Parquet().DiscoverAsync(delivery.Context, [selector], TestContext.Current.CancellationToken)).Fingerprint);
        Assert.Single(fingerprints.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Binary_cells_captured_from_every_transport_replay_from_the_checkpoint_alone()
    {
        var bytes = Fixture("rich.parquet");
        var deliveries = await DeliverAsync("rich.parquet", bytes, "");
        var selector = new ArtifactSelector("parquet", [new("path", "rich.parquet")], ["id"]);
        foreach (var delivery in deliveries)
        {
            var replay = await CheckpointHarness.CaptureAndReplayAsync(Parquet(), "parquet",
                delivery.Endpoint.Concat([new("transport", delivery.Name)]).ToDictionary(pair => pair.Key, pair => pair.Value), delivery.Environment,
                selector, "Generic.Record", null, TestContext.Current.CancellationToken);
            Assert.True(replay.Capture.Status == CheckpointStatus.Complete, $"{delivery.Name}: {replay.Capture.FailureCode}");
            Assert.Equal(7, replay.Records.Count);
            Assert.Equal([0x00, 0x01, 0xfe, 0xff], replay.Binaries["2:id=1:1"]);
        }
    }

    [Fact]
    public async Task One_checkpoint_can_mix_transports_and_formats_without_claiming_cross_system_atomicity()
    {
        var bytes = Fixture("rich.parquet");
        var text = Encoding.UTF8.GetBytes("legacy-delivery");
        var deliveries = await DeliverAsync("rich.parquet", bytes, "");
        var textDeliveries = await DeliverAsync("note.txt", text, "");
        var s3Text = textDeliveries.Single(delivery => delivery.Name == "s3");
        var sftpText = textDeliveries.Single(delivery => delivery.Name == "sftp");
        var azureParquet = deliveries.Single(delivery => delivery.Name == "azure-blob");

        var systems = new List<SystemDefinition>();
        var nodes = new List<MigrationNode>();
        var environment = new Dictionary<string, string>();
        void Add(string key, string connector, Dictionary<string, string> endpoint, Dictionary<string, string> env, ArtifactSelector selector, string semantic)
        {
            var system = new SystemDefinition(new SystemId(key), key, SystemRole.Source,
                [new StorageEndpointDefinition(new StorageEndpointId("endpoint"), new ConnectorId(connector), endpoint)]);
            systems.Add(system);
            nodes.Add(new MigrationNode(new MigrationNodeId(Guid.NewGuid()), key + "-node", MigrationNodeType.Source, semantic, system.Id, system.StorageEndpoints[0].Id, selector));
            foreach (var pair in env) environment[pair.Key] = pair.Value;
        }

        Add("s3-notes", "s3", s3Text.Endpoint, s3Text.Environment, new ArtifactSelector("object-pattern", [new("pattern", "*.txt")], ["relativePath"]), "File.Artifact");
        Add("sftp-notes", "sftp", sftpText.Endpoint, sftpText.Environment, new ArtifactSelector("object-pattern", [new("pattern", "*.txt")], ["relativePath"]), "File.Artifact");
        Add("azure-parquet", "parquet", azureParquet.Endpoint.Concat([new("transport", "azure-blob")]).ToDictionary(p => p.Key, p => p.Value), azureParquet.Environment,
            new ArtifactSelector("parquet", [new("path", "rich.parquet")], ["id"]), "Generic.Record");

        var graph = new MigrationGraph(new MigrationGraphId(Guid.NewGuid()), nodes, [], new string('a', 64), "mixed-test-v1");
        var configuration = new LoadedProjectConfiguration(new RootConfigurationDto(1, new ProjectConfigurationDto("mixed", "Mixed"), null, [], null, null, null),
            [], systems, [], "mixed", new string('b', 64));
        var root = Path.Combine(Path.GetTempPath(), $"proofshift-mixed-{Guid.NewGuid():N}");
        try
        {
            var factory = new RuntimeConnectorContextFactory(new DictionaryEnvironment(environment));
            var registry = new ConnectorRegistry([new S3SourceConnector(), new SftpSourceConnector(), Parquet()]);
            var store = new FileSystemSnapshotStore(root);
            var capture = await new SnapshotCaptureService(registry, store, factory).CaptureAsync(configuration, graph, TestContext.Current.CancellationToken);
            Assert.True(capture.Status == CheckpointStatus.Complete, capture.FailureCode);
            Assert.False(capture.Checkpoint!.CrossSystemAtomic);
            Assert.Equal(3, capture.Checkpoint.Endpoints.Count);
            Assert.All(capture.Checkpoint.Endpoints, endpoint => Assert.Equal(SourceConsistencyGuarantee.Observed, endpoint.SourceConsistency));
            Assert.Equal(1 + 1 + 7, capture.CapturedArtifacts);

            await using var loaded = await store.OpenCompleteAsync(capture.Id.Value.ToString("N"), TestContext.Current.CancellationToken);
            var replay = new CheckpointSourceArtifactStreamProvider(loaded);
            var counts = new Dictionary<string, int>();
            foreach (var node in nodes)
            {
                var context = factory.Create(configuration, node);
                _ = await replay.ValidateAsync(node.Name, context, node.Selector, TestContext.Current.CancellationToken);
                var count = 0;
                await foreach (var _ in replay.ReadAsync(node.Name, context, node.Selector, TestContext.Current.CancellationToken)) count++;
                counts[node.Name] = count;
            }

            Assert.Equal(1, counts["s3-notes-node"]);
            Assert.Equal(1, counts["sftp-notes-node"]);
            Assert.Equal(7, counts["azure-parquet-node"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Same_logical_bytes_have_the_same_hash_and_a_provider_neutral_artifact_identity_across_object_transports()
    {
        var text = Encoding.UTF8.GetBytes("identical logical payload");
        var deliveries = await DeliverAsync("payload.txt", text, "");
        var hashes = new List<string>();
        var identities = new List<string>();
        foreach (var delivery in deliveries.Where(item => item.Name != "filesystem"))
        {
            IRemoteObjectStoreFactory factory = delivery.Name switch { "s3" => new S3StoreFactory(), "azure-blob" => new AzureBlobStoreFactory(), _ => new SftpStoreFactory() };
            var connector = new RemoteObjectSourceConnector(delivery.Name, factory);
            var context = CheckpointHarness.Context(delivery.Name,
                delivery.Endpoint.Where(pair => !pair.Value.StartsWith("secret:", StringComparison.Ordinal)),
                delivery.Endpoint.Where(pair => pair.Value.StartsWith("secret:", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => delivery.Environment[pair.Value["secret:".Length..]]));
            await foreach (var record in connector.ReadAsync(context, new ArtifactSelector("object-pattern", [new("pattern", "*.txt")], ["relativePath"]), new ReadOptions(), TestContext.Current.CancellationToken))
            {
                hashes.Add(record.Provenance.SourceHash!);
                identities.Add(((StringValue)record.Values["relativePath"]).Value);
            }
        }

        Assert.Equal(3, hashes.Count);
        Assert.Single(hashes.Distinct(StringComparer.Ordinal));
        Assert.Equal(Sha(text), hashes[0]);
        Assert.Single(identities.Distinct(StringComparer.Ordinal));
    }
}
