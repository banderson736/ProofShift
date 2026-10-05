# ADR-0015: SQLite profile for disposable verification workspace

- Status: Accepted for PS-0.10A
- Date: 2026-10-04

## Context

The Verification workspace is a private, per-run SQLite scratch database. It stores intermediate source, expected-target, observed-target, and journal facts used to construct Verification and Evidence results. The finalized Evidence Graph is persisted separately.

The prior `journal_mode=DELETE` and `synchronous=FULL` profile forced full synchronization for bounded commits to this disposable workspace. The PS-0.10A fast profile measured approximately 15.2 seconds across 33 source/expected-target ingest stages and 20.8 seconds across three journal-validation stages. The workspace commits every 128 write operations, flushes before reads, rolls back unfinished transactions on disposal, and deletes its private directory after closing the connection.

## Decision

Use `journal_mode=WAL` and `synchronous=NORMAL` for the disposable verification workspace. Keep the existing bounded transaction size, schema, indexes, and temporary-file behavior unchanged. Do not apply this profile to durable Evidence persistence or customer databases.

## Alternatives considered

### Keep DELETE/FULL

Rejected because full synchronization is unnecessary for intermediate state that is deleted after the run and whose durable assurance output is stored separately.

### Use MEMORY journal mode or disable synchronization

Rejected because loss of the in-memory rollback journal or disabled synchronization weakens recovery more than needed for this scratch-workspace optimization.

### WAL/FULL

Rejected because it retains the full synchronization cost that the disposable workspace is intended to avoid.

## Failure and durability semantics

WAL/NORMAL retains SQLite transaction and WAL recovery behavior, but a power loss may lose the most recent committed scratch transactions. Verification must not treat an incomplete or failed workspace as a successful result. The workspace is private to one run, unfinished transactions are rolled back, and the scratch directory is deleted on disposal. A process or machine failure requires rerunning Verification; it does not invalidate or partially overwrite a previously persisted Evidence Graph.

## Consequences

The workspace avoids full synchronization at each bounded commit while retaining database-level transactional behavior. The profile is intentionally limited to ephemeral working state. The fast assurance benchmark and workspace invariant tests remain the validation gates; durable evidence integrity and fingerprints are unchanged.
