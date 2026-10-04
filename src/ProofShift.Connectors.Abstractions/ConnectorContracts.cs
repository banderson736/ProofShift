using ProofShift.Domain;

namespace ProofShift.Connectors.Abstractions;

public static class ConnectorIssueCodes
{
    public const string UnknownConnector = "PSCONN001";
    public const string UnsupportedSelector = "PSCONN002";
    public const string SourceObjectNotFound = "PSCONN003";
    public const string IdentityFieldNotFound = "PSCONN004";
    public const string DuplicateArtifactIdentity = "PSCONN005";
    public const string InvalidPhysicalIdentifier = "PSCONN006";
    public const string SourceConnectionFailed = "PSCONN007";
    public const string SourceReadFailed = "PSCONN008";
    public const string PathOutsideRoot = "PSCONN009";
    public const string InvalidCsv = "PSCONN010";
    public const string UnsupportedPhysicalType = "PSCONN011";
    public const string NonDeterministicIdentity = "PSCONN012";
    public const string MissingConfiguration = "PSCONN013";
    public const string PartitioningUnsupported = "PSCONN014";
}

public enum ConnectorIssueSeverity
{
    Error,
    Warning
}

public sealed record ConnectorIssue
{
    public string Code { get; }
    public ConnectorIssueSeverity Severity { get; }
    public string Message { get; }
    public string? NodeKey { get; }
    public string? Location { get; }

    public ConnectorIssue(
        string code,
        ConnectorIssueSeverity severity,
        string message,
        string? nodeKey = null,
        string? location = null)
    {
        Code = Required(code, nameof(code));
        Severity = severity;
        Message = Required(message, nameof(message));
        NodeKey = NormalizeOptional(nodeKey);
        Location = NormalizeOptional(location);
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum SourceInspectionStatus
{
    Valid,
    Invalid,
    Failed
}

public sealed record SourceColumn
{
    public string Name { get; }
    public string DataType { get; }
    public bool IsNullable { get; }
    public int? Ordinal { get; }

    public SourceColumn(string name, string dataType, bool isNullable, int? ordinal = null)
    {
        Name = Required(name, nameof(name));
        DataType = Required(dataType, nameof(dataType));
        IsNullable = isNullable;
        Ordinal = ordinal;
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public sealed record SourceInspection
{
    public SourceInspectionStatus Status { get; }
    public string? PhysicalObject { get; }
    public DomainList<SourceColumn> Columns { get; }
    public DomainList<string> PrimaryKeyFields { get; }
    public DomainList<string> IdentityFields { get; }
    public long? EstimatedRecords { get; }
    public long? Files { get; }
    public long? Bytes { get; }
    public DateTimeOffset InspectedAt { get; }
    public DomainList<ConnectorIssue> Issues { get; }

    public SourceInspection(
        SourceInspectionStatus status,
        IEnumerable<SourceColumn>? columns = null,
        IEnumerable<string>? primaryKeyFields = null,
        IEnumerable<string>? identityFields = null,
        long? estimatedRecords = null,
        string? physicalObject = null,
        long? files = null,
        long? bytes = null,
        DateTimeOffset? inspectedAt = null,
        IEnumerable<ConnectorIssue>? issues = null)
    {
        Status = status;
        PhysicalObject = string.IsNullOrWhiteSpace(physicalObject) ? null : physicalObject.Trim();
        Columns = new DomainList<SourceColumn>(columns ?? Array.Empty<SourceColumn>());
        PrimaryKeyFields = new DomainList<string>(primaryKeyFields ?? Array.Empty<string>());
        IdentityFields = new DomainList<string>(identityFields ?? Array.Empty<string>());
        EstimatedRecords = estimatedRecords;
        Files = files;
        Bytes = bytes;
        InspectedAt = inspectedAt ?? DateTimeOffset.UtcNow;
        Issues = new DomainList<ConnectorIssue>(issues ?? Array.Empty<ConnectorIssue>());
    }
}

public sealed record ReadOptions
{
    public int BatchSize { get; }
    public IReadOnlyDictionary<string, string> Partition { get; }

    public ReadOptions(int batchSize = 512, IReadOnlyDictionary<string, string>? partition = null)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        BatchSize = batchSize;
        var partitionValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (partition is not null)
        {
            foreach (var pair in partition)
            {
                partitionValues.Add(pair.Key, pair.Value);
            }
        }

        Partition = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(partitionValues);
    }
}

public interface ISourceConnector
{
    ConnectorId Id { get; }
    string Version { get; }

    Task<SourceInspection> InspectAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        CancellationToken cancellationToken);

    IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ReadOptions options,
        CancellationToken cancellationToken);
}

    public interface ISourceBinaryContentResolver
    {
        ValueTask<Stream> OpenBinaryReadAsync(
        ConnectorContext context,
        ArtifactSelector selector,
        ArtifactReference artifact,
        BinaryReferenceValue binaryReference,
        CancellationToken cancellationToken);
    }

public sealed record ConnectorContext
{
    public string SystemKey { get; }
    public string EndpointKey { get; }
    public ConnectorId Connector { get; }
    public string NodeKey { get; }
    public string SemanticType { get; }
    public RuntimeConfiguration Configuration { get; }

    public ConnectorContext(
        string systemKey,
        string endpointKey,
        ConnectorId connector,
        string nodeKey,
        string semanticType,
        RuntimeConfiguration configuration)
    {
        SystemKey = Required(systemKey, nameof(systemKey));
        EndpointKey = Required(endpointKey, nameof(endpointKey));
        Connector = connector;
        NodeKey = Required(nodeKey, nameof(nodeKey));
        SemanticType = Required(semanticType, nameof(semanticType));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();
}

public sealed class RuntimeSetting
{
    private readonly string _value;

    internal RuntimeSetting(string value, bool isSecret)
    {
        _value = value;
        IsSecret = isSecret;
    }

    public static RuntimeSetting FromRuntimeValue(string value, bool isSecret = false) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), isSecret);

    public bool IsSecret { get; }

    public TResult UseValue<TResult>(Func<string, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action(_value);
    }

    public override string ToString() => "[REDACTED]";
}

public sealed class RuntimeConfiguration
{
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<string, RuntimeSetting> _settings;

    public RuntimeConfiguration(IEnumerable<KeyValuePair<string, RuntimeSetting>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = new System.Collections.ObjectModel.ReadOnlyDictionary<string, RuntimeSetting>(
            new SortedDictionary<string, RuntimeSetting>(settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    public bool TryGet(string path, out RuntimeSetting setting) => _settings.TryGetValue(path, out setting!);

    public RuntimeSetting GetRequired(string path) =>
        _settings.TryGetValue(path, out var setting)
            ? setting
            : throw new ConnectorConfigurationException(ConnectorIssueCodes.MissingConfiguration, "Required connector configuration is missing.");

    public override string ToString() => "[REDACTED]";
}

public sealed class ConnectorConfigurationException : Exception
{
    public string Code { get; }

    public ConnectorConfigurationException(string code, string message) : base(message) => Code = code;
}

public sealed class ConnectorResolutionException : Exception
{
    public string Code { get; }

    public ConnectorResolutionException(string code, string message) : base(message) => Code = code;
}

public sealed class ConnectorRegistry
{
    private readonly System.Collections.ObjectModel.ReadOnlyDictionary<ConnectorId, ISourceConnector> _connectors;

    public ConnectorRegistry(IEnumerable<ISourceConnector> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        var connectorMap = new Dictionary<ConnectorId, ISourceConnector>();
        foreach (var connector in connectors)
        {
            ArgumentNullException.ThrowIfNull(connector);
            if (!connectorMap.TryAdd(connector.Id, connector))
            {
                throw new ArgumentException($"Connector '{connector.Id.Value}' is registered more than once.", nameof(connectors));
            }
        }

        _connectors = new System.Collections.ObjectModel.ReadOnlyDictionary<ConnectorId, ISourceConnector>(connectorMap);
    }

    public ISourceConnector Resolve(ConnectorId connectorId) =>
        _connectors.TryGetValue(connectorId, out var connector)
            ? connector
            : throw new ConnectorResolutionException(ConnectorIssueCodes.UnknownConnector, "Configured connector is not registered.");
}
