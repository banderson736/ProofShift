# ADR-0007: Materialized Source Checkpoints

Status: Accepted

## Context

PS-0.6 needs repeatable projection inputs even after the source systems are changed or unavailable. The supported source set includes transactional databases, filesystems, and CSV, which cannot participate in one shared atomic transaction. A checkpoint must therefore report per-endpoint consistency honestly and must not describe a multi-system capture as globally point-in-time consistent.

Snapshot persistence must also stay outside Domain, Graph, and concrete connector dependencies. Structured values, artifact identities, original provenance, and large binary content must survive capture/replay without loading full datasets into memory.

## Decision

- Add a `ProofShift.Snapshots` assembly depending on Domain, Configuration, Graph, Engine, and connector abstractions. It contains checkpoint capture orchestration, the local materialized store, typed segment/manifest codecs, and checkpoint-backed source streams.
- Add an optional `ICheckpointSourceConnector` capability. Existing source connectors retain their normal streaming contract. Relational connectors declare and use provider-supported transaction isolation only for checkpoint reads; PostgreSQL uses repeatable read and SQL Server uses an endpoint-scoped explicit strategy, defaulting to Observed. No database-wide settings are changed. See ADR-0016 for the PS-0.10A SQL Server policy.
- Filesystem and CSV captures are classified `Observed`. Filesystem captures compare a matched-file inventory before and after the read; CSV captures compare the source file length, modification time, and SHA-256 before and after logical-row parsing. These checks detect ordinary concurrent changes but do not claim an atomic filesystem snapshot.
- Materialize each graph source node into a versioned typed NDJSON segment. Materialize binary content as bounded streams into SHA-256-addressed blobs and verify declared length/hash during copy.
- Finalize the manifest and publish `Complete` only after all required source segments are present. Failed and cancelled captures retain state/partial output but are rejected by replay.
- Use a generic source-artifact stream provider at the Projection boundary. Checkpoint mode validates configuration hash, graph hash, exact source-node coverage, endpoint identity, and selector hash before target preparation; it never falls back to live reads.
- Verify canonical manifest hash, segment length/hash, per-record fingerprint, endpoint and aggregate source fingerprints, and all referenced binary blobs before a checkpoint is made available for projection.
- Preserve source `ArtifactReference` and source provenance. Add checkpoint ID/source-node/record-fingerprint metadata without placing resolved secrets in the manifest.
- For mixed SQL Server/filesystem/CSV capture, set `CrossSystemAtomic` to false and report the aggregate capture interval/skew.

## Consequences

- `proofshift snapshot <config>` creates a local checkpoint under `.proofshift/checkpoints`.
- `proofshift project <config> --checkpoint <id-or-path>` consumes only the materialized checkpoint as source input. Projection remains shadow-only.
- Identical checkpoint bytes and graph/configuration inputs can be replayed independently of current live source contents.
- Local filesystem access and its host permissions remain the trust boundary. A local manifest hash detects modification only when the manifest itself is trusted; this ADR does not add signatures, encryption, or remote checkpoint storage.
- Filesystem/CSV observations can detect source drift during capture but do not provide transactionally consistent snapshots. SQL endpoint consistency does not create cross-endpoint atomicity.
- This decision does not begin PS-0.7 or add evidence-graph, recovery execution, UI, SaaS, or production-write behavior.

## Alternatives Considered

- Put snapshot logic in Projection: rejected because it couples projection execution to one persistence/replay mechanism.
- Add snapshot storage to Domain or Graph: rejected because those modules must remain connector- and storage-neutral.
- Require every connector to implement a separate checkpoint interface: rejected in favor of an optional capability so existing readers and future observed-only connectors remain compatible.
- Claim a single atomic checkpoint across all sources: rejected because the participating systems have no shared transaction boundary.
- Store all artifacts in memory before writing: rejected because memory use would grow with source size.
