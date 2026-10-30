using System.Text.Json;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Cli;

internal sealed record WorkspaceRunHistoryEntry(
    string DirectoryName,
    bool Trusted,
    string ValidationStatus,
    string? ValidationIssue,
    Guid? RunId,
    string? State,
    string? Disposition,
    string? EffectivePlanFingerprint,
    Guid? BaselineId,
    Guid? BaselineRunId,
    bool? ReplayedCheckpoint,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? EvidenceIdentity,
    string PackageStatus,
    string? PackageIdentity,
    string ComparisonStatus,
    string? OutcomeClassification);

internal static class WorkspaceRunHistoryService
{
    public static async Task<IReadOnlyList<WorkspaceRunHistoryEntry>> ListAsync(string workspaceDirectory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var validation = await EvaluationWorkspaceManifestStore.ValidateAsync(root).ConfigureAwait(false);
        if (!validation.Valid)
            throw new InvalidDataException("Workspace must pass offline structural validation before listing runs.");
        var workspace = await EvaluationWorkspaceManifestStore.ReadAsync(root).ConfigureAwait(false)
            ?? throw new InvalidDataException("Workspace manifest is unavailable.");
        var runsDirectory = ResolveContained(root, workspace.Paths.Runs);
        EnsureNoReparsePoints(root, runsDirectory);
        if (!Directory.Exists(runsDirectory))
            throw new DirectoryNotFoundException("Workspace run directory is unavailable.");

        var entries = new List<WorkspaceRunHistoryEntry>();
        foreach (var directory in Directory.EnumerateDirectories(runsDirectory)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                entries.Add(Untrusted(directoryName, "Run directory is a reparse point."));
                continue;
            }

            if (!Guid.TryParse(directoryName, out var directoryRunId) || directoryRunId == Guid.Empty)
            {
                entries.Add(Untrusted(directoryName, "Run directory name is not a valid run GUID."));
                continue;
            }

            WorkspaceRunManifest run;
            try
            {
                var manifestPath = Path.Combine(directory, "run.json");
                EnsureNoReparsePoints(root, manifestPath);
                run = await WorkspaceRunManifestStore.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                if (run.WorkspaceId != workspace.WorkspaceId || run.RunId != directoryRunId)
                {
                    entries.Add(Untrusted(directoryName, "Run manifest identity does not match the workspace directory."));
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or ArgumentException)
            {
                entries.Add(Untrusted(directoryName, $"Run manifest is invalid ({exception.GetType().Name})."));
                continue;
            }

            var (packageStatus, packageIdentity) = await ReadPackageIdentityAsync(root, workspace, run,
                cancellationToken).ConfigureAwait(false);
            var (comparisonStatus, outcomeClassification) = await ReadOutcomeAsync(root, run,
                cancellationToken).ConfigureAwait(false);
            entries.Add(new WorkspaceRunHistoryEntry(directoryName, Trusted: true, "Valid", null,
                run.RunId, run.State, run.Disposition, run.PlanFingerprint, run.BaselineId, run.BaselineRunId,
                run.ReplayedCheckpoint, run.StartedAt,
                run.State == "Completed" ? run.UpdatedAt : null,
                run.EvidenceManifestIntegrityHash ?? run.VerificationEvidenceFingerprint,
                packageStatus, packageIdentity, comparisonStatus, outcomeClassification));
        }

        return entries.OrderByDescending(item => item.StartedAt)
            .ThenBy(item => item.DirectoryName, StringComparer.Ordinal).ToArray();
    }

    private static async Task<(string Status, string? Identity)> ReadPackageIdentityAsync(string root,
        EvaluationWorkspaceManifest workspace, WorkspaceRunManifest run, CancellationToken cancellationToken)
    {
        var evidenceDirectory = ResolveContained(root, workspace.Paths.Evidence);
        var packagePath = Path.Combine(evidenceDirectory, $"{run.RunId:D}.zip");
        EnsureNoReparsePoints(root, packagePath);
        if (!File.Exists(packagePath))
            return ("NotPackaged", null);

        try
        {
            var package = await WorkspaceEvidencePackageService.ValidateAsync(packagePath, cancellationToken)
                .ConfigureAwait(false);
            if (!package.Valid || package.RunId != run.RunId ||
                package.RunManifestIdentityFingerprint != run.IdentityFingerprint ||
                package.RunManifestHash != run.ManifestHash)
                return ("Invalid", null);
            return ("Valid", package.PackageIdentity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            return ("Invalid", null);
        }
    }

    private static async Task<(string Status, string? Classification)> ReadOutcomeAsync(string root,
        WorkspaceRunManifest run, CancellationToken cancellationToken)
    {
        if (run.BaselineId is null)
            return ("NotBaselineBound", null);
        if (run.State != "Completed")
            return ("NotEstablished", null);

        try
        {
            var comparison = await WorkspaceBaselineComparisonService.ReadAsync(root, run.RunId, run,
                cancellationToken).ConfigureAwait(false);
            return comparison is null
                ? ("NotAvailable", null)
                : ("Valid", comparison.OutcomeClassification);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return ("Invalid", null);
        }
    }

    private static WorkspaceRunHistoryEntry Untrusted(string directoryName, string issue) =>
        new(directoryName, Trusted: false, "Invalid", issue, null, null, null, null, null, null,
            null, null, null, null, "Unknown", null, "Unknown", null);

    private static string ResolveContained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("Workspace run-history path must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ConnectorPathUtilities.IsContained(root, path))
            throw new InvalidDataException("Workspace run-history path resolves outside the workspace.");
        return path;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        if (!ConnectorPathUtilities.IsContained(fullRoot, current))
            throw new InvalidDataException("Workspace run-history path resolves outside the workspace.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace run-history paths cannot traverse reparse points.");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                return;
            current = Directory.GetParent(current)?.FullName
                ?? throw new InvalidDataException("Workspace run-history path has no workspace parent.");
        }
    }
}
