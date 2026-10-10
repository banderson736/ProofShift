using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class PerformanceRecorder
{
    private readonly ConcurrentQueue<PerformanceStage> _stages = new();
    private readonly ConcurrentDictionary<long, PerformanceStageScope> _activeStages = new();
    private readonly AsyncLocal<PerformanceStageScope?> _currentStage = new();
    private readonly long _runStartedTimestamp;
    private readonly long _processingRootSequence;
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
        _runStartedTimestamp = Stopwatch.GetTimestamp();
        _processingRootSequence = Interlocked.Increment(ref _stageSequence);
        SampleWorkingSet();
    }

    public PerformanceStageScope StartStage(PerformanceStageKind kind, string name,
        string? connectorId = null, string? nodeKey = null, string? edgeName = null, string? ruleId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        var parent = _currentStage.Value;
        var scope = new PerformanceStageScope(this, kind, name, connectorId, nodeKey, edgeName, ruleId, parent);
        _currentStage.Value = scope;
        return scope;
    }

    public void RecordMeasuredStage(PerformanceStageKind kind, string name, long elapsedMicroseconds,
        long artifactCount = 0, long byteCount = 0, IEnumerable<PerformanceMeasurement>? measurements = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(artifactCount);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var stageMeasurements = (measurements ?? []).ToList();
        if (!stageMeasurements.Any(measurement => measurement.Name == "stageSequence"))
            stageMeasurements.Add(new PerformanceMeasurement("stageSequence",
                Interlocked.Increment(ref _stageSequence), "id"));
        var parentSequence = _currentStage.Value?.Sequence ??
            (kind == PerformanceStageKind.Fixture ? null : _processingRootSequence);
        if (parentSequence is { } parent &&
            !stageMeasurements.Any(measurement => measurement.Name == "parentStageSequence"))
            stageMeasurements.Add(new PerformanceMeasurement("parentStageSequence", parent, "id"));
        _stages.Enqueue(new PerformanceStage(kind, name.Trim(), elapsedMicroseconds, artifactCount, byteCount,
            null, null, null, null, stageMeasurements));
        SampleWorkingSet();
    }

    public PerformanceRun Complete(long temporaryWorkspacePeakBytes = 0)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            throw new InvalidOperationException("Performance run has already been completed.");
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryWorkspacePeakBytes);
        SampleWorkingSet();
        var completedAt = DateTimeOffset.UtcNow;
        var stages = _stages.ToArray();
        var elapsedMicroseconds = Math.Max(0, (completedAt - _startedAt).Ticks / 10);
        var fixtureMicroseconds = stages.Where(stage => stage.Kind == PerformanceStageKind.Fixture)
            .Sum(stage => stage.ElapsedMicroseconds);
        var processingMicroseconds = Math.Max(0, elapsedMicroseconds - fixtureMicroseconds);
        var processingStages = CreateProcessingStages(stages, processingMicroseconds,
            Stopwatch.GetElapsedTime(_runStartedTimestamp).TotalMicroseconds);
        return new PerformanceRun(PerformanceRun.FormatVersion, Guid.NewGuid().ToString("D"), _scenario,
            _startedAt, completedAt, processingStages,
            Interlocked.Read(ref _maximumObservedWorkingSetBytes), GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0) - _gcCounts[0], GC.CollectionCount(1) - _gcCounts[1],
            GC.CollectionCount(2) - _gcCounts[2], Math.Max(temporaryWorkspacePeakBytes,
                Interlocked.Read(ref _temporaryWorkspacePeakBytes)));
    }

    private List<PerformanceStage> CreateProcessingStages(PerformanceStage[] stages,
        long processingMicroseconds, double stopwatchElapsedMicroseconds)
    {
        var intervals = stages.Where(HasStageSequence).Select(stage => new
        {
            Stage = stage,
            Parent = GetMeasurement(stage, "parentStageSequence"),
            Start = GetMeasurement(stage, "stageStartOffsetMicroseconds"),
            End = GetMeasurement(stage, "stageEndOffsetMicroseconds")
        }).Where(item => item.Start is not null && item.End is not null).ToArray();
        var directWork = intervals.Where(item => item.Parent is null &&
            item.Stage.Kind != PerformanceStageKind.Fixture).ToArray();
        var fixtureIntervals = intervals.Where(item => item.Stage.Kind == PerformanceStageKind.Fixture).ToArray();
        var directWorkIntervals = MergeIntervals(directWork.Select(item =>
            (Math.Max(0, item.Start!.Value), Math.Min(stopwatchElapsedMicroseconds, item.End!.Value))));
        var excludedFixtureIntervals = MergeIntervals(fixtureIntervals.Select(item =>
            (Math.Max(0, item.Start!.Value), Math.Min(stopwatchElapsedMicroseconds, item.End!.Value))));
        var processingIntervals = SubtractIntervals([(0d, stopwatchElapsedMicroseconds)], excludedFixtureIntervals);
        var includedIntervals = SubtractIntervals(directWorkIntervals, excludedFixtureIntervals);
        var unscopedIntervals = SubtractIntervals(processingIntervals, includedIntervals);
        var coveredMicroseconds = includedIntervals.Sum(interval => interval.End - interval.Start);
        var gapMicroseconds = unscopedIntervals.Sum(interval => interval.End - interval.Start);
        var exclusiveMicroseconds = Math.Max(0, processingMicroseconds -
            checked((long)Math.Round(coveredMicroseconds + gapMicroseconds)));

        var rootSequence = _processingRootSequence;
        var result = new List<PerformanceStage>(stages.Length + unscopedIntervals.Count + 1);
        foreach (var stage in stages)
        {
            if (HasStageSequence(stage) && GetMeasurement(stage, "parentStageSequence") is null &&
                stage.Kind != PerformanceStageKind.Fixture)
                result.Add(WithParentStage(stage, rootSequence));
            else
                result.Add(stage);
        }

        foreach (var interval in unscopedIntervals)
        {
            var duration = interval.End - interval.Start;
            if (duration <= 0) continue;
            var previous = directWork.Where(item => item.End <= interval.Start)
                .OrderByDescending(item => item.End).FirstOrDefault()?.Stage.Name ?? "processing start";
            var next = directWork.Where(item => item.Start >= interval.End)
                .OrderBy(item => item.Start).FirstOrDefault()?.Stage.Name ?? "processing completion";
            var elapsed = Math.Max(0, (long)Math.Round(duration));
            var measurements = new[]
            {
                new PerformanceMeasurement("stageSequence", Interlocked.Increment(ref _stageSequence), "id"),
                new PerformanceMeasurement("parentStageSequence", rootSequence, "id"),
                new PerformanceMeasurement("stageStartOffsetMicroseconds", interval.Start, "microseconds"),
                new PerformanceMeasurement("stageEndOffsetMicroseconds", interval.End, "microseconds"),
                new PerformanceMeasurement("inclusiveElapsedMicroseconds", duration, "microseconds"),
                new PerformanceMeasurement("exclusiveElapsedMicroseconds", duration, "microseconds")
            };
            result.Add(new PerformanceStage(PerformanceStageKind.Processing,
                $"Unscoped processing interval after '{previous}' before '{next}'", elapsed, 0, 0,
                null, null, null, null, measurements));
        }

        result.Add(new PerformanceStage(PerformanceStageKind.Processing, "ProofShift processing",
            processingMicroseconds, 0, 0, null, null, null, null,
            [
                new PerformanceMeasurement("stageSequence", rootSequence, "id"),
                new PerformanceMeasurement("inclusiveElapsedMicroseconds", processingMicroseconds, "microseconds"),
                new PerformanceMeasurement("exclusiveElapsedMicroseconds", exclusiveMicroseconds, "microseconds"),
                new PerformanceMeasurement("accountedChildIntervalUnionMicroseconds", coveredMicroseconds + gapMicroseconds, "microseconds"),
                new PerformanceMeasurement("unattributedProcessingMicroseconds", exclusiveMicroseconds, "microseconds"),
                new PerformanceMeasurement("excludedFixtureElapsedMicroseconds",
                    stages.Where(stage => stage.Kind == PerformanceStageKind.Fixture).Sum(stage => stage.ElapsedMicroseconds),
                    "microseconds"),
                new PerformanceMeasurement("processingDefinitionVersion", 1, "version")
            ]));
        return result;
    }

    private static bool HasStageSequence(PerformanceStage stage) =>
        GetMeasurement(stage, "stageSequence") is not null;

    private static double? GetMeasurement(PerformanceStage stage, string name) =>
        stage.Measurements.FirstOrDefault(measurement => measurement.Name == name)?.Value;

    private static PerformanceStage WithParentStage(PerformanceStage stage, long parentSequence)
    {
        var measurements = stage.Measurements.Where(measurement => measurement.Name != "parentStageSequence").Append(
            new PerformanceMeasurement("parentStageSequence", parentSequence, "id")).ToArray();
        return stage with { Measurements = measurements };
    }

    private static List<(double Start, double End)> MergeIntervals(IEnumerable<(double Start, double End)> intervals)
    {
        var ordered = intervals.Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start).ToArray();
        var merged = new List<(double Start, double End)>();
        foreach (var interval in ordered)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End)
                merged.Add(interval);
            else
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
        }
        return merged;
    }

    private static List<(double Start, double End)> SubtractIntervals(
        IReadOnlyList<(double Start, double End)> source, IReadOnlyList<(double Start, double End)> excluded)
    {
        var result = new List<(double Start, double End)>();
        foreach (var interval in source)
        {
            var cursor = interval.Start;
            foreach (var subtraction in excluded)
            {
                if (subtraction.End <= cursor) continue;
                if (subtraction.Start >= interval.End) break;
                if (subtraction.Start > cursor)
                    result.Add((cursor, Math.Min(subtraction.Start, interval.End)));
                cursor = Math.Max(cursor, subtraction.End);
                if (cursor >= interval.End) break;
            }
            if (cursor < interval.End) result.Add((cursor, interval.End));
        }
        return result;
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
            long workspaceBytes = 0, expectedWorksetBytes = 0, ledgerBytes = 0, ledgerStagingBytes = 0, ledgerWalBytes = 0, ledgerStagingWalBytes = 0,
                walBytes = 0, evidenceBytes = 0, otherScratchBytes = 0;
            long partitionBytes = 0, maximumPartitionBytes = 0, partitionWalBytes = 0;
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
                        if (file.Name.EndsWith("-wal", StringComparison.Ordinal))
                        {
                            walBytes += file.Length;
                            if (file.Name == "ledger.sqlite-wal") ledgerWalBytes += file.Length;
                            else if (file.Name == "ledger.sqlite.staging-wal") ledgerStagingWalBytes += file.Length;
                            else if (file.Name.StartsWith("partition-", StringComparison.Ordinal)) partitionWalBytes += file.Length;
                        }
                        else if (file.Name == "working-set.sqlite") workspaceBytes += file.Length;
                        else if (file.Name == "expected.sqlite") expectedWorksetBytes += file.Length;
                        else if (file.Name.StartsWith("partition-", StringComparison.Ordinal) && file.Name.EndsWith(".db", StringComparison.Ordinal))
                        {
                            partitionBytes += file.Length;
                            maximumPartitionBytes = Math.Max(maximumPartitionBytes, file.Length);
                        }
                        else if (file.Name == "ledger.sqlite") ledgerBytes += file.Length;
                        else if (file.Name == "ledger.sqlite.staging") ledgerStagingBytes += file.Length;
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
                processCpuTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                logicalProcessorCount = Environment.ProcessorCount,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                generationZeroCollections = GC.CollectionCount(0),
                generationOneCollections = GC.CollectionCount(1),
                generationTwoCollections = GC.CollectionCount(2),
                verificationWorkspaceBytes = workspaceBytes,
                verificationExpectedWorksetBytes = expectedWorksetBytes,
                verificationPartitionBytes = partitionBytes,
                maximumVerificationPartitionBytes = maximumPartitionBytes,
                verificationPartitionWalBytes = partitionWalBytes,
                verificationLedgerBytes = ledgerBytes,
                verificationLedgerStagingBytes = ledgerStagingBytes,
                verificationLedgerWalBytes = ledgerWalBytes,
                verificationLedgerStagingWalBytes = ledgerStagingWalBytes,
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
        long completedTimestamp, long artifactCount, long byteCount, string? connectorId, string? nodeKey, string? edgeName,
        string? ruleId, IReadOnlyCollection<PerformanceMeasurement> measurements)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp, completedTimestamp);
        var stageMeasurements = measurements.ToList();
        stageMeasurements.Add(new PerformanceMeasurement("stageStartOffsetMicroseconds",
            Stopwatch.GetElapsedTime(_runStartedTimestamp, startedTimestamp).TotalMicroseconds, "microseconds"));
        stageMeasurements.Add(new PerformanceMeasurement("stageEndOffsetMicroseconds",
            Stopwatch.GetElapsedTime(_runStartedTimestamp, completedTimestamp).TotalMicroseconds, "microseconds"));
        _stages.Enqueue(new PerformanceStage(kind, name, checked((long)Math.Round(elapsed.TotalMicroseconds)),
            artifactCount, byteCount, connectorId, nodeKey, edgeName, ruleId, stageMeasurements.ToArray()));
        SampleWorkingSet();
    }

    private void RestoreCurrentStage(PerformanceStageScope scope, PerformanceStageScope? parent)
    {
        if (ReferenceEquals(_currentStage.Value, scope))
            _currentStage.Value = parent;
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
        private readonly ConcurrentBag<(long Start, long End)> _childIntervals = [];
        private readonly PerformanceStageScope? _parent;
        private long _artifactCount;
        private long _byteCount;
        private int _completed;
        private readonly long _sequence;

        internal string Name => _name;
        internal string? NodeKey => _nodeKey;
        internal long Sequence => _sequence;
        internal long? ParentSequence => _parent?._sequence;
        internal long ArtifactCount => Interlocked.Read(ref _artifactCount);
        internal double ElapsedSeconds => Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds;

        internal PerformanceStageScope(PerformanceRecorder owner, PerformanceStageKind kind, string name,
            string? connectorId, string? nodeKey, string? edgeName, string? ruleId,
            PerformanceStageScope? parent)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _owner = owner;
            _kind = kind;
            _name = name.Trim();
            _connectorId = connectorId;
            _nodeKey = nodeKey;
            _edgeName = edgeName;
            _ruleId = ruleId;
            _parent = parent;
            _sequence = Interlocked.Increment(ref owner._stageSequence);
            owner._activeStages.TryAdd(_sequence, this);
        }

        internal void AddChildInterval(long start, long end) => _childIntervals.Add((start, end));

        private long GetChildUnionTicks(long completedTimestamp)
        {
            var intervals = _childIntervals.ToArray();
            Array.Sort(intervals, static (left, right) => left.Start.CompareTo(right.Start));
            long unionTicks = 0;
            long intervalStart = 0;
            long intervalEnd = 0;
            var hasInterval = false;
            foreach (var child in intervals)
            {
                var start = Math.Max(child.Start, _startedTimestamp);
                var end = Math.Min(child.End, completedTimestamp);
                if (end <= start) continue;
                if (!hasInterval)
                {
                    intervalStart = start;
                    intervalEnd = end;
                    hasInterval = true;
                }
                else if (start <= intervalEnd)
                {
                    intervalEnd = Math.Max(intervalEnd, end);
                }
                else
                {
                    unionTicks = checked(unionTicks + intervalEnd - intervalStart);
                    intervalStart = start;
                    intervalEnd = end;
                }
            }
            return hasInterval ? checked(unionTicks + intervalEnd - intervalStart) : 0;
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
            var completedTimestamp = Stopwatch.GetTimestamp();
            var inclusiveTicks = completedTimestamp - _startedTimestamp;
            var exclusiveTicks = Math.Max(0, inclusiveTicks - GetChildUnionTicks(completedTimestamp));
            var measurements = _measurements.ToList();
            measurements.Add(new PerformanceMeasurement("stageSequence", _sequence, "id"));
            if (_parent is not null)
                measurements.Add(new PerformanceMeasurement("parentStageSequence", _parent.Sequence, "id"));
            measurements.Add(new PerformanceMeasurement("inclusiveElapsedMicroseconds",
                inclusiveTicks * 1_000_000d / Stopwatch.Frequency, "microseconds"));
            measurements.Add(new PerformanceMeasurement("exclusiveElapsedMicroseconds",
                exclusiveTicks * 1_000_000d / Stopwatch.Frequency, "microseconds"));
            _parent?.AddChildInterval(_startedTimestamp, completedTimestamp);
            _owner.RestoreCurrentStage(this, _parent);
            _owner._activeStages.TryRemove(_sequence, out _);
            _owner.CompleteStage(_kind, _name, _startedTimestamp, completedTimestamp, Interlocked.Read(ref _artifactCount),
                Interlocked.Read(ref _byteCount), _connectorId, _nodeKey, _edgeName, _ruleId, measurements);
        }
    }
}