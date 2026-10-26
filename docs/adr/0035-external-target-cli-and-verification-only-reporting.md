# ADR-0035: External Target CLI and Verification-Only Reporting

- Status: Proposed under PS-0.11F
- Date: 2026-10-26
- Decision owners: ProofShift repository owner

## Context

ProofShift supports independent target observation in its Verification engine,
but the ordinary CLI and persisted reporting workflow are centered on shadow
Projection and Recovery qualification. Evaluators need a supported way to
verify targets populated by external migration tooling without implying
ProofShift performed that migration or assessed Recovery.

## Decision proposed

1. Expose external target Verification as a normal CLI command that binds the
   configured graph and complete source checkpoint to read-only target
   observation, using the existing Verification service and connector observer
   catalog.
2. Keep Projection absent from this workflow. Persist ordinary Evidence and
   the Verification ledger as the authoritative record.
3. Add explicitly selected Verification-only persisted reports and comparisons
   that integrity-check persisted Evidence, ledger, checkpoint, and project
   context and never rerun Verification.
4. State explicitly that Recovery is not assessed, qualification is
   unavailable, and external/vendor execution lineage is not claimed.
5. Keep this additive to existing shadow dry-run reporting and comparison
   selectors.

## Consequences

- External migration assurance remains usable independently of ProofShift
  migration execution.
- Read-only target observation can be used without a Projection manifest or
  journal.
- Persisted Verification results have a distinct reporting boundary from
  Recovery-qualified dry runs.
- The generic Verification engine remains domain-neutral; the BYOM sample does
  not become a domain pack or DemoRunner scenario.
- Verification proves only the configured graph, mappings, selectors, and
  rules; it does not prove complete domain correctness or production readiness.

## Alternatives considered

- Reuse the dry-run report model by synthesizing Recovery or qualification:
  rejected because it would misrepresent external Verification.
- Build a second external verification engine in the CLI: rejected because it
  would duplicate semantic and integrity behavior.
- Treat an observation label as a Projection run ID: rejected because
  observation identity and Projection identity are distinct.
- Add customer-shaped semantics to a new generic pack: rejected because the
  sample tests generic configuration and Verification rather than adding a new
  domain pack.

Formal acceptance of this ADR is separate from the PS-0.11F implementation.
