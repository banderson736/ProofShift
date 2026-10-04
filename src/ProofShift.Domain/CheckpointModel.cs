namespace ProofShift.Domain;

public readonly record struct CheckpointId
{
    public Guid Value { get; }

    public CheckpointId(Guid value) => Value = DomainGuard.Required(value, nameof(value));
}

public enum CheckpointStatus
{
    Creating,
    Complete,
    Failed,
    Cancelled
}

public enum CheckpointGuarantee
{
    Observed,
    Consistent,
    Replayable,
    ExternallyPinned,
    Materialized
}

public enum SourceConsistencyGuarantee
{
    Observed,
    Consistent
}

public sealed record CheckpointEndpoint
{
    public string SourceNodeKey { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public ConnectorId Connector { get; }
    public string ConnectorVersion { get; }
    public string SelectorHash { get; }
    public DomainList<string> IdentityFields { get; }
    public DateTimeOffset CaptureStartedAt { get; }
    public DateTimeOffset CaptureCompletedAt { get; }
    public SourceConsistencyGuarantee SourceConsistency { get; }
    public CheckpointGuarantee Guarantee { get; }
    public bool Replayable { get; }
    public long ArtifactCount { get; }
    public long ByteCount { get; }
    public string SourceFingerprint { get; }
    public string SegmentReference { get; }
    public string SegmentSha256 { get; }
    public long SegmentLength { get; }

    public CheckpointEndpoint(
        string sourceNodeKey,
        SystemId systemId,
        StorageEndpointId endpointId,
        ConnectorId connector,
        string connectorVersion,
        string selectorHash,
        IEnumerable<string> identityFields,
        DateTimeOffset captureStartedAt,
        DateTimeOffset captureCompletedAt,
        SourceConsistencyGuarantee sourceConsistency,
        CheckpointGuarantee guarantee,
        bool replayable,
        long artifactCount,
        long byteCount,
        string sourceFingerprint,
        string segmentReference,
        string segmentSha256,
        long segmentLength)
    {
        SourceNodeKey = DomainGuard.Required(sourceNodeKey, nameof(sourceNodeKey));
        SystemId = DomainGuard.Required(systemId, nameof(systemId));
        EndpointId = DomainGuard.Required(endpointId, nameof(endpointId));
        Connector = DomainGuard.Required(connector, nameof(connector));
        ConnectorVersion = DomainGuard.Required(connectorVersion, nameof(connectorVersion));
        SelectorHash = Hash(selectorHash, nameof(selectorHash));
        IdentityFields = new DomainList<string>(identityFields);
        if (captureCompletedAt < captureStartedAt)
        {
            throw new ArgumentException("Endpoint capture completion must not precede its start.", nameof(captureCompletedAt));
        }

        if (artifactCount < 0 || byteCount < 0 || segmentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(artifactCount), "Checkpoint counts and lengths must not be negative.");
        }

        CaptureStartedAt = captureStartedAt;
        CaptureCompletedAt = captureCompletedAt;
        SourceConsistency = sourceConsistency;
        Guarantee = guarantee;
        Replayable = replayable;
        ArtifactCount = artifactCount;
        ByteCount = byteCount;
        SourceFingerprint = Hash(sourceFingerprint, nameof(sourceFingerprint));
        SegmentReference = DomainGuard.Required(segmentReference, nameof(segmentReference));
        SegmentSha256 = Hash(segmentSha256, nameof(segmentSha256));
        SegmentLength = segmentLength;
        if (guarantee == CheckpointGuarantee.Materialized && !replayable)
        {
            throw new ArgumentException("Materialized checkpoints must be replayable.", nameof(replayable));
        }
    }

    private static string Hash(string value, string parameterName)
    {
        var normalized = DomainGuard.Required(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        }

        return normalized;
    }
}

public sealed record SourceCheckpoint
{
    public CheckpointId Id { get; }
    public CheckpointStatus Status { get; }
    public string ProjectId { get; }
    public string ConfigurationHash { get; }
    public string GraphHash { get; }
    public DateTimeOffset CaptureStartedAt { get; }
    public DateTimeOffset CaptureCompletedAt { get; }
    public TimeSpan CaptureWindow => CaptureCompletedAt - CaptureStartedAt;
    public TimeSpan MaximumEndpointSkew { get; }
    public bool CrossSystemAtomic { get; }
    public bool Replayable { get; }
    public CheckpointGuarantee Guarantee { get; }
    public DomainList<CheckpointEndpoint> Endpoints { get; }
    public long ArtifactCount { get; }
    public long ByteCount { get; }
    public string? SourceFingerprint { get; }
    public string? ManifestHash { get; }

    public SourceCheckpoint(
        CheckpointId id,
        CheckpointStatus status,
        string projectId,
        string configurationHash,
        string graphHash,
        DateTimeOffset captureStartedAt,
        DateTimeOffset captureCompletedAt,
        TimeSpan maximumEndpointSkew,
        bool crossSystemAtomic,
        bool replayable,
        CheckpointGuarantee guarantee,
        IEnumerable<CheckpointEndpoint> endpoints,
        long artifactCount,
        long byteCount,
        string? sourceFingerprint,
        string? manifestHash)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Checkpoint identifier must not be empty.", nameof(id));
        }

        Id = id;
        Status = status;
        ProjectId = DomainGuard.Required(projectId, nameof(projectId));
        ConfigurationHash = Hash(configurationHash, nameof(configurationHash));
        GraphHash = Hash(graphHash, nameof(graphHash));
        if (captureCompletedAt < captureStartedAt)
        {
            throw new ArgumentException("Checkpoint completion must not precede capture start.", nameof(captureCompletedAt));
        }

        if (maximumEndpointSkew < TimeSpan.Zero || artifactCount < 0 || byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEndpointSkew), "Checkpoint skew, counts, and lengths must not be negative.");
        }

        CaptureStartedAt = captureStartedAt;
        CaptureCompletedAt = captureCompletedAt;
        MaximumEndpointSkew = maximumEndpointSkew;
        CrossSystemAtomic = crossSystemAtomic;
        Replayable = replayable;
        Guarantee = guarantee;
        Endpoints = new DomainList<CheckpointEndpoint>(endpoints.OrderBy(endpoint => endpoint.SourceNodeKey, StringComparer.Ordinal));
        ArtifactCount = artifactCount;
        ByteCount = byteCount;
        SourceFingerprint = sourceFingerprint is null ? null : Hash(sourceFingerprint, nameof(sourceFingerprint));
        ManifestHash = manifestHash is null ? null : Hash(manifestHash, nameof(manifestHash));

        if (Endpoints.Count == 0)
        {
            throw new ArgumentException("A checkpoint must include at least one source node.", nameof(endpoints));
        }

        if (status == CheckpointStatus.Complete &&
            (!replayable || guarantee != CheckpointGuarantee.Materialized || SourceFingerprint is null || ManifestHash is null ||
             Endpoints.Any(endpoint => !endpoint.Replayable || endpoint.Guarantee != CheckpointGuarantee.Materialized)))
        {
            throw new ArgumentException("Complete checkpoints must be fully materialized, replayable, and fingerprinted.", nameof(status));
        }

        if (status != CheckpointStatus.Complete && (replayable || SourceFingerprint is not null || ManifestHash is not null))
        {
            throw new ArgumentException("Incomplete checkpoints cannot claim replayability or finalized fingerprints.", nameof(status));
        }
    }

    private static string Hash(string value, string parameterName)
    {
        var normalized = DomainGuard.Required(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Value must be a SHA-256 digest.", parameterName);
        }

        return normalized;
    }
}