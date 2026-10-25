# PS-0.11D Acceptance Matrix — Internal Consolidation & Maintainability

## Status

**PS-0.11D — In Progress.** Starting baseline: `main`
`5a1a3ec064aca98266cf0abb3c7749f7416da67e`, tree
`d32fab40faa4bfc08c28b0742c46f8f372ae26c7`. All gates begin **NOT YET**.
Do not self-accept.

## Connector consolidation

| Gate | Required evidence | Status |
|---|---|---|
| CC1 | No CSV-owned source compile link remains in Parquet or StructuredFiles; one implementation owner exists. | NOT YET |
| CC2 | Connector-internal project dependencies are limited to required abstractions and SQLite; no core/domain/pack dependency points to it. | NOT YET |
| CC3 | `DiskBackedIdentityIndex` remains internal and grants access only to CSV, Parquet, and StructuredFiles. | NOT YET |
| CC4 | First/different identities succeed; duplicate identities retain the same stable connector error code. | NOT YET |
| CC5 | Cancellation propagates; creation failures remain safely wrapped; cleanup occurs on failure and disposal. | NOT YET |
| CC6 | Scratch cleanup remains bounded to ProofShift-owned abandoned files; raw identities do not enter diagnostics. | NOT YET |
| CC7 | Architecture tests reject connector-to-connector production source compile links and forbidden inward dependencies. | NOT YET |

## CLI consolidation

| Gate | Required evidence | Status |
|---|---|---|
| CLI1 | `Program.cs` is composition/routing focused; command workflow implementations live in cohesive internal modules. | NOT YET |
| CLI2 | Version, Configuration, Migration, and Assurance responsibilities have clear module ownership; Demo and Discovery remain separate. | NOT YET |
| CLI3 | No external CLI framework, reflection dispatcher, command bus, mediator, or unnecessary DI container is added. | NOT YET |
| CLI4 | All existing commands/options remain available with existing required-argument behavior and exit codes. | NOT YET |
| CLI5 | Representative human and JSON output, stdout/stderr destinations, and secret-safe errors remain compatible. | NOT YET |
| CLI6 | Configured/standalone report paths and generic pack resolution remain intact; no second reporting path is introduced. | NOT YET |
| CLI7 | Focused tests cover version, validate, plan, inspect, snapshot, project, verify, dry-run, evidence, recovery, report, compare, and demo list contracts. | NOT YET |

## Verification consolidation

| Gate | Required evidence | Status |
|---|---|---|
| VC1 | Ordering normalization/comparison/fingerprinting is extracted behind an internal cohesive codec. | NOT YET |
| VC2 | Exact ordering tests cover null, string, integer, decimal, temporal, binary/hash, negative/large/equal values, and culture independence. | NOT YET |
| VC3 | Existing normalization output, ordering, and deterministic fingerprints are unchanged. | NOT YET |
| VC4 | Diagnostics formatting/writing is extracted into a cohesive internal component. | NOT YET |
| VC5 | Diagnostic schema and existing performance evidence fields remain unchanged. | NOT YET |
| VC6 | Partition scheduling, backpressure, workers, writer lifecycle, transactions, publication, and read routing are unchanged. | NOT YET |
| VC7 | Public Verification APIs remain unchanged; equivalent fixture fingerprints remain identical. | NOT YET |

## Architecture and semantic regression

| Gate | Required evidence | Status |
|---|---|---|
| AR1 | Existing pack conformance and concrete-pack dependency guards pass. | NOT YET |
| AR2 | Dependency-direction tests prove connector-internal implementation cannot leak into Domain/Core/Packs/Graph/Engine/Verification/Recovery. | NOT YET |
| AR3 | Explicit project references match direct type usage; Reporting → Verification remains direct. | NOT YET |
| AR4 | DemoRunner/DemoScenarios remain independent of test assemblies and frameworks; package excludes test runtime. | NOT YET |
| AR5 | Configuration/graph/checkpoint/Projection/Verification/Evidence/Recovery/report/compare fingerprints and semantics are unchanged. | NOT YET |
| AR6 | Pension, Utility, Justice, HCM, and FHIR exact seeded findings and qualification outcomes remain unchanged. | NOT YET |

## Build, test, and distribution regression

| Gate | Required evidence | Status |
|---|---|---|
| RT1 | Normal restore, locked restore, and full solution build pass with SDK 10.0.402. | NOT YET |
| RT2 | Focused connector, architecture, pack, CLI, Verification, Evidence, Recovery, report/compare tests pass. | NOT YET |
| RT3 | Full build-and-test and RemoteStorage jobs pass with 0 required failures/skips. | NOT YET |
| RT4 | Oracle and Db2 integration jobs pass with 0 failures/skips. | NOT YET |
| RT5 | Windows package-only smoke and all five Ubuntu packaged Docker demos pass. | NOT YET |
| RT6 | Independent Windows and Linux package A/B hashes and deterministic provenance match. | NOT YET |
| RT7 | Runtime-only package smoke passes with no SDK installed and supported runtime 10.0.13. | NOT YET |
| RT8 | Exact source candidate/tree and full required CI evidence are recorded; no PS-0.10D large benchmark was rerun. | NOT YET |

## Candidate and evidence log

| Evidence | Value |
|---|---|
| PS-0.11C accepted candidate / run / merge | `286ea550e559b360fd9c85c2a65e2b1394a95408` / `40178365888` / `5a1a3ec064aca98266cf0abb3c7749f7416da67e` |
| Starting main baseline / tree | `5a1a3ec064aca98266cf0abb3c7749f7416da67e` / `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` |
| PS-0.11D branch | `ps-0.11d-internal-consolidation` |
| Candidate source SHA / tree | NOT YET |
| Full exact-candidate CI run | NOT YET |
| Test totals and package evidence | NOT YET |
| Gates | **0 PASS, 0 FAIL, 35 NOT YET** |
| Formal status | **In Progress; not accepted** |
