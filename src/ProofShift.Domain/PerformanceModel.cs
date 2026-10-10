namespace ProofShift.Domain;

public enum PerformanceStageKind
{
    Environment,
    Fixture,
    Checkpoint,
    Projection,
    Verification,
    Recovery,
    Reporting,
    Processing
}

public sealed record PerformanceMeasurement(string Name, double Value, string Unit);

public sealed record PerformanceStage(
    PerformanceStageKind Kind,
    string Name,
    long ElapsedMicroseconds,
    long ArtifactCount,
    long ByteCount,
    string? ConnectorId,
    string? NodeKey,
    string? EdgeName,
    string? RuleId,
    IReadOnlyList<PerformanceMeasurement> Measurements);

public sealed record PerformanceRun(
    string Format,
    string Id,
    string Scenario,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<PerformanceStage> Stages,
    long MaximumObservedWorkingSetBytes,
    long ManagedHeapBytesAtCompletion,
    int GenerationZeroCollections,
    int GenerationOneCollections,
    int GenerationTwoCollections,
    long TemporaryWorkspacePeakBytes)
{
    public const string FormatVersion = "proofshift-performance-run-v1";

    public long ElapsedMicroseconds => Math.Max(0, (CompletedAt - StartedAt).Ticks / 10);

    public long ProofShiftProcessingMicroseconds => Stages.FirstOrDefault(stage =>
        stage.Kind == PerformanceStageKind.Processing && stage.Name == "ProofShift processing")?.ElapsedMicroseconds ??
        Math.Max(0, ElapsedMicroseconds - Stages.Where(stage => stage.Kind == PerformanceStageKind.Fixture)
            .Sum(stage => stage.ElapsedMicroseconds));
}