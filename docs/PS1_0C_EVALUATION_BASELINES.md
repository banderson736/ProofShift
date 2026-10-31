# PS-1.0C — Repeatable Pilot Evaluation Baselines

## Status

**In Progress — C.1-C.5 implementation is complete; assigned C.6 final
acceptance validation is in progress.** Work starts from merged PS-1.0B baseline
`ef90706638e38bb770af4bb0e880011f67aa04f3`. It adds explicit promotion of
completed, evidence-packaged workspace runs, integrity-checked inspection,
retained baseline history, and a read-only comparison of the current
effective plan with an active or selected historical baseline. C.5 adds
rebuildable run history, explicit baseline progression, and safe
configuration-only workspace reuse. C.1-C.5 local implementation and
validation do not claim PS-1.0C acceptance. C.6 validates the complete
baseline-governed workflow using the extracted product package, provider-backed
discovery and schema drift, cross-platform package evidence, and an exact
candidate in protected CI. See the [acceptance matrix](PS1_0C_ACCEPTANCE_MATRIX.md)
for evidence and gates; exact-candidate CI and owner acceptance remain pending.

## Goal

Let an operator explicitly preserve an evaluation run as a named baseline
identity, inspect the active baseline and prior promotions, and fail closed
when the run or its evidence package is missing, invalid, or changed.
Promotion records the run's actual disposition; it never converts
`NOT QUALIFIED` to `QUALIFIED` or treats expected-exception annotations as
waivers.

## C.1 design

The workspace run manifest remains authoritative for execution state and
configuration, graph, checkpoint, Verification, reconciliation, and
disposition identities. The existing final evidence package remains
authoritative for its package identity and embedded artifact integrity.
Promotion requires both artifacts to validate and to bind to the same
completed run. A diagnostic package, incomplete run, external Verification
without a business disposition, missing package, or mismatched reference
cannot be promoted.

The baseline record stores only identities and metadata: workspace, baseline,
run and run-manifest identities; effective-plan, configuration and graph
fingerprints; discovery and applicable checkpoint identities; Verification
and reconciliation identities; evidence-package identity and workspace-
relative reference; disposition; and product/build identity. It does not copy
configuration, customer records, Evidence values, or resolved credentials.
Where a scope applies, its semantics remain bound by the existing graph and
checkpoint-manifest identities rather than duplicating scope predicates in
the baseline.

Baseline records are individual immutable files below
`.proofshift/baselines/`. An integrity-bound catalog stores the retained
promotion history and active-baseline pointer. Each promotion appends a new
record and changes only the catalog; prior baseline records are not rewritten.
The active baseline is the most recently promoted valid record. `list`, `show`,
and `active` validate the catalog, all retained baseline records, their
authoritative run manifests, and their referenced evidence packages before
returning data. Missing or changed references fail closed.

The baseline store is an application-level workspace concern. It leaves the
version-1 workspace manifest and the authoritative run/evidence formats
unchanged. See [ADR-0038](adr/0038-evaluation-baseline-promotion-and-history.md).

## C.2 semantic-drift planning

`workspace plan` defaults to the active baseline when one exists. An operator
may explicitly select a retained baseline by GUID or opt out with `none`.
Historical baseline plans come from the embedded effective plan in the
validated final Evidence package; the current plan is generated offline from
the current workspace. Planning does not run migration or Verification,
perform live discovery, write to the baseline catalog, or persist a review
decision.

The comparison reports deterministic, categorized changes for project and
endpoint configuration/bindings, packs, connector capabilities, graph nodes,
mapping edges and field transformations, verification rules, expected-
exception annotations, reconciliation definitions, evaluation scope, and
persisted physical discovery. Expected-exception changes remain annotations
and never waive findings. Missing or stale discovery is `NotComparable`;
planning does not silently replace it with a live observation. Loss of a
required connector capability and an invalid current plan are blocking.
Other semantic changes are visibly marked as requiring review; a plan is not
an authorization to execute a run.

For each baseline endpoint, planning selects the latest integrity-checked
discovery matching the baseline fingerprint that was observed no later than
the baseline promotion. It requires the latest persisted observation to be
newer than that promotion, so an unchanged structural fingerprint cannot
make a post-baseline rediscovery appear to be the historical observation.

Run-only identities (run, checkpoint, Verification, and evidence identities)
are not semantic plan drift. The plan fingerprint includes canonical
configuration and safe endpoint-binding fingerprints, but excludes workspace
run/evidence output roots and resolved environment or secret values. Changing
an endpoint binding is surfaced without printing its value. Baseline records
retain a workspace-relative run-manifest reference so an output-root
configuration change does not invalidate the historical run/package binding.
The effective-plan semantic model is versioned. A valid baseline package
created with an older effective-plan schema remains integrity-inspectable but
is reported as `NotComparable`; no missing historical detail is inferred.

## C.3 baseline-aware execution and review

Guided execution revalidates the selected baseline and its run/package
references, regenerates the canonical effective plan through the existing
workspace evaluation path, and recomputes C.2 drift immediately before
binding the run. The same baseline selection is explicit in both planning and
execution (`active`, a historical baseline ID, or `none`). If the selected
baseline or effective plan changes between initial planning and execution-time
binding, execution fails closed.

The explicit `workspace review-plan` action persists an immutable record under
`.proofshift/reviews/`. It binds the workspace, baseline ID and baseline plan
fingerprint, current plan fingerprint, C.2 semantic-drift identity/status,
decision, and integrity hash. A `Reviewed` decision means only that the
operator reviewed that exact semantic change set for evaluation. For
`NotComparable`, the separate decision
`ComparisonUnavailableAcknowledged` records that comparison was unavailable;
it never asserts semantic equivalence. Invalid/untrusted baseline or current
configuration state and C.2 `Blocking` conditions cannot be reviewed around.
Review records contain no connection values, resolved credentials, or
Evidence values; old records are retained and never updated.

`workspace run` requires the exact review ID when drift is `ReviewRequired`,
or when `NotComparable` requires the explicit acknowledgement above. A stale
review, changed baseline, changed plan, or changed drift identity is rejected
before checkpoint capture. No review is required for a baseline with no
review-required drift. Review is not production, migration, or rollback
approval.

The run manifest retains references to the selected baseline ID and run,
baseline run-manifest identity, baseline plan and Evidence-package identities,
current plan identity, pre-run drift identity/status, and (when applicable)
the review ID, integrity hash, and decision. The version-1 workspace format
and existing run format are retained; optional new run-manifest fields are
integrity-bound, and older run manifests without them remain readable.

After a completed run, C.3 reuses the B.5 stable run-comparison machinery.
An integrity-bound artifact at
`.proofshift/comparisons/<candidate-run-id>.json` is indexed by candidate run
ID and binds the baseline, candidate manifest and Evidence identities,
pre-run drift identity, detailed comparison identity, and outcome
classification. It does not mutate the baseline or rewrite the candidate run
manifest. Checkpoint replay ancestry is reported separately and alone does
not prevent `Equivalent`. Incomplete/interrupted runs receive no business
outcome classification; local summaries report `NotEstablished`.

The classification is deterministic: `QUALIFIED` to `NOT QUALIFIED` is
`Regressed`; `NOT QUALIFIED` to `QUALIFIED` is `Improved`; two
`NOT QUALIFIED` outcomes are `StillNotQualified`, with finding-level deltas
preserved; two equivalent `QUALIFIED` outcomes are `Equivalent`; and
meaningful changes with a still-`QUALIFIED` candidate are
`ChangedStillQualified`. Missing business disposition or insufficient
comparison evidence is `NotComparable`. Finding, expected-exception,
reconciliation, materialized-state, discovery, and target-identity details
come from the existing comparison contract. Expected-exception annotations
never waive findings or count as defect resolution by themselves.

Baseline-bound local summaries report baseline disposition, pre-run drift and
review state, candidate disposition, post-run outcome, finding deltas,
reconciliation changes, and materialized-state changes. Baseline promotion
remains an explicit C.1 action and is never automatic.

## C.4 portable governance packages and offline comparison

Baseline-bound packages use the existing B.4 ZIP and integrity model. The
version-1 package index adds an optional governance format marker and
allowlisted roles: `baseline-reference`, `pre-run-semantic-drift`,
`plan-review`, and `baseline-outcome-comparison`. Their hashes and paths
participate in logical package identity. No second package format or
comparison engine is introduced.

The package embeds only the immutable identity-only baseline record, not its
historical run or customer Evidence; it embeds the exact persisted drift
artifact, exact review/`NotComparable` acknowledgement when used, and the
already persisted C.3 post-run comparison for a completed run. The drift
artifact records what execution recomputed immediately before binding;
package validation verifies its identity and integrity without recomputing it
from current workspace state. Completed baseline-bound packaging fails
closed if required persisted comparison or drift evidence is missing or
mismatched. Diagnostic/incomplete packages may carry baseline, drift, and
review history but never a post-run business comparison or disposition.

The deterministic summary reports baseline and candidate dispositions,
pre-run drift, whether exact review/acknowledgement was required, outcome,
finding and reconciliation deltas, expected-exception changes, and
materialized-state changes. A `NotComparable` execution explicitly says
comparison was unavailable and does not claim equivalence, improvement, or
regression. `Improved`, `QUALIFIED`, and `Reviewed` remain evaluation
statements, not production readiness or authorization. Expected-exception
annotations never waive findings.

`workspace evidence compare-packages <baseline.zip> <candidate.zip> [--json]`
validates both complete packages offline, without extraction or workspace,
repository, or customer-system access. When the candidate contains baseline
governance, the supplied baseline must match the candidate's exact baseline
run, manifest, and package identities; mismatch fails closed. This is a
baseline-governed comparison and reuses the candidate's stored B.5 comparison,
checking it against the supplied package pair. Two valid packages without a
candidate baseline relationship can be compared ad hoc using B.5, but the
result is explicitly `AdHoc`, has no outcome classification, and does not
fabricate governance linkage.

Historical B.4 version-1 packages without governance fields/roles remain
valid and retain their prior meaning. Validation does not infer a baseline
relationship for them. The optional governance-format field and roles are
covered by existing package identity/integrity fields; unknown required
semantics continue to fail closed. Only allowlisted embedded artifacts are
accepted. Raw source rows, resolved secrets, and materialized target rows
remain excluded; Evidence may contain customer-sensitive values.

## C.1-C.5 repeatable history and workspace reuse

`workspace runs` rebuilds run history on each request by scanning run
directories and validating each run manifest against the workspace and its
directory identity. It does not maintain a second persistent run index.
Manifest trust, package validity, and baseline-comparison validity are
reported separately; malformed or mismatched run directories are shown as
untrusted entries rather than presented as valid runs. A missing package or
invalid comparison does not silently become valid history.

Baseline progression remains explicit. Promoting a candidate records its
actual disposition and, for a baseline-bound run, the comparison baseline and
outcome that existed before promotion. The new record links to the previously
active baseline; prior records and packages remain immutable. Repeating
promotion of the currently active run is idempotent and adds no history.
Promoting a run already represented by a historical baseline is rejected;
historical reactivation is not supported. A `NOT QUALIFIED` candidate can be
promoted for continued evaluation, but promotion neither changes nor endorses
its outcome. A `QUALIFIED` candidate is likewise never promoted
automatically.

`workspace init <destination> --from <source-workspace>` creates a new
workspace by reusing the validated source workspace's project configuration
and its referenced configuration files. It requires a new or empty,
non-overlapping destination. The derived workspace receives a new workspace
ID and fresh default runtime directories; it does not copy runs, baselines,
reviews, Evidence, discovery, generated files, customer data, or scratch.
Configuration references are preserved without resolving or copying secret
values. References must remain contained within the source workspace, avoid
runtime-data/artifact directories, and not traverse symlinks or reparse
points. This is configuration reuse, not an environment-binding migration or
transfer of prior evaluation evidence.

```text
proofshift workspace runs <workspace> [--json]
proofshift workspace init <destination> --from <source-workspace>
```

The pilot sequence can now be repeated safely: derive an isolated workspace
from validated configuration, evaluate and package a candidate, inspect
rebuilt run history, and explicitly promote a selected candidate when
appropriate. Existing baseline planning, review, offline comparison, and
package behavior are unchanged.

## C.6 packaged acceptance workflow

The Linux packaged-Docker rehearsal runs the operator lifecycle from the
extracted evaluation package. It creates and retains initial source and
shadow-target discovery artifacts, executes and packages a qualified baseline,
and explicitly promotes it. The synthetic shadow database then receives an
additive test-only schema change; a second read-only discovery is persisted and
`workspace plan` must classify the field addition as `ReviewRequired`. Planning
does not perform live discovery or change the baseline. An immutable review is
recorded for that exact drift, and a new guided evaluation must revalidate the
same baseline, drift, and review before checkpoint capture.

Preflight integrity-checks every retained discovery artifact but compares only
the latest artifact for each endpoint with current read-only discovery. Older
artifacts remain available for baseline comparison; they are not treated as
the current schema and do not block an explicitly reviewed drift. The
baseline-governed candidate must complete with its original disposition,
produce a valid package, and compare offline against the supplied baseline
package as `ChangedStillQualified`. The workflow explicitly promotes the
candidate, confirms the old baseline record is unchanged, verifies promotion
idempotency and historical re-promotion rejection, and derives a fresh
configuration-only workspace with a new identity and no copied runtime state.

This rehearsal uses only deterministic synthetic fixtures and isolated
test containers. The schema alteration is performed by the test harness; the
packaged discovery and preflight operations remain read-only. This workflow
does not authorize production mutation, external Recovery qualification,
publishing, signing, or PS-1.0D work.

## C.1-C.6 operator commands

```text
proofshift workspace evidence package <workspace> <run-id>
proofshift workspace evidence compare-packages <baseline.zip> <candidate.zip> [--json]
proofshift workspace runs <workspace> [--json]
proofshift workspace init <destination> --from <source-workspace>
proofshift workspace baseline promote <workspace> <run-id> [--json]
proofshift workspace baseline list <workspace> [--json]
proofshift workspace baseline show <workspace> <baseline-id> [--json]
proofshift workspace baseline active <workspace> [--json]
proofshift workspace plan <workspace> [--baseline active|<baseline-id>|none] [--json]
proofshift workspace review-plan <workspace> [--baseline active|<baseline-id>] [--json]
proofshift workspace run <workspace> [--baseline active|<baseline-id>|none] [--review <review-id>] [--checkpoint <id-or-path>] [--observation <label>] [--json]
proofshift workspace summary <workspace> <run-id> [--json]
```

The evidence package command must produce the normal final package at the
workspace's configured `evidence/<run-id>.zip` location before promotion.
Promotion is an explicit operator action, not an automatic consequence of a
run or package operation.

The portable pilot sequence is: evaluate; package and explicitly promote a
baseline; change the workspace/data; plan and review drift; run against the
baseline; package the candidate; validate it; then compare the baseline and
candidate packages offline. Promotion of a newer candidate remains a
separate explicit choice. Review is not production approval.

## Boundaries

- C.1-C.6 record and compare evaluation identities; guided runs use the
  existing B.3 runtime and do not authorize production migration or rollback.
- A promoted `NOT QUALIFIED` baseline remains `NOT QUALIFIED`; promotion is
  not an approval or endorsement.
- External read-only Verification remains read-only and cannot be promoted
  without a completed business disposition.
- Baselines do not retain raw checkpoint data, source data, customer Evidence
  values, or resolved secrets.
- Content hashes provide integrity/tamper detection, not signer identity or
  authenticity.
- C.3 review acknowledges only exact evaluation semantics; it does not assert
  safety, correctness of future results, or production approval.
- External read-only Verification remains read-only and does not qualify
  Recovery. A completed external observation without business disposition is
  not classified as a qualified outcome.
- A `QUALIFIED` candidate is not automatically promoted; promotion remains a
  separate explicit C.1 operation.
- Exact-candidate CI and owner acceptance remain pending. PS-1.0C remains
  unaccepted; PS-1.0D has not started.
