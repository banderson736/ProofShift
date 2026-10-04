namespace ProofShift.Connectors.Abstractions;

public static class ConnectorPathUtilities
{
    public static string ResolveRoot(ConnectorContext context)
    {
        var configuredRoot = context.Configuration.GetRequired("root").UseValue(value => value);
        try
        {
            var root = Path.GetFullPath(configuredRoot);
            if (!Directory.Exists(root))
            {
                throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Configured source root is unavailable.");
            }

            var rootInfo = new DirectoryInfo(root);
            var finalRoot = rootInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? rootInfo.FullName;
            return Path.GetFullPath(finalRoot);
        }
        catch (ConnectorConfigurationException)
        {
            throw;
        }
        catch
        {
            throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Configured source root is invalid.");
        }
    }

    public static bool TryResolveContainedPath(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        try
        {
            var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == ".."))
            {
                return false;
            }

            var canonicalRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, normalized));
            var relative = Path.GetRelativePath(canonicalRoot, candidate);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison))
            {
                return false;
            }

            var resolved = canonicalRoot;
            foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                resolved = Path.Combine(resolved, segment);
                FileSystemInfo info = Directory.Exists(resolved)
                    ? new DirectoryInfo(resolved)
                    : new FileInfo(resolved);
                if (!info.Exists || info.LinkTarget is null)
                {
                    continue;
                }

                resolved = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved);
                if (!IsContained(canonicalRoot, resolved))
                {
                    return false;
                }
            }

            fullPath = Path.GetFullPath(Path.Combine(resolved, string.Empty));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    public static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
