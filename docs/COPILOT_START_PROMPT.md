# First GitHub Copilot IDE Prompt

Paste the following into the IDE agent after opening the repository.

---

You are implementing ProofShift PS-0.1, the Domain Foundation.

Before changing code, read all of the following completely:

- `README.md`
- `AGENTS.md`
- `.github/copilot-instructions.md`
- `docs/PRODUCT_CONTEXT.md`
- `docs/PRODUCT_PRINCIPLES.md`
- `docs/ARCHITECTURE.md`
- `docs/DOMAIN_MODEL.md`
- `docs/CONFIGURATION.md`
- `docs/PS0_SPECIFICATION.md`
- `docs/PENSION_PACK.md`
- `docs/ROADMAP.md`
- `docs/DECISIONS.md`
- `docs/adr/0001-foundational-architecture.md`

Treat those documents as the source of truth for product intent and architecture.

## Task

Implement **PS-0.1 Domain Foundation only**. Do not implement PS-0.2 configuration parsing or any database/file connectors yet.

Build the domain types and invariants needed for:

- strongly typed identifiers;
- Project;
- SystemDefinition / StorageEndpointDefinition;
- Snapshot / EndpointSnapshot;
- ArtifactReference;
- RecordEnvelope and typed ValueNode hierarchy;
- RelationshipReference;
- TemporalMetadata;
- ProvenanceMetadata;
- MigrationPlan;
- MigrationGraph / MigrationNode / MigrationEdge;
- minimal MigrationOperation and TransformationStep abstractions required for the model;
- RecoveryDefinition / RecoveryMode;
- ArtifactDisposition / ArtifactDispositionRecord;
- LineageRecord;
- verification scopes/result abstractions;
- EvidenceRecord / EvidenceReference / EvidenceType / EvidenceResult;
- MigrationRun / RunType / runtime fingerprint;
- minimal connector/domain-pack contracts only if they are required to express the domain boundary cleanly.

## Invariants to implement/test

1. Core projects must not reference concrete domain packs or connector implementations.
2. Empty/invalid strongly typed IDs and required identifiers should fail early where appropriate.
3. Migration graph validation must identify dangling node references.
4. A destructive migration operation must have explicit recovery metadata.
5. Artifact disposition semantics must preserve the rule that every source artifact eventually receives an explainable disposition.
6. Target lineage must reference at least one source artifact and a migration path where appropriate.
7. Domain collections should not expose mutable internals.
8. Domain equality/value semantics should be deterministic.
9. Prepare, but do not over-engineer, deterministic graph/config hashing. If graph hashing requires a decision, record it in an ADR rather than inventing an unstable serialization.
10. No domain type may depend on SQL Server, PostgreSQL, YAML, EF Core, ASP.NET, or other infrastructure.

## Testing

Replace the bootstrap placeholder tests in the relevant PS-0.1 projects with meaningful xUnit tests. Do not leave tests that only assert `true`.

Run focused tests, then the complete solution test suite if the local SDK allows it.

## Non-goals

Do not add:

- UI;
- web API;
- database persistence;
- YAML parsing;
- SQL Server/PostgreSQL code;
- Docker/Kubernetes;
- Kafka/Redis;
- FHIR;
- authentication;
- AI;
- production migration execution.

## Completion report

When finished, report:

1. files/projects changed;
2. domain types implemented;
3. invariants enforced;
4. tests added and their results;
5. any architectural decision/ADR added;
6. PS-0.1 items still incomplete;
7. whether the repository is ready to proceed to PS-0.2.

Do not proceed to PS-0.2 in the same task.

---
