# PS-0.11F Acceptance Matrix — Bring-Your-Own-Migration Evaluation

## Status

**In Progress. Not self-accepted.** Gates are marked `PASS` only where focused
or exact-candidate evidence supports them. Remaining report-integrity,
comparison, dry-run regression, governance-state, and ADR gates are `NOT YET`.
Final acceptance evidence must identify the exact candidate, tree, workflow
run, and package identity where applicable.

| Gate | Required evidence | Status |
|---|---|---|
| E1 | PS-0.11E acceptance matrix records formal acceptance. | PASS |
| E2 | ADR-0034 is accepted with final governed-main evidence. | PASS |
| E3 | Governed-main SHA/tree and CI run `40244724080` are recorded. | PASS |
| CLI1 | `verify-external` accepts the required config, checkpoint, observation label, and JSON options. | PASS |
| CLI2 | Command reuses `VerifyExternalTargetAsync` and normal config/graph/rule/checkpoint binding. | PASS |
| CLI3 | Target observers resolve through generic `ConnectorCatalog.Observer`; unsupported capability fails closed. | NOT YET |
| CLI4 | External Verification requires role `target` and never invokes a shadow writer or target mutation. | PASS |
| CLI5 | Observation ID is validated, persisted, and distinct from generated observation-run and Verification-run IDs. | PASS |
| CLI6 | Passed/failed outcomes, exit codes, safe error paths, human boundary, and required JSON fields are verified. | PASS |
| CLI7 | Failed completed Verification persists Evidence and the finalized ledger. | PASS |
| CLI8 | No Projection manifest, journal, or shadow Projection run is created. | PASS |
| CLI9 | No Recovery assessment/rehearsal/Evidence or qualification is created or claimed. | PASS |
| AUTH1 | Scaffold/import accept bounded target-role options; default remains `shadow-target`. | PASS |
| AUTH2 | Generic baseline is opt-in and deterministically generates only rules supported by reviewed mappings. | PASS |
| AUTH3 | Suggestions remain unapproved and strict validation rejects unresolved required mappings. | PASS |
| AUTH4 | Existing authoring output remains compatible when new options are omitted. | PASS |
| REP1 | Explicit `report --verification-run` reads persisted data and never reruns Verification. | PASS |
| REP2 | Verification-only report verifies Evidence, ledger, checkpoint, and project/config context integrity. | NOT YET |
| REP3 | Generic no-pack project works; report contains required fields and no sensitive expected/actual values. | PASS |
| REP4 | Report explicitly excludes Recovery assessment and qualification. | PASS |
| REP5 | Explicit verification-run comparison uses persisted state, rejects cross-project runs, and reports findings/fingerprint deltas. | NOT YET |
| REP6 | Existing dry-run `--run`, `--before`, and `--after` meanings are unchanged. | NOT YET |
| BYOM1 | Public-works work-order fixture is synthetic, deterministic, generic, and not a built-in demo or domain pack. | PASS |
| BYOM2 | Source/target counts, mapped fields/relationships/history, and fixture expectations are frozen and exact. | PASS |
| BYOM3 | Four seeded defect categories have exact stable findings and an evidence-derived fixed total. | PASS |
| BYOM4 | Corrected target passes with zero Verification failures, accounting gaps, and unexplained targets. | PASS |
| BYOM5 | Corrected repeat has new run identities and stable target/Evidence fingerprints. | PASS |
| BYOM6 | Defective-to-corrected comparison resolves seeded findings without introducing findings. | PASS |
| IND1 | Extracted Linux package runs outside the source checkout and without test/source/build artifacts. | PASS |
| IND2 | Source is unavailable after checkpoint and external Verification succeeds without live-source fallback. | PASS |
| IND3 | ProofShift uses only a read-only target account; DML/DDL is denied and target state is unchanged. | PASS |
| IND4 | No Projection or Recovery artifacts exist for external Verification runs. | PASS |
| IND5 | Vendor fixture is run separately and is never invoked by ProofShift. | PASS |
| PKG1 | Evaluation package includes the BYOM sample and documentation without source, tests, credentials, or governance tools. | PASS |
| PKG2 | Windows package-only smoke checks BYOM contents, CLI usage, and package denylist. | PASS |
| PKG3 | Existing required `linux-package-demos` runs all five demos and the packaged BYOM flow. | PASS |
| PKG4 | Independent Windows/Linux A/B package archive/content/provenance values match. | PASS |
| PKG5 | Runtime-only .NET 10.0.13 validation passes without an SDK. | PASS |
| REG1 | Five existing domain demo outcomes remain exact and unchanged. | PASS |
| REG2 | Existing dry-run/report/compare behavior and accepted fingerprints remain unchanged. | NOT YET |
| REG3 | Focused CLI, authoring, Verification, Evidence, Reporting, conformance, and BYOM tests pass. | PASS |
| REG4 | Required RemoteStorage, Oracle, and Db2 integration jobs pass without required skips. | PASS |
| GOV1 | Strict protected-main workflow and all eleven existing required check names remain active. | NOT YET |
| GOV2 | Exact source-candidate push CI and PR-context evidence pass; candidate is not merged. | PASS |
| ADR1 | ADR-0035 documents the boundary and remains Proposed under PS-0.11F. | NOT YET |

## Evidence log

| Evidence | Value |
|---|---|
| PS-0.11E final governed-main SHA / tree | `2d5744753309643d7451123d59bf44b338472d4b` / `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43` |
| PS-0.11E final CI | `40244724080`, all eleven required jobs passed; 359 passed, 0 failed, 0 skipped |
| PS-0.11E closure commit | `96f4c62e7d081f44e82d40f6a44f5fa6f324527a` |
| PS-0.11F starting branch | `ps-0.11f-bring-your-own-migration-evaluation` |
| PS-0.11F starting main | `2d5744753309643d7451123d59bf44b338472d4b` |
| PS-0.11F initial implementation commit / tree | `277ca238233bdb091f68218574d0074e1c887578` / `aeb67bc9e34c13cd8d70abc737e88c3b03186a3c` |
| PS-0.11F source candidate / tree | `0225232ec2793c93154ab2ff44c3079998b72e92` / `c4e5be496a6dac626474cf5833ef9520e55ccf5c` |
| PS-0.11F candidate branch push | `origin/ps-0.11f-bring-your-own-migration-evaluation` — verified |
| PS-0.11F PR | #16 is open against `main`, head `0225232ec2793c93154ab2ff44c3079998b72e92`; it is not merged. |
| PS-0.11F push CI / PR CI | Runs `40267163458` (push) and `40267165819` (PR) passed all eleven required jobs on source candidate `0225232ec2793c93154ab2ff44c3079998b72e92`. |
| Packaged BYOM physical evaluation | `linux-package-demos` passed in both runs. Verified four seeded defects, 6 Evidence findings (2 `MissingTarget`, 2 `AttributeMismatch`, 2 `UnaccountedArtifact`) across 3 failed rules; corrected and repeat observations, read-only controls, and comparison passed. |
| Package and regression checks | Windows package smoke, Linux A/B reproducibility, runtime-only smoke, package reproducibility, and all five existing packaged demos passed in both runs. |
| Required integration checks | `build-and-test`, `remote-storage-integration`, `oracle-integration`, `db2-integration`, and `dependency-review` passed in both runs. |
| Local focused CLI tests | `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.ExternalVerificationCliTests` — 1 passed, 0 failed, 0 skipped; file target remained unchanged, source was unavailable after checkpoint, transient scratch was removable, and persisted Evidence/report remained readable. |
| Local focused authoring tests | `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests` — 22 passed, 0 failed, 0 skipped. |
| Local BYOM fixture contract test | `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.ByomFixtureContractTests` — 1 passed, 0 failed, 0 skipped. |
| Local solution build | `dotnet build ProofShift.sln --no-restore` — succeeded, 0 warnings, 0 errors. |
| BYOM PowerShell syntax / whitespace | Parser checks for `scripts\run-byom-evaluation.ps1` and `scripts\smoke-evaluation-package.ps1`, plus `git diff --check` — passed. |
| PS-0.11F candidate / tree / push CI / PR CI | Source candidate `0225232ec2793c93154ab2ff44c3079998b72e92`, tree `c4e5be496a6dac626474cf5833ef9520e55ccf5c`; push run `40267163458` and PR run `40267165819` passed. |
| Acceptance totals | **38 PASS, 0 FAIL, 7 NOT YET** |
| Formal status | **In Progress; not self-accepted.** |
