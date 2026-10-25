# PS-0.11D Acceptance Matrix — Internal Consolidation & Maintainability

## Status

**PS-0.11D — Accepted (October 25, 2026).** Candidate
`6a23233670e5be04d22cdf7b6304fd8090ca493a`, tree
`077aaba4a5c0d9fe69e12627c18fe06557510b20`, passed push CI run
`40185263041` and PR CI run `40185376379`. It was merged to `main` as
`34056aaf7342e1426bda66b0d69ba6ae763c7938` with the identical tree.

## Connector consolidation

| Gate | Required evidence | Status |
|---|---|---|
| CC1 | No CSV-owned source compile link remains in Parquet or StructuredFiles; one implementation owner exists. | PASS |
| CC2 | Connector-internal project dependencies are limited to required abstractions and SQLite; no core/domain/pack dependency points to it. | PASS |
| CC3 | `DiskBackedIdentityIndex` remains internal and grants access only to CSV, Parquet, and StructuredFiles. | PASS |
| CC4 | First/different identities succeed; duplicate identities retain the same stable connector error code. | PASS |
| CC5 | Cancellation propagates; creation failures remain safely wrapped; cleanup occurs on failure and disposal. | PASS |
| CC6 | Scratch cleanup remains bounded to ProofShift-owned abandoned files; raw identities do not enter diagnostics. | PASS |
| CC7 | Architecture tests reject connector-to-connector production source compile links and forbidden inward dependencies. | PASS |

## CLI consolidation

| Gate | Required evidence | Status |
|---|---|---|
| CLI1 | `Program.cs` is composition/routing focused; command workflow implementations live in cohesive internal modules. | PASS |
| CLI2 | Version, Configuration, Migration, and Assurance responsibilities have clear module ownership; Demo and Discovery remain separate. | PASS |
| CLI3 | No external CLI framework, reflection dispatcher, command bus, mediator, or unnecessary DI container is added. | PASS |
| CLI4 | All existing commands/options remain available with existing required-argument behavior and exit codes. | PASS |
| CLI5 | Representative human and JSON output, stdout/stderr destinations, and secret-safe errors remain compatible. | PASS |
| CLI6 | Configured/standalone report paths and generic pack resolution remain intact; no second reporting path is introduced. | PASS |
| CLI7 | Focused tests cover version, validate, plan, inspect, snapshot, project, verify, dry-run, evidence, recovery, report, compare, and demo list contracts. | PASS |

## Verification consolidation

| Gate | Required evidence | Status |
|---|---|---|
| VC1 | Ordering normalization/comparison/fingerprinting is extracted behind an internal cohesive codec. | PASS |
| VC2 | Exact ordering tests cover null, string, integer, decimal, temporal, binary/hash, negative/large/equal values, and culture independence. | PASS |
| VC3 | Existing normalization output, ordering, and deterministic fingerprints are unchanged. | PASS |
| VC4 | Diagnostics formatting/writing is extracted into a cohesive internal component. | PASS |
| VC5 | Diagnostic schema and existing performance evidence fields remain unchanged. | PASS |
| VC6 | Partition scheduling, backpressure, workers, writer lifecycle, transactions, publication, and read routing are unchanged. | PASS |
| VC7 | Public Verification APIs remain unchanged; equivalent fixture fingerprints remain identical. | PASS |

## Architecture and semantic regression

| Gate | Required evidence | Status |
|---|---|---|
| AR1 | Existing pack conformance and concrete-pack dependency guards pass. | PASS |
| AR2 | Dependency-direction tests prove connector-internal implementation cannot leak into Domain/Core/Packs/Graph/Engine/Verification/Recovery. | PASS |
| AR3 | Explicit project references match direct type usage; Reporting → Verification remains direct. | PASS |
| AR4 | DemoRunner/DemoScenarios remain independent of test assemblies and frameworks; package excludes test runtime. | PASS |
| AR5 | Configuration/graph/checkpoint/Projection/Verification/Evidence/Recovery/report/compare fingerprints and semantics are unchanged. | PASS |
| AR6 | Pension, Utility, Justice, HCM, and FHIR exact seeded findings and qualification outcomes remain unchanged. | PASS |

## Build, test, and distribution regression

| Gate | Required evidence | Status |
|---|---|---|
| RT1 | Normal restore, locked restore, and full solution build pass with SDK 10.0.402. | PASS |
| RT2 | Focused connector, architecture, pack, CLI, Verification, Evidence, Recovery, report/compare tests pass. | PASS |
| RT3 | Full build-and-test and RemoteStorage jobs pass with 0 required failures/skips. | PASS |
| RT4 | Oracle and Db2 integration jobs pass with 0 failures/skips. | PASS |
| RT5 | Windows package-only smoke and all five Ubuntu packaged Docker demos pass. | PASS |
| RT6 | Independent Windows and Linux package A/B hashes and deterministic provenance match. | PASS |
| RT7 | Runtime-only package smoke passes with no SDK installed and supported runtime 10.0.13. | PASS |
| RT8 | Exact source candidate/tree and full required CI evidence are recorded; no PS-0.10D large benchmark was rerun. | PASS |

## Candidate and evidence log

| Evidence | Value |
|---|---|
| PS-0.11D source candidate / tree | `6a23233670e5be04d22cdf7b6304fd8090ca493a` / `077aaba4a5c0d9fe69e12627c18fe06557510b20` |
| Exact-candidate push CI / PR CI | `40185263041` / `40185376379`; all ten jobs passed on both runs |
| Merge commit / tree | `34056aaf7342e1426bda66b0d69ba6ae763c7938` / `077aaba4a5c0d9fe69e12627c18fe06557510b20` |
| Test totals | build-and-test 301; RemoteStorage 54; Oracle 2; Db2 2; total 359 passed, 0 failed, 0 skipped |
| Distribution evidence | Windows package-only smoke PASS; five Ubuntu packaged Docker demos PASS; Windows/Linux independent A/B reproducibility PASS; Linux runtime-only smoke PASS |
| Windows package archive / content SHA-256 | `885ce9e13091879b9af6ca2b232e1b891d12900ff071608d8494f708600846e5` / `e846ea5e7a6f8a15af9b8dce07ad08f2b2b06a34857a989e2e3a041399a15163` |
| Linux package archive / content SHA-256 | `46cd1fd2e9c6f2538aa4ec20a86bfc345660b984fb54984e0e840002c4c14a85` / `aaa0a22f61736584316d372b95ed67407e6dd4c5f5eb2e4f8a249e47a048436c` |
| Gates | **35 PASS, 0 FAIL, 0 NOT YET** |
| Formal status | **Accepted (October 25, 2026)** |
