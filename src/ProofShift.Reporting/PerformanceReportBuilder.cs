using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Domain;

namespace ProofShift.Reporting;

public static class PerformanceReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(PerformanceRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(run, JsonOptions);
    }

    public static string ToHumanReadable(PerformanceRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var setupMicroseconds = run.Stages.Where(stage => stage.Kind is PerformanceStageKind.Environment or PerformanceStageKind.Fixture)
            .Sum(stage => stage.ElapsedMicroseconds);
        var processingMicroseconds = Math.Max(0, run.ElapsedMicroseconds - setupMicroseconds);
        var lines = new List<string>
        {
            "ProofShift Performance",
            string.Empty,
            $"Scenario: {run.Scenario}",
            $"Run: {run.Id}",
            $"Benchmark wall time: {FormatMilliseconds(run.ElapsedMicroseconds)} ms",
            $"Environment and fixture setup: {FormatMilliseconds(setupMicroseconds)} ms",
            $"Non-setup pipeline runtime: {FormatMilliseconds(processingMicroseconds)} ms",
            string.Empty
        };

        foreach (var kind in Enum.GetValues<PerformanceStageKind>())
        {
            var stages = run.Stages.Where(stage => stage.Kind == kind).ToArray();
            if (stages.Length == 0) continue;
            lines.Add(kind.ToString().ToUpperInvariant());
            foreach (var stage in stages)
            {
                var scope = string.Join(" / ", new[] { stage.ConnectorId, stage.NodeKey, stage.EdgeName, stage.RuleId }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                var label = scope.Length == 0 ? stage.Name : $"{stage.Name} [{scope}]";
                lines.Add($"{label}: {FormatMilliseconds(stage.ElapsedMicroseconds)} ms" +
                    (stage.ArtifactCount > 0 ? $", {stage.ArtifactCount.ToString("N0", CultureInfo.InvariantCulture)} artifacts" : string.Empty) +
                    (stage.ByteCount > 0 ? $", {FormatBytes(stage.ByteCount)}" : string.Empty));
                var exclusive = stage.Measurements.FirstOrDefault(measurement =>
                    measurement.Name == "exclusiveElapsedMicroseconds");
                if (exclusive is not null)
                    lines.Add($"  Exclusive: {FormatMilliseconds((long)Math.Round(exclusive.Value))} ms");
                if (stage.ArtifactCount > 0 && stage.ElapsedMicroseconds > 0)
                {
                    var seconds = stage.ElapsedMicroseconds / 1_000_000d;
                    lines.Add($"  Throughput: {(stage.ArtifactCount / seconds).ToString("N1", CultureInfo.InvariantCulture)} artifacts/sec");
                }
                foreach (var measurement in stage.Measurements)
                    lines.Add($"  {measurement.Name}: {measurement.Value.ToString("N2", CultureInfo.InvariantCulture)} {measurement.Unit}");
            }
            lines.Add(string.Empty);
        }

        lines.Add($"Maximum observed ProofShift working set: {FormatBytes(run.MaximumObservedWorkingSetBytes)}");
        lines.Add("Working set sampling: run start/end and stage boundaries; not continuous.");
        lines.Add($"Managed heap at completion: {FormatBytes(run.ManagedHeapBytesAtCompletion)}");
        lines.Add($"GC collections (0/1/2): {run.GenerationZeroCollections}/{run.GenerationOneCollections}/{run.GenerationTwoCollections}");
        lines.Add($"Temporary workspace peak: {FormatBytes(run.TemporaryWorkspacePeakBytes)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatMilliseconds(long microseconds) =>
        (microseconds / 1_000d).ToString("N2", CultureInfo.InvariantCulture);

    private static string FormatBytes(long bytes) =>
        $"{(bytes / (1024d * 1024d)).ToString("N2", CultureInfo.InvariantCulture)} MiB ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
}