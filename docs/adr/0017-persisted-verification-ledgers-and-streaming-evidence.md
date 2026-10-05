# ADR-0017: Persisted Verification ledgers and streaming Evidence

- Status: Accepted for PS-0.10A
- Date: 2026-10-04

## Context

The Verification path previously retained complete lineage, source disposition, and journal lists, built a second source-indexed lineage dictionary, and compared complete in-memory source/target arrays. Recovery repeated those collection dependencies, including per-target artifact coverage. Filesystem Evidence persistence serialized the complete graph to one byte array. These shapes made otherwise disk-backed rules require memory proportional to successful artifact count.

## Decision

- Verification creates a private per-run SQLite ledger under `.proofshift/verification-ledgers/<run-id>/ledger.sqlite`. It stores opaque artifact IDs and graph scope, not artifact identity strings or normalized source/target values.
- The ledger uses bounded write transactions, WAL/NORMAL, indexed disposition and lineage lookups, indexed journal-by-edge queries, terminal edge-scope indexes, deterministic ordered streams, a semantic receipt/fingerprint, side-store coverage joins, and clean deletion for incomplete runs. Complete ledgers are reopened by receipt for Recovery and on-demand artifact inspection.
- `VerificationResult` contains summary/result/evidence data and a ledger receipt; it no longer exposes full lineage, disposition, or journal collections. External-target Verification uses the same ledger and does not need a Projection Journal.
- Recovery reads dispositions, edge scope, and lineages from the ledger. Edge assessments and recovery-plan steps retain counts, not artifact arrays. Recovery artifact coverage is computed into counts and semantic-type summaries; `RecoveryService.ReadArtifactCoverageAsync` provides per-artifact detail on demand.
- The filesystem Evidence store uses format v2: `manifest.json` plus `evidence.ndjson`. The manifest binds run, canonicalization version, semantic fingerprint, record count, segment hash, and Complete state. Writes hash the segment incrementally and publish the manifest last; reads validate integrity before exposing a stream.
- Successful source accounting is represented as population evidence with exact migrated/excluded counts. Failure evidence remains artifact-specific. Artifact-level lineage and disposition remain in the private Verification ledger.
- Rule ordering values are normalized during workspace ingest into a generic indexed table. Keys retain value kind and typed payload; ordering is multi-field and deterministic. Relational `ReadOptions` no longer exposes an ignored `BatchSize` setting; providers control physical streaming behavior.

## Measured Hot-Path Refinement

The PS-0.10A investigation recovered the 117.108-second WAL baseline and compared it to the 229.128-second persisted-ledger/key-staging run. Nearly all of the runtime increase is inside Verification, principally workspace ingestion rather than Evidence persistence. Prepared workspace commands are reused across bounded transactions. The wide secondary ordering-key index is built at first lookup/ordered read after bulk ingest, while primary-key uniqueness remains enforced during writes. This reduced instrumented Fast to 178.301 seconds total with unchanged graph/rule/projection fingerprints and exact 149/0 assurance outcomes. The deferred index build is timed independently; indexed joins plus SQLite temporary B-tree ordering are not described as a fully index-covered multi-key sort.

Benchmark-only opt-in workload sampling runs inside the executing process at five-second intervals, records aggregate progress and distinct scratch categories, and emits no artifact values. Per-workspace diagnostics record counts, bounded transaction totals, database/WAL bytes, and actual query plans. Sampling is stopped before fixture cleanup and must not change rollback or finalized Evidence durability semantics. See `docs/COPILOT_PS0_10A_SCALE_HARDENING.md` for comparable stage timings and remaining gates.

The sampled Medium run exposed a pathological external-coverage plan: SQLite selected the journal-binding index by node prefix alone for a correlated target-identity predicate. This lookup now explicitly uses the existing node/identity index, retaining graph-scoped source binding. The preserved Medium database demonstrated a full candidate predicate count in 8.7 seconds versus over 33 minutes spent in the original stage before interruption. A plan/semantic regression prevents return to the prefix-only scan. This is a provider-local query-plan correction, not a new ledger abstraction or an integrity relaxation.

## Fingerprints and compatibility

Verification-ledger fingerprint v1 excludes database sequence and file paths and is invariant to append order. Existing Evidence canonicalization advanced to `proofshift-evidence-canonical-v2` to identify the aggregate success-evidence policy. Recovery assessment/plan fingerprints retain the prior ordered artifact-scope fields by streaming them from the ledger; Recovery plan serialization is separately versioned as v2 because its public scope arrays were replaced by counts.

## Consequences

Successful large runs no longer require in-memory source-disposition, lineage, journal, or Recovery artifact-coverage graphs. Memory may still grow with detailed failure findings, rule-specific group fan-out, migration graph size, and currently materialized Verification EvidenceGraph construction; medium-scale measurement is required before acceptance. Temporary SQLite ledgers are operational working state, not final customer Evidence.
