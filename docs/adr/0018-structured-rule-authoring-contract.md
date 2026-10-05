# ADR-0018: Structured Rule Authoring Contracts

- Status: Accepted for the explicitly assigned PS-0.10B implementation
- Date: 2026-10-05

## Pressure And Alternatives

The existing rule loader flattens sequences into comma-delimited strings and rejects mappings. This cannot represent nested grouping/comparison semantics, distinguish numeric/boolean values, or give a provider-owned validation/schema contract. Replacing every existing option with a new interpretation would silently change accepted configuration and fingerprints. Retaining string dictionaries as the primary contract would preserve that limitation.

## Decision

Rule documents explicitly declare `version: 2` to preserve nested immutable domain values. Omitted document version remains legacy version 1 and retains existing parsing/fingerprints. The root referenced-file configuration format remains version 1; rule-document version and rule-implementation version are independent. Rule descriptors belong to the rule provider/pack, never a concrete pack dependency in Configuration or Verification. They describe option kinds, nesting, allowed values, defaults, scope, descriptions, and examples; the same descriptor is the validation/schema/explanation source.

Implementation versions resolve exactly and fail closed when unavailable. Legacy version 1 options are not silently subjected to new type inference; version 2 rejects unknown options and incorrect types before rule creation. New rule-set canonicalization includes a structured-value marker; legacy rule-set bytes remain unchanged. Lists stay lists through authoring and execution; comma-separated parsing exists only at the explicit legacy boundary. Existing immutable ValueNode types are reused rather than creating another normalization framework.

Physical discovery does not imply business semantics. Pack declarations remain the only source of pack-specific schemas. No PS-0.10A runtime/storage tuning, connector expansion, new packs, UI, or AI inference is authorized by this decision.

## Validation And Remaining Work

Nested parsing and descriptor/version rejection tests cover the new boundary. The exact seeded Pension rules execute in both legacy and structured modes with the same corrected and defective business outcomes. Explicit pack selection, optional read-only discovery on existing connectors, structural artifacts/diff, review-state scaffold/CSV import, effective explanations/schemas, named maps through normal graph parsing, and committed full Pension semantics are implemented and validated. Physical discovery never activates domain semantics. PS-0.10B was accepted after final implementation commit `625ca84` passed Docker-backed run 37353339103, job 111909408991 with 143 passed, 0 failed, 0 skipped. No PS-0.10C scope is authorized by this decision.