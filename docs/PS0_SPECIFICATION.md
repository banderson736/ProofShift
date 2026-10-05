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

Focused storage tests cover all normalized value kinds, relationships/temporal metadata, binary round-trip, tamper rejection, incomplete-state rejection, and selector mismatch. The Docker pension scenario captures all three source nodes, changes CSV/document inputs, stops SQL Server, and reproduces the live projection fingerprint from the checkpoint. PS-0.6 was accepted after Docker-backed [GitHub Actions run 37180351598, job 111371493382](https://github.com/banderson736/ProofShift/actions/runs/37180351598/job/111371493382) passed 88 tests with 0 failures and 0 skips.

## PS-0.7 Semantic Verification and Evidence Graph

PS-0.7 verifies a completed shadow projection against the exact complete source checkpoint and configuration graph that produced it. Verification must never read live source systems. Expected target records are recomputed using the same deterministic transformation and identity semantics as Projection; actual target state is independently read through connector abstractions.

The run must fail closed when the checkpoint, projection manifest, journal, configuration, graph, source-node coverage, selectors, target-node coverage, or connector versions do not match. Only terminal `produced` journal entries establish expected materialized targets. Pending or failed entries cannot prove materialization. Every source artifact receives one graph-scoped disposition, and every physical target receives graph-scoped source/edge lineage. Graph-node scope is required because split target nodes may share an `ArtifactId`.

Generic rules cover source accounting, target lineage/presence, unexplained targets, uniqueness, and transformed attribute comparison. Domain packs provide typed domain-specific rule implementations without adding domain dependencies to core. Findings are structured, versioned evidence linked to rules, checkpoint, projection, graph edges, and graph-scoped artifacts. Raw source/target values and resolved secrets must not appear in normal evidence output; comparisons use typed canonical values and safe fingerprints. Persisted evidence must support integrity verification and deterministic graph fingerprints.

Acceptance requires:

- exact assertions for seeded missing-target, unexpected-target, duplicate-identity, wrong-status, and wrong-normalized-field defects, followed by a clean repaired replay;
- changed physical target state changes evidence, changed rule configuration changes the rule-set fingerprint, and repeated clean replay has a deterministic evidence fingerprint;
- rule exceptions and cancellation do not create completed verification results;
- tampered evidence storage is rejected;
- full solution validation and Docker-backed CI pass without required skips.

PS-0.7 is accepted after Docker-backed [GitHub Actions run 37185122084, job 111385343893](https://github.com/banderson736/ProofShift/actions/runs/37185122084/job/111385343893) passed 96 tests with 0 failures and 0 skips.

## PS-0.8 Recovery Readiness and Dry-Run Qualification

PS-0.8 extends the assurance chain after PS-0.7 verification. A passing verification alone does not make a migration ready. Every executed material edge and each graph-scoped target artifact must have an explainable recovery posture, and required recovery capability must be demonstrated in isolated shadow state.

Recovery modes remain distinct:

- `Reverse`: accepted only when the configured operation has a conservative, unambiguous inverse. Trimming, string/date normalization, split/concatenate without reversible semantics, lossy operations, and non-injective code maps fail Reverse analysis.
- `Restore`: requires an available, integrity-validated target recovery checkpoint. PS-0.6 source checkpoints remain source inputs and are never accepted as target rollback capability.
- `Compensate`: requires a strategy identifier resolved through the registered compensator registry. The compensator declares exact-restoration or semantic-compensation validation; semantic outcomes require the registered strategy's explicit validator.
- `Irreversible`: explicit justification is required and policy denies it by default. Qualification requires explicit policy allowance and compliance with the configured artifact limit.

The effective recovery policy is read from `recovery/policy.yaml` and has its own deterministic `proofshift-recovery-policy-v1` fingerprint. Recovery produces immutable edge/artifact assessments, a dependency-ordered plan, a separate Recovery Evidence Graph, and a rehearsal record. Recovery evidence references the finalized PS-0.7 Verification Run/Evidence Graph; it never mutates or rehashes PS-0.7 evidence.

The initial recovery checkpoint capability is shadow-only:

- PostgreSQL copies the isolated per-run schema into a recovery schema in the same test database, validates the copy, and restores by transactional schema replacement.
- Filesystem copies the isolated per-run tree, including its node-scoped identity index, to a contained recovery directory; it validates file-tree hashes and restores the exact tree.

These mechanisms demonstrate the rehearsal contract only. They are not provider-native production backups. The rehearsal captures the populated shadow baseline, applies a deterministic controlled mutation, executes/validates recovery, re-reads physical targets, and confirms exact baseline restoration for the current fixture. Any final target fingerprint change after verification prevents qualification. Semantic compensation may report a different recovery result fingerprint only when a registered explicit validator passes; the shadow namespace is then restored to its verified baseline before qualification.

`proofshift dry-run <config>` orchestrates snapshot, checkpoint-backed projection, verification, recovery analysis, required shadow rehearsal, and qualification. `proofshift recovery <config> --run <dry-run-id>` reads the integrity-checked recovery artifact. Existing `snapshot`, `project`, and `verify` commands remain independent stages. Projection alone is not a dry run; verification alone is not readiness. Qualification means only `QUALIFIED DRY RUN`, never production safety or business approval.

PS-0.8 acceptance requires:

- every executed edge and target artifact is assessed, with graph-scoped source/target evidence and deterministic reverse-dependency plan ordering;
- missing definitions/capabilities, corrupted target checkpoints, false Reverse, prohibited irreversible risk, failed rehearsal, and stale target state fail qualification with stable reasons;
- the pension scenario includes two exact end-to-end false-Reverse assertions (a many-to-one status map and a pass-through code map); Projection and Verification pass, but Recovery denies qualification;
- PostgreSQL and filesystem shadow recovery both demonstrate capture, mutation, validation, restore, and baseline fingerprint equality;
- policy, assessment, rehearsal, recovery evidence, and dry-run fingerprints are versioned and deterministic; changing policy changes the policy, assessment, and dry-run fingerprints;
- persisted assessment, plan, rehearsal, evidence, and qualification artifacts detect tampering and contain no raw record values or resolved secrets;
- clean end-to-end dry run has zero unaccounted sources, unexplained targets, verification failures, and recovery failures, with qualification `Qualified`;
- failed rehearsal, stale target, missing capability, false Reverse, and corrupted checkpoint each have exact assertions;
- restore/build/full tests and Docker-backed CI pass with no required skips.

PS-0.8 was accepted after Docker-backed [GitHub Actions run 37190061657, job 111400188926](https://github.com/banderson736/ProofShift/actions/runs/37190061657/job/111400188926) passed the full solution test suite with 103 passed, 0 failed, and 0 skipped. Production migration, production rollback, and generalized merge recovery remain deferred. PS-0.9 has since been explicitly assigned.

## PS-0.9 Public Pension Assurance Vertical

Status: Accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed the full solution test suite with 113 passed, 0 failed, and 0 skipped. PS-0.9 completes the deterministic public-pension assurance vertical. Pension concepts/rules remain in `ProofShift.Packs.Pension`; Verification remains pension-neutral. No production migration/rollback, UI/SaaS, AI, FHIR, Oracle, DB2, Kubernetes, or unrelated infrastructure is in scope.

The implemented pack provider (`proofshift.pension`, version `0.9.0`) registers member accounting/presence/uniqueness/status, employment timeline, contribution accounting/totals, service-credit totals, beneficiary relationships, retirement-election mapping, benefit-payment accounting/totals, document accounting/relationships, and code transformation rules. Currency tolerance defaults to explicit decimal `0.01`. Rules consume typed record streams through the generic temporary SQLite workspace; evidence reports contain safe fingerprints/counts rather than raw member values.

Generator, target model, and defect injector are independently versioned as `proofshift-pension-generator-v1`, `proofshift-pension-target-model-v1`, and `proofshift-pension-defects-v1`. The fast and large scales are exposed through `proofshift demo generate`. The current exact rule fixture asserts these record-level categories: 7 missing members, 4 duplicate members, 3 wrong statuses, 12 missing employment periods, 8 incorrect employment dates, 17 incorrect service-credit totals, 39 missing contributions, 8 duplicate contributions, 6 incorrect contribution amounts, 11 benefit-payment amount discrepancies, 6 broken beneficiary links, 2 wrong-member beneficiaries, 3 election mappings, 5 code transformations, 9 missing documents, 4 wrong-member documents, and 3 missing historical exports. These total 149 injected discrepancies. The fast corpus is physically loaded into SQL Server, independently verified against corrected and corrupted PostgreSQL observations, and projected through the production PostgreSQL shadow connector. Both false-Reverse edges execute in the generated graph and are rejected by Recovery after successful Projection/Verification.

`proofshift report <config> --run <dry-run-id>` reads integrity-checked persisted verification and recovery artifacts, verifies exact run/evidence/configuration/graph binding, and emits human or JSON migration-assurance output. `proofshift compare <config> --before <dry-run-id> --after <dry-run-id>` compares stored evidence and run fingerprints. `proofshift demo generate <output-directory> [--scale fast|large] [--seed <integer>]` writes clean source, corrected target, defective target, and a versioned manifest as CSV.

The local PS-0.9 fast integrated path includes physical SQL Server, CSV, and filesystem sources; PostgreSQL and filesystem shadow targets; real document/export payloads; and projected defective and corrected runs through generic Projection, Verification, and Recovery. Both runs persist integrity-checked artifacts and reports, and the CLI comparison resolves 149 discrepancies to zero with source/checkpoint and graph attribution. The integrated benchmark completed in 396,017 ms with a 519,651,328-byte peak working set. The large-scale benchmark remains generator-only. Local Windows EndToEnd tests reported 41 passed, 0 failed, and 2 symlink-capability skips; the Docker-backed GitHub Actions run passed all 113 tests with zero skips.

PS-0.9 acceptance criteria are complete: exact defective and corrected flows, source dispositions and target lineage, false-Reverse Recovery findings, evidence-backed human/JSON/exception reports, distinct persisted-run comparison and attribution, the external-target service scenario, bounded-memory large-scale generation with an opt-in benchmark, updated operator documentation, local restore/build/test, and remote Docker-backed CI with no skips. Do not begin PS-0.10 without an explicit new assignment.

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
