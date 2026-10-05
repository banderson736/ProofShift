# ProofShift Architecture

## Architectural objective

ProofShift must model a **logical system migration**, not a one-database-to-one-database copy.

A source system may contain multiple storage mechanisms:

```text
SQL Server
DB2 / Oracle
Object storage
File shares
CSV / fixed-width extracts
Search indexes
Event streams
REST APIs
Historical archives
```

A target may likewise be distributed:

```text
PostgreSQL
Object storage
Search
Event streams
Vendor APIs
SaaS domain objects
```

All participating stores can belong to one logical migration.

## System lifecycle

```text
DISCOVER
   ↓
SNAPSHOT
   ↓
MODEL / PLAN
   ↓
PROJECT TO SHADOW TARGET
   ↓
VERIFY
   ↓
REMEDIATE / RE-RUN
   ↓
APPROVE FROZEN PLAN
   ↓
PRODUCTION APPLY OR EXTERNAL EXECUTION
   ↓
POST-VERIFY
   ↓
COMMIT OR ROLLBACK
```

PS-0 implements through approval/readiness and simulates the decision boundary. Production mutation is explicitly deferred.

## Core boundaries

### Core knows about

- systems;
- storage endpoints;
- artifacts;
- semantic types;
- snapshots;
- migration plans;
- graph nodes and edges;
- transformations;
- rules;
- evidence;
- lineage;
- artifact disposition;
- recovery semantics;
- runs and runtime fingerprints.

### Core must not know about

- Pension.Member;
- FHIR.Patient;
- utility meters;
- court charges;
- SQL Server connection semantics;
- PostgreSQL table semantics;
- S3 object semantics;
- any concrete vendor product.

These arrive through domain packs and connectors.

## Dependency rule

Domain packs and connector implementations depend inward on abstractions/core. Core never depends outward on a concrete pack or connector.

Correct:

```text
ProofShift.Packs.Pension
        ↓
ProofShift.Packs.Abstractions
        ↓
ProofShift.Domain
```

Correct:

```text
ProofShift.Connectors.SqlServer
        ↓
ProofShift.Connectors.Abstractions
        ↓
ProofShift.Domain
```

Incorrect:

```text
ProofShift.Engine
        ↓
ProofShift.Packs.Pension
```

Incorrect:

```text
ProofShift.Engine
        ↓
ProofShift.Connectors.SqlServer
```

The PS-0.3 graph compiler consumes the immutable generic YAML document tree produced by `ProofShift.Configuration`, maps it through graph-specific DTOs, and then creates domain graph objects. `ProofShift.Graph` does not parse YAML itself and does not reference concrete connectors or domain packs.

PS-0.4 inspection orchestration lives in `ProofShift.Engine` and resolves only `ISourceConnector` abstractions. Concrete PostgreSQL, SQL Server, filesystem, and CSV registrations occur in the CLI composition root. Runtime configuration values are wrapped and redacted; inspection does not create snapshots, evidence, or target writes.

Relational selectors are validated identifier sets, never arbitrary SQL. Reads are ordered by configured or discovered identity fields and stream records. Filesystem/CSV paths are endpoint-relative; file content hashes and large relational binary hashes are streamed. CSV identity collision checks use a temporary disk-backed fingerprint index.

Relational read streams are physically fetched according to the provider's `DbDataReader` behavior; ProofShift does not promise a configurable fetch size. `ReadOptions` carries only supported partition selection. Relational partitioning is currently rejected explicitly, and connector streaming remains one record at a time with cancellation and deterministic identity ordering.

## Migration Graph

The Migration Graph describes how information moves and changes.

### Node categories

- Source
- Target
- Archive
- Derived
- Aggregate

Every node references a logical system, one of that system's storage endpoints, a symbolic semantic type, and a connector-neutral artifact selector. Selectors carry a kind, string properties, and ordered identity fields; connectors interpret their meaning.

For PS-0.4, relational connectors interpret `kind: table`, filesystem connectors interpret `kind: file-pattern`, and CSV connectors interpret `kind: csv`. Physical interpretation, identifier quoting, row value conversion, and physical inspection remain inside each concrete connector.

### Edge categories

- Map
- Transform
- Copy
- Split
- Merge
- Aggregate
- Derive
- Archive
- Exclude
- Relationship

Every graph edge is versioned, carries explicit recovery metadata, and has one or more source and target nodes. Exclusions are the explicit zero-target exception. Split, merge, and many-to-many operations are represented directly rather than expanded into unrelated edges.

PS-0.3 graph canonicalization uses `proofshift-graph-canonical-v1`. Relationship-only cycles are reported as warnings; cycles involving execution or destructive operations are errors until a later scheduler can model more nuanced dependency semantics.

Configuration node/edge keys remain external string identifiers. The compiler preserves these names and derives deterministic scoped internal GUIDs; it does not convert the configuration project key to `ProjectId`.

The graph should eventually support impact analysis. If a transformation changes, ProofShift should be able to determine which downstream artifacts and verification rules may be affected.

## Evidence Graph

### Accepted PS-0.10B Authoring Boundary

Existing source connectors optionally expose read-only physical discovery, with connector-neutral structural artifacts and timestamp-independent fingerprints. Provider-owned rule descriptors drive typed rule validation, generated schemas, rule metadata and effective explanations. Pack versions are explicitly selected. Scaffold/CSV import produces ordinary referenced configuration and explicit review state; validation, graph compilation, named-map resolution, Recovery and execution share the existing contracts. Physical names/types/key roles are not domain-semantic inference. The full Pension project's primary semantics are committed under `scenarios/pension-modernization/ps010b`; fixture provisioning remains synthetic code. See ADR-0018 and the authoring walkthrough for compatibility and limitations.

Evidence is graph-addressable rather than a flat log of pass/fail strings.

Material conclusions should be traceable to inputs such as:

```text
source observation
        ↓
transformation evidence
        ↓
target observation
        ↓
comparison evidence
        ↓
aggregate / domain conclusion
        ↓
readiness decision
```

Each evidence record includes rule/version, expected/actual state, inputs, timestamp, run, and explanation.

Evidence may reference artifacts or other evidence records.

### PS-0.10A persistence and scale boundary

The private Verification workspace is not the long-lived lineage/disposition API. A separate per-run SQLite ledger stores opaque artifact IDs and graph-node scopes, supports indexed queries and deterministic streams, and is reopened by receipt during Recovery. Successful `VerificationResult` values carry summary data and the ledger receipt rather than full lineage/disposition/journal collections. Recovery stores edge counts and aggregate coverage; individual target coverage is queried from the ledger on demand.

The filesystem Evidence store uses `manifest.json` plus `evidence.ndjson` (store format v2). It streams one serialized record at a time, hashes the segment incrementally, writes a completion manifest last, and verifies both manifest and segment before exposing records. Clean source accounting is a population PASS with migrated/excluded counts; detailed failures remain artifact-specific. Evidence canonicalization v2 identifies this evidence policy. The current Verification orchestrator still constructs its bounded-by-findings `EvidenceGraph` before store persistence; the scalable policy avoids one success object per artifact, while high-failure runs and rule-specific findings may still grow with failure count.

## Recovery Model

A migration cannot be considered ready solely because verification passes. The migration must also have an acceptable recovery posture.

Recovery modes:

### Reverse
The original representation can be reconstructed from target state/transform metadata.

### Restore
Original state can be restored from a preserved snapshot or artifact.

### Compensate
Exact reversal is not possible, but a defined compensating action restores an acceptable state.

### Irreversible
Information is lost and cannot be reconstructed. It is denied by default in PS-0.8; explicit policy allowance, justification, and artifact limits are required. Qualification is still not business/regulatory approval.

A destructive operation without recovery metadata is invalid configuration.

PS-0.8 implements the Recovery Assessment Engine in `ProofShift.Recovery`, downstream of Domain, Graph, Configuration, Connector Abstractions, Engine, Snapshots, Projection, Evidence, and Verification. Engine and Verification do not depend on Recovery. Concrete recovery adapters remain at the CLI composition boundary.

Recovery resolves each terminal journal edge and graph-scoped target lineage to its configured mode, inverse-loss analysis, required capability, affected artifacts, and evidence. `Reverse` uses conservative transformation analysis; `Restore` requires a validated target recovery checkpoint; `Compensate` resolves an executable strategy ID from the compensator registry; `Irreversible` is denied by default and requires explicit policy allowance and justification.

`SourceCheckpoint` continues to prove the exact source input. It is not a target rollback point. Optional `IShadowTargetRecoveryConnector` implementations capture only isolated target namespaces: PostgreSQL copies a per-run schema to a recovery schema, and filesystem copies a per-run tree to a contained recovery directory. These are local rehearsal fixtures, not claims about provider-native production backup behavior. A controlled mutation is applied, the checkpoint is validated/restored, and physical shadow state is re-observed before qualification.

Recovery findings form a separate immutable Recovery Evidence Graph referencing the finalized Verification Run and Verification Evidence fingerprint. Recovery assessment/plan/rehearsal/qualification artifacts have separate integrity hashes. Fingerprints are versioned: `proofshift-recovery-policy-v1`, `proofshift-recovery-assessment-v1`, `proofshift-recovery-plan-v1`, `proofshift-recovery-rehearsal-v1`, `proofshift-recovery-evidence-v1`, and `proofshift-dry-run-fingerprint-v1`. `proofshift dry-run` qualifies only when checkpoint, projection, verification, accounting, lineage, recovery policy, required rehearsal, and final target re-observation are all bound and pass. A qualified dry run is a technical criterion, not human approval or production-safety advice.

## Source artifact accounting

Every source artifact must receive exactly one explainable disposition:

- Migrated
- Transformed
- Derived
- Archived
- Excluded
- Superseded
- Failed
- Unaccounted

`Unaccounted` is always a failure.

A source may produce multiple targets, but its disposition remains explicit.

## Target lineage

Every target artifact must have provenance.

ProofShift should answer:

- Which sources contributed?
- Which migration edges were traversed?
- Which transformation versions were applied?
- Was information merged/split/derived?
- Was anything discarded?
- Which rules verified the result?

A target artifact without lineage is always an assurance failure.

## History as first-class data

History cannot be treated as optional metadata. ProofShift must model:

- state history;
- transaction history;
- effective-dated rows;
- event history;
- audit metadata;
- version history;
- historical documents;
- archived representations.

ProofShift verifies semantic equivalence, not row-shape identity.

Example:

```text
Legacy status periods:
ACTIVE      2001-2018
INACTIVE    2018-2019
ACTIVE      2019-present

Target events:
JOINED       2001
TERMINATED   2018
REINSTATED   2019
```

These can be semantically equivalent even though the physical models are different.

## Snapshots

ProofShift does not always need to physically copy every source byte. A snapshot is a reproducible logical checkpoint.

Connector-dependent snapshot metadata may include:

- SQL transaction/LSN/SCN references;
- schema fingerprints;
- table/entity counts;
- file/object manifests and SHA-256 hashes;
- object versions;
- search snapshots;
- event-stream offsets;
- API/export checkpoints.

The snapshot manifest and hash must be preserved with the run.

### PS-0.6 materialized checkpoints

PS-0.6 implements a replayable logical checkpoint for the exact source nodes in the compiled graph. `ProofShift.Snapshots` owns capture orchestration, local persistence, typed codecs, fingerprints, and checkpoint-backed source streams; Domain and Graph contain only connector/storage-neutral checkpoint metadata. `ICheckpointSourceConnector` is an optional capability and does not replace the ordinary streaming read contract.

Structured records are streamed to versioned NDJSON segments. Binary values are copied to SHA-256-addressed blobs with bounded buffers and length/hash verification. The manifest records configuration/graph hashes, source-node selector hashes, endpoint consistency, capture start/completion, artifact/byte counts, endpoint/source fingerprints, segment hashes, aggregate capture interval, and `CrossSystemAtomic`. A checkpoint becomes `Complete` only after every required segment and the canonical manifest are finalized. Failed/cancelled data may remain for diagnosis but is never replayable.

PostgreSQL checkpoint reads use repeatable-read transactions. SQL Server defaults to `Observed`; transaction-consistent capture requires explicit endpoint policy and an explicit `snapshot` or `serializable` isolation choice. `read-committed` is reported as `Observed` and may only be used as an explicit downgrade. ProofShift never enables SQL Server database snapshot settings automatically and never silently escalates locking. Requested/effective strategies, guarantees, and downgrades are integrity-bound in the v2 checkpoint manifest. These are per-endpoint provider guarantees, not guarantees about inspection performed before the transaction. Filesystem and CSV are `Observed`: the filesystem compares a matched-file inventory around the read, and CSV compares file state/hash around logical-row parsing. For mixed endpoints, cross-system atomicity is false and the aggregate time window is reported.

Relational `ReadOptions` does not expose a fetch-size promise. Physical row fetching follows provider streaming behavior; ProofShift consumes `DbDataReader` rows incrementally, maintains deterministic identity ordering, and propagates cancellation. Partition selection remains explicitly unsupported until a provider-backed partition model is implemented.

Checkpoint-backed Projection validates the current configuration hash, compiled graph hash, exact source-node set, endpoint identity, and selector hash before preparing destinations. It verifies manifest, segments, source fingerprints, and binary blobs before replay. The provider has no live-connector fallback, and original artifact identity/provenance is retained with additive checkpoint provenance. `proofshift project --checkpoint` therefore cannot mix checkpoint and live source reads.

## Shadow projection

A projection creates realistic isolated output rather than merely calculating an abstract result. Projection is operational output, not a verification conclusion: it answers what the compiled graph produced, not whether that output is correct.

Possible shadow targets:

- temporary schemas;
- temporary databases;
- isolated object-store prefixes;
- local filesystem/object-store emulation;
- generated API request manifests instead of live mutation.

PS-0.5 implements PostgreSQL schemas named `proofshift_shadow_<run-guid>` and filesystem roots `<configured-root>/<run-guid>`. Before any target preparation, graph destinations must resolve to a system explicitly configured as `shadow-target`; `source`, `target`, `archive`, missing, and unsupported roles fail closed. PostgreSQL clones configured template table constraints/indexes into the isolated schema without copying defaults/sequences. Filesystem files are streamed into a per-run directory after path containment and identity checks.

The Projection Journal is append-only JSONL under `.proofshift/projections/<run-guid>/journal.jsonl`. A pending ancestry entry is flushed before target writes, followed by produced/failed status; exclusions and metadata-only relationships are also explicit. Entries record source/target artifact references, target graph node, edge ID/name/version, transformation types/versions and field mappings, and recovery metadata. It records execution ancestry only; it is not an Evidence Graph and makes no correctness claim. Merge and chained target-input execution are deferred until grouping/join semantics are specified.

The `proofshift-projection-fingerprint-v1` digest is computed from graph hash and canonical values read back from materialized shadow targets in deterministic node/identity/field order. It excludes run IDs, timestamps, schema names, and machine paths. Failed/cancelled output and its journal are retained for inspection; no automatic cleanup or rollback is performed. Without `--checkpoint`, PS-0.5 behavior continues to read live source observations. With `--checkpoint`, Projection records the checkpoint ID, source fingerprint, and manifest hash on the run; neither path claims correctness or production safety.

## Verification levels

### Attribute
Exact, normalized string, date, numeric tolerance, code-map, currency.

### Entity
Existence, uniqueness, required values, cardinality.

### Relationship
Correct ownership/association/reference relationships.

### Timeline
Continuity, effective dating, semantic state history, overlap/gap checks.

### Aggregate
Counts, financial totals, service credits, payment totals, balances.

### Accounting
Every source has disposition; every target has lineage.

### Recovery
Recovery coverage and destructive-operation readiness.

PS-0.9 adds `VerificationArtifactRecord` as a generic typed source/expected-target/actual-target observation returned through `IVerificationWorkspace.ReadArtifactRecordsAsync`. The temporary SQLite workspace stores a private typed representation for the duration of verification. PS-0.10A materializes normalized typed ordering keys into a generic indexed table and reads multi-field ordered streams without correlated JSON extraction. Domain rules merge ordered groups and release each group before advancing, avoiding whole-transaction materialization in memory. The interface and workspace contain no pension schema; rule metadata declares pack-neutral grouping/ordering requirements. Evidence still stores safe values/fingerprints and graph-scoped references.

The CLI `report` consumes the persisted Verification Evidence stream and integrity-checked Recovery summary. It requires exact Verification run/evidence/configuration/graph binding and derives exception counts incrementally from evidence records. `compare` compares persisted report projections and uses fingerprints for supported difference attribution; it does not recalculate verification.

## Observability

ProofShift has two distinct observability layers.

### Operational observability

OpenTelemetry-compatible telemetry for:

- connector latency;
- records/second;
- partition duration;
- memory;
- errors/retries;
- worker health;
- DB calls.

### Verification observability

Customer/auditor-facing information for:

- source observations;
- target observations;
- transformation steps;
- lineage;
- rules;
- exceptions;
- dispositions;
- recovery classifications;
- evidence-backed conclusions.

Operational telemetry is never a substitute for verification evidence.

## Scalability direction

Do not load whole datasets into memory. The eventual execution model must support partitioned reads and workers.

PS-0 may use a single-process coordinator, but interfaces should not make distributed execution impossible.

A future implementation may use PostgreSQL-backed work coordination before introducing a dedicated queue. Kafka/Redis are not PS-0 requirements.
