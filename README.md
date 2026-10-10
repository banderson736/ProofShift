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
- SQL Server + PostgreSQL + filesystem/CSV initial connectors
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

**PS-0.10E Test Discovery and Acceptance Reconciliation is In Progress and unaccepted.** Code candidate `a6e8b15528df5767dd5368f96c000d8dcfb0d1f1` is merged to `main` as `d5bcf8b528bd9d7370841fbfb92625a8954fa75d`; GitHub Actions [run 38068572196](https://github.com/banderson736/ProofShift/actions/runs/38068572196) passed all four required jobs. The unfiltered RemoteStorage project passes 53 tests (0 failed, 0 skipped). Restore/build and the local core suite passed 239 tests: 237 passed, 0 failed, 2 Windows symlink-capability skips. Oracle and Db2 each passed 2/2 with 0 skips; Pension Fast passed exact 149 defective / 0 corrected, 0 unaccounted/unexplained, Recovery `Passed`, corrected `QUALIFIED`. SFTP preserves strict global UTF-8 ordering using bounded external enumeration; unit and OpenSSH integration tests force multiple sorted runs and verify bounds, cleanup and chunk-independent discovery fingerprints. The UUID regression preserves unannotated 16-byte binary while decoding UUID-annotated values without broadening supported types. See the [acceptance matrix](docs/PS0_10E_ACCEPTANCE_MATRIX.md). Formal PS-0.10E acceptance remains pending review; do not begin PS-0.10F.


**PS-0.10C Enterprise Connector & Data-Format Coverage is Accepted.** GitHub Actions [run 37416501703](https://github.com/banderson736/ProofShift/actions/runs/37416501703) passed `build-and-test`, `db2-integration`, and `oracle-integration`: 185 passed, 0 failed, 0 skipped. Real Oracle Free 26ai (`23.26.0-free`, built from Oracle's official source and download) and Db2 LUW Community 11.5.9.0 integrations enforce read-only target observation, typed fidelity, discovery and checkpoint replay; Db2 also covers mixed Db2/fixed-width/NDJSON/filesystem-binary offline replay with `CrossSystemAtomic=false`. Cross-provider observation covers SQL Server→Oracle, Oracle→PostgreSQL, SQL Server→Db2 and Db2→PostgreSQL. Connector schemas/strict validation, XML/fixed-width discovery and encoding, cancellation, and provider-error redaction have focused tests. Structured-file tests pass 21/21, including measured typed reads of 50,000 fixed-width, 20,000 NDJSON and 5,000 XML records. Real 20,000-row Oracle and Db2 reads and ProofShift-process RSS are recorded in [connector test runtimes](docs/CONNECTOR_TEST_RUNTIMES.md); these diagnostic samples show no evidence of whole-input materialization at tested scales and are not an SLA. Local full-suite results: 183 passed, 0 failed, 2 Windows symlink-capability skips; Oracle and Db2 each passed 2/2 with 0 skips. The milestone code commits are 739b942007dbd87a71cf3d130b0056aeebca55aa and eefcae471877b6f0218f60b5ac10b34ec63ef219. **PS-0.10D Verification Throughput & Large-Scale Execution is Accepted.** Candidate commit `6d522728c3d02c7f1f1d83e3c6e1d624e27d8b3d` passed GitHub Actions [run 38014590907](https://github.com/banderson736/ProofShift/actions/runs/38014590907) (`build-and-test`, `oracle-integration`, `db2-integration` all green) and was merged by PR #1 (merge commit `091e7e3`). The initial p8/workers=4 Medium run missed the 6,617.191 s limit at 6,794.325 s; the authorized p8/workers=8 rerun passed at 5,574.083 s (2.374x the accepted 13,234.382 s baseline). The single 1.5M physical Large run completed the full pipeline for exactly 1,500,000 generated source records (exact 149/0, `QUALIFIED`, 28,143.783 s processing, zero SQLite contention). Large is superlinear (+38.35% processing and +43.61% Verification seconds per 100k records versus Medium) and workers=8 scales worse 100k to 200k than workers=4; there is no SLA, constant-memory or linear-scaling claim. Do not run a second Large or 2M+, rerun/tune Medium, begin PS-0.10E, or add production execution/rollback without a new explicit assignment. See [Large result](docs/PS0_10D_LARGE_BENCHMARK.json), [Medium result](docs/PS0_10D_MEDIUM_BENCHMARK.json), [ADR-0023](docs/adr/0023-sequential-partition-local-verification-scratch.md) and the [roadmap](docs/ROADMAP.md).

**PS-0.1 through PS-0.9 and PS-0.10A are accepted.** PS-0.10A passed Docker-backed [GitHub Actions run 37284331965, job 111679522042](https://github.com/banderson736/ProofShift/actions/runs/37284331965/job/111679522042): 123 passed, 0 failed, 0 skipped. It preserves exact 149-defect / corrected-zero semantics with bounded PostgreSQL writes, persisted Verification ledgers, aggregate Recovery summaries, typed ordering, explicit checkpoint consistency, and NDJSON Evidence. Final Fast passed in 150.5 seconds total with 209.8 MB peak RSS. Medium completed the physical full pipeline in 3h 55m, with 424,750 checkpoint artifacts, 429,750 projected targets, 1.076 GB peak workload RSS, and exact 149/0 qualification outcomes. Restore/build and local tests passed: 121 passed, 0 failed, 2 Windows symlink-capability skips. Verification remains the dominant disk-backed cost; stage comparisons and limitations are in the [scale report](docs/COPILOT_PS0_10A_SCALE_HARDENING.md). Acceptance is not a production readiness claim or throughput SLA. PS-0.10B remains accepted; PS-0.10C is explicitly assigned as described above. Production migration and rollback remain deferred. See the [pension demo guide](docs/demo/PENSION_DEMO.md) and [roadmap](docs/ROADMAP.md).

CLI commands: `proofshift validate <config>`, `proofshift plan <config>`, `proofshift inspect <config>`, `proofshift snapshot <config> [--json]`, `proofshift project <config> [--checkpoint <id-or-path>] [--json]`, `proofshift verify <config> --checkpoint <id> --projection <run-id> [--json]`, `proofshift evidence <config> --run <verification-run-id> [--json]`, `proofshift recovery <config> --run <dry-run-id> [--json]`, `proofshift report <config> --run <dry-run-id> [--json]`, `proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json]`, `proofshift dry-run <config> [--json]`, and `proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]`.

Run the physical full-pipeline benchmark with `./scripts/pension-benchmark.ps1 -Scale fast|medium -OutputDirectory <empty-directory>`. It exercises SQL Server, CSV, filesystem, PostgreSQL, checkpoints, Projection, Verification, Evidence, Recovery, and qualification. Compare saved stage runs with `./scripts/compare-benchmarks.ps1 -Before <performance-run.json> -After <performance-run.json>`. `proofshift demo benchmark --scale large` remains a generator-enumeration benchmark and is not full-pipeline throughput.

**PS-0.10B Configuration & Authoring UX is Accepted.** [Docker-backed run 37353339103, job 111909408991](https://github.com/banderson736/ProofShift/actions/runs/37353339103/job/111909408991) verified final implementation commit `625ca84` with 143 passed, 0 failed, 0 skipped. The workflow includes typed provider-owned rules/schemas, explicit pack versions, generic/Pension init, read-only SQL Server/PostgreSQL/CSV/filesystem discovery and drift, deterministic review-state scaffold, validated CSV import, reusable code maps, strict validation, project/rule/mapping explain and effective configuration. The complete physical Pension regression loads [reviewed configuration](scenarios/pension-modernization/ps010b/README.md) and preserves exact 149/0/QUALIFIED outcomes. See [Configuration Authoring](docs/CONFIGURATION_AUTHORING.md), the [walkthrough](docs/AUTHORING_WALKTHROUGH.md), and [ADR-0018](docs/adr/0018-structured-rule-authoring-contract.md). Local tests passed 141 with 0 failures and 2 Windows capability skips; remote CI exercised required containment tests. PS-0.10C, connector/domain expansion and production execution/rollback remain deferred until a new explicit assignment.
