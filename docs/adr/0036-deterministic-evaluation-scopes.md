# ADR-0036: Deterministic Evaluation Scopes

- Status: Proposed for PS-1.0A review
- Date: 2026-10-29

## Context

Customer evaluations commonly use a declared subset rather than an entire
production source. A count or reconciliation result over that subset must not
be represented as completeness for the full migration. A scope must also be
bound to the actual captured records so that a checkpoint cannot be replayed
under a different scope or claim boundary.

The accepted PS-0.11 graph canonicalization and unscoped reports are frozen
compatibility surfaces. Source connectors have different capabilities, and
unsupported filters must not silently broaden their reads.

## Decision

Model an optional, immutable evaluation scope on a source artifact selector.
The scope has a stable ID, an explicit type, and zero or more typed relational
predicates. Its canonical fingerprint participates in graph and selector
identity. Checkpoints persist scope ID/type/fingerprint and counts; external
Verification binds to that metadata and reports the scope and a clear
outside-scope claim boundary. Predicate values are never emitted in ordinary
reports.

PS-1.0A does not execute merge/join edges. Multi-source graph representation
does not define row grouping, keys, cardinality, scheduler, provenance, or
Recovery behavior; executable merge semantics require a later explicit
assignment and separate design review.

Keep `proofshift-graph-canonical-v1` unchanged for graphs without scopes.
Graphs declaring scopes use a new canonicalization version that includes
scope semantics. Relational connectors advertise supported predicate
operators and apply values through provider parameters after schema/type
validation. Other connectors reject unsupported predicates.

## Alternatives considered

1. **Treat scope as report-only metadata.** Rejected because the report could
   label a run differently from the source records captured.
2. **Change all graphs to a new canonical version.** Rejected because it
   would change fingerprints for frozen PS-0.11 configurations without a
   semantic change.
3. **Accept arbitrary SQL predicates.** Rejected because it bypasses
   connector-neutral validation and safe parameter binding.
4. **Silently ignore unsupported filters.** Rejected because it could
   produce success-shaped results over a broader source than the user
   declared.

## Consequences

- A checkpoint cannot be replayed if scope identity or selector binding
  differs from the current configuration.
- Reports communicate only scope identity, fingerprint, type, and count, not
  raw predicate values.
- A scope fingerprint establishes deterministic configuration binding, not
  the completeness or truthfulness of a customer-provided subset.
- New predicate operators require capability declarations, safe type
  handling, tests, and documentation for each supporting connector.
- Unscoped projects must retain their accepted graph bytes, fingerprint
  version, outputs, and behavior.
