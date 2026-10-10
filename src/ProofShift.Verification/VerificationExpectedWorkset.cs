using System.Security.Cryptography;
using System.Text.Json;
using ProofShift.Engine;

namespace ProofShift.Verification;

public enum VerificationExpectedWorksetState { Building, Complete, Failed }

public sealed record VerificationExpectedWorksetKey(string CheckpointId, string CheckpointManifestHash,
    string SourceFingerprint, string ConfigurationHash, string GraphHash, string RequirementsFingerprint,
    string RuntimeVersion)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "proofshift-expected-workset-v2", CheckpointId, CheckpointManifestHash, SourceFingerprint,
        ConfigurationHash, GraphHash, RequirementsFingerprint, RuntimeVersion
    }))).ToLowerInvariant();
}

public sealed class VerificationExpectedWorkset
{
    public VerificationExpectedWorksetKey Key { get; }
    public VerificationExpectedWorksetState State { get; private set; }
    public long SourceCount { get; }
    public long ExpectedCount { get; }
    public long SourceJsonBytes { get; }
    public long ExpectedJsonBytes { get; }
    public string Checksum { get; }
    internal string DatabasePath { get; }

    internal VerificationExpectedWorkset(VerificationExpectedWorksetKey key, string databasePath, string checksum,
        long sourceCount, long expectedCount, long sourceJsonBytes, long expectedJsonBytes)
    {
        Key = key;
        DatabasePath = databasePath;
        Checksum = checksum;
        SourceCount = sourceCount;
        ExpectedCount = expectedCount;
        SourceJsonBytes = sourceJsonBytes;
        ExpectedJsonBytes = expectedJsonBytes;
        State = VerificationExpectedWorksetState.Complete;
    }

    internal async Task ValidateAsync(VerificationExpectedWorksetKey key, CancellationToken cancellationToken)
    {
        if (State != VerificationExpectedWorksetState.Complete || Key != key)
            throw new InvalidDataException("Expected workset is incomplete or its semantic input key differs.");
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(DatabasePath)!, "manifest.json"),
                cancellationToken).ConfigureAwait(false));
            var document = manifest.RootElement;
            if (document.GetProperty("format").GetString() != "proofshift-expected-workset-v2" ||
                document.GetProperty("state").GetString() != "Complete" ||
                document.GetProperty("inputs").Deserialize<VerificationExpectedWorksetKey>() != Key ||
                document.GetProperty(nameof(SourceCount)).GetInt64() != SourceCount ||
                document.GetProperty(nameof(ExpectedCount)).GetInt64() != ExpectedCount ||
                document.GetProperty(nameof(Checksum)).GetString() != Checksum ||
                Checksum != await ChecksumAsync(DatabasePath, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Expected workset content or manifest integrity validation failed.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            State = VerificationExpectedWorksetState.Failed;
            throw new InvalidDataException("Expected workset integrity validation failed; it cannot be reused.", exception);
        }
    }

    internal static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }
}

public sealed class VerificationExpectedWorksetScope : IAsyncDisposable
{
    private readonly string _root;
    private readonly Dictionary<VerificationExpectedWorksetKey, VerificationExpectedWorkset> _worksets = [];
    private bool _disposed;
    public int BuildCount { get; private set; }
    public int ReuseCount { get; private set; }

    public VerificationExpectedWorksetScope(string temporaryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);
        _root = Path.Combine(Path.GetFullPath(temporaryRoot), "expected-" + Guid.NewGuid().ToString("N"));
    }

    internal async Task<VerificationExpectedWorkset> GetOrBuildAsync(VerificationExpectedWorksetKey key,
        VerificationExecutionPlan plan, Func<SqliteVerificationWorkspace, CancellationToken, Task> build,
        PerformanceRecorder? recorder, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (key.RequirementsFingerprint != plan.ExpectedRequirementsFingerprint)
            throw new InvalidDataException("Expected workset key does not match the current field/key plan.");
        if (_worksets.TryGetValue(key, out var existing))
        {
            using var reuse = recorder?.StartStage(ProofShift.Domain.PerformanceStageKind.Verification, "expected workset reuse");
            await existing.ValidateAsync(key, cancellationToken).ConfigureAwait(false);
            ReuseCount++;
            reuse?.AddMeasurement("reuses", 1, "worksets");
            reuse?.AddMeasurement("sourceRowsReused", existing.SourceCount, "rows");
            reuse?.AddMeasurement("expectedRowsReused", existing.ExpectedCount, "rows");
            reuse?.AddMeasurement("sourceJsonBytesNotRewritten", existing.SourceJsonBytes, "bytes");
            reuse?.AddMeasurement("expectedJsonBytesNotRewritten", existing.ExpectedJsonBytes, "bytes");
            return existing;
        }
        using var stage = recorder?.StartStage(ProofShift.Domain.PerformanceStageKind.Verification, "expected workset build");
        await using var workspace = await SqliteVerificationWorkspace.CreateAsync(_root, recorder, plan, cancellationToken).ConfigureAwait(false);
        await workspace.BeginExpectedBuildAsync(key, cancellationToken).ConfigureAwait(false);
        await build(workspace, cancellationToken).ConfigureAwait(false);
        var completed = await workspace.PublishExpectedAsync(key, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _worksets.Add(key, completed);
        workspace.RetainPublishedExpected();
        BuildCount++;
        stage?.AddMeasurement("builds", 1, "worksets");
        stage?.AddMeasurement("sourceRowsBuilt", completed.SourceCount, "rows");
        stage?.AddMeasurement("expectedRowsBuilt", completed.ExpectedCount, "rows");
        stage?.AddMeasurement("sourceJsonBytesWritten", completed.SourceJsonBytes, "bytes");
        stage?.AddMeasurement("expectedJsonBytesWritten", completed.ExpectedJsonBytes, "bytes");
        return completed;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _worksets.Clear();
        if (Directory.Exists(_root))
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        return ValueTask.CompletedTask;
    }
}