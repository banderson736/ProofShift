using System.Security.Cryptography;
using System.Text;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.AzureBlob;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Connectors.RemoteStorage.Tests;

public sealed class AzuriteFixture : IAsyncLifetime
{
    // Test-only emulator image, pinned by digest-resolved tag; not redistributed.
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.35.0";
    public const string Account = "proofshiftsynth";
    public static readonly string Key = Convert.ToBase64String(SHA256.HashData("proofshift-synthetic-azurite-key"u8.ToArray()));
    private IContainer? _container;
    public string ServiceUrl { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(10000, true)
            .WithEnvironment("AZURITE_ACCOUNTS", $"{Account}:{Key}")
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Azurite Blob service successfully listens", options => options.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        try
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for Azure Blob (Azurite) integration tests.");
        }

        ServiceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(10000)}/{Account}";
    }

    public BlobServiceClient Admin() => new(new Uri(ServiceUrl), new StorageSharedKeyCredential(Account, Key));

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[Collection("RemoteStorage")]
public sealed class AzureBlobAzuriteIntegrationTests(AzuriteFixture azurite) : IClassFixture<AzuriteFixture>
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ArtifactSelector Selector(string pattern) => new("object-pattern", [new("pattern", pattern)], ["relativePath"]);

    private ConnectorContext Context(string container, string prefix = "", int pageSize = 5000, string authentication = "account-key",
        string? key = null, string? sas = null)
    {
        var secrets = new Dictionary<string, string>();
        if (authentication == "account-key") secrets["accountKey"] = key ?? AzuriteFixture.Key;
        if (authentication == "sas") secrets["sasToken"] = sas!;
        return CheckpointHarness.Context("azure-blob",
            [new("account", AzuriteFixture.Account), new("serviceUrl", azurite.ServiceUrl), new("allowInsecureHttp", "true"),
                new("container", container), new("prefix", prefix), new("authentication", authentication),
                new("pageSize", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture))], secrets);
    }

    private async Task<BlobContainerClient> NewContainerAsync()
    {
        var container = azurite.Admin().GetBlobContainerClient("ps-" + Guid.NewGuid().ToString("N")[..16]);
        await container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        return container;
    }

    private static async Task PutAsync(BlobContainerClient container, string key, byte[] data) =>
        await container.GetBlobClient(key).UploadAsync(new MemoryStream(data), overwrite: true, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Pagination_special_names_prefix_boundaries_and_hashes_are_exact()
    {
        var container = await NewContainerAsync();
        var expected = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (var index = 0; index < 130; index++) expected[$"data/item-{index:D4}.txt"] = Encoding.UTF8.GetBytes($"payload {index}");
        expected["data/ünï côdé ✓.txt"] = "unicode"u8.ToArray();
        expected["data/with space.txt"] = "space"u8.ToArray();
        expected["data/a%2Fb+c.txt"] = "encoded"u8.ToArray();
        expected["data/empty.txt"] = [];
        expected["data/nested/deep/er.txt"] = "deep"u8.ToArray();
        foreach (var pair in expected) await PutAsync(container, pair.Key, pair.Value);
        await PutAsync(container, "data-other/decoy.txt", "outside"u8.ToArray());
        await PutAsync(container, "database.txt", "outside"u8.ToArray());

        var connector = new AzureBlobSourceConnector();
        var context = Context(container.Name, "data", pageSize: 50);
        var inspection = await connector.InspectAsync(context, Selector("**/*.txt"), TestContext.Current.CancellationToken);
        Assert.Equal(SourceInspectionStatus.Valid, inspection.Status);
        Assert.Equal(expected.Count, inspection.Files);

        var records = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadAsync(context, Selector("**/*.txt"), new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
        var ordered = expected.Keys.Select(key => key["data/".Length..]).OrderBy(key => key, Comparer<string>.Create(RemoteKeyRules.CompareUtf8)).ToArray();
        Assert.Equal(ordered, records.Select(record => ((StringValue)record.Values["relativePath"]).Value).ToArray());
        foreach (var record in records)
            Assert.Equal(Sha(expected["data/" + ((StringValue)record.Values["relativePath"]).Value]), record.Provenance.SourceHash);
        Assert.DoesNotContain(records, record => record.Provenance.Location.Contains("decoy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_key_and_missing_container_fail_closed_without_leaking_secrets()
    {
        var container = await NewContainerAsync();
        await PutAsync(container, "x.txt", "x"u8.ToArray());
        var connector = new AzureBlobSourceConnector();
        var wrongKey = Convert.ToBase64String(SHA256.HashData("wrong"u8.ToArray()));
        var bad = await connector.InspectAsync(Context(container.Name, key: wrongKey), Selector("*"), TestContext.Current.CancellationToken);
        var issue = Assert.Single(bad.Issues);
        // Azure reports signature failures as AuthenticationFailed; Azurite reports AuthorizationFailure. Both fail closed.
        Assert.Contains(issue.Code, new[] { ConnectorIssueCodes.RemoteAuthenticationFailed, ConnectorIssueCodes.RemoteAuthorizationFailed });
        Assert.DoesNotContain(wrongKey, issue.Message, StringComparison.Ordinal);
        const string badSas = "?sv=synthetic&sig=AZURE-SYNTHETIC-SAS-SECRET";
        var badSasResult = await connector.InspectAsync(Context(container.Name, authentication: "sas", sas: badSas),
            Selector("*"), TestContext.Current.CancellationToken);
        var sasIssue = Assert.Single(badSasResult.Issues);
        Assert.Contains(sasIssue.Code, new[] { ConnectorIssueCodes.RemoteAuthenticationFailed, ConnectorIssueCodes.RemoteAuthorizationFailed });
        Assert.DoesNotContain(badSas, sasIssue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AZURE-SYNTHETIC-SAS-SECRET", sasIssue.Message, StringComparison.Ordinal);
        var missing = await connector.InspectAsync(Context("ps-missing-container"), Selector("*"), TestContext.Current.CancellationToken);
        Assert.NotEqual(SourceInspectionStatus.Valid, missing.Status);
    }

    [Fact]
    public async Task Sas_scoped_to_a_container_reads_and_checkpoint_replays_offline_after_remote_mutation()
    {
        var container = await NewContainerAsync();
        var original = new byte[3 * 1024 * 1024 + 5];
        new Random(11).NextBytes(original);
        await PutAsync(container, "ledger/original.bin", original);
        var sas = container.GenerateSasUri(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List, DateTimeOffset.UtcNow.AddHours(1)).Query;

        var connector = new AzureBlobSourceConnector();
        var settings = new Dictionary<string, string>
        {
            ["account"] = AzuriteFixture.Account, ["serviceUrl"] = azurite.ServiceUrl, ["allowInsecureHttp"] = "true",
            ["container"] = container.Name, ["prefix"] = "ledger", ["authentication"] = "sas", ["sasToken"] = "secret:PS_TEST_AZ_SAS"
        };
        var replay = await CheckpointHarness.CaptureAndReplayAsync(connector, "azure-blob", settings,
            new Dictionary<string, string> { ["PS_TEST_AZ_SAS"] = sas }, Selector("**/*.bin"), "File.Artifact",
            async () => await PutAsync(container, "ledger/original.bin", "overwritten"u8.ToArray()), TestContext.Current.CancellationToken);

        Assert.Equal(CheckpointStatus.Complete, replay.Capture.Status);
        Assert.False(replay.Capture.Checkpoint!.CrossSystemAtomic);
        var record = Assert.Single(replay.Records);
        Assert.Equal(Sha(original), record.Provenance.SourceHash);
        Assert.Equal(original, replay.Binaries[((StringValue)record.Values["relativePath"]).Value]);
        Assert.True(record.Provenance.Metadata.ContainsKey("providerETag"));
    }

    [Fact]
    public async Task Read_only_sas_cannot_be_used_to_write_and_connector_exposes_no_write_path()
    {
        var container = await NewContainerAsync();
        var readOnlySas = container.GenerateSasUri(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List, DateTimeOffset.UtcNow.AddHours(1));
        var denied = await Assert.ThrowsAnyAsync<Azure.RequestFailedException>(async () =>
            await new BlobContainerClient(readOnlySas).GetBlobClient("write-attempt.txt")
                .UploadAsync(new MemoryStream("x"u8.ToArray()), overwrite: true, TestContext.Current.CancellationToken));
        Assert.True(denied.Status is 403 or 401);
        Assert.False(new AzureBlobSourceConnector().Capabilities.ShadowWrite);
        Assert.DoesNotContain(typeof(AzureBlobObjectStore).GetMethods(), method =>
            method.Name.Contains("Upload", StringComparison.Ordinal) || method.Name.Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ranged_reads_and_large_object_streaming_match_exact_bytes_and_hash()
    {
        var container = await NewContainerAsync();
        var large = new byte[20 * 1024 * 1024];
        new Random(3).NextBytes(large);
        await PutAsync(container, "big/large.bin", large);
        var context = Context(container.Name, "big");
        await using var store = new AzureBlobStoreFactory().Create(context);
        var info = await store.StatAsync("large.bin", null, TestContext.Current.CancellationToken);
        Assert.Equal(large.Length, info.Length);
        await using (var slice = await store.OpenReadAsync(info, 9_000_001, 4096, TestContext.Current.CancellationToken))
        {
            var buffer = new byte[4096];
            await slice.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(large.AsSpan(9_000_001, 4096).ToArray(), buffer);
        }

        await using var seekable = new SeekableRemoteStream(store, info, 256 * 1024);
        seekable.Seek(-500, SeekOrigin.End);
        var tail = new byte[500];
        await seekable.ReadExactlyAsync(tail, TestContext.Current.CancellationToken);
        Assert.Equal(large.AsSpan(large.Length - 500).ToArray(), tail);

        var records = new List<RecordEnvelope>();
        await foreach (var record in new AzureBlobSourceConnector().ReadAsync(context, Selector("*.bin"), new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
        Assert.Equal(Sha(large), Assert.Single(records).Provenance.SourceHash);
    }

    [Fact]
    public async Task Blob_versions_are_pinned_when_the_service_reports_them_otherwise_etag_conditions_detect_change()
    {
        var container = await NewContainerAsync();
        await PutAsync(container, "v/doc.txt", "version-one"u8.ToArray());
        var connector = new AzureBlobSourceConnector();
        var context = Context(container.Name, "v");
        RecordEnvelope? record = null;
        await foreach (var item in connector.ReadAsync(context, Selector("*.txt"), new ReadOptions(), TestContext.Current.CancellationToken)) record = item;
        Assert.NotNull(record);
        var versioned = record.Provenance.Metadata.ContainsKey("providerVersionId");
        await PutAsync(container, "v/doc.txt", "version-two-longer"u8.ToArray());
        var binary = (BinaryReferenceValue)record.Values["content"];
        if (versioned)
        {
            await using var stream = await connector.OpenBinaryReadAsync(context, Selector("*.txt"), record.Artifact, binary, TestContext.Current.CancellationToken);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal("version-one", Encoding.UTF8.GetString(copy.ToArray()));
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ConnectorReadException>(async () =>
                await connector.OpenBinaryReadAsync(context, Selector("*.txt"), record.Artifact, binary, TestContext.Current.CancellationToken));
            Assert.Equal(ConnectorIssueCodes.ArtifactChangedDuringCapture, exception.Code);
        }
    }

    [Fact]
    public async Task Azure_blobs_can_be_observed_as_a_read_only_target_with_provenance()
    {
        var container = await NewContainerAsync();
        await PutAsync(container, "target/out.csv", "id,amount\n1,10.00\n"u8.ToArray());
        var records = new List<RecordEnvelope>();
        await foreach (var record in new ReaderTargetObserver(new AzureBlobSourceConnector()).ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(Context(container.Name, "target"), new RunId(Guid.NewGuid()), SystemRole.Target),
            Selector("*.csv"), new ReadOptions()), TestContext.Current.CancellationToken)) records.Add(record);
        var observed = Assert.Single(records);
        Assert.Equal("out.csv", ((StringValue)observed.Values["relativePath"]).Value);
        Assert.Equal("azure-blob", observed.Provenance.Metadata["transport"]);
    }

    [Fact]
    public void Insecure_configuration_and_inline_credentials_are_rejected()
    {
        var factory = new AzureBlobStoreFactory();
        var plain = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("azure-blob",
            [new("container", "c"), new("serviceUrl", "http://example.invalid"), new("authentication", "account-key"), new("account", "a")],
            new Dictionary<string, string> { ["accountKey"] = "k" })));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, plain.Code);
        var inline = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("azure-blob",
            [new("container", "c"), new("serviceUrl", "https://example.invalid"), new("authentication", "account-key"), new("account", "a"),
                new("accountKey", "inline-secret-value")], new Dictionary<string, string>())));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, inline.Code);
        Assert.DoesNotContain("inline-secret-value", inline.Message, StringComparison.Ordinal);
    }
}
