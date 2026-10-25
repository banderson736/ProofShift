# PS-0.11B — Product Demo Fixture Separation

## Status

**PS-0.11B — ACCEPTED.** The implementation was formally reviewed and merged to `main` as `464ef14b235c93e08db58d575595a71eced0bafe`. Source candidate `c55704c1f0b4f0fdbc3d02ce1054b510539dca8c` and CI-tested synthetic merge `24f5714336e15cf0b6e626a7aff4f578ee5bbb29` have identical tree `b48f84580dd1bb8f410fcf6a924217d7a61d693e`. Run [40167582711](https://github.com/banderson736/ProofShift/actions/runs/40167582711) passed 350 tests with 0 failures and 0 skips, Windows package-only smoke, and all five Ubuntu packaged Docker demos. See the [acceptance matrix](PS0_11B_ACCEPTANCE_MATRIX.md) for artifact hashes and domain results.

## Objective

Separate the five packaged physical demo scenarios from `ProofShift.EndToEnd.Tests`. The evaluation package must run its synthetic scenarios through dedicated non-test infrastructure, not through test discovery or the EndToEnd test assembly. Tests must independently exercise that same scenario implementation and assert the exact accepted results.

This work is a fixture/runtime-boundary change, not a scenario redesign. Preserve the accepted PS-0.10G demo behavior and PS-0.11A persisted reporting/comparison behavior.

## Required architecture

- Add a non-test DemoScenarios library for the reusable physical scenario implementations and shared scenario support.
- Add a dedicated DemoRunner executable that accepts a strict, purpose-built runner contract and invokes the scenario library. It must not host or emulate a test framework.
- Keep `ProofShift.Cli` and its generic `DemoCommands` domain-neutral. The CLI should validate an explicitly supported runner type and fail closed for unknown runner types; do not add pack-specific orchestration branches to generic CLI code.
- Make the five scenario manifests identify their DemoRunner scenario entry points. Remove test-module runner declarations, test class/method references, and package-time test-manifest rewriting.
- Keep the EndToEnd test project as an independent consumer of DemoScenarios. Tests assert scenario results; the product demo runtime must not depend on test projects or test-only frameworks.
- Preserve one authoritative implementation of each physical scenario. Do not duplicate the scenario logic between runtime and tests.
- Keep Testcontainers only where required by the non-test demo fixture lifecycle. The DemoRunner and DemoScenarios runtime graph must not depend on xUnit, `Microsoft.NET.Test.Sdk`, MTP test-module execution, or `ProofShift.EndToEnd.Tests`.
- Preserve deterministic synthetic data and existing ownership, cleanup, cancellation, output, and secret-safety behavior.

## Exact behavior to preserve

All five scenarios continue to use their accepted source/target setup and report paths:

| Pack | Defective scenario | Corrected scenario |
|---|---|---|
| Pension | 149 business discrepancies; `NOT QUALIFIED` | 0 discrepancies/accounting/lineage gaps; `QUALIFIED` |
| Utility | 14 seeded defects / 26 findings | 0 failures; `QUALIFIED` |
| Justice | 15 seeded defects / 24 findings | 0 failures; `QUALIFIED` |
| HCM | 14 seeded defects / 25 findings | 0 failures; `QUALIFIED` |
| FHIR | 9 seeded defects / 15 findings | 0 failures; `QUALIFIED` |

The Pension false-Reverse case remains Verification `PASSED` with zero Verification failures and two unsafe Reverse edges; its two Recovery discrepancies keep the run `NOT QUALIFIED`. Existing report/compare fields, aliases, Evidence references, qualification, fixture determinism, and package smoke expectations remain unchanged.

## Package and manifest requirements

- The package includes the CLI, DemoRunner, DemoScenarios and required runtime assets, sample projects, manifests, and accurate third-party notices.
- It excludes EndToEnd test assemblies, test modules, test-only dependencies, repository test source, and test-runner metadata.
- Package construction no longer builds or copies the EndToEnd test module, converts `dotnet-test` manifests, or injects class/method execution metadata.
- Package smoke checks prove the expected DemoRunner assets are present and test artifacts are absent. The smoke command runs from a clean extracted package outside the checkout.
- Windows remains a package-only smoke claim. Ubuntu validation is limited to the packaged Docker evaluation; it is not a general Linux support claim.
- Preserve package reproducibility, workspace ownership/reset safety, and exact platform-specific assertions.

## Documentation and scope boundaries

Update the evaluation, demo runbook, manifest contract, architecture/dependency documentation, and package instructions to describe the non-test runner and its actual runtime dependencies. Keep release identity/package renaming deferred.

The scope boundaries above describe the accepted PS-0.11B slice. Its acceptance does not authorize production migration or rollback, connector/domain expansion, FHIR Bulk, AI-assisted authoring, UI/API/auth, broad Verification changes, unrelated performance tuning, self-contained/AOT deployment, or installers.

Acceptance gates and evidence are recorded in the [PS-0.11B acceptance matrix](PS0_11B_ACCEPTANCE_MATRIX.md).
