using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

internal sealed record WorkspaceCleanupResource(
    string Type,
    string Path,
    string Status,
    string Action,
    string Reason);

internal sealed record WorkspaceCleanupResult(
    Guid WorkspaceId,
    Guid RunId,
    bool Executed,
    bool SafeToExecute,
    IReadOnlyList<WorkspaceCleanupResource> Resources,
    IReadOnlyList<string> Preserved);

internal static class WorkspaceCleanupService
{
    private const string TemporaryDirectoryName = "temporary";
    private const string OwnershipMarkerName = "proofshift-owned-temporary.json";
    private const string OwnershipFormat = "proofshift-owned-run-temporary-v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task MarkTemporaryOwnedAsync(string runDirectory, WorkspaceRunManifest run,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetFullPath(runDirectory), TemporaryDirectoryName);
        if (FileSystemEntryExists(temporaryDirectory))
            throw new InvalidDataException("Run temporary directory already exists; ownership cannot be established safely.");
        Directory.CreateDirectory(temporaryDirectory);
        var marker = new OwnershipMarker(OwnershipFormat, run.WorkspaceId, run.RunId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
        await using var stream = new FileStream(Path.Combine(temporaryDirectory, OwnershipMarkerName),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkspaceCleanupResult> RunAsync(string workspaceDirectory, string runReference,
        bool execute, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before cleanup.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        if (!Guid.TryParse(runReference, out var runId) || runId == Guid.Empty)
            throw new ArgumentException("Run reference must be a non-empty run GUID.", nameof(runReference));
        var runsRoot = ResolveContained(root, workspace.Paths.Runs);
        var runDirectory = ResolveContained(runsRoot, runId.ToString("D"));
        EnsureNoReparsePoints(root, runDirectory);
        if (!Directory.Exists(runDirectory))
            throw new FileNotFoundException("Selected workspace run does not exist.", runDirectory);
        var run = await WorkspaceRunManifestStore.ReadAsync(runDirectory, cancellationToken).ConfigureAwait(false);
        if (run.RunId != runId || run.WorkspaceId != workspace.WorkspaceId)
            throw new InvalidDataException("Run manifest identity does not match the selected workspace.");

        var temporaryDirectory = Path.Combine(runDirectory, TemporaryDirectoryName);
        var temporaryRelative = Path.GetRelativePath(root, temporaryDirectory).Replace(Path.DirectorySeparatorChar, '/');
        var resource = await DescribeTemporaryAsync(temporaryDirectory, temporaryRelative, workspace.WorkspaceId,
            runId, cancellationToken).ConfigureAwait(false);
        var resources = new List<WorkspaceCleanupResource> { resource };
        var safe = resource.Status is "Ready" or "AlreadyAbsent";
        if (execute && resource.Status == "Ready")
        {
            EnsureNoReparsePoints(root, temporaryDirectory);
            EnsureNoNestedReparsePoints(temporaryDirectory);
            if (!await HasValidOwnershipMarkerAsync(temporaryDirectory, workspace.WorkspaceId, runId, cancellationToken)
                    .ConfigureAwait(false))
                return new(workspace.WorkspaceId, runId, Executed: false, SafeToExecute: false,
                    [resource with { Status = "OwnershipChanged", Action = "refused" }],
                    PreservedResources(runId));
            Directory.Delete(temporaryDirectory, recursive: true);
            resources[0] = resource with { Status = "Removed", Action = "deleted" };
        }

        return new(workspace.WorkspaceId, runId, execute && safe, safe, resources, PreservedResources(runId));
    }

    private static async Task<WorkspaceCleanupResource> DescribeTemporaryAsync(string path, string relative,
        Guid workspaceId, Guid runId, CancellationToken cancellationToken)
    {
        if (!FileSystemEntryExists(path))
            return new("run-temporary", relative, "AlreadyAbsent", "no-op",
                "The run-scoped temporary directory is already absent.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            return new("run-temporary", relative, "OwnershipUnverified", "refused",
                "The temporary resource is not a normal directory owned by this run.");
        if (!await HasValidOwnershipMarkerAsync(path, workspaceId, runId, cancellationToken).ConfigureAwait(false))
            return new("run-temporary", relative, "OwnershipUnverified", "refused",
                "The exact workspace/run ownership marker is missing or invalid.");
        try
        {
            EnsureNoNestedReparsePoints(path);
        }
        catch (InvalidDataException)
        {
            return new("run-temporary", relative, "UnsafePath", "refused",
                "The temporary tree contains a symbolic link or reparse point.");
        }
        return new("run-temporary", relative, "Ready", "remove",
            "The path is the fixed temporary child of this run directory and its ownership marker matches the validated run.");
    }

    private static async Task<bool> HasValidOwnershipMarkerAsync(string directory, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(directory, OwnershipMarkerName);
        if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            return false;
        try
        {
            var marker = JsonSerializer.Deserialize<OwnershipMarker>(
                await File.ReadAllBytesAsync(markerPath, cancellationToken).ConfigureAwait(false), JsonOptions);
            return marker is not null && marker.Format == OwnershipFormat &&
                marker.WorkspaceId == workspaceId && marker.RunId == runId;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void EnsureNestedReparsePoints(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Temporary resource contains a symbolic link or reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void EnsureNoNestedReparsePoints(string path) => EnsureNestedReparsePoints(path);

    private static IReadOnlyList<string> PreservedResources(Guid runId) =>
    [
        $"runs/{runId:D}/run.json",
        $"runs/{runId:D}/evidence",
        $"runs/{runId:D}/recovery",
        $"runs/{runId:D}/summary.md",
        "evidence/ (retained Evidence packages)",
        ".proofshift/checkpoints/ (source checkpoints are immutable retained inputs)"
    ];

    private static string ResolveContained(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(fullRoot, fullPath))
            throw new InvalidDataException("Cleanup path is outside its configured root.");
        return fullPath;
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison);
    }

    private static bool FileSystemEntryExists(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is null || !Directory.Exists(parent)) return false;
        var name = Path.GetFileName(path);
        return Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, PathComparison));
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(candidate));
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new InvalidDataException("Cleanup path is outside the workspace.");
        var current = fullRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Cleanup path traverses a symbolic link or reparse point.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cleanup path traverses a symbolic link or reparse point.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record OwnershipMarker(string Format, Guid WorkspaceId, Guid RunId);
}
