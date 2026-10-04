using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ProofShift.Domain;
using ProofShift.Connectors.Abstractions;
using ProofShift.Engine;

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
    public string? CheckpointId { get; }
    public string? CheckpointSourceFingerprint { get; }
    public string? CheckpointManifestHash { get; }
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
        string journalPath,
        string? checkpointId = null,
        string? checkpointSourceFingerprint = null,
        string? checkpointManifestHash = null)
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
        CheckpointId = checkpointId;
        CheckpointSourceFingerprint = checkpointSourceFingerprint;
        CheckpointManifestHash = checkpointManifestHash;
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
        try
        {
            return GraphTargetIdentity.Create(record, selector);
        }
        catch (GraphTransformationException exception)
        {
            throw new ProjectionTransformationException(exception.Code, exception.Message);
        }
    }

    public static string CanonicalValue(ValueNode value)
    {
        try
        {
            return GraphTargetIdentity.CanonicalValue(value);
        }
        catch (GraphTransformationException exception)
        {
            throw new ProjectionTransformationException(exception.Code, exception.Message);
        }
    }

    private static string CanonicalPart(string value) =>
        $"{Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)}:{value}";
}

public static class TransformationRuntime
{
    public static RecordEnvelope Transform(RecordEnvelope source, MigrationEdge edge, MigrationNode target)
    {
        try
        {
            return GraphTransformationRuntime.Transform(source, edge, target);
        }
        catch (GraphTransformationException exception)
        {
            throw new ProjectionTransformationException(exception.Code, exception.Message);
        }
    }

}