# PS-0.11C Acceptance Matrix — Release Identity & Reproducible Distribution

## Status

**PS-0.11C — ACCEPTED — October 25, 2026.** The exact source candidate passed all required CI and distribution gates and was merged to `main`. Product identity remains `ProofShift 0.1.0-preview.1`; it is independent of the PS-0.11C milestone.

| Baseline evidence | Value |
|---|---|
| Starting accepted `main` source baseline | `464ef14b235c93e08db58d575595a71eced0bafe` |
| Product identity | `ProofShift 0.1.0-preview.1` |
| Candidate source SHA / tree | `286ea550e559b360fd9c85c2a65e2b1394a95408` / `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` |
| Exact-candidate CI run | [40178365888](https://github.com/banderson736/ProofShift/actions/runs/40178365888) — success |
| Merged `main` SHA / tree | `5a1a3ec064aca98266cf0abb3c7749f7416da67e` / `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` |
| Formal review / merge | PR #7 merged October 25, 2026 |

## Release identity

| Gate | Required evidence | Status |
|---|---|---|
| RI1 | One central product-version value drives CLI, assembly, package scripts, package names, manifests, sidecars, smoke checks, and docs. | PASS |
| RI2 | `--version` and `version` print `ProofShift 0.1.0-preview.1`; `version --json` reports product version, source revision/tree, and target framework. | PASS |
| RI3 | Version, AssemblyVersion, FileVersion, and InformationalVersion are centrally coherent. | PASS |
| RI4 | Archive names and root use product version plus platform; manifest and provenance agree. | PASS |
| RI5 | Active product-facing identity checks reject milestone names as the product version. | PASS |

## Build inputs

| Gate | Required evidence | Status |
|---|---|---|
| BI1 | Repository SDK pin is exact `10.0.402`, roll-forward disabled; all required CI jobs use that SDK. | PASS |
| BI2 | All build, test, CLI, DemoRunner, DemoScenarios, connector, pack, and evaluator projects have committed NuGet lock files; OS-conditional graphs have separate Windows/Linux locks. | PASS |
| BI3 | CI and package construction restore in locked mode; unchanged manifests restore without lock drift. | PASS |
| BI4 | Deterministic aggregate dependency-lock fingerprint is computed from sorted repository-relative lock paths and file hashes. | PASS |

## Provenance and package integrity

| Gate | Required evidence | Status |
|---|---|---|
| PR1 | Package manifest contains format, product version, platform, source revision/tree, lock hash, SDK, target framework, content hash, entry point, and scenario runner. | PASS |
| PR2 | Per-archive provenance sidecar contains required deterministic fields, archive SHA, content SHA, and package format. | PASS |
| PR3 | Reproducible metadata contains no timestamp, runner name, workspace/checkout path, random build ID, username, secret, or signature claim. | PASS |
| PR4 | `sourceWorktreeClean=true`; dirty release packaging fails or is unmistakably marked non-release. | PASS |
| PR5 | `global.json` is excluded from the evaluator package; manifest format remains `proofshift-evaluation-package-v2`. | PASS |
| PR6 | Third-party notices reflect only the shipped runtime graph; xUnit, test SDK, testhost, and MTP dependencies remain absent. | PASS |

## Independent reproducibility

| Gate | Required evidence | Status |
|---|---|---|
| RP1 | Two independent clean Windows runners build the same exact tree with SDK 10.0.402 and locked inputs. | PASS |
| RP2 | Windows archive SHA-256 and content SHA-256 are byte-identical across builds A/B. | PASS |
| RP3 | Two independent clean Ubuntu runners build the same exact tree with SDK 10.0.402 and locked inputs. | PASS |
| RP4 | Linux archive SHA-256 and content SHA-256 are byte-identical across builds A/B. | PASS |
| RP5 | Deterministic provenance fields compare equal for both independent platform build pairs. | PASS |

## Runtime and package acceptance

| Gate | Required evidence | Status |
|---|---|---|
| RT1 | Runtime-only Linux environment contains the compatible .NET 10 runtime and no SDK; exact runtime version is recorded. | PASS |
| RT2 | Extracted Linux package passes `--version`, `version --json`, `demo list --json`, and `demo describe pension --json` with runtime only. | PASS |
| RT3 | Fresh Windows package-only smoke passes outside checkout/path with spaces, without a Docker-demo claim. | PASS |
| RT4 | Fresh Ubuntu extracted package runs all five Docker-backed demos and reset/repeat equivalence. | PASS |
| RT5 | Package independence excludes test assemblies, xUnit/MTP/test SDK runtime, source, repository state, and secrets. | PASS |
| RT6 | Platform statements remain package-only on Windows and packaged Docker evaluation on Ubuntu, not general Linux support. | PASS |

## Assurance and operator regression

| Gate | Required evidence | Status |
|---|---|---|
| AS1 | Pension remains 149 defective discrepancies / 0 corrected, `NOT QUALIFIED` / `QUALIFIED`. | PASS |
| AS2 | Pension false-Reverse remains Verification `PASSED`, 0 failures, 2 unsafe Reverse declarations, 2 business discrepancies, `NOT QUALIFIED`. | PASS |
| AS3 | Utility remains 14 seeded / 26 findings / 0 corrected / `QUALIFIED`. | PASS |
| AS4 | Justice remains 15 seeded / 24 findings / 0 corrected / `QUALIFIED`. | PASS |
| AS5 | HCM remains 14 seeded / 25 findings / 0 corrected / `QUALIFIED`. | PASS |
| AS6 | FHIR remains 9 seeded / 15 findings / 0 corrected / `QUALIFIED`. | PASS |
| AS7 | Generic report, compare, evidence, and recovery behavior/persisted semantics remain unchanged. | PASS |

## Process and documentation

| Gate | Required evidence | Status |
|---|---|---|
| PD1 | Exact source candidate and source tree are recorded; any distinct tested merge identity is recorded when applicable. | PASS |
| PD2 | All required CI, runtime, and reproducibility jobs pass; no required failures or unexplained skips. | PASS |
| PD3 | README, roadmap, agent instructions, evaluation/runbook, architecture, decisions, release docs, and ADR agree. | PASS |
| PD4 | No public release, tag, signing, self-contained distribution, installer, or out-of-scope work was introduced. | PASS |

## Final evidence log

| Evidence | Value |
|---|---|
| Source candidate / tree | `286ea550e559b360fd9c85c2a65e2b1394a95408` / `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` |
| Merged `main` / tree | `5a1a3ec064aca98266cf0abb3c7749f7416da67e` / `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` |
| CI run | [40178365888](https://github.com/banderson736/ProofShift/actions/runs/40178365888), success |
| SDK / runtime | SDK `10.0.402`; runtime-only `Microsoft.NETCore.App 10.0.13`; no SDK installed in runtime-only environment |
| Windows dependency-lock SHA-256 | `fed2623628b6ba085aa807c53cb4b4e4b33c0a341c4a73cb4db7bf3c411de091` |
| Linux dependency-lock SHA-256 | `4b8b1666d361882d528d5f30203cb1eac67c94d889cad329d288649df05eccf7` |
| Windows build A/B archive SHA-256 | `c76e019c4e2192323a13c646808ec8904ebcf11f832502c62af2b53762c2a9af` / identical |
| Windows build A/B content SHA-256 | `8c65bdd6fe55c8a4bbca9d9f3508b01712c58c054226f9876c5fca5cbed4d18d` / identical |
| Linux build A/B archive SHA-256 | `fc7fcf190ab1a46f761bb1382d4869b41b6484b726baf1644dd366ce5ac48b99` / identical |
| Linux build A/B content SHA-256 | `5e5835ec3a690ea41f27f6e2d92b53d0d811991b2cc0f6e7da6b0225f94976c1` / identical |
| Test totals | build-and-test 293; RemoteStorage 53; Oracle 2; Db2 2; **350 passed, 0 failed, 0 skipped** |
| Runtime-only evaluator checks | `--version`, `version --json`, `demo list --json`, `demo describe pension --json`: PASS |
| Package acceptance | Windows package-only smoke PASS; Ubuntu packaged Docker demos 5/5 PASS; independent Windows/Linux A/B reproducibility PASS |
| Gates | **37 PASS, 0 FAIL, 0 NOT YET** |
| Formal status | **Accepted and merged to `main` as `5a1a3ec064aca98266cf0abb3c7749f7416da67e`** |
