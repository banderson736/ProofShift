using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Connectors.Abstractions;
using ProofShift.Domain;
using ProofShift.Engine;

namespace ProofShift.Projection;

public sealed record ProjectionRunManifest
{
    public const string FormatVersion = "proofshift-projection-run-v1";

    public string Format { get; }
    public RunId RunId { get; }
    public ProjectionStatus Status { get; }
    public string ProjectId { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public long SourceArtifactCount { get; }
    public long TargetArtifactCount { get; }
    public long FailureCount { get; }
    public string? FailureCode { get; }
    public string? ProjectionFingerprint { get; }
    public string ProjectionFingerprintVersion { get; }
    public string JournalPath { get; }
    public string? CheckpointId { get; }
    public string? CheckpointSourceFingerprint { get; }
    public string? CheckpointManifestHash { get; }
    public IReadOnlyDictionary<string, string> ConnectorVersions { get; }
    public IReadOnlyCollection<string> ShadowDestinations { get; }
    public string ManifestHash { get; }

    internal ProjectionRunManifest(ProjectionRun run, string manifestHash)
    {
        Format = FormatVersion;
        RunId = run.Id;
        Status = run.Status;
        ProjectId = run.ProjectId;
        ConfigurationHash = run.ConfigurationHash;
        GraphHash = run.GraphHash;
        StartedAt = run.StartedAt;
        CompletedAt = run.CompletedAt;
        SourceArtifactCount = run.SourceArtifactCount;
        TargetArtifactCount = run.TargetArtifactCount;
        FailureCount = run.FailureCount;
        FailureCode = run.FailureCode;
        ProjectionFingerprint = run.Fingerprint;
        ProjectionFingerprintVersion = run.FingerprintVersion;
        JournalPath = run.JournalPath;
        CheckpointId = run.CheckpointId;
        CheckpointSourceFingerprint = run.CheckpointSourceFingerprint;
        CheckpointManifestHash = run.CheckpointManifestHash;
        ConnectorVersions = new SortedDictionary<string, string>(
            run.ConnectorVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        ShadowDestinations = run.ShadowDestinations.Order(StringComparer.Ordinal).ToArray();
        ManifestHash = manifestHash;
    }
}

public static class ProjectionRunManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<string> WriteAsync(string projectDirectory, ProjectionRun run, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(run);
        var directory = Path.Combine(Path.GetFullPath(projectDirectory), ".proofshift", "projections", run.Id.Value.ToString("N", CultureInfo.InvariantCulture));
        var journalPath = Path.GetFullPath(Path.Combine(Path.GetFullPath(projectDirectory), run.JournalPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(journalPath) || !ConnectorPathUtilities.IsContained(directory, journalPath))
            throw new ProjectionManifestException("PSPROJ_MANIFEST_PATH", "Projection journal is unavailable or outside its run directory.");

        Directory.CreateDirectory(directory);
        var draft = ToDocument(run, manifestHash: null);
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft with { ManifestHash = hash }, JsonOptions);
        var temporaryPath = Path.Combine(directory, $"run-{Guid.NewGuid():N}.partial");
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, Path.Combine(directory, "run.json"), overwrite: false);
        return hash;
    }

    public static async Task<ProjectionRunManifest> ReadAsync(string projectDirectory, RunId expectedRunId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(projectDirectory), ".proofshift", "projections",
            expectedRunId.Value.ToString("N", CultureInfo.InvariantCulture), "run.json");
        ProjectionRunDocument document;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            document = JsonSerializer.Deserialize<ProjectionRunDocument>(bytes, JsonOptions)
                ?? throw new ProjectionManifestException("PSPROJ_MANIFEST_INVALID", "Projection run manifest is empty.");
        }
        catch (ProjectionManifestException)
        {
            throw;
        }
        catch
        {
            throw new ProjectionManifestException("PSPROJ_MANIFEST_MISSING", "Projection run manifest is missing or unreadable.");
        }

        if (document.Format != ProjectionRunManifest.FormatVersion ||
            !Guid.TryParseExact(document.RunId, "D", out var parsedRunId) || parsedRunId != expectedRunId.Value)
            throw new ProjectionManifestException("PSPROJ_MANIFEST_INVALID", "Projection run manifest format or identity is invalid.");

        var actualHash = Hash(JsonSerializer.SerializeToUtf8Bytes(document with { ManifestHash = null }, JsonOptions));
        if (document.ManifestHash is null || !string.Equals(actualHash, document.ManifestHash, StringComparison.Ordinal))
            throw new ProjectionManifestException("PSPROJ_MANIFEST_INTEGRITY", "Projection run manifest hash does not match its canonical content.");

        var run = new ProjectionRun(
            new RunId(parsedRunId),
            Enum.Parse<ProjectionStatus>(document.Status, ignoreCase: true),
            document.ProjectId,
            document.ConfigurationHash,
            document.GraphHash,
            DateTimeOffset.ParseExact(document.StartedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            document.CompletedAt is null ? null : DateTimeOffset.ParseExact(document.CompletedAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            document.SourceArtifactCount,
            document.TargetArtifactCount,
            document.FailureCount,
            document.FailureCode,
            document.ProjectionFingerprint,
            document.ConnectorVersions,
            document.ShadowDestinations,
            document.JournalPath,
            document.CheckpointId,
            document.CheckpointSourceFingerprint,
            document.CheckpointManifestHash);
        return new ProjectionRunManifest(run, document.ManifestHash);
    }

    private static ProjectionRunDocument ToDocument(ProjectionRun run, string? manifestHash) => new(
        ProjectionRunManifest.FormatVersion,
        manifestHash,
        run.Id.Value.ToString("D", CultureInfo.InvariantCulture),
        run.Status.ToString().ToLowerInvariant(),
        run.ProjectId,
        run.ConfigurationHash,
        run.GraphHash,
        run.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        run.CompletedAt?.ToString("O", CultureInfo.InvariantCulture),
        run.SourceArtifactCount,
        run.TargetArtifactCount,
        run.FailureCount,
        run.FailureCode,
        run.Fingerprint,
        run.FingerprintVersion,
        run.JournalPath,
        run.CheckpointId,
        run.CheckpointSourceFingerprint,
        run.CheckpointManifestHash,
        new SortedDictionary<string, string>(
            run.ConnectorVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal),
        run.ShadowDestinations.Order(StringComparer.Ordinal).ToArray());

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ProjectionRunDocument(
        string Format,
        string? ManifestHash,
        string RunId,
        string Status,
        string ProjectId,
        string ConfigurationHash,
        string GraphHash,
        string StartedAt,
        string? CompletedAt,
        long SourceArtifactCount,
        long TargetArtifactCount,
        long FailureCount,
        string? FailureCode,
        string? ProjectionFingerprint,
        string ProjectionFingerprintVersion,
        string JournalPath,
        string? CheckpointId,
        string? CheckpointSourceFingerprint,
        string? CheckpointManifestHash,
        SortedDictionary<string, string> ConnectorVersions,
        string[] ShadowDestinations);
}

public sealed class ProjectionManifestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
