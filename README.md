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

**PS-0.1 through PS-0.8 are accepted. PS-0.8 passed Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) with 103 passed, 0 failed, and 0 skipped.** Source checkpoints remain distinct from target recovery checkpoints. Projection remains shadow-only and is not verification evidence. Recovery rehearsal is confined to isolated PostgreSQL/filesystem shadow targets; production migration and rollback remain deferred. Do not begin PS-0.9 without a new assignment. See the [PS-0.5 fixture](scenarios/pension-modernization/ps05/proofshift.yaml) and [roadmap](docs/ROADMAP.md).

CLI commands: `proofshift validate <config>`, `proofshift plan <config>`, `proofshift inspect <config>`, `proofshift snapshot <config> [--json]`, `proofshift project <config> [--checkpoint <id-or-path>] [--json]`, `proofshift verify <config> --checkpoint <id> --projection <run-id> [--json]`, `proofshift evidence <config> --run <verification-run-id> [--json]`, `proofshift recovery <config> --run <dry-run-id> [--json]`, and `proofshift dry-run <config> [--json]`.
