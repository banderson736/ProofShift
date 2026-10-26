# PS-0.11F Acceptance Matrix — Bring-Your-Own-Migration Evaluation

## Status

**In Progress. Not self-accepted.** All PS-0.11F implementation and
distribution gates begin `NOT YET`. Evidence must identify exact candidate,
tree, workflow run, and package identity where applicable.

| Gate | Required evidence | Status |
|---|---|---|
| E1 | PS-0.11E acceptance matrix records formal acceptance. | PASS |
| E2 | ADR-0034 is accepted with final governed-main evidence. | PASS |
| E3 | Governed-main SHA/tree and CI run `40244724080` are recorded. | PASS |
| CLI1 | `verify-external` accepts the required config, checkpoint, observation label, and JSON options. | NOT YET |
| CLI2 | Command reuses `VerifyExternalTargetAsync` and normal config/graph/rule/checkpoint binding. | NOT YET |
| CLI3 | Target observers resolve through generic `ConnectorCatalog.Observer`; unsupported capability fails closed. | NOT YET |
| CLI4 | External Verification requires role `target` and never invokes a shadow writer or target mutation. | NOT YET |
| CLI5 | Observation ID is validated, persisted, and distinct from generated observation-run and Verification-run IDs. | NOT YET |
| CLI6 | Passed/failed outcomes, exit codes, safe error paths, human boundary, and required JSON fields are verified. | NOT YET |
| CLI7 | Failed completed Verification persists Evidence and the finalized ledger. | NOT YET |
| CLI8 | No Projection manifest, journal, or shadow Projection run is created. | NOT YET |
| CLI9 | No Recovery assessment/rehearsal/Evidence or qualification is created or claimed. | NOT YET |
| AUTH1 | Scaffold/import accept bounded target-role options; default remains `shadow-target`. | NOT YET |
| AUTH2 | Generic baseline is opt-in and deterministically generates only rules supported by reviewed mappings. | NOT YET |
| AUTH3 | Suggestions remain unapproved and strict validation rejects unresolved required mappings. | NOT YET |
| AUTH4 | Existing authoring output remains compatible when new options are omitted. | NOT YET |
| REP1 | Explicit `report --verification-run` reads persisted data and never reruns Verification. | NOT YET |
| REP2 | Verification-only report verifies Evidence, ledger, checkpoint, and project/config context integrity. | NOT YET |
| REP3 | Generic no-pack project works; report contains required fields and no sensitive expected/actual values. | NOT YET |
| REP4 | Report explicitly excludes Recovery assessment and qualification. | NOT YET |
| REP5 | Explicit verification-run comparison uses persisted state, rejects cross-project runs, and reports findings/fingerprint deltas. | NOT YET |
| REP6 | Existing dry-run `--run`, `--before`, and `--after` meanings are unchanged. | NOT YET |
| BYOM1 | Public-works work-order fixture is synthetic, deterministic, generic, and not a built-in demo or domain pack. | NOT YET |
| BYOM2 | Source/target counts, mapped fields/relationships/history, and fixture expectations are frozen and exact. | NOT YET |
| BYOM3 | Four seeded defect categories have exact stable findings and an evidence-derived fixed total. | NOT YET |
| BYOM4 | Corrected target passes with zero Verification failures, accounting gaps, and unexplained targets. | NOT YET |
| BYOM5 | Corrected repeat has new run identities and stable target/Evidence fingerprints. | NOT YET |
| BYOM6 | Defective-to-corrected comparison resolves seeded findings without introducing findings. | NOT YET |
| IND1 | Extracted Linux package runs outside the source checkout and without test/source/build artifacts. | NOT YET |
| IND2 | Source is unavailable after checkpoint and external Verification succeeds without live-source fallback. | NOT YET |
| IND3 | ProofShift uses only a read-only target account; DML/DDL is denied and target state is unchanged. | NOT YET |
| IND4 | No Projection or Recovery artifacts exist for external Verification runs. | NOT YET |
| IND5 | Vendor fixture is run separately and is never invoked by ProofShift. | NOT YET |
| PKG1 | Evaluation package includes the BYOM sample and documentation without source, tests, credentials, or governance tools. | NOT YET |
| PKG2 | Windows package-only smoke checks BYOM contents, CLI usage, and package denylist. | NOT YET |
| PKG3 | Existing required `linux-package-demos` runs all five demos and the packaged BYOM flow. | NOT YET |
| PKG4 | Independent Windows/Linux A/B package archive/content/provenance values match. | NOT YET |
| PKG5 | Runtime-only .NET 10.0.13 validation passes without an SDK. | NOT YET |
| REG1 | Five existing domain demo outcomes remain exact and unchanged. | NOT YET |
| REG2 | Existing dry-run/report/compare behavior and accepted fingerprints remain unchanged. | NOT YET |
| REG3 | Focused CLI, authoring, Verification, Evidence, Reporting, conformance, and BYOM tests pass. | NOT YET |
| REG4 | Required RemoteStorage, Oracle, and Db2 integration jobs pass without required skips. | NOT YET |
| GOV1 | Strict protected-main workflow and all eleven existing required check names remain active. | NOT YET |
| GOV2 | Exact source-candidate push CI and PR-context evidence pass; candidate is not merged. | NOT YET |
| ADR1 | ADR-0035 documents the boundary and remains Proposed under PS-0.11F. | NOT YET |

## Evidence log

| Evidence | Value |
|---|---|
| PS-0.11E final governed-main SHA / tree | `2d5744753309643d7451123d59bf44b338472d4b` / `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43` |
| PS-0.11E final CI | `40244724080`, all eleven required jobs passed; 359 passed, 0 failed, 0 skipped |
| PS-0.11E closure commit | `96f4c62e7d081f44e82d40f6a44f5fa6f324527a` |
| PS-0.11F starting branch | `ps-0.11f-bring-your-own-migration-evaluation` |
| PS-0.11F starting main | `2d5744753309643d7451123d59bf44b338472d4b` |
| PS-0.11F candidate / tree / push CI / PR CI | NOT YET |
| Acceptance totals | **3 PASS, 0 FAIL, 42 NOT YET** |
| Formal status | **In Progress; not self-accepted.** |
