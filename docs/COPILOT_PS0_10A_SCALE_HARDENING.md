# ProofShift — PS-0.10A Scale Baseline & Hot-Path Hardening

PS-0.9 is formally Accepted.

Current accepted remote CI baseline:

- 113 tests passed
- 0 failed
- 0 skipped

Current integrated pension benchmark:

- 8,495 checkpoint artifacts
- 8,595 projected targets
- 396,017 ms total runtime
- 519,651,328-byte observed peak working set

The separate 8.22-million-record benchmark measures generator enumeration only and must not be represented as full ProofShift pipeline throughput.

Do not begin PS-0.10B, PS-0.10C, new connectors, or new domain packs.

The purpose of PS-0.10A is to make the existing generic engine measurable and remove scale bottlenecks before product breadth increases.

---

# 1. Primary Objective

Harden the existing ProofShift pipeline for substantially larger migration workloads without changing its assurance semantics.

The system must retain:

- deterministic fingerprints;
- checkpoint reproducibility;
- projection ancestry;
- actual-target verification;
- source dispositions;
- target lineage;
- Evidence Graph integrity;
- recovery analysis;
- dry-run qualification;
- exact PS-0.9 pension discrepancy behavior.

Optimize implementation, not meaning.

---

# 2. First Rule: Measure Before Optimizing

Before making performance changes, add stage-level instrumentation and capture a reproducible baseline.

Measure at minimum:

- source inspection duration;
- checkpoint duration by node;
- checkpoint artifacts/sec;
- checkpoint bytes/sec;
- projection duration by edge;
- projection artifacts/sec;
- shadow-target write duration;
- projection-journal duration;
- target read-back duration;
- verification workspace ingest duration;
- verification rule duration individually;
- source-disposition analysis duration;
- lineage analysis duration;
- Evidence Graph construction duration;
- evidence persistence duration;
- recovery-analysis duration;
- reporting duration;
- total orchestration duration.

Capture counts with each stage.

Do not log source record values.

---

# 3. Performance Result Model

Introduce a generic runtime performance model rather than pension-specific instrumentation.

Conceptually:

```text
PerformanceRun
PerformanceStage
PerformanceMeasurement
```

Measurements may include:

- elapsed time;
- artifact count;
- byte count;
- throughput;
- process working set;
- managed heap size where useful;
- GC counts where useful;
- temporary-workspace bytes;
- connector/edge/rule identity.

Do not add high-cardinality production telemetry that records individual customer identities.

---

# 4. Human Performance Report

Produce a report similar to:

```text
ProofShift Performance

Scenario:
Pension Fast

CHECKPOINT
SQL Server MEMBER             100 records       ... ms
SQL Server CONTRIBUTION     5,000 records       ... ms
Documents                     250 records       ... ms

PROJECTION
participant                   100 records       ... ms
contribution_transaction    5,000 records       ... ms

VERIFICATION
Workspace ingestion         8,xxx records       ... ms
Contribution rules                          ... ms
Timeline rules                              ... ms
Accounting                                  ... ms
Lineage                                     ... ms

RECOVERY                                    ... ms

TOTAL                                       ... ms

Peak ProofShift Working Set                 ... MB
Temporary Workspace Peak                    ... MB
```

Also support machine-readable JSON.

---

# 5. Separate Environment Startup From Engine Runtime

Docker/container startup and fixture loading must not be conflated with ProofShift processing time.

Benchmark reporting should distinguish:

```text
environment startup
fixture/source load

from

ProofShift checkpoint
ProofShift projection
ProofShift verification
ProofShift recovery
ProofShift reporting
```

Both may be reported, but they are different measurements.

---

# 6. Benchmark Tiers

Establish three repeatable benchmark tiers.

## Fast

Approximately current PS-0.9 integrated scenario.

Purpose:

- ordinary regression;
- demo;
- CI-capable correctness/performance smoke test.

## Medium

Large enough to reveal scale behavior.

Target at least hundreds of thousands of migration artifacts, preferably approximately 1 million total artifacts if the current environment supports it reasonably.

Purpose:

- regular local/manual performance regression;
- memory-growth analysis;
- SQLite/index behavior;
- batching validation.

Do not run this on every CI invocation unless runtime proves acceptable.

## Large

Multi-million-artifact benchmark.

Purpose:

- manual/nightly scale testing;
- high-volume rule validation;
- architecture validation.

Use actual executed scale in reports.

Do not fabricate results from generator enumeration.

---

# 7. PostgreSQL Shadow Write Hot Path

Current implementation opens a PostgreSQL connection and begins/commits a transaction for each projected artifact.

Remove this per-artifact architecture.

Introduce a projection-target write session or equivalent.

Desired lifecycle:

```text
open target session
    ↓
begin bounded batch
    ↓
write N artifacts
    ↓
commit
    ↓
next bounded batch
    ↓
complete session
```

The target connector abstraction must continue to hide PostgreSQL-specific behavior from Projection.

---

# 8. PostgreSQL Batching

Use bounded configurable batching.

Requirements:

- deterministic output;
- parameterized values;
- duplicate-target detection remains fail-closed;
- cancellation works;
- partial failure remains explicit;
- failed batches cannot be reported as successful;
- Projection Journal accurately represents materialized output.

Do not require one transaction for the entire migration.

Do not require one transaction per row.

---

# 9. PostgreSQL Command Reuse

Avoid repeatedly rebuilding equivalent INSERT commands where practical.

Use:

- prepared commands;
- connector-level writer sessions;
- efficient PostgreSQL batching;

or another provider-appropriate mechanism.

Do not introduce an ORM merely for this.

---

# 10. Projection Journal Hot Path

Current Projection Journal persistence flushes after every journal record.

Preserve write-ahead semantics while removing unnecessary per-entry flushes.

The architecture must still ensure that ProofShift can distinguish:

```text
planned/pending
materialized
failed
```

after an interrupted projection.

A valid approach may be:

```text
buffer pending batch
flush pending batch
perform target batch
append produced/failed batch
flush completion batch
```

Exact implementation may differ.

Document crash-consistency semantics.

---

# 11. Journal Memory Bounds

Journal buffering must remain bounded.

Do not solve disk I/O by accumulating the entire migration journal in memory.

---

# 12. SQLite Verification Workspace Profile

The current disposable workspace uses:

```text
journal_mode=DELETE
synchronous=FULL
```

Evaluate more appropriate settings for ephemeral verification working state.

Benchmark alternatives such as:

- WAL where appropriate;
- memory-backed journal where safe;
- `synchronous=NORMAL`;
- controlled temporary-store settings;
- larger write transactions.

Do not simply disable integrity without documenting the consequence.

The final Evidence Graph remains the durable assurance artifact.

---

# 13. Verification Workspace Write Efficiency

Current ingestion performs large numbers of separate commands for:

- source records;
- expected targets;
- expected values;
- observed targets;
- observed values.

Optimize through:

- prepared command reuse;
- larger bounded transactions;
- batched inserts;
- appropriate indexes.

Preserve cancellation and deterministic identity behavior.

---

# 14. Remove JSON-Extraction Sorting From High-Volume Hot Paths

Current ordered rule reads may derive sort/group fields using JSON extraction from serialized record content.

This must not remain the primary high-volume strategy.

Introduce indexed normalized ordering keys or another deterministic disk-backed structure.

Requirements:

- pack-neutral;
- arbitrary configured rule ordering fields;
- multi-field grouping;
- deterministic ordering;
- no full-dataset RAM sort.

The Pension Pack must not dictate the generic workspace schema.

---

# 15. Rule Ordering Keys

Verification rules should be able to declare which fields they require for:

```text
grouping
ordering
lookup
```

The workspace may materialize those required keys during ingestion.

Do not require arbitrary JSON evaluation for every sorted row during rule execution.

---

# 16. ReadOptions.BatchSize

`ReadOptions.BatchSize` currently exists but does not materially control relational reads.

Either implement meaningful provider behavior or redefine/document its purpose.

Do not retain configuration that implies batching behavior which the runtime ignores.

---

# 17. Partition Architecture

Current partition requests fail as unsupported.

PS-0.10A does not need distributed execution.

However, define a clean future partition model if necessary so that later source connectors can represent:

```text
primary-key ranges
hash partitions
date ranges
physical partitions
```

Do not implement distributed workers yet.

Do not make partitioning a requirement for PS-0.10A completion.

---

# 18. Eliminate Full Verification Ledger Materialization

Current Verification eventually creates full in-memory collections of:

- LineageRecord;
- ArtifactDispositionRecord;
- source graph references;
- observed target graph references;
- journal entries.

This will not scale to large migrations.

Introduce a persisted/streaming verification-ledger abstraction.

Conceptually:

```text
IVerificationLedgerStore
```

or equivalent.

It should support:

- append/store lineage;
- append/store source dispositions;
- query by source;
- query by target;
- count by disposition;
- validate coverage;
- stream ledger contents;
- deterministic fingerprinting/integrity where appropriate.

---

# 19. VerificationResult Must Become Scale-Safe

Do not require a successful VerificationResult to contain every source disposition and every lineage record in memory.

Prefer:

```text
summary/counts
+
store receipt/reference
+
stream/query access
```

Maintain compatibility only where practical.

Do not preserve an inherently non-scalable API merely to avoid internal refactoring.

Document the change in an ADR.

---

# 20. Recovery Must Consume Ledgers Incrementally

Recovery currently relies on in-memory Verification dispositions/lineage/journal collections.

Change Recovery to consume the persisted/queryable verification ledger.

Requirements:

- same recovery conclusions;
- deterministic fingerprints;
- no pension-specific dependency;
- bounded memory.

---

# 21. Coverage Validation Must Be Store-Side/Streaming

Source disposition coverage and target lineage coverage must not require constructing complete parallel arrays of every source and every target.

Perform coverage through:

- database joins;
- indexed queries;
- streaming merge comparison;

or equivalent.

Maintain fail-closed behavior.

---

# 22. Evidence Volume Policy

ProofShift should not need one PASS evidence record per artifact to prove a successful million-record migration.

Establish an explicit evidence policy:

- aggregate successful population evidence where appropriate;
- preserve detailed failure/warning evidence;
- preserve artifact-level lineage/disposition ledgers separately;
- retain ability to inspect an individual artifact on demand.

Do not sacrifice explainability.

---

# 23. Evidence Store Scalability

Current filesystem evidence persistence serializes the complete Evidence Graph into one in-memory JSON document.

Harden evidence persistence for larger graphs.

A versioned streaming representation such as:

```text
manifest.json
evidence.ndjson
```

is acceptable.

Requirements:

- integrity verification;
- deterministic semantic fingerprint;
- cancellation safety;
- partial-file safety;
- no whole-evidence-set byte array requirement.

Migration of old PS-0 evidence fixtures does not need enterprise-grade backward compatibility unless already promised.

---

# 24. Evidence Fingerprint Semantics

Performance changes must not make evidence fingerprints depend on:

- batch size;
- thread scheduling;
- SQLite row sequence;
- file chunking;
- temporary path;
- writer buffering.

Equivalent semantic evidence must retain equivalent fingerprints.

---

# 25. Source Checkpoint Consistency Policy

Current SQL Server checkpoint reads default to Serializable isolation.

Do not silently use an intrusive consistency strategy against unknown customer systems.

Introduce explicit checkpoint-consistency configuration.

Support an initial policy model capable of expressing concepts such as:

```text
observed
transaction-consistent
```

and provider-specific supported choices.

For SQL Server, stronger transaction isolation must be explicit.

ProofShift must never enable database-level snapshot settings automatically.

---

# 26. SQL Server Safety

If the environment does not support the requested low-impact consistent-read strategy:

- report it;
- downgrade only if policy explicitly permits;
- otherwise fail.

Do not silently switch to locking behavior with potentially material operational impact.

Fixture configuration may explicitly request Serializable behavior for deterministic tests.

---

# 27. PostgreSQL Consistency

Continue using provider-supported repeatable semantics where configured, but document long-running snapshot implications.

Do not overstate:

```text
transaction-consistent endpoint
```

as:

```text
globally atomic source system
```

Cross-endpoint guarantees remain unchanged.

---

# 28. Source Operational Safety Reporting

Checkpoint inspection/reporting should indicate the selected consistency strategy.

Example:

```text
SQL Server
Strategy: transaction-consistent
Isolation: snapshot
Operational note: read-only transaction

Documents
Strategy: observed/materialized

Cross-system atomic: NO
```

Do not expose credentials.

---

# 29. Performance Regression Harness

Create a reusable benchmark runner.

Do not make xUnit timing assertions the primary benchmark mechanism.

It should be possible to run something approximately like:

```bash
proofshift benchmark <scenario>
```

or a repository script using the production orchestration path.

Use whichever fits the existing CLI architecture cleanly.

---

# 30. Baseline Preservation

Before hot-path changes, capture the accepted fast scenario's stage-level baseline.

After changes, rerun it.

Report:

```text
before
after
delta
```

for each major stage.

Do not claim improvement solely from total runtime if environment startup changed.

---

# 31. No Arbitrary Performance SLA

Do not invent a percentage-improvement requirement.

Acceptance is architectural plus measured:

- eliminate known per-artifact network transaction architecture;
- eliminate per-entry journal flushing;
- eliminate whole-ledger in-memory requirement;
- improve scratch workspace hot paths;
- demonstrate medium-scale full-pipeline execution;
- provide before/after measurements.

If performance unexpectedly worsens, investigate before acceptance.

---

# 32. Memory Scaling Analysis

Run at least:

```text
fast
medium
```

full-pipeline benchmarks.

Record peak ProofShift process memory separately from container memory where practical.

Determine whether process memory growth remains bounded enough to support continued scaling.

Do not count SQL Server/PostgreSQL container RSS as ProofShift process RSS.

---

# 33. Preserve PS-0.9 Business Semantics

The PS-0.9 defective scenario must still report exactly:

```text
149 business discrepancies
```

The corrected scenario must still report:

```text
0 business discrepancies
0 unaccounted sources
0 unexplained targets
0 verification failures
0 recovery failures
QUALIFIED
```

Performance work must not change those semantics.

---

# 34. Preserve Fingerprints

Where semantic behavior is unchanged, repeated runs must retain deterministic equivalence for:

- source fingerprint;
- graph hash;
- target/projection fingerprint;
- rule-set fingerprint;
- Evidence fingerprint;
- recovery-assessment fingerprint;
- dry-run fingerprint.

If an intentional format-version change requires a new fingerprint version, document it explicitly.

---

# 35. Preserve Security

Performance optimizations must not weaken:

- path boundaries;
- SQL parameterization;
- secret redaction;
- target role enforcement;
- checkpoint integrity;
- evidence integrity;
- context mismatch detection.

---

# 36. Explicit Non-Goals

Do not add:

```text
Oracle
Db2
MySQL
fixed-width
JSON connector
XML connector
S3
Azure Blob
FHIR
Utility Pack
Justice Pack
ERP/HCM Pack
web UI
authentication
distributed workers
production migration
production rollback
AI
```

during PS-0.10A.

---

# 37. Architecture Documentation

Update:

```text
README.md
ARCHITECTURE.md
PS0_SPECIFICATION.md or successor roadmap documentation
ROADMAP.md
DECISIONS.md
AGENTS.md
Copilot instructions
IDE handoff
```

Add an ADR for the scale architecture where appropriate.

Document:

- benchmark methodology;
- target write sessions;
- journal batching semantics;
- temporary workspace durability model;
- verification ledger persistence;
- evidence-volume policy;
- checkpoint consistency policy.

---

# 38. Acceptance Criteria

PS-0.10A is complete when:

1. Stage-level performance instrumentation exists.
2. Fast scenario has before/after stage measurements.
3. Environment/fixture setup is separated from ProofShift runtime measurements.
4. PostgreSQL projection no longer opens/commits one connection/transaction per artifact.
5. Projection Journal no longer flushes once per record.
6. Journal crash-state semantics remain correct.
7. Verification scratch storage uses an explicitly justified ephemeral durability profile.
8. Verification ingestion uses efficient batched/prepared writes.
9. High-volume sorting no longer depends primarily on per-row correlated JSON extraction.
10. Verification does not require full lineage ledger in memory.
11. Verification does not require full disposition ledger in memory.
12. Recovery consumes scale-safe persisted/queryable verification data.
13. Coverage validation does not require complete duplicate in-memory artifact arrays.
14. Evidence persistence does not require the complete serialized graph in a single byte array.
15. Evidence-success aggregation policy is documented.
16. SQL Server consistency strategy is explicit/configurable rather than silently Serializable for every customer environment.
17. Existing checkpoint guarantee semantics remain honest.
18. Fast PS-0.9 defective scenario still finds exactly 149 business discrepancies.
19. Fast corrected scenario still qualifies with zero discrepancies.
20. All accepted integrity/security tests remain green.
21. A medium-scale full-pipeline benchmark completes successfully.
22. Medium-scale memory/performance measurements are recorded.
23. Benchmark output is machine-readable and human-readable.
24. Remote CI remains green.
25. No new connector/domain scope was added.

---

# 39. Completion Report

When complete, stop and report:

## Baseline

Provide pre-change stage measurements.

## Hot-Path Changes

Describe:

- PostgreSQL writing;
- Projection Journal;
- SQLite workspace;
- sorting/indexes;
- Verification ledger;
- Evidence persistence;
- Recovery consumption.

## Consistency Safety

Describe:

- SQL Server strategy;
- PostgreSQL strategy;
- configuration behavior;
- operational-risk reporting.

## Fast Benchmark

Report before/after:

```text
checkpoint
projection
verification
recovery
reporting
ProofShift runtime total
environment/setup total
peak ProofShift memory
temporary workspace peak
```

## Medium Benchmark

Report actual:

```text
dataset/artifact counts
checkpoint time
projection time
verification time
recovery time
total ProofShift time
peak ProofShift memory
workspace peak
throughput
```

Do not fabricate unavailable metrics.

## Correctness

Confirm:

```text
Defective pension discrepancies = 149
Corrected pension discrepancies = 0
Unaccounted corrected source = 0
Unexplained corrected target = 0
Corrected dry run = QUALIFIED
```

## Fingerprints

Report whether semantic fingerprints remain equivalent where expected.

## Testing

Report:

```text
restore
build
test total
passed
failed
skipped
remote CI
```

## Architecture

List any API changes required for scale and why they were necessary.

## Deferred

Explicitly list PS-0.10B work.

Do not begin PS-0.10B automatically.