using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Connectors.Abstractions;

namespace ProofShift.Projection;

public enum ProjectionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum ProjectionJournalResult
{
    Pending,
    Produced,
    Excluded,
    Failed,
    MetadataOnly
}

public sealed record ProjectionTransformation(string Type, string Version, string? SourceField = null, string? TargetField = null);

public sealed record ProjectionJournalEntry
{
    public RunId RunId { get; }
    public ArtifactReference? Target { get; }
    public string? TargetNode { get; }
    public DomainList<ArtifactReference> Sources { get; }
    public MigrationEdgeId EdgeId { get; }
    public string Edge { get; }
    public string EdgeVersion { get; }
    public DomainList<ProjectionTransformation> Transformations { get; }
    public RecoveryMode? RecoveryMode { get; }
    public bool RecoveryRequiresSnapshot { get; }
    public string? RecoveryStrategy { get; }
    public ProjectionJournalResult Result { get; }
    public string? FailureCode { get; }

    public ProjectionJournalEntry(
        RunId runId,
        ArtifactReference? target,
        string? targetNode,
        IEnumerable<ArtifactReference> sources,
        MigrationEdgeId edgeId,
        string edge,
        string edgeVersion,
        IEnumerable<ProjectionTransformation> transformations,
        ProjectionJournalResult result,
        RecoveryDefinition? recovery = null,
        string? failureCode = null)
    {
        RunId = runId;
        Target = target;
        TargetNode = string.IsNullOrWhiteSpace(targetNode) ? null : targetNode.Trim();
        Sources = new DomainList<ArtifactReference>(sources);
        EdgeId = edgeId;
        Edge = Required(edge, nameof(edge));
        EdgeVersion = Required(edgeVersion, nameof(edgeVersion));
        Transformations = new DomainList<ProjectionTransformation>(transformations);
        RecoveryMode = recovery?.Mode;
        RecoveryRequiresSnapshot = recovery?.RequiresSnapshot ?? false;
        RecoveryStrategy = recovery?.Strategy;
        Result = result;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed record ProjectionRun
{
    public RunId Id { get; }
    public RunType Type { get; }
    public ProjectionStatus Status { get; }
    public string ProjectId { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public long SourceArtifactCount { get; }
    public long TargetArtifactCount { get; }
    public long FailureCount { get; }
    public string? FailureCode { get; }
    public string? Fingerprint { get; }
    public string FingerprintVersion { get; }
    public IReadOnlyDictionary<string, string> ConnectorVersions { get; }
    public IReadOnlyCollection<string> ShadowDestinations { get; }
    public string JournalPath { get; }

    public ProjectionRun(
        RunId id,
        ProjectionStatus status,
        string projectId,
        string configurationHash,
        string graphHash,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        long sourceArtifactCount,
        long targetArtifactCount,
        long failureCount,
        string? failureCode,
        string? fingerprint,
        IEnumerable<KeyValuePair<string, string>> connectorVersions,
        IEnumerable<string> shadowDestinations,
        string journalPath)
    {
        Id = id;
        Type = RunType.Projection;
        Status = status;
        ProjectId = Required(projectId, nameof(projectId));
        ConfigurationHash = Required(configurationHash, nameof(configurationHash));
        GraphHash = Required(graphHash, nameof(graphHash));
        StartedAt = startedAt;
        CompletedAt = completedAt;
        SourceArtifactCount = sourceArtifactCount;
        TargetArtifactCount = targetArtifactCount;
        FailureCount = failureCount;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
        Fingerprint = fingerprint;
        FingerprintVersion = "proofshift-projection-fingerprint-v1";
        ConnectorVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(connectorVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal));
        ShadowDestinations = Array.AsReadOnly(shadowDestinations.Order(StringComparer.Ordinal).ToArray());
        JournalPath = Required(journalPath, nameof(journalPath));
    }

    private static string Required(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", parameterName) : value.Trim();
}

public sealed class ProjectionTransformationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ProjectionIdentity
{
    public static string CreateTargetIdentity(RecordEnvelope record, ArtifactSelector selector)
    {
        if (selector.IdentityFields.Count == 0)
        {
            throw new ProjectionTransformationException("PSPROJ_IDENTITY", "Target selector must declare identity fields.");
        }

        var parts = new List<string>(selector.IdentityFields.Count);
        foreach (var field in selector.IdentityFields)
        {
            if (!record.Values.TryGetValue(field, out var value) || value is NullValue)
            {
                throw new ProjectionTransformationException("PSPROJ_IDENTITY", "Target identity field is missing or NULL.");
            }

            var encoded = CanonicalValue(value);
            parts.Add($"{field.Length.ToString(CultureInfo.InvariantCulture)}:{field}={encoded.Length.ToString(CultureInfo.InvariantCulture)}:{encoded}");
        }

        return string.Join('|', parts);
    }

    public static string CanonicalValue(ValueNode value) => value switch
    {
        NullValue => "null",
        StringValue text => $"string:{text.Value.Normalize(NormalizationForm.FormC)}",
        IntegerValue integer => $"integer:{integer.Value.ToString(CultureInfo.InvariantCulture)}",
        DecimalValue number => $"decimal:{number.Value.ToString("G29", CultureInfo.InvariantCulture)}",
        BooleanValue boolean => boolean.Value ? "boolean:true" : "boolean:false",
        DateValue date => $"date:{date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
        InstantValue instant => $"instant:{instant.Value.ToString("O", CultureInfo.InvariantCulture)}",
        OffsetDateTimeValue offset => $"offset:{offset.Value.ToString("O", CultureInfo.InvariantCulture)}",
        LocalDateTimeValue local => $"local:{local.Value.ToString("O", CultureInfo.InvariantCulture)}",
        BinaryReferenceValue binary => $"binary:{binary.ContentLength.ToString(CultureInfo.InvariantCulture)}:{binary.Sha256}",
        CollectionValue collection => "collection:[" + string.Concat(collection.Values.Select(item => CanonicalPart(CanonicalValue(item)))) + "]",
        ObjectValue obj => "object:{" + string.Concat(obj.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => CanonicalPart(pair.Key) + CanonicalPart(CanonicalValue(pair.Value)))) + "}",
        _ => throw new ProjectionTransformationException("PSPROJ_VALUE", "Unsupported normalized value type.")
    };

    private static string CanonicalPart(string value) =>
        $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value}";
}

public static class TransformationRuntime
{
    public static RecordEnvelope Transform(RecordEnvelope source, MigrationEdge edge, MigrationNode target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(target);

        var operation = edge.Operation;
        var output = new SortedDictionary<string, ValueNode>(StringComparer.Ordinal);
        if (operation.Type == MigrationOperationType.Copy ||
            operation.Fields.Count == 0 && operation.Type is MigrationOperationType.Archive or MigrationOperationType.Split)
        {
            foreach (var pair in source.Values)
            {
                output.Add(pair.Key, pair.Value);
            }
        }

        foreach (var field in operation.Fields)
        {
            if (field.Source is null || !source.Values.TryGetValue(field.Source, out var value))
            {
                throw new ProjectionTransformationException("PSPROJ_FIELD", $"Source field for mapped target '{field.Target}' is unavailable.");
            }

            foreach (var step in field.Pipeline)
            {
                value = Apply(value, step, source.Values);
            }

            output[field.Target] = value;
        }

        foreach (var step in operation.Steps)
        {
            if (step.Type == TransformationStepType.Copy)
            {
                continue;
            }

            if (step.Type == TransformationStepType.Rename)
            {
                if (!step.Parameters.TryGetValue("from", out var from) || !step.Parameters.TryGetValue("to", out var to) ||
                    !output.Remove(from, out var renamedValue))
                {
                    throw new ProjectionTransformationException("PSPROJ_RENAME", "rename requires existing from and configured to parameters.");
                }

                output[to] = renamedValue;
                continue;
            }

            if (step.Type is TransformationStepType.Concatenate or TransformationStepType.Split)
            {
                if (!step.Parameters.TryGetValue("target", out var targetField))
                {
                    throw new ProjectionTransformationException("PSPROJ_TRANSFORM_TARGET", "Operation-level concatenate/split requires a target field parameter.");
                }

                if (step.Type == TransformationStepType.Concatenate)
                {
                    output[targetField] = Concatenate(step, source.Values);
                }
                else
                {
                    if (!step.Parameters.TryGetValue("source", out var sourceField) ||
                        !source.Values.TryGetValue(sourceField, out var sourceValue) || sourceValue is not StringValue sourceText)
                    {
                        throw new ProjectionTransformationException("PSPROJ_SPLIT", "Operation-level split requires a string source field.");
                    }

                    output[targetField] = Split(sourceText.Value, step);
                }

                continue;
            }

            var outputFields = output.Keys.ToArray();
            foreach (var outputField in outputFields)
            {
                output[outputField] = Apply(output[outputField], step, source.Values);
            }
        }

        var identity = ProjectionIdentity.CreateTargetIdentity(
            new RecordEnvelope(source.Artifact, target.SemanticType, output, source.Provenance, source.Relationships, source.Temporal),
            target.Selector);
        var artifactType = StableArtifactIdentity.ArtifactTypeFor(target.Selector);
        var artifact = new ArtifactReference(
            new ArtifactId(StableArtifactIdentity.CreateArtifactId(target.SystemId.Value, target.EndpointId.Value, artifactType, identity)),
            target.SystemId,
            target.EndpointId,
            artifactType,
            identity);
        var record = new RecordEnvelope(
            artifact,
            target.SemanticType,
            output,
            new ProvenanceMetadata(
                source.Provenance.Connector,
                target.EndpointId,
                identity,
                source.Provenance.ObservedAt,
                source.Provenance.SourceHash,
                [new KeyValuePair<string, string>("projectionSource", source.Artifact.Id.Value),
                 new KeyValuePair<string, string>("projectionEdge", edge.Name)]),
            source.Relationships,
            source.Temporal);
        return record;
    }

    private static ValueNode Apply(ValueNode value, TransformationStep step, IReadOnlyDictionary<string, ValueNode> sourceValues)
    {
        if (step.Version != "1")
        {
            throw Unsupported($"{step.Type} version {step.Version}");
        }

        if (value is NullValue)
        {
            return value;
        }

        switch (step.Type)
        {
            case TransformationStepType.Copy:
            case TransformationStepType.Rename:
                return value;
            case TransformationStepType.Trim when value is StringValue text:
                return new StringValue(text.Value.Trim());
            case TransformationStepType.NormalizeString when value is StringValue text:
                return new StringValue(text.Value.Normalize(NormalizationForm.FormKC).ToUpperInvariant());
            case TransformationStepType.NormalizeDate:
                return NormalizeDate(value, step);
            case TransformationStepType.CodeMap when value is StringValue text:
                return CodeMap(text.Value, step);
            case TransformationStepType.Concatenate:
                return Concatenate(step, sourceValues);
            case TransformationStepType.Split when value is StringValue text:
                return Split(text.Value, step);
            default:
                throw Unsupported(step.Type.ToString());
        }
    }

    private static ValueNode NormalizeDate(ValueNode value, TransformationStep step)
    {
        if (value is DateValue)
        {
            return value;
        }

        if (value is not StringValue text || !step.Parameters.TryGetValue("format", out var format) || string.IsNullOrWhiteSpace(format))
        {
            throw new ProjectionTransformationException("PSPROJ_DATE", "normalize-date requires a string value and an explicit invariant format.");
        }

        if (!DateOnly.TryParseExact(text.Value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ProjectionTransformationException("PSPROJ_DATE", "Date value does not match the configured invariant format.");
        }

        return new DateValue(date);
    }

    private static StringValue CodeMap(string value, TransformationStep step)
    {
        foreach (var pair in step.Parameters)
        {
            if (pair.Key is "unknown" or "default")
            {
                continue;
            }

            if (string.Equals(pair.Key, value, StringComparison.Ordinal))
            {
                return new StringValue(pair.Value);
            }
        }

        return step.Parameters.GetValueOrDefault("unknown") switch
        {
            "pass-through" => new StringValue(value),
            "default" when step.Parameters.TryGetValue("defaultValue", out var defaultValue) => new StringValue(defaultValue),
            _ => throw new ProjectionTransformationException("PSPROJ_CODE_MAP", "Code-map encountered an unmapped value and no pass-through/default policy was configured.")
        };
    }

    private static StringValue Concatenate(TransformationStep step, IReadOnlyDictionary<string, ValueNode> values)
    {
        if (!step.Parameters.TryGetValue("fields", out var fieldList))
        {
            throw new ProjectionTransformationException("PSPROJ_CONCAT", "concatenate requires an ordered comma-separated fields parameter.");
        }

        var separator = step.Parameters.GetValueOrDefault("separator") ?? string.Empty;
        var parts = fieldList.Split(',', StringSplitOptions.TrimEntries);
        var strings = parts.Select(field => values.TryGetValue(field, out var value) && value is StringValue text
            ? text.Value
            : throw new ProjectionTransformationException("PSPROJ_CONCAT", "concatenate fields must exist and contain strings."));
        return new StringValue(string.Join(separator, strings));
    }

    private static CollectionValue Split(string value, TransformationStep step)
    {
        if (!step.Parameters.TryGetValue("separator", out var separator) || separator.Length == 0)
        {
            throw new ProjectionTransformationException("PSPROJ_SPLIT", "split requires a non-empty separator parameter.");
        }

        return new CollectionValue(value.Split(separator, StringSplitOptions.None).Select(part => (ValueNode)new StringValue(part)));
    }

    private static ProjectionTransformationException Unsupported(string transformation) =>
        new("PSPROJ_UNSUPPORTED_TRANSFORM", $"Transformation '{transformation}' is not supported by PS-0.5.");
}