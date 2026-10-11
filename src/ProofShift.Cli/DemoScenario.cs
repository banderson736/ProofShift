using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProofShift.Cli;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoScenarioManifest
{
    public required int ManifestVersion { get; init; }
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Domain { get; init; }
    public required DemoPackReference Pack { get; init; }
    public required string Description { get; init; }
    public required string ClaimBoundary { get; init; }
    public required string ApproximateRuntime { get; init; }
    public required DemoScenarioRequirements Requirements { get; init; }
    public required IReadOnlyList<string> SourceSystems { get; init; }
    public required IReadOnlyList<string> TargetSystems { get; init; }
    public required IReadOnlyList<string> RequiredServices { get; init; }
    public required DemoArtifactScale ArtifactScale { get; init; }
    public required DemoAcceptanceReference AcceptanceReference { get; init; }
    public string? ProjectPath { get; init; }
    public required IReadOnlyList<string> FixtureSetupSteps { get; init; }
    public required string DefectiveTargetPreparation { get; init; }
    public required string CorrectedTargetPreparation { get; init; }
    public required IReadOnlyList<DemoStageManifest> RunStages { get; init; }
    public required IReadOnlyList<DemoStageManifest> ResetStages { get; init; }
    public required IReadOnlyList<string> ReportPaths { get; init; }
    public required string ResetScope { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoPackReference
{
    public required string Id { get; init; }
    public required string Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoScenarioRequirements
{
    public required bool Docker { get; init; }
    public required string MinimumDotNetSdk { get; init; }
    public required long MinimumFreeDiskBytes { get; init; }
    public required IReadOnlyList<int> RequiredPorts { get; init; }
    public required IReadOnlyList<string> RequiredFiles { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoArtifactScale
{
    public required long SourceArtifacts { get; init; }
    public required long TargetArtifacts { get; init; }
    public required string Unit { get; init; }
    public required string Notes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoAcceptanceReference
{
    public required string IndexFile { get; init; }
    public required string ScenarioId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoStageManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Phase { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string ExpectedOutcome { get; init; }
    public required int ExpectedExitCode { get; init; }
    public required int TimeoutSeconds { get; init; }
    public string? ResultFile { get; init; }
    public string Runner { get; init; } = "proofshift-cli";
    public string? OutputFile { get; init; }
    public IReadOnlyList<DemoJsonAssertion> Assertions { get; init; } = [];
    public IReadOnlyList<DemoResultBinding> ResultBindings { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoResultBinding
{
    public required string Name { get; init; }
    public required string Pointer { get; init; }
    public required string ValueType { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DemoJsonAssertion
{
    public required string Pointer { get; init; }
    public required string ValueType { get; init; }
    public required string Expected { get; init; }
}

internal sealed record AcceptedDemoScenario(
    string ScenarioId,
    string PackId,
    string PackVersion,
    long SourceArtifacts,
    long CorrectedTargetArtifacts,
    int SeededDefects,
    int ExpectedFindings,
    int CorrectedFailures,
    int UnaccountedSources,
    int UnexplainedTargets,
    string RecoveryAssessment,
    string RecoveryRehearsal,
    string CorrectedQualification,
    int? FalseReverseDeclarations,
    string ClaimBoundary,
    double MeasuredTestSeconds,
    string RuntimeNote,
    IReadOnlyList<string> SourceConnectors,
    IReadOnlyList<string> TargetConnectors);

internal sealed record DemoPreflightCheck(string Code, bool Passed, string Message);

internal sealed record DemoPreflightResult(string ScenarioId, bool Ready, IReadOnlyList<DemoPreflightCheck> Checks);

internal sealed record DemoStageResult(string Id, string Name, string Phase, string ExpectedOutcome, int ExpectedExitCode,
    int? ActualExitCode, string Status, string? ResultFile, string? OutputFile,
    IReadOnlyDictionary<string, string> ResultBindings, string? Diagnostic,
    long? FixtureProcessElapsedMilliseconds = null, long? FixtureProcessPeakWorkingSetBytes = null);

internal sealed record DemoRunResult(string ScenarioId, string Status, string Workspace,
    IReadOnlyList<DemoStageResult> Stages, IReadOnlyList<string> Reports,
    IReadOnlyList<DemoPreflightCheck> PreflightChecks);

internal sealed class DemoManifestException(string message) : Exception(message);

internal sealed class DemoScenarioCatalog
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private DemoScenarioCatalog(string contentRoot, IReadOnlyList<DemoScenarioEntry> scenarios)
    {
        ContentRoot = contentRoot;
        Scenarios = scenarios;
    }

    public string ContentRoot { get; }
    public IReadOnlyList<DemoScenarioEntry> Scenarios { get; }

    public static DemoScenarioCatalog Load(string contentRoot)
    {
        var root = Path.GetFullPath(contentRoot);
        var scenarioRoot = Path.Combine(root, "scenarios", "demos");
        if (!Directory.Exists(scenarioRoot)) return new DemoScenarioCatalog(root, []);
        EnsureNotReparsePoint(scenarioRoot, "Demo scenario directory");

        var entries = new List<DemoScenarioEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(scenarioRoot).Order(StringComparer.Ordinal))
        {
            EnsureNotReparsePoint(directory, "Demo scenario folder");
            var manifestPath = Path.Combine(directory, "scenario.json");
            if (!File.Exists(manifestPath)) continue;
            EnsureNotReparsePoint(manifestPath, "Demo scenario manifest");

            DemoScenarioManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<DemoScenarioManifest>(File.ReadAllText(manifestPath), ManifestOptions)
                    ?? throw new DemoManifestException("Manifest JSON must contain an object.");
            }
            catch (JsonException exception)
            {
                throw new DemoManifestException($"Demo manifest '{manifestPath}' is invalid JSON: {exception.Message}");
            }

            ValidateManifest(manifest, directory, manifestPath, root);
            if (!seenIds.Add(manifest.Id))
                throw new DemoManifestException($"Duplicate demo scenario ID '{manifest.Id}'.");
            entries.Add(new DemoScenarioEntry(manifest, directory, manifestPath));
        }

        return new DemoScenarioCatalog(root, entries.OrderBy(entry => entry.Manifest.Id, StringComparer.Ordinal).ToArray());
    }

    public DemoScenarioEntry Find(string scenarioId) => Scenarios.FirstOrDefault(entry =>
        StringComparer.Ordinal.Equals(entry.Manifest.Id, scenarioId))
        ?? throw new DemoManifestException($"Demo scenario '{scenarioId}' was not found.");

    public AcceptedDemoScenario ReadAcceptanceReference(DemoScenarioEntry entry)
    {
        var reference = entry.Manifest.AcceptanceReference;
        var indexPath = DemoScenarioPaths.ResolveRelative(ContentRoot, reference.IndexFile, "Acceptance index");
        if (!File.Exists(indexPath))
            throw new DemoManifestException($"Acceptance index '{reference.IndexFile}' is missing.");
        DemoScenarioPaths.EnsureNoReparsePoints(ContentRoot, reference.IndexFile, "Acceptance index");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) ||
                !StringComparer.Ordinal.Equals(format.GetString(), "proofshift-ps010f-scenario-index-v1"))
                throw new DemoManifestException("Acceptance index format is unsupported.");
            if (!root.TryGetProperty("scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array)
                throw new DemoManifestException("Acceptance index has no scenarios array.");

            var matches = scenarios.EnumerateArray().Where(scenario =>
                scenario.TryGetProperty("scenarioId", out var id) &&
                StringComparer.Ordinal.Equals(id.GetString(), reference.ScenarioId)).ToArray();
            if (matches.Length != 1)
                throw new DemoManifestException($"Acceptance index must contain exactly one scenario '{reference.ScenarioId}'.");

            var accepted = ReadAcceptedScenario(matches[0]);
            if (!StringComparer.Ordinal.Equals(accepted.PackId, entry.Manifest.Pack.Id) ||
                !StringComparer.Ordinal.Equals(accepted.PackVersion, entry.Manifest.Pack.Version))
                throw new DemoManifestException("Demo pack identity/version does not match the accepted scenario index.");
            if (!StringComparer.Ordinal.Equals(accepted.ClaimBoundary, entry.Manifest.ClaimBoundary))
                throw new DemoManifestException("Demo claim boundary does not match the accepted scenario index.");
            if (accepted.SourceArtifacts != entry.Manifest.ArtifactScale.SourceArtifacts ||
                accepted.CorrectedTargetArtifacts != entry.Manifest.ArtifactScale.TargetArtifacts)
                throw new DemoManifestException("Demo artifact scale does not match the accepted scenario index.");
            return accepted;
        }
        catch (JsonException exception)
        {
            throw new DemoManifestException($"Acceptance index '{reference.IndexFile}' is invalid JSON: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new DemoManifestException($"Acceptance scenario '{reference.ScenarioId}' is incomplete: {exception.Message}");
        }
    }

    private static AcceptedDemoScenario ReadAcceptedScenario(JsonElement scenario)
    {
        var falseReverse = scenario.TryGetProperty("falseReverseDeclarations", out var falseReverseElement) &&
            falseReverseElement.ValueKind == JsonValueKind.Number ? falseReverseElement.GetInt32() : (int?)null;
        return new AcceptedDemoScenario(
            RequiredString(scenario, "scenarioId"), RequiredString(scenario, "packId"), RequiredString(scenario, "packVersion"),
            RequiredLong(scenario, "sourceArtifacts"), RequiredLong(scenario, "correctedTargetArtifacts"),
            RequiredInt(scenario, "seededDefects"), RequiredInt(scenario, "expectedFindings"),
            RequiredInt(scenario, "correctedFailures"), RequiredInt(scenario, "unaccountedSources"),
            RequiredInt(scenario, "unexplainedTargets"), RequiredString(scenario, "recoveryAssessment"),
            RequiredString(scenario, "recoveryRehearsal"), RequiredString(scenario, "correctedQualification"),
            falseReverse, RequiredString(scenario, "claimBoundary"),
            scenario.GetProperty("measuredTestSeconds").GetDouble(), RequiredString(scenario, "runtimeNote"),
            ReadStringArray(scenario, "sourceConnectors"), ReadStringArray(scenario, "targetConnectors"));
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        return field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()!
            : throw new InvalidOperationException($"'{name}' must be a non-empty string.");
    }

    private static int RequiredInt(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static long RequiredLong(JsonElement value, string name) => value.GetProperty(name).GetInt64();

    private static string[] ReadStringArray(JsonElement value, string name)
    {
        var field = value.GetProperty(name);
        if (field.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"'{name}' must be an array.");
        return field.EnumerateArray().Select(item => item.GetString() ??
            throw new InvalidOperationException($"'{name}' entries must be strings.")).ToArray();
    }

    private static void ValidateManifest(DemoScenarioManifest manifest, string directory, string path, string contentRoot)
    {
        if (manifest.ManifestVersion != 1) throw new DemoManifestException($"Demo manifest '{path}' uses unsupported version {manifest.ManifestVersion}.");
        if (manifest.Id.Length is < 2 or > 48 || manifest.Id.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
            manifest.Id.StartsWith('-') || manifest.Id.EndsWith('-'))
            throw new DemoManifestException($"Demo manifest '{path}' has an invalid lowercase kebab-case ID.");
        if (!StringComparer.Ordinal.Equals(Path.GetFileName(directory), manifest.Id))
            throw new DemoManifestException($"Demo manifest folder must match scenario ID '{manifest.Id}'.");
        RequireText(manifest.DisplayName, "displayName", path);
        RequireText(manifest.Domain, "domain", path);
        RequireText(manifest.Description, "description", path);
        RequireText(manifest.ClaimBoundary, "claimBoundary", path);
        RequireText(manifest.ApproximateRuntime, "approximateRuntime", path);
        RequireText(manifest.Pack.Id, "pack.id", path);
        RequireText(manifest.Pack.Version, "pack.version", path);
        RequireText(manifest.DefectiveTargetPreparation, "defectiveTargetPreparation", path);
        RequireText(manifest.CorrectedTargetPreparation, "correctedTargetPreparation", path);
        RequireText(manifest.ResetScope, "resetScope", path);
        RequireText(manifest.Requirements.MinimumDotNetSdk, "requirements.minimumDotNetSdk", path);
        if (manifest.ResetScope != "scenario-owned-only")
            throw new DemoManifestException("resetScope must be 'scenario-owned-only'.");
        if (manifest.Requirements.MinimumFreeDiskBytes < 0)
            throw new DemoManifestException("minimumFreeDiskBytes cannot be negative.");
        if (manifest.Requirements.RequiredPorts.Any(port => port is < 1 or > 65535) ||
            manifest.Requirements.RequiredPorts.Distinct().Count() != manifest.Requirements.RequiredPorts.Count)
            throw new DemoManifestException("Required ports must be unique TCP port numbers from 1 through 65535.");
        if (manifest.SourceSystems.Count == 0 || manifest.TargetSystems.Count == 0)
            throw new DemoManifestException("At least one source system and target system are required.");
        if (manifest.RequiredServices.Any(string.IsNullOrWhiteSpace))
            throw new DemoManifestException("Required service names cannot be empty.");
        if (manifest.ArtifactScale.SourceArtifacts < 0 || manifest.ArtifactScale.TargetArtifacts < 0)
            throw new DemoManifestException("Artifact scale counts cannot be negative.");
        RequireText(manifest.ArtifactScale.Unit, "artifactScale.unit", path);
        RequireText(manifest.ArtifactScale.Notes, "artifactScale.notes", path);
        if (manifest.RunStages.Count == 0) throw new DemoManifestException("At least one run stage is required.");
        if (manifest.RunStages.Select(stage => stage.Id).Distinct(StringComparer.Ordinal).Count() != manifest.RunStages.Count ||
            manifest.ResetStages.Select(stage => stage.Id).Distinct(StringComparer.Ordinal).Count() != manifest.ResetStages.Count)
            throw new DemoManifestException("Stage IDs must be unique within their stage list.");
        foreach (var stage in manifest.RunStages.Concat(manifest.ResetStages)) ValidateStage(stage, path, contentRoot);
        var resultBindingNames = manifest.RunStages.SelectMany(stage => stage.ResultBindings)
            .Select(binding => binding.Name).ToHashSet(StringComparer.Ordinal);
        if (resultBindingNames.Count != manifest.RunStages.SelectMany(stage => stage.ResultBindings).Count())
            throw new DemoManifestException("Result binding names must be unique across ordered run stages.");
        if (manifest.ProjectPath is { } projectPath)
        {
            RequireText(projectPath, "projectPath", path);
            _ = DemoScenarioPaths.ResolveRelative(contentRoot, projectPath, "Project path");
        }
        else if (manifest.RunStages.Concat(manifest.ResetStages).Any(stage =>
                     stage.Arguments.Any(argument => argument.Contains("{{project}}", StringComparison.Ordinal)) ||
                     stage.Environment.Values.Any(value => value.Contains("{{project}}", StringComparison.Ordinal))))
        {
            throw new DemoManifestException("Stages that use the {{project}} token require projectPath.");
        }
        _ = DemoScenarioPaths.ResolveRelative(contentRoot, manifest.AcceptanceReference.IndexFile, "Acceptance index");
        foreach (var file in manifest.Requirements.RequiredFiles)
            _ = DemoScenarioPaths.ResolveRelative(contentRoot, file, "Required file");
        var workspaceRoot = Path.Combine(contentRoot, ".proofshift", "demos", manifest.Id);
        foreach (var reportPath in manifest.ReportPaths)
        {
            if (string.IsNullOrWhiteSpace(reportPath)) throw new DemoManifestException("Report paths cannot be empty.");
            var normalizedReportPath = NormalizeOutputPathTokens(reportPath, resultBindingNames, "Report path");
            _ = DemoScenarioPaths.ResolveRelative(workspaceRoot, normalizedReportPath, "Report path");
        }

        foreach (var stage in manifest.RunStages.Concat(manifest.ResetStages))
        {
            foreach (var outputPath in new[] { stage.ResultFile, stage.OutputFile }.Where(value => value is not null))
            {
                if (string.IsNullOrWhiteSpace(outputPath)) throw new DemoManifestException($"Stage '{stage.Id}' output paths cannot be empty.");
                var normalizedOutputPath = NormalizeOutputPathTokens(outputPath!, resultBindingNames, $"Stage '{stage.Id}' output path");
                _ = DemoScenarioPaths.ResolveRelative(workspaceRoot, normalizedOutputPath, "Stage output path");
            }
        }
    }

    private static string NormalizeOutputPathTokens(string path, IReadOnlySet<string> resultBindingNames, string description)
    {
        var normalized = path.Replace("{{workspace}}", ".", StringComparison.Ordinal);
        foreach (var name in resultBindingNames)
            normalized = normalized.Replace($"{{{{demo.{name}}}}}", "binding", StringComparison.Ordinal);
        if (normalized.Contains("{{", StringComparison.Ordinal) || normalized.Contains("}}", StringComparison.Ordinal))
            throw new DemoManifestException($"{description} contains an unsupported metadata token.");
        return normalized;
    }

    private static void ValidateStage(DemoStageManifest stage, string manifestPath, string contentRoot)
    {
        RequireText(stage.Id, "stage.id", manifestPath);
        RequireText(stage.Name, "stage.name", manifestPath);
        RequireText(stage.Phase, "stage.phase", manifestPath);
        if (stage.Arguments.Count == 0) throw new DemoManifestException($"Stage '{stage.Id}' must declare a runner command.");
        if (stage.Runner is not ("proofshift-cli" or "dotnet-test" or "dotnet-test-module"))
            throw new DemoManifestException($"Stage '{stage.Id}' has an unsupported runner.");
        if (stage.Runner == "proofshift-cli" && stage.Arguments[0] is not ("validate" or "plan" or "inspect" or "snapshot" or "project" or "verify" or
            "evidence" or "recovery" or "report" or "compare" or "dry-run" or "packs" or "rules" or "capabilities" or
            "connectors" or "discover" or "scaffold" or "mapping" or "explain" or "config" or "init" or "demo"))
            throw new DemoManifestException($"Stage '{stage.Id}' must invoke a supported ProofShift CLI command.");
        if (stage.Runner == "proofshift-cli" && stage.Arguments[0] == "demo" && (stage.Arguments.Count < 2 ||
            stage.Arguments[1] is not ("generate" or "benchmark")))
            throw new DemoManifestException($"Stage '{stage.Id}' cannot recursively invoke a demo lifecycle command.");
        if (stage.Runner == "dotnet-test")
        {
            var projectArgumentIndex = stage.Arguments.ToList().IndexOf("--project");
            if (projectArgumentIndex < 0 || projectArgumentIndex + 1 >= stage.Arguments.Count)
                throw new DemoManifestException($"Test stage '{stage.Id}' must specify --project <repository-relative-test-project>.");
            var projectPath = stage.Arguments[projectArgumentIndex + 1];
            if (projectPath.Contains("{{", StringComparison.Ordinal) || Path.IsPathRooted(projectPath))
                throw new DemoManifestException($"Test stage '{stage.Id}' project path must be a literal repository-relative path.");
            var resolvedProject = DemoScenarioPaths.EnsureNoReparsePoints(contentRoot, projectPath, "Test project");
            if (!File.Exists(resolvedProject) || !resolvedProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                !projectPath.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
                throw new DemoManifestException($"Test stage '{stage.Id}' must target an in-repository test project.");
            if (stage.Arguments.Any(argument => argument is "--settings" or "--logger" or "--results-directory"))
                throw new DemoManifestException($"Test stage '{stage.Id}' cannot override runner output/settings paths.");
            if (!stage.Arguments.Contains("--filter-class", StringComparer.Ordinal))
                throw new DemoManifestException($"Test stage '{stage.Id}' must use --filter-class to keep fixture execution bounded.");
            if (stage.OutputFile is null)
                throw new DemoManifestException($"Test stage '{stage.Id}' must capture its runner output in outputFile.");
        }
        if (stage.Runner == "dotnet-test-module")
        {
            var modulePath = stage.Arguments[0];
            if (modulePath.Contains("{{", StringComparison.Ordinal) || Path.IsPathRooted(modulePath))
                throw new DemoManifestException($"Test stage '{stage.Id}' module path must be a literal package-relative path.");
            var resolvedModule = DemoScenarioPaths.EnsureNoReparsePoints(contentRoot, modulePath, "Test module");
            if (!File.Exists(resolvedModule) || !resolvedModule.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new DemoManifestException($"Test stage '{stage.Id}' must target an in-package test module DLL.");
            if (!stage.Arguments.Contains("-class", StringComparer.Ordinal) &&
                !stage.Arguments.Contains("-method", StringComparer.Ordinal))
                throw new DemoManifestException($"Test module stage '{stage.Id}' must use -class or -method to keep fixture execution bounded.");
            if (stage.OutputFile is null)
                throw new DemoManifestException($"Test module stage '{stage.Id}' must capture its runner output in outputFile.");
        }
        if (stage.WorkingDirectory is not ("repository" or "scenario" or "workspace") &&
            !stage.WorkingDirectory.StartsWith("workspace/", StringComparison.Ordinal))
            throw new DemoManifestException($"Stage '{stage.Id}' has an invalid workingDirectory.");
        if (stage.ExpectedOutcome is not ("success" or "expected-defective-verification" or "expected-unsafe-recovery"))
            throw new DemoManifestException($"Stage '{stage.Id}' has an unsupported expectedOutcome.");
        if ((stage.ExpectedOutcome == "success") != (stage.ExpectedExitCode == 0))
            throw new DemoManifestException($"Stage '{stage.Id}' expectedOutcome and expectedExitCode disagree.");
        if (stage.ExpectedOutcome != "success" &&
            (stage.ResultFile is null || stage.Assertions.Count == 0))
            throw new DemoManifestException($"Stage '{stage.Id}' must persist structured results and assert them for an expected defective/recovery outcome.");
        if (stage.TimeoutSeconds is < 1 or > 10800) throw new DemoManifestException($"Stage '{stage.Id}' timeoutSeconds must be between 1 and 10800.");
        if (stage.Assertions.Count > 0 && stage.ResultFile is null)
            throw new DemoManifestException($"Stage '{stage.Id}' requires resultFile when JSON assertions are configured.");
        if (stage.ResultBindings.Count > 0 && stage.ResultFile is null)
            throw new DemoManifestException($"Stage '{stage.Id}' requires resultFile when resultBindings are configured.");
        if (stage.ResultBindings.Select(binding => binding.Name).Distinct(StringComparer.Ordinal).Count() != stage.ResultBindings.Count)
            throw new DemoManifestException($"Stage '{stage.Id}' result binding names must be unique.");
        foreach (var binding in stage.ResultBindings)
        {
            if (binding.Name.Length == 0 || binding.Name.Any(character =>
                    !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new DemoManifestException($"Stage '{stage.Id}' has an invalid result binding name.");
            if (!binding.Pointer.StartsWith('/') || binding.Pointer == "/")
                throw new DemoManifestException($"Stage '{stage.Id}' binding pointers must be non-root JSON Pointers.");
            if (binding.ValueType is not ("string" or "number" or "boolean"))
                throw new DemoManifestException($"Stage '{stage.Id}' binding valueType must be string, number, or boolean.");
        }
        foreach (var assertion in stage.Assertions)
        {
            if (!assertion.Pointer.StartsWith('/') || assertion.Pointer == "/")
                throw new DemoManifestException($"Stage '{stage.Id}' assertion pointers must be non-root JSON Pointers.");
            if (assertion.ValueType is not ("string" or "number" or "boolean"))
                throw new DemoManifestException($"Stage '{stage.Id}' assertion valueType must be string, number, or boolean.");
        }
        foreach (var pair in stage.Environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                throw new DemoManifestException($"Stage '{stage.Id}' has an invalid environment variable declaration.");
            if (pair.Key.StartsWith("PROOFSHIFT_DEMO_", StringComparison.OrdinalIgnoreCase))
                throw new DemoManifestException($"Stage '{stage.Id}' cannot override reserved ProofShift demo environment variables.");
        }
    }

    private static void RequireText(string? value, string field, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DemoManifestException($"Demo manifest '{path}' requires '{field}'.");
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new DemoManifestException($"{description} cannot be a symbolic link or reparse point: '{path}'.");
    }
}

internal sealed record DemoScenarioEntry(DemoScenarioManifest Manifest, string Directory, string ManifestPath);

internal static class DemoScenarioPaths
{
    public static string EnsureNoReparsePoints(string root, string relativePath, string description)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = ResolveRelative(fullRoot, relativePath, description);
        var rootAttributes = File.GetAttributes(fullRoot);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new DemoManifestException($"{description} root cannot be a symbolic link or reparse point.");

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DemoManifestException($"{description} cannot traverse a symbolic link or reparse point.");
        }
        return fullPath;
    }

    public static string ResolveRelative(string root, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new DemoManifestException($"{description} must be a non-empty relative path.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!IsWithin(fullRoot, fullPath)) throw new DemoManifestException($"{description} escapes its permitted root.");
        return fullPath;
    }

    public static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(prefix, comparison);
    }
}