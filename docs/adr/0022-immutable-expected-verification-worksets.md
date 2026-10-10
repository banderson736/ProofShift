# ADR 0022: Immutable Expected Verification Worksets

- Status: Accepted as part of PS-0.10D (merged as 091e7e3)
- Date: 2026-10-07

## Context

Saved ledger-candidate 100k telemetry attributes 1,979.640 s of Verification wall time. Six workspaces each ingest 103,348 checkpoint sources and derive 104,564 expected rows. Source/expected ingestion totals 664.414 s exclusive, including run-specific source ledger registration. The scenario contains two exact compatible input groups, each used three times. It can eliminate repeated immutable materialization without sharing actual observations or run conclusions.

## Decision Under Test

Add an optional, sequential, orchestration-lifetime `VerificationExpectedWorksetScope`. Existing service callers without a scope retain fresh materialization. The physical benchmark owns one scope across external and projected Verification and disposes it after the scenario; there is no global cache manager or persistent cross-process reuse.

The exact key binds checkpoint ID, checkpoint manifest hash, source fingerprint, configuration hash, full graph hash, deterministic source/expected field/key requirement fingerprint, and the existing ProofShift runtime version. Checkpoint manifests bind connector versions/normalization and exact typed source state; graph hashes bind mappings and transform versions. The cache format and requirement schema are versioned. Actual target state and operational scratch paths/batch/cache/worker settings are excluded. Even recovery-only graph changes remain separate groups because full graph identity differs; no approximate compatibility is inferred.

Build from the validated checkpoint and graph transformations only. Source and expected tables, comparison hashes, typed keys, identity/ancestry metadata and expected indexes are materialized once. No Projection Journal, actual target tables/values, target-specific disposition/lineage conclusions, findings, Evidence or Recovery receipts enter the shared database. Empty run-specific tables are removed before publication.

Persist Building state before population. After bounded ingest, build expected indexes, validate SQLite integrity, checkpoint/truncate WAL, close the writer, hash the complete database and publish a Complete manifest last. Bind input key, counts, field/key schema identity and content checksum. Set the database read-only. Publication transfers ownership to the scope only after success; failed/cancelled builds are removed and cannot be reused. Corrupt completed entries are quarantined Failed and rejected without silent rebuilding.

Each target run gets its own SQLite actual/journal overlay and immutable read-only `ATTACH` (`mode=ro&immutable=1`) to the completed expected database. Route only controlled internal source/expected read-table identifiers and role-specific typed-key reads to the attachment. Preserve real tables/index hints, declared-field guards, typed values, graph scope and indexed coverage queries; do not union actual and expected key rows into a view. Record query plans for cross-database operations before claiming a scalable benefit.

Validate manifest and whole-database checksum on every reuse/attachment. Mismatched inputs or plans fail closed. Target cancellation/failure disposes only its own overlay/ledger and cannot change a completed shared workset. Every target remains independently observed. Source registrations, dispositions, lineage, journal conclusions, Evidence and durable ledger completion remain per-run under ADR-0021. Source registration still streams every artifact into each ledger; source-ledger deduplication is not part of this slice.

The cache is operational scratch, not Evidence or a checkpoint. Its identity/checksum does not enter semantic Verification/Evidence/Recovery fingerprints. Full checkpoint artifacts remain authoritative. Deleting/rebuilding the cache changes no semantics.

## Validation and Measurement

Test fresh versus reused expected records, independent actual overlays, exact mismatch guards, corruption rejection without rebuilding, cancellation while Building and after Complete, and immutable write guards. Physical external fresh/repeated Verification compares target/rule/Evidence and ledger semantic fingerprints; existing Recovery, false-Reverse and qualification assertions remain. Require two builds/four reuses across the six compatible benchmark invocations.

Record actual builds/reuses, source/expected rows and projected JSON bytes, index time, checksum/attachment time, independent actual ingestion, query plans, separate expected DB/workspace/ledger/WAL/staging bytes and RSS. Rows/bytes not rewritten are structural counts, not a hypothetical saved-time claim. Measure end-to-end Fast/50k/100k; compare both accepted and retained-ledger timings.

The cumulative 100k gate is at least 20% lower ProofShift runtime against accepted 2,723.773 s (approximately at most 2,179.018 s), not another independent 20% over retained 2,322.632 s. Run 200k only after that gate; Medium remains blocked until credible 200k scaling. No Large, broad concurrency, partitioning, scan fusion, alternative engine, PS-0.10E or production execution/rollback in this experiment.

## Initial Measured Gates

Fast and 50k each passed 2/2 physical tests without failures/skips and asserted two builds/four reuses. Fast runtime was 147.959 s; 50k was 919.796 s, 26.84% below accepted code and 10.97% below retained ledger. At 50k, RSS increased 7.17% versus retained ledger and the completed expected cache peaked at 647,573,504 B; memory/disk tradeoffs are explicit. These results warrant 100k, not automatic 200k or Medium.

The small physical projection fixture also proves fresh/reused finding, ledger and Evidence equality plus matching Recovery assessment/plan/rehearsal fingerprints and qualification/reasons, including cancellation/rule-failure/defective-target isolation. Recovery Evidence binds its specific Verification run, so cross-run dry-run provenance fingerprints are not falsely required to match; the existing same-run determinism assertion remains unchanged. No semantic fingerprint version changed.

## Cumulative 100k Gate

The combined candidate passed 2/2 physical 100k tests without failures/skips at 1,939.660 s ProofShift runtime: 28.79% below accepted 2,723.773 s and 16.49% below retained ledger 2,322.632 s. Verification was 1,620.262 s; two builds/four reuses avoided 702,549,016 projected JSON bytes of repeated writes. RSS was essentially unchanged versus ledger (+0.09%), but same-sample cache-plus-overlay scratch peaked at 2,043,514,880 B versus ledger workspace 1,395,417,088 B. This tradeoff is explicit.

The cumulative 20% gate passed, authorizing the same-semantic 200k probe. Medium remains unrun and gated on credible 200k scaling; PS-0.10D is still In Progress and unaccepted.

## 200k Result and Retention

The authorized 200k probe passed 2/2 without failures/skips, preserved exact 149/0, Recovery/false-Reverse/qualification and two builds/four reuses. Runtime was 4,469.994 s, Verification 3,830.091 s. Compared with 100k, runtime grew 2.305x and Verification 2.364x; normalized costs increased 15.23% / 18.19%. Disk populations approximately doubled and workload RSS grew 1.516x. At 200k, two builds/four reuses avoided 1,405,124,124 projected JSON bytes of repeated writes, while same-sample cache-plus-overlay scratch peaked at 4,090,568,704 B.

Retain this measured cumulative optimization, not a production SLA or completed milestone. (Historical at the time of this ADR: Medium and Large were later run; see ADR-0023 and the PS-0.10D report for the final results.) Medium and Large remain unrun. Increasing normalized Verification cost supports pausing Medium for an explicit follow-up decision rather than automatically spending a multi-hour acceptance run. Stop this slice; no concurrency, scan fusion, alternative backend or further performance experiment is authorized by retention alone.

## Alternatives

- Rebuild every source/expected workspace: existing fallback and comparison baseline; repeats deterministic transformations/serialization/index population.
- Copy completed expected rows into each actual database: rejected as the primary candidate because it retains large write duplication.
- Global persistent cache: deferred; benchmark-lifetime ownership is sufficient and limits invalidation complexity.
- Union views or general cross-rule fusion: deferred; real attached tables preserve existing identity-index constraints and isolate this experiment.

## Windows Runtime Note

URI-aware parent connections and SQLite Windows drive URI syntax are required for read-only attachment. Scratch locations remain outside semantic identity. Existing Windows path-length limits remain operational constraints; short benchmark output roots are used.