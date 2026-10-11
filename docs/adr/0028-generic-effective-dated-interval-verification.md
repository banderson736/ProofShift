# ADR-0028: Generic effective-dated interval verification

- Status: Accepted for PS-0.10F (October 11, 2026; merged as `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9`)
- Date: 2026-10-10

## Context

F4 must validate effective-dated employment history without embedding Worker, Employment, Position, Compensation, or other pack terminology into generic Verification. The existing Pension employment-timeline rule derives status-transition events from pension-specific state codes; it does not provide a reusable date-interval validity/overlap primitive. No generic interval rule existed.

## Decision

- Add generic Verification rule type `effective-dated-interval`, configured by target node, semantic type, interval identity, owner/grouping field, inclusive start/end date fields, and an optional continuity policy.
- Declare every read through descriptor field requirements and typed ordering keys. Read actual interval records as one ordered stream by owner then start date; use a global merge because overlapping intervals for one owner must remain colocated in semantic order.
- Accept typed `DateValue`, date-only-compatible local date-time values, and strict ISO `yyyy-MM-dd` strings. Null end is open-ended. Reject missing owner/identity/start and end-before-start; detect inclusive overlap and optionally detect gaps.
- Do not infer that overlap or gaps are universally invalid. Configuration opts into continuity; overlap is evaluated only for the configured interval scope.
- Use generic `attribute-comparison` for expected-versus-observed boundary preservation. The interval primitive itself only evaluates observed interval consistency.

## Alternatives

- Reuse the Pension employment timeline: rejected because it derives pension status transitions and contains pension-specific business semantics.
- Keep interval validity only in the HCM pack: rejected because date interval overlap/continuity is reusable and the acceptance assignment explicitly requires a generic primitive if none exists.
- Infer a current employment from target flags: rejected because history is the basis; HCM's pack-owned current-employment rule derives the as-of interval from configured start/end dates.

## Consequences

- Generic Verification gains one domain-neutral rule and one new compiled generic rule-type identifier; generic assemblies still have no concrete pack dependencies or semantic branches.
- HCM consumes the primitive and retains only its as-of current-employment derivation, relationship semantics, exact measure comparison, and configured payroll equation.
- Pension, Utility and Justice physical scenarios must be rerun because the shared Verification assembly changes; the HCM gate additionally verifies valid sequential dates, overlap, date-boundary mismatch, current-state derivation and correction.
- The rule makes no labor-law, benefit-policy or universal no-overlap claim. It is configured migration assurance, not legal validation.

## Validation

Verification workspace/interval tests pass 40/40, including valid sequential intervals, inclusive overlap, open-ended history, invalid bounds, and configured continuity. Five-pack conformance/genericity passes 10/10; all five final physical assurance entry points pass, including HCM overlap plus separate boundary-fidelity findings. This confirms no concrete HCM vocabulary in the generic runtime. Full local and remote acceptance passed; see the [PS-0.10F acceptance matrix](../PS0_10F_ACCEPTANCE_MATRIX.md), including run 38120389177.
