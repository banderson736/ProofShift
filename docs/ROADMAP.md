# ProofShift Roadmap

## Development philosophy

Build vertical slices through the architecture. Do not implement all abstractions before proving one end-to-end path.

Every milestone must:

- have deterministic acceptance criteria;
- include tests;
- update docs/status;
- preserve core dependency rules;
- avoid unrelated feature expansion;
- produce evidence, not only a return value.

## PS-0 — Migration Assurance Foundation

### PS-0.1 Domain foundation

Implement domain concepts and invariants without database dependencies:

- strong IDs;
- Project/System/Endpoint;
- Artifact/RecordEnvelope/ValueNode;
- temporal/provenance metadata;
- Snapshot;
- MigrationPlan/Graph/Node/Edge;
- Transformation and recovery definitions;
- ArtifactDisposition;
- Lineage;
- Evidence;
- Run/runtime fingerprint;
- connector/domain-pack abstractions only where needed by the model.

Acceptance focus:

- immutability;
- value semantics;
- source accounting invariants;
- lineage invariants;
- recovery metadata rules;
- deterministic graph hashing strategy defined/tested.

### PS-0.2 Configuration

Status: Implemented. Version-1 configuration loading, validation, canonicalization, SHA-256 fingerprinting, and `proofshift validate` are covered by focused tests. Migration graph semantics remain for PS-0.3.

Implement:

- YAML loading;
- multi-file configuration;
- environment/secret references;
- schema and semantic validation;
- reference resolution;
- canonical configuration hashing;
- actionable diagnostics.

CLI: `proofshift validate`.

### PS-0.3 Migration graph compiler/validator

Status: Implemented. Graph-v1 DTOs compile to immutable domain graphs with endpoint/reference, selector, cardinality, recovery, reachability, and cycle validation. `proofshift-graph-canonical-v1` hashing and `proofshift plan` are covered by focused tests. No connector access or migration execution is included.

Parse config into domain graph and validate:

- no dangling nodes;
- valid systems/endpoints;
- valid edge types;
- required recovery metadata;
- no prohibited graph topology;
- stable graph hash.

CLI: `proofshift plan`.

### PS-0.4 First connectors

Status: Accepted. Read-only PostgreSQL, SQL Server, filesystem, and CSV connectors are registered at the CLI composition root and exercised through `proofshift inspect`. Temporal/binary/CSV hardening is covered by the full Docker-backed [GitHub Actions run](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267): 67 passed, 0 failed, 0 skipped, including PostgreSQL/SQL Server Testcontainers and Linux symlink coverage.

Implemented:

- PostgreSQL source inspection/reader;
- SQL Server source inspection/reader;
- filesystem artifact inspection/reader;
- CSV source inspection/reader.

This milestone does not create snapshots, write targets, or execute graph operations.

No generalized feature breadth beyond the first member slice.

### PS-0.5 First vertical slice

Status: Accepted. The PS-0.5 Docker-backed [GitHub Actions run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298) passed all 82 tests with zero failures and zero skips. The slice adds a shadow-only target contract, PostgreSQL schema-per-run and filesystem directory-per-run connectors, deterministic supported transformations, a Projection Journal, read-back fingerprinting, and `proofshift project`. The 10-member synthetic fixture proves the unknown status fails with retained partial output and a corrected rerun succeeds in isolated shadow state.

Scenario:

```text
SQL Server MEMBER
       ↓
member transformation
       ↓
PostgreSQL participant + member_status
   ↓
Projection Journal + read-back fingerprint
```

The synthetic integration scenario uses 10 deterministic members and seeds:

- one missing member;
- one invalid status transformation;
- one capitalization-only difference that should normalize successfully.

Exact assertions required.

### PS-0.6 Source checkpoints and reproducible snapshots

Status: Accepted. Docker-backed [GitHub Actions run 37180351598](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with 0 failures and 0 skips. Storage, typed codecs, endpoint fingerprints, filesystem/CSV observed-read checks, relational transaction captures, checkpoint-backed source streams, and CLI `snapshot`/`project --checkpoint` are implemented. The Docker pension scenario proves source mutation/offline replay with the same projection fingerprint. The migration graph compiler is the authority for the exact source-node set.

Acceptance requires:

- deterministic, versioned streaming segments and content-addressed streamed binary blobs;
- endpoint-level consistency guarantees, capture times/window, and `CrossSystemAtomic: false` for mixed SQL/files/CSV;
- complete-only replay; failed/cancelled checkpoints remain non-replayable;
- manifest, segment, record, source-set, and binary integrity verification before target preparation;
- exact configuration/graph/source-node/selector match and no live-source fallback in checkpoint mode;
- offline replay with source containers stopped and CSV/files/database inputs modified after capture;
- full pension modernization scenario with stable read-back projection fingerprint;
- remote Docker-backed CI acceptance with no required skips.

### PS-0.7 Evidence graph

Status: Accepted. Docker-backed [GitHub Actions run 37185122084, job 111385343893](https://github.com/banderson736/ProofShift/actions/runs/37185122084/job/111385343893) passed all 96 tests with 0 failures and 0 skips. The accepted slice includes checkpoint/projection-bound semantic verification, generic and Pension rule providers, graph-scoped dispositions and lineage, hash-only SQLite working sets, physical shadow read-back comparison, deterministic Evidence Graphs, filesystem integrity storage, and CLI `verify`/`evidence` commands.

Acceptance requires:

- strict configuration, graph, checkpoint, projection manifest, selector, source coverage, journal, and connector-version binding; incomplete or ambiguous inputs fail closed;
- derive expected targets only by replaying the exact complete checkpoint through the configured graph transformations, and independently read every physical shadow target;
- detect missing, unexpected, duplicate, and changed targets; compare normalized attributes without placing raw record values or secrets in normal evidence output;
- exactly account for each graph-scoped source artifact and provide graph-scoped lineage for each physical target, including split outputs with colliding ArtifactIds;
- produce a deterministic, versioned evidence graph linked to rules, checkpoint, projection, edges, and graph-scoped artifacts, with tamper-detecting persistence;
- exact seeded-defect assertions and a clean repaired replay; changing target state or rules must change the relevant fingerprint;
- cancellation and rule exceptions must not produce a completed verification result;
- full solution tests and Docker-backed CI pass with no required skips.

### PS-0.8 Recovery

Status: Accepted. Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed the full solution test suite: 103 passed, 0 failed, and 0 skipped. PS-0.8 consumes the completed PS-0.7 checkpoint/projection/verification/evidence chain and adds a separate recovery assessment, recovery plan, target recovery checkpoints, shadow-only rehearsal, integrity-checked recovery artifacts, and first-class dry-run qualification.

The implementation supports deterministic Reverse loss analysis, Restore checkpoint validation, registered compensators, irreversible policy denial by default, PostgreSQL schema-copy and filesystem-run-root recovery checkpoints, semantic-type coverage metrics, and CLI `recovery`/`dry-run` commands. Source checkpoints remain source inputs and are never treated as target rollback backups. Recovery evidence is separate from finalized PS-0.7 evidence. Production migration and rollback remain out of scope.

Acceptance requires:

- every executed material edge and every graph-scoped target artifact has a recovery assessment;
- missing strategy/capability, corrupted recovery checkpoint, false Reverse, prohibited irreversible operation, and stale physical target all fail qualification;
- the deterministic pension scenario proves two false-Reverse strategies can follow successful projection and verification yet fail qualification;
- controlled PostgreSQL and filesystem shadow mutations are rehearsed and restored; failed rehearsal prevents qualification;
- recovery policy, assessment, rehearsal, evidence, and dry-run semantic fingerprints are deterministic and versioned;
- `proofshift dry-run` and `proofshift recovery` expose safe human/JSON reports and persisted recovery artifacts detect tampering;
- the clean pension fixture returns `QUALIFIED DRY RUN`; fault scenarios return `NOT QUALIFIED` with stable reasons;
- full solution validation and remote Docker-backed CI pass with no required skips.

PS-0.8, PS-0.9, and PS-0.10A Scale Baseline & Hot-Path Hardening are accepted. Do not begin PS-0.10B/C or connector/domain expansion without a new explicit assignment.

### PS-0.9 Public Pension Assurance Vertical

Status: Accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips. The vertical extends `ProofShift.Packs.Pension` with versioned semantic rules, typed source/expected/actual record streams in the generic disk-backed Verification workspace, a deterministic clean-data generator and separate v1 defect injector, evidence-backed `report`/`compare` commands, and a complete local fast-scale heterogeneous physical scenario.

Implemented and locally validated:

- Pension-owned rules cover member presence/uniqueness/status, interval/event employment timelines, contribution accounting/totals, service-credit totals, beneficiary relationships, retirement-election mapping, benefit-payment accounting/totals, document identity/hash/ownership, and code transformations;
- generator `proofshift-pension-generator-v1`, target model `proofshift-pension-target-model-v1`, and defect set `proofshift-pension-defects-v1` are deterministic and versioned; fast and large lazy scales are defined;
- exact rule-level assertions detect the 17 implemented artifact-level defect categories, clean timeline event/interval equivalence passes, and a regrouped service-credit representation passes on equal total;
- direct external-target rule tests use the registered Pension provider and create an Evidence Graph without running Projection;
- human/JSON report and run-comparison commands read persisted evidence, recovery artifacts, and verify run/fingerprint/configuration/graph binding;
- Docker-backed existing PS-0.8 CLI/recovery scenarios still pass after pack expansion.
- The integrated fast corpus materializes 7,945 relational SQL rows, 275 CSV metadata rows, and 275 filesystem payloads; it checkpoints 8,495 artifacts and projects 8,595 PostgreSQL/filesystem targets.
- The projected defective run records exactly 149 discrepancies and fails qualification; the corrected run records zero discrepancies and qualifies. Both persist Evidence/Recovery artifacts and human/machine reports; the CLI comparison resolves all 149 defects and attributes changes to source/checkpoint and graph.
- The integrated fast-scale run completed in 396,017 ms with 519,651,328 bytes peak working set and zero temporary workspace bytes after cleanup. The separate large benchmark remains generator-only.
- Docker-backed GitHub Actions run 37238973286 passed the full solution suite: 113 passed, 0 failed, 0 skipped.

PS-0.9 acceptance is complete. External target lineage remains graph-derived expected lineage, not observed vendor execution history.

### PS-0.10A Scale Baseline & Hot-Path Hardening

Status: Accepted after Docker-backed [run 37284331965, job 111679522042](https://github.com/banderson736/ProofShift/actions/runs/37284331965/job/111679522042) passed 123 tests with 0 failures and 0 skips. PS-0.9 semantics and the exact 149-defect / corrected-zero result are preserved.

Completed and locally validated slices:

- Generic performance-stage records and reports distinguish environment setup from ProofShift runtime.
- PostgreSQL writes and the Projection Journal use bounded batch lifecycles; Pending precedes each transaction and Produced follows commit. Duplicate batches roll back data and identity rows.
- Disposable Verification SQLite uses WAL/NORMAL and bounded transactions.
- Verification stores graph-scoped source dispositions, target lineage, and journals in an indexed per-run SQLite ledger. Successful result models carry a ledger receipt, not full source/disposition/lineage/journal collections.
- Recovery reopens the ledger, streams edge scopes and lineages, retains counts/semantic summaries, and supports on-demand artifact coverage reads.
- Ordering fields are materialized as typed normalized SQLite keys and read through indexed joins; relational `ReadOptions` no longer advertises an ignored batch size.
- Evidence store v2 writes a hashed NDJSON segment and completion manifest; CLI and pension reporting consume a record stream. Successful source accounting is aggregated while failure findings remain artifact-specific. Evidence canonicalization is explicitly v2.
- SQL Server checkpoint consistency defaults to Observed and requires explicit isolation for transaction-consistent capture; requested/effective strategies and permitted downgrades are in the v2 checkpoint manifest.

Final Fast passed in 150.5 seconds total / 139.0 seconds historical runtime, with 209.8 MB workload peak RSS and a corrected 174.7 MB workspace peak. The recovered WAL baseline was 117.1 seconds total / 104.0 seconds historical runtime / 567.3 MB peak RSS; the pre-investigation run was 229.1 / 214.7 seconds. The historical runtime metric includes Docker startup; corrected startup-free measurements are documented separately. Prepared workspace commands, deferred secondary-index construction, and an identity-index coverage fix recovered measured avoidable costs.

Medium completed successfully in 3h 55m: 411,000 generated source records, 424,750 checkpoint artifacts, 429,750 corrected projected/observed targets, exact 149/0 discrepancies and corrected QUALIFIED. Workload RSS peaked at 1.076 GB with stage-local plateaus near 700-720 MB. Verification remains above-linear measured runtime at this tier; scratch rows and disk sizes grow approximately with population, and the known repeated node-prefix coverage scan is fixed. See the full stage/scratch/memory report in `docs/COPILOT_PS0_10A_SCALE_HARDENING.md`.

Restore/build passed, and the final local suite completed with 121 passed, 0 failed, and 2 Windows symlink-capability skips. Remote Docker-backed GitHub Actions passed all 123 tests with 0 failures and 0 skips. All PS-0.10A acceptance gates are complete; measured Verification runtime/scratch costs remain explicit limitations, not a production SLA. The separate generator-only large scale is not a substitute for this completed Medium full-pipeline run.

Do not add production migration/rollback, SaaS, UI, AI, FHIR, Oracle, DB2, Kubernetes, or unrelated platform features. PS-0.10B is now explicitly assigned below; PS-0.10C remains deferred.

### PS-0.10B Configuration & Authoring UX

Status: Accepted after Docker-backed [run 37353339103, job 111909408991](https://github.com/banderson736/ProofShift/actions/runs/37353339103/job/111909408991) verified final commit `625ca84` with 143 passed, 0 failed, 0 skipped. Preserve Accepted PS-0.10A; do not continue unrelated performance optimization or expand connectors/domain packs.

The user-facing workflow is init, physical discovery, deterministic structural scaffold, review/approval, validation, effective explanation, then dry-run. Discovery must be read-only and connector-neutral, never inferred business semantics. Scaffold suggestions are not approval. Typed rule-document version 2 and provider-owned descriptors must drive validation, schemas, rule descriptions and explanations; legacy documents retain their established interpretation or an explicit migration path.

Required gates: explicit pack/version resolution; generic/Pension init templates; actual SQL Server/PostgreSQL/CSV/filesystem discovery and versioned fingerprint/diff artifacts; deterministic scaffold with unresolved approvals; CSV mapping import through the normal compiler; project/rule/mapping explain and capabilities; strict validation and secret-safe source-located diagnostics; reusable code-map authoring; customer-like committed Pension configuration and workflow preserving exact 149/0/QUALIFIED; tutorials/editor schema support; full local and zero-required-skip Docker-backed remote CI. No PS-0.10C, new domain pack, AI authoring, IDE extension, or production migration/rollback is in scope.

The implementation includes immutable structured rules/descriptors, exact pack versions, init, physical discovery/diff, review-state scaffold/CSV import, named maps through normal graph parsing, effective explain/config inspection, strict policies, and committed full Pension configuration. Docker discovery and the configured physical Pension regression passed with exact 149/0/QUALIFIED semantics. Full local tests: 143 total, 141 passed, 0 failed, 2 Windows capability skips; remote CI passed all 143 without failures/skips. See ADR-0018 and docs/CONFIGURATION_AUTHORING.md. PS-0.10B gates are complete; PS-0.10C/new connectors/packs and production migration/rollback require a new explicit assignment.

### PS-0.10C Enterprise Connector & Data-Format Coverage

Status: Accepted after Docker-backed GitHub Actions [run 37416501703](https://github.com/banderson736/ProofShift/actions/runs/37416501703) passed `build-and-test`, `db2-integration`, and `oracle-integration` with 185 passed, 0 failed, 0 skipped. Preserve Accepted PS-0.10A/B and exact Pension 149/0/QUALIFIED semantics. No new domain packs, UI, AI, production migration/rollback or PS-0.10D.

Required coverage: read-only Oracle and IBM Db2 observation/discovery/checkpoints; fixed-width, JSON, NDJSON and XML formats; independently observed ordinary targets without shadow-write authority; mixed-source checkpoint consistency and offline replay; truthful provider-owned capabilities and reproducible authoring; real provider/type/streaming tests and zero-required-skip remote acceptance.

The implementation separates observation from shadow writing and explicit Recovery capability (ADR-0019). Db2 LUW Community 11.5.9.0 passed a real local integration: catalog discovery and drift, exact/temporal values, LOB hashing/reopen, SELECT-only target observation with denied DML, and a mixed Db2/fixed-width/NDJSON/filesystem-binary checkpoint replay after the source and files were stopped or removed. Oracle Database Free 26ai built from the official pinned `oracle/docker-images` source passed locally: scoped discovery, exact NUMBER and temporal fidelity, CLOB/BLOB streaming/reopen, SELECT-only observation with denied DML, and offline checkpoint replay. Independently observed cross-provider cases cover SQL Server→Oracle, Oracle→PostgreSQL, SQL Server→Db2 and Db2→PostgreSQL. Connector-owned JSON Schemas and strict validation cover closed properties, required values, selector kinds, enums/ranges and fixed-width boundaries; XML/fixed-width discovery, encoding, cancellation, and provider-error redaction have focused coverage. Structured-file tests pass 21/21. Real reader throughput and ProofShift-process RSS measurements for Oracle, Db2, fixed-width, NDJSON and XML are recorded in [Connector Test Runtimes](CONNECTOR_TEST_RUNTIMES.md); they are diagnostic only and show no evidence of whole-input materialization at measured scales. The final full local solution run passed 183 tests with 0 failures and 2 Windows symlink-capability skips (185 total); Oracle and Db2 each passed 2 tests with 0 skips. Remote run 37416501703 passed all required jobs with 185 passed, 0 failed and 0 skipped. The tested code commits are `739b942007dbd87a71cf3d130b0056aeebca55aa` and `eefcae471877b6f0218f60b5ac10b34ec63ef219`.

Oracle CI builds from the pinned official source and Free download; vendor servers remain test-only and are not redistributed. The bounded-concurrency study passed its 100k/200k gates. The initial p8/workers=4 Medium run missed the 6,617.191 s limit at 6,794.325 s; the single authorized p8/workers=8 rerun passed exact correctness at 5,574.083 s, below both the formal limit and the 6,300 s strong-result threshold. PS-0.10D is Accepted (candidate `6d52272`, GitHub Actions run 38014590907, merged as `091e7e3`). The single capacity-gated 1.5M physical Large run passed at exactly 1,500,000 generated source records (exact 149/0, `QUALIFIED`, 28,143.783 s processing; normalized cost +38% versus Medium, so superlinear; not an SLA). Do not run a second Large or 2M+, rerun/tune Medium, start PS-0.10E, or add production execution/rollback without a new explicit assignment. See the [Large benchmark record](PS0_10D_LARGE_BENCHMARK.json), [Medium benchmark record](PS0_10D_MEDIUM_BENCHMARK.json), [PS-0.10D report](PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md#authorized-workers8-medium-rerun), and ADR-0023.

## Commercial validation gate after PS-0 / during PS-0

Do not wait for a polished enterprise product.

Use PS-0 to demonstrate:

- full source accounting;
- target lineage;
- history preservation;
- financial reconciliation;
- repeated dry runs;
- recovery analysis.

Then test with pension/IV&V/data-conversion specialists.

Primary interview questions should focus on the last migration they actually performed, existing tooling, reconciliation evidence, repeated conversion cycles, acceptance criteria, and manual effort—not hypothetical willingness to buy.

## Post-PS-0 provisional roadmap

The exact sequence is market-driven.

Likely areas:

### PS-1 generalized reconciliation/mapping
- composite identity;
- one-to-many/many-to-one;
- richer transformations;
- expected exceptions;
- custom lookup tables;
- filtering;
- partitions;
- persistent run/evidence store.

### PS-2 API / evidence explorer
- service API;
- run explorer;
- exception/lineage/evidence UI;
- report generation.

### PS-3 connector expansion
Prioritize market evidence, likely:
- CSV/fixed-width;
- files/object storage;
- Oracle;
- DB2/IBM i;
- vendor REST/API;
- domain-specific adapters.

### PS-4 scale/distributed execution
- partitions;
- resumability;
- worker coordination;
- checkpointing;
- incremental reruns/impact analysis.

### PS-5 domain-pack expansion
- mature Pension Pack;
- Utility CIS/Billing Pack;
- Justice/Case Management Pack;
- Healthcare/FHIR Pack later;
- ERP/HCM only when commercially justified.

### PS-6 production orchestration
Optional migration execution while retaining independent verification-core semantics.

### PS-7 enterprise deployment
- customer-hosted deployment packaging;
- authentication/authorization;
- secrets providers;
- signed evidence packages;
- enterprise reporting/retention.

## Future RampForge reuse

Do not implement RampForge in ProofShift milestones, but preserve reusable primitives:

```text
Observation
   ↓
Normalization
   ↓
Rule Evaluation
   ↓
Evidence
   ↓
Auditable Decision
```

Likely shared concepts:

- evidence graph;
- rules;
- provenance;
- historical runs;
- immutable configurations/hashes;
- connectors/collectors;
- reporting.
