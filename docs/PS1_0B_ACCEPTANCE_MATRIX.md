# PS-1.0B Acceptance Matrix

Status: **In Progress — technical evidence complete on the implementation
baseline; owner acceptance has not been given.** This matrix reconciles the
completed technical review with its supporting packaged-customer and CI
evidence. The implementation baseline is
`bc985901fcf6700c532d04b2d6c2ce64079800a5` (tree
`2bd9d69bac136a511a5c986a23c12ebf73afad35`). This administrative
acceptance-record update does not change product behavior or redefine that
validated implementation baseline. The closure-record PR head must also pass
its own protected CI before a technical recommendation is issued.

| # | Acceptance gate | Status | Evidence |
|---:|---|---|---|
| 1 | Initialize a fresh workspace from the packaged product | PASS | Exact-candidate PR run `40987621659`, `linux-package-demos`: the extracted Linux package ran `scripts/run-guided-workspace-evaluation.ps1`, initialized a fresh workspace, copied the packaged scenario, and completed workspace validation. |
| 2 | Incorporate discovery without repository-only dependencies | PASS | Packaged BYOM evaluation in Linux job `123845754438` used packaged discovery artifacts and the packaged CLI; the workspace workflow completed without repository-only dependencies. |
| 3 | Retain explicit mapping review requirements | PASS | Packaged BYOM workflow in job `123845754438` exercised reviewed mappings and the packaged scaffold/review path before evaluation. |
| 4 | Read-only preflight blocks invalid or unsupported evaluations | PASS | Packaged guided-workspace assertions verified default preflight is offline and blocked without declared credentials, explicit `--connect` reported READY for reachable providers, and an unavailable endpoint did not report READY. |
| 5 | Effective plan describes evaluation scope and boundaries | PASS | Packaged plan assertions verified a deterministic SHA-256 plan fingerprint and that changing expected-exception identity changes that fingerprint; plan inputs include graph, mappings, capabilities, rules, Recovery/checkpoint/replay, outputs, and exclusions. |
| 6 | Clean packaged guided evaluation completes as `QUALIFIED` | PASS | Linux packaged customer evaluation: 114 source records; 12 projected budget rows totaling `12,780.00`; completed disposition `QUALIFIED`. |
| 7 | Defective packaged guided evaluation completes as `NOT QUALIFIED` | PASS | Linux packaged customer evaluation mutated only the isolated shadow schema after Projection; total `12,781.00`; completed disposition `NOT QUALIFIED`. |
| 8 | Expected exceptions remain non-waiving | PASS | Packaged Evidence assertion matched the expected-exception annotation to the `budget-attributes` failed finding and confirmed the failure remained. |
| 9 | Replay works without a live source | PASS | The packaged workflow stopped SQL Server, verified its port was unreachable, removed the source connection reference, and completed replay from the original checkpoint with checkpoint ancestry intact. The implementation avoids resolving source credentials when no source/shadow overlap check needs them. |
| 10 | Run manifest binds evaluation identities | PASS | Packaged clean/replay runs validated persisted manifests and their workspace, plan, configuration, graph, discovery, checkpoint, Projection, Verification, reconciliation, Recovery, Evidence, and disposition bindings; replay used new run/Projection IDs and the original checkpoint ID. |
| 11 | Portable evidence package is produced | PASS | Packaged workflow created and offline-validated clean, replay, defective, and incomplete diagnostic packages; package identity matched validation output. |
| 12 | Package validation detects tampering and mismatches | PASS | Packaged tamper assertions required invalid results for modified/deleted Evidence, modified summary, substituted run manifest, and modified package manifest. Offline validation also checked artifact integrity, roles, paths, and references. |
| 13 | Customer summary agrees with machine Evidence | PASS | Packaged summaries were generated for clean, defective, and incomplete runs; summary/package validation bound summary output to run identity and Evidence, with incomplete disposition reported as not established. |
| 14 | Run comparison identifies meaningful changes | PASS | Packaged comparison assertions distinguished fresh-capture versus replay mode without reporting changed disposition, configuration, plan, population, or findings; clean-to-defective comparison identified added findings and materialized-state change. |
| 15 | Interrupted/infrastructure-failed execution cannot fabricate a disposition | PASS | Packaged workflow ran with an unavailable source, asserted no final disposition, checked the incomplete summary, and validated a diagnostic package with no disposition. |
| 16 | Cleanup is safe, explicit, and run-scoped | PASS | Packaged cleanup assertions verified preview-first behavior, removal limited to marker-owned run temporary data, retained Evidence/summary/package and configured shadow schema, idempotency, and refusal to delete unowned data. |
| 17 | Credentials and secrets do not appear in packages, summaries, effective configuration output, or logs | PASS | Packaged workflow scanned command outputs and expanded clean/defective packages for generated fixture credentials; no credential match was permitted. The Linux package smoke also ran its packaged secret-scanning checks. |
| 18 | Workspace capability reporting uses provider-owned descriptors | PASS | Exact-candidate `build-and-test` and connected packaged preflight exercised provider-backed capability/schema reporting through the packaged workspace tooling; the read-only preflight reached READY against the fixture providers. |
| 19 | PS-1.0A invariants and exact clean/defective results remain green | PASS | Exact-candidate regression suite passed. The packaged evaluation preserved clean/defective outcomes, exact findings, expected-exception non-waiver, materialized-state detection, shadow isolation, checkpoint replay, and provenance. |
| 20 | Extracted Linux package demonstrates the full Docker-backed workflow | PASS | Exact-candidate PR run `40987621659`, `linux-package-demos` job `123845754438`: fresh Linux package smoke passed five scenarios and the packaged BYOM clean/defective, replay, evidence/tamper, comparison, cleanup, incomplete-run, and credential checks passed. Claim is limited to this packaged Docker evaluation. |
| 21 | Windows package smoke passes within its evidence scope | PASS | Exact-candidate PR run `40987621659`, `windows-package-smoke` job `123845754421`: extracted package smoke passed; 0 customer scenarios ran. This is package-only evidence. |
| 22 | Exact-candidate remote CI passes all protected checks | PASS | Implementation baseline `bc985901fcf6700c532d04b2d6c2ce64079800a5`: push run `40987616414` and PR run `40987621659` both passed all 11 required jobs. `build-and-test`: 347 passed, 0 failed, 0 skipped; Remote Storage 54/0/0, Db2 2/0/0, Oracle 2/0/0. Closure-record candidate requires separate exact-head CI before final recommendation. |

## Technical evidence identity and outcomes

The technical evidence above is for implementation baseline
`bc985901fcf6700c532d04b2d6c2ce64079800a5`, parent
`dca0a0ae16c63a42de82cf5dab1c1a15ae4afee9`, tree
`2bd9d69bac136a511a5c986a23c12ebf73afad35`. It is not owner acceptance.
The later documentation-only closure commit is an administrative record and
must not be described as the implementation candidate validated by those
original runs.

| Evidence | Result |
|---|---|
| Acceptance gates on the validated implementation baseline | 22 PASS, 0 FAIL, 0 NOT YET, 0 NOT APPLICABLE |
| Push CI | Run `40987616414`; all 11 required jobs passed |
| PR CI | Run `40987621659`; all 11 required jobs passed |
| `build-and-test` | 347 passed, 0 failed, 0 skipped |
| Remote Storage integration | 54 passed, 0 failed, 0 skipped |
| Db2 integration | 2 passed, 0 failed, 0 skipped |
| Oracle integration | 2 passed, 0 failed, 0 skipped |
| Clean packaged customer scenario | 114 source records; 12 budget rows; `12,780.00`; `QUALIFIED` |
| Defective packaged customer scenario | `12,781.00`; `NOT QUALIFIED`; failed rules `budget-attributes`, `budget-total`, `proofshift.verification.materialized-state` |
| Expected-exception behavior | Annotation matched `budget-attributes`; finding remained failed |
| Materialized-state behavior | Out-of-band target mutation detected as `MaterializedTargetChanged`; reconciliation remained failed |
| Linux package | `ProofShift-Evaluation-0.1.0-preview.1-linux-x64.zip`; archive SHA-256 `dfc8d6258b6a609e10ea534230fd016e026cc19cf5aec865aaebc865fef50394`; content SHA-256 `eaab04e0a2eb5b75f7a40cfd639535d2110faaddc27a3af04781fee300933bae` |
| Windows package | `ProofShift-Evaluation-0.1.0-preview.1-win-x64.zip`; archive SHA-256 `b90f330bc17c1d6faebde767757c8f1e874dc9227c9a2f19b8348b45028f4df2`; content SHA-256 `38dfcca70f0ecd7bf6fec7aca600f4bfdafa6597d7e10f395685d37b36c26cfb` |
| Package reproducibility | Independent same-platform builds reproduced each platform's archive and content hashes in the exact-candidate CI runs. |

## B.1–B.6 workflow coverage

The packaged workflow validates workspace initialization, relative-path
containment and structural/offline validation; discovery and reviewed
mappings; offline-default and explicit read-only connected preflight; and an
effective plan whose fingerprint responds to expected-exception identity.
Guided runs persist isolated run identities and manifest bindings for
checkpoint, Projection, Verification, reconciliation, Recovery, Evidence,
and disposition. Fresh capture and checkpoint replay produce distinct runs.
Replay completed while the source was stopped/unreachable and its source
credential reference was unavailable.

Clean, defective, replay, and diagnostic packages were produced and
validated offline. Tamper checks cover Evidence modification/removal,
summary modification, run substitution, and package-manifest modification.
Summaries preserve Evidence/run identity and leave incomplete dispositions
unestablished. Comparisons report findings, expected-exception and
materialized-state changes while distinguishing replay mode from disposition
or plan changes. Cleanup is preview-first, ownership-verified, run-scoped,
idempotent, and preserves Evidence, packages, customer source data, and
configured targets. Generated fixture credentials were scanned in command
outputs and packages.

## Compatibility and preserved boundaries

- Preserve PS-1.0A predicate behavior, checkpoint compatibility and scope
  persistence, fan-out provenance, expected-exception non-waiver,
  materialized-state detection, read-only Verification, and isolated shadow
  writes.
- Windows evidence remains package-only; no Windows customer scenario was
  executed.
- Linux scenario evidence is limited to the packaged Docker evaluation and
  does not establish general Linux product support.
- External Verification remains read-only and does not qualify Recovery.
- Evidence-package integrity and evaluation disposition are separate
  concepts; package validation does not redefine the run disposition.
- Expected exceptions are annotations and never waive findings.
- Cleanup does not delete customer source data or configured targets.
- No production migration or production rollback execution is authorized.
- PS-1.0B remains unaccepted until explicit owner acceptance. Do not begin
  PS-1.0C without a separate assignment.

## Final recommendation gate

The original implementation candidate's technical evidence is complete. A
technical-readiness recommendation is pending protected CI on the
documentation-only PR-head candidate and confirmation that the PR
description and matrix refer to the current evidence. This record does not
grant owner acceptance, authorize merge, or authorize PS-1.0C.
