# ADR-0010: Recovery Readiness, Shadow Rehearsal, and Dry-Run Qualification

Status: Accepted

## Context

PS-0.7 establishes an immutable checkpoint-bound Verification Run and finalized Evidence Graph. The existing `RecoveryDefinition` classifies an edge, and Domain has a minimal `RecoveryPolicy`, but the Recovery assembly has no assessment, capability, rehearsal, plan, or qualification runtime. Source checkpoints represent pre-migration source state; they do not prove that a target can be restored. Shadow connectors expose only prepare/write/complete/read operations.

PS-0.8 must determine recovery readiness without changing production systems or mutating finalized PS-0.7 evidence.

## Decision

- `ProofShift.Recovery` owns recovery-policy loading/fingerprinting, transformation-loss analysis, edge/artifact recovery assessments, immutable recovery plans, compensator registration, shadow rehearsal orchestration, recovery evidence, and dry-run qualification. Core recovery logic consumes connector abstractions and remains domain-pack neutral.
- Recovery analysis receives a complete Verification result and its graph-scoped dispositions/lineage/journal facts. A non-passing, incomplete, or context-mismatched Verification cannot qualify.
- Recovery policy is read from the existing generic configuration document tree. The effective policy is versioned and fingerprinted independently of the encompassing configuration hash. Irreversible operations are prohibited by default; explicit policy configuration is the PS-0 approval acknowledgement, not business or regulatory approval.
- Add an optional shadow-only target recovery capability in connector abstractions. PostgreSQL checkpoints copy the isolated per-run schema into a recovery namespace in the same test database; filesystem checkpoints copy the isolated per-run root to a separate recovery directory. These are deterministic rehearsal mechanisms, not provider-native production backups.
- A recovery checkpoint binds system, endpoint, connector/version, source shadow run, graph, baseline target fingerprint, artifact count, checkpoint reference, and content-integrity hash. It is separate from `SourceCheckpoint`. Capability validation rechecks the checkpoint before restore.
- Rehearsal captures the populated shadow baseline, applies a deterministic controlled mutation, restores through the registered target recovery capability, re-reads physical targets, and compares the restored fingerprint with the baseline. Rehearsal never addresses `Target` role systems or production run contexts.
- Reverse recovery is accepted only when the configured transformation can be conservatively shown to have an unambiguous inverse. Unknown, normalization/lossy, aggregate, merge, exclude, and non-injective code-map cases are not declared reversible absent retained recovery data. Merge remains deferred consistently with Projection.
- Compensating strategies resolve through a registry keyed by strategy ID. Unknown IDs fail closed; no arbitrary scripts or free-form prose are executable.
- Recovery evidence is persisted as a separate immutable, versioned Recovery Evidence Graph that references the Verification Run and its Evidence Graph fingerprint. The PS-0.7 Evidence Graph is never appended to or rehashed in place.
- A completed Dry-Run Qualification binds checkpoint, projection manifest/fingerprint, Verification run/rules/evidence, recovery policy, assessment, rehearsal, and current target re-observation. Any mismatch, changed target, failed requirement, incomplete assessment, cancellation, or required rehearsal failure prevents qualification.
- Recovery assessment and dry-run semantic fingerprints are versioned and exclude run IDs/timestamps. Their persisted artifacts are independently integrity-checked.
- `proofshift dry-run` composes snapshot, checkpoint-backed project, verification, recovery analysis, required shadow rehearsal, and qualification. `proofshift recovery` reads a completed dry-run recovery artifact. Individual existing stage commands remain supported.

## Alternatives considered

### Treat the source checkpoint as the target restore point

Rejected because it proves the source state before migration, not the target state that must be restored or compensated.

### Append recovery findings to the PS-0.7 Evidence Graph

Rejected because that graph is finalized and fingerprinted. Mutation would invalidate its immutability and semantic fingerprint.

### Re-run Projection as a recovery rehearsal

Rejected because a rerun creates another shadow namespace; it does not demonstrate that the affected target namespace can be restored.

### Implement provider-native production backup/rollback

Rejected for PS-0.8. The initial PostgreSQL schema-copy and filesystem-copy mechanisms prove only isolated shadow rehearsal behavior.

### Put concrete connector switches in Recovery

Rejected because it reverses the connector boundary. Recovery resolves optional target recovery capabilities through connector abstractions and the CLI composition root.

## Consequences

- PostgreSQL and filesystem shadow connectors gain opt-in recovery-capability implementations; other connectors remain unsupported and cannot pass a required rehearsal.
- Recovery policy, analysis, plans, evidence, rehearsal records, qualification, and their fingerprints have separate versioned persisted representations.
- Exact restoration is required for the initial shadow rehearsal. Semantic compensation must declare and execute explicit validation; it is not treated as byte-identical restoration.
- Qualification means only that configured technical dry-run criteria passed. It is not business approval, regulator approval, production safety, or authorization to execute a migration.
- No production apply, production rollback, generalized merge recovery, enterprise backup administration, or milestone beyond PS-0.8 is introduced.
