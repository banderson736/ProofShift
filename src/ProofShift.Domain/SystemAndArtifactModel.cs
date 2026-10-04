namespace ProofShift.Domain;

public sealed record Project
{
    public ProjectId Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public DateTimeOffset CreatedAt { get; }

    public Project(ProjectId id, string name, DateTimeOffset createdAt, string? description = null)
    {
        Id = DomainGuard.Required(id, nameof(id));
        Name = DomainGuard.Required(name, nameof(name));
        Description = description;
        CreatedAt = createdAt;
    }
}

public enum SystemRole
{
    Source,
    Target,
    ShadowTarget,
    Archive
}

public sealed record SystemDefinition
{
    public SystemId Id { get; }
    public string Name { get; }
    public SystemRole Role { get; }
    public DomainList<StorageEndpointDefinition> StorageEndpoints { get; }

    public SystemDefinition(
        SystemId id,
        string name,
        SystemRole role,
        IEnumerable<StorageEndpointDefinition> storageEndpoints)
    {
        Id = DomainGuard.Required(id, nameof(id));
        Name = DomainGuard.Required(name, nameof(name));
        Role = role;
        StorageEndpoints = new DomainList<StorageEndpointDefinition>(storageEndpoints);
        if (StorageEndpoints.Count == 0)
        {
            throw new ArgumentException("A system must define at least one storage endpoint.", nameof(storageEndpoints));
        }

        if (StorageEndpoints.Select(endpoint => endpoint.Id).Distinct().Count() != StorageEndpoints.Count)
        {
            throw new ArgumentException("Storage endpoint identifiers must be unique within a system.", nameof(storageEndpoints));
        }
    }
}

public sealed record StorageEndpointDefinition
{
    public StorageEndpointId Id { get; }
    public ConnectorId Connector { get; }
    public DomainDictionary<string> Configuration { get; }

    public StorageEndpointDefinition(
        StorageEndpointId id,
        ConnectorId connector,
        IEnumerable<KeyValuePair<string, string>>? configuration = null)
    {
        Id = DomainGuard.Required(id, nameof(id));
        Connector = DomainGuard.Required(connector, nameof(connector));
        Configuration = new DomainDictionary<string>(configuration ?? Array.Empty<KeyValuePair<string, string>>());
    }
}

public sealed record Snapshot
{
    public SnapshotId Id { get; }
    public ProjectId ProjectId { get; }
    public DateTimeOffset CapturedAt { get; }
    public DomainList<EndpointSnapshot> Endpoints { get; }
    public string ManifestHash { get; }

    public Snapshot(
        SnapshotId id,
        ProjectId projectId,
        DateTimeOffset capturedAt,
        IEnumerable<EndpointSnapshot> endpoints,
        string manifestHash)
    {
        Id = DomainGuard.Required(id, nameof(id));
        ProjectId = DomainGuard.Required(projectId, nameof(projectId));
        CapturedAt = capturedAt;
        Endpoints = new DomainList<EndpointSnapshot>(endpoints);
        ManifestHash = DomainGuard.Required(manifestHash, nameof(manifestHash));

        if (Endpoints.Count == 0)
        {
            throw new ArgumentException("A snapshot must include at least one endpoint.", nameof(endpoints));
        }

        if (Endpoints.Select(endpoint => endpoint.EndpointId).Distinct().Count() != Endpoints.Count)
        {
            throw new ArgumentException("Endpoint snapshot identifiers must be unique within a snapshot.", nameof(endpoints));
        }
    }
}

public sealed record EndpointSnapshot
{
    public StorageEndpointId EndpointId { get; }
    public ConnectorId ConnectorType { get; }
    public DomainDictionary<ValueNode> Metadata { get; }
    public string ManifestHash { get; }

    public EndpointSnapshot(
        StorageEndpointId endpointId,
        ConnectorId connectorType,
        IEnumerable<KeyValuePair<string, ValueNode>> metadata,
        string manifestHash)
    {
        EndpointId = DomainGuard.Required(endpointId, nameof(endpointId));
        ConnectorType = DomainGuard.Required(connectorType, nameof(connectorType));
        Metadata = new DomainDictionary<ValueNode>(metadata);
        ManifestHash = DomainGuard.Required(manifestHash, nameof(manifestHash));
    }
}

public sealed record ArtifactReference
{
    public ArtifactId Id { get; }
    public SystemId SystemId { get; }
    public StorageEndpointId EndpointId { get; }
    public string ArtifactType { get; }
    public string Identity { get; }

    public ArtifactReference(
        ArtifactId id,
        SystemId systemId,
        StorageEndpointId endpointId,
        string artifactType,
        string identity)
    {
        Id = DomainGuard.Required(id, nameof(id));
        SystemId = DomainGuard.Required(systemId, nameof(systemId));
        EndpointId = DomainGuard.Required(endpointId, nameof(endpointId));
        ArtifactType = DomainGuard.Required(artifactType, nameof(artifactType));
        Identity = DomainGuard.Required(identity, nameof(identity));
    }
}

public abstract record ValueNode;

public sealed record NullValue : ValueNode;

public sealed record StringValue(string Value) : ValueNode;

public sealed record IntegerValue(long Value) : ValueNode;

public sealed record DecimalValue(decimal Value) : ValueNode;

public sealed record BooleanValue(bool Value) : ValueNode;

public sealed record DateValue(DateOnly Value) : ValueNode;

public sealed record DateTimeValue(DateTimeOffset Value) : ValueNode;

public sealed record BinaryReferenceValue : ValueNode
{
    public string Reference { get; }
    public string? Hash { get; }

    public BinaryReferenceValue(string reference, string? hash = null)
    {
        Reference = DomainGuard.Required(reference, nameof(reference));
        Hash = hash;
    }
}

public sealed record CollectionValue : ValueNode
{
    public DomainList<ValueNode> Values { get; }

    public CollectionValue(IEnumerable<ValueNode> values) => Values = new DomainList<ValueNode>(values);
}

public sealed record ObjectValue : ValueNode
{
    public DomainDictionary<ValueNode> Values { get; }

    public ObjectValue(IEnumerable<KeyValuePair<string, ValueNode>> values) =>
        Values = new DomainDictionary<ValueNode>(values);
}

public enum RelationshipDirection
{
    Outgoing,
    Incoming
}

public sealed record RelationshipReference
{
    public string Type { get; }
    public ArtifactReference Target { get; }
    public RelationshipDirection Direction { get; }

    public RelationshipReference(string type, ArtifactReference target, RelationshipDirection direction)
    {
        Type = DomainGuard.Required(type, nameof(type));
        Target = DomainGuard.NotNull(target, nameof(target));
        Direction = direction;
    }
}

public sealed record TemporalMetadata
{
    public DateTimeOffset? EffectiveFrom { get; }
    public DateTimeOffset? EffectiveTo { get; }
    public DateTimeOffset? RecordedAt { get; }
    public long? Version { get; }

    public TemporalMetadata(
        DateTimeOffset? effectiveFrom = null,
        DateTimeOffset? effectiveTo = null,
        DateTimeOffset? recordedAt = null,
        long? version = null)
    {
        if (effectiveFrom is not null && effectiveTo < effectiveFrom)
        {
            throw new ArgumentException("EffectiveTo must not precede EffectiveFrom.", nameof(effectiveTo));
        }

        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version must not be negative.");
        }

        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        RecordedAt = recordedAt;
        Version = version;
    }
}

public sealed record ProvenanceMetadata
{
    public ConnectorId Connector { get; }
    public StorageEndpointId Endpoint { get; }
    public string Location { get; }
    public DateTimeOffset ObservedAt { get; }
    public string? SourceHash { get; }
    public DomainDictionary<string> Metadata { get; }

    public ProvenanceMetadata(
        ConnectorId connector,
        StorageEndpointId endpoint,
        string location,
        DateTimeOffset observedAt,
        string? sourceHash = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null)
    {
        Connector = DomainGuard.Required(connector, nameof(connector));
        Endpoint = DomainGuard.Required(endpoint, nameof(endpoint));
        Location = DomainGuard.Required(location, nameof(location));
        ObservedAt = observedAt;
        SourceHash = sourceHash;
        Metadata = new DomainDictionary<string>(metadata ?? Array.Empty<KeyValuePair<string, string>>());
    }
}

public sealed record RecordEnvelope
{
    public ArtifactReference Artifact { get; }
    public string SemanticType { get; }
    public DomainDictionary<ValueNode> Values { get; }
    public DomainList<RelationshipReference> Relationships { get; }
    public TemporalMetadata? Temporal { get; }
    public ProvenanceMetadata Provenance { get; }

    public RecordEnvelope(
        ArtifactReference artifact,
        string semanticType,
        IEnumerable<KeyValuePair<string, ValueNode>> values,
        ProvenanceMetadata provenance,
        IEnumerable<RelationshipReference>? relationships = null,
        TemporalMetadata? temporal = null)
    {
        Artifact = DomainGuard.NotNull(artifact, nameof(artifact));
        SemanticType = DomainGuard.Required(semanticType, nameof(semanticType));
        Values = new DomainDictionary<ValueNode>(values);
        Relationships = new DomainList<RelationshipReference>(relationships ?? Array.Empty<RelationshipReference>());
        Temporal = temporal;
        Provenance = DomainGuard.NotNull(provenance, nameof(provenance));
    }
}
