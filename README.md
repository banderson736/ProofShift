The stop-boundary wording in the historical A/B acceptance notes below predates the accepted PS-0.10B milestone and the current explicit PS-0.10C assignment. Production migration execution and rollback remain deferred.
# ProofShift

**Migration assurance and evidence platform for high-risk system modernization.**

ProofShift is designed to prove that one representation of a system became another without unexplained loss, corruption, semantic change, or unrecoverable risk.

It is intentionally **not** a generic database diff tool and is **not initially an ETL replacement**. The verification core must be able to independently validate migrations performed by external tools, vendors, scripts, or ProofShift's optional future orchestration layer.

## Product thesis

Large regulated and government modernization programs commonly need to prove:

- every source artifact is accounted for;
- every target artifact has provenance;
- historical state survived the migration semantically even when schema structures changed;
- financial and operational invariants still hold;
- relationships were not broken or reassigned;
- documents and audit history were preserved or explicitly archived/excluded;
- repeated dry runs produce deterministic, explainable results;
- destructive transformations have a known recovery path;
- a migration can be accepted or rejected based on evidence rather than ad hoc SQL scripts and spreadsheets.

ProofShift models that lifecycle as:

```text
DISCOVER
   ↓
SNAPSHOT
   ↓
PLAN
   ↓
PROJECT / DRY RUN
   ↓
VERIFY
   ↓
APPROVE
   ↓
APPLY / OBSERVE EXTERNAL MIGRATION
   ↓
POST-VERIFY
   ↓
COMMIT
   ↘
   ROLLBACK
```

PS-0 focuses on the pre-production assurance portion through approval and recovery-readiness analysis.

## Initial vertical

The first domain pack is **public pension / retirement-system modernization**. This is a beachhead, not a hard-coded product boundary.

The long-term platform model is:

```text
                         ProofShift Core
                              │
             ┌────────────────┼────────────────┐
             │                │                │
         Connectors        Semantics       Verification
             │                │                │
             └────────────────┼────────────────┘
                              │
                        Evidence Graph
                              │
             ┌────────────────┼────────────────┐
             │                │                │
       Pension Pack      Utility Pack      Justice Pack
                                                │
                                      future Healthcare/FHIR,
                                      ERP/HCM and other packs
```

## Three architectural foundations

1. **Migration Graph** — how source artifacts map, transform, split, merge, derive, archive, exclude, or relate to target artifacts.
2. **Evidence Graph** — why ProofShift reached every material conclusion, including observations, rules, expected/actual state, lineage, and versions.
3. **Recovery Model** — whether each destructive action is directly reversible, snapshot-restorable, compensatable, or irreversible.

See `docs/ARCHITECTURE.md`, `docs/PS0_SPECIFICATION.md`, and `docs/DOMAIN_MODEL.md` before changing foundational code.

## Technology direction

- .NET 10 / C# 14
- CLI-first PS-0
- PostgreSQL for ProofShift's own persistence when persistence becomes necessary
- The registered source formats, connector capabilities, prerequisites, and
  limits are maintained in the
  [source/connector compatibility table](docs/SOURCE_CONNECTOR_COMPATIBILITY.md)
- OpenTelemetry for operational observability
- deterministic synthetic data and exact-count end-to-end tests
- no required Kubernetes/Kafka/Redis/UI for PS-0

.NET 10 is used because it is the current LTS line for a new 2026 codebase.

## Repository operating rules

Before implementing a milestone:

1. Read `.github/copilot-instructions.md`.
2. Read `docs/PRODUCT_CONTEXT.md`.
3. Read the active milestone in `docs/ROADMAP.md` and `docs/PS0_SPECIFICATION.md`.
4. Do not introduce domain-specific dependencies into the ProofShift core.
5. Do not add infrastructure because it may be useful someday.
6. Every material verification result must be explainable through evidence.
7. Every source artifact must have an explicit disposition.
8. Every target artifact must have lineage.
9. Every destructive migration operation must have recovery metadata.
10. Update documentation and milestone status with implementation changes.

## Current status

ProofShift is proprietary software. This source repository is currently
publicly visible for development/evaluation transparency; public visibility
does not make the project open source. See [LICENSE](LICENSE).

**PS-0.10E Test Discovery and Acceptance Reconciliation is Accepted.** Code candidate `a6e8b15528df5767dd5368f96c000d8dcfb0d1f1` is merged to `main` as `d5bcf8b528bd9d7370841fbfb92625a8954fa75d`; GitHub Actions [run 38068572196](https://github.com/banderson736/ProofShift/actions/runs/38068572196) passed all four required jobs. The unfiltered RemoteStorage project passes 53 tests (0 failed, 0 skipped). Local core passed 237 tests, 0 failed, with 2 documented Windows symlink-capability skips. Oracle and Db2 each passed 2/2 with 0 skips; Pension Fast passed exact 149 defective / 0 corrected, 0 unaccounted/unexplained, corrected `QUALIFIED`. SFTP preserves strict global UTF-8 ordering through bounded external enumeration; the UUID regression preserves unannotated 16-byte binary while mapping UUID-annotated values. See the [acceptance matrix](docs/PS0_10E_ACCEPTANCE_MATRIX.md).

**PS-0.10F Cross-Domain Assurance is Accepted (October 11, 2026).** Merge commit `77bceb325d925eb41c44b0eb7e3d3e5fb74542b9` passed all 60 acceptance gates; GitHub Actions run `38120389177` passed 318 tests with 0 failures and 0 skips across all required jobs. See the [cross-domain report](docs/PS0_10F_CROSS_DOMAIN_ASSURANCE.md), [scenario index](docs/PS0_10F_SCENARIO_INDEX.json), and [acceptance matrix](docs/PS0_10F_ACCEPTANCE_MATRIX.md).

**PS-0.10G Demo & Product Packaging is Accepted (October 24, 2026).** Exact candidate `d3529ccabe04eb1e7b8b3cf2ab643ff8488cf1af` passed all six required jobs in GitHub Actions [run 38172351820](https://github.com/banderson736/ProofShift/actions/runs/38172351820): 331 tests passed, 0 failed, 0 skipped; Windows package-only smoke and all five Ubuntu Docker-backed packaged demos passed. The candidate is included in the accepted `main` baseline `54be9ba2f056465d785353b839b98c8241cff503`. The FHIR report-boundary assertion defect was corrected in the smoke harness; runtime/demo semantics were unchanged. The run produced reproducible platform-specific packages and retained runner/resource evidence; see the [acceptance matrix](docs/PS0_10G_ACCEPTANCE_MATRIX.md). Windows evidence is package-only, and Ubuntu evidence does not claim general Linux product support.

**PS-0.11A Generic Operator Reporting & Comparison is Accepted.** Candidate `1837cad9ea05905dad491b3b0d54f0f7ba7cc852` passed GitHub Actions [run 40146586892](https://github.com/banderson736/ProofShift/actions/runs/40146586892): 338 passed, 0 failed, 0 skipped; Windows package smoke PASS (package-only); Linux five-demo package smoke PASS. It was merged to `main` as `de59d85cca3a5eca4d1b9e49db69a51a8aa42ed1`. These platform claims are limited to the validated package workflows.

**PS-0.11B Product Demo Fixture Separation is Accepted.** Source candidate `c55704c1f0b4f0fdbc3d02ce1054b510539dca8c` and CI-tested synthetic merge `24f5714336e15cf0b6e626a7aff4f578ee5bbb29` have identical tree `b48f84580dd1bb8f410fcf6a924217d7a61d693e`. GitHub Actions [run 40167582711](https://github.com/banderson736/ProofShift/actions/runs/40167582711) passed 350 tests with 0 failures and 0 skips, Windows package-only smoke, and all five Ubuntu packaged Docker demos. It was merged to `main` as `464ef14b235c93e08db58d575595a71eced0bafe`. See the [PS-0.11B acceptance matrix](docs/PS0_11B_ACCEPTANCE_MATRIX.md).

**PS-0.11C Release Identity & Reproducible Distribution is Accepted (October 25, 2026).** Candidate `286ea550e559b360fd9c85c2a65e2b1394a95408` / tree `d32fab40faa4bfc08c28b0742c46f8f372ae26c7` passed [GitHub Actions run 40178365888](https://github.com/banderson736/ProofShift/actions/runs/40178365888) and was merged to `main` as `5a1a3ec064aca98266cf0abb3c7749f7416da67e` (same tree). The run passed 350 tests with 0 failures/skips; Windows package-only smoke, five Ubuntu packaged Docker demos, independent Windows/Linux reproducibility, and runtime-only evaluation all passed. Product identity remains `ProofShift 0.1.0-preview.1`; no tag, publication, or signing occurred. See the [PS-0.11C acceptance matrix](docs/PS0_11C_ACCEPTANCE_MATRIX.md).

**PS-0.11D Internal Consolidation & Maintainability is Accepted (October 25, 2026).** Candidate `6a23233670e5be04d22cdf7b6304fd8090ca493a` (tree `077aaba4a5c0d9fe69e12627c18fe06557510b20`) passed push CI run `40185263041` and PR CI run `40185376379`; both passed all ten jobs. The PR merge to `main`, `34056aaf7342e1426bda66b0d69ba6ae763c7938`, has the same tree. Across required test jobs: 359 passed, 0 failed, 0 skipped. Windows package-only smoke, five Ubuntu packaged Docker demos, Windows/Linux independent A/B reproducibility, and runtime-only evaluation passed. See [PS-0.11D acceptance evidence](docs/PS0_11D_ACCEPTANCE_MATRIX.md).

**PS-0.11E Repository & Product Governance is Accepted (October 25, 2026).** Candidate `fc2411b2bd19e713af097e134388421db9d16fae` (tree `3d36bc4c054709bcfa4b996603cb4d43adeb1a60`) passed all 11 jobs in PR CI run `40188407180` and merged to `main` as `97dee40ff0db732fc83e035e276c10a5747801f0` with the same tree. Final governed-main commit `2d5744753309643d7451123d59bf44b338472d4b` (tree `d6c9e33ae8e2e4d48ebf0d278249f55ef76dce43`) passed all 11 required jobs in run `40244724080`; the test jobs reported 359 passed, 0 failed, 0 skipped. Remote governance and formal acceptance evidence are in the [PS-0.11E acceptance matrix](docs/PS0_11E_ACCEPTANCE_MATRIX.md).

**PS-0.11F Bring-Your-Own-Migration Evaluation is Accepted (October 26, 2026), by repository-owner direction.** Final acceptance: 45 PASS, 0 FAIL, 0 NOT YET. Acceptance-record PR #17 merged to `main` as `810f2f450176bde1eee0edcc200650d633b8f04b` (tree `8ec082eb21683dec06ee48971f49e0d088cdea64`). The [PS-0.11F acceptance matrix](docs/PS0_11F_ACCEPTANCE_MATRIX.md) records the implementation, CI, and governance evidence.

**PS-0.11G Pilot Readiness Acceptance is Accepted (October 28, 2026).** The exact candidate `b25a483dd5b728474e3e1f8e1b957885d42e309a` passed push CI run `40653602682` and PR-context run `40653605811` with all eleven required jobs green, then merged to `main` as `6eaac3f7de155d775032964739c832cca2317b6f` with the same tree. Its 40 acceptance gates are PASS. **ProofShift 0.1.0-preview.1 has completed PS-0.11 pilot-readiness acceptance and is GO for controlled technical evaluation within the documented support boundaries.** This is not production readiness, public release, commercial redistribution authorization, migration-execution approval, or Recovery/rollback approval for externally executed migrations. Windows evidence remains package-only; Ubuntu evidence is limited to packaged Docker evaluation. See the [PS-0.11G acceptance record](docs/PS0_11G_PILOT_READINESS_ACCEPTANCE.md) and [acceptance matrix](docs/PS0_11G_ACCEPTANCE_MATRIX.md).

**PS-1.0A Customer Data Evaluation & Generalized Mapping Foundation is Accepted (October 29, 2026), by explicit repository-owner acceptance.** Accepted implementation candidate `860d4d1a96c0eb14a4da07edb07584874e0e4b2d` was merged through PR #20 as `7b836f699933737cca5986afc3e22160bc48665e`. Push CI run `40794959644` and PR CI run `40794964414` each passed all 11 required jobs; `build-and-test` passed 320 tests with 0 failures and 0 skips. The acceptance matrix records 15 PASS, 0 FAIL, 0 NOT YET, and 0 NOT APPLICABLE, along with package hashes and packaged customer-evaluation results. Acceptance preserves explicitly scoped completeness claims, read-only external Verification, and the boundary that external Verification does not qualify Recovery. Expected-exception annotations do not waive findings. No production migration or rollback is authorized; Windows evidence remains package-only and Linux scenario evidence is limited to packaged Docker evaluation. See the [PS-1.0A scope](docs/PS1_0A_CUSTOMER_DATA_EVALUATION.md) and [acceptance matrix](docs/PS1_0A_ACCEPTANCE_MATRIX.md).

**PS-1.0B Guided Customer Evaluation Workspace & Evidence Package remains in progress pending owner acceptance.** Its implementation baseline and packaged-evaluation/CI evidence are recorded in the [acceptance matrix](docs/PS1_0B_ACCEPTANCE_MATRIX.md) and [ADR-0037](docs/adr/0037-evaluation-workspace-manifest.md). PS-1.0C.1-C.6 technical validation is complete on candidate `5062a6a317c2bcd8b1c0d09e1d221635210568e4`: exact-head push run `41169254780` and PR run `41169259744` passed all 11 required jobs; `build-and-test` passed 362 tests with 0 failures and 0 skips. The extracted-package rehearsal covers physical discovery/schema drift, baseline-bound review and execution, offline comparison, explicit re-baselining, and isolated workspace derivation. Explicit owner acceptance remains pending; PS-1.0C is not accepted. See the [C.6 workflow and boundaries](docs/PS1_0C_EVALUATION_BASELINES.md), [acceptance matrix](docs/PS1_0C_ACCEPTANCE_MATRIX.md), and [ADR-0038](docs/adr/0038-evaluation-baseline-promotion-and-history.md). PS-1.0D and production migration/rollback remain unauthorized.


**PS-0.10C Enterprise Connector & Data-Format Coverage is Accepted.** GitHub Actions [run 37416501703](https://github.com/banderson736/ProofShift/actions/runs/37416501703) passed `build-and-test`, `db2-integration`, and `oracle-integration`: 185 passed, 0 failed, 0 skipped. Real Oracle Free 26ai (`23.26.0-free`, built from Oracle's official source and download) and Db2 LUW Community 11.5.9.0 integrations enforce read-only target observation, typed fidelity, discovery and checkpoint replay; Db2 also covers mixed Db2/fixed-width/NDJSON/filesystem-binary offline replay with `CrossSystemAtomic=false`. Cross-provider observation covers SQL Server→Oracle, Oracle→PostgreSQL, SQL Server→Db2 and Db2→PostgreSQL. Connector schemas/strict validation, XML/fixed-width discovery and encoding, cancellation, and provider-error redaction have focused tests. Structured-file tests pass 21/21, including measured typed reads of 50,000 fixed-width, 20,000 NDJSON and 5,000 XML records. Real 20,000-row Oracle and Db2 reads and ProofShift-process RSS are recorded in [connector test runtimes](docs/CONNECTOR_TEST_RUNTIMES.md); these diagnostic samples show no evidence of whole-input materialization at tested scales and are not an SLA. Local full-suite results: 183 passed, 0 failed, 2 Windows symlink-capability skips; Oracle and Db2 each passed 2/2 with 0 skips. The milestone code commits are 739b942007dbd87a71cf3d130b0056aeebca55aa and eefcae471877b6f0218f60b5ac10b34ec63ef219. **PS-0.10D Verification Throughput & Large-Scale Execution is Accepted.** Candidate commit `6d522728c3d02c7f1f1d83e3c6e1d624e27d8b3d` passed GitHub Actions [run 38014590907](https://github.com/banderson736/ProofShift/actions/runs/38014590907) (`build-and-test`, `oracle-integration`, `db2-integration` all green) and was merged by PR #1 (merge commit `091e7e3`). The initial p8/workers=4 Medium run missed the 6,617.191 s limit at 6,794.325 s; the authorized p8/workers=8 rerun passed at 5,574.083 s (2.374x the accepted 13,234.382 s baseline). The single 1.5M physical Large run completed the full pipeline for exactly 1,500,000 generated source records (exact 149/0, `QUALIFIED`, 28,143.783 s processing, zero SQLite contention). Large is superlinear (+38.35% processing and +43.61% Verification seconds per 100k records versus Medium) and workers=8 scales worse 100k to 200k than workers=4; there is no SLA, constant-memory or linear-scaling claim. Do not run a second Large or 2M+, rerun/tune Medium, begin PS-0.10E, or add production execution/rollback without a new explicit assignment. See [Large result](docs/PS0_10D_LARGE_BENCHMARK.json), [Medium result](docs/PS0_10D_MEDIUM_BENCHMARK.json), [ADR-0023](docs/adr/0023-sequential-partition-local-verification-scratch.md) and the [roadmap](docs/ROADMAP.md).

**PS-0.1 through PS-0.9 and PS-0.10A through PS-0.10G are accepted.** PS-0.10A passed Docker-backed [GitHub Actions run 37284331965, job 111679522042](https://github.com/banderson736/ProofShift/actions/runs/37284331965/job/111679522042): 123 passed, 0 failed, 0 skipped. It preserves exact 149-defect / corrected-zero semantics with bounded PostgreSQL writes, persisted Verification ledgers, aggregate Recovery summaries, typed ordering, explicit checkpoint consistency, and NDJSON Evidence. Final Fast passed in 150.5 seconds total with 209.8 MB peak RSS. Medium completed the physical full pipeline in 3h 55m, with 424,750 checkpoint artifacts, 429,750 projected targets, 1.076 GB peak workload RSS, and exact 149/0 qualification outcomes. Restore/build and local tests passed: 121 passed, 0 failed, 2 Windows symlink-capability skips. Verification remains the dominant disk-backed cost; stage comparisons and limitations are in the [scale report](docs/COPILOT_PS0_10A_SCALE_HARDENING.md). Acceptance is not a production readiness claim or throughput SLA. Production migration and rollback remain deferred. See the [pension demo guide](docs/demo/PENSION_DEMO.md) and [roadmap](docs/ROADMAP.md).

CLI commands: `proofshift workspace init <directory> [--template generic|pension]`, `proofshift workspace validate <directory> [--json]`, `proofshift workspace preflight <directory> [--connect] [--json]`, `proofshift workspace plan <directory> [--baseline active|<baseline-id>|none] [--json]`, `proofshift workspace review-plan <directory> [--baseline active|<baseline-id>] [--json]`, `proofshift workspace run <directory> [--baseline active|<baseline-id>|none] [--review <review-id>] [--checkpoint <id-or-path>] [--observation <label>] [--json]`, `proofshift workspace evidence package <workspace> <run-id> [--output <zip>] [--diagnostic] [--json]`, `proofshift workspace evidence validate-package <zip> [--json]`, `proofshift workspace evidence compare-packages <baseline.zip> <candidate.zip> [--json]`, `proofshift validate <config>`, `proofshift plan <config>`, `proofshift inspect <config>`, `proofshift snapshot <config> [--json]`, `proofshift project <config> [--checkpoint <id-or-path>] [--json]`, `proofshift verify <config> --checkpoint <id> --projection <run-id> [--json]`, `proofshift evidence <config> --run <verification-run-id> [--json]`, `proofshift recovery <config> --run <dry-run-id> [--json]`, `proofshift report <config> --run <dry-run-id> [--json]`, `proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json]`, `proofshift dry-run <config> [--json]`, and `proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]`. `workspace validate` remains offline and structural. `workspace preflight` is also offline by default; `--connect` opts into read-only endpoint inspection and discovery drift checks. `workspace plan` describes the effective configuration without executing it. Baseline-aware `workspace run` revalidates the selected baseline and exact effective plan before checkpoint capture; it requires an immutable review ID only when semantic drift requires review or a valid comparison is unavailable. The review is not production approval. Completed baseline-bound runs persist a B.5 comparison outcome; incomplete runs have no business classification, and candidates are never auto-promoted. Offline package comparison validates both package identities and either enforces exact baseline governance or labels an unbound pair as ad hoc. External Verification remains read-only and does not qualify Recovery. Evidence-package validation is offline, does not extract archives, and separates package integrity from evaluation disposition; packages may contain customer-sensitive Evidence and are not automatically anonymized. Persisted reports resolve the project's exact pack/version and claim boundary, validate stored Evidence, Recovery, Projection, and ledger bindings, and never rerun Verification; comparisons allow historical configuration and graph differences while requiring the same project and pack.

The generic scenario CLI is `proofshift demo list`, `proofshift demo describe <id>`, `proofshift demo preflight <id>`, `proofshift demo run <id>`, and `proofshift demo reset <id>`; each supports `--json` where appropriate. Scenario manifests live under `scenarios/demos/<id>/scenario.json` and are documented in [Demo Scenario Manifest v1](docs/demo/DEMO_SCENARIO_MANIFEST.md). The catalog includes the Pension flagship and compact Utility, Justice, HCM, and selected FHIR-style kits; see the [cross-domain demo guide](docs/demo/CROSS_DOMAIN_DEMOS.md).

Run the physical full-pipeline benchmark with `./scripts/pension-benchmark.ps1 -Scale fast|medium -OutputDirectory <empty-directory>`. It exercises SQL Server, CSV, filesystem, PostgreSQL, checkpoints, Projection, Verification, Evidence, Recovery, and qualification. Compare saved stage runs with `./scripts/compare-benchmarks.ps1 -Before <performance-run.json> -After <performance-run.json>`. `proofshift demo benchmark --scale large` remains a generator-enumeration benchmark and is not full-pipeline throughput.

**PS-0.10B Configuration & Authoring UX is Accepted.** [Docker-backed run 37353339103, job 111909408991](https://github.com/banderson736/ProofShift/actions/runs/37353339103/job/111909408991) verified final implementation commit `625ca84` with 143 passed, 0 failed, 0 skipped. The workflow includes typed provider-owned rules/schemas, explicit pack versions, generic/Pension init, read-only SQL Server/PostgreSQL/CSV/filesystem discovery and drift, deterministic review-state scaffold, validated CSV import, reusable code maps, strict validation, project/rule/mapping explain and effective configuration. The complete physical Pension regression loads [reviewed configuration](scenarios/pension-modernization/ps010b/README.md) and preserves exact 149/0/QUALIFIED outcomes. See [Configuration Authoring](docs/CONFIGURATION_AUTHORING.md), the [walkthrough](docs/AUTHORING_WALKTHROUGH.md), and [ADR-0018](docs/adr/0018-structured-rule-authoring-contract.md). Local tests passed 141 with 0 failures and 2 Windows capability skips; remote CI exercised required containment tests. PS-0.10C, connector/domain expansion and production execution/rollback remain deferred until a new explicit assignment.
