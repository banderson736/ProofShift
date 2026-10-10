# ADR 0020: Descriptor-Driven Verification Execution Plans

- Status: Accepted for the PS-0.10D plan-driven workset slice; milestone remains in progress
- Date: 2026-10-07

## Context

Verification rules already expose ordered key requirements, while provider-owned rule descriptors validate configured rule types and options. The engine needs a generic workset before workspace population to make rule requirements and future execution properties explicit. Rule knowledge must remain outside the Verification core's domain-specific conditionals, and execution choices must not alter semantic fingerprints.

## Decision

Resolve each configured rule to its provider-owned descriptor and derive a deterministic `VerificationExecutionPlan` before workspace creation for both projection-backed and external-target Verification. Descriptors bind configured/default field options to a source or target side, semantic type, and optional key role. The plan carries rule identity/scope, required semantic types and fields, grouping/ordering/lookup keys, partition safety, and global-finalization requirements. Runtime `RequiredOrderingKeys` must be covered by descriptor bindings and are mapped to those side-specific worksets. Selector identity fields are added from the graph by node role. Workset order and field/key/index unions are deduplicated deterministically.

Execution capabilities default conservatively: a rule is not partition-safe unless explicitly declared, and requires global finalization unless explicitly declared otherwise. The plan is operational metadata and is excluded from rule-set and other semantic fingerprints. Field-reference descriptor options without side-aware bindings fail planning. Rules fail with `PSRULE008` if they read an undeclared projected field; ordered and lookup workspace calls also fail if their typed key is absent from the plan.

The workspace persists only required values in source, expected-target, and actual-target records. Full record fingerprints are computed before projection, expected values are independently derived from checkpoint plus graph transformations, and artifact identity, endpoint scope, accounting, journal ancestry, and lineage metadata remain intact. Required typed keys feed the shared generic `artifact_order_key_idx` after bulk ingest; equivalent key requirements are deduplicated. Benchmark diagnostics contain field names/counts and byte totals, never values. The authoritative source remains the checkpoint; actual targets remain independently observed through the configured connector.

This slice does not fuse scans, claim one-time decoding across rule scans, add a per-semantic SQL schema, schedule concurrent rules, partition data, or alter evidence ordering. Rules remain non-partitionable and globally finalized by default.

## Consequences

- The same rule descriptor and runtime key contract is available to internal and external-target Verification.
- Workset requirements are testable independently and the workspace enforces them at write/read boundaries.
- The 50k and final identity-preserving 100k measurements show reduced serialized-record bytes and workspace/WAL scratch, but slower ProofShift runtime (+0.59% / +1.96%). At 100k, ingest/read-back improved only 5.83%, workspace scratch fell 24.73%, WAL fell 26.29%, and ledger size was unchanged. The advancement gate failed: no 200k or candidate Medium run is authorized by these results. Retention of the local projection is a scratch tradeoff pending review, not a throughput success or PS-0.10D acceptance.
- Existing rules decode only the projected record subset on reads; cross-rule decoded-value reuse remains a future optimization.
- Concurrency and partition execution require separate semantic-equivalence tests and explicit metadata; they are not implied by this plan.

## Continuation Decision

The explicitly assigned ledger-finalization experiment retains reduced-field projection as a scratch optimization, not a throughput optimization. The existing codec/plan boundary is clean and does not require parallel storage strategies; approximately 25% scratch savings justify keeping it unchanged while measuring the ledger independently. See ADR-0021 for staging, durable publication, and failure semantics. The rejected workset runtime is not the throughput baseline.

## Alternatives Considered

### Hard-code rule requirements in Verification

Rejected because it would couple generic core behavior to concrete rule providers and domain packs.

### Infer every requirement from runtime rule execution

Deferred because runtime ordering keys alone cannot describe required fields, semantic types, or execution capabilities before workspace population.

### Begin concurrent scheduling with the plan

Rejected for this slice because current descriptors do not establish partition safety or determinism properties for the complete rule set, and write amplification/materialization costs remain to be decomposed.