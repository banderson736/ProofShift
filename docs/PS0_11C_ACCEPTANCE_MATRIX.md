# PS-0.11C Acceptance Matrix — Release Identity & Reproducible Distribution

## Status

**PS-0.11C — In Progress.** All gates begin **NOT YET**. Do not self-accept or merge before the normal review workflow.

| Baseline evidence | Value |
|---|---|
| Starting accepted `main` source baseline | `464ef14b235c93e08db58d575595a71eced0bafe` |
| Product identity | `ProofShift 0.1.0-preview.1` |
| Candidate source SHA / tree | NOT YET |
| Tested PR merge SHA / tree | NOT YET |
| Required CI run | NOT YET |
| Formal review / merge | NOT YET |

## Release identity

| Gate | Required evidence | Status |
|---|---|---|
| RI1 | One central product-version value drives CLI, assembly, package scripts, package names, manifests, sidecars, smoke checks, and docs. | NOT YET |
| RI2 | `--version` and `version` print `ProofShift 0.1.0-preview.1`; `version --json` reports product version, source revision/tree, and target framework. | NOT YET |
| RI3 | Version, AssemblyVersion, FileVersion, and InformationalVersion are centrally coherent. | NOT YET |
| RI4 | Archive names and root use product version plus platform; manifest and provenance agree. | NOT YET |
| RI5 | Active product-facing identity checks reject milestone names as the product version. | NOT YET |

## Build inputs

| Gate | Required evidence | Status |
|---|---|---|
| BI1 | Repository SDK pin is exact `10.0.402`, roll-forward disabled; all required CI jobs use that SDK. | NOT YET |
| BI2 | All build, test, CLI, DemoRunner, DemoScenarios, connector, pack, and evaluator projects have committed NuGet lock files. | NOT YET |
| BI3 | CI and package construction restore in locked mode; unchanged manifests restore without lock drift. | NOT YET |
| BI4 | Deterministic aggregate dependency-lock fingerprint is computed from sorted repository-relative lock paths and file hashes. | NOT YET |

## Provenance and package integrity

| Gate | Required evidence | Status |
|---|---|---|
| PR1 | Package manifest contains format, product version, platform, source revision/tree, lock hash, SDK, target framework, content hash, entry point, and scenario runner. | NOT YET |
| PR2 | Per-archive provenance sidecar contains required deterministic fields, archive SHA, content SHA, and package format. | NOT YET |
| PR3 | Reproducible metadata contains no timestamp, runner name, workspace/checkout path, random build ID, username, secret, or signature claim. | NOT YET |
| PR4 | `sourceWorktreeClean=true`; dirty release packaging fails or is unmistakably marked non-release. | NOT YET |
| PR5 | `global.json` is excluded from the evaluator package; manifest format remains `proofshift-evaluation-package-v2`. | NOT YET |
| PR6 | Third-party notices reflect only the shipped runtime graph; xUnit, test SDK, testhost, and MTP dependencies remain absent. | NOT YET |

## Independent reproducibility

| Gate | Required evidence | Status |
|---|---|---|
| RP1 | Two independent clean Windows runners build the same exact tree with SDK 10.0.402 and locked inputs. | NOT YET |
| RP2 | Windows archive SHA-256 and content SHA-256 are byte-identical across builds A/B. | NOT YET |
| RP3 | Two independent clean Ubuntu runners build the same exact tree with SDK 10.0.402 and locked inputs. | NOT YET |
| RP4 | Linux archive SHA-256 and content SHA-256 are byte-identical across builds A/B. | NOT YET |
| RP5 | Deterministic provenance fields compare equal for both independent platform build pairs. | NOT YET |

## Runtime and package acceptance

| Gate | Required evidence | Status |
|---|---|---|
| RT1 | Runtime-only Linux environment contains the compatible .NET 10 runtime and no SDK; exact runtime version is recorded. | NOT YET |
| RT2 | Extracted Linux package passes `--version`, `version --json`, `demo list --json`, and `demo describe pension --json` with runtime only. | NOT YET |
| RT3 | Fresh Windows package-only smoke passes outside checkout/path with spaces, without a Docker-demo claim. | NOT YET |
| RT4 | Fresh Ubuntu extracted package runs all five Docker-backed demos and reset/repeat equivalence. | NOT YET |
| RT5 | Package independence excludes test assemblies, xUnit/MTP/test SDK runtime, source, repository state, and secrets. | NOT YET |
| RT6 | Platform statements remain package-only on Windows and packaged Docker evaluation on Ubuntu, not general Linux support. | NOT YET |

## Assurance and operator regression

| Gate | Required evidence | Status |
|---|---|---|
| AS1 | Pension remains 149 defective discrepancies / 0 corrected, `NOT QUALIFIED` / `QUALIFIED`. | NOT YET |
| AS2 | Pension false-Reverse remains Verification `PASSED`, 0 failures, 2 unsafe Reverse declarations, 2 business discrepancies, `NOT QUALIFIED`. | NOT YET |
| AS3 | Utility remains 14 seeded / 26 findings / 0 corrected / `QUALIFIED`. | NOT YET |
| AS4 | Justice remains 15 seeded / 24 findings / 0 corrected / `QUALIFIED`. | NOT YET |
| AS5 | HCM remains 14 seeded / 25 findings / 0 corrected / `QUALIFIED`. | NOT YET |
| AS6 | FHIR remains 9 seeded / 15 findings / 0 corrected / `QUALIFIED`. | NOT YET |
| AS7 | Generic report, compare, evidence, and recovery behavior/persisted semantics remain unchanged. | NOT YET |

## Process and documentation

| Gate | Required evidence | Status |
|---|---|---|
| PD1 | Exact source candidate and source tree are recorded; synthetic merge SHA/tree are recorded if CI tests a PR merge. | NOT YET |
| PD2 | All six normal required CI jobs and new runtime/reproducibility jobs pass; no required failures or unexplained skips. | NOT YET |
| PD3 | README, roadmap, agent instructions, evaluation/runbook, architecture, decisions, release docs, and proposed ADR agree. | NOT YET |
| PD4 | No public release, tag, signing, self-contained distribution, installer, PS-0.11D, or out-of-scope work was introduced. | NOT YET |

## Final evidence log

| Evidence | Value |
|---|---|
| Source candidate / tree | NOT YET |
| Tested merge / tree | NOT YET |
| SDK / runtime | NOT YET |
| Dependency lock SHA-256 | NOT YET |
| Windows build A/B archive + content hashes | NOT YET |
| Linux build A/B archive + content hashes | NOT YET |
| Runtime-only commands and evidence | NOT YET |
| Required test totals | NOT YET |
| Gates | **0 PASS, 0 FAIL, 37 NOT YET** |
| Formal status | **In Progress; not accepted** |
