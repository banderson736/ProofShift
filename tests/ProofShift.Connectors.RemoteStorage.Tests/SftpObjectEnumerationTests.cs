using System.Runtime.CompilerServices;
using ProofShift.Connectors.Abstractions;
using ProofShift.Connectors.RemoteObjects;
using ProofShift.Connectors.Sftp;

namespace ProofShift.Connectors.RemoteStorage.Tests;

public sealed class SftpObjectEnumerationTests
{
    [Fact]
    public async Task Shuffled_inputs_and_chunk_sizes_produce_identical_utf8_order_and_discovery_fingerprint()
    {
        var keys = new[] { "a-file", "a.dir", "a/child", "aa", "z", "space name", "#hash", "percent%", "plus+", ".leading", "ünï" }
            .Concat(Enumerable.Range(0, 61).Select(index => $"chunk-{index:D3}"))
            .Select(key => new RemoteObjectInfo(key, key.Length, DateTimeOffset.UnixEpoch.AddSeconds(key.Length)))
            .ToArray();
        var forward = await SortAsync(keys, maximumEntries: 7, "chunk-seven");
        var reverse = await SortAsync(keys.Reverse(), maximumEntries: 13, "chunk-thirteen");
        var expected = keys.Select(item => item.Key).OrderBy(key => key,
            Comparer<string>.Create(RemoteKeyRules.CompareUtf8)).ToArray();

        Assert.Equal(expected, forward.Items.Select(item => item.Key));
        Assert.Equal(expected, reverse.Items.Select(item => item.Key));
        Assert.Equal(forward.Items.Select(item => item.CanonicalState), reverse.Items.Select(item => item.CanonicalState));
        Assert.Equal(DiscoveryFingerprint(forward.Items), DiscoveryFingerprint(reverse.Items));
        Assert.Equal(expected.Length, forward.Items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(forward.Metrics!.PeakBufferedEntries, 1, 7);
        Assert.InRange(forward.Metrics.PeakBufferedMetadataBytes, 1, 512);
        Assert.InRange(reverse.Metrics!.PeakBufferedEntries, 1, 13);
        Assert.InRange(reverse.Metrics.PeakBufferedMetadataBytes, 1, 512);
        Assert.True(forward.Metrics.InitialRunCount > 1);
        Assert.True(forward.Metrics.GeneratedRunCount > forward.Metrics.InitialRunCount);
        Assert.InRange(forward.Metrics.PeakOpenRuns, 1, 3);
        Assert.Empty(Directory.EnumerateFileSystemEntries(forward.ScratchRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(reverse.ScratchRoot));
        Directory.Delete(forward.ScratchRoot);
        Directory.Delete(reverse.ScratchRoot);
    }

    [Fact]
    public async Task Duplicate_keys_fail_closed_and_spill_runs_are_removed()
    {
        var duplicate = new[] { new RemoteObjectInfo("same", 1), new RemoteObjectInfo("same", 2) };
        var scratch = CreateScratchRoot("duplicate");
        var options = Options(maximumEntries: 1, scratch, _ => { });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in SftpObjectEnumeration.SortAsync(Items(duplicate), options, CancellationToken.None)) { }
        });

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Directory.Delete(scratch);
    }

    [Fact]
    public async Task Cancellation_after_spill_removes_all_sorted_runs()
    {
        var scratch = CreateScratchRoot("cancel");
        using var cancellation = new CancellationTokenSource();
        var options = Options(maximumEntries: 2, scratch, _ => { });
        var source = CancelAfterSpill(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SftpObjectEnumeration.SortAsync(source, options, cancellation.Token)) { }
        });

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
        Directory.Delete(scratch);
    }

    private static async Task<(RemoteObjectInfo[] Items, SftpEnumerationMetrics? Metrics, string ScratchRoot)> SortAsync(
        IEnumerable<RemoteObjectInfo> input, int maximumEntries, string name)
    {
        var scratch = CreateScratchRoot(name);
        SftpEnumerationMetrics? metrics = null;
        var options = Options(maximumEntries, scratch, value => metrics = value);
        var result = new List<RemoteObjectInfo>();
        await foreach (var item in SftpObjectEnumeration.SortAsync(Items(input), options, CancellationToken.None))
            result.Add(item);
        return (result.ToArray(), metrics, scratch);
    }

    private static SftpEnumerationOptions Options(int maximumEntries, string scratch,
        Action<SftpEnumerationMetrics> metrics) => new(maximumEntries, 512, 3, scratch, metrics);

    private static string CreateScratchRoot(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proofshift-sftp-enumeration-test-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DiscoveryFingerprint(IReadOnlyCollection<RemoteObjectInfo> items)
    {
        var context = CheckpointHarness.Context("sftp", [], new Dictionary<string, string>());
        return PhysicalDiscovery.Create(context, "sftp", "0.10.0", items.Select(item =>
            new PhysicalObject("", item.Key, "file", [], [], [], SelectorProperties: new Dictionary<string, string>
            {
                ["length"] = item.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lastModified"] = item.LastModified?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""
            })), DateTimeOffset.UnixEpoch).Fingerprint;
    }

    private static async IAsyncEnumerable<RemoteObjectInfo> Items(IEnumerable<RemoteObjectInfo> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<RemoteObjectInfo> CancelAfterSpill(CancellationTokenSource cancellation)
    {
        for (var index = 0; index < 10; index++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            yield return new RemoteObjectInfo($"cancel-{index:D2}", index);
            if (index == 3) cancellation.Cancel();
            await Task.Yield();
        }
    }
}