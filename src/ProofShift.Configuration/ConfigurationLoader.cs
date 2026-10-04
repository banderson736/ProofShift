using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProofShift.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ProofShift.Configuration;

public static class ConfigurationIssueCodes
{
    public const string MissingReferencedFile = "PSCFG001";
    public const string UnsupportedVersion = "PSCFG002";
    public const string DuplicateSystemId = "PSCFG003";
    public const string MissingRequiredProperty = "PSCFG004";
    public const string InvalidSystemRole = "PSCFG005";
    public const string DuplicateStorageEndpointId = "PSCFG006";
    public const string InvalidRelativePath = "PSCFG007";
    public const string MissingEnvironmentVariable = "PSCFG008";
    public const string InvalidYaml = "PSCFG009";
    public const string InvalidIdentifier = "PSCFG010";
    public const string DuplicateMappingKey = "PSCFG011";
    public const string InlineSecretNotAllowed = "PSCFG012";
    public const string FileReadFailure = "PSCFG013";
}

public sealed class ConfigurationLoader
{
    private const int SupportedVersion = 1;
    private readonly IEnvironmentVariableProvider _environment;

    public ConfigurationLoader(IEnvironmentVariableProvider? environment = null) =>
        _environment = environment ?? new ProcessEnvironmentVariableProvider();

    public async Task<ConfigurationLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ConfigurationValidationIssue>();
        if (string.IsNullOrWhiteSpace(path))
        {
            AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty, "Configuration path is required.");
            return CreateResult(null, issues);
        }

        string fullRootPath;
        try
        {
            fullRootPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddIssue(issues, ConfigurationIssueCodes.InvalidRelativePath, "Root configuration path is invalid.");
            return CreateResult(null, issues);
        }

        var rootFileName = Path.GetFileName(fullRootPath);
        var rootDirectory = Path.GetDirectoryName(fullRootPath) ?? Directory.GetCurrentDirectory();
        if (!File.Exists(fullRootPath))
        {
            AddIssue(issues, ConfigurationIssueCodes.MissingReferencedFile, "Root configuration file was not found.", rootFileName);
            return CreateResult(null, issues);
        }

        var parsedRoot = await ReadYamlAsync(fullRootPath, rootFileName, issues, cancellationToken).ConfigureAwait(false);
        if (parsedRoot is null)
        {
            return CreateResult(null, issues);
        }

        InspectYaml(parsedRoot.Node, rootFileName, string.Empty, issues);
        var root = ParseRoot(parsedRoot.Node, rootFileName, issues);
        if (root is null)
        {
            return CreateResult(null, issues);
        }

        var parsedFiles = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);
        var referencedFiles = new List<ReferencedConfigurationFile>();
        var systemConfigurations = new List<SystemConfigurationDto>();
        var systemDefinitions = new List<SystemDefinition>();
        var systemIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var systemReference in root.SystemReferences.OrderBy(reference => reference.Name, StringComparer.Ordinal))
        {
            var parsed = await ReadReferencedFileAsync(
                rootDirectory,
                rootFileName,
                systemReference.Path,
                ConfigurationFileKind.System,
                $"systems.{systemReference.Name}.file",
                parsedFiles,
                issues,
                cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                continue;
            }

            InspectYaml(parsed.Node, parsed.RelativePath, string.Empty, issues);
            var system = ParseSystem(parsed.Node, parsed.RelativePath, issues);
            if (system is null)
            {
                continue;
            }

            systemConfigurations.Add(system);
            if (IsIdentifier(system.Id))
            {
                if (systemIds.TryGetValue(system.Id!, out var firstFile))
                {
                    AddIssue(
                        issues,
                        ConfigurationIssueCodes.DuplicateSystemId,
                        $"Duplicate system ID '{system.Id}'.",
                        parsed.RelativePath,
                        "id");
                }
                else
                {
                    systemIds.Add(system.Id!, parsed.RelativePath);
                }
            }

            referencedFiles.Add(new ReferencedConfigurationFile(
                ConfigurationFileKind.System,
                parsed.RelativePath,
                parsed.CanonicalContent,
                parsed.Document));
        }

        var otherReferences = new[]
        {
            (Kind: ConfigurationFileKind.MigrationGraph, Name: "migration.graph", Path: root.MigrationGraphFile),
            (Kind: ConfigurationFileKind.VerificationRules, Name: "verification.rules", Path: root.VerificationRulesFile),
            (Kind: ConfigurationFileKind.RecoveryPolicy, Name: "recovery.policy", Path: root.RecoveryPolicyFile)
        };

        foreach (var reference in otherReferences)
        {
            if (reference.Path is null)
            {
                continue;
            }

            var parsed = await ReadReferencedFileAsync(
                rootDirectory,
                rootFileName,
                reference.Path,
                reference.Kind,
                reference.Name,
                parsedFiles,
                issues,
                cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                continue;
            }

            InspectYaml(parsed.Node, parsed.RelativePath, string.Empty, issues);
            referencedFiles.Add(new ReferencedConfigurationFile(
                reference.Kind,
                parsed.RelativePath,
                parsed.CanonicalContent,
                parsed.Document));
        }

        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return CreateResult(null, issues, root.Dto.Version);
        }

        foreach (var system in systemConfigurations)
        {
            var endpoints = system.StorageEndpoints.Select(endpoint => new StorageEndpointDefinition(
                new StorageEndpointId(endpoint.Id),
                new ConnectorId(endpoint.Connector!),
                endpoint.Settings.Select(setting => new KeyValuePair<string, string>(
                    setting.Key,
                    SettingIdentity(setting.Value))))).ToArray();
            systemDefinitions.Add(new SystemDefinition(
                new SystemId(system.Id!),
                system.Name!,
                ParseSystemRole(system.Role!),
                endpoints));
        }

        var canonicalConfiguration = BuildCanonicalConfiguration(rootFileName, parsedRoot.CanonicalContent, parsedFiles.Values);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalConfiguration))).ToLowerInvariant();
        var loaded = new LoadedProjectConfiguration(
            root.Dto,
            systemConfigurations,
            systemDefinitions,
            referencedFiles,
            canonicalConfiguration,
            hash);
        return CreateResult(loaded, issues);
    }

    private static async Task<ParsedFile?> ReadReferencedFileAsync(
        string rootDirectory,
        string rootFileName,
        string configuredPath,
        ConfigurationFileKind kind,
        string referenceName,
        IDictionary<string, ParsedFile> parsedFiles,
        ICollection<ConfigurationValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePath(rootDirectory, configuredPath, out var fullPath, out var relativePath))
        {
            AddIssue(
                issues,
                ConfigurationIssueCodes.InvalidRelativePath,
                "Referenced file path must be relative and remain inside the project configuration directory.",
                rootFileName,
                referenceName);
            return null;
        }

        if (parsedFiles.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        if (!File.Exists(fullPath))
        {
            AddIssue(issues, ConfigurationIssueCodes.MissingReferencedFile, "Referenced configuration file was not found.", relativePath);
            return null;
        }

        var parsed = await ReadYamlAsync(fullPath, relativePath, issues, cancellationToken).ConfigureAwait(false);
        if (parsed is null)
        {
            return null;
        }

        var withPath = parsed with { RelativePath = relativePath };
        parsedFiles.Add(relativePath, withPath);
        return withPath;
    }

    private static async Task<ParsedFile?> ReadYamlAsync(
        string fullPath,
        string relativePath,
        ICollection<ConfigurationValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddIssue(issues, ConfigurationIssueCodes.FileReadFailure, "Configuration file could not be read.", relativePath);
            return null;
        }

        try
        {
            if (InspectDuplicateMappingKeys(content, relativePath, issues))
            {
                return null;
            }

            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                AddIssue(issues, ConfigurationIssueCodes.InvalidYaml, "Configuration file must contain one YAML mapping document.", relativePath);
                return null;
            }

            return new ParsedFile(
                mapping,
                CanonicalYamlSerializer.Serialize(mapping),
                ToConfigurationDocumentNode(mapping));
        }
        catch (Exception exception) when (exception is YamlDotNet.Core.YamlException or ArgumentException or InvalidDataException)
        {
            AddIssue(issues, ConfigurationIssueCodes.InvalidYaml, "Configuration file contains invalid YAML syntax.", relativePath);
            return null;
        }
    }

    private static ParsedRoot? ParseRoot(
        YamlMappingNode node,
        string file,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var versionText = Scalar(Find(node, "proofshift"));
        int? version = int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion)
            ? parsedVersion
            : null;
        if (version is null)
        {
            AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty, "Required configuration version is missing or invalid.", file, "proofshift");
        }
        else if (version != SupportedVersion)
        {
            AddIssue(
                issues,
                ConfigurationIssueCodes.UnsupportedVersion,
                $"Unsupported ProofShift configuration version '{version}'. Supported version is {SupportedVersion}.",
                file,
                "proofshift");
        }

        var projectNode = RequiredMapping(node, "project", file, issues);
        var project = projectNode is null
            ? null
            : new ProjectConfigurationDto(
                RequiredScalar(projectNode, "id", file, "project.id", issues),
                RequiredScalar(projectNode, "name", file, "project.name", issues));
        ValidateIdentifier(project?.Id, file, "project.id", issues);

        PackConfigurationDto? pack = null;
        var packNode = Mapping(Find(node, "pack"));
        if (packNode is not null)
        {
            pack = new PackConfigurationDto(
                RequiredScalar(packNode, "id", file, "pack.id", issues),
                RequiredScalar(packNode, "version", file, "pack.version", issues));
            ValidateIdentifier(pack.Id, file, "pack.id", issues);
        }

        var systemReferences = new List<SystemFileReference>();
        var systemsNode = RequiredMapping(node, "systems", file, issues);
        if (systemsNode is not null)
        {
            if (systemsNode.Children.Count == 0)
            {
                AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty, "At least one system file reference is required.", file, "systems");
            }

            foreach (var pair in MappingEntries(systemsNode))
            {
                var name = Scalar(pair.Key);
                if (!IsIdentifier(name))
                {
                    AddIssue(issues, ConfigurationIssueCodes.InvalidIdentifier, "System reference name is invalid.", file, "systems");
                    continue;
                }

                var referenceNode = Mapping(pair.Value);
                var referencePath = referenceNode is null
                    ? null
                    : RequiredScalar(referenceNode, "file", file, $"systems.{name}.file", issues);
                if (referencePath is null)
                {
                    if (referenceNode is null)
                    {
                        AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty, "System reference must contain a file path.", file, $"systems.{name}");
                    }

                    continue;
                }

                systemReferences.Add(new SystemFileReference(name!, referencePath));
            }
        }

        var migrationNode = RequiredMapping(node, "migration", file, issues);
        var migrationFile = migrationNode is null
            ? null
            : RequiredScalar(migrationNode, "graph", file, "migration.graph", issues);
        var verificationNode = RequiredMapping(node, "verification", file, issues);
        var verificationFile = verificationNode is null
            ? null
            : RequiredScalar(verificationNode, "rules", file, "verification.rules", issues);
        var recoveryNode = RequiredMapping(node, "recovery", file, issues);
        var recoveryFile = recoveryNode is null
            ? null
            : RequiredScalar(recoveryNode, "policy", file, "recovery.policy", issues);

        var systemFiles = systemReferences.Select(reference =>
            new KeyValuePair<string, string>(reference.Name, reference.Path));
        return new ParsedRoot(
            new RootConfigurationDto(version, project, pack, systemFiles, migrationFile, verificationFile, recoveryFile),
            systemReferences,
            migrationFile,
            verificationFile,
            recoveryFile);
    }

    private static SystemConfigurationDto? ParseSystem(
        YamlMappingNode node,
        string file,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var id = RequiredScalar(node, "id", file, "id", issues);
        var name = RequiredScalar(node, "name", file, "name", issues);
        var role = RequiredScalar(node, "role", file, "role", issues);
        ValidateIdentifier(id, file, "id", issues);

        if (role is not null && !TryParseSystemRole(role, out _))
        {
            AddIssue(issues, ConfigurationIssueCodes.InvalidSystemRole, $"Invalid system role '{role}'.", file, "role");
        }

        var storageNode = RequiredMapping(node, "storage", file, issues);
        var endpoints = new List<StorageEndpointConfigurationDto>();
        if (storageNode is not null)
        {
            if (storageNode.Children.Count == 0)
            {
                AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty, "At least one storage endpoint is required.", file, "storage");
            }

            var endpointIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in MappingEntries(storageNode))
            {
                var endpointId = Scalar(pair.Key);
                if (!IsIdentifier(endpointId))
                {
                    AddIssue(issues, ConfigurationIssueCodes.InvalidIdentifier, "Storage endpoint ID is invalid.", file, "storage");
                    continue;
                }

                if (!endpointIds.Add(endpointId!))
                {
                    AddIssue(issues, ConfigurationIssueCodes.DuplicateStorageEndpointId,
                        $"Duplicate storage endpoint ID '{endpointId}'.", file, $"storage.{endpointId}");
                }

                var endpointNode = Mapping(pair.Value);
                if (endpointNode is null)
                {
                    AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
                        "Storage endpoint must be a mapping with a connector and configuration.", file, $"storage.{endpointId}");
                    continue;
                }

                var connector = RequiredScalar(endpointNode, "connector", file, $"storage.{endpointId}.connector", issues);
                ValidateIdentifier(connector, file, $"storage.{endpointId}.connector", issues);
                var settings = ParseSettings(endpointNode, file, $"storage.{endpointId}", issues);
                if (settings.Count == 0)
                {
                    AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
                        "Storage endpoint configuration is required.", file, $"storage.{endpointId}");
                }

                endpoints.Add(new StorageEndpointConfigurationDto(endpointId!, connector, settings));
            }
        }

        return new SystemConfigurationDto(id, name, role, endpoints);
    }

    private static List<KeyValuePair<string, ConfigurationSetting>> ParseSettings(
        YamlMappingNode endpoint,
        string file,
        string basePath,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var settings = new SortedDictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        foreach (var pair in MappingEntries(endpoint))
        {
            var key = Scalar(pair.Key);
            if (key is null || string.Equals(key, "connector", StringComparison.Ordinal))
            {
                continue;
            }

            AddSettings(pair.Value, key, file, $"{basePath}.{key}", settings, issues);
        }

        return settings.Select(pair => new KeyValuePair<string, ConfigurationSetting>(pair.Key, pair.Value)).ToList();
    }

    private static void AddSettings(
        YamlNode node,
        string settingPath,
        string file,
        string issuePath,
        IDictionary<string, ConfigurationSetting> settings,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (node is YamlMappingNode mapping)
        {
            var hasEnvironment = Find(mapping, "env") is not null;
            var hasSecret = Find(mapping, "secret") is not null;
            var environment = Scalar(Find(mapping, "env"));
            var secret = Scalar(Find(mapping, "secret"));
            if (hasEnvironment || hasSecret)
            {
                if (mapping.Children.Count != 1 || hasEnvironment == hasSecret)
                {
                    AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
                        "Environment-backed configuration must contain exactly one env or secret reference.", file, issuePath);
                    return;
                }

                AddEnvironmentSetting(environment ?? secret!, hasSecret, settingPath, file, issuePath, settings, issues);
                return;
            }

            foreach (var pair in MappingEntries(mapping))
            {
                var key = Scalar(pair.Key);
                if (key is not null)
                {
                    AddSettings(pair.Value, $"{settingPath}.{key}", file, $"{issuePath}.{key}", settings, issues);
                }
            }

            return;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                AddSettings(sequence.Children[index], $"{settingPath}[{index}]", file, $"{issuePath}[{index}]", settings, issues);
            }

            return;
        }

        if (node is YamlScalarNode scalar)
        {
            settings[settingPath] = new LiteralConfigurationSetting(scalar.Value ?? string.Empty);
        }
    }

    private static void AddEnvironmentSetting(
        string name,
        bool isSecret,
        string settingPath,
        string file,
        string issuePath,
        IDictionary<string, ConfigurationSetting> settings,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (!IsValidEnvironmentName(name))
        {
            AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
                "Environment reference name is missing or invalid.", file, issuePath);
            return;
        }

        var reference = new EnvironmentVariableReference(name);
        settings[settingPath] = isSecret
            ? new SecretConfigurationSetting(reference)
            : new EnvironmentConfigurationSetting(reference);
    }

    private static bool InspectDuplicateMappingKeys(
        string content,
        string file,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var hasDuplicates = false;
        using var reader = new StringReader(content);
        var parser = new Parser(reader);
        var frames = new Stack<YamlEventFrame>();

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart:
                    frames.Push(new YamlMappingFrame(ConsumeChildPath(frames)));
                    break;
                case MappingEnd:
                    if (frames.Count > 0)
                    {
                        frames.Pop();
                    }

                    break;
                case SequenceStart:
                    frames.Push(new YamlSequenceFrame(ConsumeChildPath(frames)));
                    break;
                case SequenceEnd:
                    if (frames.Count > 0)
                    {
                        frames.Pop();
                    }

                    break;
                case Scalar scalar when frames.TryPeek(out var frame) && frame is YamlMappingFrame mapping:
                    if (mapping.ExpectingKey)
                    {
                        var key = scalar.Value ?? string.Empty;
                        if (!mapping.Keys.Add(key))
                        {
                            hasDuplicates = true;
                            var code = mapping.Path == "storage" || mapping.Path.EndsWith(".storage", StringComparison.Ordinal)
                                ? ConfigurationIssueCodes.DuplicateStorageEndpointId
                                : ConfigurationIssueCodes.DuplicateMappingKey;
                            AddIssue(issues, code, "Duplicate YAML mapping key.", file, JoinPath(mapping.Path, key));
                        }

                        mapping.PendingKey = key;
                        mapping.ExpectingKey = false;
                    }
                    else
                    {
                        mapping.PendingKey = null;
                        mapping.ExpectingKey = true;
                    }

                    break;
                default:
                    if (frames.TryPeek(out var parent) && parent is YamlMappingFrame parentMapping && !parentMapping.ExpectingKey)
                    {
                        parentMapping.PendingKey = null;
                        parentMapping.ExpectingKey = true;
                    }
                    else if (frames.TryPeek(out parent) && parent is YamlSequenceFrame sequence)
                    {
                        sequence.NextIndex++;
                    }

                    break;
            }
        }

        return hasDuplicates;
    }

    private static string ConsumeChildPath(Stack<YamlEventFrame> frames)
    {
        if (!frames.TryPeek(out var parent))
        {
            return string.Empty;
        }

        if (parent is YamlMappingFrame mapping)
        {
            var childPath = JoinPath(mapping.Path, mapping.PendingKey ?? string.Empty);
            mapping.PendingKey = null;
            mapping.ExpectingKey = true;
            return childPath;
        }

        if (parent is YamlSequenceFrame sequence)
        {
            return $"{sequence.Path}[{sequence.NextIndex++}]";
        }

        return string.Empty;
    }

    private void InspectYaml(
        YamlNode node,
        string file,
        string path,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (node is YamlMappingNode mapping)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                var key = Scalar(pair.Key);
                var currentPath = key is null ? path : JoinPath(path, key);
                if (key is not null && !seen.Add(key))
                {
                    var code = path.EndsWith("storage", StringComparison.Ordinal)
                        ? ConfigurationIssueCodes.DuplicateStorageEndpointId
                        : ConfigurationIssueCodes.DuplicateMappingKey;
                    AddIssue(issues, code, "Duplicate YAML mapping key.", file, currentPath);
                }

                if (key is not null && IsInlineSecretKey(key, pair.Value))
                {
                    AddIssue(issues, ConfigurationIssueCodes.InlineSecretNotAllowed,
                        "Inline secret material is not allowed; use an environment or secret reference.", file, currentPath);
                }

                if (key == "connection" && pair.Value is YamlScalarNode)
                {
                    AddIssue(issues, ConfigurationIssueCodes.InlineSecretNotAllowed,
                        "Connection values must use an environment or secret reference.", file, currentPath);
                }

                if (key is "env" or "secret")
                {
                    var name = Scalar(pair.Value);
                    if (!IsValidEnvironmentName(name))
                    {
                        AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
                            "Environment reference name is missing or invalid.", file, currentPath);
                    }
                    else if (string.IsNullOrWhiteSpace(_environment.GetValue(name!)))
                    {
                        AddIssue(issues, ConfigurationIssueCodes.MissingEnvironmentVariable,
                            "Required environment variable is unavailable.", file, currentPath);
                    }
                }

                InspectYaml(pair.Value, file, currentPath, issues);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                InspectYaml(sequence.Children[index], file, $"{path}[{index}]", issues);
            }
        }
    }

    private static bool IsInlineSecretKey(string key, YamlNode value)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        var looksSensitive = normalized.Contains("password", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.Ordinal) ||
            normalized.Contains("connectionstring", StringComparison.Ordinal) ||
            normalized.Contains("apikey", StringComparison.Ordinal) ||
            normalized.Contains("secret", StringComparison.Ordinal) && normalized is not "secret" and not "secretref" and not "secretreference";
        if (!looksSensitive || normalized.EndsWith("ref", StringComparison.Ordinal) ||
            normalized.EndsWith("reference", StringComparison.Ordinal) || normalized.EndsWith("env", StringComparison.Ordinal))
        {
            return false;
        }

        return !IsEnvironmentReferenceNode(value);
    }

    private static bool IsEnvironmentReferenceNode(YamlNode node)
    {
        if (node is not YamlMappingNode mapping || mapping.Children.Count != 1)
        {
            return false;
        }

        var pair = mapping.Children.Single();
        return Scalar(pair.Key) is "env" or "secret" && IsValidEnvironmentName(Scalar(pair.Value));
    }

    private static bool IsValidEnvironmentName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        return name.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string SettingIdentity(ConfigurationSetting setting) => setting switch
    {
        LiteralConfigurationSetting literal => literal.Value,
        EnvironmentConfigurationSetting environment => $"env:{environment.Reference.Name}",
        SecretConfigurationSetting secret => $"secret:{secret.Reference.Name}",
        _ => throw new InvalidOperationException("Unsupported configuration setting type.")
    };

    private static bool TryParseSystemRole(string value, out SystemRole role)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "source":
                role = SystemRole.Source;
                return true;
            case "target":
                role = SystemRole.Target;
                return true;
            case "shadow-target":
                role = SystemRole.ShadowTarget;
                return true;
            case "archive":
                role = SystemRole.Archive;
                return true;
            default:
                role = default;
                return false;
        }
    }

    private static SystemRole ParseSystemRole(string value) =>
        TryParseSystemRole(value, out var role)
            ? role
            : throw new InvalidOperationException("System role was not validated.");

    private static string BuildCanonicalConfiguration(
        string rootFileName,
        string canonicalRoot,
        IEnumerable<ParsedFile> referencedFiles)
    {
        var builder = new StringBuilder(CanonicalYamlSerializer.FormatVersion).Append('\n');
        builder.Append(JsonSerializer.Serialize(NormalizeRelativePath(rootFileName)))
            .Append('=')
            .Append(canonicalRoot)
            .Append('\n');

        foreach (var file in referencedFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(JsonSerializer.Serialize(file.RelativePath))
                .Append('=')
                .Append(file.CanonicalContent)
                .Append('\n');
        }

        return builder.ToString();
    }

    private static bool TryResolvePath(
        string rootDirectory,
        string configuredPath,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath) || Path.IsPathRooted(configuredPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var candidate = Path.GetFullPath(Path.Combine(root, configuredPath));
            var relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }

            if (!ResolveExistingLinks(root, relative, out candidate))
            {
                return false;
            }

            fullPath = candidate;
            relativePath = NormalizeRelativePath(relative);
            return relativePath.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ResolveExistingLinks(string root, string relative, out string candidate)
    {
        candidate = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            candidate = Path.Combine(candidate, segment);
            FileSystemInfo info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : new FileInfo(candidate);
            if (!info.Exists || info.LinkTarget is null)
            {
                continue;
            }

            candidate = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            var physicalRelative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(physicalRelative) || physicalRelative == ".." ||
                physicalRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                physicalRelative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static YamlNode? Find(YamlMappingNode? mapping, string name)
    {
        if (mapping is null)
        {
            return null;
        }

        foreach (var pair in mapping.Children)
        {
            if (string.Equals(Scalar(pair.Key), name, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IEnumerable<KeyValuePair<YamlNode, YamlNode>> MappingEntries(YamlMappingNode mapping) =>
        mapping.Children.OrderBy(pair => Scalar(pair.Key) ?? string.Empty, StringComparer.Ordinal);

    private static ConfigurationDocumentNode ToConfigurationDocumentNode(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new ConfigurationMappingNode(MappingEntries(mapping).Select(pair =>
            new KeyValuePair<string, ConfigurationDocumentNode>(
                Scalar(pair.Key) ?? CanonicalYamlSerializer.Serialize(pair.Key),
                ToConfigurationDocumentNode(pair.Value)))),
        YamlSequenceNode sequence => new ConfigurationSequenceNode(sequence.Children.Select(ToConfigurationDocumentNode)),
        YamlScalarNode scalar => ToConfigurationScalarNode(scalar),
        _ => throw new InvalidDataException("Unsupported YAML node type.")
    };

    private static ConfigurationScalarNode ToConfigurationScalarNode(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain)
        {
            var normalized = value?.ToLowerInvariant();
            if (normalized is "null" or "~")
            {
                return new ConfigurationScalarNode(ConfigurationScalarKind.Null, null);
            }

            if (normalized is "true" or "false")
            {
                return new ConfigurationScalarNode(ConfigurationScalarKind.Boolean, normalized);
            }

            if (value is not null && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                return new ConfigurationScalarNode(ConfigurationScalarKind.Number, value);
            }
        }

        return new ConfigurationScalarNode(ConfigurationScalarKind.Text, value ?? string.Empty);
    }

    private static string? Scalar(YamlNode? node) =>
        node is YamlScalarNode scalar ? scalar.Value : null;

    private static YamlMappingNode? Mapping(YamlNode? node) => node as YamlMappingNode;

    private static YamlMappingNode? RequiredMapping(
        YamlMappingNode parent,
        string property,
        string file,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var node = Find(parent, property);
        if (node is YamlMappingNode mapping)
        {
            return mapping;
        }

        AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
            "Required configuration mapping is missing or invalid.", file, property);
        return null;
    }

    private static string? RequiredScalar(
        YamlMappingNode parent,
        string property,
        string file,
        string path,
        ICollection<ConfigurationValidationIssue> issues)
    {
        var value = Scalar(Find(parent, property));
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        AddIssue(issues, ConfigurationIssueCodes.MissingRequiredProperty,
            "Required configuration value is missing or empty.", file, path);
        return null;
    }

    private static void ValidateIdentifier(
        string? value,
        string file,
        string path,
        ICollection<ConfigurationValidationIssue> issues)
    {
        if (value is not null && !IsIdentifier(value))
        {
            AddIssue(issues, ConfigurationIssueCodes.InvalidIdentifier, "Identifier contains unsupported characters.", file, path);
        }
    }

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !(char.IsAsciiLetterOrDigit(value[0])))
        {
            return false;
        }

        return value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static string JoinPath(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static void AddIssue(
        ICollection<ConfigurationValidationIssue> issues,
        string code,
        string message,
        string? file = null,
        string? path = null,
        ValidationSeverity severity = ValidationSeverity.Error) =>
        issues.Add(new ConfigurationValidationIssue(code, severity, message, file, path));

    private static ConfigurationLoadResult CreateResult(
        LoadedProjectConfiguration? configuration,
        IEnumerable<ConfigurationValidationIssue> issues,
        int? configurationVersion = null) =>
        new(configuration, issues
            .OrderBy(issue => issue.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal),
            configurationVersion);

    private abstract class YamlEventFrame(string path)
    {
        public string Path { get; } = path;
    }

    private sealed class YamlMappingFrame(string path) : YamlEventFrame(path)
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public bool ExpectingKey { get; set; } = true;
        public string? PendingKey { get; set; }
    }

    private sealed class YamlSequenceFrame(string path) : YamlEventFrame(path)
    {
        public int NextIndex { get; set; }
    }

    private sealed record SystemFileReference(string Name, string Path);

    private sealed record ParsedRoot(
        RootConfigurationDto Dto,
        IReadOnlyCollection<SystemFileReference> SystemReferences,
        string? MigrationGraphFile,
        string? VerificationRulesFile,
        string? RecoveryPolicyFile);

    private sealed record ParsedFile(
        YamlMappingNode Node,
        string CanonicalContent,
        ConfigurationDocumentNode Document)
    {
        public string RelativePath { get; init; } = string.Empty;
    }
}
