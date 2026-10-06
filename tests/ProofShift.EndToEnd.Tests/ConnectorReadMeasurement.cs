using System.Diagnostics;
using System.Text.Json;

namespace ProofShift.EndToEnd.Tests;

internal sealed record ConnectorReadMeasurement(
    string Connector,
    long FixtureBytes,
    long PayloadBytesEstimate,
    long RecordCount,
    double ElapsedSeconds,
    double RecordsPerSecond,
    long StartingRssBytes,
    long EndingRssBytes,
    long PeakRssBytes,
    long StartingManagedHeapBytes,
    long EndingManagedHeapBytes,
    double? ContainerStartupSeconds,
    double? FixtureProvisioningSeconds)
{
    public static async Task<ConnectorReadMeasurement> MeasureAsync(
        string connector,
        long fixtureBytes,
        long payloadBytesEstimate,
        Func<CancellationToken, Task<long>> read,
        double? containerStartupSeconds,
        double? fixtureProvisioningSeconds,
        CancellationToken cancellationToken)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var startingRss = process.WorkingSet64;
        var peakRss = startingRss;
        var peakLock = new object();
        var startingHeap = GC.GetTotalMemory(forceFullCollection: false);
        using var samplingCancellation = new CancellationTokenSource();
        var sampler = SamplePeakRssAsync(process, sample =>
        {
            lock (peakLock) peakRss = Math.Max(peakRss, sample);
        }, samplingCancellation.Token);

        var stopwatch = Stopwatch.StartNew();
        var recordCount = await read(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        process.Refresh();
        var endingRss = process.WorkingSet64;
        var endingHeap = GC.GetTotalMemory(forceFullCollection: false);
        lock (peakLock) peakRss = Math.Max(peakRss, endingRss);
        samplingCancellation.Cancel();
        await sampler.ConfigureAwait(false);

        var measurement = new ConnectorReadMeasurement(connector, fixtureBytes, payloadBytesEstimate, recordCount,
            stopwatch.Elapsed.TotalSeconds, recordCount / stopwatch.Elapsed.TotalSeconds, startingRss, endingRss,
            peakRss, startingHeap, endingHeap, containerStartupSeconds, fixtureProvisioningSeconds);
        var reportPath = Path.Combine(Path.GetTempPath(), $"ProofShift-PS010C-reader-measurements-{Environment.ProcessId}.jsonl");
        await File.AppendAllTextAsync(reportPath, JsonSerializer.Serialize(measurement) + Environment.NewLine, cancellationToken)
            .ConfigureAwait(false);
        return measurement;
    }

    private static async Task SamplePeakRssAsync(Process process, Action<long> sample, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                process.Refresh();
                sample(process.WorkingSet64);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
