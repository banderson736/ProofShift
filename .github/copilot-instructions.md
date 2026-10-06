PS-0.10C Enterprise Connector & Data-Format Coverage is Accepted after GitHub Actions run 37416501703 passed all required jobs with 185 passed, 0 failed, 0 skipped. Preserve read-only observation separately from shadow writing/Recovery. The final code commits are 739b942007dbd87a71cf3d130b0056aeebca55aa and eefcae471877b6f0218f60b5ac10b34ec63ef219. Oracle CI builds from pinned official Oracle source and downloads Free media directly from Oracle; images are test-only and not redistributed. See docs/CONNECTOR_TEST_RUNTIMES.md and ADR-0019. Do not begin PS-0.10D, other provider/domain expansion, or production execution/rollback.
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

PS-0.4 was accepted after Docker-backed GitHub Actions run 37173803105 passed 67 tests with zero failures and zero skips. PS-0.5 Shadow Projection was accepted after run 37177385707 passed 82 tests with zero failures and zero skips. PS-0.6 Source Checkpoints & Reproducible Snapshots was accepted after Docker-backed GitHub Actions run 37180351598, job 111371493382, passed 88 tests with zero failures and zero skips. PS-0.7 Semantic Verification & Evidence Graph was accepted after Docker-backed GitHub Actions run 37185122084, job 111385343893, passed 96 tests with zero failures and zero skips. PS-0.8 Recovery Readiness & Dry-Run Qualification was accepted after Docker-backed GitHub Actions run 37190061657, job 111400188926, passed 103 tests with zero failures and zero skips. PS-0.9 Public Pension Assurance Vertical was accepted after Docker-backed GitHub Actions run 37238973286, job 111543651181 passed 113 tests with zero failures and zero skips. PS-0.10A Scale Baseline & Hot-Path Hardening is Accepted after Docker-backed run 37284331965, job 111679522042 passed 123 tests with zero failures and zero skips; the physical Medium full pipeline completed with verified workload memory and exact 149/0 semantics. Preserve the measured scale architecture and documented above-linear Verification runtime/scratch limits. Do not begin PS-0.10B/C, connector/domain expansion, production migration execution, or rollback without a new explicit assignment.

PS-0.6 checkpoint invariants: record per-endpoint consistency truthfully; do not claim cross-system atomicity; incomplete checkpoints cannot be replayed; checkpoint projection must match configuration, graph, exact source-node coverage, and selectors; and checkpoint mode must never fall back to live source reads.

Do not skip ahead to UI, SaaS, Kubernetes, AI, FHIR, Oracle, DB2, or enterprise infrastructure. PS-0.10A is complete; preserve its accepted behavior and leave PS-0.10B/C deferred until a new explicit assignment.

PS-0.10B Configuration & Authoring UX is Accepted after Docker-backed run 37353339103, job 111909408991 verified final implementation commit 625ca84 with 143 passed, 0 failed, 0 skipped. Preserve deterministic authoring, provider-owned typed schemas, explicit pack versions, read-only discovery/diff, review-state scaffold/import, named maps, effective explain/strict validation, and committed Pension configuration with exact 149/0/QUALIFIED semantics. The workflow and limitations are documented in docs/CONFIGURATION_AUTHORING.md, docs/AUTHORING_WALKTHROUGH.md and ADR-0018. Do not begin PS-0.10C, connector/domain expansion, UI/AI authoring, unrelated performance work, or production execution/rollback without a new explicit assignment.

## Implementation style
The active PS-0.10C assignment in the current-work section supersedes historical A/B status text that says C is deferred or unassigned. Production migration execution and rollback remain out of scope.

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
