using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

internal sealed record WorkspaceRunProgress(string Stage, string Status, DateTimeOffset At);

internal sealed record WorkspaceRunManifest
{
    public const string CurrentFormat = "proofshift-workspace-run-v1";

    public string Format { get; init; } = CurrentFormat;
    public Guid WorkspaceId { get; init; }
    public Guid RunId { get; init; }
    public string ProductVersion { get; init; } = string.Empty;
    public string? SourceRevision { get; init; }
    public string? SourceTree { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string State { get; init; } = "Created";
    public string? PlanFingerprint { get; init; }
    public string? ConfigurationHash { get; init; }
    public string? GraphHash { get; init; }
    public IReadOnlyList<string> DiscoveryFingerprints { get; init; } = [];
    public string? CheckpointId { get; init; }
    public string? CheckpointManifestHash { get; init; }
    public string? CheckpointSourceFingerprint { get; init; }
    public string? CheckpointStoreScope { get; init; }
    public bool? ReplayedCheckpoint { get; init; }
    public string? ProjectionRunId { get; init; }
    public string? ProjectionManifestReference { get; init; }
    public string? ProjectionManifestHash { get; init; }
    public string? ProjectionFingerprint { get; init; }
    public string? VerificationRunId { get; init; }
    public string? VerificationOutcome { get; init; }
    public string? VerificationEvidenceFingerprint { get; init; }
    public string? VerificationLedgerFingerprint { get; init; }
    public string? VerificationLedgerReference { get; init; }
    public IReadOnlyList<string> ReconciliationRuleIds { get; init; } = [];
    public IReadOnlyList<string> ReconciliationFailedRuleIds { get; init; } = [];
    public string? RecoveryRunId { get; init; }
    public string? RecoveryQualification { get; init; }
    public string? RecoveryArtifactReference { get; init; }
    public string? RecoveryArtifactIntegrityHash { get; init; }
    public string? EvidenceReference { get; init; }
    public string? EvidenceManifestIntegrityHash { get; init; }
    public string? Disposition { get; init; }
    public string? FailureCode { get; init; }
    public IReadOnlyList<WorkspacePreflightCheck> ReadinessChecks { get; init; } = [];
    public IReadOnlyList<WorkspaceRunProgress> Progress { get; init; } = [];
    public string IdentityFingerprint { get; init; } = string.Empty;
    public string? ManifestHash { get; init; }
}

internal sealed record WorkspaceRunProgressEvent(string Type, WorkspaceRunProgress Progress);

internal sealed record WorkspaceRunResult(
    WorkspaceRunManifest? Manifest,
    string? RunDirectory,
    IReadOnlyList<WorkspacePreflightCheck> ReadinessChecks);

internal static class WorkspaceRunManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task WriteAsync(string runDirectory, WorkspaceRunManifest manifest,
        bool createNew, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(runDirectory);

        var prepared = manifest with
        {
            IdentityFingerprint = ComputeIdentityFingerprint(manifest),
            ManifestHash = null
        };
        var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(prepared, JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared with { ManifestHash = hash }, JsonOptions);
        var destination = Path.Combine(runDirectory, "run.json");
        var temporary = Path.Combine(runDirectory, $"run-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: !createNew);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<WorkspaceRunManifest> ReadAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(runDirectory), "run.json");
        return ReadAndValidate(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    public static WorkspaceRunManifest ReadAndValidate(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<WorkspaceRunManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Workspace run manifest is empty.");
        if (manifest.Format != WorkspaceRunManifest.CurrentFormat ||
            manifest.RunId == Guid.Empty || manifest.WorkspaceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.ManifestHash) ||
            !string.Equals(manifest.IdentityFingerprint, ComputeIdentityFingerprint(manifest), StringComparison.Ordinal))
            throw new InvalidDataException("Workspace run manifest format or identity is invalid.");

        var actualHash = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest with { ManifestHash = null }, JsonOptions));
        if (!string.Equals(actualHash, manifest.ManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Workspace run manifest integrity validation failed.");
        return manifest;
    }

    private static string ComputeIdentityFingerprint(WorkspaceRunManifest manifest)
    {
        var input = new IdentityInput(
            manifest.Format,
            manifest.WorkspaceId,
            manifest.RunId,
            manifest.ProductVersion,
            manifest.SourceRevision,
            manifest.SourceTree,
            manifest.PlanFingerprint,
            manifest.ConfigurationHash,
            manifest.GraphHash,
            manifest.DiscoveryFingerprints.Order(StringComparer.Ordinal).ToArray(),
            manifest.CheckpointId,
            manifest.CheckpointManifestHash,
            manifest.CheckpointSourceFingerprint,
            manifest.CheckpointStoreScope,
            manifest.ReplayedCheckpoint,
            manifest.ProjectionRunId,
            manifest.ProjectionManifestReference,
            manifest.ProjectionManifestHash,
            manifest.ProjectionFingerprint,
            manifest.VerificationRunId,
            manifest.VerificationOutcome,
            manifest.VerificationEvidenceFingerprint,
            manifest.VerificationLedgerFingerprint,
            manifest.VerificationLedgerReference,
            manifest.ReconciliationRuleIds.Order(StringComparer.Ordinal).ToArray(),
            manifest.ReconciliationFailedRuleIds.Order(StringComparer.Ordinal).ToArray(),
            manifest.RecoveryRunId,
            manifest.RecoveryQualification,
            manifest.RecoveryArtifactReference,
            manifest.RecoveryArtifactIntegrityHash,
            manifest.EvidenceReference,
            manifest.EvidenceManifestIntegrityHash,
            manifest.Disposition,
            manifest.FailureCode);
        return Hash(JsonSerializer.SerializeToUtf8Bytes(input, JsonOptions));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record IdentityInput(
        string Format,
        Guid WorkspaceId,
        Guid RunId,
        string ProductVersion,
        string? SourceRevision,
        string? SourceTree,
        string? PlanFingerprint,
        string? ConfigurationHash,
        string? GraphHash,
        IReadOnlyList<string> DiscoveryFingerprints,
        string? CheckpointId,
        string? CheckpointManifestHash,
        string? CheckpointSourceFingerprint,
        string? CheckpointStoreScope,
        bool? ReplayedCheckpoint,
        string? ProjectionRunId,
        string? ProjectionManifestReference,
        string? ProjectionManifestHash,
        string? ProjectionFingerprint,
        string? VerificationRunId,
        string? VerificationOutcome,
        string? VerificationEvidenceFingerprint,
        string? VerificationLedgerFingerprint,
        string? VerificationLedgerReference,
        IReadOnlyList<string> ReconciliationRuleIds,
        IReadOnlyList<string> ReconciliationFailedRuleIds,
        string? RecoveryRunId,
        string? RecoveryQualification,
        string? RecoveryArtifactReference,
        string? RecoveryArtifactIntegrityHash,
        string? EvidenceReference,
        string? EvidenceManifestIntegrityHash,
        string? Disposition,
        string? FailureCode);
}
