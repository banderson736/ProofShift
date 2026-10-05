using ProofShift.Configuration;
using ProofShift.Domain;

namespace ProofShift.Graph;

public static class GraphIssueCodes
{
    public const string UnsupportedVersion = "PSGRAPH001";
    public const string DuplicateNodeId = "PSGRAPH002";
    public const string DuplicateEdgeId = "PSGRAPH003";
    public const string UnknownSystem = "PSGRAPH004";
    public const string UnknownStorageEndpoint = "PSGRAPH005";
    public const string UnknownSourceNode = "PSGRAPH006";
    public const string UnknownTargetNode = "PSGRAPH007";
    public const string InvalidOperation = "PSGRAPH008";
    public const string MissingRecovery = "PSGRAPH009";
    public const string InvalidRecovery = "PSGRAPH010";
    public const string OrphanSource = "PSGRAPH011";
    public const string OrphanTarget = "PSGRAPH012";
    public const string InvalidCardinality = "PSGRAPH013";
    public const string CycleDetected = "PSGRAPH014";
    public const string InvalidSelector = "PSGRAPH015";
    public const string InvalidTransformation = "PSGRAPH016";
    public const string MissingRequiredProperty = "PSGRAPH017";
    public const string InvalidNodeType = "PSGRAPH018";
    public const string InvalidGraphDocument = "PSGRAPH019";
}

public enum GraphValidationSeverity
{
    Error,
    Warning
}

public sealed record GraphValidationIssue
{
    public string Code { get; }
    public GraphValidationSeverity Severity { get; }
    public string Message { get; }
    public string? File { get; }
    public string? Path { get; }

    public GraphValidationIssue(
        string code,
        GraphValidationSeverity severity,
        string message,
        string? file = null,
        string? path = null)
    {
        Code = Required(code, nameof(code));
        Severity = severity;
        Message = Required(message, nameof(message));
        File = NormalizeOptional(file);
        Path = NormalizeOptional(path);
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value must not be empty.", parameterName)
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record MigrationGraphConfigurationDto
{
    public int? Version { get; }
    public DomainList<MigrationNodeConfigurationDto> Nodes { get; }
    public DomainList<MigrationEdgeConfigurationDto> Edges { get; }

    public MigrationGraphConfigurationDto(
        int? version,
        IEnumerable<MigrationNodeConfigurationDto> nodes,
        IEnumerable<MigrationEdgeConfigurationDto> edges)
    {
        Version = version;
        Nodes = new DomainList<MigrationNodeConfigurationDto>(nodes);
        Edges = new DomainList<MigrationEdgeConfigurationDto>(edges);
    }
}

public sealed record MigrationNodeConfigurationDto
{
    public string Key { get; }
    public string? Type { get; }
    public string? System { get; }
    public string? Storage { get; }
    public string? SemanticType { get; }
    public ArtifactSelectorConfigurationDto? Selector { get; }

    public MigrationNodeConfigurationDto(
        string key,
        string? type,
        string? system,
        string? storage,
        string? semanticType,
        ArtifactSelectorConfigurationDto? selector)
    {
        Key = key;
        Type = type;
        System = system;
        Storage = storage;
        SemanticType = semanticType;
        Selector = selector;
    }
}

public sealed record ArtifactSelectorConfigurationDto
{
    public string? Kind { get; }
    public DomainDictionary<string> Properties { get; }
    public DomainList<string> IdentityFields { get; }

    public ArtifactSelectorConfigurationDto(
        string? kind,
        IEnumerable<KeyValuePair<string, string>> properties,
        IEnumerable<string> identityFields)
    {
        Kind = kind;
        Properties = new DomainDictionary<string>(properties);
        IdentityFields = new DomainList<string>(identityFields);
    }
}

public sealed record MigrationEdgeConfigurationDto
{
    public string Key { get; }
    public DomainList<string> From { get; }
    public DomainList<string> To { get; }
    public string? OperationType { get; }
    public string? Version { get; }
    public bool IsDestructive { get; }
    public DomainDictionary<string> Parameters { get; }
    public DomainList<TransformationStepConfigurationDto> Steps { get; }
    public DomainList<TransformationFieldConfigurationDto> Fields { get; }
    public RecoveryConfigurationDto? Recovery { get; }

    public MigrationEdgeConfigurationDto(
        string key,
        IEnumerable<string> from,
        IEnumerable<string> to,
        string? operationType,
        string? version,
        bool isDestructive,
        IEnumerable<KeyValuePair<string, string>> parameters,
        IEnumerable<TransformationStepConfigurationDto> steps,
        IEnumerable<TransformationFieldConfigurationDto> fields,
        RecoveryConfigurationDto? recovery)
    {
        Key = key;
        From = new DomainList<string>(from);
        To = new DomainList<string>(to);
        OperationType = operationType;
        Version = version;
        IsDestructive = isDestructive;
        Parameters = new DomainDictionary<string>(parameters);
        Steps = new DomainList<TransformationStepConfigurationDto>(steps);
        Fields = new DomainList<TransformationFieldConfigurationDto>(fields);
        Recovery = recovery;
    }
}

public sealed record TransformationStepConfigurationDto
{
    public string? Type { get; }
    public string? Version { get; }
    public DomainDictionary<string> Parameters { get; }

    public TransformationStepConfigurationDto(
        string? type,
        string? version,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        Type = type;
        Version = version;
        Parameters = new DomainDictionary<string>(parameters);
    }
}

public sealed record TransformationFieldConfigurationDto
{
    public string Target { get; }
    public string? Source { get; }
    public DomainList<TransformationStepConfigurationDto> Pipeline { get; }

    public TransformationFieldConfigurationDto(
        string target,
        string? source,
        IEnumerable<TransformationStepConfigurationDto> pipeline)
    {
        Target = target;
        Source = source;
        Pipeline = new DomainList<TransformationStepConfigurationDto>(pipeline);
    }
}

public sealed record RecoveryConfigurationDto
{
    public string? Mode { get; }
    public string? Strategy { get; }
    public bool? RequiresSnapshot { get; }
    public string? Justification { get; }

    public RecoveryConfigurationDto(string? mode, string? strategy, bool? requiresSnapshot, string? justification)
    {
        Mode = mode;
        Strategy = strategy;
        RequiresSnapshot = requiresSnapshot;
        Justification = justification;
    }
}

public sealed record GraphConfigurationParseResult
{
    public MigrationGraphConfigurationDto? Configuration { get; }
    public DomainList<GraphValidationIssue> Issues { get; }

    public GraphConfigurationParseResult(
        MigrationGraphConfigurationDto? configuration,
        IEnumerable<GraphValidationIssue> issues)
    {
        Configuration = configuration;
        Issues = new DomainList<GraphValidationIssue>(issues);
    }
}

public static class MigrationGraphConfigurationParser
{
    public static GraphConfigurationParseResult Parse(ReferencedConfigurationFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var issues = new List<GraphValidationIssue>();
        if (file.Document is not ConfigurationMappingNode root)
        {
            AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                "Migration graph configuration must be a mapping.", file.RelativePath);
            return CreateResult(null, issues);
        }

        try { root = ReusableCodeMaps.Resolve(root); }
        catch (InvalidDataException exception)
        {
            AddIssue(issues, "PSMAP001", exception.Message, file.RelativePath, "codeMaps");
            return CreateResult(null, issues);
        }

        var versionNode = Find(root, "version");
        var version = ReadInteger(versionNode);
        var nodes = ParseNodes(Find(root, "nodes"), file.RelativePath, issues);
        var edges = ParseEdges(Find(root, "edges"), file.RelativePath, issues);
        return CreateResult(new MigrationGraphConfigurationDto(version, nodes, edges), issues);
    }

    private static List<MigrationNodeConfigurationDto> ParseNodes(
        ConfigurationDocumentNode? node,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        var nodes = new List<MigrationNodeConfigurationDto>();
        if (node is ConfigurationMappingNode mapping)
        {
            foreach (var entry in mapping.Values)
            {
                if (entry.Value is not ConfigurationMappingNode definition)
                {
                    AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                        "Each graph node must be a mapping.", file, $"nodes.{entry.Key}");
                    continue;
                }

                nodes.Add(ParseNode(entry.Key, definition, file, issues));
            }

            return nodes;
        }

        if (node is ConfigurationSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Values.Count; index++)
            {
                if (sequence.Values[index] is not ConfigurationMappingNode definition)
                {
                    AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                        "Each graph node must be a mapping.", file, $"nodes[{index}]");
                    continue;
                }

                var key = ReadText(Find(definition, "id"));
                if (key is null)
                {
                    AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                        "Sequence graph nodes require an id.", file, $"nodes[{index}].id");
                    continue;
                }

                nodes.Add(ParseNode(key, definition, file, issues));
            }

            return nodes;
        }

        AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
            "Migration graph nodes must be a mapping or sequence.", file, "nodes");
        return nodes;
    }

    private static MigrationNodeConfigurationDto ParseNode(
        string key,
        ConfigurationMappingNode definition,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        var selector = ParseSelector(definition, file, $"nodes.{key}.selector", issues);
        return new MigrationNodeConfigurationDto(
            key,
            ReadText(Find(definition, "type")),
            ReadText(Find(definition, "system")),
            ReadText(Find(definition, "storage")),
            ReadText(Find(definition, "semanticType")),
            selector);
    }

    private static ArtifactSelectorConfigurationDto? ParseSelector(
        ConfigurationMappingNode node,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var selectorNode = Find(node, "selector") as ConfigurationMappingNode;
        var legacySelectorKind = "table";
        if (selectorNode is null)
        {
            selectorNode = Find(node, "source") as ConfigurationMappingNode ?? Find(node, "target") as ConfigurationMappingNode;
            if (selectorNode is null)
            {
                return null;
            }
        }
        else
        {
            legacySelectorKind = ReadText(Find(selectorNode, "kind")) ?? string.Empty;
        }

        var kind = legacySelectorKind.Length > 0
            ? legacySelectorKind
            : ReadText(Find(selectorNode, "kind"));
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Find(selectorNode, "properties") is ConfigurationMappingNode propertyMap)
        {
            FlattenScalars(propertyMap, string.Empty, properties, file, path, issues);
        }

        if (kind == "table" && properties.Count == 0)
        {
            var table = ReadText(Find(selectorNode, "table"));
            if (table is not null)
            {
                properties.Add("name", table);
            }
        }

        foreach (var entry in selectorNode.Values)
        {
            if (entry.Key is "kind" or "properties" or "identity" or "key" or "table")
            {
                continue;
            }

            if (ReadText(entry.Value) is { } scalar)
            {
                properties.TryAdd(entry.Key, scalar);
            }
        }

        var identityNode = Find(selectorNode, "identity") ?? Find(selectorNode, "key");
        var identity = new List<string>();
        if (identityNode is ConfigurationSequenceNode identitySequence)
        {
            foreach (var field in identitySequence.Values)
            {
                if (ReadText(field) is { } identityField)
                {
                    identity.Add(identityField);
                }
                else
                {
                    AddIssue(issues, GraphIssueCodes.InvalidSelector,
                        "Selector identity fields must be scalar strings.", file, path);
                }
            }
        }
        else if (identityNode is not null)
        {
            AddIssue(issues, GraphIssueCodes.InvalidSelector,
                "Selector identity must be a sequence.", file, path);
        }

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        return new ArtifactSelectorConfigurationDto(normalizedKind, properties, identity);
    }

    private static List<MigrationEdgeConfigurationDto> ParseEdges(
        ConfigurationDocumentNode? node,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        var edges = new List<MigrationEdgeConfigurationDto>();
        if (node is ConfigurationMappingNode mapping)
        {
            foreach (var entry in mapping.Values)
            {
                if (entry.Value is not ConfigurationMappingNode definition)
                {
                    AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                        "Each graph edge must be a mapping.", file, $"edges.{entry.Key}");
                    continue;
                }

                edges.Add(ParseEdge(entry.Key, definition, file, issues));
            }

            return edges;
        }

        if (node is ConfigurationSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Values.Count; index++)
            {
                if (sequence.Values[index] is not ConfigurationMappingNode definition)
                {
                    AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                        "Each graph edge must be a mapping.", file, $"edges[{index}]");
                    continue;
                }

                var key = ReadText(Find(definition, "id"));
                if (key is null)
                {
                    AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                        "Sequence graph edges require an id.", file, $"edges[{index}].id");
                    continue;
                }

                edges.Add(ParseEdge(key, definition, file, issues));
            }

            return edges;
        }

        AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
            "Migration graph edges must be a mapping or sequence.", file, "edges");
        return edges;
    }

    private static MigrationEdgeConfigurationDto ParseEdge(
        string key,
        ConfigurationMappingNode definition,
        string file,
        ICollection<GraphValidationIssue> issues)
    {
        var operation = Find(definition, "operation") as ConfigurationMappingNode;
        if (operation is null)
        {
            AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                "Graph edge requires an operation mapping.", file, $"edges.{key}.operation");
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<TransformationStepConfigurationDto>();
        var fields = new List<TransformationFieldConfigurationDto>();
        var operationType = operation is null ? null : ReadText(Find(operation, "type"));
        var operationVersion = operation is null ? null : ReadText(Find(operation, "version"));
        var explicitlyDestructive = operation is not null &&
            (ReadBoolean(Find(operation, "destructive")) == true || ReadBoolean(Find(operation, "isDestructive")) == true);
        if (operation is not null)
        {
            if (Find(operation, "parameters") is ConfigurationMappingNode parameterMap)
            {
                FlattenScalars(parameterMap, string.Empty, parameters, file, $"edges.{key}.operation.parameters", issues);
            }

            foreach (var entry in operation.Values)
            {
                if (entry.Key is "type" or "version" or "destructive" or "isDestructive" or "parameters" or "steps" or "fields")
                {
                    continue;
                }

                FlattenScalars(entry.Value, entry.Key, parameters, file, $"edges.{key}.operation.{entry.Key}", issues);
            }

            steps.AddRange(ParseSteps(Find(operation, "steps"), file, $"edges.{key}.operation.steps", issues));
            fields.AddRange(ParseFields(Find(operation, "fields"), file, $"edges.{key}.operation.fields", issues));
        }

        return new MigrationEdgeConfigurationDto(
            key,
            ReadStringSequence(Find(definition, "from"), file, $"edges.{key}.from", issues),
            ReadStringSequence(Find(definition, "to"), file, $"edges.{key}.to", issues),
            operationType,
            ReadText(Find(definition, "version")) ?? operationVersion,
            explicitlyDestructive,
            parameters,
            steps,
            fields,
            ParseRecovery(Find(definition, "recovery")));
    }

    private static List<TransformationFieldConfigurationDto> ParseFields(
        ConfigurationDocumentNode? node,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var fields = new List<TransformationFieldConfigurationDto>();
        if (node is null)
        {
            return fields;
        }

        if (node is not ConfigurationMappingNode mapping)
        {
            AddIssue(issues, GraphIssueCodes.InvalidTransformation, "Operation fields must be a mapping.", file, path);
            return fields;
        }

        foreach (var entry in mapping.Values)
        {
            if (entry.Value is not ConfigurationMappingNode definition)
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    "Each field mapping must be a mapping.", file, $"{path}.{entry.Key}");
                continue;
            }

            fields.Add(new TransformationFieldConfigurationDto(
                entry.Key,
                ReadText(Find(definition, "source")),
                ParseSteps(Find(definition, "pipeline"), file, $"{path}.{entry.Key}.pipeline", issues)));
        }

        return fields;
    }

    private static List<TransformationStepConfigurationDto> ParseSteps(
        ConfigurationDocumentNode? node,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var steps = new List<TransformationStepConfigurationDto>();
        if (node is null)
        {
            return steps;
        }

        if (node is not ConfigurationSequenceNode sequence)
        {
            AddIssue(issues, GraphIssueCodes.InvalidTransformation, "Transformation pipeline must be a sequence.", file, path);
            return steps;
        }

        for (var index = 0; index < sequence.Values.Count; index++)
        {
            var item = sequence.Values[index];
            if (ReadText(item) is { } scalarType)
            {
                steps.Add(new TransformationStepConfigurationDto(scalarType, null, []));
                continue;
            }

            if (item is not ConfigurationMappingNode mapping)
            {
                AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                    "Transformation step must be a name or mapping.", file, $"{path}[{index}]");
                continue;
            }

            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (Find(mapping, "parameters") is ConfigurationMappingNode parameterMap)
            {
                FlattenScalars(parameterMap, string.Empty, parameters, file, $"{path}[{index}].parameters", issues);
            }

            foreach (var entry in mapping.Values)
            {
                if (entry.Key is "type" or "version" or "parameters")
                {
                    continue;
                }

                if (entry.Key == "values")
                {
                    if (entry.Value is not ConfigurationMappingNode valueMap)
                    {
                        AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                            "Transformation values must be a mapping of source codes to target values.", file, $"{path}[{index}].values");
                        continue;
                    }

                    foreach (var valueEntry in valueMap.Values)
                    {
                        if (ReadText(valueEntry.Value) is { } mappedValue)
                        {
                            parameters[valueEntry.Key] = mappedValue;
                        }
                        else
                        {
                            AddIssue(issues, GraphIssueCodes.InvalidTransformation,
                                "Code-map values must be scalar strings.", file, $"{path}[{index}].values.{valueEntry.Key}");
                        }
                    }

                    continue;
                }

                FlattenScalars(entry.Value, entry.Key, parameters, file, $"{path}[{index}].{entry.Key}", issues);
            }

            steps.Add(new TransformationStepConfigurationDto(
                ReadText(Find(mapping, "type")),
                ReadText(Find(mapping, "version")),
                parameters));
        }

        return steps;
    }

    private static RecoveryConfigurationDto? ParseRecovery(ConfigurationDocumentNode? node)
    {
        if (node is not ConfigurationMappingNode mapping)
        {
            return null;
        }

        return new RecoveryConfigurationDto(
            ReadText(Find(mapping, "mode")),
            ReadText(Find(mapping, "strategy")),
            ReadBoolean(Find(mapping, "requiresSnapshot")),
            ReadText(Find(mapping, "justification")));
    }

    private static List<string> ReadStringSequence(
        ConfigurationDocumentNode? node,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        var values = new List<string>();
        if (node is ConfigurationSequenceNode sequence)
        {
            foreach (var item in sequence.Values)
            {
                if (ReadText(item) is { } value)
                {
                    values.Add(value);
                }
                else
                {
                    AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                        "Edge references must be scalar node keys.", file, path);
                }
            }
        }
        else if (node is not null)
        {
            AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                "Edge references must be sequences.", file, path);
        }
        else
        {
            AddIssue(issues, GraphIssueCodes.MissingRequiredProperty,
                "Edge reference sequence is required.", file, path);
        }

        return values;
    }

    private static void FlattenScalars(
        ConfigurationDocumentNode node,
        string prefix,
        IDictionary<string, string> output,
        string file,
        string path,
        ICollection<GraphValidationIssue> issues)
    {
        if (node is ConfigurationMappingNode mapping)
        {
            foreach (var entry in mapping.Values)
            {
                var key = prefix.Length == 0 ? entry.Key : $"{prefix}.{entry.Key}";
                FlattenScalars(entry.Value, key, output, file, $"{path}.{key}", issues);
            }
        }
        else if (node is ConfigurationSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Values.Count; index++)
            {
                FlattenScalars(sequence.Values[index], $"{prefix}[{index}]", output, file, $"{path}[{index}]", issues);
            }
        }
        else if (node is ConfigurationScalarNode scalar && scalar.Kind != ConfigurationScalarKind.Null)
        {
            output[prefix] = scalar.Value ?? string.Empty;
        }
        else
        {
            AddIssue(issues, GraphIssueCodes.InvalidGraphDocument,
                "Selector and operation properties must contain scalar values.", file, path);
        }
    }

    private static ConfigurationDocumentNode? Find(ConfigurationMappingNode mapping, string key) =>
        mapping.Values.TryGetValue(key, out var value) ? value : null;

    private static string? ReadText(ConfigurationDocumentNode? node) =>
        node is ConfigurationScalarNode { Kind: not ConfigurationScalarKind.Null } scalar
            ? scalar.Value?.Trim()
            : null;

    private static bool? ReadBoolean(ConfigurationDocumentNode? node)
    {
        var text = ReadText(node);
        return bool.TryParse(text, out var value) ? value : null;
    }

    private static int? ReadInteger(ConfigurationDocumentNode? node)
    {
        var text = ReadText(node);
        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static GraphConfigurationParseResult CreateResult(
        MigrationGraphConfigurationDto? configuration,
        IEnumerable<GraphValidationIssue> issues) =>
        new(configuration, issues
            .OrderBy(issue => issue.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal));

    private static void AddIssue(
        ICollection<GraphValidationIssue> issues,
        string code,
        string message,
        string file,
        string? path = null) =>
        issues.Add(new GraphValidationIssue(code, GraphValidationSeverity.Error, message, file, path));
}
