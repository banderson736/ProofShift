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

**PS-0.1 through PS-0.9 and PS-0.10A are accepted.** PS-0.10A passed Docker-backed [GitHub Actions run 37284331965, job 111679522042](https://github.com/banderson736/ProofShift/actions/runs/37284331965/job/111679522042): 123 passed, 0 failed, 0 skipped. It preserves exact 149-defect / corrected-zero semantics with bounded PostgreSQL writes, persisted Verification ledgers, aggregate Recovery summaries, typed ordering, explicit checkpoint consistency, and NDJSON Evidence. Final Fast passed in 150.5 seconds total with 209.8 MB peak RSS. Medium completed the physical full pipeline in 3h 55m, with 424,750 checkpoint artifacts, 429,750 projected targets, 1.076 GB peak workload RSS, and exact 149/0 qualification outcomes. Restore/build and local tests passed: 121 passed, 0 failed, 2 Windows symlink-capability skips. Verification remains the dominant disk-backed cost; stage comparisons and limitations are in the [scale report](docs/COPILOT_PS0_10A_SCALE_HARDENING.md). Acceptance is not a production readiness claim or throughput SLA. Do not begin PS-0.10B/C or production migration/rollback without a new explicit assignment. See the [pension demo guide](docs/demo/PENSION_DEMO.md) and [roadmap](docs/ROADMAP.md).

CLI commands: `proofshift validate <config>`, `proofshift plan <config>`, `proofshift inspect <config>`, `proofshift snapshot <config> [--json]`, `proofshift project <config> [--checkpoint <id-or-path>] [--json]`, `proofshift verify <config> --checkpoint <id> --projection <run-id> [--json]`, `proofshift evidence <config> --run <verification-run-id> [--json]`, `proofshift recovery <config> --run <dry-run-id> [--json]`, `proofshift report <config> --run <dry-run-id> [--json]`, `proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json]`, `proofshift dry-run <config> [--json]`, and `proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]`.

Run the physical full-pipeline benchmark with `./scripts/pension-benchmark.ps1 -Scale fast|medium -OutputDirectory <empty-directory>`. It exercises SQL Server, CSV, filesystem, PostgreSQL, checkpoints, Projection, Verification, Evidence, Recovery, and qualification. Compare saved stage runs with `./scripts/compare-benchmarks.ps1 -Before <performance-run.json> -After <performance-run.json>`. `proofshift demo benchmark --scale large` remains a generator-enumeration benchmark and is not full-pipeline throughput.

**PS-0.10B Configuration & Authoring UX is assigned and In Progress.** The foundation provides opt-in structured rule documents, provider-owned validation/schema descriptors, explicit pack versions, generic/Pension `init`, `rules list/describe/schema`, `capabilities`, and strict authored-rule checks. See [Configuration Authoring](docs/CONFIGURATION_AUTHORING.md) and [ADR-0018](docs/adr/0018-structured-rule-authoring-contract.md). Discovery/scaffold/import/explain, the committed complete Pension authoring workflow, and remote integration acceptance remain open. No PS-0.10C or unrelated performance work is in scope.
