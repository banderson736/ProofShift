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

### Source checkpoint lifecycle

The runtime checkpoint model is an immutable, finalized source basis. `Creating`, `Failed`, and `Cancelled` checkpoints may retain diagnostic partial data but cannot claim replayability or finalized fingerprints. Only a complete materialized checkpoint can be opened for replay.

Each `CheckpointEndpoint` identifies one graph source node, its system/endpoint/connector and version, selector hash and identity fields, endpoint capture interval, `Observed` or `Consistent` source guarantee, artifact/byte counts, source-set fingerprint, and materialized segment hash/reference. `SourceCheckpoint` adds project/configuration/graph identity, aggregate source fingerprint, manifest hash, capture window/skew, and `CrossSystemAtomic`. In the PS-0.6 SQL Server/filesystem/CSV scenario, `CrossSystemAtomic` is false.

Checkpoint types remain storage-neutral in Domain. The filesystem representation and replay implementation live in `ProofShift.Snapshots`; no path, connector implementation, database transaction, or serialization dependency is introduced into Domain.

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
public sealed record InstantValue(DateTimeOffset Value) : ValueNode;
public sealed record OffsetDateTimeValue(DateTimeOffset Value) : ValueNode;
public sealed record LocalDateTimeValue(DateTime Value) : ValueNode;
public sealed record BinaryReferenceValue(string Reference, long ContentLength, string Sha256) : ValueNode;
public sealed record CollectionValue(IReadOnlyCollection<ValueNode> Values) : ValueNode;
public sealed record ObjectValue(IReadOnlyDictionary<string, ValueNode> Values) : ValueNode;
```

`InstantValue` represents an absolute instant normalized to UTC. `OffsetDateTimeValue` preserves an explicit source offset. `LocalDateTimeValue` preserves wall-clock fields without assigning a timezone. Date-only values remain `DateValue`. Connectors must not infer UTC for SQL `datetime`/`datetime2` or PostgreSQL `timestamp without time zone`.

`BinaryReferenceValue` carries a content reference, byte length, and SHA-256. The reference is opened through the matching source connector's binary stream resolver; large binary values are never copied into record envelopes as `byte[]`.

PS-0.4 maps database UUID/GUID values to invariant string values and decimals directly to `DecimalValue`. Floating-point source values remain invariant strings rather than being rounded through `decimal`.

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
    public string? GraphCanonicalizationVersion { get; init; }
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
    public required ArtifactSelector Selector { get; init; }
}

public sealed record ArtifactSelector
{
    public required string Kind { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public required IReadOnlyCollection<string> IdentityFields { get; init; }
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
    public required string Name { get; init; }
    public required IReadOnlyCollection<MigrationNodeId> Sources { get; init; }
    public required IReadOnlyCollection<MigrationNodeId> Targets { get; init; }
    public required MigrationOperation Operation { get; init; }
    public required RecoveryDefinition Recovery { get; init; }
    public required string Version { get; init; }
}
```

Edges represent one-or-more inputs and outputs. An explicit `Exclude` operation may have no target nodes; other operations require target references. Graph compilation preserves the external edge key in `Name` and derives the strongly typed internal ID deterministically.

## Migration operations

Compiled operation definition:

```csharp
public sealed record MigrationOperation
{
    public required MigrationOperationType Type { get; init; }
    public required IReadOnlyCollection<TransformationStep> Steps { get; init; }
    public required IReadOnlyCollection<TransformationFieldDefinition> Fields { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public required bool IsDestructive { get; init; }
}

public sealed record TransformationFieldDefinition
{
    public required string Target { get; init; }
    public string? Source { get; init; }
    public required IReadOnlyCollection<TransformationStep> Pipeline { get; init; }
}
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

Each field definition has a target name, optional source expression, and ordered pipeline. Steps have an explicit version; PS-0.3 assigns version `1` when a v1 graph omits one.

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

The immutable edge definition is configuration, not demonstrated capability. PS-0.8 runtime concepts (`RecoveryAssessment`, `RecoveryCoverage`, `RecoveryPlan`, `RecoveryRehearsal`, and `DryRunQualification`) live in `ProofShift.Recovery`, not Domain. They bind to the Verification Run, source checkpoint, completed projection manifest, recovery policy, graph-scoped dispositions, and target lineage.

`SourceCheckpoint` and `ShadowRecoveryCheckpoint` are distinct. The former is the known pre-migration source input. The latter is a connector-backed checkpoint of an isolated populated shadow target used only for recovery rehearsal. Recovery checkpoints carry system/endpoint/connector/run/graph identity, baseline fingerprint, target artifact count, opaque connector reference, content digest, and capture time.

Recovery rehearsal records distinguish the strategy result fingerprint from the exact shadow cleanup fingerprint. Exact restore requires equality with the captured baseline. Semantic compensation is accepted only through a registered compensator's explicit validator; before dry-run qualification, the isolated shadow namespace is returned to the verified baseline and re-observed. Recovery evidence is a separate immutable graph referencing, not mutating, the finalized Verification Evidence Graph.

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

PS-0.9's generic runtime `VerificationArtifactRecord` carries one source, expected-target, or observed-target normalized `RecordEnvelope` scoped to a graph node. `IVerificationWorkspace.ReadArtifactRecordsAsync` is an ordered asynchronous stream, optionally filtered by node/semantic type and sorted by normalized fields. The implementation persists the temporary typed working set in SQLite and removes it when the run completes. Pack rules may aggregate a current group, but must not retain the whole production-scale dataset.

Pension concepts/rules remain outside Domain. `Pension.Member`, `Pension.Employment`, `Pension.Contribution`, `Pension.ServiceCredit`, `Pension.Beneficiary`, `Pension.RetirementElection`, `Pension.BenefitPayment`, and `Pension.Document` are pack-owned semantic types. Their evidence uses generic `EvidenceRecord`, graph-scoped `EvidenceReference`, `VerificationArtifactRecord`, dispositions, and lineage.

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

public sealed record SourceInspection
{
    public required SourceInspectionStatus Status { get; init; }
    public required IReadOnlyCollection<SourceColumn> Columns { get; init; }
    public required IReadOnlyCollection<string> PrimaryKeyFields { get; init; }
    public required IReadOnlyCollection<string> IdentityFields { get; init; }
    public long? EstimatedRecords { get; init; }
    public long? Files { get; init; }
    public long? Bytes { get; init; }
    public required IReadOnlyCollection<ConnectorIssue> Issues { get; init; }
}
```

The source/runtime contracts and metadata types live in `ProofShift.Connectors.Abstractions`, not in the domain assembly. Runtime configuration is redacted and separate from hashed endpoint identity.

```csharp
public interface IShadowTargetConnector
{
    ConnectorId Id { get; }
    string Version { get; }

    Task PrepareAsync(ShadowTargetContext context, ArtifactSelector selector, CancellationToken cancellationToken);

    Task WriteAsync(ShadowWriteRequest request, CancellationToken cancellationToken);

    Task CompleteAsync(ShadowTargetContext context, CancellationToken cancellationToken);

    IAsyncEnumerable<RecordEnvelope> ReadAsync(ReadRequest request, CancellationToken cancellationToken);
}
```

The runtime contract is owned by `ProofShift.Connectors.Abstractions`; it accepts only `ShadowTargetContext`, which requires `SystemRole.ShadowTarget`. PostgreSQL and filesystem implement this interface. There is no production-target connector contract or production-write mode in PS-0.5. Projection run/journal types live in `ProofShift.Projection`, not Domain, and projection records are not verification evidence.

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
