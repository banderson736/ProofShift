# PS-0.10D Initial Profile and Experiments

**Status:** In progress; PS-0.10D is not accepted.

**As of:** 2026-10-09

**Current status summary (supersedes any earlier "Medium failed" wording below, which is retained as history):** the authoritative Medium result is workers=8, ProofShift processing 5,574.083 s versus the accepted 13,234.382 s baseline (2.374x speedup), 1,043.108 s under the 6,617.191 s formal maximum and 725.917 s under the 6,300 s strong-result threshold: **PASS**. The earlier workers=4 Medium run (6,794.325 s) remains a valid historical gate failure. The single authorized Large run then completed the full physical pipeline at exactly 1,500,000 generated source records (see "Authorized Large Full-Pipeline Run" at the end). PS-0.10D remains **In Progress** and unaccepted until the remaining validation, vendor, commit and remote CI gates pass and the final report is reviewed.

This report records the accepted-code baselines, profiling instrumentation, scaling probes, and optimization experiments completed so far. The original assignment document, `docs/COPILOT_PS0_10D_VERIFICATION_THROUGHPUT.md`, remains unchanged; the later explicit assignments governed these runs.

## Starting Point and Method

- Measurement began from `main` at `bd6c51d`, the accepted PS-0.10C state. The tracked worktree was clean; the supplied PS-0.10D brief was untracked.
- Fast and Medium use `scripts/pension-benchmark.ps1` and the physical Pension pipeline: synthetic source materialization, SQL Server/PostgreSQL/filesystem/CSV operations, checkpoint, projection, external observation, Verification, Recovery, and reporting.
- Fast and Medium results are under `%TEMP%\ProofShift-PS010D-baseline-fast-20261006-c7ceed02757c40ba9d2a014433604569` and `%TEMP%\ProofShift-PS010D-baseline-medium-20261006-2ec71d94a59f413daea3d899bfa018ac`.
- The instrumented Medium run and scaling probes retain structured JSON and workload samples in their respective `%TEMP%\ProofShift-PS010D-*` output directories.
- Test host: Windows, `net10.0|x64`; Docker Desktop reported 62.75 GB host memory. Storage type and detailed CPU model were not recorded.

## Unmodified Baselines

| Scale | Generated records | Checkpoint artifacts | Corrected targets | Total | ProofShift runtime | Peak RSS | Correctness |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Fast | 8,220 | 8,495 | 8,595 | 168.724 s | 156.901 s | 210,579,456 B | 149 defective / 0 corrected; qualified |
| Medium | 411,000 | 424,750 | 429,750 | 13,896.496 s | 13,234.382 s | 1,091,010,560 B | 149 defective / 0 corrected; qualified |

The accepted Medium baseline used approximately 7.64 GB for the largest workspace database and 1.08 GB for WAL. In the fresh sampler, the corresponding high-water values were 7,637,086,208 B for workspace and 1,725,085,264 B for WAL; these represent different scratch categories and should not be conflated with the aggregate temporary-workspace peak.

## Profiling Added

Instrumentation is aggregated per operation category and does not record artifact values.

- `VerificationService` splits projection-journal handling into workspace append, ledger append, and journal read/validation time.
- Workspace query metrics record execution count and returned rows/records for journal validation, ordered artifact reads, lineage streams, and source-fact streams.
- Workspace diagnostics capture query plans for source disposition, graph-derived source disposition, projection lineage, attribute comparisons, ordered reads, target coverage, and journal validation.
- Workspace journal writing records aggregate command preparation/parameter binding time, SQLite execution time, and transaction flush time. Flush time is further split into commit, scratch high-water observation, and transaction disposal.
- Regression tests assert metric structure and meaningful index use, not exact elapsed milliseconds.

The first fully profiled Fast run took 178.103 s total / 165.565 s ProofShift runtime, about 5.5% above the untouched Fast baseline. The instrumented Medium run took 14,033.331 s total / 13,411.237 s ProofShift runtime, about 0.98% / 1.34% above the untouched Medium run. These differences are measurement overhead plus normal run-to-run variance, not optimization results.

## Medium Profile

The profiled Medium run passed 2 tests, with 0 failures and 0 skips. It retained 424,750 checkpoint artifacts, 429,750 corrected targets, 149 defective discrepancies, 0 corrected discrepancies, 0 unaccounted sources, 0 unexplained targets, and `QUALIFIED` corrected output.

| Verification stage | Inclusive time | Calls / records |
| --- | ---: | ---: |
| Verification | 7,285.392 s | 3 calls |
| External target Verification | 4,771.476 s | 3 calls |
| Verification ledger writes | 3,761.924 s | 12,832,403 operations |
| Projection journal validation | 3,085.188 s | 2,578,500 entries |
| External source and expected-target ingest | 2,196.203 s | 2,563,500 artifacts |
| Workspace source and expected-target ingest | 2,148.885 s | 2,563,500 artifacts |
| External target read-back and ingest | 1,128.058 s | 1,289,177 artifacts |
| Actual target read-back and ingest | 1,119.754 s | 1,289,177 artifacts |
| Verification ledger lineage writes | 922.352 s | 2,578,411 operations |
| External target lineage analysis | 722.534 s | 1,289,161 artifacts |

These stages are nested/inclusive and must not be summed as exclusive time.

The 3,085.188 s journal-validation stage decomposed into:

| Component | Time across 3 passes |
| --- | ---: |
| Workspace journal appends | 1,614.761 s |
| Ledger journal appends | 1,415.194 s |
| Journal streaming and graph validation remainder | 55.233 s |

Query-specific Medium measurements included:

| Query/read category | Time | Returned rows/records |
| --- | ---: | ---: |
| Ordered expected-target reads | 232.857 s | 4,686,000 |
| Ordered actual-target reads | 229.665 s | 4,805,792 |
| Graph-derived lineage stream | 65.827 s | 1,289,161 |
| Projection lineage stream | 64.465 s | 1,289,250 |
| Graph-derived source disposition facts | 62.324 s | 1,274,250 |
| Source disposition facts | 40.670 s | 1,274,250 |
| Journal integrity query | 45.348 s | 3 executions |

Ordered-read plans seek identity and ordering-key indexes but also use temporary B-trees for sorting. The relationship and aggregate rule stages together were about 690 s, roughly 5% of ProofShift runtime; even ideal parallelization of only those stages cannot explain or remove the dominant multi-hour cost.

Profiled Medium resources: peak RSS 1,086,251,008 B; aggregate temporary-workspace peak 8,723,311,760 B; sampler workspace high-water 7,637,086,208 B; ledger high-water 13,265,096,704 B; WAL high-water 1,725,085,264 B. These were effectively unchanged from the unmodified baseline.

## Exclusive Attribution, Ledger Amplification, and Initial Plan

The recorder now links nested stages to parent scopes and subtracts the union of child intervals, so overlapping children are not double-counted. The nested/overlapping scope regression passes. In the final fixture-stabilized Fast profile, the sum of exclusive stages matches the 151.071 s summed inclusive duration of recorded top-level Verification scopes (100% of those recorded intervals). This does not establish 95% attribution against Medium Verification wall time; the Medium hierarchy profile remains outstanding.

The ledger now reports successful insert-command executions, affected row counts, and UTF-8 byte totals for serialized disposition, lineage, and journal payloads. The latest physical Fast run passed both E2E tests and retained the exact business assertions. Across its six ledger stores, 256,553 API write calls resulted in 359,620 inserted rows (1.402 rows/API call) and 82,702,264 serialized payload bytes:

| Ledger table | Successful insert commands | Inserted rows |
| --- | ---: | ---: |
| graph_nodes | 138 | 138 |
| sources | 50,970 | 50,970 |
| targets | 51,424 | 51,392 |
| dispositions | 50,970 | 50,970 |
| lineage | 51,481 | 51,481 |
| lineage_sources | 51,481 | 51,481 |
| journal | 51,570 | 51,570 |
| journal_scope source rows | 25,785 | 25,785 |
| journal_scope target rows | 25,785 | 25,785 |
| metadata finalization | 6 | 48 |

At Fast scale, this explains why the API-operation counter is not an SQL-row count: `AppendLineageAsync` also writes its source bindings, `AppendJournalEntryAsync` also writes unique source/target scope rows, and finalization writes eight metadata rows per ledger. The 32-row gap between target insert commands and rows reflects conflict-ignored duplicate registrations. The earlier Medium total of 12,832,403 API operations predates table-level telemetry and remains unreconciled by table; Fast ratios are not extrapolated to Medium. Counts exclude schema DDL and read queries; payload values are never recorded.

Provider-owned descriptors now resolve option-bound, side-specific fields, semantic types, grouping/ordering/lookup keys, and conservative execution flags into a deterministic plan before workspace creation. The graph contributes selector identity fields by source/target role; generic attribute comparison contributes graph-mapped target fields when no attribute is configured. Both projected-target and external-target Verification pass the same plan to the workspace.

The workspace stores projected typed JSON, expected/actual comparison hashes, and typed key rows only for the role/semantic union. Full source fingerprints are still computed from the original records before projection; expected values are derived from the full checkpoint artifact and configured graph transformations before projection. Artifact IDs, endpoint identity, semantic type, full-record fingerprints, source accounting, journal ancestry, lineage, and Recovery ledgers remain unchanged. The workspace remains generic rather than using per-Pension tables. Rules receive a declared field set; Pension field readers and workspace ordering/lookup calls fail with `PSRULE008` when a field/key is undeclared. The existing generic typed-value table is retained and populated only for plan-required keys; its shared secondary index is created after ingestion from the deduplicated plan index requirement.

Descriptor coverage now includes generic attribute comparison (configured attribute or graph-mapped target fields), generic structural/accounting rules (artifact identity/facts), and Pension member, employment, financial, relationship, election, payment, document, and code rules. Pension providers declare actual field options/defaults and key roles; the employment timeline declares both source and target fields, while other Pension semantic rules declare target fields. Cross-semantic beneficiary membership lookup is declared against the member target workset. Runtime key fields without a matching descriptor declaration fail planning. Selector identity fields are added independently from the graph. No Pension branch exists in Verification. All still-unproven rules remain non-partitionable and globally finalized.

| Provider rule family | Declared workset fields and keys |
| --- | --- |
| Generic source accounting, target lineage/presence, unexpected targets | Artifact identity and graph-derived accounting/lineage facts; no record-value fields |
| Generic entity uniqueness | Target identity facts; configured target node/semantic scope |
| Generic attribute comparison | Configured `attribute`; otherwise graph-mapped target fields, scoped by optional target node/semantic type |
| Pension member accounting | Source disposition facts; semantic type defaults to `Pension.Member` |
| Pension member uniqueness | Target `businessKey` grouping when configured; empty means artifact identity |
| Pension member presence/status | Target `businessKey` (`member_id` default) grouping; status also retains `attribute` (`status` default) |
| Pension employment timeline | Source member/start/end/status fields (`member_id`, `effective_from`, `effective_to`, `status` defaults); target member/date/event fields (`participant_id`, `event_date`, `event_code` defaults), with target semantic type option |
| Pension contribution accounting/total | Target transaction key (`transaction_id`), amount (`amount`), comparison fields (`member_id`, `period`, `category`) or configured group fields |
| Pension service-credit total | Target amount (`credit`) and grouping field (`member_id`) |
| Pension beneficiary relationship | Target beneficiary key (`beneficiary_id`), member (`member_id`), relationship (`relationship`), allocation (`allocation`); member target lookup key (`member_id`) under configured member semantic type |
| Pension retirement election | Target election key (`election_id`) and configured/default compared member/code/date fields |
| Pension benefit payment/total | Target payment key (`payment_id`), amount (`amount`), configured/default comparison or grouping fields |
| Pension document/export accounting and relationship | Target document/export key (`document_id`/`export_id`), content hash (`content_hash`), member field (`member_id`); semantic type/node options scope historical exports |
| Pension code transformation | Target code (`code`) and business key (`id`) unless configured |

Configured field options remain dynamic by design: the rule definition resolves them into explicit per-run worksets. The focused descriptor omission test fails planning for an unbound field-reference option, and the plan-driven workspace test confirms required values survive while irrelevant values are omitted. The Fast Pension run also exercises the declared-field guard across the configured Pension rules. No exercised rule currently relies on an undeclared field read.

Plan and field-projection metadata are operational and do not enter semantic fingerprints. The full artifact remains authoritative in the source checkpoint or physical target connector; workspace reads contain only the projected query representation. Detailed failure evidence continues to use stable artifact references and redacted hashes, so this slice did not add a full-artifact side copy. Typed `ValueNode` values, including exact decimals, dates, local/offset/instant timestamps, binary references, and nulls, round-trip through projected records.

## Scaling Probes

Both probes used the same deterministic Pension record mix, physical pipeline, and corrected/defective qualification checks. Each passed 2 tests with 0 failures and 0 skips.

| Scale | Generated records | Checkpoint artifacts | Targets | ProofShift runtime | Seconds per 100k generated records | Peak RSS | Workspace sampler peak | Ledger sampler peak | WAL sampler peak | Correctness |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 50k | 50,000 | 51,674 | 52,282 | 1,257.244 s | 2,514.487 s | 286.7 MiB | 0.862 GiB | 1.503 GiB | 0.196 GiB | 149 / 0; qualified |
| 100k | 100,000 | 103,348 | 104,564 | 2,723.773 s | 2,723.773 s | 385.3 MiB | 1.726 GiB | 3.008 GiB | 0.395 GiB | 149 / 0; qualified |

Doubling generated records increased runtime 2.166x, or 8.3% more time per 100k. Workspace, ledger, and WAL scratch approximately doubled; peak RSS increased 1.34x. This is mildly superlinear runtime growth over these two points, not evidence of a linear curve across the full workload.

### Plan-Driven 50k Candidate

The physical plan-driven 50k run passed 2 tests with 0 failures and 0 skips: 149 defective discrepancies, 0 corrected discrepancies, 0 corrected unaccounted sources, 0 corrected unexplained targets, Recovery `PASSED`, and corrected `QUALIFIED`. It completed in 1,333.880 s total / 1,264.607 s ProofShift processing versus the accepted 1,257.244 s baseline: a 0.59% regression, so it does not meet the runtime advancement gate.

| Metric | Accepted 50k baseline | Plan-driven 50k | Delta |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 1,257.244 s | 1,264.607 s | +0.59% |
| Peak RSS | 286.7 MiB | 290.1 MiB | +1.2% |
| Workspace sampler peak | 0.862 GiB | 0.638 GiB | -26.0% |
| Ledger sampler peak | 1.503 GiB | 1.502 GiB | -0.1% |
| WAL sampler peak | 0.196 GiB | 0.147 GiB | -25.0% |
| Full serialized record bytes observed | not measured | 1,154,137,250 B | diagnostic baseline |
| Projected record JSON bytes written | not measured | 822,824,352 B | 28.71% below full serialization |
| Physical field instances retained | not measured | 2,903,162 / 4,683,374 | 61.99% retained |

Plan-driven ingest/read-back values were: source plus expected target 172.656 s, external source plus expected target 166.870 s, actual target read-back 109.643 s, and external target read-back 102.251 s. Ordered expected reads took 26.818 s, ordered actual reads 26.462 s, and ordering-index builds 15.825 s across six workspaces. The JSON-byte and field reductions are meaningful storage changes, but the 50k runtime is not a performance win; retained-field materialization cost plus repeated reads/ledger work remain dominant.

### Plan-Driven 100k Candidate and No-Advance Decision

The final identity-preserving physical 100k run passed 2 tests with 0 failures and 0 skips in 48m 43.830s. It retained 100,000 generated records, 103,348 checkpoint artifacts, 104,564 corrected targets, exact 149 defective / 0 corrected discrepancies, zero corrected unaccounted sources and unexplained targets, passed Recovery checks, and corrected `QUALIFIED`. The two false-Reverse assertions also passed. Artifacts are under `%TEMP%\ProofShift-PS010D-workset-probe-100k-aeeadcbfb4fe47879040132bc1232d5b\integrated-assurance`.

| Metric | Accepted 100k baseline | Plan-driven 100k | Delta |
| --- | ---: | ---: | ---: |
| Total scenario time | not used for the gate | 2,921.063 s | includes fixtures/startup |
| ProofShift runtime | 2,723.773 s | 2,777.283 s | +1.96% |
| Top-level Verification inclusive time | 2,404.190 s | 2,430.043 s | +1.08% |
| Ingest/read-back stage time | 1,335.399 s | 1,257.618 s | -5.83% |
| Peak RSS | 404,017,152 B | 407,310,336 B | +0.82% |
| Workspace sampler peak | 1,853,755,392 B | 1,395,417,088 B | -24.73% |
| Ledger sampler peak | 3,230,273,536 B | 3,230,273,536 B | unchanged |
| WAL sampler peak | 424,627,864 B | 313,000,584 B | -26.29% |
| Aggregate temporary-workspace peak | 2,120,325,648 B | 1,551,174,152 B | -26.84% |
| Full serialized record bytes observed | not measured | 2,308,526,440 B | diagnostic baseline |
| Projected record JSON bytes written | not measured | 1,776,422,548 B | 23.05% below full serialization |
| Physical field instances retained | not measured | 6,510,806 / 9,367,424 | 69.50% retained |

The candidate processed 36.01 generated records/s and took 2,777.283 s per 100k generated records. Candidate top-level Verification scopes total 2,430.042 s by inclusive-scope telemetry; the table uses elapsed stage totals for the same comparison definition on both runs. Ingest/read-back sums the four non-overlapping ingest stage categories below, not their nested children. The baseline predates exclusive hierarchy telemetry; candidate exclusive ingest/read-back is 1,257.616 s, effectively the same as its elapsed stage sum. Neither comparison approaches the 30% gate.

| Ingest/read category | Accepted 100k | Plan-driven 100k |
| --- | ---: | ---: |
| External source and expected-target ingest | 444.436 s | 389.491 s |
| External target read-back and ingest | 213.581 s | 241.982 s |
| Workspace source and expected-target ingest | 454.227 s | 383.935 s |
| Actual target read-back and ingest | 223.155 s | 242.210 s |

Across six workspaces, ordering-index builds took 33.633 s. Ordered expected-target reads took 56.467 s, ordered actual-target reads 55.939 s, and ordered source reads 0.723 s. Shared index creation remains plan-driven and deferred until ingestion; no covering/composite-index experiment was performed.

**Decision: do not advance.** Exact correctness passed, but the candidate achieved neither at least 20% lower ProofShift runtime nor at least 30% lower dominant ingest/materialization time. No 200k, candidate Medium, or Large run was started. The plan-driven projection remains local as a measured scratch tradeoff, pending review; it is not a retained throughput win or milestone acceptance. PS-0.10D remains In Progress. Broad concurrency, partitioning, scan fusion, PS-0.10E, and production execution/rollback remain deferred.

Comparability note: the 50k process started before selector identity fields were added to the plan union. The final Fast rerun and completed 100k run include those mandatory fields. The 50k result remains useful for the candidate gate, but is not a bit-for-bit run of the final identity-field union; its larger JSON reduction must not be attributed solely to scale.

## Optimization Experiments

| Experiment | Evidence | Decision |
| --- | --- | --- |
| Prepared ledger command reuse | Fast aggregate ledger writes fell from ~23.17 s to ~20.86–20.95 s in two candidate runs, but whole-run timing was noisy. On Medium, ProofShift runtime rose 2.86%; aggregate ledger time improved only 0.95%, while journal validation worsened 2.54%. | Reverted; no Medium throughput win. |
| Reuse parameter objects in prepared commands | At 50k, runtime improved ~1.5%; at 100k runtime was ~0.5% slower and ledger time changed only ~0.5%. | Reverted; no scalable benefit. |
| `INSERT RETURNING` instead of `last_insert_rowid()` | At 50k, workspace append time worsened ~5.9% and journal-stage time worsened ~3.7%, despite small unrelated total-run variation. | Reverted. |
| Batch journal writes / explicit row sequences | Uncached multi-row batches raised Fast workspace append from 11.39 s to 16.65 s. Reusing prepared multi-row statements still raised it to 13.84 s. | Reverted; no 50k run warranted. |
| Workspace transaction threshold 128 to 256 | Fast journal flush time fell 8.322 s to 6.191 s and flush count 405 to 204, but whole Fast time was slightly slower and WAL rose. Two long-output-path 50k runs failed with SQLite Error 14 opening a later workspace. A short-output-path run passed, suggesting path length was a confound; however, its 50k runtime was still 1.31% slower and journal validation 3.18% slower than batch 128. | Reverted to 128. The short-path run suggests the failure was path-related, but does not make batch 256 a throughput improvement. |

## Determinism and Reproducibility Notes

- Graph, projection, and rule-set hashes matched between the untouched and profiled Fast runs. Database/CSV source endpoint fingerprints also matched.
- The filesystem payload endpoint variance came from synthetic files receiving runtime creation timestamps; the filesystem connector correctly includes `LastWriteTimeUtc` in source record semantics. The physical corpus fixture now assigns a fixed UTC timestamp to generated payload files. A regression proves equivalent file content plus timestamp produces the same source-record fingerprint, while changing the timestamp changes that fingerprint. Production connector semantics were not weakened. Full snapshot fingerprints are not persisted in the benchmark summary for direct cross-run comparison.
- Long output directory names caused SQLite Error 14 in two 50k batch-256 probes. A short output path allowed the run to complete; keep benchmark output paths short on Windows. The workspace transaction threshold is restored to 128.
- `%TEMP%` artifacts are diagnostic outputs, not committed benchmark fixtures. The existing untracked D brief was left untouched.

## Current Code and Verification

This section records the completed workset slice before the ledger continuation. The final ledger-specific validation and retention decision are recorded below.

Retained changes add Verification phase/query measurements, exclusive stage attribution, ledger table-row and payload metrics, descriptor-driven plan materialization and index planning, query-plan capture and regression assertions, and deterministic `probe-50k` / `probe-100k` scales. No candidate that improves runtime has yet been retained.

- Full `ProofShift.Verification.Tests`: 16 passed, 0 failed, 0 skipped.
- Full `ProofShift.Engine.Tests`: 3 passed, 0 failed, 0 skipped.
- Full `ProofShift.Configuration.Tests`: 12 passed, 0 failed, 0 skipped.
- Focused workspace/query-plan tests: 5 passed, 0 failed, 0 skipped.
- Focused nested recorder tests: 2 passed, 0 failed, 0 skipped.
- Final physical Fast pipeline with plan-driven projection: 2 passed, 0 failed, 0 skipped; exact 149/0 and qualification assertions passed. Total 168.268 s; ProofShift processing 155.703 s versus the untouched 156.901 s baseline (-0.76%); peak RSS 215,461,888 B versus 210,579,456 B (+2.3%). Projected JSON bytes were 23.05% below full serialized bytes observed. This small instrumented Fast timing delta is not a throughput claim.
- Filesystem timestamp/fingerprint regression: 1 passed, 0 failed, 0 skipped.
- Plan-driven physical 50k probe: 2 passed, 0 failed, 0 skipped; exact 149/0, zero corrected accounting/lineage gaps, Recovery passed, corrected `QUALIFIED`.
- Plan-driven physical 100k probe: 2 passed, 0 failed, 0 skipped; exact 149/0, zero corrected accounting/lineage gaps, Recovery and false-Reverse assertions passed, corrected `QUALIFIED`. Advancement gate failed; no larger candidate run.
- Final complete non-vendor core suite: 187 total, 185 passed, 0 failed, 2 skipped. EndToEnd: 102 passed / 2 skipped; the skips are Windows host symlink-creation capability checks. Configuration 12, Domain 11, Engine 3, Evidence 5, Graph 28, Recovery 8, and Verification 16 all passed. The initial core invocation was interrupted and does not count; the logged sequential rerun completed successfully. Log: `%TEMP%\ProofShift-PS010D-final-core-tests.log`.
- Separate Oracle and Db2 integration projects were not rerun locally; no remote CI or zero-skip acceptance gate is claimed. The dedicated final Fast/50k/100k probes each passed 2/2 without skips.
- Diagnostics: no errors reported; `git diff --check` clean.

## Remaining PS-0.10D Gaps

> Historical: this list reflects the state after the plan-driven workset slice. It is superseded by the final Medium (workers=8 PASS) and Large (exactly 1,500,000 records PASS) sections and the final gate matrix at the end of this report.

- No measured Medium throughput improvement; the assignment's 2x target is unmet.
- Approximately 95% attribution against total Medium Verification wall time is not yet demonstrated; the 100% Fast result applies only to recorded top-level Verification scopes.
- The plan now drives role/semantic field projection and the shared typed-key index. A separate covering/composite-index experiment has not been run; compatible scan reuse and one-time decoding across multiple rule scans remain open.
- No full-pipeline Large run (minimum preferred 1.5 million generated records) has been attempted.
- The 50k and final 100k plan-driven runs reduce serialized JSON and workspace/WAL scratch, but ProofShift runtime is respectively 0.59% and 1.96% slower than baseline. The 100k ingest/read-back improvement is only 5.83%. The advancement gate failed; 200k and candidate Medium remain blocked.
- The synthetic filesystem timestamp source of variance is fixed and record-fingerprint behavior is regression-tested; full snapshot fingerprint equivalence across separate physical Fast runs is not persisted or directly compared yet.
- PS-0.10D remains unaccepted and no product-performance claim has been made. ADR-0020 records only the initial plan boundary; it does not accept the milestone. PS-0.10E and production execution/rollback remain out of scope.

## Recommended Next Investigation

The ledger continuation below supersedes the initial workset recommendation: retain projection as a scratch tradeoff and investigate durable ledger fanout independently. Its completed 100k gate still does not authorize 200k or Medium. Preserve workspace batch size 128 and defer broad concurrency. A subsequent explicitly scoped investigation may measure shared scans/one-time decoding or a covering/composite index at Fast/intermediate scale; none is implemented or claimed here.

## Durable Ledger Finalization Continuation

The new explicit assignment focuses only on ledger architecture. Reduced-field workspace projection remains enabled as a clean, measured scratch optimization; planner/codec/identity guards require no alternate hot-path strategy. It is not a throughput win. Workspace batch size 128, rule execution, Evidence format, concurrency and partitioning remain unchanged. ADR-0021 records the experimental ledger boundary; the milestone remains In Progress.

### Ledger Logical Model

| Table/category | Logical fact and persistence purpose | Duplication/query role | Finalized? |
| --- | --- | --- | --- |
| graph_nodes | Node-key to scoped node-ID binding | Resolves journal node keys; graph identity is also bound by the run | Yes |
| sources | Exact graph-scoped checkpoint artifact registration | Separate from its eventual disposition; defines accounting population | Yes |
| targets | Exact graph-scoped observed artifact registration | Separate from lineage; defines independently observed population | Yes |
| dispositions | One explained disposition per source, with target references/reason | References sources but does not replace registration | Yes |
| lineage | Target provenance, scoped source references, edge path, plan hash and basis | Canonical opaque logical payload | Yes |
| lineage_sources | One source binding per lineage association | Indexed projection of lineage payload; required for scale-safe Recovery queries | Yes |
| journal | Execution/materialization result, edge/version, source/target facts and failure code | Execution ancestry is not replaced by graph-derived lineage | Yes |
| journal_scope source | Unique terminal edge/source participation | Query projection of produced/excluded journal payloads | Yes |
| journal_scope target | Unique terminal edge/target participation | Query projection of produced/excluded journal payloads | Yes |
| metadata/finalization | Completion marker, counts and integrity fingerprints | Operational lifecycle is distinct from semantic identity | Yes |

Source registration and disposition remain separate concepts/tables; their durable publication becomes set-oriented rather than independent per-artifact writes. Lineage bindings and terminal scopes can be derived from already staged opaque payloads; no audit fields, ancestry, or materialization state are flattened away.

### Existing Amplification Without a Medium Rerun

Saved final workset telemetry provides these old INSERT execution counts. Every category has the same finalized row count as INSERT executions except conflict-ignored targets and batched metadata, noted below. Base categories correspond to logical API facts; lineage_sources and journal_scope are fan-out SQL, not extra ledger API calls. Medium's 12,832,403 API operations and 3,761.924 s remain historical measured totals only; these smaller-run table counts are not extrapolated as Medium facts.

| Table/category | Fast old SQL | 50k old SQL | 100k old SQL |
| --- | ---: | ---: | ---: |
| graph_nodes | 138 | 138 | 138 |
| sources | 50,970 | 310,044 | 620,088 |
| targets | 51,424 | 313,546 | 627,238 |
| dispositions | 50,970 | 310,044 | 620,088 |
| lineage | 51,481 | 313,603 | 627,295 |
| lineage_sources | 51,481 | 313,603 | 627,295 |
| journal | 51,570 | 313,692 | 627,384 |
| journal_scope source | 25,785 | 156,846 | 313,692 |
| journal_scope target | 25,785 | 156,846 | 313,692 |
| metadata/finalization | 6 | 6 | 6 |
| Total INSERT executions | 359,610 | 2,188,368 | 4,376,916 |
| Logical write API calls (excludes Complete) | 256,553 | 1,561,067 | 3,122,231 |

Targets finalized to 51,392 / 313,514 / 627,206 rows; each run ignored 32 duplicate registrations. Metadata finalized to 48 rows across six stores. Serialized disposition/lineage/journal payloads totaled 82,702,264 / 503,394,034 / 1,006,852,774 bytes. Old aggregate ledger API write scopes took 22.850 / 324.112 / 771.092 s. These overlapping aggregate scopes are not added to enclosing Verification wall time.

### Candidate Lifecycle and Validation

Pending disk-backed staging accepts logical facts in bounded commits. Set-oriented derivation builds indexed source bindings and journal scope before pre-completion validation reads. After semantic evaluation, Finalizing freezes writes and copies the unchanged durable tables in phases capped at 16,384 rows. Staged/final counts, semantic fingerprint and coverage must match; SQLite integrity is checked. The existing Complete marker plus new Complete state are committed last in one WAL/FULL transaction. Failure/cancellation exposes no receipt or completed summary; incomplete run directories are deleted on disposal. A separate auxiliary checksum binds query-only tables without changing semantic fingerprint v1.

Regression coverage includes independent legacy INSERT-built source/target/disposition/lineage/binding/journal/scope row equality, append-order invariance, duplicate registration/scope behavior, multi-source lineage, auxiliary-table tamper rejection, six failure/cancellation cases before/mid/after copy but before Complete, and a 16,385-row bounded-copy test. Legacy metadata rows are preserved; two new operational metadata keys (`state`, `auxiliaryFingerprint`) are excluded from semantic identity. This deliberate metadata addition is recorded by ADR-0021.

### Ledger Candidate Fast Gate

The physical candidate Fast run passed 2 tests, 0 failed, 0 skipped: exact 149/0, zero corrected accounting/lineage gaps, Recovery and false-Reverse assertions, corrected `QUALIFIED`. Artifacts: `%TEMP%\PS-D-ledger-fast-6a71161a\integrated-assurance`.

| Metric | Reference | Ledger candidate Fast |
| --- | ---: | ---: |
| ProofShift runtime (accepted untouched baseline) | 156.901 s | 158.946 s (+1.30%) |
| ProofShift runtime (final workset diagnostic) | 155.703 s | 158.946 s (+2.08%) |
| Scenario total | accepted 168.724 s | 171.477 s |
| Workload RSS | accepted 210,579,456 B | 214,282,240 B (+1.76%) |
| Ledger write scopes (final workset diagnostic) | 22.850 s | staging 7.967 s |
| Added set derivation | none | 1.629 s |
| Added durable finalization, including integrity | none | 9.267 s |
| Combined measured ledger persistence | 22.850 s | 18.863 s (-17.45%) |
| Verification wall time | final workset diagnostic 106.661 s | 100.919 s (-5.38%) |
| Workspace sampler peak | final workset diagnostic 101,208,064 B | 114,155,520 B |
| Durable ledger sampler peak | final workset diagnostic 266,285,056 B | 265,887,744 B |
| Durable ledger final bytes | final workset diagnostic 266,416,128 B | 266,018,816 B |
| Staging DB sampler peak | not present | 58,302,464 B |
| Total WAL sampler peak | final workset diagnostic 21,045,024 B | 30,941,296 B |
| Durable ledger WAL sampler peak | not separately measured | 8,289,472 B |
| Ledger staging WAL sampler peak | not present | 9,204,112 B |

The durable-finalization scope totals 9.267 s inclusive / 4.713 s exclusive; table-copy and completion child scopes total 4.554 s exclusive. These children are not added again to the root. The aggregate staging-write metric is the sum of write API scopes, not another enclosing wall-time interval. Staging/derivation/finalization arithmetic here is non-overlapping measured ledger work; it does not claim full Verification attribution. Counts, payload bytes and all business semantics match the old Fast ledger: 256,553 logical write API calls remain, 82,702,264 serialized payload bytes remain, and the only new finalized metadata rows are lifecycle/auxiliary integrity keys.

Lineage-source INSERT executions fell from 51,481 to 6; source-scope and target-scope INSERT executions each fell from 25,785 to 6. Logical staging still requires 256,553 INSERT executions. Durable table publication requires 48 bounded INSERT SELECT executions across six stores; metadata uses 6 INSERTs, 6 Complete-state UPDATEs, and 6 Finalizing-state INSERTs. Including 18 derivation and 12 staging lifecycle operations, successful INSERT/UPDATE DML totals 256,649 versus old 359,610 (-28.63%). SQL operation figures exclude schema DDL, transactions, attachment, integrity/count/fingerprint reads, and failed operations; wall-time scopes include the actual corresponding costs.

Fast is not an end-to-end throughput win. Correctness, substantial ledger-scope reduction, stable RSS, and a small 1.30% end-to-end delta warranted the permitted 50k diagnostic, not automatic 100k advancement.

Focused candidate validation: Engine 3/3, Verification 23/23, Evidence 5/5, Recovery 8/8; all zero failures/skips. Receipt-integrity and lifecycle changes passed the final Verification rerun. Final post-probe validation and scale decisions remain pending.

### Ledger Candidate 50k Gate

The physical 50k run passed 2/2 with no failures/skips in 18m 33.600s. It preserved exact 149/0, zero corrected unaccounted/unexplained artifacts, Recovery/false-Reverse checks and corrected `QUALIFIED`. Artifacts: `%TEMP%\PS-D-ledger-50k-b37aed9c\integrated-assurance`.

| Metric | Accepted 50k baseline | Ledger candidate | Delta |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 1,257.244 s | 1,033.177 s | -17.82% |
| Verification wall time | 1,084.933 s | 866.737 s | -20.11% |
| Old write scopes versus new combined measured ledger work | 280.613 s | 188.231 s | -32.92% |
| Peak RSS | 300,642,304 B | 308,527,104 B | +2.62% |
| Final ledger bytes | 1,613,656,064 B | 1,612,935,168 B | -0.04% |

Candidate ledger staging writes took 84.419 s, set derivation 19.546 s, and durable finalization 84.266 s inclusive / 29.202 s exclusive; its table-copy/metadata child scopes total 55.064 s exclusive. The net combined measured ledger work is 188.231 s. Compared with the prior workset diagnostic's 324.112 s, this is -41.92%; that diagnostic is not the accepted throughput baseline. Accepted old ledger write scopes exclude completion fingerprint/count reads that lacked a separate finalization scope; the candidate includes these inside finalization, so the ledger-time comparison is conservative but not identical instrumentation coverage. Verification/end-to-end wall times use the same definitions.

Logical API writes remain 1,561,067. Staging fanout executions fell to 24 lineage-binding commands plus 21 commands for each journal-scope side; durable copy used 168 commands across six stores, with no copy exceeding 16,384 rows. Total successful INSERT/UPDATE DML is 1,561,331 versus 2,188,368 old (-28.65%). Payload bytes remain 503,394,034. Finalized table rows match the old counts; two extra operational metadata keys per store are the only intended metadata-row change.

Storage sampler peaks: workspace 696,078,336 B; durable ledger 1,612,804,096 B; staging DB 353,271,808 B; total SQLite WAL 164,589,976 B; durable ledger WAL 32,317,312 B; staging WAL 55,500,552 B. Staging and durable DBs coexist during bounded publication, so scratch increases transiently; do not add independently observed high-water values as a simultaneous peak.

**Decision: run 100k.** The accepted-baseline 17.82% end-to-end improvement and reduced ledger/Verification time meet the prerequisite that 50k indicate improvement. The 100k candidate must be compared with accepted 2,723.773 s processing / 2,404.190 s Verification / 675.124 s measured ledger write scopes, not rejected workset 2,777.283 s / 771.092 s. No 200k, Medium or Large run has started; their gates remain closed pending 100k.

### Ledger Candidate 100k Gate and Final Decision

The physical 100k run passed 2/2 with no failures/skips in 40m 56.410s. It preserved 100,000 generated records, 103,348 checkpoint artifacts, 104,564 corrected targets, exact 149/0 discrepancies, zero corrected unaccounted/unexplained artifacts, Recovery/false-Reverse assertions and corrected `QUALIFIED`. Artifacts: `%TEMP%\PS-D-ledger-100k-1068b164\integrated-assurance`.

| Metric | Accepted 100k baseline | Ledger candidate | Delta |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 2,723.773 s | 2,322.632 s | -14.73% |
| Verification wall time | 2,404.190 s | 1,979.640 s | -17.66% |
| Old write scopes versus new combined measured ledger work | 675.124 s | 478.506 s | -29.12% |
| Peak RSS | 404,017,152 B | 410,066,944 B | +1.50% |
| Workspace sampler peak | 1,853,755,392 B | 1,395,417,088 B | -24.73% |
| Durable ledger sampler peak | 3,230,273,536 B | 3,228,983,296 B | -0.04% |
| Final ledger bytes | 3,230,404,608 B | 3,229,114,368 B | -0.04% |
| Total SQLite WAL sampler peak | 424,627,864 B | 306,886,536 B | -27.73% |

Candidate total scenario time was 2,453.745 s. Staging DB peaked at 707,362,816 B, durable ledger WAL at 47,775,552 B, and staging WAL at 103,313,152 B. Staging and durable databases coexist during publication; the final ledger is not smaller in a material way. Workspace savings are inherited from reduced-field projection, not attributed to the ledger strategy. Total WAL includes workspace and ledger/staging WAL; separate peaks are not additive simultaneous usage. Fast's total WAL was higher than the workset diagnostic, so finalization does not universally reduce WAL spikes.

| Measured ledger cost | Accepted old write scopes | 100k candidate |
| --- | ---: | ---: |
| Removed direct durable write work | 675.124 s | no per-artifact durable writes |
| Logical staging API writes | none | 221.522 s |
| Set derivation of bindings/scope | none | 53.575 s |
| Durable finalization (inclusive, includes integrity) | not separately instrumented | 203.410 s |
| Durable finalization exclusive remainder | not separately instrumented | 60.581 s |
| Durable table/metadata child scopes | not separately instrumented | 142.828 s |
| Combined measured persistence work | 675.124 s | 478.506 s |

The candidate removes 675.124 s of direct-write scopes and adds 221.522 s staging + 53.575 s derivation + 203.410 s finalization, net -196.617 s of measured ledger work. Verification wall time improves 424.550 s and ProofShift processing improves 401.141 s; do not ascribe every wall-time delta solely to ledger metrics or add nested child scopes twice. The old write metric lacks separately timed completion/integrity reads; the candidate includes them, as noted for 50k. Against the rejected workset's 771.092 s write metric, ledger work improves 37.94%, still below 40%; that is not the advancement baseline.

### 100k Operations and Row Equivalence

| Table/category | Old INSERT executions | Staging INSERT executions | Set derivation executions | Durable copy executions | Finalized rows | Copy duration |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| graph_nodes | 138 | 138 | 0 | 6 | 138 | 0.012 s |
| sources | 620,088 | 620,088 | 0 | 42 | 620,088 | 24.084 s |
| targets | 627,238 | 627,238 | 0 | 42 | 627,206 | 22.417 s |
| dispositions | 620,088 | 620,088 | 0 | 42 | 620,088 | 7.777 s |
| lineage | 627,295 | 627,295 | 0 | 42 | 627,295 | 10.346 s |
| lineage_sources | 627,295 | 0 | 42 | 42 | 627,295 | 23.453 s |
| journal | 627,384 | 627,384 | 0 | 39 | 627,384 | 30.931 s |
| journal_scope source | 313,692 | 0 | 39 | shared below | 313,692 | shared below |
| journal_scope target | 313,692 | 0 | 39 | shared below | 313,692 | shared below |
| journal_scope combined copy | included above | 0 | included above | 39 | 627,384 | 23.418 s |
| metadata/lifecycle | 6 | 12 lifecycle DML | 0 | 18 durable DML | 60 durable metadata rows | completion child 0.391 s |

Rows in the primary fact/association/scope tables match old finalized counts exactly. Metadata retains the original 48 semantic/count/completion rows and adds 12 operational state/auxiliary-checksum rows across six stores; this is the intentional ADR-0021 metadata change. The 32 ignored target registrations remain ignored. Staging API calls remain 3,122,231, so this is SQL/fanout/durability-boundary reduction, not fewer logical API facts. Payload serialization remains 1,006,852,774 B.

Successful INSERT/UPDATE DML decreases from 4,376,916 to 3,122,675 (-28.66%): 3,122,231 logical staging inserts, 120 derivation executions, 294 durable fact-copy executions, 12 staging lifecycle DML, and 18 durable lifecycle/completion DML. Counts exclude reads, schema/PRAGMA/ATTACH operations, transactions and failed operations; their actual costs remain in wall-time scopes. No durable copy exceeds 16,384 rows. Multi-source fanout derivation is bounded by parent-fact windows; a single fact may have multiple source associations, so association-row count is not falsely claimed to equal the parent-row bound.

### Scaling and Retention

Candidate 50k to 100k runtime grows 2.248x, from 2,066.353 to 2,322.632 s per normalized 100k generated records (+12.40% normalized cost). Ledger staging/derivation/finalization grows from 188.231 to 478.506 s (2.542x). This remains superlinear over the measured pair; the candidate does not establish linear scaling or a production SLA.

**Decision: no advancement and no Medium.** The 100k candidate meets neither at least 20% end-to-end improvement nor at least 40% ledger-time reduction plus clear end-to-end improvement. No probe-200k, Medium or Large run was started, and no 200k scale was added. Retain the implementation locally as an experimental, correctness-tested ledger improvement for review, not as a gate-passing throughput solution. Reduced-field projection remains a scratch optimization. Further tuning, workspace/rule redesign, concurrency, partitioning, PS-0.10E and production execution/rollback are outside this completed experiment.

### Final Ledger Slice Validation

- Required final focused suites: Engine 3/3, Verification 24/24, Evidence 5/5, Recovery 8/8; 40 passed, 0 failed, 0 skipped.
- Physical Fast, 50k, and 100k: 2/2 each, all zero failures/skips, exact 149/0 and Recovery/qualification behavior preserved.
- Regression coverage includes independent legacy fact/payload row equivalence; duplicate registrations and journal scope; multi-source lineage; semantic append-order fingerprints; receipt-bound auxiliary integrity; lifecycle metadata tamper rejection; bounded finalization; six failure/cancellation cases before/mid/after rows but before Complete; and post-marker telemetry failure that revokes caches/receipt, rejects reopening, and deletes incomplete output.
- The last production change is failure-path-only cache/receipt revocation after an exception. The successful staging/finalization data path and measured physical results are unchanged; the added regression and all four required focused suites passed afterward. No redundant physical scale rerun was performed for this failure-only repair.
- Diagnostics report no errors and `git diff --check` is clean. Changes remain local and uncommitted; no publication occurred. Full solution, vendor integration and remote CI were not rerun for this ledger slice and are not claimed as satisfied acceptance gates.

PS-0.10D remains In Progress and unaccepted. Medium's 2x target, full large-scale execution and remote zero-skip acceptance remain unmet. This candidate does not warrant Medium, and no 200k/Medium/Large run was started. Stop this experiment here; PS-0.10E and production execution/rollback remain out of scope.

## Shared Immutable Expected-State Continuation

The new assignment retains the measured ledger optimization, planner/guards, reduced-field projection as scratch optimization, exclusive telemetry and deterministic fixtures. The original accepted 100k performance baseline remains 2,723.773 s; improvements are cumulative. The earlier ledger no-advance decision is historical, not a direction to revert it. No Medium or Large is authorized by this continuation.

### Saved Residual 100k Profile

Before new implementation, analyze `%TEMP%\PS-D-ledger-100k-1068b164\integrated-assurance\performance-run.json`; do not rerun it for existing measurements. Follow stageSequence/parentStageSequence from the six Verification roots and sum only exclusive scopes. Aggregate API/query metrics lacking hierarchy IDs are excluded from this sum.

| Exclusive category | Saved 100k time |
| --- | ---: |
| Source and expected ingest | 664.414 s |
| Projection journal validation | 416.000 s |
| Actual target ingest/read-back | 372.815 s |
| Durable ledger finalization, exclusive parent plus children | 203.409 s |
| Rule execution | 97.621 s |
| Ledger binding/scope derivation | 53.575 s |
| Shared ordering-index construction | 26.346 s |
| Remaining named accounting/lineage/coverage/setup scopes | 145.460 s |
| Sum | 1,979.639640 s |
| Verification root elapsed time | 1,979.640161 s |

The difference is 0.000521 s of recorder boundary/rounding; this attributes essentially 100% of the saved candidate Verification scopes, not Medium. Source/expected ingest includes independent ledger source registration and is not claimed to be entirely reusable work. Journal, actual observations and durable-ledger conclusions are not shared.

### Measured Duplication

Six workspaces each ingest 103,348 sources and derive 104,564 expected rows (207,912 combined). Across the scenario: 620,088 source rows / 328,816,656 projected JSON bytes and 627,384 expected rows / 725,006,868 projected JSON bytes. Per workspace: source JSON 54,802,776 B; expected JSON 120,834,478 B. These are JSON payload bytes, not total database or index size.

Two exact compatibility groups exist. Corrected external, corrected external repeat and corrected projected use the corrected checkpoint/configuration/graph/runtime/requirements. Defective external, projected false-Reverse and projected defective use the defective equivalents. Each group has three uses; no cross-graph/checkpoint approximation is allowed, even if many transformed fields happen to be equal. The candidate must measure two immutable builds and four read-only reuses.

Previously each invocation serializes source/expected representations and populates identity, expected-value and typed-key structures. Each workspace creates one physical typed-key secondary index containing all required roles: six builds / 26.346 s. This implies six populations of source and expected keys, not twelve independently measured index builds. Source-versus-expected-versus-actual index duration cannot be apportioned from the saved mixed-index telemetry. Expected identity/binding indexes are also recreated with each workspace schema.

### Candidate Boundary

ADR-0022 records optional sequential orchestration-lifetime ownership; exact input/schema key; Building/Complete/Failed publication and integrity; readonly attached expected tables/indexes; fresh actual/journal overlays; and run-specific ADR-0021 ledgers. No expected data comes from targets or Projection Journal claims. Each Verification still opens/validates its authoritative checkpoint. Source ledger registration remains duplicated and streamed per run, intentionally outside this optimization.

Unit tests cover attached read-only guards, identical expected typed values, independent corrected/defective actual overlays, all key-mismatch categories, corruption quarantine without rebuilding, cancelled build rejection, and completed-cache survival after target cancellation. Existing default callers retain fresh materialization. Physical benchmark assertions require two builds/four reuses and fresh/repeated external Evidence and ledger fingerprint equality. Physical measurement and broader determinism/Recovery comparisons are pending; no hypothetical elapsed-time savings are claimed.

Fast is the first physical gate. Run 50k next, then 100k only with credible savings. Compare new actual end-to-end timing against both accepted baseline and retained ledger candidate. The cumulative gate is at most 2,179.018 s at comparable 100k. No 200k until it passes; no Medium until credible 200k scaling, and no Large/PS-0.10E.

### Shared Expected Fast Gate

The physical Fast candidate passed 2/2, no failures/skips, including exact 149/0, zero corrected accounting/lineage gaps, Recovery/false-Reverse/qualification checks, fresh/repeated external target/rule/Evidence and ledger fingerprint equality, and exactly two expected builds/four reuses. Artifacts: `%TEMP%\PS-D-exp-fast-1bfd3d4d\integrated-assurance`.

| Metric | Accepted baseline | Retained ledger | Shared expected candidate |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 156.901 s | 158.946 s | 147.959 s |
| Verification wall time | not separately quoted here | 100.919 s | 84.300 s |
| Source/expected preparation plus ledger source registration | not separately quoted here | 30.716 s | 14.408 s |
| Actual-target ingest/read-back | not separately quoted here | 17.565 s | 17.789 s |
| Peak RSS | 210,579,456 B | 214,282,240 B | 226,578,432 B |
| Per-run workspace sampler peak | not separately quoted here | 114,155,520 B | 60,948,480 B |
| Completed expected-cache sampler peak | none | none | 106,262,528 B |
| Final durable ledger bytes | not separately quoted here | 266,018,816 B | 266,067,968 B |
| Ledger staging sampler peak | none | 58,302,464 B | 58,302,464 B |
| Total WAL sampler peak | not separately quoted here | 30,941,296 B | 33,396,816 B |

Runtime is 5.70% below accepted Fast / 6.91% below retained ledger. Verification is 16.47% below retained ledger. RSS rises 5.74% versus retained ledger / 7.60% versus accepted Fast; this is reported, not hidden by per-run workspace reduction. Shared expected DBs coexist with actual overlays and ledgers, so compare complete scratch categories rather than only the overlay. Independently sampled peaks are not simultaneous totals.

Expected builds took 11.544 s inclusive (9.869 s source/expected ingest, 0.410 s expected-side ordering indexes, 1.265 s remaining exclusive publication/integrity work). Four reuse validations took 0.386 s, six attachments 0.366 s, and per-run source ledger registration 2.113 s. Their measured combined immutable preparation/validation/registration is 14.408 s; the old 30.716 s ingest metric excludes separately timed mixed ordering indexes while the new build includes expected-side indexes. This is a measured phase comparison, not old duration multiplied by reuse count. Actual-target reads remain independent and total 17.789 s.

Two builds materialized 16,990 sources / 9,009,280 projected JSON bytes and 17,190 expected rows / 19,858,578 bytes. Four reuses avoided rewriting 33,980 source rows / 18,018,560 bytes and 34,380 expected rows / 39,717,156 bytes. Full checkpoint artifacts remain authoritative. Expected/source typed-key indexes were populated in two builds instead of six; actual keys remain six fresh populations. There are eight total physical ordering-index builds (two expected-side + six actual-side) instead of six mixed-role indexes; total index time is 1.445 s versus 2.172 s previously. This is not a claim that every physical index count decreased.

Focused pre-gate validation: Engine 3/3, Verification 26/26, Evidence 5/5, Recovery 8/8, no failures/skips; EndToEnd project build and diagnostics clean. URI-aware Windows attachment and source coverage-before-publication are tested/validated. The measured Fast result warrants the requested 50k probe; 100k requires credible 50k savings, and 200k/Medium/Large remain blocked.

### Shared Expected 50k Gate

The physical 50k candidate passed 2/2 with zero failures/skips in 16m 36.015s: exact 149/0, zero corrected accounting/lineage gaps, Recovery/false-Reverse/qualification checks, external replay Evidence/ledger fingerprint equality, and two builds/four reuses. Artifacts: `%TEMP%\PS-D-exp-50k-88e39d98\integrated-assurance`.

| Metric | Accepted 50k | Retained ledger | Combined expected reuse |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 1,257.244 s | 1,033.177 s | 919.796 s |
| Verification wall time | 1,084.933 s | 866.737 s | 731.999 s |
| Source/expected preparation plus source ledger registration | not separately quoted here | 294.113 s | 129.574 s |
| Actual ingest/read-back | not separately quoted here | 164.781 s | 173.899 s |
| Peak RSS | 300,642,304 B | 308,527,104 B | 330,645,504 B |
| Per-run workspace sampler peak | 925,138,944 B | 696,078,336 B | 372,322,304 B |
| Expected-cache sampler peak | none | none | 647,573,504 B |
| Final durable ledger bytes | 1,613,656,064 B | 1,612,935,168 B | 1,612,615,680 B |
| Ledger staging sampler peak | not separately measured | 353,271,808 B | 353,218,560 B |
| Total SQLite WAL sampler peak | 210,898,744 B | 164,589,976 B | 126,146,256 B |

ProofShift runtime improves 26.84% against accepted code / 10.97% against retained ledger; Verification improves 15.55% against retained ledger. RSS is 7.17% above retained ledger; a smaller overlay is not claimed as lower total cache-plus-overlay scratch. Staging and durable ledger costs remain run-specific. Full scenario time was 993.313 s.

Expected builds took 100.853 s inclusive: 95.216 s source/expected ingest, 2.518 s expected-side ordering indexes and 3.119 s remaining exclusive publication/integrity. Four validations took 1.352 s, six attachments 1.991 s, and six independent source ledger registration streams 25.377 s. Combined measured preparation/validation/registration is 129.574 s versus old 294.113 s; no saved time is inferred by multiplying an old duration.

Two builds materialized 103,348 sources / 54,802,774 JSON bytes and 104,564 expected rows / 120,830,176 bytes. Four reuses avoided 206,696 source writes / 109,605,548 bytes and 209,128 expected writes / 241,660,352 bytes. Expected/source typed-key populations decreased from six to two; actual-side indexes remained six fresh builds. Eight total physical ordering builds took 9.323 s, of which 2.518 s were expected-side. Query coverage continued to use the existing expected identity-index hint against the attached database.

### Fresh/Reused Recovery Determinism

The neighboring small physical Pension projection fixture passes 2/2 after opting into a shared scope. Fresh-build versus reused repaired Verification produces identical finding JSON, ledger semantic fingerprint (covering dispositions/lineage/journal), and Evidence semantic fingerprint. The same completed expected cache remains valid across cancelled Verification, a throwing rule, defective target observation and repaired observation.

Recovery on fresh and reused equivalent Verification results has identical assessment, plan and rehearsal fingerprints, qualification status and reasons. Each assessment correctly binds its own Verification run ID. The existing same-Verification repeated Recovery assertions still require identical dry-run fingerprint. An initial attempted cross-run dry-run equality exposed the existing run-bound Recovery Evidence reference: different Verification run IDs legitimately produce different Recovery Evidence/dry-run provenance. No Recovery/Evidence canonicalization was changed and no original assertion was weakened; compare semantic Recovery results plus explicit bindings, not a false cross-run provenance equality.

**Decision: run 100k.** The 50k measured cumulative and incremental savings are credible, with exact correctness and fresh/reused Recovery checks passed. Keep the implementation unchanged. Evaluate 100k against both accepted 2,723.773 s and retained ledger 2,322.632 s; the cumulative advancement gate remains at most 2,179.018 s. No 200k, Medium or Large has started.

### Shared Expected 100k Cumulative Gate

The physical 100k combined candidate passed 2/2, zero failures/skips, in 34m 39.985s. Exact 149/0, zero corrected unaccounted/unexplained artifacts, Recovery/false-Reverse/qualification checks, replay Evidence/ledger equality and two builds/four reuses passed. Artifacts: `%TEMP%\PS-D-exp-100k-d7f1be86\integrated-assurance`.

| Metric | Accepted 100k | Retained ledger | Combined expected reuse |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 2,723.773 s | 2,322.632 s | 1,939.660 s |
| Verification wall time | 2,404.190 s | 1,979.640 s | 1,620.262 s |
| Source/expected preparation plus source ledger registration | not separately quoted here | 664.415 s | 285.936 s |
| Actual target ingest/read-back | not separately quoted here | 372.815 s | 387.907 s |
| Peak RSS | 404,017,152 B | 410,066,944 B | 410,431,488 B |
| Per-run workspace sampler peak | 1,853,755,392 B | 1,395,417,088 B | 747,196,416 B |
| Expected-cache sampler peak | none | none | 1,296,318,464 B |
| Final durable ledger bytes | 3,230,404,608 B | 3,229,114,368 B | 3,229,446,144 B |
| Ledger staging sampler peak | not separately measured | 707,362,816 B | 707,473,408 B |
| Total SQLite WAL sampler peak | 424,627,864 B | 306,886,536 B | 227,955,576 B |

Runtime improves 28.79% cumulatively / 16.49% versus retained ledger. Verification improves 32.61% cumulatively / 18.15% versus retained ledger. RSS rises only 0.09% versus retained ledger. Full scenario time was 2,077.184 s. Cache plus overlay peaked at 2,043,514,880 B in the same sampler observation, versus ledger-only workspace 1,395,417,088 B; the candidate trades higher combined scratch residence for lower repeated writes/runtime. Do not characterize the overlay-only reduction as a whole-scratch reduction. Ledger/WAL/staging remain separate.

Two expected builds took 211.844 s inclusive: 201.726 s source/expected ingest, 5.056 s expected-side ordering indexes and 5.062 s remaining exclusive publication/integrity work. Four reuse validations took 2.608 s, six attachments 3.904 s, and independent source ledger registration 67.580 s. Combined measured preparation/validation/registration is 285.936 s versus retained ledger's 664.415 s. Actual-target ingestion remains fresh and took 387.907 s, so not all phases improved. Eight total ordering-index builds took 18.157 s; two expected-side builds accounted for 5.056 s, with six actual-side populations still run-specific.

Two builds wrote 206,696 source rows / 109,605,552 projected JSON bytes and 209,128 expected rows / 241,668,956 bytes. Four reuses avoided 413,392 source writes / 219,211,104 bytes and 418,256 expected writes / 483,337,912 bytes (702,549,016 projected JSON bytes not rewritten). Full checkpoint artifacts remain authoritative. Source/expected transforms, serialization and index populations fell from six to two; no elapsed-time improvement is inferred solely from these counts.

Combined 50k to 100k runtime grows 2.109x; normalized runtime is 1,839.593 then 1,939.660 s per 100k generated records (+5.44%). This is better than the retained ledger pair but still mildly superlinear over the two measured points. No linear-scaling or production SLA claim is made.

**Cumulative gate: PASS.** 1,939.660 s is below 2,179.018 s and 28.79% below the accepted 2,723.773 s baseline. Add and run `probe-200k` only now, using exactly doubled per-kind 100k counts totaling 200,000 generated records, with unchanged rules/Evidence/Recovery and two-build/four-reuse assertions. Harness compilation and PowerShell parsing passed. Medium and Large remain unrun; Medium requires credible 200k scaling and is not automatically authorized by this 100k result.

### Gate-Authorized 200k Scaling Result

The same-semantic physical 200k probe passed 2/2 with zero failures/skips in 1h 19m 11.299s. It generated 200,000 records, checkpointed 206,696 artifacts and projected 209,128 corrected targets. Exact 149/0, zero corrected unaccounted/unexplained artifacts, Recovery/false-Reverse/qualification checks, replay Evidence/ledger equality and two-build/four-reuse assertions all passed. Artifacts: `%TEMP%\PS-D-exp-200k-1134f086\integrated-assurance`. There is no accepted original 200k baseline, so no invented 200k improvement percentage is reported.

| Combined scale | ProofShift runtime | Runtime per 100k generated records | Verification | Verification per 100k |
| --- | ---: | ---: | ---: | ---: |
| 50k | 919.796 s | 1,839.593 s | 731.999 s | 1,463.998 s |
| 100k | 1,939.660 s | 1,939.660 s | 1,620.262 s | 1,620.262 s |
| 200k | 4,469.994 s | 2,234.997 s | 3,830.091 s | 1,915.045 s |

100k to 200k grows 2.305x in ProofShift runtime and 2.364x in Verification time: normalized costs rise 15.23% / 18.19%. This is superlinear and worsens relative to the 50k-to-100k curve; it is not a catastrophic 4x-time doubling, but it is not evidence of linear scaling or a production SLA. Total scenario time was 4,747.703 s. RSS grew 1.516x while expected/overlay/ledger disk populations grew approximately 2x and total WAL 1.926x.

| 200k resource category | Peak/final bytes |
| --- | ---: |
| Workload RSS | 622,030,848 B |
| Per-run workspace sampler peak | 1,496,363,008 B |
| Completed expected-cache sampler peak | 2,594,205,696 B |
| Cache plus overlay, same-sample peak | 4,090,568,704 B |
| Durable ledger sampler peak | 6,448,922,624 B |
| Final durable ledger bytes | 6,449,053,696 B |
| Ledger staging sampler peak | 1,414,139,904 B |
| Total SQLite WAL sampler peak | 438,998,456 B |

Two expected builds took 443.999 s inclusive: 425.800 s source/expected ingest, 9.388 s expected-side ordering indexes and 8.811 s remaining exclusive publication/integrity. Four reuse validations took 14.198 s, six attachments 7.697 s and six per-run source registration streams 173.379 s; combined preparation/validation/registration was 639.274 s. Actual targets were freshly ingested in 878.023 s. Eight total physical ordering-index builds took 36.217 s; actual-side indexes remain independent. Validation time is observed, not assumed negligible at larger scales.

Two builds wrote 413,392 source rows / 219,211,084 JSON bytes and 418,256 expected rows / 483,350,978 bytes. Four reuses avoided 826,784 source writes / 438,422,168 bytes and 836,512 expected writes / 966,701,956 bytes: 1,405,124,124 projected JSON bytes not rewritten. These are measured structural counters, not a hypothetical elapsed-time win. The shared cache still contains no actual-target observations, journals, findings or durable run conclusions.

### Shared Expected Retention and Medium Decision

Retain the ledger foundation, projected scratch representation, planner/guards and shared immutable expected worksets as the cumulative measured candidate. The original 100k 20% advancement gate passed with 28.79% improvement; 200k correctness and bounded/disk-backed resource behavior also passed. However, the rising normalized Verification cost (+18.19% at the final doubling) makes a long Medium run a weak next throughput-acceptance experiment without reviewing the remaining repeated work and I/O costs. This is engineering judgment, not a fabricated numerical gate.

**Decision: keep Medium paused and stop this slice.** Medium and Large were not run. A subsequent explicit decision can use these measurements to authorize a Medium diagnostic or a focused residual-cost investigation; neither is started here. No concurrency, partitioning, general scan fusion, new embedded engine, PS-0.10E or production execution/rollback was added. PS-0.10D remains In Progress and unaccepted; Medium's 2x target and remote acceptance remain unverified.

### Final Shared Expected Slice Validation

- Required final focused suites: Engine 3/3, Verification 26/26, Evidence 5/5, Recovery 8/8; 42 passed, 0 failed, 0 skipped.
- Expected-workset tests assert exact input/plan mismatch rejection, immutable source/expected guards, independent actual overlays, corruption rejection/quarantine without rebuilding, cancelled build non-publication, and completed-cache survival after target cancellation.
- Small physical fresh/reused Verification/Recovery fixture: 2/2, no failures/skips, after its final update. Finding JSON, ledger and Evidence semantic fingerprints, Recovery assessment/plan/rehearsal and qualification/reasons match; distinct Verification provenance remains truthful. Original same-run dry-run fingerprint assertions remain intact.
- Physical Fast, 50k, 100k and gate-authorized 200k each passed 2/2 with zero failures/skips, exact assurance assertions and two builds/four reuses. No scale was run concurrently. The original saved 100k profile was analyzed without rerunning it.
- EndToEnd project builds and PowerShell harness parsing passed; 200k per-kind counts exactly double 100k and total 200,000. No correctness checks, rules, Evidence or Recovery stages were removed.
- Diagnostics report no errors; `git diff --check` is clean. All implementation/test/doc changes remain local and uncommitted; no publication occurred and supplied briefs were not overwritten.
- Full solution, separate vendor integration and remote CI were not rerun for this slice and are not claimed as acceptance. Medium's 2x target, Large execution and remote zero-skip milestone acceptance remain unmet.

The shared expected-state slice is complete and retained as a cumulative measured foundation. PS-0.10D remains In Progress and unaccepted. Medium and Large remain unrun; stop here and require an explicit follow-up decision before another throughput experiment or milestone expansion.

## Scaling Differential and Single Index-Strategy Candidate

The new assignment retains all cumulative architecture and forbids Medium/Large. Analyze saved shared-workset 100k/200k artifacts first; neither was rerun for profiling. The candidate targets the scaling differential, not only the largest 200k duration.

### Excess-Over-Linear Attribution

ProofShift excess is 4,469.994 - 2 x 1,939.660 = 590.674 s. Verification excess is 3,830.091 - 2 x 1,620.262 = 589.567 s, 99.81% of the processing excess. Following hierarchical stage IDs and summing exclusive durations reconciles the Verification excess to rounding. The table ranks named exclusive scopes; nested query/API metrics below are not added again.

| Exclusive stage | 100k | 200k | Growth | Normalized delta | Excess over 2x |
| --- | ---: | ---: | ---: | ---: | ---: |
| Projection journal validation | 407.601 s | 942.860 s | 2.313x | +15.66% | 127.658 s |
| Actual target read-back/ingest | 191.080 s | 448.524 s | 2.347x | +17.37% | 66.363 s |
| Projected rule evaluation | 51.408 s | 168.593 s | 3.279x | +63.97% | 65.776 s |
| Shared-source ledger registration | 67.580 s | 173.379 s | 2.566x | +28.28% | 38.219 s |
| External target read-back/ingest | 196.826 s | 429.498 s | 2.182x | +9.11% | 35.846 s |
| External Verification orchestration remainder | 21.469 s | 74.009 s | 3.447x | +72.36% | 31.071 s |
| Durable journal copy | 32.748 s | 94.477 s | 2.885x | +44.25% | 28.982 s |
| Staged journal scope derivation | 28.911 s | 83.856 s | 2.901x | +45.03% | 26.034 s |
| Expected workset source/expected ingest | 201.725 s | 425.800 s | 2.111x | +5.54% | 22.349 s |
| Durable journal-scope copy | 24.569 s | 71.192 s | 2.898x | +44.88% | 22.054 s |
| Durable target copy | 24.057 s | 70.010 s | 2.910x | +45.51% | 21.897 s |
| Durable source copy | 25.798 s | 71.689 s | 2.779x | +38.94% | 20.092 s |
| Durable lineage-source copy | 24.481 s | 66.603 s | 2.721x | +36.03% | 17.641 s |
| Staged lineage binding derivation | 27.566 s | 69.712 s | 2.529x | +26.45% | 14.581 s |
| Durable finalization exclusive remainder | 62.949 s | 134.990 s | 2.144x | +7.22% | 9.092 s |
| Expected workset reuse validation | 2.608 s | 14.198 s | 5.444x | +172.19% | 8.982 s |
| External target lineage analysis | 28.992 s | 66.051 s | 2.278x | +13.91% | 8.067 s |
| Projected Verification orchestration remainder | 21.612 s | 49.723 s | 2.301x | +15.03% | 6.498 s |
| Durable lineage copy | 10.225 s | 25.578 s | 2.502x | +25.08% | 5.129 s |
| Workspace diagnostics | 5.322 s | 14.949 s | 2.809x | +40.45% | 4.305 s |

The remaining individually measured accounting/coverage/index/publication/Evidence scopes reconcile the rest; this is not an unexplained multi-hour Other bucket. Orchestration remainder includes binding/checkpoint validation and untimed per-operation work, so attribution is named at the scope boundary, not falsely decomposed into CPU/I/O subcomponents. Snapshot, Projection, Recovery and reporting net excess outside Verification totals only 1.107 s after positive/negative phase deltas cancel. Actual observation and workspace ingestion share one measured boundary and cannot be truthfully split from saved data.

### Operation and Query Differential

| Nested diagnostic/category | 100k | 200k | Interpretation |
| --- | ---: | ---: | --- |
| Ledger API fact writes | 3,122,231 | 6,244,559 | approximately 2x operations |
| Ledger staging write scopes | 218.506 s | 572.273 s | 2.619x cost; not superlinear fact count |
| Ledger set derivation | 56.477 s | 153.568 s | 2.719x cost |
| Durable finalization inclusive | 212.999 s | 551.474 s | 2.589x cost; primary/secondary maintenance during copy |
| Journal-containing workspace flushes | 4,902 | 9,804 | exactly 2x transactions |
| Workspace journal commit time | 258.600 s | 577.729 s | +60.529 s excess; dominates flush cost |
| Scratch high-water observation within flush | 1.671 s | 3.580 s | small, not the dominant journal offender |
| Ordered source reads | 12 calls / 641,988 rows / 14.168 s | 12 / 1,283,976 / 28.773 s | near 2x rows/cost |
| Ordered expected reads | 78 calls / 1,140,144 rows / 49.895 s | 78 / 2,280,288 / 160.227 s | 3.211x cost, +60.437 s excess |
| Ordered actual reads | 90 calls / 1,169,132 rows / 49.610 s | 90 / 2,338,472 / 106.314 s | 2.143x cost |
| Workspace ordering-index builds | 8 / 18.157 s | 8 / 36.217 s | approximately linear cost |
| Relationship-family rules | 12 / 15.569 s | 12 / 33.323 s | modest excess |
| Aggregate-family rules | 18 / 52.545 s | 18 / 110.345 s | modest excess |
| Timeline rules | 6 / 1.466 s | 6 / 3.838 s | small absolute excess |
| Attribute/entity/accounting-family rules | 54 / 50.431 s | 54 / 159.203 s | larger per-call cost; contribution accounting leads |
| Evidence construction, external/projected combined | 0.032 s | 0.041 s | immaterial absolute cost |
| Verification Evidence persistence | 0.023 s | 0.022 s | no scaling offender |
| Recovery inclusive | 92.160 s | 193.797 s | 2.103x; nested stages not added again |
| Recovery artifact persistence | 0.092 s | 0.091 s | no scaling offender |
| Persisted report/comparison + CLI | 1.694 s | 1.501 s | no scaling offender |

Rule-family labels here are profiling classifications of configured IDs, not branches in Verification. Query plans are unchanged at both scales: grouped expected/actual reads seek identity indexes and primary typed-key bindings but use temporary B-trees for remaining ordering terms. Graph-derived lineage scans expected_identity_idx, seeks source primary bindings and covering actual identity, and uses a temporary B-tree for final ordering terms. No plan switch or failed identity seek was found. Ordered-read duration includes query execution, reads and decoding; separate sort-only time/temp bytes are not available, so TEMP B-tree presence alone is not blamed.

The beneficiary rule can issue one indexed member lookup per beneficiary when its member node is configured. Saved telemetry does not separately count those lookups or aggregate groups/ValueNode construction, so new compact per-rule lookup and complete/partial scan/row/typed-record/top-level field-decode counters are added. They record IDs/counts only, not values. Shared-scan compatibility would require equal semantic side, grouping/ordering and lookup requirements; contribution total versus contribution accounting have different group/order keys and are not automatically fused. No scan fusion or one-time decode strategy is implemented in this candidate.

### Candidate C: Deferred Ledger Secondary Index Phases

Choose exactly one primary optimization: ledger staging/finalization index timing. Saved staging and durable-copy scopes show substantial excess with approximately linear fact/DML counts. Maintain primary/unique constraints throughout staging and every bounded durable 16,384-row copy. Defer only SQL-declared secondary indexes, discovered through sqlite_master rather than domain names. Staging builds a required secondary index before its first indexed read and builds all remaining indexes before finalization. Durable destination builds all seven secondary indexes after table copies, before fingerprint/count/coverage/SQLite checks and the Complete marker.

Final table/index schema, opaque payloads, fingerprints, Pending/Finalizing/Complete/Failed/Cancelled semantics and auxiliary integrity remain unchanged. No workspace batch-size/PRAGMA change, shared-workset validation relaxation, covering index, relationship rewrite, concurrency, partitioning or alternate backend. Add staging/destination index-build timing and counts; whole ledger/WAL/scratch measurements will account for index cost. Primary random-key insertion and workspace journal commit costs remain and may limit this candidate.

Tests assert both seven-index builds, legacy row/fingerprint equivalence, bounded copies, fail-closed lifecycle, and partial scan/decode/lookup telemetry. Fast, 50k and non-regressing 100k are the gates before any candidate 200k rerun. Candidate 200k progression is preferred normalized runtime penalty at most 8%, targeting 5%, with normalized Verification penalty also materially reduced. Medium/Large remain forbidden in this slice. No new probe result or hypothetical improvement is claimed yet.

### Index-Strategy Fast Gate

The physical Fast index candidate passed 2/2 with zero failures/skips, exact 149/0, Recovery/false-Reverse/qualification checks and two builds/four expected-workset reuses. Artifacts: `%TEMP%\PS-D-idx-fast-a6d2f93f\integrated-assurance`.

| Metric | Retained shared-workset Fast | Index candidate Fast |
| --- | ---: | ---: |
| ProofShift runtime | 147.959 s | 149.257 s (+0.88%) |
| Verification | 84.300 s | 89.339 s |
| Measured staging/derivation/index/finalization work | 19.606 s | 19.029 s (-2.94%) |
| Ledger staging API scopes | 7.851 s | 6.306 s |
| Set derivation | 1.771 s | 1.261 s |
| Staging deferred index builds | maintained during writes | 0.979 s / 42 indexes |
| Durable finalization inclusive | 9.984 s | 10.483 s |
| Destination deferred indexes, child of finalization | maintained during copy | 1.235 s / 42 indexes |
| Peak RSS | 226,578,432 B | 227,012,608 B |
| Final ledger bytes | 266,067,968 B | 259,198,976 B |
| Staging sampler peak | 58,302,464 B | 37,314,560 B |
| Total WAL sampler peak | 33,396,816 B | 20,286,944 B |

Workspace/expected-cache sampler peaks are unchanged at 60,948,480 / 106,262,528 B. All seven secondary indexes exist on each staging/durable ledger before completed publication, while primary constraints protect ingestion/copy. Destination index time is already inside finalization and is not added twice. Fast is not an end-to-end throughput win; its small runtime delta, lower staged/ledger/WAL storage and slightly lower combined measured ledger work justify only the intermediate 50k scaling diagnostic.

The new telemetry recorded 180 complete scans, 242,402 returned/decoded typed records and 979,270 top-level field values, with zero partial scans in this configured Fast scenario. A focused early-disposal test proves partial scans are counted. Beneficiary membership executed 960 indexed scalar lookups across six rule invocations, taking 0.027 s: this is a confirmed per-artifact path, not a hidden claim of batched relationship processing. It is reported as a separate residual candidate; no relationship rewrite is mixed into candidate C.

Focused suites before Fast: Engine 3/3, Verification 27/27, Evidence 5/5, Recovery 8/8 (43 passed, no failures/skips), diagnostics clean. Plan/workspace/reuse/ledger lifecycle tests are included. The production index/telemetry candidate is frozen for 50k. 100k requires credible intermediate savings; 200k requires all requested smaller gates. Medium and Large remain unrun and forbidden.

### Index-Strategy 50k Gate

The candidate passed 2/2 physical tests with zero failures/skips in 15m 19.321s, preserving exact 149/0, zero corrected unaccounted/unexplained artifacts, Recovery/false-Reverse/qualification and two builds/four reuses. Artifacts: `%TEMP%\PS-D-idx-50k-a81a718c\integrated-assurance`.

| Metric | Retained shared-workset 50k | Index candidate |
| --- | ---: | ---: |
| ProofShift runtime | 919.796 s | 845.818 s (-8.04%) |
| Verification | 731.999 s | 666.888 s |
| Combined measured ledger work including index construction | 196.547 s | 124.994 s (-36.40%) |
| Staging API writes | 85.488 s | 43.455 s |
| Set derivation | 21.263 s | 10.429 s |
| Staging secondary index construction | maintained during writes | 7.307 s / 42 indexes |
| Durable finalization inclusive | 89.796 s | 63.803 s |
| Destination secondary indexes, child of finalization | maintained during copy | 7.104 s / 42 indexes |
| Peak RSS | 330,645,504 B | 331,735,040 B |
| Final ledger bytes | 1,612,615,680 B | 1,571,835,904 B |
| Staging sampler peak | 353,218,560 B | 343,654,400 B |
| Total WAL sampler peak | 126,146,256 B | 102,851,776 B |

Workspace and expected-cache peaks remain 372,322,304 / 647,573,504 B. Destination index time is inside finalization; only staging index time is separately added to ledger API/derivation/finalization to avoid double-counting. Smaller final DB size reflects index/table page packing, not removal of logical facts or final indexes. Primary/unique constraints, 16,384-row durable copies and all integrity/completion checks remain enforced.

The scenario executes 180 complete workset scans, 1,475,528 typed-record decodes and 5,960,974 top-level field-value decodes. Each rule/side/semantic stream reports six executions across the six independent Verification runs. Contribution accounting and contribution total each read 182,484 expected contribution rows and 182,422 actual rows; benefit-payment accounting and totals each read 72,984 expected/actual rows; document accounting and relationship each read 9,132 expected and 9,114 actual rows. These are observable repeated scans, not automatic proof of scan-compatible order/group semantics. Employment timeline reads 10,950 source and 10,926 actual rows. No partial scans occurred in the configured scenario; early-disposal behavior is regression-tested.

Beneficiary membership performs 5,838 indexed scalar queries across six rule calls (one per beneficiary), taking 0.157 s of measured query time. This confirmed N+1 pattern is not concealed; its small absolute measured time does not motivate mixing candidate D into this ledger-index experiment. Aggregate group-count and sort-only byte/time telemetry are not present and are not inferred as measured facts.

**Decision: run 100k.** End-to-end and full ledger-cost savings are credible at 50k. Freeze the single candidate and compare 100k against accepted 2,723.773 s and retained 1,939.660 s; material regression against retained performance would block ordinary advancement. No candidate 200k rerun yet, and no Medium/Large.

### Index-Strategy 100k Non-Regression Gate

The candidate passed 2/2 physical tests with zero failures/skips in 32m 46.346s. Exact 149/0, zero corrected accounting/lineage gaps, Recovery/false-Reverse/qualification, two expected builds/four reuses and replay Evidence/ledger equality passed. Artifacts: `%TEMP%\PS-D-idx-100k-0c827d5c\integrated-assurance`.

| Metric | Accepted original 100k | Retained cumulative 100k | Index candidate |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 2,723.773 s | 1,939.660 s | 1,821.961 s |
| Verification | 2,404.190 s | 1,620.262 s | 1,479.123 s |
| Full measured ledger staging/derivation/index/finalization | not identically instrumented | 487.983 s | 291.317 s |
| Peak RSS | 404,017,152 B | 410,431,488 B | 411,717,632 B |
| Per-run workspace sampler peak | 1,853,755,392 B | 747,196,416 B | 747,196,416 B |
| Expected-cache sampler peak | none | 1,296,318,464 B | 1,296,318,464 B |
| Final ledger bytes | 3,230,404,608 B | 3,229,446,144 B | 3,147,104,256 B |
| Staging sampler peak | not separately measured | 707,473,408 B | 688,087,040 B |
| Total WAL sampler peak | 424,627,864 B | 227,955,576 B | 196,528,216 B |

Runtime improves 6.07% against retained / 33.11% cumulatively. Verification improves 8.71% against retained. Full measured ledger work improves 40.30%, including all deferred index cost; RSS rises 0.31% versus retained. No logical facts, rules, Evidence/Recovery stages or integrity checks were removed.

Staging API scopes took 102.265 s, set derivation 27.702 s, staging secondary indexes 14.134 s (42 builds), and durable finalization 147.216 s inclusive, containing destination secondary indexes 14.384 s (42 builds). Primary/unique constraints and bounded 16,384-row transactions remain active. Index cost is explicit and included; destination child time is not added twice.

Per-rule/system scans remain 180 completed executions, reading/decoding 2,951,264 typed records and 11,922,808 top-level field values; there are no partial scans in the physical scenario. Beneficiary membership executes 11,676 indexed scalar queries in 0.375 s, approximately twice the 50k operation count. This candidate does not reduce rule scan/decoding count or the per-artifact lookup path; gains are measured in ledger maintenance timing and page packing.

**100k gate: PASS.** No regression against retained 1,939.660 s. Fast, 50k and 100k exact gates plus the pre-200k focused suite rerun passed: Engine 3/3, Verification 27/27, Evidence 5/5, Recovery 8/8, 43 passed, no failures/skips; diagnostics and whitespace checks clean.

Authorize a new candidate 200k run only now. Relative to candidate 1,821.961 s at 100k, the preferred at-most-8% normalized runtime penalty requires 200k processing at most 3,935.436 s; the 5% target is 3,826.119 s. A comparable 8% normalized Verification ceiling is 3,194.905 s. Report both penalties against retained +15.23%/+18.19%; do not reset the accepted Medium 13,234.382 s / approximately 6,617 s target. Medium and Large remain prohibited, and stage-derived planning estimates are produced only if the candidate 200k progression gate passes.

### Index-Strategy 200k Scaling Gate

The warranted candidate 200k probe passed 2/2 physical tests with zero failures/skips in 1h 12m 39.518s. Exact 149/0, zero corrected accounting/lineage gaps, Recovery/false-Reverse/qualification, two expected builds/four reuses and replay Evidence/ledger equality passed. Artifacts: `%TEMP%\PS-D-idx-200k-ced0cc2b\integrated-assurance`. No old 200k baseline was rerun just for profiling; this run followed the successful smaller candidate gates.

| Metric | Retained cumulative 200k | Index candidate 200k |
| --- | ---: | ---: |
| ProofShift runtime | 4,469.994 s | 4,078.879 s (-8.75%) |
| Verification | 3,830.091 s | 3,392.772 s |
| Full measured ledger work including index construction | 1,277.315 s | 715.245 s (-44.00%) |
| Staging API scopes | 572.273 s | 258.258 s |
| Set derivation | 153.568 s | 76.783 s |
| Deferred staging indexes | maintained during writes | 29.917 s / 42 builds |
| Durable finalization inclusive | 551.474 s | 350.287 s |
| Deferred destination indexes, finalization child | maintained during copy | 29.690 s / 42 builds |
| Peak RSS | 622,030,848 B | 622,616,576 B |
| Per-run workspace peak | 1,496,363,008 B | 1,496,363,008 B |
| Expected-cache peak | 2,594,205,696 B | 2,594,205,696 B |
| Final ledger bytes | 6,449,053,696 B | 6,285,037,568 B |
| Staging peak | 1,414,139,904 B | 1,375,682,560 B |
| Total WAL peak | 438,998,456 B | 389,760,336 B |

Full scenario time was 4,356.442 s. Resource comparisons preserve distinct categories and do not add independently sampled peaks. All final seven-index schemas, opaque row counts and primary/unique constraints are preserved; lower DB size is page packing, not lost facts. The candidate addresses the measured ledger differential while leaving shared-workset integrity, workspace operations and rules unchanged.

| Scaling pair | Runtime per 100k: 100k / 200k | Normalized runtime penalty | Verification per 100k: 100k / 200k | Normalized Verification penalty |
| --- | ---: | ---: | ---: | ---: |
| Retained before candidate C | 1,939.660 / 2,234.997 s | +15.23% | 1,620.262 / 1,915.045 s | +18.19% |
| Deferred ledger indexes | 1,821.961 / 2,039.439 s | +11.94% | 1,479.123 / 1,696.386 s | +14.69% |

**Preferred at-most-8% progression: FAIL.** Processing 4,078.879 s exceeds 3,935.436 s; Verification 3,392.772 s also exceeds the comparable 3,194.905 s ceiling. The 5% target is not met. Both penalties improved, but not to the requested progression envelope. No Medium planning estimates are produced because the candidate 200k gate did not pass; the original Medium approximately 6,617 s processing / 2x Verification target is unchanged.

### Why It Helped and What Remains

100k/200k full measured ledger costs decrease from 487.983/1,277.315 s to 291.317/715.245 s, including staging and destination index construction. Candidate ledger normalized growth still penalizes 200k by 22.76%, so primary-key maintenance, reads/integrity and bounded durable I/O are not solved by deferring secondary indexes. Candidate processing excess over 2x drops from 590.674 to 434.956 s (-155.717 s); Verification excess drops from 589.567 to 434.527 s. These are whole measured results, not a hypothetical sum of nested savings.

Ordered expected reads remain 59.034 s at candidate 100k and 169.204 s at candidate 200k; workspace journal commit time remains 270.366 and 612.196 s. Those unchanged architectural operations still grow faster than rows and are concrete residual opportunities, not reasons to add parallel workers or replace SQLite by assumption. No covering-index/scan-fusion/relationship rewrite is mixed into this candidate.

The candidate 200k executes 180 complete scans, reading/decoding 5,902,736 typed projected records and 23,846,476 top-level field values. That is approximately twice the 100k rows/values with unchanged scan count. Beneficiary membership executes 23,352 indexed scalar lookups in 0.694 s, approximately twice the 100k count; the N+1 path is explicit, not falsely described as a joined stream. Aggregate group counts and pure sorting temporary bytes/times remain unavailable. New telemetry supports a later independently selected scan/index/relationship candidate.

### Candidate Retention and Decision

Retain candidate C's measured secondary-index timing improvement and compact scan/lookup telemetry: it improves both 100k and 200k end-to-end timing, reduces ledger/WAL storage, preserves final schema and assurance semantics, and does not meaningfully increase workload RSS. Retention is a cumulative optimization foundation, not evidence that the scaling gate or Medium milestone passed.

**Medium is not warranted now and was not run. Large was not run.** Stop this single-candidate experiment. Further focused residual-cost work requires a new explicit assignment; do not silently implement shared scans, covering indexes, relationship batching, broad concurrency, partitions, another backend, PS-0.10E or production execution/rollback. PS-0.10D remains In Progress and unaccepted.

### Final Scaling Investigation Validation

- Required final focused suites: Engine 3/3, Verification 27/27, Evidence 5/5, Recovery 8/8; 43 passed, 0 failed, 0 skipped. Execution-plan, projected workspace, immutable workset, and ledger lifecycle/auxiliary integrity coverage are included.
- New focused tests assert both staging/destination restore all seven secondary indexes; partial scan disposal reports exact rows/typed decodes; and two scalar membership lookups are aggregated with exact match count. Independent rule context resets in finally, without shared evaluators or registration-order changes.
- Physical candidate Fast, 50k, 100k and warranted 200k each passed 2/2 with no failures/skips, exact assurance semantics, two immutable builds/four reuses, and replay Evidence/ledger equality. All smaller required gates and whitespace checks passed before new candidate 200k. No probes ran concurrently.
- Saved original 100k/200k traces provided the scaling differential without rerunning either for profiling. Runtime/index/storage costs and per-rule operations are reported; no unmeasured aggregate groups, pure sort time, temporary sort bytes or CPU-bound conclusion is invented.
- Diagnostics report no errors and `git diff --check` is clean. Changes remain local and uncommitted; no publication occurred. Supplied briefs and previously retained architecture remain intact.
- Full solution, Oracle/Db2 and remote CI were not rerun for this performance experiment and are not claimed as acceptance. Medium and Large were not run; no Medium forecast is issued after the failed progression gate.

This single candidate/investigation is complete. Retain the measured index-timing improvement and telemetry, but normalized 200k penalties of 11.94% processing / 14.69% Verification do not pass the preferred at-most-8% criterion. Medium is not warranted; PS-0.10D remains In Progress and unaccepted. Stop here pending a new explicit assignment.

## Sequential Partition-Local Scratch Continuation

The new explicit assignment retains all cumulative architecture and tests whether growing actual/journal SQLite hot sets explain increasing per-operation cost. It does not assume that hypothesis is true. ADR-0023 records a sequential storage-locality experiment, no parallel workers, Medium/Large, PS-0.10E or replacement backend.

The plan owns graph-scoped artifact-identity scratch assignment using versioned SHA-256 of length-prefixed typed canonical keys. Operational counts 1/4/8 do not enter semantic fingerprints or expected-cache keys. Targets are routed directly while observed; target-bearing journal entries use the same node/artifact assignment, otherwise first source scope owns the complete fact. Multi-source ancestry is not casually duplicated.

Each partition has a separate WAL/NORMAL writer and bounded 128-write transactions. Writers execute sequentially; all flush before global reads. The coordinator's global TEMP views use unique operational sequence mapping so values, typed keys and journal associations cannot collide. One read-only expected workset attachment remains shared, without row copies per partition. Ledger staging/publication is still global and canonical, preserving ADR-0021 state/integrity and one receipt per run.

**Rule coverage at the storage-only stage:** no local evaluators were enabled; all configured rules remained global. The later Partition-Local Rule Evaluation continuation below supersedes this stage, documenting the narrow identity-key rules now locally evaluated and the rule families that remain global.

Tests pass stable assignment across a child .NET process, repeated typed identity/group/timeline/relationship-owner vectors, forced cross-partition group/timeline records, exact small decimal sum, global duplicates, multi-source ancestry, cross-partition membership, cancellation and broken-partition cleanup. Identical small physical input at 1/4/8 matches findings, ledger/Evidence fingerprints, Recovery assessment/plan/rehearsal and qualification/reasons. Initial failures found/fixed exact G29 exponent parsing and a missing attribute-read visibility barrier; all equality assertions remain.

Telemetry distinguishes max/sum partition DBs, coordinator, expected DB, total/partition WAL, per-partition rows/indexes/largest index where observable, native DML/commit time/count and ordering-index build phases. Global cross-database query time and unknown OS I/O attribution are not allocated to partitions by guess. Smaller individual DB size alone is not a successful result.

Fast and larger partition comparisons are pending. Count 1 reference processing should approximate retained 1,821.961 s at 100k; ordinary advancement requires no material regression and exact semantics. Only a credible best sequential candidate proceeds to 200k against retained 4,078.879 s; preferred normalized penalties are at most 8%, targeting 5%. No parallelism until sequential scaling benefit is demonstrated. A failed storage-locality prototype with global-query overhead cannot alone prove SQLite or every hot-set mechanism is the cause.

### Partition Fast Diagnostics and Compatibility Repairs

Initial sequential Fast counts 1/4/8 each passed 2/2 with zero failures/skips and exact assurance/reuse assertions. Artifact roots: `%TEMP%\PS-D-part-fast-p1-85af71c2`, `PS-D-part-fast-p4-74909be0`, `PS-D-part-fast-p8-01c1465a` (all `integrated-assurance`). Reference runtime was 129.016 s; initial four/eight were 340.393 / 152.627 s. Four-partition actual ordered reads took 179.564 s versus reference 3.498 s because SQLite materialized the union of key tables and chose two unindexed LEFT JOIN scans. Eight happened to choose automatic covering seeks. This confound was repaired, not interpreted as evidence against locality.

Actual/key joins now occur within each partition using the original local primary indexes, then one deterministic global sort merges all rows. No local evaluator, scan fusion or parallelism was added. Repaired Fast four/eight each passed 2/2, roots `%TEMP%\PS-D-part-localfast-p4-5fd1c680` and `PS-D-part-localfast-p8-04a9c851`.

| Repaired Fast metric | Reference 1 | 4 partitions | 8 partitions |
| --- | ---: | ---: | ---: |
| ProofShift runtime | 129.016 s | 175.519 s | 170.291 s |
| Verification | 83.344 s | 126.629 s | 122.776 s |
| Peak RSS | 214,298,624 B | 242,573,312 B | 268,300,288 B |
| Coordinator/workspace DB sampler peak | 60,948,480 B | 28,917,760 B | 26,722,304 B |
| Largest partition DB sampler peak | reference DB above | 16,044,032 B | 7,528,448 B |
| Sum of partition DB sampler peak | none | 56,336,384 B | 48,840,704 B |
| Expected-cache peak (one shared population pair) | 106,262,528 B | 106,262,528 B | 106,262,528 B |
| Total WAL peak | 29,182,056 B | 30,661,232 B | 51,842,280 B |
| Instrumented native DML commands | 611,870 | 611,870 | 611,870 |
| Native DML command time | 5.141 s | 4.044 s | 4.070 s |

Partition row counts and journal associations remain exact. Native write cost decreases despite equal command count and much smaller individual DBs, but total cost increases. Independently sampled coordinator/max/sum peaks must not be added as a simultaneous total. dbstat index-size measurements are unavailable in this runtime (`indexSizeAvailable=0`); no largest-index size is invented. Per-partition ordering-index build phases and native DML/commit counts remain recorded.

Phase inspection then exposed a second view confound: projected lineage at eight took 29.680 s versus 1.265 s reference, and target lineage coverage 15.236 s versus 0.043 s, because remapped global sequence expressions defeated local ancestry indexes. The same local journal/source join is now evaluated per partition before global canonical union/order; coverage queries check all partitions with native ancestry joins. No ancestry rows are copied, discarded or inferred from target state. Forced cross-partition/multi-source tests and the identical-input physical 1/4/8 fingerprint/Recovery comparison pass after this repair. The Fast table above predates this second repair and is diagnostic, not the final candidate performance result.

**Next diagnostic: paired 50k counts 1 and 8.** Eight has the smallest write hot set and slightly better repaired Fast processing among partitioned candidates. Use a paired reference rather than equating the historical Fast timing variation to an optimization win. This intermediate run tests scale amortization with the final query-compatible implementation. Full 100k 1/4/8 comparisons are practical only if smaller diagnostics do not show a clear material regression; do not force multi-hour runs or parallel workers to produce a favorable number. No new 200k/Medium/Large has started.

### Paired 50k Sequential Locality Diagnostic

Reference one and eight partitions each passed 2/2 with zero failures/skips and exact assurance/reuse assertions using the final local-index query shape. Roots: `%TEMP%\PS-D-part-50k-p1-a75ac179\integrated-assurance` and `%TEMP%\PS-D-part-50k-p8-7b8a6dbc\integrated-assurance`. They ran sequentially, with no workers.

| Paired 50k metric | 1 partition | 8 partitions |
| --- | ---: | ---: |
| ProofShift runtime | 926.781 s | 715.798 s (-22.77%) |
| Verification | 741.662 s | 536.467 s (-27.67%) |
| Peak RSS | 310,833,152 B | 355,561,472 B (+14.39%) |
| Working-set/coordinator sampler peak, includes build files | 372,322,304 B | 323,817,472 B |
| Largest local DB high-water | 372,322,304 B | 47,837,184 B |
| Sum partition DB sampler peak | reference DB above | 370,917,376 B |
| Same-sample working-set plus partitions peak | 372,322,304 B | 370,921,472 B |
| Same-sample above plus completed expected cache peak | 1,019,895,808 B | 1,018,494,976 B |
| Shared expected-cache sampler peak | 647,573,504 B | 647,573,504 B |
| Total WAL sampler peak | 102,851,776 B | 122,768,112 B |
| Partition WAL sampler peak | included in reference total | 61,108,096 B |
| Native instrumented DML executions | 3,727,538 | 3,727,538 |
| Native DML command duration | 59.797 s | 31.904 s (-46.65%) |
| Native DML per command | 16.04 microseconds | 8.56 microseconds |
| Committed transactions | 4,903 | 4,930 |
| Final canonical ledger bytes | 1,571,835,904 B | 1,571,835,904 B |

Actual rows across run stores remain 313,546 and journal rows 313,692, identical across counts. Same-sample totals avoid double-counting the expected builder's working-set file and final cache or summing unrelated peaks. Largest-index bytes are not observable in this SQLite build; availability is explicitly false. This is evidence of lower local native write cost with smaller DBs, not proof of lower total I/O bytes or every SQLite hot-set mechanism. Existing global expected/ledger stores remain unchanged.

| Paired phase | 1 partition | 8 partitions |
| --- | ---: | ---: |
| External actual observation/ingest | 86.836 s | 52.632 s |
| Projected actual observation/ingest | 84.862 s | 53.213 s |
| Projection journal validation | 172.682 s | 78.995 s |
| Global journal integrity query, nested | 4.608 s | 8.422 s |
| Ordered actual reads, nested | 25.558 s | 19.994 s |
| External rule evaluation | 39.120 s | 29.477 s |
| Projected rule evaluation | 45.358 s | 36.559 s |
| Projected lineage | 10.263 s | 9.785 s |
| Projected target lineage coverage | 0.581 s | 1.263 s |
| Workspace ordering-index construction | 10.897 s | 10.124 s |

Global integrity/coverage costs are higher in some paths and remain included in whole-run timing. Partition commit telemetry includes actual and journal commits, while the legacy reference journal-commit metric excludes actual commits; those durations are not falsely compared as identical scope. No parallelism, new evaluator partition safety, expected copies, or ledger partition merges were introduced.

The paired one-partition runtime is 9.57% above the historical retained 50k 845.818 s, so use the contemporaneous pair to assess locality and also disclose historical drift. Eight is 15.37% below that historical result. One pair does not establish repeatability, 100k non-regression or 200k scaling, and no SLA is claimed.

**Decision: run sequential 100k counts 1/4/8.** Paired intermediate gains are credible after eliminating measured view/index confounds. Preserve the final candidate unchanged and compare historical retained 1,821.961 s plus contemporaneous reference. Choose a best credible count before any new 200k; no workers until sequential 200k scaling benefit is demonstrated. Medium/Large/PS-0.10E remain blocked.

### Sequential 100k Candidate Selection

All three counts passed 2/2 physical tests with zero failures/skips, exact 149 defective / 0 corrected discrepancies, zero unaccounted sources/unexplained targets, QUALIFIED corrected output, and the existing Evidence/Recovery/reuse assertions. Runs were sequential without workers. Roots under `%TEMP%`: `PS-D-part-100k-p1-22ecc100`, `PS-D-part-100k-p4-35f674bd`, and `PS-D-part-100k-p8-0f4a8c0b`, each with `integrated-assurance` output.

| 100k metric | 1 partition | 4 partitions | 8 partitions |
| --- | ---: | ---: | ---: |
| ProofShift processing | 1,860.195 s | 1,709.013 s | 1,493.104 s |
| Verification | 1,510.567 s | 1,331.452 s | 1,167.904 s |
| Peak RSS | 410,697,728 B | 417,669,120 B | 432,893,952 B |
| Coordinator sampler peak, includes build files | 747,196,416 B | 569,974,784 B | 569,974,784 B |
| Largest partition DB sampler peak | reference DB above | 187,265,024 B | 94,658,560 B |
| Sum partition DB sampler peak | reference DB above | 743,747,584 B | 742,309,888 B |
| Same-sample coordinator plus partitions peak | 747,196,416 B | 743,751,680 B | 742,313,984 B |
| Same-sample above plus completed expected cache | 2,043,514,880 B | 2,040,070,144 B | 2,038,632,448 B |
| Shared expected-cache peak | 1,296,318,464 B | 1,296,318,464 B | 1,296,318,464 B |
| Total WAL sampler peak | 196,528,216 B | 196,647,824 B | 209,012,072 B |
| Partition WAL sampler peak | included in reference total | 78,564,408 B | 91,147,016 B |
| Native DML executions | 7,456,196 | 7,456,196 | 7,456,196 |
| Native DML duration | 130.597 s | 98.936 s | 76.523 s |
| Committed transactions | 9,804 | 9,819 | 9,834 |
| Final canonical ledger size | 3,147,104,256 B | 3,147,104,256 B | 3,147,104,256 B |

Actual rows 627,238 and journal rows 627,384 are identical across counts. Largest-index size remains unavailable, not measured zero. Eight reduces processing 19.73%, Verification 22.68%, and native DML duration 41.41% versus the contemporaneous reference; RSS increases 5.40% and total WAL increases 6.35%. Same-sample total DB scratch is nearly unchanged. Against historical retained 100k processing 1,821.961 s, eight improves 18.05%; the current reference is 2.10% slower than historical. These are single sequential measurements, not repeatability or causal proof.

| 100k phase | 1 partition | 4 partitions | 8 partitions |
| --- | ---: | ---: | ---: |
| External actual observation/ingest | 185.739 s | 148.414 s | 122.776 s |
| Projected actual observation/ingest | 185.039 s | 147.095 s | 125.369 s |
| Projection journal validation | 379.950 s | 263.490 s | 199.486 s |
| Global journal integrity query, nested | 7.588 s | 18.057 s | 19.665 s |
| Ordered actual reads, nested | 53.185 s | 45.478 s | 42.659 s |
| External rule evaluation | 64.711 s | 73.673 s | 57.321 s |
| Projected rule evaluation | 66.548 s | 59.725 s | 59.089 s |
| Projected lineage | 21.382 s | 20.980 s | 22.464 s |
| Projected target lineage coverage | 1.654 s | 2.430 s | 2.590 s |
| Workspace ordering-index construction | 19.362 s | 18.255 s | 17.829 s |

Reference journal-only commit telemetry is 271.963 s; partition all-actual-plus-journal commits are 304.725 / 214.147 s for four/eight. Different scopes prevent a like-for-like commit claim. Global journal integrity/coverage and some lineage costs regress and remain included. All rules, expected worksets and canonical ledger staging/finalization remain global.

### Sequential 200k Result and Decision

The selected eight-partition 200k run passed 2/2 physical tests with zero failures/skips. It generated exactly 200,000 source records, 206,696 checkpoint artifacts and 209,128 corrected projection targets. Exact assurance remained 149 defective / 0 corrected discrepancies, zero unaccounted sources/unexplained targets and QUALIFIED output; Evidence, Recovery, replay and expected-workset assertions passed. Root: `%TEMP%\\PS-D-part-probe-200k-p8-c0c5b22c\\integrated-assurance`.

| Eight-partition 200k metric | Result |
| --- | ---: |
| ProofShift processing | 3,168.623 s |
| Scenario total | 3,428.687 s |
| Verification (external + projected) | 2,575.492 s |
| External target verification | 1,204.730 s |
| Projected verification | 1,370.762 s |
| Peak RSS | 623,656,960 B |
| Final canonical ledger | 6,285,037,568 B |
| Shared expected cache peak | 2,594,205,696 B |
| Largest local partition DB sampler peak | 187,244,544 B |
| Sum partition DB sampler peak | 1,487,646,720 B |
| Total WAL sampler peak | 400,101,792 B |
| Partition WAL sampler peak | 165,410,016 B |
| Native DML executions | 14,913,512 |
| Native DML duration | 172.910 s |

Relative to eight partitions at 100k, processing grows 2.121x (+6.11% normalized penalty) and Verification grows 2.205x (+10.26%). Processing is 22.32% below the retained 200k result of 4,078.879 s; Verification is 24.09% below its retained 3,392.772 s. The 8% processing ceiling is 3,225.104 s, passed by 56.481 s; the 5% target is 3,135.518 s, missed by 33.105 s. Verification's normalized penalty improves from the retained 14.69% to 10.26%, a material but incomplete improvement. The test runner's 57m 11s duration is not the ProofShift processing metric: scenario total is 3,428.687 s and the measured ProofShift processing field is 3,168.623 s.

Partition-local high-water and aggregate sampler values are not additive with peaks from different timestamps. Some workload samples do not expose partition-specific fields; use the explicit per-partition stage records for those intervals and do not interpret absent telemetry as zero. All configured rule evaluation, expected-workset processing and durable ledger staging/finalization remain global. The measurements support smaller actual/journal write hot sets, not a fully partition-local evaluator or a causal claim about all SQLite costs.

**Decision: B, partition locality helps, but the scaling result is insufficient to advance to Medium.** Eight partitions improves the 100k/200k processing and Verification measurements, and passes the 8% 200k processing gate narrowly; it misses the 5% target. More importantly, applying the observed 2.121x 100k-to-200k growth to one further doubling projects about 6,720 s of processing, slightly above the approximately 6,617 s Medium target. This extrapolation is a caution, not an SLA forecast, but it is not a credible acceptance path. Do not run Medium or Large, start PS-0.10E, or add workers in this slice. No alternate backend is adopted. PS-0.10D remains In Progress and unaccepted; a new explicit assignment is required for further throughput work.

## Partition-Local Rule Evaluation Continuation

This continuation supersedes the preceding B-slice stop only for the explicitly assigned sequential local-rule work; Medium, Large, workers and PS-0.10E remain prohibited by the current assignment. The operational partition hash is now `proofshift-verification-partition-sha256-v2` and uses node-scoped logical target identity rather than ArtifactId, so duplicate physical records with distinct IDs cannot be split across local uniqueness scans.

Provider descriptors and the execution plan resolve every rule to `PartitionLocal`, `PartitionPartialWithGlobalMerge` or `Global`; default is Global. Partial mode requires a provider merger. The plan declares a typed key basis, workset role and field options; unsupported owner-key routing fails closed. The expected cache remains one canonical immutable workset: schema v2 adds a fixed-eight-bucket operational index on node-scoped target identity, maps run partition p to its bucket subset, and records index build time/rows. No N expected builds or expected row copies are introduced; bucket/hash values do not enter semantic requirement/Evidence/Recovery fingerprints.

| Rule family | Current mode | Partition key / merge |
| --- | --- | --- |
| Generic target presence | PartitionPartialWithGlobalMerge | Target artifact identity; sum partition missing/observed counts into the existing canonical population finding |
| Generic attribute comparison | PartitionPartialWithGlobalMerge | Target artifact identity; merge comparison counts and canonical population finding |
| Pension member attribute comparison | PartitionPartialWithGlobalMerge | Same generic attribute implementation and identity key |
| Pension member presence | PartitionLocal when one explicit target node's `businessKey` exactly matches its selector identity | `member_id`, graph-proven |
| Pension member uniqueness | PartitionLocal when one explicit target node's configured/default `businessKey` exactly matches its selector identity | `member_id`, graph-proven |
| Pension member status | PartitionLocal when one explicit target node's `businessKey` exactly matches its selector identity | `member_id`, graph-proven |
| Generic unexpected target | PartitionLocal | Target artifact identity; finding stable keys unioned/sorted |
| Generic entity uniqueness | PartitionLocal | Target artifact identity; duplicates cannot cross partitions |
| Source disposition, target lineage/accounting | Global | Exact-one disposition and graph-wide lineage/final coverage checks retained |
| Pension timelines and relationships | Global | Timeline owner/related populations may cross identity shards |
| Financial COUNT/SUM/MIN/MAX rules | Global | Group-key shard/partial aggregate merge is not implemented |

Rule evaluators run partitions sequentially. Local actual/journal queries use the selected SQLite database; expected rows come from the indexed bucket view. Partition partials are merged by semantic result keys/counts, never by partition ID; conflicting duplicate stable keys fail. Source accounting, target lineage/coverage and the one durable ledger finalization remain the final global safety/publication path. The deterministic value-free manifest stores partition/hash version, rule mode/key declarations, and per-partition actual/expected/journal counts; per-rule/per-partition stages report wall time, findings, scans, typed decodes and lookups. Skew is reported diagnostically without failing Verification.

Tests include distinct ArtifactIds whose hashing would split a duplicate identity, record identities sharing a timeline owner across partitions, global merge/query-count equivalence, exact decimal preservation, value-free deterministic manifest fingerprinting, required-merger rejection, and unsupported grouping-key rejection. Physical 1/4/8 equivalence passes for findings, ledger/Evidence, Recovery and qualification. Focused gates pass Engine 3/3, Verification 38/38, Evidence 5/5 and Recovery 8/8; current-source Fast p8 passes 2/2 with exact 149/0 discrepancy semantics. The sequential local-rule 100k and authorized 200k p8 probes are now complete; their results and the resulting gate decision follow.

### Sequential Local-Rule 100k and 200k Results

Both probes ran sequentially at eight partitions, without workers. Each passed 2/2 physical end-to-end tests with zero failures/skips, exact 149 defective / 0 corrected discrepancies, zero unaccounted sources/unexplained targets, and corrected `QUALIFIED` output. The execution plan reported 3 `PartitionLocal`, 0 `PartitionPartialWithGlobalMerge`, and 12 `Global` rules. The 200k run preserved the same semantic rules, Evidence, Recovery, replay, and expected-workset assertions.

| Local-rule p8 metric | 100k | 200k |
| --- | ---: | ---: |
| ProofShift processing | 1,663.892 s | 3,864.780 s |
| Scenario total | 1,801.345 s | 4,144.128 s |
| Exact-name `verification` inclusive subtotal (not the full category) | not summarized in this table | 1,726.090 s |
| Checkpoint artifacts | 103,348 | 206,696 |
| Corrected projection targets | 104,564 | 209,128 |
| Peak workload RSS | 435,056,640 B | 642,310,144 B |
| Final canonical ledger | not captured in this table | 6,285,037,568 B |

The 200k scenario completed in 1h 09m 07s in the test runner. Its summary reports 4,144.128 s scenario time and 3,864.780 s ProofShift runtime. The 1,726.090 s value is the inclusive sum of the three exact-name `verification` stages; it is not the complete Verification category and overlaps nested work. Summing non-overlapping exclusive intervals for all `kind=4` Verification stages gives 3,144.050 s. The retained 200k artifact gives 2,575.491 s by the same calculation. The separately reported external-target and projection/verification/recovery stopwatch fields are not additive to stage totals. Peak sampled partition scratch was 1,487,347,712 B, with a 187,523,072 B largest-partition sample; temporary-workspace peak was 1,656,454,232 B. These independently sampled peaks must not be added together. The final durable ledger was 6,285,037,568 B. Artifacts: `%TEMP%/PS-D-rulocal-200k-p8-63759b37/integrated-assurance`.

Against the retained eight-partition 200k reference of 3,168.623 s processing / 2,575.491 s exclusive Verification, local-rule processing is 21.97% slower and the complete exclusive Verification category is 3,144.050 s, 22.08% slower. The processing and Verification non-regression gates fail. From the earlier standalone local-rule 100k result, doubling to 200k takes 2.323x processing, a 16.14% normalized penalty over linear scaling. This is not a credible end-to-end scaling result and does not authorize a worker experiment.

### Matched Same-Code 100k A/B Reconciliation

To resolve the saved timing-boundary ambiguity, the same code and eight-partition harness were run sequentially at 100k in `GlobalRuleReference` and `PartitionLocalExperimental`. The reference artifacts are `%TEMP%/PS-D-recon-100k-reference-p8-e3e84913/integrated-assurance`; the experimental artifacts are `%TEMP%/PS-D-recon-100k-local-p8-3c7548bc/integrated-assurance`. Each physical run passed 2/2 tests with zero failures/skips and retained 103,348 checkpoint artifacts, 104,564 targets, 149 defective / 0 corrected discrepancies, zero unaccounted/unexplained artifacts, Recovery checks, false-Reverse assertions, and corrected `QUALIFIED` output.

| Matched 100k metric | Global reference | Partition-local experimental | Delta |
| --- | ---: | ---: | ---: |
| ProofShift processing | 1,620.254 s | 1,668.528 s | +48.274 s (+2.98%) |
| Scenario total | 1,758.366 s | 1,806.242 s | +47.876 s |
| Complete Verification category, exclusive `kind=4` intervals | 1,276.505 s | 1,309.974 s | +33.469 s (+2.62%) |
| Non-Verification processing, by subtraction | 343.749 s | 358.554 s | +14.805 s |
| Peak workload RSS | 458,412,032 B | 432,762,880 B | -5.59% |

The processing recorder root is `ProofShift processing`. Its child-interval union was 1,620,255,230.6 us against a 1,620,254,187 us root in the reference, and 1,668,529,143 us against a 1,668,527,808 us root in the candidate. Both reported zero unattributed processing; the approximately 1.0/1.3 ms differences are timer-boundary rounding. All recorded processing categories are rooted, so no Verification work was moved outside the processing timer.

| Root-descendant exclusive category | Reference | Experimental | Delta |
| --- | ---: | ---: | ---: |
| Verification | 673.356 s | 688.956 s | +15.600 s |
| External target verification | 603.149 s | 621.017 s | +17.869 s |
| Projection | 165.172 s | 168.874 s | +3.703 s |
| Recovery analysis and rehearsal | 102.956 s | 115.150 s | +12.194 s |
| Checkpoint | 36.239 s | 36.351 s | +0.112 s |
| Docker database startup | 12.516 s | 12.728 s | +0.212 s |
| Unscoped processing intervals | 25.183 s | 23.778 s | -1.405 s |
| Remaining named persistence/report categories, net | 1.684 s | 1.675 s | -0.010 s |

The root-descendant exclusive category delta totals 48.274 s within 0.3 ms rounding of the processing-root delta, satisfying the 95% attribution requirement for this paired probe. The unchanged-mode external target verification and Recovery categories account for substantial run-to-run movement; one sequential pair does not establish that every millisecond of the whole-run regression is caused by local rule execution. The separate 200k result and the observed work-count change support rejecting this candidate as a throughput advancement.

The per-rule change is fully accounted for. Each performance artifact contains six verification plans, each with 3 locally eligible rules and 12 remaining global rules in the experimental run. Across those six runs, member presence changed from 12 whole-set scans to 96 partition scans; member status likewise changed 12 to 96; member uniqueness changed 6 to 48. The three rules therefore add 210 scans total, while the other 150 scan executions remain unchanged. The 144 `partition-local rule evaluation` stage records are exactly 3 rules x 8 partitions x 6 runs.

| Work counter | Global reference | Experimental | Delta |
| --- | ---: | ---: | ---: |
| Completed rule scans | 180 | 390 | +210 |
| Query executions | 195 | 405 | +210 |
| Expected-target ordered-query rows | 1,140,144 | 1,140,144 | unchanged |
| Actual-target ordered-query rows | 1,169,132 | 1,169,132 | unchanged |
| All recorded rows returned | 5,902,531 | 5,902,531 | unchanged |
| Top-level field-value decodes | 11,922,808 | 11,922,808 | unchanged |
| Typed-record decodes | 2,951,264 | 2,951,264 | unchanged |
| Actual rows | 627,238 | 627,238 | unchanged |

The extra queries repartition the same decoded rows. Ordered expected-query time rose from 58.575 s to 70.013 s; actual-query time rose from 46.397 s to 46.742 s. Local rule evaluation itself recorded 5.032 s exclusive across its 144 partition stages. No additional decoded information or changed findings offset the query fan-out.

Instrumentation output also grew: stage records 1,368 to 1,914 (+546), measurement records 11,630 to 15,284 (+3,654), and `performance-run.json` 1,751,628 to 2,302,863 bytes. There was no instrumentation-off paired control, so these volume changes do not quantify telemetry CPU/wall-time overhead; the processing-root attribution only proves that recorded intervals cover processing, not that recording is free.

Source, graph, projection, and rule-set fingerprints match between the paired runs. Evidence, Recovery, and DryRun fingerprints differ across run IDs; their cross-run equivalence remains a separate run-binding question and is not claimed as proven by this comparison. The earlier standalone 100k local-rule timing (1,663.892 s) remains historical; the paired 1,668.528 s value is the comparison used for this gate.

**Historical decision for the sequential local-rule continuation:** hold PS-0.10D In Progress. Its matched 100k non-regression gate failed; do not promote that evaluator. The later, explicitly assigned bounded-write-concurrency continuation below supersedes only the earlier prohibition on workers. Medium/Large, PS-0.10E, production migration execution and rollback remain out of scope.

**Matched 100k decision: FAIL non-regression.** Keep `GlobalRuleReference` as the service and benchmark default. The partition-local path remains benchmark-only and experimental; do not promote it or infer a throughput win from lower peak RSS. Its extra scans are an inherent consequence of evaluating the three current rules once per partition rather than once globally, so no corrective code optimization is justified by these artifacts. The unchanged-mode phase variation prevents claiming the full +2.98% as local-rule-only cost. This result did not authorize concurrency; the later bounded-write experiment below was separately assigned.

## Bounded Partition Scratch-Write Concurrency Continuation

The explicit PS-0.10D follow-on rejects sequential local-rule evaluation as a throughput advancement and authorizes only bounded concurrency for independent partition scratch writes. Keep `GlobalRuleReference`, the shared immutable expected workset, eight deterministic actual/journal scratch databases, global rule evaluation, and one canonical ledger with global finalization. `maxPartitionWorkers` is operational, defaults to 1, and is excluded from semantic fingerprints. Each worker lane owns its partition SQLite writers; bounded per-lane queues drain at the barriers before global reads, rules, or ledger publication. No ledger or rule work is partitioned.

All three 100k p8 physical runs used the same code and `GlobalRuleReference`; each passed 2/2 tests, zero failures/skips, exact 149 defective / 0 corrected discrepancies, zero corrected accounting/lineage gaps, and corrected `QUALIFIED` output. Artifacts:

- workers=1: `%TEMP%/PS-D-workers-100k-p8-w1-4d116a66/integrated-assurance`
- workers=2: `%TEMP%/PS-D-workers-100k-p8-w2-0cf5fbef/integrated-assurance`
- workers=4: `%TEMP%/PS-D-workers-100k-p8-w4-6e3daf96/integrated-assurance`

| 100k p8 metric | Workers=1 | Workers=2 | Workers=4 |
| --- | ---: | ---: | ---: |
| ProofShift processing | 1,729.686 s | 1,513.969 s | 1,336.970 s |
| Complete Verification, exclusive `kind=4` intervals | 1,361.684 s | 1,150.154 s | 997.078 s |
| Scenario total | 1,873.664 s | 1,659.157 s | 1,482.372 s |
| Peak workload RSS | 426,872,832 B | 437,940,224 B | 469,012,480 B |
| Temporary workspace peak | 835,691,008 B | 835,691,008 B | 835,691,008 B |
| Process-wide CPU-time delta | 1,447,219 ms | 1,498,766 ms | 1,427,578 ms |

Workers=2 improves processing 12.47% against workers=1 and 6.56% against the retained 1,620.254 s 100k reference, passing both the clear-improvement and no-regression gates. Peak RSS is 2.59% above workers=1, workspace peak is unchanged, and worker telemetry reports maximum concurrency 2, average active lanes 1.70 for actual-target writes / 1.78 for journal writes, and zero SQLite busy exceptions or retries.

Workers=4 improves processing a further 11.69% and complete Verification 13.31% against workers=2. It is 17.48% below the retained processing reference and 21.89% below its 1,276.505 s exclusive Verification reference. Peak RSS is 7.10% above workers=2 (469,012,480 B, a 31,072,256 B increase); workspace peak is unchanged. Process-wide CPU-time delta falls 4.75% from workers=2. Worker telemetry reaches four simultaneous lanes, averaging 3.01 active lanes for actual-target writes and 3.34 for journal writes; SQLite busy exceptions/retries remain zero. The measured queue-wait counter is a sum across operations, not wall-clock delay.

Across workers=1/2/4, source, graph, projection and rule-set fingerprints match. Evidence, Recovery and dry-run fingerprints differ between separate run IDs; their run-bound provenance is not a cross-run determinism failure. No sequential local-rule mode was used for these comparisons.

The gate-authorized 200k p8 workers=4 run passed 2/2 physical tests, zero failures/skips, and the same exact assurance assertions. Artifacts: `%TEMP%/PS-D-workers-200k-p8-w4-a759f3fb/integrated-assurance`.

| 200k p8 metric | Retained `GlobalRuleReference` | Workers=4 |
| --- | ---: | ---: |
| ProofShift processing | 3,168.623 s | 2,682.757 s |
| Complete Verification, exclusive `kind=4` intervals | 2,575.491 s | 2,087.057 s |
| Scenario total | 3,428.687 s | 2,976.413 s |
| Peak workload RSS | 623,656,960 B | 665,333,760 B |
| Final canonical ledger | 6,285,037,568 B | 6,285,037,568 B |

Processing improves 15.33% and complete Verification 18.96% against the retained 200k result. Relative to the selected workers=4 100k run, processing grows 2.007x, a +0.33% normalized penalty; Verification grows 2.093x, a +4.66% normalized penalty. The latter remains mildly superlinear and is not a production SLA. At 200k the peak RSS is 6.68% above retained p8; the temporary-workspace summary peak is 1,656,454,232 B. Workload samples show 1,317,511,168 B maximum verification-workspace bytes, 2,634,960,896 B expected-cache bytes, a 2,635,059,200 B maximum same-sample sum of those two counters, 187,666,432 B largest partition, 1,487,347,712 B summed partition databases, 6,285,037,568 B ledger, and 407,039,872 B total WAL. Independently sampled peaks must not be added. Worker telemetry reaches four simultaneous lanes, with mean active lanes 3.05 for actual-target writes and 3.57 for journal writes; SQLite busy exceptions and retries are zero.

Source, graph, projection and rule-set fingerprints match the retained same-scale run; Evidence, Recovery and dry-run fingerprints differ across run IDs as expected for run-bound provenance. Exact workload counts and assurance results are unchanged.

**Decision: bounded scratch-write concurrency passes the assigned 100k and 200k gates using eight partitions and four workers.** Retain `GlobalRuleReference` and the single global ledger path. PS-0.10D remains In Progress and unaccepted. This result does not authorize Medium, Large, PS-0.10E, another backend, production migration execution or rollback. No larger benchmark was started.

## Authoritative Medium Throughput Gate

The 2026-10-09 explicit assignment authorized exactly one physical Medium run with the retained architecture: `GlobalRuleReference`, eight deterministic partitions, four workers, shared immutable expected state, plan-driven reduced-field workspace projection, deferred secondary indexes, set-oriented ledger staging/finalization, one canonical ledger, and global Recovery/Evidence/qualification. No implementation or performance parameter changed before or during the run. `PartitionLocalExperimental` was not used.

Artifacts are in `%TEMP%/PS-D-M-p8-w4-20261009/integrated-assurance`; the persisted review sidecar is [PS0_10D_MEDIUM_BENCHMARK.json](PS0_10D_MEDIUM_BENCHMARK.json). The physical EndToEnd assembly passed 2/2 tests with 0 failures and 0 skips. The scenario generated 411,000 records using `proofshift-pension-generator-v1`, seed `20261003`, and retained 424,750 checkpoint artifacts and 429,750 corrected targets. Execution metadata records p8, workers=4 and `GlobalRuleReference`; configuration hash is `392a01e7392732b6b0f3c6c1fbdf7a76c7a07ad991735be99ae72275acf40d93`.

| Metric | Accepted Medium baseline | PS-0.10D Medium | Delta |
| --- | ---: | ---: | ---: |
| ProofShift processing | 13,234.382 s | 6,794.325 s | -6,440.057 s (-48.66%); 1.948x speedup |
| Formal 2x processing limit | 6,617.191 s | 6,794.325 s | +177.134 s (+2.68% over limit) |
| Strong-result threshold | ~6,300 s | 6,794.325 s | Missed by 494.325 s |
| Scenario total | 13,896.496 s | 7,408.982 s | -6,487.514 s (-46.68%) |
| Peak workload RSS | 1,091,010,560 B | 1,101,176,832 B | +10,166,272 B (+0.93%) |
| Aggregate temporary-workspace peak | 8,723,311,760 B (profiled baseline) | 3,380,884,512 B | -61.24% |
| Largest workspace DB | ~7.64 GB | workspace is partitioned; see scratch rows below | Not directly like-for-like |
| WAL | ~1.08 GB in the assignment | 810,115,952 B total sampled WAL | Lower directionally; historical scope caveat below |

**Formal decision: FAIL.** Processing is 177.134 s over the immutable 6,617.191 s target. The ~6,300 s strong-result line is also not met. The measured result, not the 100k/200k extrapolation, decides the gate.

The complete current Verification category is 5,354.381 s, summing the non-overlapping exclusive `kind=4` intervals. The historical Medium profile reports 7,285.392 s as an inclusive subtotal of three top-level Verification scopes. Since those definitions differ, no Verification speedup percentage is claimed. The primary decision remains ProofShift processing.

| Exclusive processing stage category | Seconds | % of processing root |
| --- | ---: | ---: |
| Expected-workset build/reuse | 1,031.590 | 15.183% |
| Actual-target observation/ingest | 800.844 | 11.787% |
| Journal processing | 474.703 | 6.987% |
| Projection | 634.240 | 9.335% |
| Global rule evaluation | 613.909 | 9.036% |
| Source accounting | 274.607 | 4.042% |
| Target lineage | 349.484 | 5.144% |
| Workspace/index work | 271.223 | 3.992% |
| Ledger staging exclusive intervals | 245.126 | 3.608% |
| Ledger derivation | 217.675 | 3.204% |
| Ledger finalization | 798.617 | 11.754% |
| Evidence construction/persistence | 0.061 | 0.001% |
| Recovery analysis and rehearsal | 384.495 | 5.659% |
| Reporting and CLI | 1.554 | 0.023% |
| External-target Verification orchestration remainder | 190.751 | 2.807% |
| Verification orchestration remainder | 248.161 | 3.652% |
| Checkpoint | 137.553 | 2.025% |
| Other named processing (startup and plan remainder) | 26.469 | 0.390% |
| Explicit unscoped intervals | 161.646 | 2.379% |

The processing root is 6,794.324964 s. Its recorded child-interval union is 6,794.325733 s, a 0.769 ms boundary difference; `unattributedProcessingMicroseconds` is zero. The 28 explicit unscoped gap intervals total 161.646 s, yielding 97.6209% named interval coverage and passing the >=95% attribution target. Per-category exclusive work seconds can overlap across concurrent worker lanes; the table percentages are per-category shares, not additive wall-time shares. The recorder's root interval union is the reconciliation measure.

The `verification ledger staging writes` instrumentation also records 717.271 s as a sum of write API scopes. That aggregate is useful to explain staging work but is not a non-overlapping wall interval and is not added to the stage table or processing root. The six compatible verification paths built two immutable expected worksets and reused them four times (six attachments); incompatible checkpoint/configuration/graph keys remained separate.

| Worker/resource observation | Medium result |
| --- | ---: |
| Configured partitions / workers / rule mode | 8 / 4 / `GlobalRuleReference` |
| Maximum simultaneous workers | 4 |
| Average active actual-target lanes | 2.697 |
| Average active journal lanes | 3.652 |
| Worker execution time, actual-target / journal | 1,726.475 s / 1,732.358 s (aggregate lane time) |
| Queue wait, actual-target / journal | 241,452.063 s / 272,932.169 s (aggregate operation time, not wall delay) |
| SQLite busy exceptions / retries | 0 / 0 |
| Process CPU-time delta | 6,850,437.5 ms (sampler interval) |
| Peak managed heap / at completion | 885,302,464 B / 494,208,192 B |
| GC collection deltas, Gen0 / Gen1 / Gen2 | 45,950 / 4,856 / 68 |
| Expected-cache high-water | 5,419,405,312 B |
| Verification-workspace high-water | 2,709,725,184 B |
| Largest partition DB / sum of partition DBs | 385,052,672 B / 3,061,403,648 B |
| Ledger staging DB high-water | 2,826,989,568 B |
| Final canonical ledger bytes | 12,924,080,128 B |
| Total WAL high-water | 810,115,952 B |
| Aggregate temporary-workspace peak | 3,380,884,512 B |

These are separately sampled categories and must not be added. No dedicated scratch-capacity estimator exists in the repository scripts. The preflight found 2,399.09 GiB free on `C:` and used an ad hoc 20 GiB planning allowance; observed peak volume use was 23.56 GiB, so that allowance understated the run by 3.56 GiB. The volume still had 2,375.53 GiB free at its lowest observed point, 2,383.24 GiB after completion, and no out-of-disk condition occurred. The accepted brief cites ~1.08 GB WAL, while the older detailed profile records a 1,725,085,264 B WAL high-water. As those historical scopes disagree, the current 810,115,952 B total SQLite WAL is reported without asserting a precise cross-run percentage reduction.

Correctness remained exact: 149 defective discrepancies, 0 corrected discrepancies, 0 corrected unaccounted sources, 0 corrected unexplained targets, 0 corrected Verification failures, 0 corrected Recovery failures, corrected Recovery assessment `PASSED`, corrected Recovery rehearsal `PASSED`, and corrected `QUALIFIED`. The physical tests passed the external-target observation path independently of Projection Journal state. The two false-Reverse declarations were both detected as invalid Reverse edges; the false-Reverse scenario remained `NOT QUALIFIED` as designed.

### Medium Fingerprint Contract

The configuration hash, graph hash, checkpoint manifest/source semantics, projection fingerprint, and rule-set fingerprint are execution-independent identities for equivalent inputs; partition/worker settings are not included. The benchmark records these hashes plus the exact business findings and canonical ledger counts. This single Medium run does not assert cross-run equality of its ledger fingerprint.

The Evidence graph fingerprint is a canonical content hash that omits Evidence record IDs, `RunId`, and `EvaluatedAt`, but hashes reference keys. Current Verification Evidence includes checkpoint and projection-run references, so the fingerprint is provenance-sensitive across separate run IDs. The Recovery assessment fingerprint hashes assessment/policy/outcome data and the Verification Evidence fingerprint; it does not directly hash a run ID, but inherits that context sensitivity. The Recovery Evidence fingerprint explicitly binds run, checkpoint and projection references and the Verification Evidence fingerprint, so it is run-bound. The DryRun fingerprint aggregates Verification Evidence, Recovery assessment/plan/rehearsal and Recovery Evidence, and is intentionally run-bound. No canonicalization was changed to force cross-run byte equality.

The locally observed current fingerprints and the source/runtime/worker metadata are in [PS0_10D_MEDIUM_BENCHMARK.json](PS0_10D_MEDIUM_BENCHMARK.json); raw stage, sample and summary artifacts remain in `%TEMP%/PS-D-M-p8-w4-20261009/integrated-assurance`.

**Historical Decision B for workers=4: Medium throughput gate FAILED.** The later, conditionally authorized workers=8 rerun below supersedes that result for the final Medium gate only; it does not authorize another benchmark or milestone expansion.

## Authorized Workers=8 Medium Rerun

After the workers=4 miss, the explicit continuation authorized worker-saturation measurements at 100k with workers=6/8, a 200k run at the fastest qualifying worker count, and one Medium rerun only after the stated projections passed. The same architecture and `GlobalRuleReference` remained frozen; only `maxPartitionWorkers` varied. The stage-informed 411k projection was 5,037.966 s optimistic, 5,779.759 s central and 6,199.884 s conservative. The conditional authorization thresholds were met. Exactly one Medium rerun ran with p8/workers=8 on 2026-10-09; no concurrent or repeat benchmark was started.

Artifacts are in `%TEMP%/PS-D-M-p8-w8-20261009/integrated-assurance`. The EndToEnd assembly passed 2/2 tests, 0 failures and 0 skips (1h 41m 32.857s). It used the same 411,000 generated records, 424,750 checkpoint artifacts and 429,750 projected targets as the workers=4 Medium run. Exact outcomes were 149 defective discrepancies, 0 corrected discrepancies, zero unaccounted sources, zero unexplained targets, corrected `QUALIFIED`, and both false-Reverse declarations rejected with their invalid scenario not qualified. The independent external-target observation path remained enabled.

| Medium metric | Accepted baseline | Initial workers=4 | Final workers=8 |
| --- | ---: | ---: | ---: |
| ProofShift processing | 13,234.382 s | 6,794.325 s | 5,574.083 s |
| Complete Verification, exclusive `kind=4` intervals | Historical profile is not directly comparable | 5,354.381 s | 4,423.960 s |
| Scenario total | 13,896.496 s | 7,408.982 s | 6,089.439 s |
| Peak workload RSS | 1,091,010,560 B | 1,101,176,832 B | 1,119,571,968 B |
| Aggregate temporary-workspace peak | 8,723,311,760 B (profiled baseline) | 3,380,884,512 B | 3,380,884,512 B |
| Final canonical ledger | Not comparable | 12,924,080,128 B | 12,924,080,128 B |

**Final Medium decision: PASS.** Processing is 1,043.108 s below the immutable 6,617.191 s formal limit and 725.917 s below the 6,300 s strong-result threshold. It is 17.96% faster than the workers=4 run and 57.88% below the accepted Medium processing baseline (2.374x speedup). The measured result beat the central projection by 205.676 s. Complete exclusive Verification is 4,423.960 s; it is reported using the same non-overlapping `kind=4` definition as workers=4, not compared with the historical inclusive profile.

The worker saturation curve confirms diminishing returns at 100k while workers=8 remains the fastest tested setting:

| 100k p8 workers | Processing | Complete exclusive Verification | Peak workload RSS |
| ---: | ---: | ---: | ---: |
| 1 | 1,729.686 s | 1,361.684 s | 426,872,832 B |
| 2 | 1,513.969 s | 1,150.154 s | 437,940,224 B |
| 4 | 1,336.970 s | 997.078 s | 469,012,480 B |
| 6 | 1,124.355 s | 828.203 s | 441,540,608 B |
| 8 | 1,086.520 s | 806.783 s | 497,209,344 B |

At 200k, workers=8 processed in 2,451.565 s with 1,910.802 s complete exclusive Verification, 2,718.179 s scenario total and 636,911,616 B peak RSS. Against workers=4 at that scale, processing improved 8.62% and Verification 8.45%. However, normalized 100k-to-200k growth was +12.82% for processing and +18.42% for Verification, materially worse than workers=4 (+0.33% / +4.66%). This scaling tradeoff is retained explicitly; workers=8 was selected because the observed Medium gate passed, not because its normalized scaling was better.

Worker telemetry reached all eight configured lanes, with average active lanes of about 5.58 for actual-target writes and 7.37 for journal writes. SQLite busy exceptions/retries were zero. Queue-wait remains aggregate per-operation wait, not wall-clock delay. Two immutable expected worksets were built and four compatible reuses served the six paths. The configuration, graph, source/checkpoint, projection and rule-set fingerprints match the workers=4 run; Evidence, Recovery and DryRun remain provenance/run-bound, so cross-run fingerprint equality is not claimed.

The processing root's 82.754 s explicit unscoped gaps give 98.515% named-interval coverage, above the 95% attribution requirement. Peak workload RSS increased 1.67% from workers=4 and 2.62% from the accepted baseline; temporary-workspace peak and final canonical-ledger bytes were unchanged from workers=4. Resource peaks are sampled independently and must not be added. Raw stage, summary and sample artifacts retain CPU, heap, GC and scratch detail.

**Decision A: the authorized workers=8 Medium throughput and correctness gates passed.** At that point PS-0.10D remained In Progress and Large was not yet authorized. The subsequent explicit continuation authorizes one 1.5M physical Large run after its measured scratch-capacity preflight; it does not authorize 2M+, another Medium, PS-0.10E, production migration/rollback, or automatic milestone acceptance.

## Authorized Large Full-Pipeline Run

The continuation authorized exactly one physical Large run at a minimum of 1,500,000 generated source records, with the workers=8 architecture frozen (`GlobalRuleReference`, 8 partitions, 8 workers, shared immutable expected worksets, plan-driven workspace projection, deferred secondary indexes, partition-local actual/journal scratch writes, global rules, set-oriented ledger finalization, one canonical ledger, independent actual-target observation). No tuning occurred before or during the run, workers were not changed, 2M+ was not started, and no second Large run is authorized.

**Scratch-capacity preflight.** `scripts/estimate-pension-scratch.ps1` replaced the earlier ad hoc 20 GiB allowance (which understated Medium's observed 23.56 GiB). It projects per-category scratch from the retained W8 100k/200k/Medium artifacts (observed growth exponent, never below linear), derives the volume requirement from the measured Medium whole-volume use scaled by records, adds a 25% margin, and writes a path-bound estimate that `scripts/pension-benchmark.ps1 -Scale large-acceptance` requires (it fails closed without a passing estimate or with a different output path). Independent high-water values are not summed. Result: estimated 107.84 GiB including margin versus 2,351.97 GiB free; output root `%TEMP%\PSD-L15` (short path, avoiding the earlier Windows SQLite Error 14 confound). The `large-acceptance` scale is 1,500,000 records using the Medium corpus proportions (largest-remainder allocation); the separate 8.22M `large` generator-enumeration scale is unchanged and is not a Verification claim.

**Result: the Large full-pipeline gate passed.** The EndToEnd assembly passed 2/2 tests, 0 failed, 0 skipped, in 8h 28m 08s (2026-10-09 16:28Z to 2026-10-10 00:56Z). Artifacts: `%TEMP%/PSD-L15/integrated-assurance`; structured record: [PS0_10D_LARGE_BENCHMARK.json](PS0_10D_LARGE_BENCHMARK.json).

| Large metric | Result |
| --- | ---: |
| Generated source records | 1,500,000 (exactly) |
| Checkpoint artifacts / projected targets | 1,550,182 / 1,568,430 |
| ProofShift processing | 28,143.783 s |
| Scenario total | 30,482.582 s |
| Complete Verification, exclusive `kind=4` intervals | 23,186.709 s |
| Processing records/s; processing s/100k | 53.30; 1,876.252 |
| Verification s/100k | 1,545.781 |
| Named interval coverage | 97.218% (782.840 s explicit unscoped) |

Source counts were Member 18,248; Employment 54,745; Contribution 912,409; ServiceCredit 63,869; Beneficiary 29,197; RetirementElection 6,387; BenefitPayment 364,963; Document 45,620; HistoricalExport 4,562 (50,182 CSV records, 45,620 binary payloads). Generator `proofshift-pension-generator-v1`, seed 20261003.

**Correctness (preferred, not the fallback).** The full 149-defect corpus was preserved: 149 defective / 0 corrected discrepancies, 0 corrected unaccounted sources, 0 corrected unexplained targets, defective `NOT QUALIFIED`, corrected `QUALIFIED`, false-Reverse scenario not qualified, and independent external target observation with all required Evidence/Recovery/report outputs persisted (248 Evidence records, 635,868 B Evidence, 97,900 B Recovery artifacts). The minimum-correctness fallback was not used. Two expected-workset builds and four compatible reuses occurred (no cross-key reuse).

| Exclusive stage category | Seconds | % of processing |
| --- | ---: | ---: |
| Expected-workset build/reuse | 4,020.737 | 14.29 |
| Journal processing (includes staged journal-scope derivation) | 3,529.806 | 12.54 |
| Target lineage | 2,933.673 | 10.42 |
| Actual-target observation/ingest | 2,638.286 | 9.37 |
| Global rule evaluation | 2,538.724 | 9.02 |
| Ledger finalization | 2,528.501 | 8.98 |
| Projection | 2,248.042 | 7.99 |
| Verification orchestration remainder | 1,836.267 | 6.52 |
| Recovery | 1,400.584 | 4.98 |
| Ledger staging | 1,267.342 | 4.50 |
| Workspace/index | 1,141.881 | 4.06 |
| Source accounting | 1,125.282 | 4.00 |
| Explicit unscoped | 782.840 | 2.78 |
| Checkpoint | 548.586 | 1.95 |
| Other named / Reporting / Evidence | 25.055 | 0.09 |

Classification is by stage-name pattern; lane-overlapping exclusive seconds are not additive wall time, and ledger derivation is folded into the journal/lineage rows rather than tabulated separately.

| Worker telemetry | Result |
| --- | ---: |
| Configured / maximum simultaneous workers | 8 / 8 |
| Mean active actual-target / journal lanes | 5.18 / 7.52 |
| Worker execution, actual-target / journal | 11,742.0 s / 13,075.8 s (aggregate lane time) |
| Worker idle, actual-target / journal | 6,386.5 s / 840.9 s |
| Queue wait, actual-target / journal | 1,345,908 s / 1,810,144 s (aggregate per-operation, not wall delay) |
| SQLite busy exceptions / retries / worker failures | 0 / 0 / 0 |

| Resource (separately sampled; not additive) | Result |
| --- | ---: |
| Peak workload RSS | 3,494,281,216 B |
| Managed heap peak / at completion | 3,160,449,576 B / 1,757,810,488 B |
| GC deltas Gen0 / Gen1 / Gen2 | 167,861 / 18,507 / 246 |
| Process CPU time | 29,438,828 ms |
| Expected-cache high-water | 19,787,104,256 B |
| Verification-workspace high-water | 9,893,675,008 B |
| Largest partition DB / sum of partition DBs | 1,406,144,512 B / 11,220,717,568 B |
| Ledger staging DB high-water | 10,322,001,920 B |
| Final canonical ledger | 47,213,477,888 B |
| Total WAL high-water | 5,873,138,952 B |
| Aggregate temporary-workspace peak | 12,365,309,856 B |
| Same-sample named scratch peak (only valid aggregate) | 88,540,426,240 B (82.46 GiB) |
| Disposable scratch left after run | 194,202 B |

**Scratch estimate versus observed.** The estimate (107.84 GiB including margin) was not exceeded by the 82.46 GiB same-sample named scratch peak; free space after the run was 2,292.8 GiB with artifacts retained. The sampler does not record lowest-free-space, so that value is not reported (spot checks early in Verification stayed above 2,343 GiB); no out-of-disk condition occurred. Expected cache and the final ledger each grew 3.65x for 3.65x Medium records, i.e. approximately linearly.

**Memory.** Peak RSS (3.49 GB) is 3.12x Medium (1.12 GB). It occurred while the E2E harness generated and loaded the in-memory corrected/defective external fixture arrays (RSS rose 1.48 to 2.23 to about 3.45 GiB), then fell to about 2.14 GiB once collected and stayed flat while Verification scratch grew by tens of GiB. This indicates the increase is a harness-fixture effect rather than Verification state, but it is a measured increase and is reported, not hidden; no constant-memory claim is made.

**Scaling (workers=8).**

| Scale | Records | Processing | s/100k | Verification | Verification s/100k | Peak RSS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 100k | 100,000 | 1,086.520 s | 1,086.52 | 806.783 s | 806.78 | 497,209,344 B |
| 200k | 200,000 | 2,451.565 s | 1,225.78 | 1,910.802 s | 955.40 | 636,911,616 B |
| Medium | 411,000 | 5,574.083 s | 1,356.20 | 4,423.960 s | 1,076.39 | 1,119,571,968 B |
| Large | 1,500,000 | 28,143.783 s | 1,876.25 | 23,186.709 s | 1,545.78 | 3,494,281,216 B |

The curve is superlinear: normalized processing rises 38.35% and Verification 43.61% from Medium to Large (and 12.8% / 18.4% from 100k to 200k). Absolute throughput remains practical on this host (about 7.8 h of processing for 1.5M generated records), scratch grows approximately linearly, and the remaining superlinearity is attributable to the same per-operation SQLite/ledger cost growth identified at 100k/200k rather than a new failure mode; no stage-level explanation beyond the table above is claimed. Linear scaling, constant memory, an SLA and any result beyond exactly 1,500,000 generated records are not claimed. The 8.22M figure remains a generator enumeration only.

**Fingerprints.** Configuration, graph, and rule-set hashes match Medium; source/projection fingerprints differ with the larger corpus. Evidence, Recovery assessment and DryRun fingerprints remain provenance/run-bound; canonicalization was not changed.

**Decision: the Large full-pipeline gate passed** (at least 1,500,000 generated records; 0 unaccounted, 0 unexplained, 0 corrected Verification/Recovery failures; `QUALIFIED`; required outputs persisted). PS-0.10D remains **In Progress**: post-Large focused/full/vendor/Pension regression, final commit and remote zero-skip CI are still required and the final report must be reviewed. Do not run a second Large or 2M+, begin PS-0.10E, or perform production migration/rollback.


## Final Acceptance Closure

**Status:** PS-0.10D is In Progress and unaccepted. Local and vendor gates below have passed; remote CI is NOT YET run and formal acceptance requires review of the completion report.

### Retained Architecture

`VerificationExecutionPlan` with descriptor-declared required fields/keys; reduced-field scratch projection; shared immutable expected-state worksets; set-oriented ledger staging/finalization with deferred secondary indexes and one canonical ledger; deterministic eight-partition actual/journal scratch with bounded partition scratch-write workers (`maxPartitionWorkers=8`); `GlobalRuleReference` semantic evaluation; independent external target observation; and `scripts/estimate-pension-scratch.ps1`, a path-bound, scale-bound, fail-closed, margin-aware capacity preflight that `large-acceptance` requires. Estimate 107.84 GiB including a 25% margin versus an observed same-sample named scratch peak of 82.46 GiB is benchmark evidence for this run, not a universal sizing formula. Disposable scratch left after Large was 194,202 B; raw scratch databases are not committed.

### Rejected Experiments (do not reintroduce)

Prepared-command micro-tuning; parameter-object reuse; `INSERT RETURNING`; larger journal batches; transaction threshold 256 (see Optimization Experiments table); and partition-local semantic rule evaluation, rejected because it multiplied scans/queries (180 to 390 rule scans, 195 to 405 queries) without reducing decoded rows and regressed end-to-end throughput (+2.98% at matched 100k, +21.97% at 200k).

### Limitations (retained, not hidden)

- Large processing s/100k rose 38.35% and Verification s/100k 43.61% versus Medium: superlinear. Scratch (expected cache, ledger) grew approximately linearly (3.65x for 3.65x records).
- workers=8 had worse normalized 100k-to-200k scaling (+12.82% / +18.42%) than workers=4 (+0.33% / +4.66%) despite being fastest in absolute time.
- Large peak RSS (3.49 GB) was reached while the E2E harness held corrected/defective external fixture arrays; it then fell to about 2.14 GB during Verification. No constant-memory claim.
- No SLA, no linear-scaling claim. The permitted Large statement is: ProofShift completed the full physical assurance pipeline for exactly 1,500,000 generated source records. The 8.22M figure is generator enumeration only; no 2M+ or "millions verified" claim is made.
- Separately sampled scratch peaks are not additive; only the same-sample 82.46 GiB aggregate is valid.

### Fingerprint Contract

Execution-independent where inputs match: configuration, graph, checkpoint/source semantics, projection semantics, rule set, business findings, ledger semantic content. Provenance/run-bound: Evidence where reference identities differ, Recovery context derived from Evidence, Recovery Evidence, DryRun. Canonicalization was not changed.

### Validation Results

| Gate | Result |
| --- | --- |
| Focused suites (Engine 4, Verification 47, Evidence 5, Recovery 8, Configuration 12) | 76 passed, 0 failed, 0 skipped |
| Restore / Debug build | Succeeded, 0 warnings, 0 errors |
| Full local suite | 219 total, 217 passed, 0 failed, 2 skipped (Windows symlink-capability tests: `FilesystemShadowRejectsSymlinkEscapeWhenHostAllowsSymlinkCreation`, `CsvConnectorRejectsTraversalAndSymlinkAncestorEscapes`); per project: Configuration 12, Domain 11, EndToEnd 104 (102 passed/2 skipped), Engine 4, Evidence 5, Graph 28, Recovery 8, Verification 47 |
| Fast 149/0 regression | Passed within the full-suite EndToEnd run (`FastDefectiveAndCorrectedDatasetsRunThroughVerificationServiceWithExactBusinessCounts`: 149 defective, `NotQualified` defective/false-Reverse, CLI report/comparison) |
| Oracle Free integration | 2 passed, 0 failed, 0 skipped (4m 34s) |
| Db2 LUW Community integration | 2 passed, 0 failed, 0 skipped (6m 12s) |

The local suite is not a zero-skip result; the two symlink skips are host-specific and do not satisfy the remote zero-skip gate.

### Gate Matrix

| Item | Status |
| --- | --- |
| Profile / attribution (>=95% named coverage: Medium 98.515%, Large 97.218%) | PASS |
| Execution plan; descriptor declarations; required-field materialization; typed semantics | PASS |
| Index strategy (deferred ledger secondary indexes) | PASS |
| No N+1 relationship regression (beneficiary lookup unchanged, measured) | PASS |
| Set-oriented accounting and lineage (ledger staging/finalization) | PASS |
| Bounded concurrency; deterministic partitioning | PASS |
| Global-rule behavior (`GlobalRuleReference`) | PASS |
| Exact decimal handling | PASS |
| Worker-count semantic equivalence; operational settings excluded from semantic identity | PASS |
| Deterministic Evidence semantics | PASS |
| Scale-safe Recovery; external target Verification | PASS |
| Fast 149/0 | PASS |
| Medium >= 2x (13,234.382 s to 5,574.083 s, 2.374x, limit 6,617.191 s) | PASS |
| Large >= 1.5M full pipeline (exactly 1,500,000) and qualification | PASS |
| Resource measurements; scratch estimation | PASS |
| Focused tests; full local tests | PASS (2 documented symlink skips) |
| Oracle; Db2 | PASS |
| Remote CI zero-required-skip | NOT YET |
| No PS-0.10E | PASS (not started) |

PS-0.10D acceptance gates are not yet complete: remote CI and review remain. Do not begin PS-0.10E.
