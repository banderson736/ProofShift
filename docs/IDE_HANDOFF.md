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

PS-0.4 was accepted after [GitHub Actions run 37173803105](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267) passed all 67 tests with zero failures and zero skips. PS-0.5 Shadow Projection was accepted after [run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298) passed all 82 tests with zero failures and zero skips. PS-0.6 Source Checkpoints & Reproducible Snapshots was accepted after Docker-backed [run 37180351598, job 111371493382](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with zero failures and zero skips. The current assignment is PS-0.7 Semantic Verification & Evidence Graph. Do not begin PS-0.8 or later.

PS-0.6 uses a separate `ProofShift.Snapshots` assembly and a generic connector-neutral source stream provider. PostgreSQL/SQL Server capture guarantees are endpoint-level only; filesystem and CSV are observed reads with before/after drift checks. Mixed-source checkpoints explicitly report `CrossSystemAtomic: false`. Checkpoint replay matches configuration hash, graph hash, exact source-node coverage, endpoint identity, and selectors; it never falls back to live reads.

PS-0.7 is active and must verify from the exact checkpoint and completed projection manifest, independently read physical shadow state, preserve graph-node scope for split artifacts, and emit integrity-checked deterministic evidence. Keep raw values and secrets out of normal evidence output. PS-0.7 is not accepted until the seeded defect/clean replay scenario, full suite, and Docker-backed CI pass without required skips.

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
