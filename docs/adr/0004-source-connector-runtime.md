# ADR 0004: Source Connector Runtime and Artifact Identity

- Status: Accepted
- Date: 2026-10-03

## Context

PS-0.4 must inspect and stream source artifacts through PostgreSQL, SQL Server, filesystem, and CSV implementations while keeping the domain/graph connector-neutral. PS-0.2 stores environment and secret references in hashed domain endpoint settings; resolved runtime material must only cross the connector boundary. Relational selectors are symbolic and must not become arbitrary SQL.

## Decision

1. `ProofShift.Connectors.Abstractions` owns the read-only `ISourceConnector`, runtime context/settings wrappers, registry contract, inspection metadata, read options, and stable `PSCONN` issues.
2. `ProofShift.Engine` owns source-node orchestration and runtime setting resolution. It depends only on configuration/domain/connector abstractions. The CLI composition root explicitly registers PostgreSQL, SQL Server, files, and CSV implementations; Graph and Domain do not reference concrete connector projects.
3. Runtime setting values are exposed through callback-only `RuntimeSetting.UseValue` and stringify as `[REDACTED]`. Resolved values are not copied into domain records, provenance, diagnostics, inspection reports, or hashes. Connector failures are sanitized at the inspection boundary.
4. Relational selectors accept `kind: table` and schema/table or `name`; identifiers are validated and quoted by each provider. The connector constructs fixed metadata/read queries and orders by identity columns. Explicit identity fields take precedence; otherwise a discovered primary key is used. Missing or nullable identity is an error.
5. Relational and CSV identities use ordered, length-prefixed field names and invariant-formatted values. CSV identity also includes the normalized project-relative file path. Artifact IDs are lowercase SHA-256 over system key, endpoint key, type, and physical identity; physical identity remains readable in `ArtifactReference.Identity`.
6. Inspection remains metadata/header-only and does not scan a complete relational table or CSV. Relational reads order by identity and detect adjacent duplicate IDs while streaming; nullable identity columns are rejected from schema metadata. CSV rows stream through CsvHelper with textual values and explicit headers/delimiters; duplicate identity detection uses a temporary SQLite index containing identity fingerprints during reads. Files use normalized endpoint-relative paths and stream SHA-256 hashing. No source read claims snapshot semantics.
7. PostgreSQL and SQL Server integration tests use Testcontainers. Filesystem and CSV tests use temporary synthetic trees/files. Container tests may require a working Docker-compatible runtime.
8. Large relational binary values are read sequentially and represented by a length/SHA-256 `BinaryReferenceValue` that can be reopened through the connector. UUID values use invariant string representations; timestamptz/UTC values, explicit offsets, and local timestamps remain distinct; decimal values remain decimal and are never converted through floating point. Unsupported physical types fail with a stable connector issue.

## Alternatives considered

### Concrete connector switch in Engine

Rejected because it reverses the dependency boundary and makes connector packages a core runtime dependency.

### Pass raw endpoint dictionaries to connectors

Rejected because resolved secrets could be logged or serialized accidentally, and setting identity/secret material would be indistinguishable.

### Accept arbitrary SQL selectors

Rejected because it increases injection risk and hides selection semantics. Initial relational access is built from validated identifiers.

### Use row numbers for absent identity

Rejected because identity would change with ordering and could not support complete artifact accounting.

### Load all artifact identities in memory to detect duplicates

Rejected. Relational rows are ordered by the identity and checked against the preceding identity as they stream. CSV uses a temporary SQLite index containing SHA-256 identity fingerprints so managed memory does not grow with row count. The index is transient and removed on normal disposal.

### Treat reads/inspection as snapshots

Rejected because current reads do not define a consistent immutable checkpoint.

## Consequences

- Four read-only connector implementations and an explicit CLI registry are introduced.
- Provider package versions are centrally managed.
- SQL identifiers remain provider-owned; no SQL syntax enters the graph/domain model.
- CSV identity collision detection is disk-backed; very-large-file throughput still requires measurement before scale claims.
- File and database observation remains distinct from snapshots, projection, migration execution, and verification evidence.
