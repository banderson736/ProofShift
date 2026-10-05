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

**PS-0.1 through PS-0.9 are accepted. PS-0.9 passed Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181): 113 passed, 0 failed, 0 skipped.** The fast integrated scenario captures physical SQL Server, CSV, and filesystem sources, projects to PostgreSQL/filesystem targets, persists Evidence and Recovery artifacts, and compares defective and corrected report runs. The defective run reports exactly 149 discrepancies; the corrected run qualifies with zero. Production migration and rollback remain deferred. PS-0.10 has not been assigned. See the [pension demo guide](docs/demo/PENSION_DEMO.md) and [roadmap](docs/ROADMAP.md).

CLI commands: `proofshift validate <config>`, `proofshift plan <config>`, `proofshift inspect <config>`, `proofshift snapshot <config> [--json]`, `proofshift project <config> [--checkpoint <id-or-path>] [--json]`, `proofshift verify <config> --checkpoint <id> --projection <run-id> [--json]`, `proofshift evidence <config> --run <verification-run-id> [--json]`, `proofshift recovery <config> --run <dry-run-id> [--json]`, `proofshift report <config> --run <dry-run-id> [--json]`, `proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json]`, `proofshift dry-run <config> [--json]`, and `proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]`.
