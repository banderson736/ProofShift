using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Cli;

internal static class WorkspaceDerivationService
{
    public static async Task<Guid> InitializeFromAsync(string sourceWorkspaceDirectory,
        string destinationDirectory, CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(sourceWorkspaceDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var sourceValidation = await EvaluationWorkspaceManifestStore.ValidateAsync(sourceRoot)
            .ConfigureAwait(false);
        if (!sourceValidation.Valid)
            throw new InvalidDataException("Source workspace must pass offline structural validation before reuse.");
        var sourceWorkspace = await EvaluationWorkspaceManifestStore.ReadAsync(sourceRoot).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source workspace manifest is unavailable.");
        EnsureNoDestinationReparsePoints(destinationRoot);
        if (IsWithin(sourceRoot, destinationRoot) || IsWithin(destinationRoot, sourceRoot))
            throw new InvalidOperationException("Source and destination workspaces must not overlap.");
        if (File.Exists(destinationRoot) ||
            Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any())
            throw new InvalidOperationException("Workspace derivation requires a new or empty destination; nothing was overwritten.");

        var sourceConfiguration = ResolveContained(sourceRoot, sourceWorkspace.ProjectConfiguration);
        EnsureNoReparsePoints(sourceRoot, sourceConfiguration);
        var loaded = await new ConfigurationLoader(requireEnvironmentValues: false)
            .LoadAsync(sourceConfiguration, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsValid || loaded.Configuration is null)
            throw new InvalidDataException("Source project configuration is invalid or contains non-reference secret material.");

        var configurationRoot = Path.GetDirectoryName(sourceConfiguration)
            ?? throw new InvalidDataException("Source project configuration has no containing directory.");
        var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize(Path.GetRelativePath(sourceRoot, sourceConfiguration))
        };
        foreach (var reference in loaded.Configuration.ReferencedFiles)
        {
            var sourcePath = ResolveContained(configurationRoot, reference.RelativePath);
            EnsureNoReparsePoints(sourceRoot, sourcePath);
            EnsureOutsideRuntimeDirectories(sourceRoot, sourceWorkspace, sourcePath);
            configFiles.Add(Normalize(Path.GetRelativePath(sourceRoot, sourcePath)));
        }

        foreach (var relativePath in configFiles.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = ResolveContained(sourceRoot, relativePath);
            EnsureNoReparsePoints(sourceRoot, sourcePath);
            EnsureOutsideRuntimeDirectories(sourceRoot, sourceWorkspace, sourcePath);
            var destinationPath = ResolveContained(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        var derivedWorkspace = new EvaluationWorkspaceManifest
        {
            FormatVersion = EvaluationWorkspaceManifest.CurrentFormatVersion,
            WorkspaceId = Guid.NewGuid(),
            ProjectConfiguration = sourceWorkspace.ProjectConfiguration,
            Paths = EvaluationWorkspacePaths.Default
        };
        foreach (var entry in derivedWorkspace.Paths.Entries)
            Directory.CreateDirectory(Path.Combine(destinationRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
        await EvaluationWorkspaceManifestStore.WriteNewAsync(destinationRoot, derivedWorkspace).ConfigureAwait(false);

        var destinationValidation = await EvaluationWorkspaceManifestStore.ValidateAsync(destinationRoot)
            .ConfigureAwait(false);
        if (!destinationValidation.Valid)
            throw new InvalidDataException("Derived workspace failed offline structural validation.");
        return derivedWorkspace.WorkspaceId;
    }

    private static void EnsureNoDestinationReparsePoints(string destinationRoot)
    {
        var current = Path.GetFullPath(destinationRoot);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Workspace derivation destination cannot traverse symbolic links or reparse points.");
            var parent = Directory.GetParent(current);
            if (parent is null)
                return;
            current = parent.FullName;
        }
    }

    private static void EnsureOutsideRuntimeDirectories(string root, EvaluationWorkspaceManifest workspace,
        string filePath)
    {
        foreach (var entry in workspace.Paths.Entries)
        {
            var runtimePath = ResolveContained(root, entry.Path);
            if (IsWithin(runtimePath, filePath))
                throw new InvalidDataException("Referenced configuration cannot be stored inside workspace data or generated-artifact directories.");
        }
    }

    private static string ResolveContained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("Workspace reuse references must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ConnectorPathUtilities.IsContained(root, path))
            throw new InvalidDataException("Workspace reuse reference resolves outside its root.");
        return path;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." || !Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        if (!ConnectorPathUtilities.IsContained(fullRoot, current))
            throw new InvalidDataException("Workspace reuse path resolves outside the source workspace.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workspace reuse cannot traverse symbolic links or reparse points.");
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                return;
            current = Directory.GetParent(current)?.FullName
                ?? throw new InvalidDataException("Workspace reuse path has no source workspace parent.");
        }
    }

    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
