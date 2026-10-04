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

PS-0.8 is accepted. PS-0.9 Public Pension Assurance Vertical is the active assignment. Do not begin PS-0.10 without an explicit new assignment.

### PS-0.9 Public Pension Assurance Vertical

Status: In progress. The vertical extends `ProofShift.Packs.Pension` with versioned semantic rules, typed source/expected/actual record streams in the generic disk-backed Verification workspace, a deterministic clean-data generator and separate v1 defect injector, evidence-backed `report`/`compare` commands, and a CSV fixture generator.

Implemented and locally validated:

- Pension-owned rules cover member presence/uniqueness/status, interval/event employment timelines, contribution accounting/totals, service-credit totals, beneficiary relationships, retirement-election mapping, benefit-payment accounting/totals, document identity/hash/ownership, and code transformations;
- generator `proofshift-pension-generator-v1`, target model `proofshift-pension-target-model-v1`, and defect set `proofshift-pension-defects-v1` are deterministic and versioned; fast and large lazy scales are defined;
- exact rule-level assertions detect the 17 implemented artifact-level defect categories, clean timeline event/interval equivalence passes, and a regrouped service-credit representation passes on equal total;
- direct external-target rule tests use the registered Pension provider and create an Evidence Graph without running Projection;
- human/JSON report and run-comparison commands read persisted evidence, recovery artifacts, and verify run/fingerprint/configuration/graph binding;
- Docker-backed existing PS-0.8 CLI/recovery scenarios still pass after pack expansion.

Acceptance still requires:

- connect generated pension data to a complete multi-source SQL Server/filesystem/CSV to structurally different PostgreSQL/document shadow graph and run defective plus corrected end-to-end dry runs;
- represent both false-Reverse cases as actual configured graph edges and assert the Recovery engine rejects them after successful Projection/Verification;
- exact report counts for all 18 categories, including executable recovery findings, plus corrected zero counts, source accounting, target lineage, recovery readiness, and Qualified status;
- exercise report redaction, evidence-backed exception drill-down, resolved/introduced run comparison, and difference attribution across distinct persisted defective/corrected runs;
- run full service-level Verification against externally populated target state without depending on Projection output;
- add an opt-in dataset/throughput/duration/memory benchmark and verify the large generator without running large benchmarks in ordinary CI;
- update the full pension demo and buyer/operator flow, complete schema fixtures, and run all required remote Docker-backed CI with no skips.

Do not add production migration/rollback, SaaS, UI, AI, FHIR, Oracle, DB2, Kubernetes, or unrelated platform features. Do not begin PS-0.10 until PS-0.9 is accepted.

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
