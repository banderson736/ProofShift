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

---

# 40. Implementation Status — 2026-10-04

PS-0.10A remains In Progress. Locally implemented slices include bounded PostgreSQL write sessions and journal batches; the WAL/NORMAL disposable SQLite profile; a persisted indexed Verification ledger with store-side coverage; ledger-backed Recovery with count/semantic summaries and on-demand per-artifact coverage; typed indexed ordering keys; streaming Evidence v2 manifest/NDJSON persistence; aggregate success evidence; ReadOptions with no ignored fetch-size property; and explicit SQL Server checkpoint consistency reporting. See ADR-0013 through ADR-0017.

Latest-code Fast completed successfully on 2026-10-04: 229,128 ms total, 214,729 ms ProofShift runtime, 12,636 ms environment startup, 205,840,384-byte peak process working set, 29,400,840-byte managed heap at completion, and 162,298,952-byte temporary-workspace peak. Stage totals: checkpoint 5,884 ms; external-target Verification 89,911 ms; Projection/Verification/Recovery 117,553 ms; persisted reporting/comparison 1,749 ms. It preserved 149 defective discrepancies, zero corrected discrepancies, zero unaccounted sources, zero unexplained targets, and NOT QUALIFIED / QUALIFIED outcomes. It recorded 8,495 checkpoint artifacts, 8,595 corrected projection targets, 266,510,336 ledger bytes, 636,162 evidence bytes, and 97,087 recovery artifact bytes. The prior aggregate Fast measurement was 153,358 ms total / 140,261 ms ProofShift runtime / 202,629,120-byte peak working set; stage-level before measurements were not retained in the current workspace, so a stage-by-stage delta is not yet available.

The reusable physical runner is `scripts/pension-benchmark.ps1 -Scale fast|medium`; output includes the full pipeline run, not generator enumeration. Compare saved stage JSON with `scripts/compare-benchmarks.ps1 -Before <run.json> -After <run.json>`. The deterministic Medium tier declares 411,000 generated source rows before payload artifacts and projected targets. The 2026-10-04 Docker-backed attempt was stopped when no further SQLite writes were observed during corrected external Verification. No completed result or subsequent scenarios were recorded; a hang or its root cause has not been established. At stop, the checkpoint store was approximately 1.0 GB, corrected-run `working-set.sqlite` approximately 6.69 GB, and persisted Verification ledger files approximately 381 MB. The approximately 175 MB sampled peak belonged to the `dotnet test` coordinator, not a verified workload process; do not use it to claim bounded Medium workload memory. These are partial disk observations only, not completed-scale throughput or acceptance metrics.

Final local validation: restore and solution build passed; the full solution test suite completed with 121 total, 119 passed, 0 failed, and 2 Windows symlink-capability skips. The CLI aggregate-PASS expectation is now 9 findings instead of the former 57 artifact-level findings. Target-lineage counts include member-status split outputs and payload archives; Fast remains exactly 8,595 targets. Exact business-discrepancy assertions were not weakened.

Still outstanding for acceptance: diagnose and complete Medium with counts, stage durations, workspace/ledger/evidence sizes, throughput, verified workload working set, and managed heap; investigate Fast runtime and scratch-storage cost; retain before/after stage-level comparisons; and obtain remote Docker-backed CI with zero required skips. GitHub CLI is unavailable and these workspace changes have not been committed or pushed. Do not begin PS-0.10B/C.

---

# 41. Performance Investigation

## Recovered Fast Measurements

The former statement that baseline stage measurements were unavailable was incorrect. The retained `%TEMP%/ProofShift-PS010A-fast-wal-final/integrated-assurance/performance-run.json` is the 117.108-second run. The current pre-investigation artifact is `ProofShift-PS010A-fast-final-500bf4b36a4241328d356fc04cc8f560`; the optimized, sampled run is `ProofShift-PS010A-investigation-final-fast`. All execute the same Fast physical scenario with three external verifications and three projected assurance runs. Seconds below are actual sums of matching named stage records, not estimates.

| Stage (Seconds Unless Noted) | WAL Baseline | Pre-Investigation | Optimized/Sampled |
| --- | ---: | ---: | ---: |
| Docker startup | 11.538 | 12.636 | 12.548 |
| Source fixture load | 1.187 | 1.213 | 1.169 |
| External target fixture loads | 11.512 | 12.824 | 11.392 |
| Checkpoint (two captures) | 5.858 | 5.856 | 5.610 |
| Projection (three runs, inclusive) | 15.493 | 15.868 | 15.340 |
| Projection journal validation (three runs) | 13.298 | 23.583 | 21.104 |
| Projection target readback | 1.100 | 1.186 | 1.137 |
| Projected source/expected ingest | 10.496 | 39.711 | 22.166 |
| External source/expected ingest | 10.474 | 39.929 | 22.534 |
| Projected actual-target ingest | 4.717 | 15.553 | 9.221 |
| External actual-target ingest | 4.570 | 14.953 | 9.045 |
| Secondary ordering-index build | Unavailable | Unavailable | 3.632 |
| Projected rules (includes deferred index build) | 3.655 | 3.957 | 5.247 |
| External rules (includes deferred index build) | 3.689 | 3.752 | 5.397 |
| Projected source accounting | 0.671 | 1.730 | 1.554 |
| External source accounting | 0.607 | 1.662 | 1.606 |
| Projected target lineage | 0.624 | 3.017 | 2.843 |
| External target lineage | 0.638 | 4.240 | 4.150 |
| External lineage coverage/finalization | 0.299 | 7.252 | 7.085 |
| Ledger writes (nested in ingest/analysis) | Unavailable | 23.039 | 22.300 |
| Projected Evidence construction | 0.004 | 0.004 | 0.004 |
| External Evidence construction | 0.031 | 0.036 | 0.031 |
| Verification Evidence persistence | 0.024 | 0.024 | 0.020 |
| Recovery (inclusive) | 6.212 | 8.499 | 7.626 |
| Recovery persistence | 0.542 | 0.123 | 0.104 |
| Persisted report/compare construction | 0.299 | 0.074 | 0.070 |
| CLI reporting/compare | 2.539 | 1.490 | 1.458 |
| Workspace diagnostics | Unavailable | Unavailable | 0.512 |
| Projected Verification (inclusive) | 36.867 | 91.845 | 66.407 |
| External Verification (inclusive) | 22.601 | 75.798 | 53.837 |
| Historically reported ProofShift runtime | 104.031 | 214.729 | 165.416 |
| Runtime excluding fixtures AND Docker startup | 92.493 | 202.093 | 152.868 |
| Total | 117.108 | 229.128 | 178.301 |
| Workload peak working set (decimal MB) | 567.325 | 205.840 | 210.670 |

The historical runtime formula subtracts Fixture stages but not Environment stages. Keep that original metric for comparison; the corrected startup-free metric above also subtracts the separately measured Docker stage. Inclusive parent stages, ledger-write timing, and secondary-index timing overlap their child stages and must not be added together. The cited 20.8-second journal and 15.2-second ingest values belong to other iterations; they are not measurements from the 117.108-second artifact and are not substituted here.

## Measured Regression And Fixes

Projected/external Verification grew by 108.175 seconds combined, explaining approximately 98% of the 110.698-second historical-runtime increase. Source/expected ingest grew by 58.670 seconds, actual-target ingest by 21.219 seconds, and journal validation by 10.285 seconds. Accounting/lineage, persisted ledger finalization/integrity work, and Recovery explain most of the remainder. Evidence persistence is approximately 24 milliseconds, not a material contributor. The measured ledger-write stage is nested: counting its 23 seconds again would double-count the regression.

Two generic workspace changes were benchmarked independently. Prepared workspace command reuse reduced combined source ingest from 79.640 to 71.449 seconds and target ingest from 30.506 to 26.611 seconds. Building the wide secondary ordering-key index after ingest, rather than maintaining it on every insertion, further reduced ingest; the independently timed deferred build costs 3.632 seconds across six workspaces. Primary keys and integrity constraints remain enforced during ingest. Parameterization, WAL/NORMAL scratch settings, 128-operation bounded transactions, cancellation, and exact business assertions are preserved. No complete ledger is cached in RAM.

The remaining difference from the WAL baseline is the price of persistent, graph-scoped ledger writes/integrity checks and typed normalized key staging. It is not justified merely by being slower: Medium must still complete and show no pathological scaling. High-volume ordered rule plans use primary-key indexed key joins and SQLite temporary B-trees for multi-key ordering; they contain no JSON extraction. The secondary index serves typed-value lookups. Disk-backed sorting remains explicit rather than claiming that every multi-key ORDER BY is fully index-covered.

## Scratch And Workload Methodology

`workload-samples.ndjson` samples the executing process every five seconds, including PID, timestamp, current/peak working set, managed heap, GC counters, active stage/counts, and disjoint workspace database, ledger database, WAL, evidence, and other scratch sizes. It never records artifact values. It begins after synthetic generation and Docker startup; in-process `PerformanceRecorder` measurements cover the whole run. A sampled maximum is not a guarantee that short-lived peaks were observed. Process peak working set captures unsampled RSS peaks; managed-heap peaks are sampled.

Each workspace emits aggregate diagnostics before deletion: table row counts, commit count, batch size, database/WAL sizes, normalized input bytes, index names, and actual EXPLAIN QUERY PLAN output. This diagnostic read does not commit pending failed/cancelled writes. Fast workspaces contain approximately 128,000 ordering-key rows and 201 external / 335-336 projected bounded commits. Main databases are approximately 130 MB external / 151 MB projected; WAL at completion is approximately 21-25 MB. Internal logical input bytes include source, expected, and actual serialized records, approximately 31.5-31.6 MB per workspace; resulting main-database amplification is approximately 4.1-4.8 times that internal staging denominator. This is not a physical relational-source byte ratio, and journal/ledger/WAL add further storage.

Optimized Fast sampled heap peaked at 55.1 MB. Exact assertions still report 149 defective / 0 corrected, zero unaccounted/unexplained corrected state, corrected QUALIFIED, and preserved false-Reverse/external Verification behavior. Medium and remote CI remain pending; do not mark PS-0.10A Accepted.

Cross-run fingerprint attribution: corrected/defective graph hashes, projection fingerprints, and rule-set fingerprints match the pre-investigation run exactly. All nine relational/CSV source endpoint fingerprints match; only the two filesystem payload endpoint fingerprints differ. Filesystem source envelopes include `modifiedAt` and temporal recorded time, which change when physical fixture files are recreated. Consequently aggregate source, bound Evidence, Recovery, and dry-run fingerprints differ across independently recreated fixtures. This is an observation difference, not evidence of batch/index-dependent hashing; same-checkpoint repeat assertions and append-order-independent ledger fingerprint regressions remain the determinism checks. Do not claim blanket cross-fixture fingerprint equality.

## Confirmed Medium Coverage Bottleneck

The sampled Medium attempt was stopped at the user's request after 65.0 minutes, preserving its workspace, WAL, and samples. It had spent 2,018.6 seconds inside the first corrected external lineage-coverage query; it had not completed any assurance scenario or the full pipeline. Actual workload peak RSS was 1,063,407,616 bytes; ingest/coverage RSS stayed near 695-700 MB after fixture-loading allocation subsided. This is a partial memory observation, not a completed Medium gate.

Using the same Microsoft.Data.Sqlite version against the preserved Medium database in read-only mode confirmed the defect. The correlated provenance query chose `expected_journal_binding_idx` with only `node_key=?`, repeatedly scanning a node's expected population for each observed target. Merely rewriting the inner JOIN as EXISTS did not change the bad plan. Directing that specific lookup to the existing `expected_identity_idx` produces `node_key=? AND identity_hash=?`, followed by the source graph-scope primary-key lookup. No schema, uniqueness, coverage, durability, or fingerprint semantics were weakened.

`dotnet run --file scripts/inspect-verification-sqlite.cs -- <working-set.sqlite>` reproduces both plans and bounded original-query timings without modifying the database. On the preserved Medium data, the original 100-target sample took 148.2 ms; the indexed candidate took 1.55 ms. The candidate checked the complete observed-target population with zero uncovered targets in 8,725.7 ms. That last measurement is a predicate-count diagnostic, not a completed Verification or Medium pipeline; the original complete query was deliberately not rerun.

The production query now explicitly selects the identity index. A regression checks missing source artifacts, wrong source-node scope, wrong target-node scope, bound duplicate observations, and the exact two-column indexed seek. Verification tests pass 8/8. The complete coverage-fixed Fast run passed: 168.180 seconds total, 155.436 seconds historically reported runtime, 210,669,568-byte peak working set, and exact 149 defective / 0 corrected / 0 unaccounted / 0 unexplained / corrected QUALIFIED. External lineage coverage across all three Fast passes fell from 7.085 seconds to 0.188 seconds. Artifacts are retained under `%TEMP%/ProofShift-PS010A-coverage-fixed-fast/integrated-assurance`.

Medium must be rerun with this fix; neither the interrupted run nor the successful isolated query fulfills its end-to-end gate. Restore and solution build passed. The full local suite completed with 123 total, 121 passed, 0 failed, and 2 Windows symlink-capability skips. Medium has not been restarted, and no commit/push was made; approved publication remains conditional on completing local Fast/Medium gates. Remote Docker CI with no required skips remains required before acceptance.

---

# 42. Completed Local Gates - 2026-10-05

This section supersedes earlier in-progress observations. PS-0.10A remains In Progress pending remote Docker CI. The corrected Medium full pipeline completed successfully, with 1 passed, 0 failed, 0 skipped, in 3h 55m 27.8s test-platform duration. Its scenario stopwatch measured 14,125.385 seconds total. Source/checkpoint, Projection, independent target observation, Verification, accounting/lineage, Evidence, Recovery, qualification, CLI reporting, and comparison all executed; this is not generator enumeration or an isolated query result.

## Final Fast Comparison

Final Fast artifacts: `%TEMP%/ProofShift-PS010A-acceptance-fast/integrated-assurance`. The final refresh includes the post-index scratch-peak telemetry correction. The completed Medium binary predates that telemetry-only correction; its actual file diagnostics and five-second samples provide the correct larger scratch measurements. Assurance logic is identical.

| Stage / Metric | WAL Baseline | Pre-Investigation | Final Fast |
| --- | ---: | ---: | ---: |
| Docker startup (s) | 11.538 | 12.636 | 11.467 |
| Source fixture load (s) | 1.187 | 1.213 | 1.209 |
| External target fixture loads (s) | 11.512 | 12.824 | 9.992 |
| Checkpoints (s) | 5.858 | 5.856 | 5.196 |
| Projection, inclusive (s) | 15.493 | 15.868 | 13.989 |
| Journal validation (s) | 13.298 | 23.583 | 18.770 |
| Projection target readback (s) | 1.100 | 1.186 | 1.024 |
| Source/expected ingest, six passes (s) | 20.970 | 79.640 | 38.813 |
| Actual-target ingest, six passes (s) | 9.287 | 30.506 | 16.056 |
| Deferred secondary-index build, nested in rules (s) | Unavailable | Unavailable | 3.029 |
| Rule execution, six passes (s) | 7.344 | 7.709 | 8.730 |
| Source accounting, six passes (s) | 1.278 | 3.392 | 2.547 |
| Target lineage, six passes (s) | 1.262 | 7.257 | 5.913 |
| External lineage coverage (s) | 0.299 | 7.252 | 0.156 |
| Ledger writes, overlapping child timing (s) | Unavailable | 23.039 | 19.546 |
| Evidence construction (s) | 0.035 | 0.040 | 0.031 |
| Verification Evidence persistence (s) | 0.024 | 0.024 | 0.019 |
| Recovery, inclusive (s) | 6.212 | 8.499 | 6.749 |
| Recovery/report/CLI persistence and reporting (s) | 3.380 | 1.687 | 1.611 |
| Historical runtime metric, includes startup (s) | 104.031 | 214.729 | 139.012 |
| Runtime excluding fixtures AND startup (s) | 92.493 | 202.093 | 127.545 |
| Total (s) | 117.108 | 229.128 | 150.529 |
| Peak workload RSS, decimal MB | 567.325 | 205.840 | 209.797 |

Nested stages must not be summed with their inclusive parents. The remaining approximately 35-second historical-runtime increase over the WAL baseline is concentrated in persisted Verification/key staging: combined inclusive Verification is 97.942 seconds versus 59.468 seconds at baseline. Persistence replaces the baseline's full in-memory ledger/coverage model. Prepared-command reuse, deferred secondary indexing, and the measured identity-index correction recover the material avoidable costs without restoring artifact-sized in-memory result collections. No arbitrary Fast SLA is asserted; timings are single-run observations on this host.

## Completed Medium Results

Artifacts: `%TEMP%/ProofShift-PS010A-coverage-fixed-medium/integrated-assurance`. Requested and executed scale are both 50 times Fast, without reduction: 411,000 generated source records; 424,750 checkpoint artifacts; 429,750 projected and corrected observed targets; 429,677 defective observed targets. One corrected source-plus-target population is 854,500 artifacts. Six Verification scenarios, three Projections and three Recovery assessments intentionally revisit these populations; stage denominators below reflect all matching passes.

| Medium Stage | Seconds | Measured Throughput |
| --- | ---: | --- |
| Checkpoints, two captures | 149.816 | 849,500 / 149.816 = 5,670 artifacts/s |
| Projection, three runs inclusive | 662.184 | 1,289,250 / 662.184 = 1,947 targets/s |
| Projection target readback | 33.926 | 38,002 targets/s |
| External source/expected ingest | 2,157.270 | 1,188 staged artifacts/s |
| Projected source/expected ingest | 2,188.715 | 1,171 staged artifacts/s |
| External actual-target ingest | 1,164.087 | 1,107 observed targets/s |
| Projected actual-target ingest | 1,141.945 | 1,129 observed targets/s |
| Journal validation | 3,028.730 | 851 entries/s |
| Deferred secondary-index build | 211.878 | Included in rule stages |
| Rules, external + projected | 696.871 | Finding counts are not records/s |
| Source accounting, external + projected | 238.874 | Approximately 10,669 source dispositions/s |
| Target lineage, external + projected | 1,034.279 | Approximately 2,493 lineages/s |
| External lineage coverage | 23.319 | Approximately 8 seconds per pass |
| Ledger writes, overlapping timing | 3,728.693 | 3,442 bounded write operations/s |
| Verification Evidence persistence | 0.018 | 248 persisted records, 636,288 bytes |
| Recovery, three runs inclusive | 402.838 | Aggregate coverage and isolated rehearsal |
| Persisted reporting/comparison, stopwatch | 1.636 | Includes CLI and integrity-checked projections |
| Historical runtime metric | 13,503.024 | Includes 12.525 seconds startup |
| Runtime excluding fixtures AND startup | 13,490.498 | Approximately 3h 44m 50s |
| Total scenario stopwatch | 14,125.385 | Approximately 3h 55m 25s |

## Memory And Scratch

Actual workload peak RSS: 1,076,113,408 bytes, separate from SQL Server/PostgreSQL container memory. Sampled managed-heap peak: 879,628,368 bytes; completion heap: 488,018,032 bytes. Five-second samples show fixture-loading peaks near 1.06 GB followed by generally 686-722 MB working sets during Verification/Recovery. Journal-validation samples remain between 709.5 and 718.5 MB across 606 samples. This supports stage-local plateaus rather than one retained ledger object per processed artifact. Peak RSS grew about 5.1 times for 50 times the artifacts; two tiers do not establish an asymptotic memory bound. Synthetic fixture arrays remain part of benchmark memory, and high-failure/fan-out workloads remain a limitation.

The sampled maximum main workspace database is 7,637,086,208 bytes; individual completed workspace diagnostics report approximately 1.08 GB WAL, yielding approximately 8.72 GB database plus WAL for the largest projected workspace. All active SQLite WAL files together peaked at 1,720,334,904 sampled bytes. The legacy Medium `temporaryWorkspacePeakBytes=6,568,544,368` is a pre-secondary-index write-bound measurement and is not the true final scratch peak; the telemetry correction now samples immediately after deferred index construction. Final Fast's corrected workspace peak is 174,725,496 bytes.

Six retained Verification ledgers total 13,262,045,184 bytes, versus 266,510,336 at Fast, approximately proportional to workload. Checkpoints total 1,001,466,746 bytes. Persisted Verification Evidence is 636,288 bytes; Recovery artifacts total 97,548 bytes. These are distinct persistent/temporary categories, not one per-run memory graph. Workspace diagnostics show 6,417,000 ordering-key rows per clean Medium workspace, 10,034 external / 16,749 projected bounded 128-operation transactions, and approximately 1.58 GB source/expected/actual normalized JSON staging. Main workspace amplification against that internal staging denominator is approximately 4.1-4.8 times, similar to Fast. A physical relational source-plus-target byte ratio was not measured and is not fabricated.

## Remaining Performance Limits

Projection and Recovery scale near the 50-times population increase, while inclusive Verification grows approximately 106-112 times against the same pre-telemetry Fast binary. Ledger-write time grows approximately 171 times and journal validation approximately 145 times. The dominant costs are indexed SQLite writes, typed field staging, ledger serialization/integrity work, and per-entry graph/journal validation against larger disk-backed stores. This is above-linear measured wall time, not a claim of perfect linear scaling. Captured coverage/journal plans use the required identity/ancestry indexes; rows, bounded transaction counts, and disk growth are approximately population-proportional. The earlier node-prefix coverage scan is eliminated. No new evidence of the same repeated full-node scan was observed in these captured paths, but disk I/O and transaction/index tuning remain performance limitations, not a promised throughput SLA.

## Correctness, Consistency, And Validation

Fast and Medium both assert exactly 149 defective discrepancies, 0 corrected discrepancies, 0 corrected unaccounted sources, 0 unexplained targets, and corrected QUALIFIED, including false-Reverse and external-target scenarios. SQL Server defaults to Observed; stronger consistency requires explicit isolation, permitted downgrade is reported, and no database-level settings are enabled automatically. PostgreSQL retains endpoint repeatable-read semantics, not global atomicity. Relational `ReadOptions.BatchSize` was removed rather than advertising an ignored fetch-size promise; provider streaming controls physical fetching.

Final restore/build passed with 0 warnings and 0 errors. Full local tests: 123 total, 121 passed, 0 failed, 2 Windows symlink-capability skips. Same-checkpoint repeat and append-order regression tests remain the fingerprint checks; independently recreated physical-file fixtures retain the source-timestamp attribution documented above. Remote Docker CI is the remaining acceptance gate. The user approved scoped commit/push after these local gates. No connector/domain expansion, production migration/rollback, or PS-0.10B/C was started.