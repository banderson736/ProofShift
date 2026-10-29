# PS-1.0A Customer Data Evaluation & Generalized Mapping Foundation

## Status

**In Progress — not accepted.** Authorized branch:
`ps-1.0a-customer-data-evaluation`. Starting point: accepted PS-0.11G commit
`a095c21588cbd1608b8a543cdfaab71312b596d8`.

The acceptance matrix is authoritative for gate status. This scope document
does not imply that the slice is complete or that a customer evaluation has
been approved.

## Objective

Provide a deterministic, customer-controlled evaluation path for reviewed
mappings and reconciliation rules over an explicitly declared evaluation
scope. External target Verification remains read-only; optional shadow
Projection writes only to an isolated destination. Generalize the existing
authoring and verification foundation without weakening the accepted PS-0.11
behavior or claim limits.

The product may claim complete accounting or reconciliation only for the
source selectors and evaluation scopes recorded in the run evidence. No
result proves coverage outside those selectors/scopes.

## Required outcomes

- Define stable scope IDs/types and, where supported, typed source predicates.
- Bind scope semantics to graph/configuration fingerprints, selector hashes,
  complete source checkpoints, replay validation, and external Verification
  reports.
- Reject unsupported scope predicates rather than silently reading a broader
  source.
- Generalize reviewed mapping authoring with explicit identity/attribute
  roles, validated deterministic pipelines, and unambiguous supported
  topology.
- Provide explicit expected-exception semantics with auditable boundaries.
- Provide connector- and domain-neutral reconciliation rules where their
  inputs are available.
- Exercise the workflow with deterministic synthetic customer-shaped data in
  the packaged evaluation path.
- Document customer preparation, scoped evaluation, mappings, and
  reconciliation, and preserve the PS-0.11 acceptance outcomes.

## Invariants and non-goals

- External Verification remains read-only. It does not execute or qualify an
  external migration and never implies Recovery readiness.
- Customer source data and secrets must remain in the customer-controlled
  environment; committed and packaged scenarios use synthetic data only.
- Scope values must not be exposed in ordinary reports. Fingerprints and
  explicit scope metadata are sufficient for traceability.
- No implicit join/merge semantics are introduced. A multi-source graph is
  not permission to invent row grouping, cardinality, scheduler, or recovery
  behavior.
- No UI, API, SaaS, AI/fuzzy matching, arbitrary SQL/scripts, new packs or
  connectors, distributed execution, persistent server storage, production
  migration execution, rollback, publishing, tagging, or signing is included.
- Product identity remains `ProofShift 0.1.0-preview.1`.

## Current implementation notes

The first scope foundation, relational predicate path, scope-bound
checkpoint/report metadata, optional authoring scope flags, identity/pipeline
mapping import, exact generic aggregate-total comparison, and
non-waiving expected-exception annotations are implemented with focused local
tests. Merge execution is explicitly deferred pending separately specified
join semantics. The packaged BYOM fixture now includes a separate synthetic
customer-data shadow workflow with generic attribute and aggregate
reconciliation, a qualified run, and a defective run whose exact exception is
annotation-only. The workflow is wired into the package smoke runner, but it
has not yet been executed in this environment. Complete acceptance evidence
and Docker/package/remote CI validation remain open. See the
[acceptance matrix](PS1_0A_ACCEPTANCE_MATRIX.md).
