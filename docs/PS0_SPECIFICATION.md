# ProofShift PS-0 — Migration Assurance Foundation

## Milestone purpose

PS-0 proves the foundational architecture of ProofShift using a deterministic synthetic public-pension modernization scenario distributed across multiple source/target storage types.

It must prove that ProofShift can:

1. model a migration as a versioned graph;
2. snapshot the source logically;
3. generate a real isolated shadow target;
4. preserve current and historical semantics;
5. account for every source artifact;
6. provide provenance for every target artifact;
7. verify attributes, relationships, timelines, aggregates, and financial invariants;
8. create graph-addressable evidence explaining all material conclusions;
9. analyze reversibility/recovery before production;
10. deterministically detect every deliberately injected defect.

## Demonstration topology

### Legacy pension system

SQL Server:

- MEMBER
- EMPLOYMENT_HISTORY
- CONTRIBUTION
- SERVICE_CREDIT
- BENEFICIARY
- RETIREMENT_ELECTION
- BENEFIT_PAYMENT

File storage:

- member statements
- supplemental CSV extracts
- synthetic documents
- document metadata
- historical exports

### Shadow target

PostgreSQL:

- participant
- employment_period
- contribution_transaction
- service_period
- beneficiary_relationship
- retirement_election
- benefit_payment

Object-storage abstraction:

- migrated documents
- historical statements
- archived source artifacts

Filesystem-backed storage or MinIO is sufficient for PS-0.

## Synthetic scale target

The deterministic generator should support at least:

```text
Members                         100,000
Employment periods             ~300,000
Contribution transactions    ~5,000,000
Service-credit periods         ~350,000
Beneficiary relationships      ~160,000
Retirement elections            ~35,000
Benefit payments             ~2,000,000
Documents                      ~250,000
```

Early vertical slices may run with 10–1,000 records. The generator must be capable of the larger scale without changing semantics.

Suggested deterministic seed: `20261003`.

## Seeded defects

PS-0's final scenario should include exact known failures such as:

### Current state
- 7 missing members
- 4 duplicate members
- 3 incorrect member statuses

### History
- 12 missing employment periods
- 8 incorrect employment dates
- 17 incorrect service-credit totals

### Financial
- 39 missing contributions
- 8 duplicate contributions
- 6 incorrect contribution amounts
- 11 benefit-payment discrepancies

### Relationships
- 6 broken beneficiary relationships
- 2 beneficiaries assigned to wrong members

### Transformations
- 3 incorrect retirement-election mappings
- 5 incorrect code transformations

### Documents/history
- 9 missing documents
- 4 documents associated with wrong members
- 3 missing historical exports

### Recovery
- 2 transformations incorrectly configured as reversible despite information loss

Exact numbers may be refined once the generator is implemented, but the scenario must maintain an explicit defect manifest and exact assertions.

## Source-artifact dispositions

Every source artifact receives exactly one disposition:

- MIGRATED
- TRANSFORMED
- DERIVED
- ARCHIVED
- EXCLUDED
- SUPERSEDED
- FAILED
- UNACCOUNTED

`UNACCOUNTED > 0` causes readiness failure.

## Target lineage

Every target artifact must identify contributing source artifacts and the migration edge path. `target_without_lineage > 0` causes readiness failure.

## Historical semantic equivalence

PS-0 must include at least one case where legacy and target history are structurally different but semantically equivalent. Verification should pass when the meaning is preserved.

## Snapshot requirements

PS-0 snapshots:

- SQL Server logical database state / metadata / counts / schema fingerprint and reproducibility markers;
- file-storage manifest including relative path, size, and SHA-256.

Snapshots are immutable run inputs.

## Migration-plan requirements

A plan binds:

- source snapshot IDs;
- systems/storage endpoints;
- migration graph version/hash;
- mapping/transform versions;
- domain-pack version;
- verification policy/rules;
- recovery policy;
- configuration hash.

A changed mapping or rule set produces a new plan version.

## Dry-run / projection requirement

`proofshift dry-run` must create an isolated physical shadow representation. It is not sufficient to only report hypothetical changes.

The dry run sequence is conceptually:

```text
inspect
snapshot
plan
project
verify
recovery analysis
report
```

## Recovery readiness

Every transformation has one recovery mode:

- Reverse
- Restore
- Compensate
- Irreversible

For PS-0, irreversible destructive transformations fail readiness.

Expected report style:

```text
Recovery Analysis
────────────────────────────
Directly reversible           64.2%
Snapshot restorable           31.7%
Compensating recovery          4.1%
Irreversible                   0.0%

Source systems checkpointed    YES
All destructive transforms
have recovery strategies       YES

ROLLBACK READINESS             PASS
```

## Verification scopes

PS-0 must exercise:

### Attribute
- exact
- normalized string
- date/date-time
- numeric/currency tolerance
- code map

### Entity
- existence
- uniqueness
- required value
- expected cardinality

### Relationship
- correct member/beneficiary/document ownership

### Timeline
- semantic state/history equivalence
- gap/overlap/effective-date validation

### Aggregate
- counts
- contribution totals
- benefit-payment totals
- service-credit totals

### Accounting
- all source artifacts dispositioned
- all target artifacts have lineage

### Recovery
- all destructive operations have approved recovery paths

## CLI target

Initial command shape:

```text
proofshift validate <config>
proofshift inspect <config>
proofshift snapshot <config>
proofshift plan <config>
proofshift project <config-or-plan>
proofshift verify <run-id>
proofshift report <run-id>
proofshift evidence <run-id>
proofshift lineage <artifact-id>
proofshift recovery <run-id>
proofshift dry-run <config>
```

Do not prematurely lock command syntax if a better consistent CLI emerges, but preserve capabilities.

## Final acceptance criteria

### Modeling
- Multiple source types can participate in one migration.
- Multiple target types can participate.
- Source/target structure may differ substantially.
- History is first-class.

### Snapshot
- SQL Server snapshot manifest is reproducible.
- File/object manifest is reproducible.

### Graph
- mappings/transformations are represented in an immutable versioned graph.
- graph hash is included with runs.

### Projection
- real isolated shadow target is produced.
- same snapshot + same plan produce equivalent output.

### Verification
- seeded current-state defects are found.
- seeded history defects are found.
- relationship corruption is found.
- financial reconciliation problems are found.
- document loss/misassociation is found.
- structurally different but semantically equivalent history can pass.

### Evidence
- every material fail has traceable evidence.
- every material pass has traceable evidence.
- every source artifact has a disposition.
- every target artifact has lineage.

### Recovery
- every transformation has recovery classification.
- destructive operations without recovery strategy fail readiness.
- recovery analysis is reported.

### Exactness
- 100% of deliberately injected defects are detected.
- correct transformations are not incorrectly flagged.

### Reproducibility
- run records are immutable.
- relevant versions/hashes are preserved.
- historical runs can be compared.

### Reporting
- human-readable report generated.
- machine-readable JSON evidence/report generated.

## PS-0.4 Connector Runtime Boundary

PS-0.4 provides read-only physical inspection and streaming reads for PostgreSQL, SQL Server, filesystem, and CSV sources. This is not snapshotting: current observations are not claimed to be immutable or transactionally consistent. It does not write targets, execute graph operations, create evidence, or assign artifact dispositions/lineage. Relational integration tests use Testcontainers and require a Docker-compatible runtime; filesystem and CSV tests use synthetic local fixtures.

Temporal fidelity preserves date-only values, UTC instants, explicit offsets, and local timestamps without assigning a timezone. Binary references carry a retrievable source reference, length, and SHA-256; content is reopened and transferred by stream. `.github/workflows/ci.yml` runs database Testcontainers and filesystem symlink checks on Ubuntu. PS-0.4 acceptance is complete: the [Docker-backed CI run](https://github.com/banderson736/ProofShift/actions/runs/37173803105/job/111352102267) reports 67 passed, 0 failed, and 0 skipped.

## PS-0.5 Shadow Projection boundary

PS-0.5 consumes live source observations through PS-0.4 connectors, executes direct-source paths using copy/map/transform/split/archive/exclude, and writes only to systems explicitly classified `shadow-target`. PostgreSQL uses a generated run schema and predefined template tables; filesystem output uses a generated run directory. Both target connectors detect duplicate identities and support connector-neutral read-back. Binary file content is streamed source-to-target and checked against source length/SHA-256.

The JSONL Projection Journal records what was written, excluded, failed, or retained as metadata-only, including artifact ancestry, edge/version, transformations/versions, and recovery metadata. It is not verification evidence. `proofshift-projection-fingerprint-v1` is computed over actual read-back values. Failure and cancellation retain partial shadow state and journal; neither status reports success. PS-0.5 does not create source snapshots, execute rollback, verify correctness, or write production targets. Merge and chained target-input execution are explicitly deferred because join/scheduling semantics are not defined.

The reusable 10-member configuration, database setup SQL, CSV, and document fixture are in `scenarios/pension-modernization/ps05/`. PS-0.5 acceptance is complete: Docker-backed [GitHub Actions run 37177385707](https://github.com/banderson736/ProofShift/actions/runs/37177385707/job/111362742298) reports 82 passed, 0 failed, and 0 skipped, including the PostgreSQL/SQL Server projection and CLI scenarios.

## PS-0.6 Source Checkpoints and Reproducible Snapshots

PS-0.6 captures only source nodes required by the compiled graph into a local materialized checkpoint. Structured records use a versioned typed streaming representation; binary values use bounded content-addressed streams with length/SHA-256 validation. The manifest preserves configuration/graph/selector identity, endpoint guarantees and capture times, source/segment fingerprints, aggregate capture window, and explicit cross-system atomicity.

PostgreSQL and SQL Server checkpoint reads use provider-supported consistent transactions (repeatable read and serializable respectively). Filesystem/CSV capture is observed, not atomic, and detects ordinary input changes during capture. Mixed endpoint checkpoints report `CrossSystemAtomic: false`. Failed/cancelled checkpoints retain diagnostics but cannot be replayed.

`proofshift snapshot <config>` creates the checkpoint. `proofshift project <config> --checkpoint <id-or-path>` validates the checkpoint and uses it as the only source provider; graph/configuration/source coverage mismatch, tampering, or missing data fails before target preparation. Replay retains original artifact identity and provenance and adds checkpoint provenance. Shadow projection remains distinct from verification and never writes to production targets.

Focused storage tests cover all normalized value kinds, relationships/temporal metadata, binary round-trip, tamper rejection, incomplete-state rejection, and selector mismatch. The Docker pension scenario captures all three source nodes, changes CSV/document inputs, stops SQL Server, and reproduces the live projection fingerprint from the checkpoint. Full local solution validation passed 86 tests with 0 failures and 2 Windows symlink-permission skips. PS-0.6 remote CI acceptance remains pending; PS-0.7 and later remain out of scope.

## PS-0 non-goals

Do not add these unless a prerequisite forces a minimal abstraction:

- web frontend;
- SaaS hosting;
- multi-tenancy;
- live production migration;
- FHIR;
- Oracle/DB2;
- Kafka;
- Kubernetes;
- Redis;
- AI-generated mappings;
- arbitrary code transforms;
- enterprise SSO;
- FedRAMP/RampForge implementation;
- real customer data.
