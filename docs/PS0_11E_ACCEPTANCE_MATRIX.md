# PS-0.11E Acceptance Matrix — Repository & Product Governance

## Status

**Accepted (October 25, 2026).** Candidate `fc2411b2bd19e713af097e134388421db9d16fae`,
tree `3d36bc4c054709bcfa4b996603cb4d43adeb1a60`, merged to `main` as
`97dee40ff0db732fc83e035e276c10a5747801f0` with the same tree.

Statuses are evidence-driven: `PASS`, `FAIL`, or `NOT YET`.

## Main branch protection

| Gate | Required evidence | Status |
|---|---|---|
| RP1 | Active repository ruleset targets `refs/heads/main`. | PASS |
| RP2 | Pull requests required; 0 approving reviews and no CODEOWNER approval. | PASS |
| RP3 | All required acceptance checks use exact successful workflow check names. | PASS |
| RP4 | Strict status-check policy requires the branch to be current with `main`. | PASS |
| RP5 | Force-push and branch deletion protections are active. | PASS |
| RP6 | Any bypass is absent or restricted to repository administrators for emergencies only. | PASS |
| RP7 | Solo-maintainer PR workflow is usable without external review requirements. | PASS |
| RP8 | Actual ruleset and merge settings are read back from GitHub after configuration. | PASS |

## Merge and lineage

| Gate | Required evidence | Status |
|---|---|---|
| ML1 | Merge commits enabled; squash and rebase disabled. | PASS |
| ML2 | Automatic source-branch deletion after merge enabled. | PASS |
| ML3 | Accepted candidate SHA/tree and merge lineage remain auditable. | PASS |

## Ownership and contribution process

| Gate | Required evidence | Status |
|---|---|---|
| OC1 | CODEOWNERS assigns `* @banderson736`; no required owner approval. | PASS |
| OC2 | CONTRIBUTING documents scoped branch, locked restore, explicit staging, and no self-acceptance. | PASS |
| OC3 | PR template covers scope, behavior, formats, dependencies, packages, validation, candidate SHA/tree, CI, docs, and non-goals. | PASS |
| OC4 | Governance policy accurately describes expected remote state and emergency bypass. | PASS |

## Security and dependencies

| Gate | Required evidence | Status |
|---|---|---|
| SD1 | Security policy prohibits public vulnerability details and real sensitive data. | PASS |
| SD2 | Private vulnerability reporting is enabled if supported, or accurately recorded as blocked/disabled. | PASS |
| SD3 | Secret scanning and push-protection state is verified; no unsupported claim is made. | PASS |
| SD4 | All third-party GitHub Actions are pinned to official verified full commit SHAs. | PASS |
| SD5 | Workflow permissions are least privilege; no unsafe privileged triggers or secrets to untrusted PR code. | PASS |
| SD6 | PR dependency review fails on high-severity dependency introductions without license-policy conclusions. | PASS |
| SD7 | Weekly Dependabot covers NuGet and GitHub Actions with no automatic merge. | PASS |
| SD8 | Dependency policy requires appropriate lockfiles, locked restore, full relevant CI, and review. | PASS |
| SD9 | Read-only governance verifier distinguishes permission failures from policy mismatches and prints no tokens. | PASS |

## Legal, IP, and data

| Gate | Required evidence | Status |
|---|---|---|
| LI1 | LICENSE states proprietary/all-rights-reserved terms without granting an OSS license. | PASS |
| LI2 | README distinguishes public visibility from open-source licensing. | PASS |
| LI3 | Public visibility, proprietary license, and no-public-release status are documented accurately. | PASS |
| LI4 | No repository visibility change occurs without explicit owner authorization. | PASS |
| LI5 | Synthetic-only, no-secrets, PII/PHI, confidential-schema, and employer/client IP rules are explicit. | PASS |

## Package and product regression

| Gate | Required evidence | Status |
|---|---|---|
| RG1 | Governance files and admin tools stay outside the evaluator package (except the existing LICENSE); package allowlist and product version are unchanged. | PASS |
| RG2 | build-and-test passes with 0 failures and 0 required skips. | PASS |
| RG3 | remote-storage-integration, oracle-integration, and db2-integration pass. | PASS |
| RG4 | windows-package-reproducibility-b and linux-package-reproducibility-b pass. | PASS |
| RG5 | windows-package-smoke and linux-package-demos pass, including all five synthetic domains. | PASS |
| RG6 | package-reproducibility and linux-runtime-only-smoke pass. | PASS |
| RG7 | Dependency review is successful on the PR context. | PASS |
| RG8 | Pension, false-Reverse, Utility, Justice, HCM, and FHIR exact accepted outcomes remain unchanged. | PASS |

## Candidate and evidence log

| Evidence | Value |
|---|---|
| PS-0.11D candidate / tree | `6a23233670e5be04d22cdf7b6304fd8090ca493a` / `077aaba4a5c0d9fe69e12627c18fe06557510b20` |
| PS-0.11D push CI / PR CI / merge | `40185263041` / `40185376379` / `34056aaf7342e1426bda66b0d69ba6ae763c7938` |
| PS-0.11E branch | `ps-0.11e-repository-product-governance` |
| Initial candidate push CI | `40188182133` failed at dependency review while the dependency graph was disabled; after it was enabled, exact-candidate PR CI passed |
| Verified workflow actions | checkout `v7.0.1`; setup-dotnet `v6.0.0`; upload-artifact `v7.0.2`; download-artifact `v8.0.2`; dependency-review `v5.0.0`, each pinned to verified upstream commit |
| Candidate SHA / tree | `fc2411b2bd19e713af097e134388421db9d16fae` / `3d36bc4c054709bcfa4b996603cb4d43adeb1a60` |
| Candidate push CI / PR CI | `40188182133` (dependency graph disabled; initial run failed) / `40188407180` (all 11 jobs PASS) |
| Main merge / tree / push CI | `97dee40ff0db732fc83e035e276c10a5747801f0` / `3d36bc4c054709bcfa4b996603cb4d43adeb1a60` / `40225632710` (all 11 jobs PASS) |
| Final governed-main baseline / tree / CI | `2d5744753309643d7451123d59bf44b338472d4b` / `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43` / `40244724080` (all 11 required jobs PASS) |
| Final governed-main test results | `build-and-test`: 301 passed; `remote-storage-integration`: 54 passed; Oracle: 2 passed; Db2: 2 passed; total 359 passed, 0 failed, 0 skipped |
| GitHub remote readback | public; default `main`; active `main-protection` targeting `refs/heads/main`; PR required, 0 approvals, CODEOWNER review false; exact 11 checks required, strict=true; deletion and non-fast-forward blocked; no bypass actors |
| Required check contexts | `build-and-test`, `remote-storage-integration`, `oracle-integration`, `db2-integration`, `dependency-review`, `windows-package-reproducibility-b`, `linux-package-reproducibility-b`, `windows-package-smoke`, `linux-package-demos`, `package-reproducibility`, `linux-runtime-only-smoke` |
| Merge and security readback | merge commit=true; squash=false; rebase=false; delete branch on merge=true; private vulnerability reporting=true; secret scanning=enabled; push protection=enabled |
| Read-only verifier | All 19 reported checks PASS against authenticated GitHub API readback |
| Gates | **37 PASS, 0 FAIL, 0 NOT YET** |
| Formal status | **Accepted (October 25, 2026), based on the verified governed-main state and final CI run `40244724080`.** |
