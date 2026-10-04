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

Status: Accepted. The PS-0.5 Docker-backed [GitHub Actions run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298) passed all 82 tests with zero failures and zero skips. The slice adds a shadow-only target contract, PostgreSQL schema-per-run and filesystem directory-per-run connectors, deterministic supported transformations, a Projection Journal, read-back fingerprinting, and `proofshift project`. The 10-member synthetic fixture proves the unknown status fails with retained partial output and a corrected rerun succeeds in isolated shadow state. PS-0.6 is not started.

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

### PS-0.6 Evidence graph

Ensure source observation → transformation → target observation → comparison → decision can be traced.

Add evidence query/serialization.

### PS-0.7 Recovery

Add:

- snapshot manifest;
- recovery classification;
- recovery policy validation;
- readiness report.

### PS-0.8 Historical data

Add `EMPLOYMENT_HISTORY` with semantic timeline comparison.

Demonstrate structurally different but semantically equivalent history.

### PS-0.9 Financial data

Add:

- CONTRIBUTION;
- BENEFIT_PAYMENT;
- aggregate financial reconciliation;
- tolerance semantics.

### PS-0.10 Relationships

Add BENEFICIARY and correct/incorrect member relationships.

### PS-0.11 Files/documents

Add filesystem/object abstraction and document accounting/provenance.

### PS-0.12 Full synthetic scenario

Scale generator; inject full deterministic defect manifest; produce final dry-run/readiness report and machine-readable evidence.

PS-0 is not complete until every deliberately introduced defect is detected with no false failures among deliberately correct transformations.

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
