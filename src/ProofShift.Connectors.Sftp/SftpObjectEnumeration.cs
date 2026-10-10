using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ProofShift.Connectors.RemoteObjects;

namespace ProofShift.Connectors.Sftp;

internal sealed record SftpEnumerationMetrics(int PeakBufferedEntries, long PeakBufferedMetadataBytes,
    int InitialRunCount, int GeneratedRunCount, int PeakOpenRuns);

internal sealed record SftpEnumerationOptions(int MaximumBufferedEntries, long MaximumBufferedMetadataBytes,
    int MergeFanIn, string ScratchRoot, Action<SftpEnumerationMetrics>? MetricsCallback = null)
{
    public static SftpEnumerationOptions Default { get; } = new(512, 4 * 1024 * 1024, 16, Path.GetTempPath());

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBufferedEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBufferedMetadataBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MergeFanIn, 2);
        ArgumentException.ThrowIfNullOrWhiteSpace(ScratchRoot);
    }
}

internal static class SftpObjectEnumeration
{
    private static readonly JsonSerializerOptions SpillJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async IAsyncEnumerable<RemoteObjectInfo> SortAsync(IAsyncEnumerable<RemoteObjectInfo> source,
        SftpEnumerationOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string? scratchDirectory = null;
        var runs = new List<string>();
        var chunk = new List<RemoteObjectInfo>(Math.Min(options.MaximumBufferedEntries, 4096));
        long bufferedBytes = 0;
        long peakBufferedBytes = 0;
        var peakBufferedEntries = 0;
        var generatedRunCount = 0;
        var peakOpenRuns = 0;
        var initialRunCount = 0;

        async Task<string> SpillChunkAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            scratchDirectory ??= CreateScratchDirectory(options.ScratchRoot);
            chunk.Sort((left, right) => RemoteKeyRules.CompareUtf8(left.Key, right.Key));
            var path = Path.Combine(scratchDirectory, $"run-{generatedRunCount:D8}.jsonl");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                RestrictFile(path);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: false);
                string? previous = null;
                foreach (var item in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureStrictlyIncreasing(previous, item.Key);
                    var json = JsonSerializer.Serialize(item, SpillJsonOptions);
                    await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    previous = item.Key;
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            generatedRunCount++;
            runs.Add(path);
            chunk.Clear();
            bufferedBytes = 0;
            return path;
        }

        try
        {
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemBytes = EstimateMetadataBytes(item);
                if (chunk.Count > 0 && (chunk.Count >= options.MaximumBufferedEntries ||
                    bufferedBytes > options.MaximumBufferedMetadataBytes - Math.Min(itemBytes, options.MaximumBufferedMetadataBytes)))
                    await SpillChunkAsync().ConfigureAwait(false);

                chunk.Add(item);
                bufferedBytes = checked(bufferedBytes + itemBytes);
                peakBufferedEntries = Math.Max(peakBufferedEntries, chunk.Count);
                peakBufferedBytes = Math.Max(peakBufferedBytes, bufferedBytes);
                if (chunk.Count >= options.MaximumBufferedEntries || bufferedBytes >= options.MaximumBufferedMetadataBytes)
                    await SpillChunkAsync().ConfigureAwait(false);
            }

            if (runs.Count == 0)
            {
                chunk.Sort((left, right) => RemoteKeyRules.CompareUtf8(left.Key, right.Key));
                string? previous = null;
                foreach (var item in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureStrictlyIncreasing(previous, item.Key);
                    yield return item;
                    previous = item.Key;
                }

                yield break;
            }

            if (chunk.Count > 0)
                await SpillChunkAsync().ConfigureAwait(false);
            initialRunCount = runs.Count;

            while (runs.Count > options.MergeFanIn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextPass = new List<string>((runs.Count + options.MergeFanIn - 1) / options.MergeFanIn);
                for (var offset = 0; offset < runs.Count; offset += options.MergeFanIn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = runs.Skip(offset).Take(options.MergeFanIn).ToArray();
                    if (batch.Length == 1)
                    {
                        nextPass.Add(batch[0]);
                        continue;
                    }

                    peakOpenRuns = Math.Max(peakOpenRuns, batch.Length);
                    var mergedPath = Path.Combine(scratchDirectory!, $"run-{generatedRunCount:D8}.jsonl");
                    await MergeToRunAsync(batch, mergedPath, cancellationToken).ConfigureAwait(false);
                    generatedRunCount++;
                    nextPass.Add(mergedPath);
                    foreach (var path in batch) File.Delete(path);
                }

                runs = nextPass;
            }

            peakOpenRuns = Math.Max(peakOpenRuns, runs.Count);
            await foreach (var item in MergeRunsAsync(runs, cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (scratchDirectory is not null && Directory.Exists(scratchDirectory))
                Directory.Delete(scratchDirectory, recursive: true);

            options.MetricsCallback?.Invoke(new SftpEnumerationMetrics(peakBufferedEntries, peakBufferedBytes,
                initialRunCount, generatedRunCount, peakOpenRuns));
        }
    }

    private static async Task MergeToRunAsync(IReadOnlyList<string> runs, string destination,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RestrictFile(destination);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: false);
        await foreach (var item in MergeRunsAsync(runs, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(item, SpillJsonOptions);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<RemoteObjectInfo> MergeRunsAsync(IReadOnlyList<string> runs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readers = new List<StreamReader>(runs.Count);
        var queue = new PriorityQueue<RunHead, string>(Utf8StringComparer.Instance);
        try
        {
            for (var index = 0; index < runs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stream = new FileStream(runs[index], FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 64 * 1024, leaveOpen: false);
                readers.Add(reader);
                var first = await ReadNextAsync(reader, cancellationToken).ConfigureAwait(false);
                if (first is not null) queue.Enqueue(new RunHead(index, reader, first), first.Key);
            }

            string? previous = null;
            while (queue.TryDequeue(out var head, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureStrictlyIncreasing(previous, head.Item.Key);
                yield return head.Item;
                previous = head.Item.Key;
                var next = await ReadNextAsync(head.Reader, cancellationToken).ConfigureAwait(false);
                if (next is not null) queue.Enqueue(head with { Item = next }, next.Key);
            }
        }
        finally
        {
            foreach (var reader in readers)
                reader.Dispose();
        }
    }

    private static async Task<RemoteObjectInfo?> ReadNextAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null) return null;
        return JsonSerializer.Deserialize<RemoteObjectInfo>(line, SpillJsonOptions)
            ?? throw new InvalidDataException("SFTP enumeration spill run contains an invalid object descriptor.");
    }

    private static int EstimateMetadataBytes(RemoteObjectInfo item) => checked(
        Encoding.UTF8.GetByteCount(item.Key) + Encoding.UTF8.GetByteCount(item.ETag ?? "") +
        Encoding.UTF8.GetByteCount(item.VersionId ?? "") + 64);

    private static string CreateScratchDirectory(string parent)
    {
        var root = Path.GetFullPath(parent);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "proofshift-sftp-enumeration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void EnsureStrictlyIncreasing(string? previous, string current)
    {
        if (previous is not null && RemoteKeyRules.CompareUtf8(previous, current) >= 0)
            throw new InvalidDataException("SFTP enumeration contains duplicate object keys.");
    }

    private sealed record RunHead(int RunIndex, StreamReader Reader, RemoteObjectInfo Item);

    private sealed class Utf8StringComparer : IComparer<string>
    {
        public static Utf8StringComparer Instance { get; } = new();
        public int Compare(string? left, string? right) =>
            left is null ? right is null ? 0 : -1 : right is null ? 1 : RemoteKeyRules.CompareUtf8(left, right);
    }
}