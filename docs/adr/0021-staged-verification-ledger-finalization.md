# ADR 0021: Staged Verification Ledger Finalization

- Status: Experimental PS-0.10D candidate; retention depends on measured gates
- Date: 2026-10-07

## Context

ADR-0017 persists opaque graph-scoped facts in bounded WAL/NORMAL ledger transactions and publishes a completion receipt. Medium profiling measured 3,761.924 s of ledger write scopes and 12,832,403 API operations; table-level Medium counts were not measured and are not inferred from smaller probes. Saved final workset Fast/50k/100k telemetry identifies separate source registration/disposition facts, lineage-source fanout, and journal-source/target scope fanout. These query indexes duplicate associations already carried in opaque lineage/journal payloads.

## Decision Under Test

Keep the public ledger contract, durable table schema, opaque payloads, indexed Recovery streams, source/disposition distinction, conflict behavior, and semantic fingerprint v1. Accumulate logical facts in a separate private disk-backed `ledger.sqlite.staging` database, not in completed Evidence. Staging uses WAL/NORMAL and bounded 4,096-logical-write commits. This threshold applies only to disposable staging; it is not a larger durable per-artifact batch or the primary optimization.

Derive lineage-source bindings and terminal journal scope with `INSERT ... SELECT` / SQLite JSON iteration in bounded 16,384-parent-row phases. Preserve strict duplicate lineage binding rejection and conflict-ignored source/target registration and journal scope. Derive before indexed pre-completion reads used to compute source dispositions. New appended facts use rowid watermarks, never a repeated full rebuild. Rowids and execution order are operational, not semantic identity.

After semantic evaluation, freeze writes, enter Finalizing, and copy tables set-wise into the unchanged durable schema with at most 16,384 rows per transaction. Durable publication uses WAL/FULL, strengthening synchronization relative to ADR-0017 rather than weakening it. Compare staged/final semantic fingerprints, counts, and coverage; run SQLite integrity validation. Add a separate auxiliary checksum for graph nodes, lineage bindings, and journal scope. It is an integrity checksum only and does not enter Evidence/Recovery semantic fingerprints. Legacy complete ledgers remain readable with their existing validation contract.

Publish the existing completion metadata and the Complete state atomically in the last durable transaction. Pending, Finalizing, Failed, and Cancelled are explicit operational metadata. No receipt or completed summary is exposed before publication. Handled failure/cancellation rolls back pending staging work, records a terminal state where possible, and removes incomplete run files on disposal. A process/power failure may leave incomplete files; absence of Complete metadata rejects them. A previously completed run directory cannot be reused or overwritten. New receipts carry an optional auxiliary checksum so deleting lifecycle/checksum metadata cannot downgrade their reopen contract to legacy validation; Evidence semantic identity remains unchanged.

Successful publication deletes disposable staging after closing it; inability to delete scratch does not invalidate an already committed complete ledger. If an unexpected exception occurs after writing the completion marker but before returning success (for example, telemetry failure), revoke in-memory completion caches/receipt, record Failed where possible, reject reopening, and clean the incomplete output on disposal. Reopening validates the original fingerprint/count contract plus the auxiliary checksum for newly finalized ledgers. Recovery still streams the finalized ledger, never staging or a full in-memory lineage graph.

## Measurements and Gates

Report staging API/insert operations and payload bytes separately from derived SQL phases and durable copy operations. Record staged/finalized rows, maximum rows per copy, SQL executions, phase duration, staging DB/WAL, durable DB/WAL, workspace, and workload RSS. Preserve hierarchical exclusive timing; aggregate write metrics must not be added to their enclosing wall time.

Run Fast, then 50k only without obvious Fast regression. Run 100k only when 50k indicates improvement. Compare end-to-end runtime with accepted 2,723.773 s at 100k, not the rejected 2,777.283 s workset candidate. Advancement normally requires at least 20% end-to-end improvement, or at least 40% ledger persistence/finalization reduction plus clear end-to-end improvement. No 200k/Medium/Large until the explicit gates pass. PS-0.10D remains In Progress; PS-0.10E, workspace/rule redesign, concurrency, and production execution/rollback remain out of scope.

## Alternatives

- Keep direct durable inserts: baseline and fallback if finalization overhead outweighs saved fanout/durability work.
- Reuse prepared statements or increase durable per-command batches: prior experiments did not demonstrate scalable benefit; not the strategy here.
- Stage in memory: rejected because successful populations must remain disk-backed and bounded.
- Stage in the Verification workspace: rejected for this experiment because it couples independent workspace and ledger redesigns.

## Workset Decision

Retain the existing reduced-field projection as a scratch optimization: it already enforces descriptor/identity requirements through the codec without a second storage strategy or new ledger dependency. The final 100k workset saved 24.73% workspace and 26.29% WAL, but regressed runtime 1.96%. No throughput claim is made. Leave workset code unchanged during the ledger experiment so its effect is isolated.

## Measured Candidate Decision

Fast, 50k and 100k each passed 2/2 physical assurance tests without failures/skips, preserving exact 149/0 and Recovery/qualification behavior. At 50k, runtime improved 17.82% against accepted code. At 100k, runtime improved 14.73% (2,322.632 s versus 2,723.773 s); combined measured staging/derivation/finalization improved 29.12% against accepted ledger write scopes (478.506 s versus 675.124 s). The candidate includes completion/integrity cost that was not separately instrumented in the old write metric. Successful INSERT/UPDATE executions fell 28.66%, but logical API fact count is unchanged. Peak RSS increased 1.50%; staging adds transient disk duplication during publication. Normalized runtime grows 12.40% from 50k to 100k, so scaling is still superlinear at these points.

Neither 100k advancement threshold passed. No 200k, Medium or Large run is warranted or started. Retain this local experimental implementation for review as a measured ledger improvement, not an accepted throughput solution or PS-0.10D completion. The detailed table, timing and scratch report is in `docs/PS0_10D_INITIAL_PROFILE_AND_EXPERIMENTS.md`.

## Scaling-Differential Index Experiment

The subsequent explicitly assigned scaling investigation retains ADR-0021 and ADR-0022 cumulatively. Saved 100k/200k traces show linear fact counts but staging cost growing 2.619x and durable finalization 2.589x. Test deferring only secondary indexes: staging builds them before required reads or finalization; destination builds all after bounded table copies but before integrity/completion publication. Primary/unique constraints remain active, the 16,384-row copy bound is unchanged, and final table/index schema plus semantic/auxiliary fingerprints are unchanged. SQL-declared index definitions come from SQLite metadata, not pack-specific knowledge. Record both index-build phases and complete runtime/storage cost. This is one experimental ledger-index candidate, not a retained performance win yet; no simultaneous scan fusion, covering index, relationship rewrite or broad tuning.

### Measured Index-Strategy Retention

The single index candidate passed Fast/50k/100k/200k exact physical gates, each 2/2 without failures/skips. At 100k, processing improved from retained 1,939.660 s to 1,821.961 s (-6.07%); at 200k, 4,469.994 s to 4,078.879 s (-8.75%). Full measured ledger work including index-build costs improved 40.30% / 44.00%. Final secondary index definitions, primary constraints, opaque row counts, fingerprints and fail-closed lifecycle remain unchanged; tests require all seven indexes on staging/destination before publication. RSS is essentially unchanged and ledger/WAL sizes decrease.

Retain deferred secondary index phases as a measured cumulative ledger foundation. This supersedes the candidate-pending wording above but does not accept PS-0.10D or relax durability. Candidate normalized 200k processing penalty is 11.94%, Verification 14.69%, still outside the preferred at-most-8% progression criterion. No Medium/ Large or stage-based Medium forecast is authorized by this result; more residual-cost work requires a new explicit assignment.