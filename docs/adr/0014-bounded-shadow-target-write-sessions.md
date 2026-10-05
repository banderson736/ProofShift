# ADR-0014: Bounded shadow target write sessions

- Status: Accepted for PS-0.10A
- Date: 2026-10-04

## Context

The PS-0.9 integrated fast benchmark reports PostgreSQL shadow write time near 8 seconds for 5,000 contribution artifacts and roughly 3.3 seconds for 2,000 benefit-payment artifacts per projection. The current connector opens a connection and commits a transaction per artifact. Projection also flushes each journal line, but the corrected stage profile measures journal validation at about 7 seconds per run after replacing per-entry SQLite membership lookups; PostgreSQL writes are the next measured projection bottleneck.

Performance work must retain duplicate-target detection, deterministic materialized fingerprints, cancellation behavior, explicit partial output, and truthful pending/materialized/failed journal state. A batch must not be reported as materialized before its target transaction commits.

## Decision

- Add an optional `IShadowTargetWriteSessionProvider` capability to connector abstractions. Projection remains connector-neutral and uses it when present; other connectors retain the existing single-write fallback.
- A session is scoped to one isolated target node and has a bounded configurable batch size.
- Each `WriteBatchAsync` is atomic for connectors implementing the capability: one endpoint connection, one bounded database transaction, and one commit per batch. The whole migration is not one transaction.
- Projection writes and flushes `pending` journal entries before a batch begins. After the batch commits, it writes and flushes `produced` entries. A failed batch is rolled back and receives explicit failure journal state; cancellation before commit leaves pending entries and no successful result.
- Buffering is bounded by configured batch size. No full migration dataset or journal is accumulated in memory.

## Alternatives considered

### One transaction for the complete migration

Rejected because it increases lock/snapshot lifetime and couples failure recovery to an unbounded transaction.

### Keep one connection but commit each row

Rejected because measured transaction/round-trip overhead remains proportional to artifact count.

### Generic bulk loader in Projection

Rejected because provider-specific write mechanics must remain behind connector abstractions and the current value mapping is connector-neutral.

## Crash consistency

The journal is write-ahead at batch granularity. Pending entries are flushed before each target batch. A batch is marked produced only after the connector confirms commit. A crash before the completion record leaves the batch pending; a handled write failure or cancellation records the uncommitted batch as failed. Previously committed batches retain produced status. Projection never reports success after a partial batch failure.

## Consequences

Projection gains an optional capability while preserving `IShadowTargetConnector.WriteAsync` compatibility. PostgreSQL can reuse a connection and bounded transaction batches; filesystem remains on the existing streaming fallback unless it later proves a batch capability is useful. Journal semantic fingerprints must be verified unchanged across batch sizes.