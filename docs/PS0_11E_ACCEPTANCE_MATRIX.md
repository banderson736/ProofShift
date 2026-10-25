# PS-0.11E Acceptance Matrix — Repository & Product Governance

## Status

**In Progress; not accepted.** Starting `main`:
`34056aaf7342e1426bda66b0d69ba6ae763c7938`, tree
`077aaba4a5c0d9fe69e12627c18fe06557510b20`.

Statuses are evidence-driven: `PASS`, `FAIL`, or `NOT YET`.

## Main branch protection

| Gate | Required evidence | Status |
|---|---|---|
| RP1 | Active repository ruleset targets `refs/heads/main`. | NOT YET |
| RP2 | Pull requests required; 0 approving reviews and no CODEOWNER approval. | NOT YET |
| RP3 | All required acceptance checks use exact successful workflow check names. | NOT YET |
| RP4 | Strict status-check policy requires the branch to be current with `main`. | NOT YET |
| RP5 | Force-push and branch deletion protections are active. | NOT YET |
| RP6 | Any bypass is absent or restricted to repository administrators for emergencies only. | NOT YET |
| RP7 | Solo-maintainer PR workflow is usable without external review requirements. | NOT YET |
| RP8 | Actual ruleset and merge settings are read back from GitHub after configuration. | NOT YET |

## Merge and lineage

| Gate | Required evidence | Status |
|---|---|---|
| ML1 | Merge commits enabled; squash and rebase disabled. | NOT YET |
| ML2 | Automatic source-branch deletion after merge enabled. | NOT YET |
| ML3 | Accepted candidate SHA/tree and merge lineage remain auditable. | NOT YET |

## Ownership and contribution process

| Gate | Required evidence | Status |
|---|---|---|
| OC1 | CODEOWNERS assigns `* @banderson736`; no required owner approval. | NOT YET |
| OC2 | CONTRIBUTING documents scoped branch, locked restore, explicit staging, and no self-acceptance. | PASS |
| OC3 | PR template covers scope, behavior, formats, dependencies, packages, validation, candidate SHA/tree, CI, docs, and non-goals. | PASS |
| OC4 | Governance policy accurately describes expected remote state and emergency bypass. | PASS |

## Security and dependencies

| Gate | Required evidence | Status |
|---|---|---|
| SD1 | Security policy prohibits public vulnerability details and real sensitive data. | PASS |
| SD2 | Private vulnerability reporting is enabled if supported, or accurately recorded as blocked/disabled. | PASS |
| SD3 | Secret scanning and push-protection state is verified; no unsupported claim is made. | NOT YET |
| SD4 | All third-party GitHub Actions are pinned to official verified full commit SHAs. | PASS |
| SD5 | Workflow permissions are least privilege; no unsafe privileged triggers or secrets to untrusted PR code. | PASS |
| SD6 | PR dependency review fails on high-severity dependency introductions without license-policy conclusions. | NOT YET |
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
| RG2 | build-and-test passes with 0 failures and 0 required skips. | NOT YET |
| RG3 | remote-storage-integration, oracle-integration, and db2-integration pass. | NOT YET |
| RG4 | windows-package-reproducibility-b and linux-package-reproducibility-b pass. | NOT YET |
| RG5 | windows-package-smoke and linux-package-demos pass, including all five synthetic domains. | NOT YET |
| RG6 | package-reproducibility and linux-runtime-only-smoke pass. | NOT YET |
| RG7 | Dependency review is successful on the PR context. | NOT YET |
| RG8 | Pension, false-Reverse, Utility, Justice, HCM, and FHIR exact accepted outcomes remain unchanged. | NOT YET |

## Candidate and evidence log

| Evidence | Value |
|---|---|
| PS-0.11D candidate / tree | `6a23233670e5be04d22cdf7b6304fd8090ca493a` / `077aaba4a5c0d9fe69e12627c18fe06557510b20` |
| PS-0.11D push CI / PR CI / merge | `40185263041` / `40185376379` / `34056aaf7342e1426bda66b0d69ba6ae763c7938` |
| PS-0.11E branch | `ps-0.11e-repository-product-governance` |
| Pre-change remote readback | public; default `main`; no repository rulesets; private vulnerability reporting disabled; administrator-only settings not readable in this environment |
| Verified workflow actions | checkout `v7.0.1`; setup-dotnet `v6.0.0`; upload-artifact `v7.0.2`; download-artifact `v8.0.2`; dependency-review `v5.0.0`, each pinned to verified upstream commit |
| Candidate SHA / tree | NOT YET |
| Push CI / PR CI | NOT YET |
| Remote settings readback | NOT YET |
| Gates | **16 PASS, 0 FAIL, 21 NOT YET** |
| Formal status | **In Progress; not accepted** |
