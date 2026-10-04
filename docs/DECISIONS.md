# Architectural Decisions Summary

This file is a quick index. Detailed decisions may be added as ADRs under `docs/adr/`.

## Accepted decisions

### D-001 — ProofShift is migration assurance, not generic DB diff
Status: Accepted

ProofShift's core value is semantic, auditable, recoverable migration assurance across heterogeneous systems.

### D-002 — Public pensions are the first domain pack
Status: Accepted

The core remains domain-neutral. Pension is the initial demonstration/commercial wedge based on current market research.

### D-003 — Migration Graph, Evidence Graph, Recovery Model are foundational
Status: Accepted

Do not collapse these into one flat job/result structure.

### D-004 — History is first-class
Status: Accepted

Current state alone is insufficient.

### D-005 — Dry runs create shadow output
Status: Accepted

Projection should materialize realistic isolated target state, not only predict changes.

### D-006 — Verification can operate independently of execution
Status: Accepted

External migration tools/vendors remain supported. Future ProofShift orchestration is optional.

### D-007 — Every source artifact has a disposition
Status: Accepted

Unaccounted artifacts fail readiness.

### D-008 — Every target artifact has lineage
Status: Accepted

Targets without provenance fail readiness.

### D-009 — Recovery readiness is part of migration readiness
Status: Accepted

Destructive transformations require explicit recovery strategy. PS-0 rejects irreversible operations.

### D-010 — Domain packs, not a universal business schema
Status: Accepted

Generic core concepts remain small; industry semantics are pack-specific.

### D-011 — .NET 10
Status: Accepted for bootstrap

New repository targets current LTS .NET 10. Reconsider only for a concrete customer/platform constraint.

### D-012 — CLI first
Status: Accepted

PS-0 is usable/testable without web UI.

### D-013 — Minimal infrastructure
Status: Accepted

No Kafka/Redis/Kubernetes merely because the team knows them. Add only from measured requirements.

### D-014 — Canonical fingerprints are defined by the compiler
Status: Accepted

PS-0.2 defines `proofshift-config-canonical-v1`; PS-0.3 defines `proofshift-graph-canonical-v1`. Both compute SHA-256 fingerprints without resolved secret values. See `docs/adr/0002-graph-fingerprint-canonicalization.md`.

### D-015 — Configuration project identifiers remain distinct from domain IDs
Status: Accepted

The version-1 configuration project ID is a validated string identifier. The PS-0.1 `ProjectId` is a GUID, and no stable conversion contract is specified. PS-0.2 retains the configured project as a DTO instead of inventing an identity mapping; plan binding must define the relationship explicitly.

### D-016 — Compile graph keys to deterministic internal IDs
Status: Accepted

PS-0.3 compiles connector-neutral selectors and many-to-many edges, derives scoped deterministic UUIDv8-style GUIDs from external graph keys, and hashes `proofshift-graph-canonical-v1`. Relationship-only cycles are warnings; execution/destructive cycles are errors. See `docs/adr/0003-migration-graph-compilation.md`.

### D-017 — Read-only connector inspection and runtime secret boundary
Status: Accepted

PS-0.4 places read-only source contracts and redacting runtime settings in connector abstractions, inspection orchestration in Engine, and concrete registrations in the CLI composition root. Relational identifiers are validated/quoted, CSV duplicate checks use a temporary disk-backed identity index, and file/binary hashes stream. Inspection/read is not snapshotting or evidence. See `docs/adr/0004-source-connector-runtime.md`.

### D-018 — Preserve timestamp semantics and retrievable binary references
Status: Accepted

PS-0.4 source values distinguish instants, explicit offsets, and local timestamps without assigning a machine timezone. Binary values carry reference, length, and SHA-256, and source binary content is reopened as a stream through connector abstractions. See `docs/adr/0005-source-value-fidelity.md`.

### D-019 — PS-0.5 is gated on Docker-backed connector acceptance
Status: Accepted

PS-0.5 projection must not begin until PostgreSQL and SQL Server Testcontainers integration tests pass in Docker-capable CI. Local skips do not satisfy this gate. The Ubuntu workflow runs the full suite, including the Linux symlink-boundary test.
