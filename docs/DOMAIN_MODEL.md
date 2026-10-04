# ProofShift Domain Model

This document is the intended domain contract for PS-0.1. Names may be refined during implementation, but conceptual responsibilities should not be collapsed without an ADR.

## Strong identifiers

Use strongly typed IDs instead of naked GUIDs/strings in domain APIs.

```csharp
public readonly record struct ProjectId(Guid Value);
public readonly record struct RunId(Guid Value);
public readonly record struct SnapshotId(Guid Value);
public readonly record struct MigrationPlanId(Guid Value);
public readonly record struct MigrationGraphId(Guid Value);
public readonly record struct MigrationNodeId(Guid Value);
public readonly record struct MigrationEdgeId(Guid Value);
public readonly record struct EvidenceId(Guid Value);
public readonly record struct ArtifactId(string Value);
public readonly record struct RuleId(string Value);
public readonly record struct ConnectorId(string Value);
```

## Project

Long-running modernization effort containing multiple snapshots, plans, dry runs, verification runs, and eventual production observations.

```csharp
public sealed record Project
{
    public required ProjectId Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

## SystemDefinition

A logical source/target/archive/shadow system containing one or more storage endpoints.

```csharp
public sealed record SystemDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SystemRole Role { get; init; }
    public required IReadOnlyCollection<StorageEndpointDefinition> StorageEndpoints { get; init; }
}

public enum SystemRole
{
    Source,
    Target,
    ShadowTarget,
    Archive
}
```

## StorageEndpointDefinition

Binds a logical storage location to a connector. Persisted configuration must reference secrets rather than contain secrets.

```csharp
public sealed record StorageEndpointDefinition
{
    public required string Id { get; init; }
    public required ConnectorId Connector { get; init; }
    public required IReadOnlyDictionary<string, string> Configuration { get; init; }
}
```

## Snapshot

Immutable logical source checkpoint.

```csharp
public sealed record Snapshot
{
    public required SnapshotId Id { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required IReadOnlyCollection<EndpointSnapshot> Endpoints { get; init; }
    public required string ManifestHash { get; init; }
}

public sealed record EndpointSnapshot
{
    public required string EndpointId { get; init; }
    public required string ConnectorType { get; init; }
    public required IReadOnlyDictionary<string, object?> Metadata { get; init; }
    public required string ManifestHash { get; init; }
}
```

## Artifact

The smallest independently accountable source/target thing. More general than a database row.

Examples: row, FHIR resource, CSV record, document, event, transaction, API object, search document.

```csharp
public sealed record ArtifactReference
{
    public required ArtifactId Id { get; init; }
    public required string SystemId { get; init; }
    public required string EndpointId { get; init; }
    public required string ArtifactType { get; init; }
    public required string Identity { get; init; }
}
```

## RecordEnvelope

Storage-independent normalized representation emitted by connectors.

```csharp
public sealed record RecordEnvelope
{
    public required ArtifactReference Artifact { get; init; }
    public required string SemanticType { get; init; }
    public required IReadOnlyDictionary<string, ValueNode> Values { get; init; }
    public IReadOnlyCollection<RelationshipReference> Relationships { get; init; }
        = Array.Empty<RelationshipReference>();
    public TemporalMetadata? Temporal { get; init; }
    public required ProvenanceMetadata Provenance { get; init; }
}
```

## Typed values

Avoid reducing values to `object` in normalized record content.

```csharp
public abstract record ValueNode;
public sealed record NullValue : ValueNode;
public sealed record StringValue(string Value) : ValueNode;
public sealed record IntegerValue(long Value) : ValueNode;
public sealed record DecimalValue(decimal Value) : ValueNode;
public sealed record BooleanValue(bool Value) : ValueNode;
public sealed record DateValue(DateOnly Value) : ValueNode;
public sealed record DateTimeValue(DateTimeOffset Value) : ValueNode;
public sealed record BinaryReferenceValue(string Reference, string? Hash) : ValueNode;
public sealed record CollectionValue(IReadOnlyCollection<ValueNode> Values) : ValueNode;
public sealed record ObjectValue(IReadOnlyDictionary<string, ValueNode> Values) : ValueNode;
```

## Relationships

Relationships are explicit rather than implicit foreign keys.

```csharp
public sealed record RelationshipReference
{
    public required string Type { get; init; }
    public required ArtifactReference Target { get; init; }
    public RelationshipDirection Direction { get; init; }
}
```

Example semantic relationships:

```text
member HAS_BENEFICIARY beneficiary
member HAS_EMPLOYMENT employment
payment PAID_TO retiree
document BELONGS_TO member
```

## Temporal metadata

```csharp
public sealed record TemporalMetadata
{
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
    public long? Version { get; init; }
}
```

## Provenance

```csharp
public sealed record ProvenanceMetadata
{
    public required string Connector { get; init; }
    public required string Endpoint { get; init; }
    public required string Location { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? SourceHash { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}
```

## MigrationPlan

Immutable plan version. A material change creates a new plan version.

```csharp
public sealed record MigrationPlan
{
    public required MigrationPlanId Id { get; init; }
    public required int Version { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required SnapshotId SourceSnapshotId { get; init; }
    public required MigrationGraph Graph { get; init; }
    public required VerificationPolicy Verification { get; init; }
    public required RecoveryPolicy Recovery { get; init; }
    public required string ConfigurationHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

## MigrationGraph

```csharp
public sealed record MigrationGraph
{
    public required MigrationGraphId Id { get; init; }
    public required IReadOnlyCollection<MigrationNode> Nodes { get; init; }
    public required IReadOnlyCollection<MigrationEdge> Edges { get; init; }
    public required string GraphHash { get; init; }
}
```

### Nodes

```csharp
public sealed record MigrationNode
{
    public required MigrationNodeId Id { get; init; }
    public required string Name { get; init; }
    public required MigrationNodeType Type { get; init; }
    public required string SemanticType { get; init; }
    public required string SystemId { get; init; }
    public required string EndpointId { get; init; }
}

public enum MigrationNodeType
{
    Source,
    Target,
    Archive,
    Derived,
    Aggregate
}
```

### Edges

```csharp
public sealed record MigrationEdge
{
    public required MigrationEdgeId Id { get; init; }
    public required MigrationNodeId From { get; init; }
    public required MigrationNodeId To { get; init; }
    public required MigrationOperation Operation { get; init; }
    public required RecoveryDefinition Recovery { get; init; }
    public required string Version { get; init; }
}
```

## Migration operations

Base abstraction:

```csharp
public abstract record MigrationOperation;
```

Expected operations:

- Copy
- Map
- Transform
- Split
- Merge
- Aggregate
- Derive
- Archive
- Exclude
- Relationship

Not every operation needs to be fully implemented in PS-0.1.

## Transformations

Transformations are composable and versioned.

Initial operations:

- copy
- rename
- trim
- normalize-string
- normalize-date
- code-map
- concatenate
- split
- lookup
- calculate
- archive
- exclude

Arbitrary user code is out of scope for PS-0.

## Recovery

```csharp
public sealed record RecoveryDefinition
{
    public required RecoveryMode Mode { get; init; }
    public string? Strategy { get; init; }
    public bool RequiresSnapshot { get; init; }
    public string? Justification { get; init; }
}

public enum RecoveryMode
{
    Reverse,
    Restore,
    Compensate,
    Irreversible
}
```

## ArtifactDisposition

```csharp
public enum ArtifactDisposition
{
    Migrated,
    Transformed,
    Derived,
    Archived,
    Excluded,
    Superseded,
    Failed,
    Unaccounted
}

public sealed record ArtifactDispositionRecord
{
    public required ArtifactReference Source { get; init; }
    public required ArtifactDisposition Disposition { get; init; }
    public IReadOnlyCollection<ArtifactReference> Targets { get; init; }
        = Array.Empty<ArtifactReference>();
    public string? Reason { get; init; }
}
```

Invariant: every source artifact has one explainable disposition; `Unaccounted` fails readiness.

## Lineage

```csharp
public sealed record LineageRecord
{
    public required ArtifactReference Target { get; init; }
    public required IReadOnlyCollection<ArtifactReference> Sources { get; init; }
    public required IReadOnlyCollection<MigrationEdgeId> Path { get; init; }
    public required string PlanHash { get; init; }
}
```

Invariant: every target artifact has lineage.

## Verification rules

```csharp
public interface IVerificationRule
{
    RuleId Id { get; }
    string Version { get; }
    VerificationScope Scope { get; }

    Task<RuleEvaluation> EvaluateAsync(
        VerificationContext context,
        CancellationToken cancellationToken);
}

public enum VerificationScope
{
    Attribute,
    Entity,
    Relationship,
    Timeline,
    Aggregate,
    Accounting,
    Recovery
}
```

## Evidence

```csharp
public enum EvidenceResult
{
    Pass,
    Fail,
    Warning,
    NotApplicable
}

public enum EvidenceType
{
    Observation,
    Transformation,
    Comparison,
    Relationship,
    Timeline,
    Aggregate,
    Accounting,
    Recovery,
    Decision
}

public sealed record EvidenceReference
{
    public ArtifactId? ArtifactId { get; init; }
    public EvidenceId? EvidenceId { get; init; }
}

public sealed record EvidenceRecord
{
    public required EvidenceId Id { get; init; }
    public required RunId RunId { get; init; }
    public required EvidenceType Type { get; init; }
    public required RuleId RuleId { get; init; }
    public required string RuleVersion { get; init; }
    public required EvidenceResult Result { get; init; }
    public required IReadOnlyCollection<EvidenceReference> Inputs { get; init; }
    public EvidenceValue? Expected { get; init; }
    public EvidenceValue? Actual { get; init; }
    public required string Explanation { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }
}
```

The implementation must avoid storing duplicate full payloads for every piece of evidence. Evidence should reference immutable observations where possible.

## Runs

```csharp
public enum RunType
{
    Inspection,
    Snapshot,
    Projection,
    Verification,
    DryRun,
    ProductionObservation
}

public sealed record MigrationRun
{
    public required RunId Id { get; init; }
    public required MigrationPlanId PlanId { get; init; }
    public required int PlanVersion { get; init; }
    public required RunType Type { get; init; }
    public required RunStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required RuntimeFingerprint Runtime { get; init; }
}
```

Runtime fingerprint records ProofShift/configuration/graph/connector/pack versions so a run can be reproduced and audited.

## Connector contracts

```csharp
public interface ISourceConnector
{
    ConnectorId Id { get; }
    string Version { get; }

    Task<SourceInspection> InspectAsync(
        StorageEndpointDefinition endpoint,
        CancellationToken cancellationToken);

    Task<EndpointSnapshot> SnapshotAsync(
        StorageEndpointDefinition endpoint,
        CancellationToken cancellationToken);

    IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ReadRequest request,
        CancellationToken cancellationToken);
}

public interface ITargetConnector
{
    ConnectorId Id { get; }
    string Version { get; }

    Task PrepareShadowAsync(
        ShadowTargetRequest request,
        CancellationToken cancellationToken);

    Task WriteAsync(
        WriteRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<RecordEnvelope> ReadAsync(
        ReadRequest request,
        CancellationToken cancellationToken);
}
```

A concrete connector may implement both.

## Domain-pack contract

```csharp
public interface IDomainPack
{
    string Id { get; }
    string Version { get; }

    IReadOnlyCollection<SemanticTypeDefinition> SemanticTypes { get; }
    IReadOnlyCollection<IVerificationRule> DefaultRules { get; }
    IReadOnlyCollection<MappingTemplate> MappingTemplates { get; }
}
```

PS-0 pack: `proofshift.pension`.
