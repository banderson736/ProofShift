# GitHub Copilot IDE Handoff

## Read first

Before making changes, read in this order:

1. `README.md`
2. `.github/copilot-instructions.md`
3. `docs/PRODUCT_CONTEXT.md`
4. `docs/PRODUCT_PRINCIPLES.md`
5. `docs/ARCHITECTURE.md`
6. `docs/DOMAIN_MODEL.md`
7. `docs/CONFIGURATION.md`
8. `docs/PS0_SPECIFICATION.md`
9. `docs/ROADMAP.md`
10. `docs/DECISIONS.md`

Use `docs/MARKET_RESEARCH.md` for commercial/domain context, not as an implementation specification.

## Current assignment

PS-0.4 was accepted after [GitHub Actions run 37173803105](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267) passed all 67 tests with zero failures and zero skips. PS-0.5 Shadow Projection was accepted after [run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298) passed all 82 tests with zero failures and zero skips. PS-0.6 Source Checkpoints & Reproducible Snapshots was accepted after Docker-backed [run 37180351598, job 111371493382](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with zero failures and zero skips. PS-0.7 Semantic Verification & Evidence Graph was accepted after Docker-backed [run 37185122084, job 111385343893](https://github.com/banderson736/ProofShift/actions/runs/37185122084/job/111385343893) passed 96 tests with zero failures and zero skips. PS-0.8 Recovery Readiness & Dry-Run Qualification was accepted after Docker-backed [run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed 103 tests with zero failures and zero skips. PS-0.9 Public Pension Assurance Vertical was accepted after Docker-backed [run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips. PS-0.10A Scale Baseline & Hot-Path Hardening is Accepted after run 37284331965, job 111679522042 passed 123 tests with zero failures and zero skips; do not begin PS-0.10B/C without a new explicit assignment.

PS-0.6 uses a separate `ProofShift.Snapshots` assembly and a generic connector-neutral source stream provider. PostgreSQL/SQL Server capture guarantees are endpoint-level only; filesystem and CSV are observed reads with before/after drift checks. Mixed-source checkpoints explicitly report `CrossSystemAtomic: false`. Checkpoint replay matches configuration hash, graph hash, exact source-node coverage, endpoint identity, and selectors; it never falls back to live reads.

PS-0.7 verification uses the exact checkpoint and completed projection manifest, independently reads physical shadow state, preserves graph-node scope for split artifacts, and emits integrity-checked deterministic evidence. Raw values and secrets remain out of normal evidence output. Acceptance is complete; the CI run passed without required skips.

PS-0.8 must distinguish source checkpoints from target recovery checkpoints, assess each executed graph edge/artifact, validate Reverse loss conservatively, resolve Compensate strategies through a registry, default-deny irreversible state, and rehearse only isolated PostgreSQL/filesystem shadow restoration. Keep recovery evidence separate from the immutable Verification Evidence Graph. `QUALIFIED DRY RUN` is technical qualification, never business approval or production safety.

PS-0.9 Public Pension Assurance Vertical was accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips. Keep pension rule logic in `ProofShift.Packs.Pension`; do not add production migration/rollback.

### PS-0.10A current scope

Preserve the exact defective 149 / corrected 0 discrepancies, corrected zero unaccounted/unexplained state, false-Reverse behavior, and external Verification. Completed locally: bounded PostgreSQL writes and journal batches, disposable SQLite WAL/NORMAL profile, SQLite Verification ledger with store-side coverage, ledger-backed Recovery with aggregate artifact coverage, typed indexed ordering keys, streamed Evidence NDJSON v2 and aggregate successful source accounting, explicit SQL Server checkpoint consistency, and ReadOptions without a misleading fetch-size property.

Final Fast passed in 150.5 seconds total / 139.0 seconds historical runtime with 209.8 MB peak RSS. Medium completed successfully in 3h 55m with 424,750 checkpoint artifacts, 429,750 projected targets, 1.076 GB workload peak RSS, stage-local memory plateaus, and exact 149/0 qualification semantics. The original WAL baseline (117.1 / 104.0 seconds, 567.3 MB peak) and the 229.1-second pre-investigation stage records were recovered. Prepared commands, deferred secondary indexes, and explicit identity-index coverage fix measured bottlenecks. Historical runtime includes startup; interpret it with the corrected metric and limitations in `docs/COPILOT_PS0_10A_SCALE_HARDENING.md`.

PS-0.10A is Accepted after Docker-backed [run 37284331965, job 111679522042](https://github.com/banderson736/ProofShift/actions/runs/37284331965/job/111679522042) verified commit `8111c9c` with 123 passed, 0 failed, 0 skipped. Final local restore/build passed; local tests completed with 121 passed, 0 failed, and 2 Windows symlink-capability skips. Medium artifacts are under `%TEMP%/ProofShift-PS010A-coverage-fixed-medium/integrated-assurance`; final Fast artifacts are under `%TEMP%/ProofShift-PS010A-acceptance-fast/integrated-assurance`. The Medium binary predates a telemetry-only correction for post-index scratch peak; use completed workspace file diagnostics and samples for its actual scratch measurements. Verification still has above-linear measured runtime and significant disk staging; preserve those limitations. Do not start PS-0.10B/C or production write/rollback work without a new explicit assignment.

Use `./scripts/pension-benchmark.ps1 -Scale medium -OutputDirectory <empty-directory>` for the physical medium run and `./scripts/compare-benchmarks.ps1 -Before <before/performance-run.json> -After <after/performance-run.json>` for stage/memory/workspace comparisons. `proofshift demo benchmark --scale large` measures generator enumeration only.

### PS-0.1 deliverables

Implement only the domain/abstraction types needed for:

- strong IDs;
- Project;
- SystemDefinition and StorageEndpointDefinition;
- Snapshot and EndpointSnapshot;
- ArtifactReference;
- RecordEnvelope + ValueNode types;
- RelationshipReference;
- TemporalMetadata / ProvenanceMetadata;
- MigrationPlan;
- MigrationGraph/Node/Edge;
- MigrationOperation base and minimal concrete operations necessary for configuration/tests;
- RecoveryDefinition / RecoveryMode;
- ArtifactDispositionRecord;
- LineageRecord;
- evidence model;
- run/runtime fingerprint model;
- verification scope/result abstractions.

Add validation/domain services only where invariants do not belong in constructors/value objects.

### Required PS-0.1 tests

At minimum test:

- strong ID equality/value semantics;
- invalid/empty IDs where applicable;
- graph cannot reference nonexistent nodes when graph validation is invoked;
- destructive migration edges require recovery definition;
- artifact dispositions reject unsupported/unexplained state where applicable;
- target lineage requires one or more valid source references;
- immutable collections are not exposed as mutable internals;
- canonical graph representation/hash is deterministic once hashing is introduced.

Do not create fake tests that simply assert constructed values equal themselves.

### PS-0.2 deliverables

Implement configuration loading for the proposed multi-file layout:

```text
proofshift.yaml
systems/source.yaml
systems/target.yaml
migration/graph.yaml
rules/pension.yaml
recovery/policy.yaml
```

Features:

- YAML parse;
- external-file references;
- environment/secret references;
- structural validation;
- semantic/cross-reference validation;
- canonical non-secret configuration representation;
- deterministic configuration hash;
- diagnostics with file/path context;
- CLI `proofshift validate` once supporting library code is ready.

### Explicit non-goals

Do not add:

- web UI;
- API server;
- EF Core database persistence unless PS-0.1/0.2 demonstrably requires it;
- Docker/Kubernetes;
- Kafka/Redis;
- AI;
- FHIR;
- Oracle/DB2;
- authentication;
- production migration execution.

## Development workflow

For each slice:

1. restate which acceptance criteria are being addressed;
2. inspect existing code/tests/docs;
3. make the smallest cohesive change;
4. add/modify tests;
5. run focused tests;
6. run full solution tests when feasible;
7. report what changed and any assumptions;
8. update `docs/ROADMAP.md` status and ADR/DECISIONS when architecture changes.

Do not silently reinterpret the product architecture. If an implementation constraint requires changing a core principle, record an ADR before proceeding.
