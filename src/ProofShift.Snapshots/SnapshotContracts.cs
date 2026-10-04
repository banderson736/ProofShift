using ProofShift.Domain;

namespace ProofShift.Snapshots;

public static class SnapshotIssueCodes
{
    public const string CaptureFailed = "PSSNAP001";
    public const string IncompleteSourceCoverage = "PSSNAP002";
    public const string ArtifactChangedDuringCapture = "PSSNAP003";
    public const string IntegrityFailure = "PSSNAP004";
    public const string MissingReplayArtifact = "PSSNAP005";
    public const string UnsupportedFormat = "PSSNAP006";
    public const string CheckpointNotComplete = "PSSNAP007";
    public const string SourceFingerprintMismatch = "PSSNAP008";
    public const string SelectorMismatch = "PSSNAP009";
    public const string SnapshotPathInvalid = "PSSNAP010";
}

public sealed record SnapshotCaptureResult
{
    public CheckpointId Id { get; }
    public CheckpointStatus Status { get; }
    public SourceCheckpoint? Checkpoint { get; }
    public int ExpectedSourceNodes { get; }
    public int CapturedSourceNodes { get; }
    public long CapturedArtifacts { get; }
    public long CapturedBytes { get; }
    public string? FailureCode { get; }

    public SnapshotCaptureResult(
        CheckpointId id,
        CheckpointStatus status,
        SourceCheckpoint? checkpoint,
        int expectedSourceNodes,
        int capturedSourceNodes,
        long capturedArtifacts,
        long capturedBytes,
        string? failureCode = null)
    {
        Id = id;
        Status = status;
        Checkpoint = checkpoint;
        ExpectedSourceNodes = expectedSourceNodes;
        CapturedSourceNodes = capturedSourceNodes;
        CapturedArtifacts = capturedArtifacts;
        CapturedBytes = capturedBytes;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
    }
}

public sealed record CheckpointSegment(string Reference, string Sha256, long Length);

public interface ICheckpointWriteSession : IAsyncDisposable
{
    CheckpointId Id { get; }

    Task BeginSourceNodeAsync(string sourceNodeKey, CancellationToken cancellationToken);

    Task<long> WriteRecordAsync(
        string sourceNodeKey,
        RecordEnvelope record,
        Func<string, CancellationToken, ValueTask<Stream>>? openBinaryReadAsync,
        CancellationToken cancellationToken);

    Task<CheckpointSegment> CompleteSourceNodeAsync(string sourceNodeKey, CancellationToken cancellationToken);

    Task<string> FinalizeAsync(SourceCheckpoint checkpoint, CancellationToken cancellationToken);

    Task MarkIncompleteAsync(CheckpointStatus status, string failureCode, CancellationToken cancellationToken);
}

public interface ILoadedCheckpoint : IAsyncDisposable
{
    SourceCheckpoint Manifest { get; }

    IAsyncEnumerable<RecordEnvelope> ReadAsync(
        string sourceNodeKey,
        ArtifactSelector selector,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenBinaryReadAsync(
        string sourceNodeKey,
        ArtifactReference artifact,
        BinaryReferenceValue binaryReference,
        CancellationToken cancellationToken);
}

public interface IMaterializedSnapshotStore
{
    Task<ICheckpointWriteSession> CreateAsync(CheckpointId id, CancellationToken cancellationToken);

    Task<ILoadedCheckpoint> OpenCompleteAsync(string checkpointIdOrPath, CancellationToken cancellationToken);

    Task DeleteAsync(string checkpointIdOrPath, CancellationToken cancellationToken);
}

public sealed class SnapshotStoreException : Exception
{
    public string Code { get; }

    public SnapshotStoreException(string code, string message) : base(message) => Code = code;
}