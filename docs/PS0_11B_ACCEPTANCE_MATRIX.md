# PS-0.11B Acceptance Matrix — Product Demo Fixture Separation

## Status

**PS-0.11B — ACCEPTED.** All 26 acceptance gates passed. The candidate was formally reviewed and merged; this matrix records evidence and does not authorize changes to the accepted slice.

| Evidence | Value |
|---|---|
| Base before PS-0.11B | `de59d85cca3a5eca4d1b9e49db69a51a8aa42ed1` |
| Source candidate | `c55704c1f0b4f0fdbc3d02ce1054b510539dca8c` |
| Tested GitHub Actions merge SHA | `24f5714336e15cf0b6e626a7aff4f578ee5bbb29` |
| Candidate and tested-merge tree | `b48f84580dd1bb8f410fcf6a924217d7a61d693e` (identical) |
| Acceptance run | [40167582711](https://github.com/banderson736/ProofShift/actions/runs/40167582711) |
| Final merge SHA | `464ef14b235c93e08db58d575595a71eced0bafe` |
| Gate totals | **26 PASS, 0 FAIL, 0 NOT YET** |
| Review / merge | Formally reviewed and merged to `main` |

GitHub Actions tested the synthetic pull-request merge commit, not the source candidate SHA. The source candidate and tested merge have the same Git tree, so the tested source contents are identical.

## Architecture and source boundary

| Gate | Evidence | Status |
|---|---|---|
| A1 | Dedicated non-test DemoScenarios library owns all five physical scenarios and shared support. | PASS |
| A2 | Dedicated DemoRunner invokes the scenario library through the strict runner contract. | PASS |
| A3 | DemoCommands remains generic, validates supported runner types, and fails closed for unknown types. | PASS |
| A4 | EndToEnd independently consumes the scenario library and retains exact assertions; no duplicate authoritative implementation remains. | PASS |
| A5 | Runtime dependency graph excludes EndToEnd, xUnit, Microsoft.NET.Test.Sdk, and test-module execution dependencies. | PASS |
| A6 | Testcontainers is used only where required by the scenario fixture lifecycle. | PASS |
| A7 | All five manifests use DemoRunner entry points and contain no test execution metadata. | PASS |
| A8 | Package construction no longer builds/copies test modules or rewrites test manifests. | PASS |

## Scenario regressions

| Gate | Evidence | Status |
|---|---|---|
| S1 | Pension package results preserve 149 defective discrepancies, 0 corrected, and `NOT QUALIFIED` / `QUALIFIED`. | PASS |
| S2 | Pension false-Reverse preserves Verification `PASSED`, 0 Verification failures, 2 unsafe Reverse declarations, 2 Recovery discrepancies, and `NOT QUALIFIED`. | PASS |
| S3 | Utility preserves 14 seeded defects / 26 findings and corrected 0 failures / `QUALIFIED`. | PASS |
| S4 | Justice preserves 15 seeded defects / 24 findings and corrected 0 failures / `QUALIFIED`. | PASS |
| S5 | HCM preserves 14 seeded defects / 25 findings and corrected 0 failures / `QUALIFIED`. | PASS |
| S6 | FHIR preserves 9 seeded defects / 15 findings and corrected 0 failures / `QUALIFIED`. | PASS |
| S7 | Report/compare schemas, compatibility aliases, Evidence references, deterministic outputs, reset/repeat equivalence, and secret safety remain unchanged. | PASS |

## Package and clean-runner acceptance

| Gate | Evidence | Status |
|---|---|---|
| P1 | Reproducible Windows and Linux x64 packages include non-test runtime assets and notices. | PASS |
| P2 | Package checks exclude test assemblies/modules, test-only runtime dependencies, source, and test-runner metadata. | PASS |
| P3 | Fresh Windows package-only smoke passed. | PASS |
| P4 | All five extracted Ubuntu Docker demos passed without repository test artifacts. | PASS |
| P5 | Windows claims remain package-only; Ubuntu claims remain scoped to packaged Docker evaluation. | PASS |
| P6 | Clean-room, workspace ownership, reset-safety, and reparse-point checks passed, including reset/repeat equivalence. | PASS |

| Artifact | Size | Archive SHA-256 | Content SHA-256 |
|---|---:|---|---|
| Windows x64 | 105,354,502 bytes | `b9eb599c9ed20d15fe9e50d28685c9837655820482107fdcdbb742c47f71a08a` | `60145dade2ba5ea5dbbc8e791e0a14170edbde54f8a2649fda365ceac89ed670` |
| Linux x64 | 101,925,442 bytes | `e64f6866f46a0ba024e99505e3c1127a2d8ff491f9ce5c78fc3254e193cb3353` | `9c8e224cd37e00b00eba4b527c32ea4ee8e022a7dfb6712d1bed5f8c8a3e8173` |

## Build, tests, and documentation

| Gate | Evidence | Status |
|---|---|---|
| V1 | Architecture, runner validation, manifest, package-content, and scenario regression tests passed. | PASS |
| V2 | Required regression/integration jobs passed with no failures or skips. | PASS |
| V3 | Required build and package checks passed. | PASS |
| V4 | Product, evaluation, runbook, manifest, architecture, and assignment documentation agree with the accepted runtime boundary. | PASS |
| V5 | Exact tested source tree passed the complete required CI campaign and normal formal review. | PASS |

| Required job | Result |
|---|---:|
| build-and-test | 293 passed, 0 failed, 0 skipped |
| remote-storage-integration | 53 passed, 0 failed, 0 skipped |
| oracle-integration | 2 passed, 0 failed, 0 skipped |
| db2-integration | 2 passed, 0 failed, 0 skipped |
| **Total automated tests** | **350 passed, 0 failed, 0 skipped** |
| windows-package-smoke | PASS |
| linux-package-demos | 5 / 5 PASS |

## Decision and scope

[ADR-0032](adr/0032-non-test-demo-scenario-runtime.md) is Accepted under PS-0.11B. The slice changed the demo runtime boundary, not scenario semantics. All five deterministic synthetic scenarios, reporting, comparison, Evidence, Recovery, qualification, and package-independence behavior remain as recorded above.

Windows evidence is package-only. Ubuntu evidence covers the packaged Docker evaluation and does not claim general Linux product support. PS-0.11B is merged as `464ef14b235c93e08db58d575595a71eced0bafe`.
