# PS-0.11B Acceptance Matrix — Product Demo Fixture Separation

## Status

In Progress. All gates begin **NOT YET**. This matrix records candidate evidence; it does not authorize self-acceptance or merge.

**Base:** PS-0.11A merge `de59d85cca3a5eca4d1b9e49db69a51a8aa42ed1`
**Candidate:** NOT YET
**GitHub Actions run:** NOT YET
**Formal review / merge:** NOT YET

## Architecture and source boundary

| Gate | Required evidence | Status |
|---|---|---|
| A1 | Dedicated non-test DemoScenarios library owns the five physical scenario implementations and shared scenario support. | NOT YET |
| A2 | Dedicated DemoRunner executable invokes DemoScenarios through the documented generic runner contract. | NOT YET |
| A3 | DemoCommands remains domain-neutral; supported runner types are explicitly validated and unknown types fail closed. | NOT YET |
| A4 | EndToEnd tests independently call the scenario implementation and retain exact assertions; no duplicate authoritative scenario implementation remains. | NOT YET |
| A5 | DemoRunner/DemoScenarios dependency graph contains no EndToEnd assembly, xUnit, `Microsoft.NET.Test.Sdk`, or test-module runtime execution dependency. | NOT YET |
| A6 | Testcontainers is retained only as a runtime dependency where required by the scenario fixture lifecycle. | NOT YET |
| A7 | All five manifests use the DemoRunner contract and no longer contain test project/class/method execution metadata. | NOT YET |
| A8 | Package construction no longer builds/copies EndToEnd tests or rewrites test-module manifests. | NOT YET |

## Scenario regressions

| Gate | Required evidence | Status |
|---|---|---|
| S1 | Pension packaged defective/corrected runs preserve 149 / 0 discrepancy results and `NOT QUALIFIED` / `QUALIFIED`. | NOT YET |
| S2 | Pension false-Reverse remains Verification `PASSED`, zero Verification failures, two unsafe Reverse edges, two Recovery discrepancies, `NOT QUALIFIED`. | NOT YET |
| S3 | Utility preserves 14 seeded defects / 26 findings and corrected zero failures / `QUALIFIED`. | NOT YET |
| S4 | Justice preserves 15 seeded defects / 24 findings and corrected zero failures / `QUALIFIED`. | NOT YET |
| S5 | HCM preserves 14 seeded defects / 25 findings and corrected zero failures / `QUALIFIED`. | NOT YET |
| S6 | FHIR preserves 9 seeded defects / 15 findings and corrected zero failures / `QUALIFIED`. | NOT YET |
| S7 | Report/compare schema, compatibility aliases, Evidence references, deterministic output, reset/repeat equivalence, and secret safety remain unchanged. | NOT YET |

## Package and clean-runner acceptance

| Gate | Required evidence | Status |
|---|---|---|
| P1 | Reproducible Windows and Linux x64 packages contain DemoRunner/DemoScenarios runtime assets and accurate notices. | NOT YET |
| P2 | Package content checks prove EndToEnd/test assemblies, test modules, test-only dependencies, source, and test-runner metadata are absent. | NOT YET |
| P3 | Clean extracted Windows package passes package-only CLI, sample-configuration, manifest, and content smoke. | NOT YET |
| P4 | Clean extracted Ubuntu package runs all five Docker-backed demos and exact output checks without repository test artifacts. | NOT YET |
| P5 | Platform claims remain limited to Windows package-only smoke and Ubuntu packaged Docker evaluation. | NOT YET |
| P6 | Existing clean-room, ownership-marker, reset safety, and reparse-point checks remain effective. | NOT YET |

## Build, tests, and documentation

| Gate | Required evidence | Status |
|---|---|---|
| V1 | Targeted architecture/dependency, runner validation, manifest, package-content, and scenario regression tests pass. | NOT YET |
| V2 | Required full regression and integration suites pass with no unexplained skips. | NOT YET |
| V3 | `git diff --check` and the repository's required build/package checks pass. | NOT YET |
| V4 | README, evaluation guide, demo runbook, manifest contract, architecture/dependency docs, and current-assignment guidance agree with the implementation. | NOT YET |
| V5 | Candidate SHA has complete required GitHub Actions evidence and is ready for formal review; no self-acceptance or merge is performed. | NOT YET |

## Evidence log

| Evidence | Value |
|---|---|
| Candidate commit | NOT YET |
| GitHub Actions run | NOT YET |
| Build-and-test results | NOT YET |
| Package job results | NOT YET |
| Linux five-demo results | NOT YET |
| Review status | NOT YET |
