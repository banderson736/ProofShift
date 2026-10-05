using System.Collections.Concurrent;
using System.Diagnostics;
using ProofShift.Domain;

namespace ProofShift.Engine;

public sealed class PerformanceRecorder
{
    private readonly ConcurrentQueue<PerformanceStage> _stages = new();
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
            _owner.CompleteStage(_kind, _name, _startedTimestamp, Interlocked.Read(ref _artifactCount),
                Interlocked.Read(ref _byteCount), _connectorId, _nodeKey, _edgeName, _ruleId, _measurements.ToArray());
        }
    }
}