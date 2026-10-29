# Supported Source and Connector Compatibility

This is the customer-facing inventory of connectors registered by the packaged
ProofShift CLI. It describes implemented capability, not a promise that every
provider or source configuration has identical semantics. Confirm the
capabilities exposed by the exact installed package with
`proofshift capabilities --json`; connector-owned settings and selector
requirements are available with `proofshift connectors schema <connector-id>`.

## Source connector compatibility

Every source connector listed below is registered for discovery, source reads,
complete checkpoint capture, replay of a complete captured checkpoint, and
read-only target observation for Verification. Replay uses the materialized
checkpoint; it does not reconnect to or fall back to the live source. Checkpoint
capture must complete before replay. Checkpoint consistency is described per
connector and is not cross-system atomicity.

| Source / format (connector ID) | Discovery | Checkpoint and replay | Verification observation | Shadow write / Projection | Limitations and prerequisites |
| --- | --- | --- | --- | --- | --- |
| Microsoft SQL Server (`sqlserver`) | Relational tables and metadata | Yes; observed by default. Transaction-consistent capture requires explicit `transaction-consistent` configuration and an isolation choice; `snapshot` requires the database's snapshot-isolation setting to be enabled, and `serializable` is opt-in. ProofShift does not change database settings. A `read-committed` downgrade must be explicitly permitted and is reported as observed. | Read-only through the source reader; use a target-role account with read privileges only. | Registered shadow writer: yes, when configured as a separate shadow target. | Reachable SQL Server endpoint, approved read permissions, and stable configured selectors. The managed provider is included in the package; a SQL command-line client is not required for normal CLI use. |
| PostgreSQL (`postgres`) | Relational tables and metadata | Yes; repeatable-read transaction consistency is implemented for source checkpoint capture. Replay is from the checkpoint. | Read-only through the source reader; use a target-role account with read privileges only. | Registered shadow writer: yes. Projection requires an isolated destination, template tables, and privileges to create and populate run-scoped schemas. | Reachable PostgreSQL endpoint and approved permissions. Use a separate database/schema for shadow work; never point a shadow writer at a customer source or production target. |
| Oracle (`oracle`) | Relational tables and metadata | Yes; observed consistency. | Read-only through the source reader; no Oracle shadow writer is registered. | No. | Reachable supported Oracle service and approved read permissions. Oracle's managed provider is included. Oracle Free used by integration tests is not a product prerequisite or a production-support claim. |
| IBM Db2 LUW (`db2`) | Relational tables and metadata | Yes; observed consistency. | Read-only through the source reader; no Db2 shadow writer is registered. | No. | Reachable Db2 LUW endpoint and approved read permissions. Supported connector scope is Db2 LUW; z/OS, IBM i, and Db2 Connect-dependent targets are excluded. The managed provider is included. |
| CSV (`csv`) | Delimited files, header and field inspection | Yes; observed consistency. | Read-only through the source reader. | No. | A readable local file path, valid header, and configured delimiter/selector. Keep the file stable during capture or capture an approved immutable copy first. |
| Filesystem (`files`) | Files and file metadata | Yes; observed consistency. | Read-only through the source reader. | Registered shadow writer: yes, when configured as a separate shadow target. Projection writes under a run-specific directory inside the configured shadow root. | Readable source paths; shadow Projection requires a dedicated writable destination root. Do not select the source tree or customer-owned files as the shadow root. |
| Fixed-width (`fixed-width`) | Fixed-width records and declared fields | Yes; observed consistency. | Read-only through the source reader. | No. | Readable input files and explicit field start/length definitions; the format is not inferred. |
| JSON (`json`), NDJSON (`ndjson`), XML (`xml`) | Structured documents/records | Yes; observed consistency. | Read-only through the source reader. | No. | Readable input files and selectors valid for the chosen format. Unsupported or malformed content fails closed; these IDs do not imply a generic REST/API connector. |
| S3 / S3-compatible object storage (`s3`) | Objects within the configured scope | Yes; observed consistency; bytes are materialized into the checkpoint for replay. | Read-only through the source reader. | No. | Reachable endpoint/bucket, read/list permissions, and customer-managed credentials. Provider version pinning varies. Integration validation used MinIO; it is not AWS certification. |
| Azure Blob Storage (`azure-blob`) | Blobs within the configured scope | Yes; observed consistency; bytes are materialized into the checkpoint for replay. | Read-only through the source reader. | No. | Reachable account/container, read/list permissions, and customer-managed credentials. Provider version pinning varies. Integration validation used Azurite; it is not Azure service certification. |
| SFTP (`sftp`) | Files within the configured remote scope | Yes; observed consistency; bytes are materialized into the checkpoint for replay. | Read-only through the source reader. | No. | Reachable SFTP server, read/list permissions, and verified host key. Integration validation used an OpenSSH-backed fixture; it is not production-server certification. |
| Apache Parquet (`parquet`) over filesystem, S3, Azure Blob, or SFTP | Parquet files and supported flat-schema columns | Yes; observed consistency; checkpoint replay is offline from the source. | Read-only through the source reader. | No. | The corresponding supported transport and readable files/objects are required. Nested/repeated schemas and legacy INT96/time-of-day columns are unsupported and fail closed. No general Parquet server is required. |

Relational source selectors support the typed evaluation predicates
`equals`, `not-equals`, `in`, `range`, `date-time-range`, `is-null`, and
`is-not-null`. CSV, filesystem, structured-file, object-storage, and Parquet
connectors do not support these relational predicates; unsupported predicates
are rejected rather than ignored. Use `full-declared-source` or
`customer-prepared-subset` scopes where appropriate, and keep completeness
claims bounded to the captured selectors and scope.
Predicate values are parsed against the discovered relational column type and
bound as provider parameters. `range` is inclusive and numeric-only;
`date-time-range` is inclusive and temporal-only, with explicit offsets
required for timestamp-with-time-zone columns. `is-null` and `is-not-null`
must be used for null checks; ordinary comparisons follow database null
semantics. Missing fields, malformed or incompatible values, and reversed
range bounds fail closed instead of broadening the read.

## Projection and external Verification boundary

The source table's shadow-write column means that the CLI has a separately
registered target writer for that connector ID. It does not mean that source
reads or external Verification can write. Current shadow writers are
PostgreSQL and filesystem only. PostgreSQL Projection writes run-isolated
schemas; filesystem Projection writes run-isolated directories. Select an
explicitly disposable destination and retain its exact run ID before cleanup.

External target Verification observes target-role nodes through the reader
capability and is read-only. It does not execute an external migration, create
Projection artifacts, or assess Recovery. The compatibility table does not
establish vendor certification, business correctness, regulatory approval, or
service-level guarantees.

## Runtime and external prerequisites

- The evaluation package is framework-dependent and requires the .NET 10
  runtime. The package supplies the connector provider libraries, not database
  servers, customer credentials, or remote services.
- Database and remote endpoints must be provisioned and reachable by the
  customer's environment; grant only the read permissions needed for discovery
  and checkpoint capture, and read-only target permissions for external
  Verification.
- Filesystem, CSV, structured-file, and Parquet inputs must be readable by the
  process. Files and object sets do not have a cross-file atomic checkpoint
  guarantee; use a stable source or immutable prepared copy when required.
- The packaged synthetic BYOM fixture additionally requires PowerShell 7,
  Docker, and network access to pull its SQL Server/PostgreSQL test images.
  Those are fixture prerequisites only, not prerequisites for customer
  connectors generally. The runner is shipped at
  `scripts/run-byom-evaluation.ps1`; no repository checkout is needed.
- Only PostgreSQL and filesystem provide shadow Projection destinations.
  Provision a dedicated isolated PostgreSQL database/schema or filesystem root
  and grant the shadow writer the necessary create/write permissions. Never
  configure a production source or external target as a shadow destination.

Integration-test fixtures and their limits do not expand the
customer-supported capability inventory above.
