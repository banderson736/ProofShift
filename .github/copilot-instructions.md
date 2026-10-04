# GitHub Copilot Instructions — ProofShift

You are working in **ProofShift**, a migration assurance and evidence platform for high-risk system modernization.

## Mandatory context

Before architectural or implementation work, read:

- `README.md`
- `docs/PRODUCT_CONTEXT.md`
- `docs/PRODUCT_PRINCIPLES.md`
- `docs/ARCHITECTURE.md`
- `docs/DOMAIN_MODEL.md`
- `docs/CONFIGURATION.md`
- `docs/PS0_SPECIFICATION.md`
- `docs/ROADMAP.md`
- `docs/DECISIONS.md`

Do not infer product goals only from nearby code.

## Core invariants

Never violate these without an explicit ADR and user approval:

1. ProofShift models logical systems, not only databases.
2. Core is domain-neutral and connector-neutral.
3. Domain packs depend on core abstractions; core never depends on a concrete domain pack.
4. Connector implementations depend on connector abstractions; core never depends on a concrete connector.
5. History is first-class.
6. Every source artifact must have an explainable disposition.
7. Every target artifact must have lineage/provenance.
8. Every material verification result must have traceable evidence.
9. Every destructive migration operation must have recovery metadata.
10. Dry runs create isolated shadow output where practical.
11. Verification must work even when another system performs the migration.
12. Configuration, graph, rules, connectors and domain packs must be versionable/reproducible.
13. PS-0 uses deterministic synthetic data only.

## Current work boundary

PS-0.4 has been accepted after the Docker-backed GitHub Actions suite passed with 67 tests, zero failures, and zero skips. The active milestone is PS-0.5 Shadow Projection, implemented only to the acceptance criteria in `docs/adr/0006-shadow-projection-runtime.md` and the user-provided `ProofShift Copilot Prompt — PS-0.5 Shadow Projection`. PS-0.5 acceptance still requires its own Docker-backed CI run with no required integration skips. Do not begin PS-0.6 or broaden scope.

Do not skip ahead to UI, SaaS, Kubernetes, AI, FHIR, or enterprise infrastructure.

## Implementation style

- Target .NET 10 / modern C#.
- Favor immutable records/value objects for domain state.
- Use strongly typed identifiers at domain boundaries.
- Prefer explicit types over `object` for normalized values.
- Keep domain logic free of database/framework dependencies.
- Keep abstractions small and driven by actual PS-0 vertical slices.
- Avoid speculative generic frameworks.
- Prefer deterministic pure functions for comparison/transformation logic where possible.
- Async streams are appropriate for large connector reads.
- Do not load a complete production-size dataset into memory.
- CancellationToken should flow through I/O and long-running work.
- Treat evidence as structured data, not only log messages.
- Provide actionable validation/error messages.

## Testing style

- Tests must assert business invariants and exact seeded defects.
- Avoid shallow tests that only assert property getters.
- Use deterministic seeds.
- Every intentionally seeded defect must eventually have an exact assertion in the end-to-end scenario.
- Add regression tests for every fixed defect in core behavior.
- Do not weaken assertions to make a failing test pass without explaining why the expectation was wrong.

## Changes to architecture

If code suggests the architecture should change:

1. explain the implementation pressure;
2. compare alternatives;
3. add/update an ADR;
4. update relevant docs;
5. do not silently make the change.

## Security/IP

- Never add real customer/employer/client data.
- Never add secrets.
- Keep secret references out of hashes/logs/evidence after resolution.
- Do not copy proprietary employer/client implementation details.

## Work reporting

At the end of each Copilot task, report:

- milestone/slice addressed;
- files changed;
- design decisions made;
- tests added/changed;
- focused/full test results;
- remaining acceptance criteria;
- any docs/ADR updates;
- blockers/assumptions.
