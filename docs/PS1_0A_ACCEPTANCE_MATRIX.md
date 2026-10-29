# PS-1.0A Acceptance Matrix

Status: **In Progress — not owner accepted.** This is an exact-candidate
technical review, not owner acceptance or merge authorization.

## Candidate identity and validation

- Baseline candidate reviewed before the current gate-closure work:
  `ad37b331795df2ec7073e1a6e5dc12bf63ce9b5f`
- Branch: `ps-1.0a-customer-data-evaluation`
- Parent: `d041f28c6597e6be0fd3f483d69bacf9f795f0a4`
- Tree: `de597d4333802e7284516c9cd7dc85eb5f4a4754`
- Accepted PS-0.11G base (`origin/main`): `a095c21588cbd1608b8a543cdfaab71312b596d8`, an ancestor of the candidate
- CI: [run 40786241059](https://github.com/banderson736/ProofShift/actions/runs/40786241059), workflow `.github/workflows/ci.yml`, run 206
- Exact-candidate check: CI `head_sha` is `ad37b331795df2ec7073e1a6e5dc12bf63ce9b5f`
- CI result: all 11 jobs succeeded; build-and-test reported 317 passed, 0 failed, 0 skipped
- Focused local contract test: `ByomFixtureContractTests` passed 2/2; PowerShell parse, exact failed-rule ID validation, and `git diff --check` passed
- Linux package: `ProofShift-Evaluation-0.1.0-preview.1-linux-x64.zip`
  - SHA-256: `e63f99028011cf10e55040aec239478e44df66dd23fb640604d4311b09851442`
  - Content SHA-256: `b56cae80620df639ed539b69f7c0c72f779d0b3f0af85e1dde09e509334a4b7c`
- Windows package: `ProofShift-Evaluation-0.1.0-preview.1-win-x64.zip`
  - SHA-256: `e79b7a4832b12441779d009188f9052c8e8e54bcf6d29c61b753b924deff4e65`
  - Content SHA-256: `caa4bc85b6f4efc9ecf5e1f2239c084577da07590ec3dc36abfed5a990a2599f`
- Reproducibility: two independent Linux builds and two independent Windows
  builds matched their respective archive and content hashes. The
  cross-platform `package-reproducibility` job passed.
- Package execution: extracted Linux package smoke passed five packaged demos,
  both BYOM workflows, and documented-command checks. Windows package-only
  smoke passed; it did not claim scenario execution. Linux runtime-only smoke
  passed.

The candidate, CI, and package evidence above describe the baseline only. The
gate-closure evidence below applies to follow-up changes; final acceptance
requires a new exact-SHA CI run and package validation. Local validation of
the follow-up work:

- `dotnet build ProofShift.sln --no-restore` passed with 0 warnings and 0 errors.
- `AuthoringCliTests`: 23 passed; `SnapshotStoreTests`: 11 passed; the
  non-relational predicate contract test: 1 passed.
- Domain (11), Graph (30), Engine (4), Verification (53), Evidence (5),
  Recovery (8), and Configuration (12) test projects passed.
- The new PostgreSQL predicate matrix was skipped because this workstation
  cannot connect to Docker at `npipe://./pipe/docker_engine`. In the full
  EndToEnd project run, 160 passed, 15 skipped, and 5 container-backed tests
  failed to connect to that unavailable endpoint. No application assertion
  failure was reported by those five tests.
- Package-script parsing and `git diff --check` passed. Full package/Docker
  smoke and exact-candidate CI remain pending.

## Acceptance gates

| # | Acceptance gate | Status | Exact-candidate evidence / remaining work |
|---|---|---|---|
| 1 | PS-0.11G baseline and branch identity are preserved | PASS | Candidate branch and full SHA are recorded above. `origin/main` is the accepted PS-0.11G base and is an ancestor of the exact candidate. CI verified that it ran on the full candidate SHA. |
| 2 | Scope declarations are deterministic, validated, and included in graph/selector identity without changing legacy unscoped canonical bytes | PASS | CI build-and-test passed the 30-test Graph suite, including `EvaluationScopesAreCanonicalAndChangeTheGraphIdentity`, malformed/unknown-property rejection, canonical ordering, and the legacy unscoped v1 version assertion. Review of `GraphCanonicalizer` confirms unscoped graphs retain the v1 serializer path; scoped graphs use the v2 identity. |
| 3 | Supported relational predicates are typed, parameterized, deterministic, and fail closed for unsupported values/operators | NOT YET | Added `PostgreSqlEvaluationScopePredicatesMatchTypesNullsAndRejectInvalidInputs` with exact results for all seven advertised operators, null behavior, malformed/incompatible values, missing fields, and invalid null-check arguments. Local execution was skipped because Docker is unavailable; this must pass on the new exact-SHA CI candidate before the gate can pass. |
| 4 | Unsupported non-relational connectors reject predicate scopes without broad-read fallback | PASS | `NonRelationalSourcesRejectEveryPredicateBeforeReading` passed. It covers all 10 registered non-relational source connectors across CSV, filesystem, four structured formats, S3, Azure Blob, SFTP, and Parquet; all seven operators plus matching/mismatching values, missing fields, null checks, malformed values, invalid types, and incompatible range types fail with `PSCONN035` before any record is emitted. |
| 5 | Scope identity survives checkpoint serialization/replay and mismatched scopes are rejected | PASS | `HistoricalUnscopedManifestLoadsAndReplaysWithoutMutation` independently serializes the pre-scope v2 wire schema, verifies its integrity hash, replays unchanged artifact/provenance twice, rejects a changed scope and corrupted hash, and confirms the historical manifest bytes remain unchanged. Current-scope persistence remains covered; `SnapshotStoreTests` passed 11/11. |
| 6 | External Verification and persisted reports show scope identity/count and the outside-scope claim boundary, with Recovery NOT ASSESSED and Qualification NOT AVAILABLE | PASS | The extracted Linux package's external read-only BYOM workflow passed. Its runner validates full-declared-source scope/counts, persisted report boundary text, and the Recovery/Qualification boundary; the package smoke reported `BYOM external read-only evaluation: PASS`. |
| 7 | Mapping authoring supports explicit identity/attribute roles, validates deterministic pipelines, and retains ambiguity/unmapped review states | PASS | Exact-candidate build-and-test passed the authoring CLI coverage (including reviewed mapping import, strict validation, role/pipeline checks, and review states); extracted-package validation and smoke passed. |
| 8 | Supported field fan-out/topology has exact source/target disposition and lineage assertions; merge is not implicitly executed | PASS | `MappingImportEmitsExplicitFieldFanOutEdgesWithoutImplicitMerge` now runs mapping import through checkpoint capture/replay, shadow projection, and Verification. It asserts two one-source/one-target transform edges, distinct target IDs, exact per-edge provenance, one transformed source disposition, two target lineages, and persisted failing Evidence tied to the defective target and edge. `AuthoringCliTests` passed 23/23. |
| 9 | Expected exceptions have exact scope-bound matching, owner/reason metadata, and auditable evidence without waiving failures | PASS | Verification configuration tests passed, including same-scope matching and cross-scope non-matching. Packaged evidence linked `ExpectedExceptionAnnotated` to the failed `budget-attributes` finding, retained its `Fail` result, and left the defective evaluation failed / NOT QUALIFIED. |
| 10 | Generic reconciliation detects exact aggregate-total match, mismatch, invalid/null measures, and overflow | PASS | Packaged shadow Evidence asserted an exact `12,780` pass and `12,781` failure. `GenericAggregateTotalReconcilesExactValuesAndRejectsInvalidMeasures` passed exact match, mismatch, null, non-numeric, and decimal-overflow assertions using generic Verification rules. |
| 11 | Synthetic packaged customer-shaped evaluation proves the declared-scope claim boundary and exact seeded outcomes | PASS | The extracted Linux package runner checkpointed 114 synthetic shadow-source records into an isolated PostgreSQL shadow workflow. The clean dry-run passed with 12 budget rows totaling `12,780.00` and outcome `QUALIFIED`. The defective workflow totaled `12,781.00`, failed Verification, and emitted the exact distinct failed-rule set recorded below. |
| 12 | Customer runbook and data-preparation, generalized mapping, and reconciliation guides are accurate and linked | PASS | Added the customer-facing [source/connector compatibility table](SOURCE_CONNECTOR_COMPATIBILITY.md), linked it from evaluator and customer guides, documented conservative cleanup and retention steps, and added package inclusion and smoke-contract checks. PowerShell parsing and documentation/link/contract checks passed; the packaged Docker smoke remains pending exact-candidate CI. |
| 13 | Existing PS-0.11 behavior, reports, demos, package contents, and protected checks remain compatible | NOT YET | The new follow-up changes have not yet run through the protected CI and package jobs. Baseline compatibility evidence remains as recorded above; the full EndToEnd local run was blocked by the unavailable Docker endpoint. |
| 14 | Architecture decision and repository assignment/status documentation are complete and consistent | PASS | ADR-0036 records the scope design and preserves the unscoped v1 compatibility boundary. The scope/runbook/BYOM documentation is consistent with the accepted read-only external Verification and no-merge/no-production-execution boundaries. This matrix keeps PS-1.0A In Progress and not owner accepted. |
| 15 | Required CI and platform/package checks pass on the exact candidate | NOT YET | The 11-job result above belongs to baseline `ad37b331795df2ec7073e1a6e5dc12bf63ce9b5f`. A new candidate containing the gate-closure changes must be pushed and pass every required check; no new candidate SHA or run exists yet. |

### Packaged BYOM result details

The successful `linux-package-demos` job ran the packaged evaluator outside the
repository checkout. It validated discovery, reviewed mapping import, strict
configuration validation, checkpoint capture, shadow dry-run/projection,
Verification, Evidence, reconciliation, and the independent external
read-only Verification workflow.

The shadow workflow captured 114 synthetic source artifacts and used separate
`byom_shadow` source/target databases and run-isolated PostgreSQL schemas. Its
clean run passed with 12 budget rows totaling `12,780.00` and disposition
`QUALIFIED`. Its defective run changed only `B001`, totaled `12,781.00`, and
remained failed / `NOT QUALIFIED`.

The defective Evidence contained these **three distinct failed rule IDs**:

1. `budget-attributes` — expected/actual mapped value hashes differ. The
   scope-bound expected exception matched and linked to this finding; the
   finding remained failed.
2. `budget-total` — expected `12780`, actual `12781`; reconciliation failed.
3. `proofshift.verification.materialized-state` — `MaterializedTargetChanged`
   detected the post-projection physical target mutation (both fingerprints
   represented 114 records).

The runner asserts the exact distinct ID set, not just a count. The
`VerificationSummary` Evidence record reuses the materialized-state rule ID
and is not a fourth distinct failed rule.

The external read-only workflow retained the original three-table, 102-record
`byom` surface. Assertions confirmed that shadow writes did not change the
external source/target state, external discovery did not include the shadow
budget table, and external target observation used a read-only role. The
shadow workflow used separate four-table `byom_shadow` databases.

Qualified Evidence included source disposition, target lineage, mapped
attribute comparison, and aggregate comparison. The defective attribute
finding carried source/target artifact and graph references plus an edge
reference; its exception annotation referenced the original Evidence ID.
Materialized-state Evidence referenced the checkpoint and projection. The
runner also verified repeatable fingerprints and retained the failed outcome.

The packaged workflow and smoke result prove exact aggregate match/mismatch,
the expected-exception non-waiver, and the fixture isolation described above.
They do not prove merge execution, production migration/rollback, or
completeness outside the declared evaluation scope.

## Accepted-behavior compatibility review

| Accepted behavior | Exact-candidate evidence | Result |
|---|---|---|
| Unscoped graphs retain canonical v1 behavior | Graph suite passed, including v1 version, semantic equivalence, canonical ordering, and deterministic hash tests; unscoped serializer path is unchanged. | PASS |
| Checkpoints remain immutable, integrity-checked, and replayable only under matching selectors; incomplete checkpoints cannot replay | Snapshot suite passed 11/11, including the independent pre-scope v2 manifest replay, hash corruption rejection, selector mismatch rejection, and unchanged manifest bytes. | PASS |
| Checkpoint consistency truthfully records strategy/downgrade; no cross-system atomicity claim | Snapshot consistency regression passed; scope metadata is endpoint-bound and does not assert cross-system atomicity. | PASS |
| Normalized value-kind fidelity and deterministic projection fingerprints remain stable | Projection runtime regressions passed in build-and-test; packaged shadow checks compared projection/read-back and repeated evidence fingerprints. | PASS |
| Shadow projection rejects unsafe targets/chained inputs and preserves deterministic transformation, journal, duplicate, exclusion, and cleanup behavior | Projection runtime regressions and extracted Linux Docker-backed package workflows passed. | PASS |
| Recovery qualification remains separate from observation and fails closed on incomplete/cancelled preparation | Dry-run and projection failure regressions passed; packaged external result states Recovery NOT ASSESSED and Qualification NOT AVAILABLE. | PASS |
| Previously accepted Pension assurance outcomes remain stable | Exact-candidate build-and-test and all five packaged domain demos passed; no Pension-specific aggregate semantics were introduced. | PASS |
| External target Verification remains read-only and does not imply Recovery qualification | Packaged BYOM external workflow passed with SELECT-only observation and unchanged target state; Windows smoke was package-only and makes no execution claim. | PASS |
| Fan-out topology preserves exact end-to-end disposition and lineage | The checkpoint-backed mapping/projection/Verification test asserts exact disposition, per-target lineage, Evidence graph links, and persisted ledger rows. | PASS |

## Documentation review

The customer runbook now covers cleanup and retention for generated workspaces,
checkpoints and Evidence, package outputs, PostgreSQL shadow schemas,
filesystem Projection outputs, and Docker fixtures. The canonical compatibility
table lists registered source connectors, implemented capabilities, predicate
limitations, and prerequisites, and is included in the package allowlist with
smoke assertions. Package execution on the new exact candidate remains pending.

## Roll-up and recommendation

| Status | Count |
|---|---:|
| PASS | 12 |
| FAIL | 0 |
| NOT YET | 3 |
| NOT APPLICABLE | 0 |

Blocking gates: **3** (new provider-backed predicate contract has not run
locally or in candidate CI), **13** (new-candidate compatibility checks), and
**15** (required CI and package checks on the new exact SHA).

**Recommendation: `PS-1.0A NOT READY FOR OWNER ACCEPTANCE`.** Resolve the
listed gates and review a new exact candidate before owner acceptance. This
technical pilot does not authorize production migration or rollback. Do not
merge, publish, tag, or begin PS-1.0B without the owner's explicit direction.
