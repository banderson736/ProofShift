# ADR-0016: Explicit SQL Server checkpoint consistency

- Status: Accepted for PS-0.10A
- Date: 2026-10-04

## Context

The SQL Server relational connector previously selected Serializable for every checkpoint read. That can introduce range locks and operational impact against a source endpoint without the operator explicitly choosing it. The checkpoint contract must distinguish Observed from transaction-consistent reads and must not imply cross-system atomicity.

## Decision

- SQL Server checkpoint reads default to `observed` and open no checkpoint transaction unless an endpoint explicitly configures one.
- `checkpoint.consistency: transaction-consistent` requires an explicit `checkpoint.isolation` selection. `snapshot` and `serializable` satisfy the request when the provider starts the selected mode.
- `read-committed` is not classified transaction-consistent. It is rejected for that request unless `checkpoint.allowDowngrade: true`; with that explicit permission it is recorded as effective `read-committed`, guarantee `observed`, with a downgrade reason.
- Unsupported or unavailable requested modes fail capture. ProofShift does not modify database-wide options and does not silently escalate or weaken isolation.
- PostgreSQL retains its current repeatable-read checkpoint strategy. Filesystem and CSV remain Observed.
- Snapshot endpoint manifests record requested strategy, effective strategy, guarantee, and downgrade. Manifest canonicalization advances to v2 to bind these fields.

## Alternatives considered

### Keep Serializable as the SQL Server default

Rejected because it silently chooses an intrusive locking strategy for an unknown customer workload.

### Automatically enable database snapshot settings

Rejected because this changes customer database-wide operational configuration and is outside ProofShift's source-read boundary.

### Silently fall back when a requested isolation is unavailable

Rejected because it would misstate checkpoint guarantees. Any downgrade must be explicitly permitted and recorded.

## Consequences

Deterministic fixtures that require Serializable declare it explicitly. A checkpoint may contain a mixture of endpoint guarantees, but it never claims that separate systems were captured atomically. Checkpoint reports can explain the requested and effective endpoint strategy without exposing connection settings.
