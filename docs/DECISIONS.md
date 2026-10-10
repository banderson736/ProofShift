# Architectural Decisions Summary

This file is a quick index. Detailed decisions may be added as ADRs under `docs/adr/`.

PS-0.10B authoring contracts: see [ADR-0018](adr/0018-structured-rule-authoring-contract.md). Rule-document v2 preserves typed nested values; legacy documents retain their interpretation/fingerprints. Provider-owned descriptors validate exact rule versions and configured types. This is an explicitly assigned authoring slice, not PS-0.10B acceptance.

## Accepted decisions

PS-0.10C's assigned implementation boundary is recorded in [ADR-0019](adr/0019-target-observation-and-connector-capabilities.md): independent read-only target observation, distinct shadow mutation/Recovery authority and connector capability reporting. The milestone remains In Progress; provider and format acceptance gates are not satisfied by compilation.

### D-001 — ProofShift is migration assurance, not generic DB diff
Status: Accepted

ProofShift's core value is semantic, auditable, recoverable migration assurance across heterogeneous systems.

### D-002 — Public pensions are the first domain pack
Status: Accepted

The core remains domain-neutral. Pension is the initial demonstration/commercial wedge based on current market research.

### D-003 — Migration Graph, Evidence Graph, Recovery Model are foundational
Status: Accepted

Do not collapse these into one flat job/result structure.

### D-004 — History is first-class
Status: Accepted

Current state alone is insufficient.

### D-005 — Dry runs create shadow output
Status: Accepted

Projection should materialize realistic isolated target state, not only predict changes.

### D-006 — Verification can operate independently of execution
Status: Accepted

External migration tools/vendors remain supported. Future ProofShift orchestration is optional.

### D-007 — Every source artifact has a disposition
Status: Accepted

Unaccounted artifacts fail readiness.

### D-008 — Every target artifact has lineage
Status: Accepted

Targets without provenance fail readiness.

### D-009 — Recovery readiness is part of migration readiness
Status: Accepted

Destructive transformations require explicit recovery strategy. PS-0 rejects irreversible operations.

### D-010 — Domain packs, not a universal business schema
Status: Accepted

Generic core concepts remain small; industry semantics are pack-specific.

### D-011 — .NET 10
Status: Accepted for bootstrap

New repository targets current LTS .NET 10. Reconsider only for a concrete customer/platform constraint.

### D-012 — CLI first
Status: Accepted

PS-0 is usable/testable without web UI.

### D-013 — Minimal infrastructure
Status: Accepted

No Kafka/Redis/Kubernetes merely because the team knows them. Add only from measured requirements.

### D-014 — Canonical fingerprints are defined by the compiler
Status: Accepted

PS-0.2 defines `proofshift-config-canonical-v1`; PS-0.3 defines `proofshift-graph-canonical-v1`. Both compute SHA-256 fingerprints without resolved secret values. See `docs/adr/0002-graph-fingerprint-canonicalization.md`.

### D-015 — Configuration project identifiers remain distinct from domain IDs
Status: Accepted

The version-1 configuration project ID is a validated string identifier. The PS-0.1 `ProjectId` is a GUID, and no stable conversion contract is specified. PS-0.2 retains the configured project as a DTO instead of inventing an identity mapping; plan binding must define the relationship explicitly.

### D-016 — Compile graph keys to deterministic internal IDs
Status: Accepted

PS-0.3 compiles connector-neutral selectors and many-to-many edges, derives scoped deterministic UUIDv8-style GUIDs from external graph keys, and hashes `proofshift-graph-canonical-v1`. Relationship-only cycles are warnings; execution/destructive cycles are errors. See `docs/adr/0003-migration-graph-compilation.md`.

### D-017 — Read-only connector inspection and runtime secret boundary
Status: Accepted

PS-0.4 places read-only source contracts and redacting runtime settings in connector abstractions, inspection orchestration in Engine, and concrete registrations in the CLI composition root. Relational identifiers are validated/quoted, CSV duplicate checks use a temporary disk-backed identity index, and file/binary hashes stream. Inspection/read is not snapshotting or evidence. See `docs/adr/0004-source-connector-runtime.md`.

### D-018 — Preserve timestamp semantics and retrievable binary references
Status: Accepted

PS-0.4 source values distinguish instants, explicit offsets, and local timestamps without assigning a machine timezone. Binary values carry reference, length, and SHA-256, and source binary content is reopened as a stream through connector abstractions. See `docs/adr/0005-source-value-fidelity.md`.

### D-019 — PS-0.5 is gated on Docker-backed connector acceptance
Status: Accepted

PS-0.5 projection must not begin until PostgreSQL and SQL Server Testcontainers integration tests pass in Docker-capable CI. Local skips do not satisfy this gate. The Ubuntu workflow runs the full suite, including the Linux symlink-boundary test. Gate satisfied by [GitHub Actions run 37173803105](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267): 67 passed, 0 failed, 0 skipped.

### D-020 — Projection is shadow-only and distinct from verification
Status: Accepted

PS-0.5 writes only through `IShadowTargetConnector` contexts for systems with role `shadow-target`. PostgreSQL schemas and filesystem directories are isolated per run; the append-only Projection Journal records execution ancestry, and a versioned fingerprint is computed from read-back materialized state. Projection is not evidence of correctness. Merge/chained execution is deferred where deterministic semantics are undefined. See `docs/adr/0006-shadow-projection-runtime.md`.

Acceptance status: accepted after [GitHub Actions run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298), which passed all 82 tests with zero failures and zero skips.

### D-021 — Checkpoints are local materialized inputs with endpoint-only consistency claims
Status: Accepted

PS-0.6 implements immutable materialized source checkpoints in a separate `ProofShift.Snapshots` assembly. The generic Projection source provider validates exact configuration/graph/source coverage and fails closed without live-read fallback. Relational connectors use provider-supported checkpoint transactions; filesystem/CSV remain observed reads with drift detection. Mixed-source checkpoints report `CrossSystemAtomic: false`. Incomplete checkpoints cannot be replayed, and integrity is checked before shadow destinations are prepared. See `docs/adr/0007-materialized-source-checkpoints.md`.

PS-0.6 was accepted after Docker-backed [GitHub Actions run 37180351598](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with zero failures and zero skips. PS-0.7 Semantic Verification & Evidence Graph was accepted after Docker-backed [GitHub Actions run 37185122084](https://github.com/banderson736/ProofShift/actions/runs/37185122084/job/111385343893) passed 96 tests with zero failures and zero skips. PS-0.8 Recovery Readiness & Dry-Run Qualification was accepted after Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed 103 tests with zero failures and zero skips.

### D-022 — Recovery readiness is separate from source checkpointing and finalized verification evidence
Status: Accepted for PS-0.8

Recovery assessment consumes a complete Verification result and actual graph-scoped journal/lineage coverage. Source checkpoints remain pre-migration source inputs; target restore capability must be separately captured and validated. PostgreSQL per-run schema copies and filesystem per-run tree copies are shadow-only rehearsal mechanisms and do not claim provider-native production backup behavior. Recovery evidence is a separate immutable graph referencing the finalized PS-0.7 evidence. Qualification re-observes the physical target and requires exact shadow cleanup to the verified baseline, even when a registered semantic compensator validates a non-identical intermediate recovery result. See `docs/adr/0010-recovery-readiness-and-dry-run-qualification.md`.

PS-0.8 was accepted after Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed the full solution test suite: 103 passed, 0 failed, and 0 skipped.

### D-023 — Pension semantic rules consume generic ordered verification-record streams
Status: Accepted for PS-0.9

PS-0.9 adds `IVerificationWorkspace.ReadArtifactRecordsAsync`, which streams typed source, expected-target, and actual-target records from the private disk-backed SQLite workspace and can order them by normalized fields. Pension rules merge ordered groups to bound transaction, payment, service-credit, and timeline working sets. The shared Verification layer contains no pension concepts; pack rules remain registered through `PensionPack`. See `docs/adr/0011-streamable-verification-record-observations.md`.

PS-0.9 was accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips. The integrated fast scenario captures physical SQL Server, CSV, and filesystem sources (8,495 checkpoint artifacts, including 275 binary payloads), projects 8,595 PostgreSQL/filesystem targets, and runs corrected and projected-defective datasets through generic Verification and Recovery. Persisted reports show 149 defects/not-qualified versus zero/qualified; CLI comparison resolves all 149 and attributes changes to source/checkpoint and graph. The integrated fast run measured 396,017 ms and 519,651,328 bytes peak working set. The large-scale benchmark remains generator-only. PS-0.10 is not assigned.

### D-024 — Verification SQLite is disposable WAL/NORMAL scratch state
Status: Accepted for PS-0.10A

The private per-run Verification workspace uses SQLite WAL with `synchronous=NORMAL` and retains bounded transactions. Power loss may lose its latest committed scratch transactions, so incomplete Verification is rerun and never accepted. Final Evidence Graph persistence remains a separate durable operation. See `docs/adr/0015-disposable-verification-workspace-sqlite-profile.md`.

### D-025 — SQL Server checkpoint consistency is explicit and endpoint-scoped
Status: Accepted for PS-0.10A

SQL Server checkpoint capture defaults to Observed. Transaction-consistent reads require explicit endpoint isolation; Serializable is opt-in, Snapshot is not enabled automatically, and any permitted downgrade is recorded in the checkpoint manifest. PostgreSQL retains repeatable-read semantics, and no endpoint policy claims cross-system atomicity. Snapshot manifest canonicalization is v2 to integrity-bind requested/effective strategy and downgrade metadata. See `docs/adr/0016-explicit-sqlserver-checkpoint-consistency.md`.

### D-026 — Persist Verification ledgers and stream Evidence artifacts
Status: Accepted for PS-0.10A

Verification and Recovery consume a private indexed SQLite ledger rather than successful result-sized lineage/disposition/journal arrays. Recovery exposes aggregate coverage plus on-demand ledger queries. Filesystem Evidence uses a v2 manifest and streamed NDJSON segment with integrity hashes; successful source accounting uses population evidence while failures remain detailed. Generic typed ordering keys are materialized and indexed at workspace ingest. Evidence canonicalization is explicitly v2 for the population-evidence policy. See `docs/adr/0017-persisted-verification-ledgers-and-streaming-evidence.md`.

### D-027 — Derive generic Verification worksets from rule descriptors
Status: Accepted for the PS-0.10D plan-driven workset slice; milestone remains in progress

Both projection-backed and external-target Verification derive the same deterministic execution plan before workspace creation. Provider descriptors bind fields to side, semantic type, and key role; selector identity is preserved from the graph. The plan drives projected source/expected/actual workspace values and comparison hashes, typed key writes, and deduplicated shared index construction. Undeclared field/key accesses fail closed. The plan and projections are operational and excluded from semantic fingerprints. Partition safety and global-finalization remain conservative defaults; concurrency and partition execution are deferred. The 50k candidate reduced JSON/workspace scratch but was 0.59% slower, so the throughput advancement gate is unmet. See [ADR-0020](adr/0020-descriptor-driven-verification-execution-plans.md).

### D-028 - Stage logical ledger facts and publish bounded durable sets
Status: Experimental PS-0.10D candidate; no acceptance or throughput claim

Separate disposable logical-fact staging from finalized receipt-backed ledgers, derive association/scope tables set-wise, preserve the final schema and semantic fingerprint v1, and publish Complete only after count/fingerprint/coverage/integrity validation. Failure/cancellation cannot expose a complete receipt. Durable WAL/FULL publication strengthens the existing synchronization contract. Existing reduced-field worksets remain enabled only as a measured scratch tradeoff and are unchanged during this experiment. See [ADR-0021](adr/0021-staged-verification-ledger-finalization.md).

### D-029 - Reuse immutable checkpoint-derived expected state within an orchestration
Status: Experimental cumulative PS-0.10D candidate; no acceptance claim

An optional benchmark-lifetime scope keys read-only source/expected worksets by exact checkpoint/configuration/graph/runtime/field-key requirements, validates publication and content integrity, and attaches them to fresh actual/journal overlays. Actual observations, findings, ledgers and Recovery remain run-specific; cache identity is excluded from semantic Evidence. Retain the ledger and scratch foundations cumulatively. See [ADR-0022](adr/0022-immutable-expected-verification-worksets.md).

### D-030 - Test sequential actual/journal partition-local scratch
Status: Experimental PS-0.10D locality investigation

Plan-owned versioned SHA-256 routes streamed actual/journal facts to independent sequential scratch writers at operational counts 1/4/8. Global read views preserve rule/accounting/lineage semantics; expected cache and canonical ledger remain singular and independently integrity-validated. All existing rules remain globally evaluated pending provider partition-safety proof. Smaller DBs are not a performance claim; measure query/flush overhead and max/summed scratch. See [ADR-0023](adr/0023-sequential-partition-local-verification-scratch.md).

### D-031 - Execute only proven identity-keyed rules locally
Status: Experimental PS-0.10D continuation; sequential local-rule path rejected as a throughput advancement

The sequential local-rule probes completed at 100k/200k and failed their non-regression gates, so those rules remain benchmark-only and `GlobalRuleReference` remains the runtime and benchmark default. The v2 logical target-identity hash colocates duplicate identities; the canonical immutable expected cache remains singular and gains only an operational fixed-eight-bucket index. Generic target presence/attribute summaries are merged globally; generic unexpected-target/uniqueness findings are locally unioned by stable semantic key. Pension member presence, uniqueness and status are local only when the configured/default business key exactly matches the graph's single-field target selector identity; otherwise the planner resolves Global with a reason. Source disposition, target lineage, Pension timelines/relationships/aggregates and durable ledger finalization remain Global. The later explicit assignment authorizes bounded scratch-write workers only, as recorded in D-032; Medium/Large, PS-0.10E, backend replacement and production execution/rollback remain out of scope. See [ADR-0023](adr/0023-sequential-partition-local-verification-scratch.md).

### D-032 - Bound concurrency to independent partition scratch writes
Status: Experimental PS-0.10D continuation; worker-saturation 100k/200k gates and authorized workers=8 Medium gate passed; milestone remains In Progress

The explicit follow-on rejects sequential local-rule evaluation as a throughput advancement and measures bounded workers only for actual-target/journal scratch writes and partition-local index builds. Preserve `GlobalRuleReference`, the shared immutable expected workset, global rules, and one canonical ledger with global finalization. At 100k/eight partitions, workers=2 processed in 1,513.969 s, workers=4 in 1,336.970 s, workers=6 in 1,124.355 s and workers=8 in 1,086.520 s. The workers=8 200k run processed in 2,451.565 s with 1,910.802 s exclusive Verification and passed exact assertions with zero SQLite busy exceptions/retries. Its normalized 100k-to-200k penalties (+12.82% processing / +18.42% Verification) are materially worse than workers=4 (+0.33% / +4.66%); keep that scaling tradeoff visible. The original workers=4 Medium gate missed, but the one conditionally authorized workers=8 Medium rerun passed, as recorded in D-034. The capacity-gated 1.5M Large run passed (see D-035); a second Large, 2M+, PS-0.10E, an alternate backend, and production execution/rollback remain unauthorized. See [PS-0.10D experiment results](PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md#bounded-partition-scratch-write-concurrency-continuation) and [ADR-0023](adr/0023-sequential-partition-local-verification-scratch.md).

### D-033 - Authoritative Medium throughput gate
Status: Historical initial workers=4 PS-0.10D Medium run; correctness passed, throughput gate failed; superseded by D-034 for the final Medium gate

The explicitly authorized physical Medium run used p8/workers=4 and `GlobalRuleReference`, with 411,000 generated records, 424,750 checkpoint artifacts, and 429,750 projected targets. The full E2E pipeline passed 2/2, zero failures/skips, and exact 149/0 findings, zero corrected accounting/lineage gaps, Recovery `PASSED`, Recovery rehearsal `PASSED`, and `QUALIFIED`. ProofShift processing was 6,794.325 s against the fixed 6,617.191 s limit, a 177.134 s (2.68%) miss; therefore this workers=4 run failed its Medium throughput gate. Root interval union reconciles within 0.769 ms and named interval coverage is 97.6209%. Peak RSS is 1,101,176,832 B (+0.93% versus accepted); expected-cache, workspace, partition, ledger, staging and WAL peaks are in the report/JSON artifact. The four focused post-run suites passed Engine 4/4, Verification 43/43, Evidence 5/5, Recovery 8/8. This historical stop was superseded by D-034's one conditionally authorized workers=8 rerun; no further Medium run is authorized. See [Medium benchmark artifact](PS0_10D_MEDIUM_BENCHMARK.json), [experiment report](PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md#authoritative-medium-throughput-gate), and [ADR-0023](adr/0023-sequential-partition-local-verification-scratch.md).

### D-034 - Authorized workers=8 Medium saturation gate
Status: Experimental PS-0.10D; workers=8 Medium correctness and formal/strong throughput gates passed; milestone remains In Progress

Following the historical workers=4 miss, the conditional continuation measured 100k workers=6/8, ran 200k at workers=8, and authorized exactly one Medium p8/workers=8 rerun after the assigned projection thresholds passed. The frozen `GlobalRuleReference` pipeline completed the 411,000-record Medium workload in 5,574.083 s processing (1,043.108 s below the fixed 6,617.191 s limit and 725.917 s below the 6,300 s strong-result line); scenario total was 6,089.439 s. EndToEnd passed 2/2 with zero failures/skips. Exact 149/0 findings, zero corrected accounting/lineage gaps, independent external verification, false-Reverse rejection, Recovery/rehearsal `PASSED` and corrected `QUALIFIED` were preserved. Complete exclusive `kind=4` Verification was 4,423.960 s; named interval coverage was 98.515%. Worker lanes reached eight with zero SQLite busy exceptions/retries. Peak RSS was 1,119,571,968 B; aggregate temporary-workspace peak and final canonical ledger matched workers=4. PS-0.10D remains In Progress: no Large, another Medium, PS-0.10E, alternate backend or production migration/rollback is authorized. See [structured benchmark result](PS0_10D_MEDIUM_BENCHMARK.json), [experiment report](PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md#authorized-workers8-medium-rerun), and [ADR-0023](adr/0023-sequential-partition-local-verification-scratch.md).

### D-035 - Large full-pipeline gate and scratch-capacity preflight
Status: Experimental PS-0.10D; Large full-pipeline gate passed; milestone remains In Progress

A reusable, path-bound scratch-capacity preflight (scripts/estimate-pension-scratch.ps1; 25% margin; measured category growth; independent high-water values never summed) now gates the 1.5M `large-acceptance` scale, replacing the ad hoc 20 GiB allowance. The single authorized p8/workers=8 `GlobalRuleReference` run processed exactly 1,500,000 generated source records (1,550,182 checkpoint artifacts, 1,568,430 targets) through the full physical pipeline: 2/2 tests, 0 failures/skips, exact 149/0, zero unaccounted/unexplained, Recovery/rehearsal passed, corrected `QUALIFIED`, false-Reverse not qualified, independent external observation. Processing 28,143.783 s (complete exclusive Verification 23,186.709 s), 97.218% named-interval coverage, zero SQLite busy/retries/worker failures, final ledger 47,213,477,888 B, same-sample scratch 82.46 GiB against a 107.84 GiB estimate. Normalized processing rose 38% versus Medium: the curve is superlinear and no SLA, constant-memory or linear-scaling claim is made. Peak RSS (3.49 GB) arose during harness fixture load. See [Large benchmark record](PS0_10D_LARGE_BENCHMARK.json) and [report](PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md#authorized-large-full-pipeline-run). No second Large, 2M+, PS-0.10E or production execution/rollback is authorized.
