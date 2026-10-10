# ProofShift — PS-0.10D Verification Throughput & Large-Scale Execution

PS-0.10C is formally **Accepted**.

Do not begin PS-0.10E transport/storage expansion.

Do not add new domain packs.

Do not add UI, AI, production migration, or production rollback.

---

# 1. Objective

Implement:

# PS-0.10D — Verification Throughput & Large-Scale Execution

PS-0.10A proved that Verification can move large working sets to bounded-memory/disk-backed execution.

It did **not** prove commercially useful throughput.

The accepted Medium benchmark exposed the remaining weakness:

```text
Generated source records       411,000

Checkpoint artifacts           424,750
Projected targets              429,750

Full benchmark                 3h 55m

Peak workload RSS              1.076 GB

Later-stage RSS plateau        ~700–720 MB

Largest workspace DB           7.64 GB
Workspace WAL                  ~1.08 GB
```

Verification remains the dominant cost and shows above-linear wall-time growth.

PS-0.10D must address that.

---

# 2. Central Question

PS-0.10D must answer:

> Can ProofShift semantically verify hundreds of thousands and millions of migration artifacts in a time and resource envelope suitable for real migration-assurance work?

This milestone is not primarily about memory safety.

It is about:

```text
throughput
algorithmic scaling
scratch efficiency
parallel execution
rule execution planning
```

while preserving exact assurance semantics.

---

# 3. Do Not Start by Optimizing

Before modifying Verification behavior:

1. Run the accepted implementation.
2. Capture detailed phase measurements.
3. Identify dominant CPU, I/O, query, serialization and indexing costs.
4. Preserve the results as the PS-0.10D baseline.

Do not optimize from assumptions.

---

# 4. Accepted Correctness Baseline

All performance work must preserve:

```text
Pension defective discrepancies = 149

Pension corrected discrepancies = 0

Corrected unaccounted sources = 0

Corrected unexplained targets = 0

Corrected Recovery failures = 0

Corrected Recovery rehearsal = PASSED

Corrected qualification = QUALIFIED
```

The two false-Reverse behaviors must remain semantically unchanged.

---

# 5. Fingerprint Baseline

Equivalent runs must continue to produce equivalent semantic:

```text
configuration hash
graph hash
source fingerprint
projection fingerprint
rule-set fingerprint
Evidence fingerprint
recovery-assessment fingerprint
dry-run fingerprint
```

Execution strategy must not enter semantic identity.

Examples of execution-only settings:

```text
partition count
batch size
worker count
SQLite page/cache tuning
temporary paths
rule scheduling
```

must not change semantic fingerprints.

---

# 6. Deep Verification Profiling

Break Verification into measurable phases.

At minimum:

```text
Source artifact ingestion
Expected target derivation
Observed target ingestion

Ordering-key extraction
Workspace writes
Index creation
Index maintenance
Workspace finalization

Source accounting
Target lineage

Per-rule preparation
Per-rule execution
Per-rule result materialization

Aggregate calculation
Relationship lookups
Timeline grouping
Uniqueness processing

Evidence construction
Evidence canonicalization
Evidence persistence

Verification ledger writes
Verification ledger queries

Workspace cleanup
```

---

# 7. Resource Measurements

Collect where practical:

```text
elapsed wall time
CPU time
process RSS
managed heap
GC counts
scratch bytes
SQLite DB bytes
WAL bytes
rows written
rows read
query counts
index counts
```

Do not collect sensitive artifact values.

---

# 8. Explain Runtime

At least approximately 95% of Verification wall time should be attributable to measured stages.

Do not leave a giant unexplained:

```text
Other = 2 hours
```

bucket.

---

# 9. Query Instrumentation

Identify high-cost SQLite operations.

For material queries record where useful:

```text
query category
execution count
cumulative duration
rows returned
query plan
```

Do not log parameter values containing customer data.

---

# 10. Query Plan Regression Tests

Add targeted tests ensuring important high-volume queries use expected indexes.

Examples:

```text
artifact identity lookup
semantic-type scan
grouping key lookup
target lineage lookup
source disposition lookup
relationship lookup
```

Avoid brittle tests tied to irrelevant SQLite plan text.

Assert meaningful plan properties.

---

# 11. Verification Execution Plan

Introduce a generic concept equivalent to:

```text
VerificationExecutionPlan
```

derived before workspace population.

The plan should know:

```text
which rules will run
which semantic types they require
which source fields they require
which target fields they require
which grouping keys they require
which ordering keys they require
which lookup keys they require
whether a rule is partition-safe
whether a rule requires global finalization
```

---

# 12. Rule Descriptors Drive the Plan

Use the rule metadata/descriptor architecture established in PS-0.10B.

Do not hard-code Pension rule knowledge into Verification.

If rule metadata is currently insufficient, extend the generic rule descriptor contract.

---

# 13. Required-Field Projection

Do not copy every field of every RecordEnvelope into high-volume Verification indexes merely because it exists.

For each semantic type determine the union of fields actually required by:

```text
configured verification rules
identity
lineage/accounting
required evidence
```

Materialize only those fields in high-performance rule structures.

---

# 14. Preserve Artifact Detail

Required-field projection must not destroy auditability.

The full original artifact remains available from:

```text
checkpoint
target observation
or an immutable artifact source reference
```

Detailed failure evidence may fetch additional artifact context when required.

---

# 15. Avoid Full Record Duplication

Inspect whether full serialized `RecordEnvelope` JSON is duplicated across:

```text
workspace
verification ledger
evidence staging
expected state
observed state
```

Reduce unnecessary duplication.

Prefer:

```text
artifact identity
semantic type
required verification columns
stable source pointer/reference
```

where full data already exists elsewhere.

---

# 16. Single Parsing / Normalization

A field needed by five rules should not be independently:

```text
JSON parsed
normalized
converted
```

five times.

Normalize/materialize required values once per artifact where practical.

Reuse them across compatible rules.

---

# 17. Typed Workspace Columns

For frequently queried/grouped values, use type-aware indexed representations.

Support:

```text
string
integer
decimal
date
timestamp
offset timestamp
boolean
null
```

without losing existing ProofShift semantics.

Do not stringify exact decimals merely for indexing convenience.

---

# 18. Workspace Schema Review

Measure whether the current generic row/value layout remains appropriate.

Optimize schema based on real measured operations.

Possible techniques may include:

```text
covering indexes
composite indexes
WITHOUT ROWID tables
normalized required-key tables
semantic-type partition tables
```

but none are required unless measurement justifies them.

---

# 19. Avoid Index Explosion

Do not create one independent index for every rule if several rules can share a compatible index.

The Verification Execution Plan should deduplicate index requirements.

---

# 20. Deferred Index Construction

Preserve the successful PS-0.10A pattern:

```text
bulk ingest
    ↓
build appropriate secondary indexes
```

where safe.

Uniqueness/integrity constraints that must protect ingestion may remain immediate.

---

# 21. Scratch Amplification

Measure:

```text
workspace bytes
+
WAL bytes
+
ledger bytes
+
Evidence staging bytes
```

relative to logical source/target data.

The accepted Medium result created approximately:

```text
7.64 GB workspace
+
1.08 GB WAL
```

Investigate the major contributors.

---

# 22. Scratch Reduction

Reduce duplicate scratch representation where generic and measurable.

Do not sacrifice:

```text
determinism
artifact identity
queryability
integrity
```

merely to shrink disk usage.

---

# 23. WAL Lifecycle

Review WAL high-water behavior.

Where safe for disposable workspace databases:

```text
checkpoint/truncate WAL
```

at logical phase boundaries if it measurably reduces disk pressure without harming throughput.

Do not add per-batch forced checkpoint behavior that slows execution.

---

# 24. Rule Scheduling

The current engine should not simply execute every rule sequentially when independent rules can safely run concurrently.

Introduce bounded rule scheduling.

---

# 25. Rule Execution Metadata

A rule should be classifiable as something equivalent to:

```text
Independent
Partitionable
RequiresGlobalState
RequiresOrderedGroup
RequiresRelationshipIndex
```

Use a generic capability model.

Do not classify based on Pension rule names.

---

# 26. Bounded Parallel Rule Execution

Independent read-only rules may execute concurrently.

Requirements:

```text
bounded worker count
cancellation
no shared mutable semantic state
deterministic Evidence result
deterministic final ordering
```

Do not start one unbounded task per rule.

---

# 27. Operational Concurrency Setting

Allow an operational setting similar to:

```text
verification.maxConcurrency
```

or repository-consistent equivalent.

This is an execution setting, not semantic configuration.

It must not affect fingerprints.

---

# 28. Determinism Across Concurrency

Required test:

```text
same checkpoint
same target
same rules

maxConcurrency = 1
maxConcurrency = N
```

Expected:

```text
same business findings
same evidence semantic fingerprint
same qualification result
```

---

# 29. Artifact Partitioning

Introduce deterministic partitioning for high-volume rule processing where appropriate.

Possible partition keys:

```text
semantic identity hash
member/customer/case owner key
configured grouping key
```

Partitioning must preserve rule semantics.

---

# 30. Stable Partition Function

Partition assignment must be deterministic and versioned if persisted.

Do not use runtime-randomized language hash functions.

Use a stable hash algorithm.

---

# 31. Partition Count Is Operational

Changing:

```text
partitionCount = 8
```

to:

```text
partitionCount = 16
```

must not change semantic Verification output.

---

# 32. Partition-Safe Rules

Examples likely suitable for keyed partitioning include:

```text
entity comparison
attribute comparison
per-member timeline
per-member contribution accounting
per-account billing reconciliation
uniqueness when partitioned by identity
```

Do not assume all rules are partition-safe.

---

# 33. Global Rules

Rules requiring global state must have explicit behavior.

Examples may include:

```text
whole-population totals
global duplicate checks
cross-partition relationship checks
```

Use either:

```text
global execution
```

or:

```text
partition partials
    ↓
deterministic final merge
```

---

# 34. Two-Phase Aggregation

For associative aggregate operations:

```text
COUNT
SUM
MIN
MAX
```

support:

```text
partition aggregate
    ↓
global deterministic merge
```

where semantically valid.

Exact decimal arithmetic must remain exact.

---

# 35. Decimal Merge Safety

Never merge financial partition totals through binary floating point.

Use existing exact decimal semantics.

---

# 36. Timeline Partitioning

Timeline rules may partition by the entity whose timeline is being evaluated.

All events for one logical entity must reach the same partition.

Do not split one member/customer/case timeline between independent workers.

---

# 37. Relationship Processing

Avoid N+1 relationship lookups.

Where a rule verifies relationships:

```text
member → beneficiary
case → person
account → premise
```

use indexed joins or grouped streaming rather than one workspace query per artifact.

---

# 38. Aggregate Rule Fusion

Investigate whether rules sharing the same:

```text
semantic type
grouping keys
ordering
```

can share a scan.

Example:

```text
contribution count
contribution total
missing contribution accounting
```

may not require three complete passes over the same ordered dataset.

Implement only when generic and clearly measurable.

---

# 39. Scan Reuse

The execution plan should identify scan-compatible rules.

A reusable scan may feed several rule evaluators.

Do not couple rule implementations together directly.

---

# 40. Rule Isolation

A shared scan must not allow one rule's failure/exception to corrupt another rule's evaluation.

Rule-level execution errors remain independently attributable.

---

# 41. Source Accounting Optimization

Source disposition validation must remain exact while minimizing repeated full scans.

Prefer store-side:

```text
GROUP BY
JOIN
NOT EXISTS
```

or streaming merge operations over per-artifact queries.

---

# 42. Target Lineage Optimization

Likewise, target lineage coverage should be checked with indexed/set-oriented processing.

No one-query-per-target behavior.

---

# 43. Expected Target Derivation

Profile expected-target construction carefully.

If transformations are repeatedly recalculated for several rules, derive expected state once and reuse it.

Do not reuse projection output as proof of correctness.

Expected state remains independently derived from:

```text
checkpoint
migration graph
transformation definitions
```

---

# 44. Expected State Cache

A Verification-local deterministic expected-state cache is acceptable.

It must not:

```text
trust target state
trust projection journal as actual target
```

and must remain disposable/reproducible.

---

# 45. Evidence Policy

Preserve the scalable Evidence policy established in PS-0.10A.

Do not generate verbose per-artifact PASS evidence simply because execution is being parallelized.

Keep:

```text
aggregate PASS evidence
detailed discrepancies
artifact disposition ledger
target lineage ledger
```

---

# 46. Evidence Construction Parallelism

Detailed discrepancy Evidence may be constructed concurrently where safe.

Canonical finalization must remove scheduling-order effects.

---

# 47. Failure Ordering

User-facing discrepancy ordering must be deterministic.

Do not allow worker completion order to determine report ordering.

---

# 48. SQLite Contention

Measure whether a single SQLite workspace becomes a concurrency bottleneck.

Do not add parallel workers that merely serialize on one writer lock.

---

# 49. Per-Partition Workspace Option

If measurement supports it, allow partition-local scratch databases:

```text
partition-000.db
partition-001.db
...
```

with deterministic merge/finalization.

This is optional, not mandatory.

---

# 50. Alternative Embedded Execution Engine

Do **not** replace SQLite merely because Medium is slow.

First exhaust measurable improvements in:

```text
schema
indexes
scans
rule planning
partitioning
concurrency
```

---

# 51. Alternative Backend Evaluation Gate

If, after those improvements, SQLite remains a demonstrated architectural bottleneck, a controlled spike of another embedded analytical engine is permitted.

Examples may include an engine suitable for:

```text
large set operations
columnar scans
grouping
joins
```

Any candidate must:

```text
work with .NET
run locally/CI
have acceptable licensing
preserve deterministic exact semantics
fit ProofShift's temporary-workspace abstraction
```

Do not adopt a new dependency without benchmark evidence.

---

# 52. Backend Abstraction

If an alternate workspace backend is explored, it must sit behind the existing/generic verification-workspace contract.

Rules must not contain backend-specific SQL.

---

# 53. Baseline Benchmarks

Before optimization rerun or recover comparable measurements for:

```text
Fast
Accepted Medium
```

Use the current PS-0.10C/PS-0.10D starting commit.

---

# 54. Fast Benchmark

The accepted current Fast result is approximately:

```text
Total runtime                 150.5 s

Processing time excluding
fixtures / Docker startup     127.5 s

Peak workload RSS             209.8 MB
```

Preserve it as a comparison point.

---

# 55. Medium Benchmark

The accepted scale baseline is:

```text
Generated source records       411,000

Checkpoint artifacts           424,750

Projected targets              429,750

Total                          3h 55m

Peak RSS                       1.076 GB

Later plateau                  ~700–720 MB

Workspace DB                   7.64 GB

WAL                            ~1.08 GB
```

Reproduce comparable workload semantics.

---

# 56. Intermediate Scaling Probes

Do not wait four hours after every optimization.

Create smaller deterministic scaling points.

Suggested source-record scales:

```text
~50k
~100k
~200k
~400k
```

Use the same scenario distribution where practical.

---

# 57. Scaling Curve

For each probe record:

```text
Verification time
records/sec
RSS
workspace bytes
WAL bytes
```

Calculate:

```text
seconds per 100k artifacts
```

or another normalized metric.

---

# 58. Detect Superlinear Behavior

The purpose of the probes is to identify whether increasing data volume causes disproportionate growth.

Investigate cases where:

```text
2x artifacts
→ approximately 4x time
```

or worse.

Do not hide this inside overall benchmark totals.

---

# 59. Performance Acceptance Target — Medium

PS-0.10D is explicitly a throughput milestone.

On comparable hardware, the same accepted Medium workload must achieve a **material** improvement.

Primary engineering target:

```text
at least 2× faster ProofShift processing
```

than the accepted Medium baseline.

If comparable baseline stage data makes Verification-only comparison more reliable, use:

```text
at least 2× faster Verification
```

as the primary comparison.

Do not count faster container startup as improvement.

---

# 60. No Fake Benchmark Wins

The following do not count:

```text
smaller dataset
fewer rules
disabled Evidence
disabled Recovery
fewer correctness checks
pre-populated workspace
warm result reuse
```

unless clearly reported as a separate diagnostic benchmark.

---

# 61. Medium Memory Guard

Do not recover speed by returning to unbounded object retention.

On the same workload, peak ProofShift RSS should remain reasonably near the accepted architecture.

Treat material regression above the accepted:

```text
1.076 GB
```

as something requiring explanation.

No fixed hard ceiling is imposed if a justified performance tradeoff is measured.

---

# 62. Medium Scratch Target

Scratch reduction is desirable but secondary to correct throughput.

Report final:

```text
workspace DB peak
WAL peak
other scratch peak
```

against the accepted approximately:

```text
7.64 GB + 1.08 GB WAL
```

---

# 63. Large Full-Pipeline Benchmark

PS-0.10D must execute a workload materially larger than the accepted Medium run through the **real full pipeline**.

Minimum preferred scale:

```text
1,500,000 generated source records
```

Preferred target:

```text
2,000,000+ generated source records
```

---

# 64. Large Pipeline

Large must include:

```text
physical source materialization
checkpoint
projection / external target preparation
target observation
Verification
source accounting
target lineage
Evidence
Recovery
qualification
report
```

It must not be generator-only.

---

# 65. Large Correctness

At minimum execute a corrected Large run through qualification.

Expected:

```text
unaccounted sources = 0
unexplained targets = 0
verification failures = 0
recovery failures = 0
QUALIFIED
```

---

# 66. Large Defect Coverage

Where runtime permits, execute the deterministic seeded defect corpus at Large scale as well.

The same:

```text
149 seeded business discrepancies
```

must still be detectable without scale-dependent false positives.

If running both defective and corrected Large doubles runtime excessively, Medium may remain the exact 149/0 dual-run gate while Large uses corrected qualification plus targeted seeded discrepancies.

Document the choice.

---

# 67. Large Memory

Measure RSS throughout the Large run.

The run must not show behavior consistent with retaining one complete managed object graph per artifact.

Scratch/disk growth proportional to workload is acceptable.

---

# 68. Large Progress

Long benchmarks must periodically report:

```text
stage
processed artifacts
total expected artifacts where known
records/sec
RSS
scratch bytes
elapsed
```

No artifact values.

---

# 69. Large Time

There is no public product SLA in PS-0.10D.

However:

```text
multi-day execution
```

for the minimum Large benchmark should be treated as a performance failure requiring investigation rather than automatic acceptance.

---

# 70. Stretch Benchmark

After Large passes, optionally attempt:

```text
5 million+ source records
```

or the existing approximately 8.22-million-record generated corpus through as much of the **real full pipeline** as practical.

This is a stretch goal, not a mandatory acceptance gate.

Do not represent generator-only performance as full-pipeline performance.

---

# 71. Disk Capacity Safety

Before long benchmarks estimate expected temporary storage.

Fail early with a useful diagnostic if available disk space is clearly insufficient.

Do not fill the machine unexpectedly.

---

# 72. Temporary Storage Location

Allow benchmark/runtime scratch location to be configured operationally.

Large enterprise migrations may require a dedicated fast volume.

This setting must not affect semantic identity.

---

# 73. Cleanup

Successful and failed Verification runs must clean disposable scratch state according to existing lifecycle policy.

Retained diagnostic artifacts must be explicit.

---

# 74. Cancellation

Partitioned/parallel execution must retain cancellation semantics.

On cancellation:

```text
stop scheduling new work
cancel active work
mark Verification incomplete/cancelled
do not finalize valid Evidence
do not qualify
```

---

# 75. Worker Failure

If one partition/rule worker fails:

```text
overall Verification cannot report clean success
```

Other work may terminate or finish according to a deterministic policy.

Record the failing rule/partition without exposing sensitive data.

---

# 76. Recovery Compatibility

Recovery must continue consuming the persisted scale-safe Verification ledgers.

Do not reconstruct complete lineage/disposition arrays to gain performance.

---

# 77. External Target Compatibility

All performance improvements must work for:

```text
ProofShift projection targets
external vendor-populated targets
```

Verification must not become dependent on a Projection Journal.

---

# 78. Connector Neutrality

PS-0.10D optimizes Verification.

Do not add special fast paths specifically for:

```text
PostgreSQL
Oracle
Db2
```

unless they concern generic streamed observation behavior.

Verification should consume canonical observed artifacts consistently.

---

# 79. Pack Neutrality

Do not optimize by checking:

```text
if Pension
```

inside Verification.

All planner/partition/index decisions must derive from generic rule metadata.

---

# 80. Pension Rule Regression

Pension remains the primary deep workload for benchmarks.

But any new execution-plan abstraction must be demonstrably reusable by future:

```text
Utility
Justice
ERP/HCM
Healthcare
```

rules.

---

# 81. Single-Thread Reference Mode

Maintain a deterministic reference execution mode:

```text
maxConcurrency = 1
```

This is useful for:

```text
correctness comparison
debugging
performance attribution
```

---

# 82. Parallel Equivalence Tests

For representative rules compare:

```text
single-threaded
partitioned/parallel
```

and assert exact semantic equivalence.

---

# 83. Benchmark Reproducibility

Dataset generation must remain deterministic.

Record:

```text
generator version
seed
scale
configuration hash
runtime version
machine/environment summary
```

with benchmark output.

---

# 84. Hardware Metadata

Performance artifacts should include safe operational metadata such as:

```text
OS
architecture
logical CPU count
available memory
runtime version
storage type if explicitly provided
```

Do not collect identifying machine names unnecessarily.

---

# 85. Benchmark Artifact

Persist benchmark results in structured JSON.

Suggested categories:

```text
scenario
dataset
environment
stages
rules
resources
scratch
throughput
fingerprints
result
```

---

# 86. Benchmark Comparison

Extend or use the existing comparison tooling to compare:

```text
baseline
candidate
```

Show:

```text
duration delta
throughput delta
RSS delta
scratch delta
```

per major stage.

---

# 87. Performance Regression Tests

Do not create brittle unit tests asserting exact milliseconds.

Instead test structural performance properties such as:

```text
query count is bounded
prepared commands reused
required index exists
partition assignment deterministic
scan shared between compatible rules
no N+1 query pattern
```

---

# 88. CI Performance Smoke Test

Normal CI should include a small enough execution to catch catastrophic regression.

Do not run the multi-hour Medium/Large benchmark on every push.

---

# 89. Scheduled / Manual Scale Workflow

Add a manually invokable or scheduled CI workflow for:

```text
Medium
Large
```

if GitHub runner resource/time limits make it sensible.

Do not make PS-0.10D acceptance depend on an environment incapable of running the workload.

Record exactly where acceptance benchmarks were executed.

---

# 90. No New Public Claims

Do not change marketing/product docs to claim:

```text
X million records per hour
```

as a guaranteed product number.

Benchmark numbers are environment-specific engineering evidence.

---

# 91. Documentation

Update:

```text
README.md
ARCHITECTURE.md
ROADMAP.md
DECISIONS.md
AGENTS.md
Copilot instructions
IDE handoff
performance/benchmark documentation
```

Add ADR(s) for:

```text
Verification Execution Plan
partition semantics
parallel determinism
workspace/index strategy
```

where appropriate.

---

# 92. Explicit Non-Goals

Do not add:

```text
S3
Azure Blob
SFTP
Parquet
REST
FHIR Bulk Data

Utility Pack
Justice Pack
ERP/HCM Pack
Healthcare Pack

Web UI
AI mapping
distributed cluster execution
production migration
production rollback
```

during PS-0.10D.

Distributed execution is deferred.

This milestone remains one-machine execution with bounded parallelism.

---

# 93. PS-0.10D Acceptance Criteria

PS-0.10D is Accepted only when:

1. Detailed Verification stage profiling exists.
2. At least ~95% of Verification runtime is attributable to measured stages.
3. A generic Verification Execution Plan exists.
4. Rule descriptors declare needed fields/keys/execution properties.
5. Required fields are materialized once rather than repeatedly reparsed.
6. High-volume rules avoid repeated full RecordEnvelope JSON parsing.
7. Index requirements are deduplicated.
8. High-volume relationships avoid N+1 queries.
9. Source accounting remains set-oriented/streaming.
10. Target lineage remains set-oriented/streaming.
11. Compatible independent rules may run with bounded concurrency.
12. Deterministic partitioning exists where applicable.
13. Partition/global rule semantics are explicit.
14. Exact decimal aggregation remains exact.
15. Single-thread and parallel execution produce identical semantic output.
16. Concurrency/partition settings do not change semantic fingerprints.
17. Evidence remains deterministic.
18. Recovery remains scale-safe.
19. External-target Verification remains supported.
20. Fast Pension remains exactly 149/0.
21. Corrected Fast remains QUALIFIED.
22. Same Medium workload is materially faster.
23. Primary target of at least 2× Medium Verification/processing improvement is achieved, or milestone remains unaccepted pending explicit review.
24. Medium RSS remains bounded and measured.
25. Medium scratch usage is measured.
26. Scaling probes no longer show an unexplained severe superlinear pattern.
27. A full-pipeline Large run of at least ~1.5M generated source records completes.
28. Large corrected run has zero unaccounted/unexplained artifacts and qualifies.
29. Large memory/scratch behavior is measured.
30. No production assurance semantics were weakened.
31. Normal remote CI passes with zero required skips.
32. No PS-0.10E scope was added.

---

# 94. Completion Report

When complete, stop and report:

## Baseline Analysis

Explain exactly where accepted Medium Verification time was spent.

Provide:

```text
stage
duration
percentage
rows/queries where relevant
```

## Execution Architecture

Describe:

```text
Verification Execution Plan
required-field projection
typed key materialization
index planning
scan reuse
partitioning
bounded concurrency
global merge
```

## Fast

Report:

```text
before
after
delta
```

for:

```text
ProofShift processing time
Verification time
peak RSS
scratch
```

and confirm 149/0 correctness.

## Medium

Compare directly with the accepted baseline:

```text
411,000 generated source records
424,750 checkpoint artifacts
429,750 projected targets
3h 55m
1.076 GB peak RSS
7.64 GB workspace
1.08 GB WAL
```

Provide new:

```text
processing time
Verification time
speedup
records/sec
peak RSS
workspace
WAL
```

## Scaling Curve

Report results for intermediate probes.

State whether time growth appears:

```text
approximately linear
mildly superlinear
materially superlinear
```

and why.

## Large

Report actual:

```text
generated source records
checkpoint artifacts
target artifacts

checkpoint time
projection time
Verification time
Recovery time
reporting time
total

records/sec
peak RSS
scratch peak

business discrepancy status
accounting status
lineage status
qualification
```

## Determinism

Compare:

```text
single worker
multiple workers
different partition counts
```

and confirm semantic fingerprint equivalence.

## Query / I/O Improvements

Describe measured changes only.

## Tests

Report:

```text
restore
build
local tests
passes
failures
local skips
remote CI
```

## Git

Report milestone commit SHA(s).

## Limitations

Document remaining throughput limitations honestly.

## Deferred

State that PS-0.10E remains deferred.

Do not begin PS-0.10E automatically.