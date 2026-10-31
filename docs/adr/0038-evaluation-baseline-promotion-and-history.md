# ADR-0038: Evaluation Baseline Promotion and History

- Status: Accepted architecture for PS-1.0C.1-C.5; C.6 technical validation complete, owner acceptance pending
- Date: 2026-10-30

## Context

PS-1.0B provides integrity-bound workspace run manifests and portable final
Evidence packages. Repeatable pilot evaluation needs an explicit way to
preserve a selected completed result and identify which retained result is
currently active, without replacing the run manifest or package as the
authority for their existing identities and evidence.

The workspace manifest is format version 1 and must remain compatible with
existing workspaces. A baseline is governance metadata, not a second run
format, evidence format, or semantic comparison engine.

## Decision

Promotion is an explicit operator command and requires a completed run with
the business disposition `QUALIFIED` or `NOT QUALIFIED`, plus a valid final
Evidence package at the workspace's standard run-bound package path. The
package must bind to the exact run manifest, workspace, product version, plan,
and disposition. Incomplete runs, diagnostic packages, external observations
without business disposition, and invalid or missing references fail closed.

Both permitted dispositions may be promoted. Promotion preserves the
disposition verbatim and is not an approval, qualification, or waiver of
findings.

Store one immutable identity-only record per promotion under
`.proofshift/baselines/`, without adding a required workspace-manifest field
or changing the version-1 workspace paths. Keep a separate integrity-bound
catalog that appends each promotion to history and points to the newest
promotion as active. Updating the catalog never rewrites older baseline
records. A promotion lock prevents concurrent catalog lost updates.

Each baseline binds to its authoritative run manifest and final package by
identity and workspace-relative reference. `list`, `show`, and `active`
validate all catalog entries, baseline record hashes, run manifests, and
referenced package integrity before returning results. Missing or changed
references fail closed. Records contain fingerprints and metadata only, not
configuration contents, source/customer records, raw Evidence values, or
resolved credentials.

The baseline identity hash and catalog integrity hash provide tamper
detection, not signatures, signer identity, or authenticity.

## Alternatives considered

1. **Add baseline paths to `proofshift.workspace.json`.** Deferred because
   this would require a workspace manifest change and migration behavior
   before the first baseline use. The fixed application-owned directory
   preserves version-1 compatibility.
2. **Rewrite a single baseline file on each promotion.** Rejected because it
   would erase promotion history and mutate the prior active baseline.
3. **Copy run configuration or Evidence into the baseline.** Rejected because
   authoritative, integrity-checked artifacts already exist and duplication
   would increase data exposure and create competing identities.
4. **Promote only `QUALIFIED` runs.** Rejected because a valid
   `NOT QUALIFIED` result is still useful as an explicitly selected
   comparison baseline; promotion must never alter its disposition.
5. **Automatically promote after a run.** Rejected because baseline
   selection is an operator governance decision.

## Consequences

- Baseline records are durable workspace metadata, separate from run and
  Evidence package artifacts.
- A previously promoted record remains unchanged when a newer promotion
  changes the active pointer.
- Every inspection revalidates the retained chain and its run/package
  references, so missing or tampered historical evidence can make inspection
  fail closed.
- The package must exist at the workspace's default evidence path; custom
  package exports are not additional baseline authorities.
- C.1 does not authorize baseline-aware execution, production migration, or
  production rollback.

## C.2 semantic-drift planning addendum

The assigned C.2 plan comparison uses the current canonical effective plan
and reads the baseline plan from the embedded effective-plan artifact in the
baseline's validated final Evidence package. It does not reconstruct a
historical plan from current configuration, execute a run, perform live
discovery, change baseline history, or persist an operator review decision.

Plan identity includes canonical configuration identity and hashed endpoint
binding identity, but excludes workspace run/evidence output roots and
resolved environment/secret values. Binding changes are reported using
sanitized category/detail text; values are not copied into drift output.
Because output-root settings may change while a baseline remains retained,
the baseline stores a workspace-relative reference to its authoritative run
manifest rather than resolving the run through the current workspace run
directory setting. The final package reference remains independently
workspace-relative and integrity-validated.

The effective-plan semantic model has an explicit version. A valid package
with a pre-C.2 plan remains inspectable, but because its plan does not contain
the C.2 comparison fingerprints it is reported as `NotComparable`; missing
historical semantics are not reconstructed or guessed. For earlier C.1
baseline records that predate the run-manifest-relative-path field, reference
validation falls back to the workspace's configured run directory.

Comparison reports deterministic categories for configuration, endpoint
bindings, packs, capabilities, graph, mappings, verification rules,
expected-exception annotations, reconciliation, evaluation scope, and
persisted physical discovery. A missing required connector capability or an
invalid current plan blocks; other semantic changes are marked
`ReviewRequired`; missing, corrupt, or non-newer persisted discovery is
`NotComparable`.
Expected-exception annotations remain non-waiving. Plan generation reports
these conditions but does not execute the planned evaluation.

Connected preflight integrity-checks retained discovery artifacts, while
fresh physical comparison uses only the newest persisted artifact for each
configured endpoint. Older artifacts remain available to C.2 baseline
comparison and do not represent the current physical schema.

## C.3 baseline-aware execution and comparison addendum

Guided execution accepts `active`, an explicit historical baseline ID, or
`none`, matching C.2 plan selection. Before checkpoint capture, execution
validates the selected baseline record, its authoritative run manifest and
Evidence package, regenerates the canonical effective plan through the
existing workspace evaluation path, and recomputes C.2 drift. A second
execution-time recomputation is compared with the initially selected plan and
drift so a baseline switch or post-review semantic edit cannot silently
inherit authorization.

Persist the minimum immutable operator review artifact under
`.proofshift/reviews/`. It binds workspace and baseline identity, baseline and
current plan fingerprints, semantic-drift identity/status, decision, review
ID, and integrity hash. `Reviewed` authorizes only evaluation of that exact
semantic change set. `ComparisonUnavailableAcknowledged` is a distinct
decision for a valid but insufficient historical comparison; it is not
semantic equivalence. Blocking drift and invalid/untrusted state cannot be
overridden. No review is required for no-review-needed drift. No roles,
quorum, assignment, or approval workflow is introduced; notes are omitted to
avoid an uncontrolled secret-bearing text field.

New optional C.3 identities are added to the existing run manifest and its
integrity fingerprint without changing the manifest format or requiring new
workspace-manifest fields. They identify the baseline and run, baseline
manifest/plan/package, current plan and pre-run drift, and exact review
identity/decision. Missing optional properties in historical C.1/C.2/B runs
remain null and are omitted from canonical hash input, preserving prior
manifest validation.

After a completed run, reuse B.5 finding matching and run comparison. Persist
an integrity-bound comparison record indexed by candidate run ID under
`.proofshift/comparisons/`; it binds both run identities, baseline package
identity, candidate Evidence identity, pre-run drift identity, comparison
identity and classification. It is not inserted into the already finalized
run manifest, avoiding a circular identity dependency; the index lookup key
is the candidate run ID, and the record validates against that run's final
manifest identity. The baseline and run remain bound to the exact baseline
selected at execution, independent of later active-baseline changes.

Outcome classification distinguishes `Equivalent`,
`ChangedStillQualified`, `Improved`, `Regressed`, `StillNotQualified`, and
`NotComparable`. Replay ancestry alone does not affect the outcome.
`NOT QUALIFIED` to `NOT QUALIFIED` retains detailed finding changes even when
the high-level classification remains `StillNotQualified`. Expected-exception
annotations are compared separately and never waive failed findings.
Incomplete runs have no business outcome classification; local summaries
report `NotEstablished`. Baseline promotion remains the explicit C.1 action
and never occurs automatically.

The existing B.3 execution, checkpoint, replay, projection, Verification,
reconciliation, Recovery, and disposition engines are unchanged. External
Verification remains read-only and does not qualify Recovery. C.3 does not
authorize production migration or rollback.

### C.3 alternatives considered

1. **Store C.3 state in the workspace manifest.** Rejected to preserve
   version-1 workspace compatibility and keep execution state on the run.
2. **Update one mutable review record after each plan change.** Rejected
   because it would erase review history; each review has a new ID and is
   immutable.
3. **Treat acknowledgement of `NotComparable` as review/equivalence.**
   Rejected; the decision is separately named and recorded without asserting
   equivalence.
4. **Create a general approval workflow.** Rejected as unnecessary for the
   single explicit acknowledgement required by this evaluation slice.
5. **Rewrite the final run manifest after comparison to include a direct
   comparison hash.** Rejected because the comparison binds the final run
   identity; updating that manifest would create a circular identity. A
   candidate-run-ID-indexed comparison artifact provides lookup while
   retaining immutable final identity.

C.1-C.5 implementation and C.6 technical validation are complete. The exact
candidate passed push CI run `41169254780` and PR CI run `41169259744`, with
all 11 required jobs passing in each; `build-and-test` passed 362 tests with
0 failures and 0 skips. Explicit owner acceptance remains pending. This ADR
records architecture decisions and does not claim PS-1.0C acceptance.

## C.4 portable governance packages and offline comparison addendum

Keep the existing B.4 ZIP and `proofshift-evidence-package-v1` format.
Governance is an optional marker with allowlisted embedded roles for the
identity-only baseline reference, exact execution-time pre-run drift, exact
review/acknowledgement, and persisted post-run comparison. These entries are
covered by the package's existing logical identity and integrity validation;
they do not introduce a second package authority or copy historical run
Evidence into the baseline reference.

Persist exact pre-run drift at execution time and bind its reference and hash
to the run manifest. Packaging must preserve this artifact as the historical
authority rather than recomputing drift from the current workspace. Validate
each governance artifact's schema, integrity, run/baseline identity, and
classification. Completed baseline-bound packages require the persisted
post-run comparison. Diagnostic/incomplete packages can preserve applicable
pre-run governance but cannot claim a business outcome.

The summary adds baseline and candidate disposition, exact review decision,
pre-run drift, finding/exception/reconciliation/materialized-state changes,
and outcome. `NotComparable` explicitly makes no equivalence/improvement/
regression assertion. Summary output remains deterministic and excludes raw
Evidence values and explanations.

Offline comparison validates both supplied packages and uses the existing B.5
comparison logic. A baseline-governed candidate must bind exactly to the
supplied baseline's run, run-manifest, and Evidence-package identities; a
mismatch is an error, never a fallback. A pair with no candidate governance
link is explicitly `AdHoc` and receives no invented baseline or business
outcome classification. Legacy B.4 packages without governance fields remain
valid. Offline comparison performs no workspace lookup, extraction, live
discovery, or customer-system access.

### C.4 alternatives considered

1. **Recompute historical drift during packaging.** Rejected because current
   workspace state can differ from execution-time state; preserve the exact
   run-bound artifact instead.
2. **Embed the full historical run or Evidence in the baseline reference.**
   Rejected because the existing package and baseline authorities already
   provide identities; duplication would expand sensitive-data exposure.
3. **Silently compare a mismatched candidate as an ad hoc pair.** Rejected
   because it would discard the candidate's declared governance relationship.
4. **Require all package pairs to have baseline governance.** Rejected to
   preserve B.5 ad hoc comparisons and backward compatibility; unbound pairs
   are explicitly labeled without an inferred classification.
5. **Replace the v1 package format.** Rejected because optional allowlisted
   roles preserve existing package validation and legacy package compatibility.

C.4 implementation and validation are recorded in the acceptance matrix.
C.1-C.6 technical validation is complete; explicit owner acceptance remains
pending. This ADR does not claim PS-1.0C acceptance or authorize production
migration/rollback.

## C.5 repeatable pilot history and re-baselining addendum

Run history is a rebuildable projection of the authoritative run directories,
not a second persistent index. Each listing validates the workspace and
checks each run manifest's integrity, workspace identity, and directory/run
identity. Invalid entries remain visible as untrusted history with a
validation issue. Package and baseline-comparison status are reported
separately from manifest trust so missing, invalid, or unavailable artifacts
are not presented as verified outcomes.

Baseline progression remains append-only and explicit. A newly promoted
baseline retains the prior active baseline link and, when its run was bound to
a baseline, the comparison baseline/outcome that was already established for
that candidate. Re-promoting the active run returns an idempotent
`AlreadyActive` result without adding a catalog entry. A run already
represented by a historical baseline is rejected; historical reactivation is
not supported. Promotion does not change a candidate disposition, mutate
prior baseline/package artifacts, or happen automatically.

Workspace derivation is configuration-only:
`workspace init <destination> --from <source-workspace>` requires an
offline-valid source and a new or empty destination outside the source tree.
It copies only the source project configuration and its referenced files,
preserves reference paths and secret-reference semantics without resolving
secrets, and creates a fresh workspace ID with default empty runtime paths.
It excludes runs, baselines, reviews, Evidence, discovery, generated files,
customer data, and scratch. Source/destination containment, overlapping
roots, runtime/artifact directories, and reparse points are checked so reuse
cannot traverse outside the source or copy old evaluation outputs into the
derived workspace. Environment-specific endpoint bindings are preserved as
configuration; derivation does not interpret or normalize them.

### C.5 alternatives considered

1. **Persist a separate run-history index.** Rejected because manifests and
   directories are authoritative; rebuilding avoids stale index state and
   recovery/reconciliation logic.
2. **Allow historical promotion to reactivate an old baseline.** Rejected
   because it makes progression ambiguous and could obscure the current
   candidate history; a historical run must be evaluated as a new candidate
   before explicit promotion.
3. **Copy complete workspace contents when reusing configuration.** Rejected
   because it would carry stale baselines, runtime outputs, discovery, or
   customer data into a different environment and workspace identity.
4. **Resolve environment values or transfer review/discovery state during
   derivation.** Rejected because secret resolution and environment-specific
   observations must remain local to the new workspace; copied configuration
   references do not establish that the new environment has been observed.

C.5 implementation and validation are recorded in the acceptance matrix.
C.6 validated the complete packaged lifecycle, including physical schema
drift and latest-per-endpoint connected discovery, baseline-governed review
and execution, offline comparison, explicit progression, and isolated
workspace derivation. Exact-candidate push and PR CI passed all 11 required
jobs; explicit owner acceptance remains pending. This ADR does not claim
PS-1.0C acceptance or authorize production migration/rollback.
