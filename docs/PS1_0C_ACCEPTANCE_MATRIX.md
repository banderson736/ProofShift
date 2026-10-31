# PS-1.0C Acceptance Matrix

Status: **C.1-C.6 technical validation gates pass; explicit owner acceptance
remains pending.** Candidate `5062a6a317c2bcd8b1c0d09e1d221635210568e4`
(tree `312ddd47cc81f62be942e7b5342e08ceef010538`) passed exact-head push CI
run `41169254780` and PR CI run `41169259744`, with all 11 required jobs
passing in each. The PR synthetic merge commit
`69e496ecafb602408e309e93340b95373dcef9b1` has the same tree as the
candidate. `build-and-test` passed 362 tests, 0 failed, 0 skipped. The
extracted-package rehearsal and persisted governance identities are recorded
below. This matrix records technical evidence, not owner acceptance.

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
| 10 | Existing workspace manifest, run, evidence-package, and PS-1.0B behavior remains compatible | PASS | The latest complete `AuthoringCliTests` class run passed 60 tests with 0 failures; 2 host-capability symlink tests were skipped. Existing B.4 package tests remain part of the class. |
| 11 | Focused tests and `git diff --check` pass | PASS | The focused class passed and `git diff --check` passed. |

## C.2 semantic-drift planning gates

| # | C.2 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Plan supports active, selected historical, and no-baseline comparison without executing a run or writing baseline state | PASS | `WorkspacePlanReportsNoBaselineAndIgnoresRunOnlyAndOutputRootChanges` checks `none`, active and a selected historical baseline, empty run output before planning, and unchanged catalog bytes after planning. |
| 2 | Historical plan is read from the validated final package and matches the stored workspace, plan, and configuration identities | PASS | The same regression reads the packaged effective plan and compares its identities; the baseline plan service validates package/run identity before comparison. |
| 3 | Run-only identity, replay ancestry, `QUALIFIED`/`NOT QUALIFIED` outcome, and output-root changes do not create plan drift | PASS | The regression uses two `NOT QUALIFIED` runs with different run IDs and checkpoint replay ancestry, changes run/evidence roots, and verifies `NoSemanticChange`. |
| 4 | Configuration, mapping, verification-rule, expected-exception, reconciliation, and scope changes are deterministically categorized | PASS | `WorkspacePlanSemanticDriftCategorizesMeaningfulPlanChangesAndBlocksMissingCapabilities` exercises each plan category. |
| 5 | Required capability removal blocks; ordinary semantic changes require review | PASS | The typed plan comparison test verifies blocking classification for removed `sourceRead` and review classification for mapping changes. |
| 6 | Discovery drift uses persisted, integrity-checked artifacts; missing history is not comparable and planning performs no live discovery | PASS | `WorkspacePlanComparesPersistedDiscoveryAndReportsMissingHistoryWithoutLiveDiscovery` verifies unchanged rediscovery remains comparable despite an identical structural fingerprint, detects a later added field, then removes the baseline artifact and verifies `NotComparable`. |
| 7 | Runtime secret values are excluded; configuration-binding changes are visible without exposing values | PASS | The output-root regression changes the runtime value without drift, then changes the secret reference and verifies categorized drift with both runtime values absent from JSON. |
| 8 | Invalid active baseline references fail closed during planning | PASS | `WorkspaceBaselinePromotionAndInspectionFailClosedOnIncompleteOrTamperedReferences` asserts planning returns `PSPLAN099` after the active package is tampered. |
| 9 | Assigned local validation: focused C.2, Configuration, Graph, Verification, applicable discovery tests, CLI build, docs/link validation, and whitespace checks | PASS | Feasible local validations listed below passed. Docker was unavailable locally; the exact-head CI run below supplies PostgreSQL/SQL Server discovery coverage. |

## C.3 baseline-aware execution, review, and outcome gates

| # | C.3 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Review records are immutable and bind workspace, selected baseline, exact plan/drift identity, decision, and integrity | PASS | `WorkspacePlanReviewBindsExactBaselineAndDriftAndBlocksBeforeCheckpointCapture` verifies the review identity, sanitized persistence, exact validation, and rejection after baseline or plan drift changes. |
| 2 | `NotComparable` uses a distinct acknowledgement and does not claim semantic equivalence | PASS | `WorkspacePlanComparesPersistedDiscoveryAndReportsMissingHistoryWithoutLiveDiscovery` persists and validates `ComparisonUnavailableAcknowledged` against the exact unavailable comparison. |
| 3 | Review-required drift is rejected without its exact review before checkpoint capture; blocking drift cannot be bypassed | PASS | The missing-review test verifies rejection before checkpoint capture; `WorkspaceRunRejectsBlockingDriftRegardlessOfReview` asserts blocking conditions throw before execution regardless of review state. |
| 4 | Guided execution revalidates the selected baseline and canonical current plan/drift before checkpoint capture | PASS | Exact-head Linux packaged-Docker rehearsal passed baseline-bound guided execution after physical schema drift, with persisted review `5aa4b6ed-d15c-43fb-815f-48368fd61389`, `QUALIFIED` disposition, and `ChangedStillQualified` persisted outcome. |
| 5 | Run-manifest identities bind baseline, pre-run drift, and optional review without changing the version-1 format | PASS | C.3 outcome tests persist the baseline/drift binding, validate it on read, and reject tampering with the drift identity. Historical null-field compatibility remains covered by the existing C.1/C.2 workspace regressions; no manifest version change was made. |
| 6 | Completed candidates reuse B.5 comparison and classify qualification transitions, changed-qualified outcomes, and missing disposition | PASS | `WorkspaceBaselineOutcomeComparisonClassifiesQualificationAndReplayWithoutPromoting` covers `Equivalent`, `ChangedStillQualified`, `Improved`, `Regressed`, `StillNotQualified`, and `NotComparable`, then validates the stored comparison. |
| 7 | Incomplete runs receive no business comparison or persisted outcome | PASS | The outcome regression sets a candidate to `Interrupted`, verifies no comparison record is returned or written, and existing incomplete-summary coverage reports `NotEstablished`. |
| 8 | Local summaries expose pre-run drift separately from post-run outcomes; no baseline is automatically promoted | PASS | The outcome regression checks the post-run summary classification and confirms the active baseline remains unchanged. |
| 9 | Expected-exception annotations remain non-waiving; external Verification remains read-only and does not qualify Recovery | PASS | Existing B.5 and workspace tests assert findings remain active under expected-exception annotations; C.3 changes do not modify those runtime contracts. |
| 10 | Assigned focused C.3 regressions and full authoring CLI regression class pass | PASS | Focused review/acknowledgement/outcome/blocking tests passed; the latest complete `AuthoringCliTests` class run passed 60 tests with 0 failures and 2 host-capability symlink skips. |

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
| 8 | Focused C.4 regressions and the full authoring CLI regression class pass | PASS | Both focused C.4/C.3 regression tests passed; the latest complete `AuthoringCliTests` class run passed 60 tests with 0 failures and 2 host-capability symlink skips. |

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
| 8 | Workspace derivation rejects source-configuration and destination symlink/reparse-point traversal | PASS | Both guards and regression tests passed on the symlink-capable Linux CI runner with no skips; this Windows host's two capability-based skips are separately recorded below. |
| 9 | C.5 regressions and the complete authoring CLI class pass | PASS | The latest `AuthoringCliTests` run passed 60 tests with 0 failures; both symlink-capability tests were skipped on this Windows host. |

## Validation evidence

| Validation | Result |
|---|---|
| Focused workspace/CLI regression suite | `dotnet test --project .\tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --no-restore --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests`: 60 passed, 0 failed, 2 skipped (source-configuration and destination symlink creation unavailable on this Windows host). SDK 10.0.403 was selected by temporarily changing the pinned SDK from 10.0.402; `global.json` was restored byte-for-byte. |
| Focused C.4 governance/offline comparison and C.3 summary outcome regressions | 2 passed, 0 failed, 0 skipped |
| C.3 review/acknowledgement/outcome/blocking regressions | 4 passed, 0 failed, 0 skipped |
| Configuration and Graph tests | Configuration 12 and Graph 30 passed; 0 failed and 0 skipped. |
| Evidence, Verification, Recovery, Engine, and Projection runtime tests | Evidence 5, Verification 53, Recovery 8, Engine 4, and ProjectionRuntime 15 passed; 0 failed and 0 skipped. |
| Applicable discovery/Evidence tests | New persisted-discovery plan/acknowledgement regression passed; three non-Docker `PhysicalDiscoveryTests` passed; Evidence package/baseline coverage passed in `AuthoringCliTests`. |
| Docker-backed PostgreSQL/SQL Server discovery tests | Not run successfully: Docker endpoint `npipe://./pipe/docker_engine` was unavailable. The full `PhysicalDiscoveryTests` class was attempted and those two tests failed at container startup. |
| CLI build | `dotnet build src\ProofShift.Cli\ProofShift.Cli.csproj --no-restore` using installed .NET SDK 10.0.403: passed, 0 warnings, 0 errors. |
| End-to-end test-project build | `dotnet build tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --no-restore` using installed .NET SDK 10.0.403: passed, 0 warnings, 0 errors. |
| C.5 authoring CLI tests | `dotnet test --project .\tests\ProofShift.EndToEnd.Tests\ProofShift.EndToEnd.Tests.csproj --no-restore --filter-class ProofShift.EndToEnd.Tests.AuthoringCliTests`: passed, 60 passed, 0 failed, 2 skipped (symlink creation unavailable). SDK 10.0.403 was selected by temporarily changing the pinned SDK from 10.0.402; `global.json` was restored byte-for-byte. |
| Documentation/link validation | Relative links in six product documentation files passed. `.github/copilot-instructions.md` retains pre-existing `docs/...` links that do not resolve relative to `.github`; no new links were added there. |
| Whitespace/error check | `git diff --check`: passed. |
| Exact-head package/CI evidence / owner acceptance | Candidate `5062a6a317c2bcd8b1c0d09e1d221635210568e4` passed push run `41169254780` and PR run `41169259744`; all 11 required jobs passed in both. `build-and-test` passed 362 tests with 0 failures and 0 skips. Explicit owner acceptance remains pending. |
| Persisted packaged-pilot governance identities | PR smoke artifact `13030972332`; candidate run `63dce8b0-1c5d-4be7-a798-98999ed0f032`, `QUALIFIED`, outcome `ChangedStillQualified`. Review ID `5aa4b6ed-d15c-43fb-815f-48368fd61389`, integrity SHA-256 `148a1bad7458d71cf49669d0c9f9d591c695228b25a38e4f0780e89aeacf695f`, drift identity `54e81ad1d61f128a09bd56a54dda1a9f7fe84552bead9defc4d12bb460e0d7f5`. Comparison index key/ID is the candidate run ID; record integrity SHA-256 `499a088ce1853b3393d735a6c3f4dca4c181f3258f80f7e887e66f1dad1ffb0d`, detailed-comparison identity fingerprint `85d466f4aded35b9a099e8ae614d1ae8ef67d22b0d53bc75cb356db68acbc86d`. |
| PR package reproducibility and platform-scoped smoke | PR packages were built from synthetic merge SHA `69e496ecafb602408e309e93340b95373dcef9b1`, whose tree equals candidate tree `312ddd47cc81f62be942e7b5342e08ceef010538`. Linux archive/content SHA-256: `d7dae91095d9bb2efb9ef8a5263ba187d7b403be1c5d18a799c7dca2c41005db` / `e544e76aa04412c30f56990b99478d6a88cd96498c952da015554782b75b26a7`. Windows `win-x64` package-only archive/content SHA-256: `1e95e20c22a2d866f3bc298f8ecdea54b15731edefc383b669ad720ac1422186` / `a076ad7d24a3cebbd62cb02986ad05f6bf0c6503c10e5b33646c06fd4ce47bce`. |

## C.6 final acceptance gates

| # | C.6 acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | C.1-C.5 source and documentation diff has a complete final review with no unrelated behavior changes | PASS | Final review found and fixed successful baseline-bound runs not persisting their comparison; the run now reloads its authoritative manifest before comparison, and the packaged workflow asserts the returned outcome. Same-fingerprint rediscovery now anchors historical selection to baseline promotion time; the regression covers unchanged rediscovery followed by real drift. No unrelated behavior changes were identified. |
| 2 | Focused authoring CLI regressions, including latest parser and symlink guard changes, build and pass | PASS | Latest complete `AuthoringCliTests` run: 60 passed, 0 failed, 2 host-capability symlink skips. |
| 3 | Packaged workflow scripts parse and `git diff --check` passes | PASS | PowerShell parsing passed for `scripts/run-guided-workspace-evaluation.ps1` and `scripts/smoke-evaluation-package.ps1`; `git diff --check` passed after the final lifecycle assertion. |
| 4 | Extracted Linux package creates real source/shadow discovery artifacts and a qualified packaged baseline | PASS | Linux packaged-Docker rehearsal passed in exact-head PR run `41169259744`; smoke artifact `13030972332` records candidate run `63dce8b0-1c5d-4be7-a798-98999ed0f032`, initial discovery, qualified execution, and explicit baseline promotion. |
| 5 | Physical schema change is detected as `ReviewRequired`, planning remains offline/read-only, and exact review binds the candidate | PASS | The exact-head packaged rehearsal detected the added physical field, required review, and executed the candidate against that baseline/review binding. |
| 6 | Candidate completes baseline-bound execution and remains `QUALIFIED`; external Verification remains read-only and does not qualify Recovery | PASS | The packaged candidate completed `QUALIFIED`; BYOM evidence confirmed read-only external Verification, with no Projection or Recovery qualification artifacts. |
| 7 | Offline package comparison validates baseline binding and reports `ChangedStillQualified` with discovery deltas | PASS | The extracted-package offline comparison returned `BaselineGoverned` / `ChangedStillQualified` and retained the physical discovery delta. Comparison index key/ID: candidate run `63dce8b0-1c5d-4be7-a798-98999ed0f032`; record integrity SHA-256 `499a088ce1853b3393d735a6c3f4dca4c181f3258f80f7e887e66f1dad1ffb0d`; detailed-comparison identity fingerprint `85d466f4aded35b9a099e8ae614d1ae8ef67d22b0d53bc75cb356db68acbc86d`. |
| 8 | Candidate re-baselining preserves history, is idempotent when repeated, and rejects historical re-promotion | PASS | The exact-head Linux rehearsal retained the old baseline, promoted the candidate, reported `AlreadyActive` on repeat, and rejected historical re-promotion. |
| 9 | Extracted-package workspace derivation reuses only configuration with a new identity and no copied runtime/data state | PASS | The exact-head Linux rehearsal validated a new workspace identity, byte-identical project configuration, and empty runtime/history/data paths. |
| 10 | Source and destination symlink tests pass on a symlink-capable runner with no skip | PASS | Both symlink/reparse tests passed on the exact-head Linux CI runner; the Windows local capability skips are documented separately. |
| 11 | Linux and Windows packages build reproducibly; Windows remains package-only and Linux claims remain packaged-Docker scoped | PASS | Exact-head push/PR CI passed Linux and Windows package A/B reproducibility, Linux packaged Docker demos, and Windows package-only smoke. Platform claims remain scoped accordingly. |
| 12 | Full required tests and protected CI pass on the exact candidate; candidate SHA/tree match PR head | PASS | Candidate `5062a6a317c2bcd8b1c0d09e1d221635210568e4` (tree `312ddd47cc81f62be942e7b5342e08ceef010538`) passed push run `41169254780` and PR run `41169259744`, 11/11 jobs each. The PR synthetic merge SHA `69e496ecafb602408e309e93340b95373dcef9b1` has the identical tree. `build-and-test`: 362 passed, 0 failed, 0 skipped. |
| 13 | Final docs and acceptance evidence reconcile technical results and scope; owner decision remains separate | PASS | This matrix and linked status documents record the completed technical evidence and preserve owner acceptance as a separate pending decision. No merge or PS-1.0C acceptance is claimed. |

## Scope boundary

This assigned pass implements C.1 baseline promotion/validation/history,
C.2 read-only semantic-drift planning, C.3 baseline-aware
execution/review/outcome comparison, C.4 portable governance packages and
offline comparison, and C.5 rebuildable run history, explicit
baseline progression, safe configuration-only workspace reuse, and assigned
C.6 final validation. The matrix records evidence, not PS-1.0C acceptance.
The technical workflow evidence above does not constitute PS-1.0C acceptance.
The exact-head CI and documentation reconciliation are complete. Explicit
owner acceptance remains separate and pending; no merge, production
migration, or production rollback is implied.
