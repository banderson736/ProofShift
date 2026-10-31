using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

internal sealed record WorkspaceSemanticDriftArtifact(
    string Format,
    Guid WorkspaceId,
    Guid RunId,
    string DriftIdentityFingerprint,
    WorkspaceSemanticDrift Drift,
    string IntegrityHash);

internal static class WorkspaceSemanticDriftArtifactStore
{
    private const string Format = "proofshift-workspace-semantic-drift-v1";
    private const string RelativeDirectory = ".proofshift/drifts";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<(string Reference, string IntegrityHash)> WriteAsync(string workspaceDirectory,
        Guid workspaceId, Guid runId, WorkspaceSemanticDrift drift, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var directory = ResolveContained(root, RelativeDirectory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(root, directory);
        var path = Path.Combine(directory, $"{runId:D}.json");
        EnsureNoReparsePoints(root, path);
        var artifact = new WorkspaceSemanticDriftArtifact(Format, workspaceId, runId,
            WorkspacePlanReviewService.IdentityFingerprint(drift), drift, string.Empty);
        artifact = artifact with { IntegrityHash = IntegrityHash(artifact) };
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, artifact, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            artifact.IntegrityHash);
    }

    public static byte[] Serialize(WorkspaceSemanticDriftArtifact artifact) =>
        JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);

    public static async Task<WorkspaceSemanticDriftArtifact> ReadAsync(string workspaceDirectory,
        string reference, Guid workspaceId, Guid runId, string expectedIdentity, string expectedIntegrityHash,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var path = ResolveContained(root, reference);
        EnsureNoReparsePoints(root, path);
        var artifact = JsonSerializer.Deserialize<WorkspaceSemanticDriftArtifact>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Semantic-drift artifact is empty.");
        if (artifact.Format != Format || artifact.WorkspaceId != workspaceId || artifact.RunId != runId ||
            artifact.DriftIdentityFingerprint != expectedIdentity ||
            artifact.DriftIdentityFingerprint != WorkspacePlanReviewService.IdentityFingerprint(artifact.Drift) ||
            artifact.IntegrityHash != expectedIntegrityHash ||
            artifact.IntegrityHash != IntegrityHash(artifact))
            throw new InvalidDataException("Semantic-drift artifact identity or integrity is invalid.");
        return artifact;
    }

    public static WorkspaceSemanticDriftArtifact ValidatePackagedArtifact(ReadOnlySpan<byte> bytes,
        WorkspaceRunManifest run)
    {
        var artifact = JsonSerializer.Deserialize<WorkspaceSemanticDriftArtifact>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Packaged semantic-drift artifact is empty.");
        if (artifact.Format != Format || artifact.WorkspaceId != run.WorkspaceId ||
            artifact.RunId != run.RunId ||
            artifact.DriftIdentityFingerprint != run.PreRunSemanticDriftIdentityFingerprint ||
            artifact.DriftIdentityFingerprint != WorkspacePlanReviewService.IdentityFingerprint(artifact.Drift) ||
            artifact.IntegrityHash != IntegrityHash(artifact) ||
            artifact.Drift.Status.ToString() != run.PreRunSemanticDriftStatus ||
            artifact.Drift.BaselineId != run.BaselineId ||
            artifact.Drift.CurrentPlanFingerprint != run.PlanFingerprint)
            throw new InvalidDataException("Packaged semantic-drift artifact does not match the candidate run.");
        return artifact;
    }

    public static string ComputeIntegrityHash(WorkspaceSemanticDriftArtifact artifact) =>
        IntegrityHash(artifact);

    private static string IntegrityHash(WorkspaceSemanticDriftArtifact artifact) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(artifact with { IntegrityHash = string.Empty }, JsonOptions));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ResolveContained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Contains('\\'))
            throw new InvalidDataException("Semantic-drift artifact reference is not a safe relative path.");
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ProofShift.Connectors.Abstractions.ConnectorPathUtilities.IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Semantic-drift artifact path is outside the workspace.");
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Semantic-drift artifact path is outside the workspace.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Semantic-drift artifact path traverses a reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Semantic-drift artifact path traverses a reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
