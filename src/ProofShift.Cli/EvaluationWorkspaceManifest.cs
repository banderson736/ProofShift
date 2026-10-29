using System.Text.Json;
using System.Text.Json.Serialization;
using ProofShift.Configuration;
using ProofShift.Connectors.Abstractions;
using ProofShift.Graph;
using ProofShift.Recovery;
using ProofShift.Verification;

namespace ProofShift.Cli;

internal sealed record EvaluationWorkspaceManifest
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "proofshift.workspace.json";

    public required int FormatVersion { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string ProjectConfiguration { get; init; }
    public required EvaluationWorkspacePaths Paths { get; init; }
}

internal sealed record EvaluationWorkspacePaths
{
    public required string CustomerData { get; init; }
    public required string Discovery { get; init; }
    public required string Generated { get; init; }
    public required string Runs { get; init; }
    public required string Evidence { get; init; }
    public required string Temporary { get; init; }

    public static EvaluationWorkspacePaths Default { get; } = new()
    {
        CustomerData = "data",
        Discovery = "discovery",
        Generated = "generated",
        Runs = "runs",
        Evidence = "evidence",
        Temporary = ".proofshift/tmp"
    };

    public IReadOnlyList<(string Name, string Path)> Entries =>
    [
        (nameof(CustomerData), CustomerData),
        (nameof(Discovery), Discovery),
        (nameof(Generated), Generated),
        (nameof(Runs), Runs),
        (nameof(Evidence), Evidence),
        (nameof(Temporary), Temporary)
    ];
}

internal sealed record EvaluationWorkspaceIssue(string Code, string Message);

internal sealed record EvaluationWorkspaceValidationResult(
    bool Valid,
    Guid? WorkspaceId,
    int? FormatVersion,
    string? ProjectConfiguration,
    IReadOnlyList<EvaluationWorkspaceIssue> Issues);

internal static class EvaluationWorkspaceManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static async Task WriteNewAsync(string workspaceDirectory, EvaluationWorkspaceManifest manifest)
    {
        var path = Path.Combine(Path.GetFullPath(workspaceDirectory), EvaluationWorkspaceManifest.FileName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public static async Task<EvaluationWorkspaceValidationResult> ValidateAsync(string workspaceDirectory)
    {
        var root = Path.GetFullPath(workspaceDirectory);
        var issues = new List<EvaluationWorkspaceIssue>();
        if (!Directory.Exists(root))
        {
            issues.Add(new("PSWORK001", "Workspace directory does not exist."));
            return Result(null, issues);
        }

        EvaluationWorkspaceManifest? manifest;
        try
        {
            manifest = await ReadAsync(root).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            issues.Add(new("PSWORK002", "Workspace manifest is not valid versioned JSON."));
            return Result(null, issues);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new("PSWORK003", "Workspace manifest is missing or unreadable."));
            return Result(null, issues);
        }

        if (manifest is null)
        {
            issues.Add(new("PSWORK002", "Workspace manifest is empty."));
            return Result(null, issues);
        }

        if (manifest.FormatVersion != EvaluationWorkspaceManifest.CurrentFormatVersion)
            issues.Add(new("PSWORK004", "Workspace manifest format version is unsupported."));
        if (manifest.WorkspaceId == Guid.Empty)
            issues.Add(new("PSWORK005", "Workspace identity must be a non-empty GUID."));

        var configurationPath = ResolveRelativePath(root, manifest.ProjectConfiguration, "project configuration", issues);
        if (manifest.Paths is null)
        {
            issues.Add(new("PSWORK006", "Workspace path layout is missing."));
        }
        else
        {
            var resolvedPaths = new List<(string Name, string Relative, string Absolute)>();
            foreach (var entry in manifest.Paths.Entries)
            {
                var absolute = ResolveRelativePath(root, entry.Path, entry.Name, issues);
                if (absolute is not null) resolvedPaths.Add((entry.Name, Normalize(entry.Path), absolute));
            }

            for (var left = 0; left < resolvedPaths.Count; left++)
            {
                for (var right = left + 1; right < resolvedPaths.Count; right++)
                {
                    var leftResolved = Normalize(Path.GetRelativePath(root, resolvedPaths[left].Absolute));
                    var rightResolved = Normalize(Path.GetRelativePath(root, resolvedPaths[right].Absolute));
                    if (PathsOverlap(resolvedPaths[left].Relative, resolvedPaths[right].Relative) ||
                        PathsOverlap(leftResolved, rightResolved))
                        issues.Add(new("PSWORK007", $"Workspace paths '{resolvedPaths[left].Name}' and '{resolvedPaths[right].Name}' overlap."));
                }
            }

            if (configurationPath is not null)
            {
                var configurationRelative = Normalize(Path.GetRelativePath(root, configurationPath));
                foreach (var entry in resolvedPaths)
                {
                    var resolvedRelative = Normalize(Path.GetRelativePath(root, entry.Absolute));
                    if (PathsOverlap(configurationRelative, entry.Relative) || PathsOverlap(configurationRelative, resolvedRelative))
                        issues.Add(new("PSWORK007", $"Project configuration overlaps workspace path '{entry.Name}'."));
                }
            }

            foreach (var entry in resolvedPaths)
            {
                if (!Directory.Exists(entry.Absolute))
                    issues.Add(new("PSWORK008", $"Workspace directory '{entry.Name}' is missing."));
            }
        }

        if (configurationPath is not null && File.Exists(configurationPath))
            await ValidateProjectConfigurationAsync(configurationPath, issues).ConfigureAwait(false);
        else if (configurationPath is not null)
            issues.Add(new("PSWORK009", "Referenced project configuration does not exist."));

        return new EvaluationWorkspaceValidationResult(issues.Count == 0, manifest.WorkspaceId,
            manifest.FormatVersion, manifest.ProjectConfiguration, issues);
    }

    public static async Task<EvaluationWorkspaceManifest?> ReadAsync(string workspaceDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(workspaceDirectory), EvaluationWorkspaceManifest.FileName);
        return JsonSerializer.Deserialize<EvaluationWorkspaceManifest>(
            await File.ReadAllTextAsync(path).ConfigureAwait(false), JsonOptions);
    }

    private static async Task ValidateProjectConfigurationAsync(string configurationPath,
        List<EvaluationWorkspaceIssue> issues)
    {
        ConfigurationLoadResult loaded;
        try
        {
            loaded = await new ConfigurationLoader(requireEnvironmentValues: false).LoadAsync(configurationPath).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            issues.Add(new("PSWORK010", "Referenced project configuration could not be read."));
            return;
        }

        if (!loaded.IsValid || loaded.Configuration is null)
        {
            issues.Add(new("PSWORK011", "Referenced project configuration is invalid."));
            return;
        }

        var configuration = loaded.Configuration;
        var compilation = MigrationGraphCompiler.Compile(configuration);
        if (!compilation.IsValid || compilation.Graph is null)
        {
            issues.Add(new("PSWORK012", "Referenced migration graph is invalid."));
            return;
        }

        foreach (var issue in ConnectorConfigurationValidator.Validate(configuration, compilation.Graph,
            CliComposition.Connectors()).Where(issue => issue.Severity == ValidationSeverity.Error))
            issues.Add(new(issue.Code, issue.Message));

        try
        {
            var definitions = VerificationRuleConfigurationLoader.Load(configuration);
            var registry = CliComposition.RulesFor(configuration, definitions);
            _ = registry.Resolve(definitions);
            foreach (var issue in RuleReferenceValidator.Validate(definitions, registry, compilation.Graph)
                .Where(issue => issue.Severity == ValidationSeverity.Error))
                issues.Add(new(issue.Code, issue.Message));
            _ = RecoveryPolicyConfigurationLoader.Load(configuration);
        }
        catch (VerificationRuleException exception)
        {
            issues.Add(new(exception.Code, "Referenced verification rules are invalid."));
        }
        catch (RecoveryException)
        {
            issues.Add(new("PSWORK013", "Referenced recovery policy is invalid."));
        }
    }

    private static string? ResolveRelativePath(string root, string? relativePath, string label,
        List<EvaluationWorkspaceIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            issues.Add(new("PSWORK014", $"Workspace {label} path must be a canonical relative path."));
            return null;
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            issues.Add(new("PSWORK014", $"Workspace {label} path must not contain empty, current, or parent segments."));
            return null;
        }

        if (!ConnectorPathUtilities.TryResolveContainedPath(root, relativePath, out var resolved))
        {
            issues.Add(new("PSWORK014", $"Workspace {label} path must remain inside the workspace."));
            return null;
        }

        return resolved;
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimEnd('/');

    private static EvaluationWorkspaceValidationResult Result(EvaluationWorkspaceManifest? manifest,
        IReadOnlyList<EvaluationWorkspaceIssue> issues) =>
        new(false, manifest?.WorkspaceId, manifest?.FormatVersion, manifest?.ProjectConfiguration, issues);
}
