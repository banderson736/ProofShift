using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.Sftp;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Connectors.RemoteStorage.Tests;

public sealed class SftpFixture : IAsyncLifetime
{
    // Test-only image (public Docker Hub), not redistributed. See docs/CONNECTOR_TEST_RUNTIMES.md.
    public const string Image = "atmoz/sftp:alpine";
    public const string User = "psreader";
    public const string Password = "Synthetic-Sftp-Password-2026";
    private IContainer? _container;
    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string Fingerprint { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(22, true)
            .WithCommand($"{User}:{Password}:1001:100:upload")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server listening on 0.0.0.0 port 22", options => options.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        try
        {
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("A Docker-compatible runtime is required for SFTP integration tests.");
        }

        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(22);
        var result = await _container.ExecAsync(["ssh-keygen", "-lf", "/etc/ssh/ssh_host_ed25519_key.pub"], TestContext.Current.CancellationToken);
        Fingerprint = result.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(part => part.StartsWith("SHA256:", StringComparison.Ordinal));
    }

    public async Task PutAsync(string relativePath, byte[] data)
    {
        var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(directory))
            await ShellAsync($"mkdir -p /home/{User}/upload/{directory} && chown -R 1001:100 /home/{User}/upload/{directory}");
        await _container!.CopyAsync(data, $"/home/{User}/upload/{relativePath}", 1001, 100, global::DotNet.Testcontainers.Configurations.UnixFileModes.UserRead | global::DotNet.Testcontainers.Configurations.UnixFileModes.UserWrite | global::DotNet.Testcontainers.Configurations.UnixFileModes.GroupRead | global::DotNet.Testcontainers.Configurations.UnixFileModes.OtherRead, TestContext.Current.CancellationToken);
    }

    public async Task ShellAsync(string command)
    {
        var result = await _container!.ExecAsync(["sh", "-c", command], TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

[Collection("RemoteStorage")]
public sealed class SftpIntegrationTests(SftpFixture sftp) : IClassFixture<SftpFixture>
{
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ArtifactSelector Selector(string pattern) => new("object-pattern", [new("pattern", pattern)], ["relativePath"]);

    private ConnectorContext Context(string? fingerprint = null, string password = SftpFixture.Password, string root = "/upload") => CheckpointHarness.Context("sftp",
        [new("host", sftp.Host), new("port", sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("username", SftpFixture.User),
            new("remoteRoot", root), new("hostKeyFingerprint", fingerprint ?? sftp.Fingerprint), new("authentication", "password")],
        new Dictionary<string, string> { ["password"] = password });

    private async Task<string> NewRootAsync()
    {
        var name = "run-" + Guid.NewGuid().ToString("N")[..12];
        await sftp.ShellAsync($"mkdir -p /home/{SftpFixture.User}/upload/{name} && chown 1001:100 /home/{SftpFixture.User}/upload/{name}");
        return name;
    }

    [Fact]
    public async Task Reads_nested_special_names_in_byte_order_with_exact_hashes_and_never_follows_symlinks()
    {
        var run = await NewRootAsync();
        var files = new Dictionary<string, byte[]>
        {
            [$"{run}/a/2.txt"] = "two"u8.ToArray(), [$"{run}/a/10.txt"] = "ten"u8.ToArray(), [$"{run}/with space.txt"] = "space"u8.ToArray(),
            [$"{run}/ünï.txt"] = "unicode"u8.ToArray(), [$"{run}/deep/er/x.txt"] = "deep"u8.ToArray(), [$"{run}/empty.txt"] = []
        };
        foreach (var pair in files) await sftp.PutAsync(pair.Key, pair.Value);
        await sftp.PutAsync($"outside-{run}.txt", "must never be visible"u8.ToArray());
        await sftp.ShellAsync($"cd /home/{SftpFixture.User}/upload/{run} && ln -s ../outside-{run}.txt escape.txt && ln -s .. updir && ln -s a linkeddir");

        var connector = new SftpSourceConnector();
        var context = Context(root: $"/upload/{run}");
        var records = new List<RecordEnvelope>();
        await foreach (var record in connector.ReadAsync(context, Selector("**/*.txt"), new ReadOptions(), TestContext.Current.CancellationToken)) records.Add(record);
        var keys = records.Select(record => ((StringValue)record.Values["relativePath"]).Value).ToArray();
        Assert.Equal(files.Keys.Select(key => key[(run.Length + 1)..]).OrderBy(key => key, Comparer<string>.Create(RemoteKeyRules.CompareUtf8)).ToArray(), keys);
        Assert.DoesNotContain(keys, key => key.Contains("escape", StringComparison.Ordinal) || key.StartsWith("updir", StringComparison.Ordinal) || key.StartsWith("linkeddir", StringComparison.Ordinal));
        foreach (var record in records)
            Assert.Equal(Sha(files[$"{run}/" + ((StringValue)record.Values["relativePath"]).Value]), record.Provenance.SourceHash);

        // Direct addressing of a link is also refused.
        await using var store = new SftpStoreFactory().Create(context);
        var refused = await Assert.ThrowsAsync<RemoteStoreException>(() => store.StatAsync("escape.txt", null, TestContext.Current.CancellationToken));
        Assert.True(refused.Kind is RemoteFailureKind.ScopeEscape or RemoteFailureKind.NotFound);
        var viaDirectory = await Assert.ThrowsAsync<RemoteStoreException>(() => store.StatAsync("linkeddir/2.txt", null, TestContext.Current.CancellationToken));
        Assert.Equal(RemoteFailureKind.ScopeEscape, viaDirectory.Kind);
    }

    [Fact]
    public async Task Many_small_objects_use_bounded_spills_and_keep_global_order_across_chunk_sizes()
    {
        var run = await NewRootAsync();
        var keys = Enumerable.Range(0, 48).Select(index => $"chunk-{index:D2}.dat")
            .Concat(["a-file.txt", "a.dir.txt", "a/child.txt", "aa.txt", "z.txt", ".leading.txt",
                "space name.txt", "#hash.txt", "percent%.txt", "plus+.txt", "ünï.txt"])
            .ToArray();
        var root = $"/home/{SftpFixture.User}/upload/{run}";
        var commands = new List<string> { $"mkdir -p '{root}/a'" };
        commands.AddRange(keys.Select(key => $"printf 'synthetic' > '{root}/{key}'"));
        await sftp.ShellAsync(string.Join("; ", commands));

        var scratchRoot = Path.Combine(Path.GetTempPath(), $"proofshift-sftp-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchRoot);
        SftpEnumerationMetrics? metrics = null;
        var options = new SftpEnumerationOptions(8, 512, 3, scratchRoot, value => metrics = value);
        var context = Context(root: $"/upload/{run}");
        var store = new SftpStoreFactory(options).Create(context);
        RemoteObjectInfo[] listed;
        await using (store)
        {
            var items = new List<RemoteObjectInfo>();
            await foreach (var item in store.ListAsync("", TestContext.Current.CancellationToken)) items.Add(item);
            listed = items.ToArray();
        }

        var expected = keys.OrderBy(key => key, Comparer<string>.Create(RemoteKeyRules.CompareUtf8)).ToArray();
        Assert.Equal(expected, listed.Select(item => item.Key));
        Assert.Equal(keys.Length, listed.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(metrics!.PeakBufferedEntries, 1, 8);
        Assert.InRange(metrics.PeakBufferedMetadataBytes, 1, 512);
        Assert.True(metrics.InitialRunCount > 1);
        Assert.True(metrics.GeneratedRunCount > metrics.InitialRunCount);
        Assert.InRange(metrics.PeakOpenRuns, 1, 3);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));

        var baselineDiscovery = await new RemoteObjectSourceConnector("sftp", new SftpStoreFactory(options))
            .DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        SftpEnumerationMetrics? alternateMetrics = null;
        var alternateOptions = new SftpEnumerationOptions(13, 1024, 3, scratchRoot, value => alternateMetrics = value);
        var discovery = new RemoteObjectSourceConnector("sftp", new SftpStoreFactory(alternateOptions));
        var discovered = await discovery.DiscoverAsync(context, [], TestContext.Current.CancellationToken);
        Assert.True(PhysicalDiscovery.Verify(discovered));
        Assert.Equal(baselineDiscovery.Fingerprint, discovered.Fingerprint);
        Assert.Equal(expected.Length, listed.Length);
        Assert.NotNull(alternateMetrics);
        Assert.InRange(alternateMetrics!.PeakBufferedEntries, 1, 13);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));
        Directory.Delete(scratchRoot);
    }

    [Fact]
    public async Task Wrong_host_key_and_wrong_password_fail_closed_with_distinct_redacted_diagnostics()
    {
        var connector = new SftpSourceConnector();
        var wrongKey = "SHA256:" + Convert.ToBase64String(SHA256.HashData("not-the-host-key"u8.ToArray())).TrimEnd('=');
        var mismatch = await connector.InspectAsync(Context(fingerprint: wrongKey), Selector("**/*"), TestContext.Current.CancellationToken);
        var hostIssue = Assert.Single(mismatch.Issues);
        Assert.Equal(ConnectorIssueCodes.RemoteHostKeyMismatch, hostIssue.Code);
        Assert.DoesNotContain(sftp.Fingerprint, hostIssue.Message, StringComparison.Ordinal);

        const string badPassword = "Synthetic-Wrong-Password-Value";
        var denied = await connector.InspectAsync(Context(password: badPassword), Selector("**/*"), TestContext.Current.CancellationToken);
        var issue = Assert.Single(denied.Issues);
        Assert.Equal(ConnectorIssueCodes.RemoteAuthenticationFailed, issue.Code);
        Assert.DoesNotContain(badPassword, issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkpoint_replays_offline_after_remote_mutation_and_detects_truthful_hash()
    {
        var run = await NewRootAsync();
        var original = new byte[1024 * 1024 + 3];
        new Random(21).NextBytes(original);
        await sftp.PutAsync($"{run}/ledger.bin", original);
        var settings = new Dictionary<string, string>
        {
            ["host"] = sftp.Host, ["port"] = sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["username"] = SftpFixture.User,
            ["remoteRoot"] = $"/upload/{run}", ["hostKeyFingerprint"] = sftp.Fingerprint, ["authentication"] = "password", ["password"] = "secret:PS_TEST_SFTP_PASSWORD"
        };
        var replay = await CheckpointHarness.CaptureAndReplayAsync(new SftpSourceConnector(), "sftp", settings,
            new Dictionary<string, string> { ["PS_TEST_SFTP_PASSWORD"] = SftpFixture.Password }, Selector("*.bin"), "File.Artifact",
            () => sftp.PutAsync($"{run}/ledger.bin", "overwritten"u8.ToArray()), TestContext.Current.CancellationToken);
        Assert.True(replay.Capture.Status == CheckpointStatus.Complete, "capture failure: " + replay.Capture.FailureCode);
        var record = Assert.Single(replay.Records);
        Assert.Equal(Sha(original), record.Provenance.SourceHash);
        Assert.Equal(original, replay.Binaries[record.Artifact.Identity]);
        Assert.Equal("sftp", record.Provenance.Metadata["transport"]);
        Assert.False(record.Provenance.Metadata.ContainsKey("providerVersionId"));
    }

    [Fact]
    public async Task Ranged_reads_return_exact_slices_and_read_only_observation_works()
    {
        var run = await NewRootAsync();
        var data = new byte[3 * 1024 * 1024];
        new Random(9).NextBytes(data);
        await sftp.PutAsync($"{run}/range.bin", data);
        var context = Context(root: $"/upload/{run}");
        await using var store = new SftpStoreFactory().Create(context);
        var info = await store.StatAsync("range.bin", null, TestContext.Current.CancellationToken);
        await using (var slice = await store.OpenReadAsync(info, 1_234_567, 500, TestContext.Current.CancellationToken))
        {
            var buffer = new byte[500];
            await slice.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            Assert.Equal(data.AsSpan(1_234_567, 500).ToArray(), buffer);
            Assert.Equal(0, await slice.ReadAsync(new byte[1], TestContext.Current.CancellationToken));
        }

        var observed = new List<RecordEnvelope>();
        await foreach (var record in new ReaderTargetObserver(new SftpSourceConnector()).ObserveAsync(new TargetObservationRequest(
            new TargetObservationContext(context, new RunId(Guid.NewGuid()), SystemRole.Target), Selector("*.bin"), new ReadOptions()), TestContext.Current.CancellationToken))
            observed.Add(record);
        Assert.Equal(Sha(data), Assert.Single(observed).Provenance.SourceHash);
        Assert.DoesNotContain(typeof(SftpObjectStore).GetMethods(), method =>
            method.Name.Contains("Upload", StringComparison.Ordinal) || method.Name.Contains("Delete", StringComparison.Ordinal) || method.Name.Contains("Write", StringComparison.Ordinal));
    }

    [Fact]
    public void Unpinned_host_keys_and_inline_secrets_are_refused()
    {
        var factory = new SftpStoreFactory();
        var missingPin = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("sftp",
            [new("host", "h"), new("username", "u"), new("remoteRoot", "/r"), new("hostKeyFingerprint", "trust-on-first-use"), new("authentication", "password")],
            new Dictionary<string, string> { ["password"] = "p" })));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, missingPin.Code);
        var inline = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("sftp",
            [new("host", "h"), new("username", "u"), new("remoteRoot", "/r"), new("hostKeyFingerprint", "SHA256:" + new string('A', 43)),
                new("authentication", "password"), new("password", "inline-password-value")], new Dictionary<string, string>())));
        Assert.Equal(ConnectorIssueCodes.InsecureRemoteConfiguration, inline.Code);
        Assert.DoesNotContain("inline-password-value", inline.Message, StringComparison.Ordinal);
        const string privateKeyMarker = "SYNTHETIC-PRIVATE-KEY-MATERIAL";
        const string passphraseMarker = "SYNTHETIC-PRIVATE-KEY-PASSPHRASE";
        var privateKeyFailure = Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("sftp",
            [new("host", "sftp.example.invalid"), new("username", "synthetic"), new("remoteRoot", "/root"),
                new("hostKeyFingerprint", "SHA256:" + new string('A', 43)), new("authentication", "private-key")],
            new Dictionary<string, string> { ["privateKey"] = privateKeyMarker, ["privateKeyPassphrase"] = passphraseMarker })));
        Assert.Equal(ConnectorIssueCodes.MissingConfiguration, privateKeyFailure.Code);
        Assert.DoesNotContain(privateKeyMarker, privateKeyFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(passphraseMarker, privateKeyFailure.Message, StringComparison.Ordinal);
        Assert.Throws<ConnectorConfigurationException>(() => factory.Create(CheckpointHarness.Context("sftp",
            [new("host", "h"), new("username", "u"), new("remoteRoot", "/r/../etc"), new("hostKeyFingerprint", "SHA256:" + new string('A', 43)), new("authentication", "password")],
            new Dictionary<string, string> { ["password"] = "p" })));
    }
}
