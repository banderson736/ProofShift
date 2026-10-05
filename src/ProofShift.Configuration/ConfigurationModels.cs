using ProofShift.Domain;

namespace ProofShift.Configuration;

public enum ValidationSeverity
{
    Error,
    Warning
}

public sealed record ConfigurationValidationIssue
{
    public string Code { get; }
    public ValidationSeverity Severity { get; }
    public string Message { get; }
    public string? File { get; }
    public string? Path { get; }
    public int? Line { get; }
    public int? Column { get; }

    public ConfigurationValidationIssue(
        string code,
        ValidationSeverity severity,
        string message,
        string? file = null,
        string? path = null,
        int? line = null,
        int? column = null)
    {
        Code = Required(code, nameof(code));
        Severity = severity;
        Message = Required(message, nameof(message));
        File = NormalizeOptional(file);
        Path = NormalizeOptional(path);
        Line = line;
        Column = column;
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ConfigurationLoadResult
{
    public LoadedProjectConfiguration? Configuration { get; }
    public int? ConfigurationVersion { get; }
    public DomainList<ConfigurationValidationIssue> Issues { get; }
    public bool IsValid => Configuration is not null && Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    public ConfigurationLoadResult(
        LoadedProjectConfiguration? configuration,
        IEnumerable<ConfigurationValidationIssue> issues,
        int? configurationVersion = null)
    {
        Configuration = configuration;
        ConfigurationVersion = configuration?.Root.Version ?? configurationVersion;
        Issues = new DomainList<ConfigurationValidationIssue>(issues);
    }
}

public sealed record ProjectConfigurationDto
{
    public string? Id { get; }
    public string? Name { get; }

    public ProjectConfigurationDto(string? id, string? name)
    {
        Id = id;
        Name = name;
    }
}

public sealed record PackConfigurationDto
{
    public string? Id { get; }
    public string? Version { get; }

    public PackConfigurationDto(string? id, string? version)
    {
        Id = id;
        Version = version;
    }
}

public sealed record RootConfigurationDto
{
    public int? Version { get; }
    public ProjectConfigurationDto? Project { get; }
    public PackConfigurationDto? Pack { get; }
    public DomainList<PackConfigurationDto> Packs { get; }
    public bool UsesExplicitPackList { get; }
    public DomainDictionary<string> SystemFiles { get; }
    public string? MigrationGraphFile { get; }
    public string? VerificationRulesFile { get; }
    public string? RecoveryPolicyFile { get; }

    public RootConfigurationDto(
        int? version,
        ProjectConfigurationDto? project,
        PackConfigurationDto? pack,
        IEnumerable<KeyValuePair<string, string>> systemFiles,
        string? migrationGraphFile,
        string? verificationRulesFile,
        string? recoveryPolicyFile,
        IEnumerable<PackConfigurationDto>? packs = null)
    {
        Version = version;
        Project = project;
        Pack = pack;
        Packs = new DomainList<PackConfigurationDto>(packs ?? (pack is null ? [] : [pack]));
        UsesExplicitPackList = packs is not null;
        SystemFiles = new DomainDictionary<string>(systemFiles);
        MigrationGraphFile = migrationGraphFile;
        VerificationRulesFile = verificationRulesFile;
        RecoveryPolicyFile = recoveryPolicyFile;
    }
}

public sealed record SystemConfigurationDto
{
    public string? Id { get; }
    public string? Name { get; }
    public string? Role { get; }
    public DomainList<StorageEndpointConfigurationDto> StorageEndpoints { get; }

    public SystemConfigurationDto(
        string? id,
        string? name,
        string? role,
        IEnumerable<StorageEndpointConfigurationDto> storageEndpoints)
    {
        Id = id;
        Name = name;
        Role = role;
        StorageEndpoints = new DomainList<StorageEndpointConfigurationDto>(storageEndpoints);
    }
}

public abstract record ConfigurationSetting;

public sealed record LiteralConfigurationSetting : ConfigurationSetting
{
    public string Value { get; }

    public LiteralConfigurationSetting(string value) => Value = value;
}

public sealed record EnvironmentConfigurationSetting : ConfigurationSetting
{
    public EnvironmentVariableReference Reference { get; }

    public EnvironmentConfigurationSetting(EnvironmentVariableReference reference) => Reference = reference;
}

public sealed record SecretConfigurationSetting : ConfigurationSetting
{
    public EnvironmentVariableReference Reference { get; }

    public SecretConfigurationSetting(EnvironmentVariableReference reference) => Reference = reference;
}

public sealed record StorageEndpointConfigurationDto
{
    public string Id { get; }
    public string? Connector { get; }
    public DomainDictionary<ConfigurationSetting> Settings { get; }

    public StorageEndpointConfigurationDto(
        string id,
        string? connector,
        IEnumerable<KeyValuePair<string, ConfigurationSetting>> settings)
    {
        Id = id;
        Connector = connector;
        Settings = new DomainDictionary<ConfigurationSetting>(settings);
    }
}

public enum ConfigurationFileKind
{
    System,
    MigrationGraph,
    VerificationRules,
    RecoveryPolicy
}

public enum ConfigurationScalarKind
{
    Text,
    Number,
    Boolean,
    Null
}

public abstract record ConfigurationDocumentNode
{
    public int? Line { get; init; }
    public int? Column { get; init; }
}

public sealed record ConfigurationMappingNode : ConfigurationDocumentNode
{
    public DomainDictionary<ConfigurationDocumentNode> Values { get; }

    public ConfigurationMappingNode(IEnumerable<KeyValuePair<string, ConfigurationDocumentNode>> values) =>
        Values = new DomainDictionary<ConfigurationDocumentNode>(values);
}

public sealed record ConfigurationSequenceNode : ConfigurationDocumentNode
{
    public DomainList<ConfigurationDocumentNode> Values { get; }

    public ConfigurationSequenceNode(IEnumerable<ConfigurationDocumentNode> values) =>
        Values = new DomainList<ConfigurationDocumentNode>(values);
}

public sealed record ConfigurationScalarNode : ConfigurationDocumentNode
{
    public ConfigurationScalarKind Kind { get; }
    public string? Value { get; }

    public ConfigurationScalarNode(ConfigurationScalarKind kind, string? value)
    {
        Kind = kind;
        Value = value;
    }
}

public sealed record ReferencedConfigurationFile
{
    public ConfigurationFileKind Kind { get; }
    public string RelativePath { get; }
    public string CanonicalContent { get; }
    public ConfigurationDocumentNode Document { get; }

    public ReferencedConfigurationFile(
        ConfigurationFileKind kind,
        string relativePath,
        string canonicalContent,
        ConfigurationDocumentNode document)
    {
        Kind = kind;
        RelativePath = Required(relativePath, nameof(relativePath));
        CanonicalContent = Required(canonicalContent, nameof(canonicalContent));
        Document = document ?? throw new ArgumentNullException(nameof(document));
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public sealed record LoadedProjectConfiguration
{
    public RootConfigurationDto Root { get; }
    public DomainList<SystemConfigurationDto> SystemConfigurations { get; }
    public DomainList<SystemDefinition> Systems { get; }
    public DomainList<ReferencedConfigurationFile> ReferencedFiles { get; }
    public ReferencedConfigurationFile? MigrationGraphConfiguration =>
        ReferencedFiles.FirstOrDefault(file => file.Kind == ConfigurationFileKind.MigrationGraph);
    public string CanonicalConfiguration { get; }
    public string CanonicalizationVersion { get; }
    public string ConfigurationHash { get; }

    public LoadedProjectConfiguration(
        RootConfigurationDto root,
        IEnumerable<SystemConfigurationDto> systemConfigurations,
        IEnumerable<SystemDefinition> systems,
        IEnumerable<ReferencedConfigurationFile> referencedFiles,
        string canonicalConfiguration,
        string configurationHash)
    {
        Root = root;
        SystemConfigurations = new DomainList<SystemConfigurationDto>(systemConfigurations);
        Systems = new DomainList<SystemDefinition>(systems);
        ReferencedFiles = new DomainList<ReferencedConfigurationFile>(referencedFiles);
        CanonicalConfiguration = canonicalConfiguration;
        CanonicalizationVersion = CanonicalYamlSerializer.FormatVersion;
        ConfigurationHash = configurationHash;
    }
}

public sealed record EnvironmentVariableReference
{
    public string Name { get; }

    public EnvironmentVariableReference(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !IsValidName(name))
        {
            throw new ArgumentException("Environment variable reference has an invalid name.", nameof(name));
        }

        Name = name;
    }

    private static bool IsValidName(string value) =>
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}

public interface IEnvironmentVariableProvider
{
    string? GetValue(string name);
}

public sealed class ProcessEnvironmentVariableProvider : IEnvironmentVariableProvider
{
    public string? GetValue(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed class RuntimeEnvironmentValue
{
    private readonly string _value;

    internal RuntimeEnvironmentValue(string value) => _value = value;

    public TResult Use<TResult>(Func<string, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(_value);
    }

    public override string ToString() => "[REDACTED]";
}

public sealed class EnvironmentVariableResolver
{
    private readonly IEnvironmentVariableProvider _provider;

    public EnvironmentVariableResolver(IEnvironmentVariableProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public RuntimeEnvironmentValue Resolve(EnvironmentVariableReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var value = _provider.GetValue(reference.Name);
        return value is null
            ? throw new InvalidOperationException("Required environment variable is unavailable.")
            : new RuntimeEnvironmentValue(value);
    }
}
