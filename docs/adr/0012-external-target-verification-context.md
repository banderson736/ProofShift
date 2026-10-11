# ADR-0012: External target verification context

- Status: Accepted for PS-0.9
- Date: 2026-10-04

## Context

`VerificationService.VerifyAsync` currently requires a successful Projection manifest and journal. That correctly binds projected dry runs, but prevents a customer/vendor-populated target from being verified independently. Reusing a fabricated Projection run or Projection Journal would falsely claim ProofShift executed the migration and would contaminate source dispositions and lineage.

## Decision

- Add `ExternalMigrationObservation` as a separate generic verification input. It binds an observation identifier, target namespace/run identity, configuration and graph hashes, source checkpoint ID/manifest/source fingerprint, observed target nodes/endpoints/connectors, and observation time.
- Add a separate `VerifyExternalTargetAsync` path on the existing `VerificationService`. It replays expected semantics only from the exact source checkpoint and graph, independently reads actual target state, and never opens a Projection manifest or journal.
- Generic mapped-attribute comparison joins checkpoint/graph-derived expected values directly to independently observed actual values. It does not require Projection Journal rows; the expected mapping edge/source references come from the graph-derived expected workset. Projected verification still has its separate journal validation and lineage checks.
- Add a generic ordered workspace query for expected graph-derived source facts, target matches, and lineage. External source disposition and target lineage may be reported as graph-derived expectations, never as observed vendor execution records. A `LineageBasis` marker preserves that distinction.
- External results use a distinct run record with no Projection run ID or Projection manifest hash. They produce the same immutable Evidence Graph format and can be persisted through the existing evidence store.
- External verification does not create Recovery qualification. Recovery remains authoritative for the projected defective/corrected dry-run scenario and requires actual execution/recovery context.

## Alternatives considered

### Fabricate a successful Projection manifest and journal

Rejected because this invents ProofShift execution evidence and makes journal-derived dispositions/lineage dishonest.

### Re-read source/target connectors inside the Pension Pack

Rejected because it would violate pack/connector boundaries, fail checkpoint binding, and introduce a second verification path per domain.

### Add a second pension-specific verification service

Rejected because expected-state derivation, target observation, evidence, and accounting are generic engine responsibilities.

## Consequences

Verification exposes separate projected and external-observation APIs. Both share checkpoint replay, generic record workspace, configured rules, Evidence Graph, and integrity store, but only the projected path validates Projection journal evidence. External lineage explicitly means an expected graph relationship; it does not prove how or when an external vendor executed a transformation. The external run binds to a read-only target namespace/run identity supplied by the adapter and is not eligible for Recovery qualification in this slice. Generic mapped-field comparisons now work on external observations without manufacturing or consulting a Projection Journal.
