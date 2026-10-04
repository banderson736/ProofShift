# ADR 0006: Shadow Projection Runtime

- Status: Accepted
- Date: 2026-10-03

## Context

PS-0.5 must materialize configured graph output while preventing any accidental production-target write. Source connectors are already read-only and runtime secret values are callback-redacted. Projection must remain distinct from verification and must preserve partial output ancestry after failure.

## Decision

1. A separate `IShadowTargetConnector` contract lives in `ProofShift.Connectors.Abstractions`. Its `ShadowTargetContext` constructor and the Projection orchestrator require `SystemRole.ShadowTarget`; source, normal target, archive, missing, and unsupported roles fail closed before target preparation. Concrete target connectors are registered only at the CLI composition root.
2. Before preparing targets, projection rejects a PostgreSQL source and destination with the same resolved connection string and rejects filesystem shadow roots equal to, containing, or contained by live filesystem/CSV source roots.
3. PostgreSQL uses `proofshift_shadow_<run-guid>` schemas. Target tables are cloned from configured pre-existing templates with constraints/indexes but not defaults/sequences. Writes are parameterized and transactional per artifact. A schema-local unique identity registry stores SHA-256 identity hashes. The schema is retained after success/failure for inspection; cleanup is explicit operator deletion.
4. Filesystem output uses `<configured-root>/<run-guid>/<graph-node>/<relative-path>`. Relative paths are checked against that run root, binary data is streamed through a bounded buffer and verified by length/SHA-256, and a private SQLite identity/manifest index stores SHA-256 identity fingerprints plus node-scoped logical/storage paths. Raw artifact identity strings are not stored in this index. Failed/cancelled run directories are retained.
5. Projection is a run-level success/failure/cancellation boundary. A failure never becomes success because partial records exist. PostgreSQL transactions are per artifact; filesystem files are staged then atomically moved. Cancellation stops current work and retains already materialized state and journal.
6. The append-only JSONL Projection Journal writes pending ancestry before a target write, then records produced/failed terminal state, as well as excluded/metadata-only entries. It records source/target artifact references, graph target node, edge ID/name/version, transformation types/versions and source/target field mapping, and recovery metadata. It is execution ancestry, not evidence or correctness.
7. `proofshift-projection-fingerprint-v1` hashes the compiled graph hash and deterministic canonical values read back from materialized destinations, ordered by graph node, artifact identity, and field. It excludes run ID, times, generated schema, and machine paths. A read-back count mismatch fails the run.
8. Target artifact identity is derived from configured target identity fields using invariant typed canonicalization and a length-prefixed SHA-256 ArtifactId input. Physical run schema/directory and graph-node namespace do not participate in that semantic identity.
9. PS-0.5 executes direct source-to-target/archive copy/map/transform/split/archive/exclude paths. Relationship edges are metadata-only. Merge and chained target-input execution are deferred until deterministic grouping/join and scheduling semantics are specified. Unsupported operations/transforms fail before target preparation.
10. Source reads remain live observations. PS-0.5 does not claim snapshot semantics, verification, source disposition completeness, rollback, or production execution.

## Alternatives considered

### Write through the existing generic target interface

Rejected because it could blur production and shadow writes. A shadow-only interface makes the destination capability explicit and independently enforceable.

### One shared target schema/root

Rejected because repeat runs could append to or overwrite one another and failed output would be difficult to inspect independently.

### Delete failed output automatically

Rejected because partial output and its journal are needed to diagnose deterministic failures and cancellation.

### Execute merge with implicit row grouping

Rejected because the graph model does not yet define join/group semantics, keys, or conflict behavior.

## Consequences

- Projection run/journal models belong to the Projection assembly, not Domain or Evidence.
- PS-0.5 depends on Engine, Graph, and connector abstractions; concrete targets remain outside core projects.
- Operators must manage retained shadow state explicitly. No cleanup/rollback command is introduced.
- Docker CI must execute the SQL Server/PostgreSQL mixed-source scenario and PostgreSQL target integration tests without skips before PS-0.5 is accepted.