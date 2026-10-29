# PS-1.0A Acceptance Matrix

Status: **Accepted (October 29, 2026), by explicit repository-owner
acceptance.** The accepted implementation candidate is permanently
`860d4d1a96c0eb14a4da07edb07584874e0e4b2d`. This administrative closure
record does not amend or redefine that candidate.

## Candidate identity and validation

- Exact candidate: `860d4d1a96c0eb14a4da07edb07584874e0e4b2d`
- Candidate parent: `eb1973c6529feae16812ac3cfaf24c548b481f84`
- Candidate tree: `fde95d162f075badb369f5fc5c1d0c957873f04d`
- Branch: `ps-1.0a-customer-data-evaluation`
- Pull request: [#20](https://github.com/banderson736/ProofShift/pull/20),
  merged to `main` as `7b836f699933737cca5986afc3e22160bc48665e`.
- Owner decision: explicitly accepted on October 29, 2026.
- Administrative closure update: documentation-only; it is separate from the
  accepted implementation candidate and its exact-SHA technical validation.
- Accepted PS-0.11G base (`origin/main`):
  `a095c21588cbd1608b8a543cdfaab71312b596d8`; it is an ancestor of this
  candidate.
- Push CI: [run 40794959644](https://github.com/banderson736/ProofShift/actions/runs/40794959644),
  run 208. PR CI: [run 40794964414](https://github.com/banderson736/ProofShift/actions/runs/40794964414),
  run 209. Both runs' `head_sha` is exactly
  `860d4d1a96c0eb14a4da07edb07584874e0e4b2d`; all 11 jobs succeeded in both.
- Push `build-and-test`: 320 passed, 0 failed, 0 skipped across nine test
  assemblies. The acceptance-specific integration jobs additionally passed:
  remote storage 54/0/0, Db2 2/0/0, and Oracle 2/0/0.
- Linux package:
  `ProofShift-Evaluation-0.1.0-preview.1-linux-x64.zip`
  - Archive SHA-256: `4ae7c0e570b0e9ff95901fc0503ad2e8e8bd071f10141e68f8f0dcebd3e5434d`
  - Content SHA-256: `e57993bfee400df65e8980743b9352e6d714f2432f2b4f464f97492c2e4c4f25`
- Windows package:
  `ProofShift-Evaluation-0.1.0-preview.1-win-x64.zip`
  - Archive SHA-256: `81badc3a1aed3fbffca5dac140fa303e1e473008bb421e23b4841a6f6b3fa61b`
  - Content SHA-256: `88110f039ab1f72673f14fe1953791cfbc45ce58f2326b679e382382946b0d6e`
- Package reproducibility: the independent Linux and Windows archive and
  content hashes matched in both builds; `package-reproducibility` passed.
- Package execution: `linux-package-demos` passed all five packaged demos,
  clean and defective shadow BYOM, external read-only BYOM, and packaged
  documentation/command checks. `windows-package-smoke` passed package-only
  integrity, front-door, docs, and preflight checks; no Windows scenario
  execution is claimed. `linux-runtime-only-smoke` passed.
- Earlier local Docker unavailability is not a remaining evidence gap:
  exact-candidate CI ran the full EndToEnd assembly with 180 passed, 0 failed,
  and 0 skipped, including the PostgreSQL Testcontainers predicate test.

## Acceptance gates

| # | Acceptance gate | Status | Exact-candidate evidence / remaining work |
|---|---|---|---|
| 1 | PS-0.11G baseline and branch identity are preserved | PASS | The exact candidate, parent, tree, branch, and accepted `origin/main` base are recorded above. `origin/main` is an ancestor of the candidate, and both CI runs verified the full candidate SHA. |
| 2 | Scope declarations are deterministic, validated, and included in graph/selector identity without changing legacy unscoped canonical bytes | PASS | CI build-and-test passed the 30-test Graph suite, including `EvaluationScopesAreCanonicalAndChangeTheGraphIdentity`, malformed/unknown-property rejection, canonical ordering, and the legacy unscoped v1 version assertion. Review of `GraphCanonicalizer` confirms unscoped graphs retain the v1 serializer path; scoped graphs use the v2 identity. |
| 3 | Supported relational predicates are typed, parameterized, deterministic, and fail closed for unsupported values/operators | PASS | Exact-candidate push and PR `build-and-test` ran `PostgreSqlEvaluationScopePredicatesMatchTypesNullsAndRejectInvalidInputs` in the 180-test EndToEnd assembly (0 failed, 0 skipped). The test starts a PostgreSQL Testcontainers provider and covers all seven advertised operators with matching/nonmatching values, database null semantics, malformed/incompatible values, missing fields, reversed ranges, and invalid null-check arguments. |
| 4 | Unsupported non-relational connectors reject predicate scopes without broad-read fallback | PASS | Exact-candidate EndToEnd CI ran `NonRelationalSourcesRejectEveryPredicateBeforeReading` for all 10 registered non-relational sources: CSV, filesystem, JSON, NDJSON, XML, fixed-width, S3, Azure Blob, SFTP, and Parquet. The contract applies all seven operators, valid match/mismatch cases, missing fields, null checks, malformed/invalid typed values, and incompatible range inputs; each deterministically fails with `PSCONN035` before any record is emitted. |
| 5 | Scope identity survives checkpoint serialization/replay and mismatched scopes are rejected | PASS | Exact-candidate EndToEnd CI ran `HistoricalUnscopedManifestLoadsAndReplaysWithoutMutation`. Its independently serialized pre-scope v2 manifest validates the integrity hash, preserves missing scope metadata and historical bytes, replays artifact identity/provenance twice, rejects a scoped-selector mismatch and corrupted hash, and remains compatible with current scoped checkpoints. |
| 6 | External Verification and persisted reports show scope identity/count and the outside-scope claim boundary, with Recovery NOT ASSESSED and Qualification NOT AVAILABLE | PASS | Exact-candidate `linux-package-demos` passed the external read-only workflow and its persisted-report assertions for scope/count and claim boundary. The original three-table, 102-record `byom` surface remained unchanged; shadow budget objects were excluded, target observation stayed read-only, and Recovery/Qualification remained NOT ASSESSED / NOT AVAILABLE. |
| 7 | Mapping authoring supports explicit identity/attribute roles, validates deterministic pipelines, and retains ambiguity/unmapped review states | PASS | Exact-candidate `build-and-test` passed the authoring coverage for reviewed mapping import, strict validation, explicit roles/pipelines, and review states. The packaged Linux workflow also passed mapping import and strict validation. |
| 8 | Supported field fan-out/topology has exact source/target disposition and lineage assertions; merge is not implicitly executed | PASS | Exact-candidate EndToEnd CI ran `MappingImportEmitsExplicitFieldFanOutEdgesWithoutImplicitMerge` as part of the 180/0/0 assembly. The checkpoint-backed mapping/projection/Verification scenario asserts two separate one-source/one-target edges, distinct target identities, per-edge provenance, source disposition, target lineages, and persisted failing Evidence linked to the defective target and edge; it does not execute an implicit merge. |
| 9 | Expected exceptions have exact scope-bound matching, owner/reason metadata, and auditable evidence without waiving failures | PASS | Verification configuration tests passed, including same-scope matching and cross-scope non-matching. Packaged evidence linked `ExpectedExceptionAnnotated` to the failed `budget-attributes` finding, retained its `Fail` result, and left the defective evaluation failed / NOT QUALIFIED. |
| 10 | Generic reconciliation detects exact aggregate-total match, mismatch, invalid/null measures, and overflow | PASS | Packaged shadow Evidence asserted an exact `12,780` pass and `12,781` failure. `GenericAggregateTotalReconcilesExactValuesAndRejectsInvalidMeasures` passed exact match, mismatch, null, non-numeric, and decimal-overflow assertions using generic Verification rules. |
| 11 | Synthetic packaged customer-shaped evaluation proves the declared-scope claim boundary and exact seeded outcomes | PASS | The extracted Linux package runner checkpointed 114 synthetic shadow-source records into an isolated PostgreSQL shadow workflow. The clean dry-run passed with 12 budget rows totaling `12,780.00` and outcome `QUALIFIED`. The defective workflow totaled `12,781.00`, failed Verification, and emitted the exact distinct failed-rule set recorded below. |
| 12 | Customer runbook and data-preparation, generalized mapping, and reconciliation guides are accurate and linked | PASS | The exact-candidate Linux package smoke passed the packaged documentation/link/command checks, including `SOURCE_CONNECTOR_COMPATIBILITY.md`. The customer runbook provides reviewed cleanup steps for exact PostgreSQL shadow schemas, filesystem Projection outputs, generated workspace/checkpoint/Evidence/scratch/package paths, and synthetic Docker fixtures; retention distinguishes generated evaluation outputs from customer source data. |
| 13 | Existing PS-0.11 behavior, reports, demos, package contents, and protected checks remain compatible | PASS | Both exact-SHA CI runs passed all 11 protected jobs. Build-and-test passed 320/0/0; remote storage passed 54/0/0, Db2 2/0/0, and Oracle 2/0/0. Linux package demos, Windows package-only smoke, Linux runtime-only smoke, and cross-platform package reproducibility all passed. |
| 14 | Architecture decision and repository assignment/status documentation are complete and consistent | PASS | ADR-0036 records the scope design and preserves the unscoped v1 compatibility boundary. The scope/runbook/BYOM documentation is consistent with accepted read-only external Verification and no-production-execution boundaries. This matrix records explicit owner acceptance of the exact candidate and distinguishes this administrative closure update from the validated implementation candidate. |
| 15 | Required CI and platform/package checks pass on the exact candidate | PASS | Push run 40794959644 and PR run 40794964414 both completed successfully on exact SHA `860d4d1a96c0eb14a4da07edb07584874e0e4b2d`. All 11 jobs passed, including build-and-test, dependency review, Linux/Windows package reproducibility, Linux packaged demos, Windows package smoke, Linux runtime-only smoke, Db2, Oracle, and remote-storage integration. |

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

The exact-candidate `linux-package-demos` smoke passed the packaged workflow,
including exact aggregate match/mismatch, expected-exception non-waiver, and
fixture-isolation assertions. It does not prove merge execution, production
migration/rollback, or completeness outside the declared evaluation scope.

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

The customer runbook covers cleanup and retention for generated workspaces,
checkpoints and Evidence, package outputs, PostgreSQL shadow schemas,
filesystem Projection outputs, and Docker fixtures. The canonical compatibility
table lists registered source connectors, implemented capabilities, predicate
limitations, and prerequisites, and is included in the package allowlist.
Exact-candidate package smoke passed the packaged documentation, link, and
command assertions. Windows evidence remains package-only; Linux scenario
evidence is limited to the packaged Docker evaluation.

## Roll-up and recommendation

| Status | Count |
|---|---:|
| PASS | 15 |
| FAIL | 0 |
| NOT YET | 0 |
| NOT APPLICABLE | 0 |

Owner acceptance is recorded for the exact implementation candidate above.
All technical acceptance evidence remains tied to that candidate; the
administrative closure record does not create a new product candidate or
authorize production migration or rollback.

**Final decision: `PS-1.0A ACCEPTED`.** Acceptance matrix totals:
15 PASS, 0 FAIL, 0 NOT YET, 0 NOT APPLICABLE. PS-1.0A acceptance does not
authorize production migration or rollback. Windows evidence remains
package-only; Linux scenario evidence is limited to packaged Docker
evaluation; external Verification remains read-only and does not qualify
Recovery; expected-exception annotations never waive findings.
