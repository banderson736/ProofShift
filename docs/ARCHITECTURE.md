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

## Migration Graph

The Migration Graph describes how information moves and changes.

### Node categories

- Source
- Target
- Archive
- Derived
- Aggregate

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

Every graph edge is versioned and carries recovery metadata.

The graph should eventually support impact analysis. If a transformation changes, ProofShift should be able to determine which downstream artifacts and verification rules may be affected.

## Evidence Graph

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
Information is lost and cannot be reconstructed. Requires explicit policy/approval and is a readiness failure in PS-0.

A destructive operation without recovery metadata is invalid configuration.

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

## Shadow projection

A dry run must create realistic projected output rather than merely calculate an abstract result.

Possible shadow targets:

- temporary schemas;
- temporary databases;
- isolated object-store prefixes;
- local filesystem/object-store emulation;
- generated API request manifests instead of live mutation.

The same frozen plan and transformation versions used to build an approved shadow result should be reusable for production orchestration or external verification.

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
