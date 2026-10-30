# PS-1.0C Acceptance Matrix

Status: **In Progress — C.1-C.5 implementation complete; assigned C.6 final
acceptance validation in progress.** Focused local evidence for C.1-C.5 is
recorded below. The extracted-package pilot, Docker-backed live discovery and
schema-drift workflow, cross-platform package acceptance, exact-candidate CI,
and explicit owner acceptance remain pending. This matrix is not an acceptance
claim.

| # | C.1 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Baseline promotion is an explicit action over a completed run with a final evidence package | PASS | `WorkspaceBaselinePromotionRetainsImmutableHistoryAndExposesActiveBaseline` explicitly packages then promotes the selected run. |
| 2 | Valid `QUALIFIED` and `NOT QUALIFIED` runs can be promoted without changing their disposition | PASS | The focused baseline test promoted both outcomes and asserted their stored dispositions remain exact. |
| 3 | Incomplete runs and invalid run manifests fail closed | PASS | `WorkspaceBaselinePromotionAndInspectionFailClosedOnIncompleteOrTamperedReferences` rejected an incomplete run and an invalid run manifest. |
| 4 | Run-manifest and evidence-package identities bind to the same workspace and run | PASS | The focused tests promoted a correctly bound package and rejected a valid package belonging to a different run. |
| 5 | Missing, modified, or invalid run/package references are rejected during promotion and inspection | PASS | The focused tests rejected a missing package, invalid run manifest, mismatched package, and package tampering during baseline inspection. |
| 6 | Baseline records contain the required configuration, graph, plan, discovery, checkpoint, Verification, reconciliation, package, disposition, and build identities | PASS | Promotion JSON assertions verify the run, plan/configuration/graph, discovery, checkpoint, Verification, reconciliation, package, disposition, and product identities; store reads revalidate bindings. |
| 7 | Baseline records do not copy raw customer data, Evidence values, or resolved secrets | PASS | The focused test asserts that synthetic sensitive Evidence values and exception text are absent from the persisted baseline record. |
| 8 | Promotion appends immutable history and updates active selection without rewriting earlier records | PASS | The focused test promotes two runs, byte-compares the first record before/after, and verifies the second record points to the prior active baseline. |
| 9 | `list`, `show`, and `active` inspect retained baselines and fail closed on broken references | PASS | The focused test exercises empty and populated list/active, historical show, and tampered-reference rejection. |
| 10 | Existing workspace manifest, run, evidence-package, and PS-1.0B behavior remains compatible | PASS | The latest complete `AuthoringCliTests` class run passed 59 tests with 0 failures; 2 host-capability symlink tests were skipped. Existing B.4 package tests remain part of the class. |
| 11 | Focused tests and `git diff --check` pass | PASS | The focused class passed and `git diff --check` passed. |

## C.2 semantic-drift planning gates

| # | C.2 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Plan supports active, selected historical, and no-baseline comparison without executing a run or writing baseline state | PASS | `WorkspacePlanReportsNoBaselineAndIgnoresRunOnlyAndOutputRootChanges` checks `none`, active and a selected historical baseline, empty run output before planning, and unchanged catalog bytes after planning. |
| 2 | Historical plan is read from the validated final package and matches the stored workspace, plan, and configuration identities | PASS | The same regression reads the packaged effective plan and compares its identities; the baseline plan service validates package/run identity before comparison. |
| 3 | Run-only identity, replay ancestry, `QUALIFIED`/`NOT QUALIFIED` outcome, and output-root changes do not create plan drift | PASS | The regression uses two `NOT QUALIFIED` runs with different run IDs and checkpoint replay ancestry, changes run/evidence roots, and verifies `NoSemanticChange`. |
| 4 | Configuration, mapping, verification-rule, expected-exception, reconciliation, and scope changes are deterministically categorized | PASS | `WorkspacePlanSemanticDriftCategorizesMeaningfulPlanChangesAndBlocksMissingCapabilities` exercises each plan category. |
| 5 | Required capability removal blocks; ordinary semantic changes require review | PASS | The typed plan comparison test verifies blocking classification for removed `sourceRead` and review classification for mapping changes. |
| 6 | Discovery drift uses persisted, integrity-checked artifacts; missing history is not comparable and planning performs no live discovery | PASS | `WorkspacePlanComparesPersistedDiscoveryAndReportsMissingHistoryWithoutLiveDiscovery` detects an added field, then removes the baseline artifact and verifies `NotComparable`. |
| 7 | Runtime secret values are excluded; configuration-binding changes are visible without exposing values | PASS | The output-root regression changes the runtime value without drift, then changes the secret reference and verifies categorized drift with both runtime values absent from JSON. |
| 8 | Invalid active baseline references fail closed during planning | PASS | `WorkspaceBaselinePromotionAndInspectionFailClosedOnIncompleteOrTamperedReferences` asserts planning returns `PSPLAN099` after the active package is tampered. |
| 9 | Assigned local validation: focused C.2, Configuration, Graph, Verification, applicable discovery tests, CLI build, docs/link validation, and whitespace checks | PASS | All feasible local validations listed below passed. The PostgreSQL/SQL Server discovery cases require Docker, which is unavailable locally; exact-candidate CI and owner acceptance remain pending. |

## C.3 baseline-aware execution, review, and outcome gates

| # | C.3 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Review records are immutable and bind workspace, selected baseline, exact plan/drift identity, decision, and integrity | PASS | `WorkspacePlanReviewBindsExactBaselineAndDriftAndBlocksBeforeCheckpointCapture` verifies the review identity, sanitized persistence, exact validation, and rejection after baseline or plan drift changes. |
| 2 | `NotComparable` uses a distinct acknowledgement and does not claim semantic equivalence | PASS | `WorkspacePlanComparesPersistedDiscoveryAndReportsMissingHistoryWithoutLiveDiscovery` persists and validates `ComparisonUnavailableAcknowledged` against the exact unavailable comparison. |
| 3 | Review-required drift is rejected without its exact review before checkpoint capture; blocking drift cannot be bypassed | PASS | The missing-review test verifies rejection before checkpoint capture; `WorkspaceRunRejectsBlockingDriftRegardlessOfReview` asserts blocking conditions throw before execution regardless of review state. |
| 4 | Guided execution revalidates the selected baseline and canonical current plan/drift before checkpoint capture | PARTIAL | The run service performs offline and connected recomputation and rejects mismatched identities. The local C.3 tests validate stale review rejection; a successful baseline-bound live execution test is not available in the local environment. |
| 5 | Run-manifest identities bind baseline, pre-run drift, and optional review without changing the version-1 format | PASS | C.3 outcome tests persist the baseline/drift binding, validate it on read, and reject tampering with the drift identity. Historical null-field compatibility remains covered by the existing C.1/C.2 workspace regressions; no manifest version change was made. |
| 6 | Completed candidates reuse B.5 comparison and classify qualification transitions, changed-qualified outcomes, and missing disposition | PASS | `WorkspaceBaselineOutcomeComparisonClassifiesQualificationAndReplayWithoutPromoting` covers `Equivalent`, `ChangedStillQualified`, `Improved`, `Regressed`, `StillNotQualified`, and `NotComparable`, then validates the stored comparison. |
| 7 | Incomplete runs receive no business comparison or persisted outcome | PASS | The outcome regression sets a candidate to `Interrupted`, verifies no comparison record is returned or written, and existing incomplete-summary coverage reports `NotEstablished`. |
| 8 | Local summaries expose pre-run drift separately from post-run outcomes; no baseline is automatically promoted | PASS | The outcome regression checks the post-run summary classification and confirms the active baseline remains unchanged. |
| 9 | Expected-exception annotations remain non-waiving; external Verification remains read-only and does not qualify Recovery | PASS | Existing B.5 and workspace tests assert findings remain active under expected-exception annotations; C.3 changes do not modify those runtime contracts. |
| 10 | Assigned focused C.3 regressions and full authoring CLI regression class pass | PASS | Focused review/acknowledgement/outcome/blocking tests passed; the latest complete `AuthoringCliTests` class run passed 59 tests with 0 failures and 2 host-capability symlink skips. |

## C.4 portable governance packages and offline comparison gates

| # | C.4 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Baseline-bound packages preserve exact baseline, execution-time drift, review, and persisted outcome identities using allowlisted optional roles | PASS | `BaselineGovernancePackageValidatesAndComparesOffline` validates the governance marker and all four roles, then successfully validates the completed package. |
| 2 | Diagnostic/incomplete packages can preserve pre-run governance but cannot claim a post-run business comparison | PASS | The same regression packages an interrupted diagnostic run and asserts it validates without a `baseline-outcome-comparison` role. |
| 3 | Offline comparison validates exact supplied baseline binding; unbound package pairs are explicitly ad hoc | PASS | The regression verifies a baseline-governed pair and an ad hoc pair, and rejects a different otherwise-valid baseline package for a governed candidate. |
| 4 | Tampering or substituting valid artifacts from another identity is rejected | PASS | The regression substitutes review, drift, outcome comparison, baseline reference, and summary artifacts, as well as malformed comparison bytes; every package fails validation. |
| 5 | Offline outcome preserves finding, expected-exception, and materialized-state changes | PASS | The regression asserts `Regressed`, the added materialized-state finding, and the expected-exception delta for the defective candidate. |
| 6 | Package generation is deterministic and legacy package behavior remains valid | PASS | Repeated candidate packaging produces the same package identity; existing B.4 package validation regressions pass in the full authoring class. |
| 7 | Baseline-aware summaries report outcome transitions and explicitly avoid conclusions when comparison is unavailable | PASS | `WorkspaceBaselineOutcomeComparisonClassifiesQualificationAndReplayWithoutPromoting` checks improved/not-qualified outcomes and asserts `NotComparable` is not reported as `Equivalent`. |
| 8 | Focused C.4 regressions and the full authoring CLI regression class pass | PASS | Both focused C.4/C.3 regression tests passed; the latest complete `AuthoringCliTests` class run passed 59 tests with 0 failures and 2 host-capability symlink skips. |

## C.5 repeatable pilot history and re-baselining gates

| # | C.5 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Run history is rebuilt from run directories and validates manifests against workspace/directory identity without a persistent index | PASS | `WorkspacePilotHistorySupportsExplicitNotQualifiedProgressionAndRebuildableRunListing` verifies valid entries and an invalid manifest is returned as untrusted history. |
| 2 | Run trust, package status, and comparison status are distinct; invalid package/comparison state is not represented as trusted success | PASS | The run-history regression verifies valid package/outcome identities and invalid run trust; the history service reports package and comparison state independently. |
| 3 | Baseline progression records the prior active baseline and the candidate's existing comparison/outcome without changing its disposition | PASS | The progression regression promotes a `NOT QUALIFIED` candidate and asserts its predecessor, comparison baseline, `StillNotQualified` classification, and unchanged disposition. |
| 4 | Re-promoting the active run is idempotent; re-promoting a historical baseline run fails without changing history | PASS | The progression regression asserts `AlreadyActive`, rejects the historical run with `PSWORKBASE098`, and verifies the catalog still has two entries. |
| 5 | Changed-but-qualified candidates remain active only after explicit promotion; prior record/package bytes remain unchanged | PASS | `WorkspacePilotHistoryPromotesChangedQualifiedCandidatesOnlyExplicitly` checks the active pointer before/after promotion and byte equality of the candidate package; the progression test byte-compares prior baseline/package artifacts. |
| 6 | Derived workspaces reuse only validated configuration references, get a new identity, and exclude all source execution/history/data outputs | PASS | `WorkspaceInitFromReusesConfigurationWithoutCopyingHistoryDataOrSecrets` asserts copied config bytes, new ID, empty runtime/history directories, no baseline/review state, and preserved source data. |
| 7 | Workspace derivation rejects overlapping source/destination roots and nonempty destinations without overwriting existing files | PASS | `WorkspaceInitFromReusesConfigurationWithoutCopyingHistoryDataOrSecrets` asserts overlapping roots fail before creating output and a nonempty destination's sentinel file remains unchanged. |
| 8 | Workspace derivation rejects source-configuration and destination symlink/reparse-point traversal | PARTIAL | Both guards and regression tests are implemented; source-configuration and destination symlink cases were skipped because this Windows host does not permit symlink creation. Linux CI evidence is pending. |
| 9 | C.5 regressions and the complete authoring CLI class pass | PASS | The latest `AuthoringCliTests` run passed 59 tests with 0 failures; both symlink-capability tests were skipped on this Windows host. |

## Validation evidence

| Validation | Result |
|---|---|
| Focused workspace/CLI regression suite | `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests`: 59 passed, 0 failed, 2 skipped (source-configuration and destination symlink creation unavailable on this Windows host). SDK 10.0.403 was selected with temporary same-band roll-forward; `global.json` was restored byte-for-byte. |
| Focused C.4 governance/offline comparison and C.3 summary outcome regressions | 2 passed, 0 failed, 0 skipped |
| C.3 review/acknowledgement/outcome/blocking regressions | 4 passed, 0 failed, 0 skipped |
| Configuration and Graph tests | Configuration 12 and Graph 30 passed; 0 failed and 0 skipped. |
| Evidence, Verification, Recovery, Engine, and Projection runtime tests | Evidence 5, Verification 53, Recovery 8, Engine 4, and ProjectionRuntime 15 passed; 0 failed and 0 skipped. |
| Applicable discovery/Evidence tests | New persisted-discovery plan/acknowledgement regression passed; three non-Docker `PhysicalDiscoveryTests` passed; Evidence package/baseline coverage passed in `AuthoringCliTests`. |
| Docker-backed PostgreSQL/SQL Server discovery tests | Not run successfully: Docker endpoint `npipe://./pipe/docker_engine` was unavailable. The full `PhysicalDiscoveryTests` class was attempted and those two tests failed at container startup. |
| CLI build | `dotnet build src\ProofShift.Cli\ProofShift.Cli.csproj --no-restore` using installed .NET SDK 10.0.403: passed, 0 warnings, 0 errors. |
| End-to-end test-project build | `dotnet build tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --no-restore` using installed .NET SDK 10.0.403: passed, 0 warnings, 0 errors. |
| C.5 authoring CLI tests | `dotnet test --project tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests`: passed, 59 passed, 0 failed, 2 skipped (symlink creation unavailable). SDK 10.0.403 was selected with temporary same-band roll-forward; `global.json` was restored byte-for-byte. |
| Documentation/link validation | Relative links in six product documentation files passed. `.github/copilot-instructions.md` retains pre-existing `docs/...` links that do not resolve relative to `.github`; no new links were added there. |
| Whitespace/error check | `git diff --check`: passed. |
| Exact-candidate CI / owner acceptance | Pending; no product acceptance is claimed. |

## C.6 final acceptance gates

| # | C.6 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | C.1-C.5 source and documentation diff has a complete final review with no unrelated behavior changes | PASS | Final review found and fixed successful baseline-bound runs not persisting their comparison; the run now reloads its authoritative manifest before comparison, and the packaged workflow asserts the returned outcome. No unrelated behavior changes were identified. |
| 2 | Focused authoring CLI regressions, including latest parser and symlink guard changes, build and pass | PASS | Latest complete `AuthoringCliTests` run: 59 passed, 0 failed, 2 host-capability symlink skips. |
| 3 | Packaged workflow scripts parse and `git diff --check` passes | PASS | PowerShell parsing passed for `scripts/run-guided-workspace-evaluation.ps1` and `scripts/smoke-evaluation-package.ps1`; `git diff --check` passed after the final lifecycle assertion. |
| 4 | Extracted Linux package creates real source/shadow discovery artifacts and a qualified packaged baseline | NOT YET | Docker is unavailable locally; exact-candidate Linux package workflow has not run. |
| 5 | Physical schema change is detected as `ReviewRequired`, planning remains offline/read-only, and exact review binds the candidate | NOT YET | Provider-backed workflow is implemented in the packaged runner but not yet executed. |
| 6 | Candidate completes baseline-bound execution and remains `QUALIFIED`; external Verification remains read-only and does not qualify Recovery | NOT YET | Exact-candidate packaged evidence is pending. |
| 7 | Offline package comparison validates baseline binding and reports `ChangedStillQualified` with discovery deltas | NOT YET | Exact-candidate packaged evidence is pending. |
| 8 | Candidate re-baselining preserves history, is idempotent when repeated, and rejects historical re-promotion | NOT YET | Exact-candidate packaged evidence is pending. |
| 9 | Extracted-package workspace derivation reuses only configuration with a new identity and no copied runtime/data state | NOT YET | Exact-candidate packaged evidence is pending. |
| 10 | Source and destination symlink tests pass on a symlink-capable runner with no skip | NOT YET | Windows host skipped both tests; Linux CI result is pending. |
| 11 | Linux and Windows packages build reproducibly; Windows remains package-only and Linux claims remain packaged-Docker scoped | NOT YET | Required exact-candidate package jobs have not run. |
| 12 | Full required tests and protected CI pass on the exact candidate; candidate SHA/tree match PR head | NOT YET | No candidate commit, PR, or exact-candidate CI run exists yet. |
| 13 | All final docs, acceptance evidence, and scope boundaries are accurate; owner acceptance is explicit and separate | NOT YET | C.6 remains unaccepted; no self-acceptance or merge is authorized. |

## Scope boundary

This assigned pass implements C.1 baseline promotion/validation/history,
C.2 read-only semantic-drift planning, C.3 baseline-aware
execution/review/outcome comparison, C.4 portable governance packages and
offline comparison, and C.5 rebuildable run history, explicit
baseline progression, safe configuration-only workspace reuse, and assigned
C.6 final validation. The matrix records evidence, not PS-1.0C acceptance.
Exact-candidate package/CI evidence and explicit owner acceptance remain
pending. No production migration or production rollback is implied.
