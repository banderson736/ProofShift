namespace ProofShift.DemoScenarios;

public static class DemoScenarioContentRoot
{
    public static string ResolvePath(string contentRootDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalizedRelativePath = relativePath.Replace('\\', '/');
        var segments = normalizedRelativePath.Split('/');
        if (Path.IsPathRooted(relativePath) ||
            segments.Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
            throw new ArgumentException("Scenario content paths must be safe, relative paths.", nameof(relativePath));

        var contentRoot = Path.GetFullPath(contentRootDirectory);
        if (!Directory.Exists(contentRoot))
            throw new DirectoryNotFoundException("Scenario content root directory does not exist.");
        RejectReparsePoint(contentRoot);
        var fullPath = Path.GetFullPath(Path.Combine(contentRoot,
            Path.Combine(segments)));
        var relativeToRoot = Path.GetRelativePath(contentRoot, fullPath);
        if (Path.IsPathRooted(relativeToRoot) || relativeToRoot == ".." ||
            relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new ArgumentException("Scenario content paths must remain within the content root.", nameof(relativePath));

        var currentPath = contentRoot;
        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            RejectReparsePoint(currentPath);
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Scenario content paths cannot traverse a reparse point.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
