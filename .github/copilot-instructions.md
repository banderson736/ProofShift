PS-0.10C Enterprise Connector & Data-Format Coverage is Accepted after GitHub Actions run 37416501703 passed all required jobs with 185 passed, 0 failed, 0 skipped. Preserve read-only observation separately from shadow writing/Recovery. The final code commits are 739b942007dbd87a71cf3d130b0056aeebca55aa and eefcae471877b6f0218f60b5ac10b34ec63ef219. Oracle CI builds from pinned official Oracle source and downloads Free media directly from Oracle; images are test-only and not redistributed. See docs/CONNECTOR_TEST_RUNTIMES.md and ADR-0019. PS-0.10D bounded concurrency passed 100k/200k, and the authorized p8/workers=8 Medium passed at 5,574.083 s. The authorized 1.5M physical Large run passed at exactly 1,500,000 generated records (exact 149/0, `QUALIFIED`, 28,143.783 s processing). PS-0.10D is Accepted (candidate 6d522728c3d02c7f1f1d83e3c6e1d624e27d8b3d, CI run 38014590907, merged as 091e7e3). Do not run a second Large or 2M+, rerun/tune Medium, or begin PS-0.10E without a new explicit assignment.
# GitHub Copilot Instructions — ProofShift

You are working in **ProofShift**, a migration assurance and evidence platform for high-risk system modernization.

## Current Assignment

PS-0.10F Cross-Domain Assurance is Accepted (October 11, 2026), merged to `main` as `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9`. Candidate `e3e437696808a16cda276e58a4460b9a297f0aea` passed GitHub Actions run 38120389177 with 318 passed, 0 failed, 0 skipped across all four required jobs. See docs/PS0_10F_ACCEPTANCE_MATRIX.md.

PS-0.10G Demo & Product Packaging is Accepted (October 24, 2026) on exact candidate `d3529ccabe04eb1e7b8b3cf2ab643ff8488cf1af`, included in `main` baseline `54be9ba2f056465d785353b839b98c8241cff503`. GitHub Actions [run 38172351820](https://github.com/banderson736/ProofShift/actions/runs/38172351820) passed all six jobs; regression jobs reported 331 passed, 0 failed, 0 skipped. Windows package-only smoke and five Ubuntu packaged Docker demos passed. The FHIR boundary correction was smoke-harness-only; scenario/runtime semantics are unchanged. Windows claims remain package-only and Ubuntu claims remain limited to packaged Docker evaluation, not general Linux support. See [PS-0.10G acceptance evidence](docs/PS0_10G_ACCEPTANCE_MATRIX.md).

PS-0.11A Generic Operator Reporting & Comparison is Accepted. Candidate `1837cad9ea05905dad491b3b0d54f0f7ba7cc852` passed run `40146586892` with 338 passed, 0 failed, 0 skipped, Windows package-only smoke, and five Ubuntu packaged demos; merged to `main` as `de59d85cca3a5eca4d1b9e49db69a51a8aa42ed1`.

PS-0.11B Product Demo Fixture Separation is Accepted and merged to `main` as `464ef14b235c93e08db58d575595a71eced0bafe`. Source candidate `c55704c1f0b4f0fdbc3d02ce1054b510539dca8c` and CI-tested synthetic merge `24f5714336e15cf0b6e626a7aff4f578ee5bbb29` have identical tree `b48f84580dd1bb8f410fcf6a924217d7a61d693e`. Run [40167582711](https://github.com/banderson736/ProofShift/actions/runs/40167582711) passed 350 tests with 0 failures/skips, Windows package-only smoke, and all five Ubuntu packaged Docker demos. See [the acceptance matrix](docs/PS0_11B_ACCEPTANCE_MATRIX.md).

PS-0.11C Release Identity & Reproducible Distribution is **Accepted (October 25, 2026)**. Candidate `286ea550e559b360fd9c85c2a65e2b1394a95408`, tree `d32fab40faa4bfc08c28b0742c46f8f372ae26c7`, passed GitHub Actions run `40178365888` with 350 passed, 0 failed, 0 skipped, and was merged to `main` as `5a1a3ec064aca98266cf0abb3c7749f7416da67e` with the same tree. See [PS-0.11C acceptance evidence](docs/PS0_11C_ACCEPTANCE_MATRIX.md). Product identity remains `ProofShift 0.1.0-preview.1`; do not publish, tag, or sign it.

PS-0.11D Internal Consolidation & Maintainability is **Accepted (October 25, 2026)**. Candidate `6a23233670e5be04d22cdf7b6304fd8090ca493a`, tree `077aaba4a5c0d9fe69e12627c18fe06557510b20`, passed push CI `40185263041` and PR CI `40185376379` with all ten jobs successful, then merged to `main` as `34056aaf7342e1426bda66b0d69ba6ae763c7938` with the same tree. Both runs passed 359 tests with 0 failures/skips and the required package evidence. See [the PS-0.11D acceptance matrix](docs/PS0_11D_ACCEPTANCE_MATRIX.md).

PS-0.11E Repository & Product Governance is **Accepted (October 25, 2026)**. Final governed-main commit `2d5744753309643d7451123d59bf44b338472d4b`, tree `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43`, passed all eleven required jobs in run `40244724080`; test jobs reported 359 passed, 0 failed, 0 skipped. See the [PS-0.11E acceptance matrix](docs/PS0_11E_ACCEPTANCE_MATRIX.md).

PS-0.11F Bring-Your-Own-Migration Evaluation is **Accepted (October 26, 2026), by repository-owner direction**. Exact candidate `9748d22889dce85584915dfea06df037f925bf30`, tree `1d96d7f44133789831cb17c3951b6dc5a3187daf`, passed push CI `40269503856` and PR CI `40269505613` with all eleven required jobs green; PR #16 merged to `main` as `e811c08729609afdd3a2cfdf4cb7634012871205` with the same tree. Its 45 acceptance gates passed. Acceptance-record PR #17 merged to `main` as `810f2f450176bde1eee0edcc200650d633b8f04b`, tree `8ec082eb21683dec06ee48971f49e0d088cdea64`. See [the PS-0.11F acceptance matrix](docs/PS0_11F_ACCEPTANCE_MATRIX.md).

PS-0.11G Pilot Readiness Acceptance is **Accepted (October 28, 2026)**, and PS-0.11 is complete. The formal decision is **GO — controlled technical pilot/evaluation ready** within documented support boundaries. The exact candidate `b25a483dd5b728474e3e1f8e1b957885d42e309a` passed push CI `40653602682` and PR-context CI `40653605811` with all eleven required checks; PR #18 merged as `6eaac3f7de155d775032964739c832cca2317b6f`, with the same tree. See [the acceptance record](docs/PS0_11G_PILOT_READINESS_ACCEPTANCE.md) and [acceptance matrix](docs/PS0_11G_ACCEPTANCE_MATRIX.md). Preserve read-only external Verification, the no-Projection/no-Recovery-qualification boundary, all accepted domain outcomes, package-only Windows claim, Ubuntu packaged-Docker evaluation boundary, and all eleven protected checks. Production migration/rollback, publication, tagging, and signing remain unauthorized.
PS-0.11G Pilot Readiness Acceptance is **Accepted (October 28, 2026)**, and PS-0.11 is complete. The formal decision is **GO — controlled technical pilot/evaluation ready** within documented support boundaries. The exact candidate `b25a483dd5b728474e3e1f8e1b957885d42e309a` passed push CI `40653602682` and PR-context CI `40653605811` with all eleven required checks; PR #18 merged as `6eaac3f7de155d775032964739c832cca2317b6f`, with the same tree. See [the acceptance record](docs/PS0_11G_PILOT_READINESS_ACCEPTANCE.md) and [acceptance matrix](docs/PS0_11G_ACCEPTANCE_MATRIX.md). Preserve read-only external Verification, the no-Projection/no-Recovery-qualification boundary, all accepted domain outcomes, package-only Windows claim, Ubuntu packaged-Docker evaluation boundary, and all eleven protected checks. Production migration/rollback, publication, tagging, and signing remain unauthorized.

Current assignment: **PS-1.0A Customer Data Evaluation & Generalized Mapping Foundation — In Progress**, authorized on `ps-1.0a-customer-data-evaluation` from baseline `a095c21588cbd1608b8a543cdfaab71312b596d8`. Follow [the scope](docs/PS1_0A_CUSTOMER_DATA_EVALUATION.md), [the acceptance matrix](docs/PS1_0A_ACCEPTANCE_MATRIX.md), and the customer runbook/preparation/mapping/reconciliation guides. The claim boundary is complete accounting only inside a declared evaluation scope. Preserve read-only external Verification and PS-0.11 compatibility. Do not self-accept, merge, or begin PS-1.0B. Production migration/rollback, UI/API/SaaS, AI/fuzzy matching, arbitrary SQL/scripts, new packs/connectors, distributed execution, and persistent server storage remain out of scope.

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

The explicit PS-0.10D continuation assignment authorized bounded scratch-write workers, the 100k/200k saturation study, and one conditionally authorized Medium rerun. The p8/workers=8 `GlobalRuleReference` Medium rerun passed exact correctness and both processing thresholds. The latest assignment authorized one 1.5M physical Large run after its scratch-capacity preflight; that run passed and is recorded in docs/PS0_10D_LARGE_BENCHMARK.json. PS-0.10D was then Accepted (CI run 38014590907, merged as 091e7e3). A second Large, 2M+, another Medium, PS-0.10E and production migration execution/rollback each require a new explicit assignment. Preserve the accepted architecture. See docs/PS0_10D_MEDIUM_BENCHMARK.json.

PS-0.6 checkpoint invariants: record per-endpoint consistency truthfully; do not claim cross-system atomicity; incomplete checkpoints cannot be replayed; checkpoint projection must match configuration, graph, exact source-node coverage, and selectors; and checkpoint mode must never fall back to live source reads.

Do not skip ahead to UI, SaaS, Kubernetes, AI, FHIR, Oracle, DB2, or enterprise infrastructure. PS-0.10A is complete; preserve its accepted behavior and leave PS-0.10B/C deferred until a new explicit assignment.

PS-0.10B Configuration & Authoring UX is Accepted after Docker-backed run 37353339103, job 111909408991 verified final implementation commit 625ca84 with 143 passed, 0 failed, 0 skipped. Preserve deterministic authoring, provider-owned typed schemas, explicit pack versions, read-only discovery/diff, review-state scaffold/import, named maps, effective explain/strict validation, and committed Pension configuration with exact 149/0/QUALIFIED semantics. The workflow and limitations are documented in docs/CONFIGURATION_AUTHORING.md, docs/AUTHORING_WALKTHROUGH.md and ADR-0018. Do not begin PS-0.10C, connector/domain expansion, UI/AI authoring, unrelated performance work, or production execution/rollback without a new explicit assignment.

## Implementation style
The active PS-0.10D continuation in the current-work section supersedes historical status text that says D is deferred or unassigned. Production migration execution and rollback remain out of scope.

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
