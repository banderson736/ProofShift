# Architectural Decisions Summary

This file is a quick index. Detailed decisions may be added as ADRs under `docs/adr/`.

PS-0.10B authoring contracts: see [ADR-0018](adr/0018-structured-rule-authoring-contract.md). Rule-document v2 preserves typed nested values; legacy documents retain their interpretation/fingerprints. Provider-owned descriptors validate exact rule versions and configured types. This is an explicitly assigned authoring slice, not PS-0.10B acceptance.

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

PS-0.5 projection must not begin until PostgreSQL and SQL Server Testcontainers integration tests pass in Docker-capable CI. Local skips do not satisfy this gate. The Ubuntu workflow runs the full suite, including the Linux symlink-boundary test. Gate satisfied by [GitHub Actions run 37173803105](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267): 67 passed, 0 failed, 0 skipped.

### D-020 — Projection is shadow-only and distinct from verification
Status: Accepted

PS-0.5 writes only through `IShadowTargetConnector` contexts for systems with role `shadow-target`. PostgreSQL schemas and filesystem directories are isolated per run; the append-only Projection Journal records execution ancestry, and a versioned fingerprint is computed from read-back materialized state. Projection is not evidence of correctness. Merge/chained execution is deferred where deterministic semantics are undefined. See `docs/adr/0006-shadow-projection-runtime.md`.

Acceptance status: accepted after [GitHub Actions run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298), which passed all 82 tests with zero failures and zero skips.

### D-021 — Checkpoints are local materialized inputs with endpoint-only consistency claims
Status: Accepted

PS-0.6 implements immutable materialized source checkpoints in a separate `ProofShift.Snapshots` assembly. The generic Projection source provider validates exact configuration/graph/source coverage and fails closed without live-read fallback. Relational connectors use provider-supported checkpoint transactions; filesystem/CSV remain observed reads with drift detection. Mixed-source checkpoints report `CrossSystemAtomic: false`. Incomplete checkpoints cannot be replayed, and integrity is checked before shadow destinations are prepared. See `docs/adr/0007-materialized-source-checkpoints.md`.

PS-0.6 was accepted after Docker-backed [GitHub Actions run 37180351598](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with zero failures and zero skips. PS-0.7 Semantic Verification & Evidence Graph was accepted after Docker-backed [GitHub Actions run 37185122084](https://github.com/banderson736/ProofShift/actions/runs/37185122084/job/111385343893) passed 96 tests with zero failures and zero skips. PS-0.8 Recovery Readiness & Dry-Run Qualification was accepted after Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed 103 tests with zero failures and zero skips.

### D-022 — Recovery readiness is separate from source checkpointing and finalized verification evidence
Status: Accepted for PS-0.8

Recovery assessment consumes a complete Verification result and actual graph-scoped journal/lineage coverage. Source checkpoints remain pre-migration source inputs; target restore capability must be separately captured and validated. PostgreSQL per-run schema copies and filesystem per-run tree copies are shadow-only rehearsal mechanisms and do not claim provider-native production backup behavior. Recovery evidence is a separate immutable graph referencing the finalized PS-0.7 evidence. Qualification re-observes the physical target and requires exact shadow cleanup to the verified baseline, even when a registered semantic compensator validates a non-identical intermediate recovery result. See `docs/adr/0010-recovery-readiness-and-dry-run-qualification.md`.

PS-0.8 was accepted after Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed the full solution test suite: 103 passed, 0 failed, and 0 skipped.

### D-023 — Pension semantic rules consume generic ordered verification-record streams
Status: Accepted for PS-0.9

PS-0.9 adds `IVerificationWorkspace.ReadArtifactRecordsAsync`, which streams typed source, expected-target, and actual-target records from the private disk-backed SQLite workspace and can order them by normalized fields. Pension rules merge ordered groups to bound transaction, payment, service-credit, and timeline working sets. The shared Verification layer contains no pension concepts; pack rules remain registered through `PensionPack`. See `docs/adr/0011-streamable-verification-record-observations.md`.

PS-0.9 was accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips. The integrated fast scenario captures physical SQL Server, CSV, and filesystem sources (8,495 checkpoint artifacts, including 275 binary payloads), projects 8,595 PostgreSQL/filesystem targets, and runs corrected and projected-defective datasets through generic Verification and Recovery. Persisted reports show 149 defects/not-qualified versus zero/qualified; CLI comparison resolves all 149 and attributes changes to source/checkpoint and graph. The integrated fast run measured 396,017 ms and 519,651,328 bytes peak working set. The large-scale benchmark remains generator-only. PS-0.10 is not assigned.

### D-024 — Verification SQLite is disposable WAL/NORMAL scratch state
Status: Accepted for PS-0.10A

The private per-run Verification workspace uses SQLite WAL with `synchronous=NORMAL` and retains bounded transactions. Power loss may lose its latest committed scratch transactions, so incomplete Verification is rerun and never accepted. Final Evidence Graph persistence remains a separate durable operation. See `docs/adr/0015-disposable-verification-workspace-sqlite-profile.md`.

### D-025 — SQL Server checkpoint consistency is explicit and endpoint-scoped
Status: Accepted for PS-0.10A

SQL Server checkpoint capture defaults to Observed. Transaction-consistent reads require explicit endpoint isolation; Serializable is opt-in, Snapshot is not enabled automatically, and any permitted downgrade is recorded in the checkpoint manifest. PostgreSQL retains repeatable-read semantics, and no endpoint policy claims cross-system atomicity. Snapshot manifest canonicalization is v2 to integrity-bind requested/effective strategy and downgrade metadata. See `docs/adr/0016-explicit-sqlserver-checkpoint-consistency.md`.

### D-026 — Persist Verification ledgers and stream Evidence artifacts
Status: Accepted for PS-0.10A

Verification and Recovery consume a private indexed SQLite ledger rather than successful result-sized lineage/disposition/journal arrays. Recovery exposes aggregate coverage plus on-demand ledger queries. Filesystem Evidence uses a v2 manifest and streamed NDJSON segment with integrity hashes; successful source accounting uses population evidence while failures remain detailed. Generic typed ordering keys are materialized and indexed at workspace ingest. Evidence canonicalization is explicitly v2 for the population-evidence policy. See `docs/adr/0017-persisted-verification-ledgers-and-streaming-evidence.md`.
