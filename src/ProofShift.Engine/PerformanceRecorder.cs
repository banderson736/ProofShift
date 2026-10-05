using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class PerformanceRecorder
{
    private readonly ConcurrentQueue<PerformanceStage> _stages = new();
    private readonly ConcurrentDictionary<long, PerformanceStageScope> _activeStages = new();
    private long _stageSequence;
    private readonly string _scenario;
    private readonly DateTimeOffset _startedAt;
    private readonly int[] _gcCounts = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _maximumObservedWorkingSetBytes;
    private long _temporaryWorkspacePeakBytes;
    private int _completed;

    public PerformanceRecorder(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        _scenario = scenario.Trim();
        _startedAt = DateTimeOffset.UtcNow;
        SampleWorkingSet();
    }

    public PerformanceStageScope StartStage(PerformanceStageKind kind, string name,
        string? connectorId = null, string? nodeKey = null, string? edgeName = null, string? ruleId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        return new PerformanceStageScope(this, kind, name, connectorId, nodeKey, edgeName, ruleId);
    }

    public void RecordMeasuredStage(PerformanceStageKind kind, string name, long elapsedMicroseconds,
        long artifactCount = 0, long byteCount = 0, IEnumerable<PerformanceMeasurement>? measurements = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactCount);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        _stages.Enqueue(new PerformanceStage(kind, name.Trim(), elapsedMicroseconds, artifactCount, byteCount,
            null, null, null, null, (measurements ?? []).ToArray()));
        SampleWorkingSet();
    }

    public PerformanceRun Complete(long temporaryWorkspacePeakBytes = 0)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            throw new InvalidOperationException("Performance run has already been completed.");
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryWorkspacePeakBytes);
        SampleWorkingSet();
        var completedAt = DateTimeOffset.UtcNow;
        return new PerformanceRun(PerformanceRun.FormatVersion, Guid.NewGuid().ToString("D"), _scenario,
            _startedAt, completedAt, _stages.ToArray(),
            Interlocked.Read(ref _maximumObservedWorkingSetBytes), GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0) - _gcCounts[0], GC.CollectionCount(1) - _gcCounts[1],
            GC.CollectionCount(2) - _gcCounts[2], Math.Max(temporaryWorkspacePeakBytes,
                Interlocked.Read(ref _temporaryWorkspacePeakBytes)));
    }

    public void ObserveTemporaryWorkspaceBytes(long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        while (true)
        {
            var current = Interlocked.Read(ref _temporaryWorkspacePeakBytes);
            if (byteCount <= current || Interlocked.CompareExchange(ref _temporaryWorkspacePeakBytes, byteCount, current) == current)
                return;
        }
    }

    public IAsyncDisposable StartSampling(string outputDirectory, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval), "Sampling interval must be at least one second.");
        return new WorkloadSampler(this, Path.GetFullPath(outputDirectory), interval);
    }

    private sealed class WorkloadSampler : IAsyncDisposable
    {
        private readonly PerformanceRecorder _owner;
        private readonly string _directory;
        private readonly StreamWriter _writer;
        private readonly Timer _timer;
        private readonly object _gate = new();
        private Exception? _failure;
        private int _disposed;

        public WorkloadSampler(PerformanceRecorder owner, string directory, TimeSpan interval)
        {
            _owner = owner;
            _directory = directory;
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(new FileStream(Path.Combine(directory, "workload-samples.ndjson"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Capture();
            _timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_failure is not null) return;
                    try { Capture(); }
                    catch (Exception exception) { _failure = exception; }
                }
            }, null, interval, interval);
        }

        private void Capture()
        {
            using var process = Process.GetCurrentProcess();
            _owner.SampleWorkingSet();
            long workspaceBytes = 0, ledgerBytes = 0, walBytes = 0, evidenceBytes = 0, otherScratchBytes = 0;
            var roots = Directory.EnumerateDirectories(_directory, "working-*")
                .Append(Path.Combine(_directory, ".proofshift"));
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var file = new FileInfo(path);
                        if (!file.Exists) continue;
                        if (file.Name.EndsWith("-wal", StringComparison.Ordinal)) walBytes += file.Length;
                        else if (file.Name == "working-set.sqlite") workspaceBytes += file.Length;
                        else if (file.Name == "ledger.sqlite") ledgerBytes += file.Length;
                        else if (file.Name == "evidence.ndjson") evidenceBytes += file.Length;
                        else otherScratchBytes += file.Length;
                    }
                }
                catch (DirectoryNotFoundException) { }
                catch (FileNotFoundException) { }
            }
            var sample = new
            {
                timestamp = DateTimeOffset.UtcNow,
                processId = process.Id,
                workingSetBytes = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                generationZeroCollections = GC.CollectionCount(0),
                generationOneCollections = GC.CollectionCount(1),
                generationTwoCollections = GC.CollectionCount(2),
                verificationWorkspaceBytes = workspaceBytes,
                verificationLedgerBytes = ledgerBytes,
                sqliteWalBytes = walBytes,
                evidenceBytes,
                otherScratchBytes,
                activeStages = _owner._activeStages.Values.Select(stage => new
                {
                    name = stage.Name,
                    nodeKey = stage.NodeKey,
                    artifacts = stage.ArtifactCount,
                    elapsedSeconds = stage.ElapsedSeconds
                }).ToArray()
            };
            _writer.WriteLine(JsonSerializer.Serialize(sample));
            _writer.Flush();
            Console.WriteLine($"PERFORMANCE_PROGRESS pid={process.Id} rss={sample.workingSetBytes} heap={sample.managedHeapBytes} workspace={workspaceBytes} ledger={ledgerBytes} stages={string.Join(',', sample.activeStages.Select(stage => stage.name))}");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _timer.DisposeAsync().ConfigureAwait(false);
            if (_failure is null && Directory.Exists(_directory)) Capture();
            await _writer.DisposeAsync().ConfigureAwait(false);
            if (_failure is not null)
                throw new IOException("Workload performance sampling failed.", _failure);
        }
    }

    internal void CompleteStage(PerformanceStageKind kind, string name, long startedTimestamp,
        long artifactCount, long byteCount, string? connectorId, string? nodeKey, string? edgeName,
        string? ruleId, IReadOnlyCollection<PerformanceMeasurement> measurements)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        _stages.Enqueue(new PerformanceStage(kind, name, checked((long)Math.Round(elapsed.TotalMicroseconds)),
            artifactCount, byteCount, connectorId, nodeKey, edgeName, ruleId, measurements.ToArray()));
        SampleWorkingSet();
    }

    private void SampleWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        while (true)
        {
            var current = Interlocked.Read(ref _maximumObservedWorkingSetBytes);
            if (workingSet <= current || Interlocked.CompareExchange(ref _maximumObservedWorkingSetBytes, workingSet, current) == current)
                return;
        }
    }

    public sealed class PerformanceStageScope : IDisposable
    {
        private readonly PerformanceRecorder _owner;
        private readonly PerformanceStageKind _kind;
        private readonly string _name;
        private readonly string? _connectorId;
        private readonly string? _nodeKey;
        private readonly string? _edgeName;
        private readonly string? _ruleId;
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<PerformanceMeasurement> _measurements = new();
        private long _artifactCount;
        private long _byteCount;
        private int _completed;
        private readonly long _sequence;

        internal string Name => _name;
        internal string? NodeKey => _nodeKey;
        internal long ArtifactCount => Interlocked.Read(ref _artifactCount);
        internal double ElapsedSeconds => Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;

        internal PerformanceStageScope(PerformanceRecorder owner, PerformanceStageKind kind, string name,
            string? connectorId, string? nodeKey, string? edgeName, string? ruleId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _owner = owner;
            _kind = kind;
            _name = name.Trim();
            _connectorId = connectorId;
            _nodeKey = nodeKey;
            _edgeName = edgeName;
            _ruleId = ruleId;
            _sequence = Interlocked.Increment(ref owner._stageSequence);
            owner._activeStages.TryAdd(_sequence, this);
        }

        public void AddArtifacts(long count = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Interlocked.Add(ref _artifactCount, count);
        }

        public void AddBytes(long count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Interlocked.Add(ref _byteCount, count);
        }

        public void AddMeasurement(string name, double value, string unit)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(unit);
            _measurements.Enqueue(new PerformanceMeasurement(name.Trim(), value, unit.Trim()));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _owner._activeStages.TryRemove(_sequence, out _);
            _owner.CompleteStage(_kind, _name, _startedTimestamp, Interlocked.Read(ref _artifactCount),
                Interlocked.Read(ref _byteCount), _connectorId, _nodeKey, _edgeName, _ruleId, _measurements.ToArray());
        }
    }
}