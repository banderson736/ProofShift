# ADR-0011: Streamable normalized records for domain verification

- Status: Accepted for PS-0.9
- Date: 2026-10-04

## Context

The PS-0.7 Verification workspace retained source/expected/actual record fingerprints and per-field hashes, then discarded normalized values. That is sufficient for generic equality checks, but not for pack-owned rules that group typed amounts, reconstruct semantic timelines, or verify relationships. PS-0.9 requires those checks to remain in `ProofShift.Packs.Pension` and to work for externally populated targets.

## Decision

- Extend the generic `IVerificationWorkspace` with a filtered asynchronous stream of typed source, graph-derived expected-target, and independently observed actual-target records.
- Keep the working set disk-backed in the existing temporary SQLite workspace. Domain rules consume one normalized record at a time; they must not retain full production datasets.
- Preserve typed values, relationships, and temporal metadata in a per-run scratch representation. Do not place raw values into findings, Evidence Graphs, reports, logs, or fingerprints; evidence continues to use safe keys, counts, and fingerprints.
- The Pension Pack consumes this generic contract through the existing rule-provider registry. Generic Verification has no pension rule references.

## Alternatives considered

### Re-read connector data from the Pension Pack

Rejected because packs must remain connector-neutral, the source must be the exact checkpoint, and target observations must be bound to the current verification run.

### Keep only fingerprints and hashes

Rejected because aggregate, timeline, and relationship semantics cannot be computed from hashes.

### Materialize all records in memory

Rejected because the configured scale includes millions of transactions and hundreds of thousands of documents.

## Consequences

The temporary verification workspace now contains typed record data until verification completes and its workspace is disposed. Callers remain responsible for safe local scratch storage. Pack rules gain enough data to perform deterministic semantic checks without changing connector ownership or the evidence format.