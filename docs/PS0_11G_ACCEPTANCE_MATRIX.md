# PS-0.11G Acceptance Matrix — Pilot Readiness

## Status

**Accepted — October 28, 2026. GO — controlled technical pilot/evaluation
ready.** The 40 gates below are supported by the exact-candidate CI,
package/rehearsal evidence, merged-tree identity, and independent formal GO
decision recorded here. This is not production readiness, GA, public release,
commercial distribution authorization, approval for paid trials, production
migration execution or rollback readiness, an SLA, or regulatory, security,
compliance, or domain certification.

The implementation PR (#18) merged before the independent GO/NO-GO review.
The source candidate passed exact-candidate push CI, the PR-context tree was
identical, and the final merge tree is identical. Preserve this sequence
deviation; future implementation PRs should follow candidate → exact CI →
independent formal review → acceptance decision → merge unless the repository
owner explicitly directs otherwise.

| Group | Gate | Required conclusion | Status | Evidence |
|---|---|---|---|---|
| Baseline | BASE1 | PS-0.11F implementation and acceptance-record closure are merged and authoritative. | PASS | PS-0.11F matrix: 45 PASS; implementation merge `e811c087...`; acceptance closure `810f2f450176bde1eee0edcc200650d633b8f04b`. |
| Baseline | BASE2 | PS-0.11F acceptance is 45 PASS, 0 FAIL, 0 NOT YET; closure is `810f2f450176bde1eee0edcc200650d633b8f04b`, tree `8ec082eb21683dec06ee48971f49e0d088cdea64`. | PASS | Verified PS-0.11F acceptance matrix and merged closure commit/tree. |
| Baseline | BASE3 | ADR-0035 remains Proposed unless separately directed. | PASS | [ADR-0035](adr/0035-external-target-cli-and-verification-only-reporting.md) remains `Proposed under PS-0.11F`; PS-0.11G does not accept it. |
| Status | STAT1 | README, AGENTS, Copilot instructions, roadmap, PS-0.11F scope, and PS-0.11F matrix agree on F acceptance and G current assignment. | PASS | Status surfaces now record F accepted, G accepted, PS-0.11 complete, and no active implementation assignment; F historical evidence is unchanged. |
| Status | STAT2 | PS-0.11G is accepted; PS-1.0 remains unstarted. | PASS | Formal GO decision dated October 28, 2026; PS-1.0 implementation has not started and requires a new owner assignment. |
| Front door | CLI1 | `--help` and `help` exit 0, write help to stdout, and write nothing to stderr. | PASS | Run `40653602682` and PR-context run `40653605811`; `windows-package-smoke`, `linux-package-demos`, and `linux-runtime-only-smoke` passed. |
| Front door | CLI2 | No arguments and an invalid command preserve invalid-usage behavior. | PASS | Exact candidate package-smoke and CLI regression coverage in runs `40653602682` and `40653605811`. |
| Front door | CLI3 | Root help discovers all required commands/aliases and does not expose milestone identifiers. | PASS | Root help validated in the exact-candidate package smoke and runtime-only job; runs `40653602682`, `40653605811`. |
| Front door | CLI4 | Help distinguishes read-only external Verification from shadow dry-run Recovery qualification. | PASS | Exact candidate help content validated by package smoke; external Verification remains read-only and separate from shadow Recovery qualification. |
| Front door | CLI5 | Existing `demo --help` behavior remains valid. | PASS | Windows package smoke and five extracted-Linux-package demos passed in both exact-candidate runs. |
| Package docs | DOC1 | Root `START_HERE.md` is included in both platform archives and answers all required evaluator questions. | PASS | Independent Windows/Linux package builds and package checks passed; runs `40653602682`, `40653605811`. |
| Package docs | DOC2 | Pilot evaluator guide documents both supported tracks and their boundaries. | PASS | `docs/PILOT_EVALUATOR_GUIDE.md` validated in package smoke and physical Linux pilot rehearsal; run `40653605811`. |
| Package docs | DOC3 | Pilot intake checklist captures fit/access/evidence/environment topics without requesting production credentials. | PASS | `docs/PILOT_INTAKE_CHECKLIST.md` included and validated in the extracted evaluator package; run `40653605811`. |
| Package docs | DOC4 | All relative links in the five required operator Markdown files resolve within the extracted package. | PASS | Windows package smoke and Linux packaged rehearsal validated packaged docs and links; runs `40653602682`, `40653605811`. |
| Package docs | DOC5 | Documented command forms are checked against the CLI surface and exercised where practical. | PASS | Windows package-only smoke, Linux packaged demos, and runtime-only smoke passed; runs `40653602682`, `40653605811`. |
| Package docs | DOC6 | Primary evaluator documents do not require internal milestones, source paths, test projects, or developer tools. | PASS | Extracted package front door and rehearsals passed without repository/source/test-project dependencies; run `40653605811`. |
| Windows | WIN1 | Fresh Windows runner verifies package identity, provenance, x64 runtime, and denylist from an extracted package. | PASS | Fresh GitHub-hosted Windows package-only validation; Windows archive SHA-256 `64836287d3f420641ff7fc424d77d11e92843e5140ff92a9f9473a07109f4133`, content SHA-256 `ac04886810881003ff9a22cb0d1b61789461fa5f4e049d133747be1431c60ed0`, lock SHA-256 `f1f413f77f55ead841e5657f677957dc72d5e8ab427679f352b83d40fc95a550`; run `40653605811`. |
| Windows | WIN2 | Windows package-only smoke checks root help/version, demo help/list/describe, sample config validation, docs links, and notices. | PASS | Fresh Windows package-only smoke passed on candidate in runs `40653602682` and `40653605811`; no Docker-backed Windows physical-demo claim. |
| Runtime-only | RT1 | Isolated Linux .NET runtime 10.0.13 environment has no SDK. | PASS | Dedicated `linux-runtime-only-smoke`: Microsoft.NETCore.App `10.0.13`; installed SDKs: none; run `40653605811`. |
| Runtime-only | RT2 | Runtime-only package accepts version, JSON version, root help, demo list, and demo description. | PASS | All specified commands passed in the dedicated no-SDK job `linux-runtime-only-smoke`, run `40653605811`. |
| Demos | DEMO1 | Pension preserves 149 defective discrepancies, zero corrected, corrected `QUALIFIED`, and false-Reverse distinction. | PASS | Linux packaged physical rehearsal: 149 defective / 0 corrected, corrected `QUALIFIED`; False-Reverse Verification passed with 0 Verification failures, 2 rejected unsafe Reverse declarations, 2 discrepancies, `NOT QUALIFIED`; run `40653605811`. |
| Demos | DEMO2 | Utility preserves 14 seeded defects, 26 findings, zero corrected, `QUALIFIED`. | PASS | Packaged Linux demo rehearsal: 14 seeded, 26 findings, 0 corrected, `QUALIFIED`; run `40653605811`. |
| Demos | DEMO3 | Justice preserves 15 seeded defects, 24 findings, zero corrected, `QUALIFIED`. | PASS | Packaged Linux demo rehearsal: 15 seeded, 24 findings, 0 corrected, `QUALIFIED`; run `40653605811`. |
| Demos | DEMO4 | HCM preserves 14 seeded defects, 25 findings, zero corrected, `QUALIFIED`. | PASS | Packaged Linux demo rehearsal: 14 seeded, 25 findings, 0 corrected, `QUALIFIED`; run `40653605811`. |
| Demos | DEMO5 | FHIR preserves 9 seeded defects, 15 findings, zero corrected, `QUALIFIED`. | PASS | Packaged Linux demo rehearsal: 9 seeded, 15 findings, 0 corrected, `QUALIFIED`; run `40653605811`. |
| BYOM | BYOM1 | Extracted package workflow accounts for 102 source artifacts and observes 100 defective/102 corrected targets. | PASS | External migration evaluation from extracted package: 102 source artifacts; 100 defective targets; 102 corrected targets; run `40653605811`. |
| BYOM | BYOM2 | Four seeded defect categories preserve six Evidence findings: 2 MissingTarget, 2 AttributeMismatch, 2 UnaccountedArtifact. | PASS | Physical BYOM Evidence: six findings, exactly 2 MissingTarget, 2 AttributeMismatch, 2 UnaccountedArtifact; run `40653605811`. |
| BYOM | BYOM3 | Corrected run passes with zero Verification failures, unaccounted sources, and unexplained targets; no Recovery qualification. | PASS | Corrected physical BYOM: Verification passed; zero failures, unaccounted sources, or unexplained targets; Recovery assessed `false`; qualification unavailable; run `40653605811`. |
| BYOM | BYOM4 | Corrected repeat uses fresh run IDs and stable target/Evidence semantic fingerprints. | PASS | Same-checkpoint repeat matched target `ead980fa890238f036d08a54c13884bddac439816e3cd03d7b2d134c093d1fa8` and Evidence `ee2e32a9866c877b02c71092647d74fd37dff1b540ecf83b31fbf77fba6e7798`; both are valid deterministic SHA-256 fingerprints; run `40653605811`. |
| Independence | IND1 | Source is physically unavailable after checkpoint; no live-source fallback occurs. | PASS | Physical package rehearsal stopped source availability after checkpoint; no live-source fallback; run `40653605811`. |
| Independence | IND2 | Target observer is read-only; DML and DDL are denied; target state is unchanged by ProofShift. | PASS | BYOM rehearsal verified target credentials read-only, DML/DDL denied, no target mutation by ProofShift; run `40653605811`. |
| Independence | IND3 | External migration fixture is invoked separately; no Projection or Recovery artifacts/qualification are created. | PASS | External fixture invoked separately; Projection and Recovery artifacts absent; Recovery qualification unavailable; run `40653605811`. |
| Reporting | REP1 | Persisted Evidence/report/compare work from stored state and retain exact findings/fingerprints without raw-value leakage. | PASS | Packaged BYOM persisted reporting and comparison passed; exact findings/fingerprints retained without raw-value leakage; run `40653605811`. |
| Reporting | REP2 | Machine-readable rehearsal result is emitted without secrets, raw migration values, or temporary paths. | PASS | Linux package emitted `pilot-readiness-result.json`; safe-field checks passed; run `40653605811`. |
| Security | SEC1 | Synthetic-only data, safe diagnostics, secret references, and package denylist checks pass. | PASS | Synthetic physical fixtures and package denylist/security checks passed in Windows package smoke and Linux rehearsal; run `40653605811`. |
| Reproducibility | REPRO1 | Independent Windows A/B builds match archive SHA, content SHA, and deterministic provenance. | PASS | Windows A/B archive SHA-256 `64836287d3f420641ff7fc424d77d11e92843e5140ff92a9f9473a07109f4133`, content SHA-256 `ac04886810881003ff9a22cb0d1b61789461fa5f4e049d133747be1431c60ed0`, deterministic provenance matched; run `40653605811`. |
| Reproducibility | REPRO2 | Independent Linux A/B builds match archive SHA, content SHA, and deterministic provenance. | PASS | Linux A/B archive SHA-256 `534e3a868648de6a624f16655746450660dac038e9df60f832c5a686e38bcb2d`, content SHA-256 `c301ec6a75795365391161b52122480aa6ae1893a4167c2944be189c325a2d42`, lock SHA-256 `873ea615ecf4c7174cc87c88c6c6a1d063d853d31ddf00e0b558ea5a8b31e387`; deterministic provenance matched; run `40653605811`. |
| Governance | GOV1 | All eleven existing protected checks remain configured and exact-candidate push/PR checks pass. | PASS | Both runs `40653602682` and `40653605811` passed all eleven named jobs; active `main-protection` ruleset targets `refs/heads/main`, with PR/current-main checks, deletion and force-push protection, no bypass actors; merge commits only and delete-branch enabled. |
| Claims | CLAIM1 | Package, platform, pilot, Recovery, distribution, and product limitations are truthful and consistent. | PASS | Acceptance language retains package-only Windows and Ubuntu packaged-Docker limits, no production/rollback approval, no public/commercial release, no SLA/certification, and product identity `0.1.0-preview.1`. |
| Final review | REVIEW1 | Independent reviewer can make a controlled-pilot GO/NO-GO decision from the package and evidence. | PASS | Independent formal decision issued October 28, 2026: **GO — controlled technical pilot/evaluation ready** for the exact merged tree. |

## Result

**PASS: 40**
**FAIL: 0**
**NOT YET: 0**

## Evidence log

| Evidence | Value |
|---|---|
| PS-0.11F implementation candidate | `9748d22889dce85584915dfea06df037f925bf30`, tree `1d96d7f44133789831cb17c3951b6dc5a3187daf` |
| PS-0.11F implementation merge | `e811c08729609afdd3a2cfdf4cb7634012871205`, same tree |
| PS-0.11F acceptance-record merge | `810f2f450176bde1eee0edcc200650d633b8f04b`, tree `8ec082eb21683dec06ee48971f49e0d088cdea64` |
| PS-0.11F acceptance result | 45 PASS, 0 FAIL, 0 NOT YET |
| PS-0.11G starting branch | `ps-0.11g-pilot-readiness-acceptance` |
| PS-0.11G starting main/tree | `810f2f450176bde1eee0edcc200650d633b8f04b` / `8ec082eb21683dec06ee48971f49e0d088cdea64` |
| PS-0.11G source candidate/tree | `b25a483dd5b728474e3e1f8e1b957885d42e309a` / `5b65c63a819bf1db7a06c29d36a52bc1e5d2598b` |
| PS-0.11G starting accepted baseline/tree | `810f2f450176bde1eee0edcc200650d633b8f04b` / `8ec082eb21683dec06ee48971f49e0d088cdea64` |
| PS-0.11G push CI | [Run 40653602682](https://github.com/banderson736/ProofShift/actions/runs/40653602682): all 11 required jobs PASS |
| PS-0.11G PR-context CI | [Run 40653605811](https://github.com/banderson736/ProofShift/actions/runs/40653605811): all 11 required jobs PASS; synthetic merge `6a107d24bbcee87f9cd94b597c298cf0f3059e16`, tree `5b65c63a819bf1db7a06c29d36a52bc1e5d2598b` |
| PS-0.11G required check names | `build-and-test`; `remote-storage-integration`; `oracle-integration`; `db2-integration`; `dependency-review`; `windows-package-reproducibility-b`; `linux-package-reproducibility-b`; `windows-package-smoke`; `linux-package-demos`; `package-reproducibility`; `linux-runtime-only-smoke` |
| PS-0.11G test totals | build-and-test 306; RemoteStorage 54; Oracle 2; Db2 2; total 364 passed, 0 failed, 0 skipped |
| PS-0.11G Windows package | 105,820,628 bytes; archive SHA-256 `64836287d3f420641ff7fc424d77d11e92843e5140ff92a9f9473a07109f4133`; content SHA-256 `ac04886810881003ff9a22cb0d1b61789461fa5f4e049d133747be1431c60ed0`; dependency-lock SHA-256 `f1f413f77f55ead841e5657f677957dc72d5e8ab427679f352b83d40fc95a550` |
| PS-0.11G Linux package | 102,390,752 bytes; archive SHA-256 `534e3a868648de6a624f16655746450660dac038e9df60f832c5a686e38bcb2d`; content SHA-256 `c301ec6a75795365391161b52122480aa6ae1893a4167c2944be189c325a2d42`; dependency-lock SHA-256 `873ea615ecf4c7174cc87c88c6c6a1d063d853d31ddf00e0b558ea5a8b31e387` |
| PS-0.11G runtime-only result | Microsoft.NETCore.App `10.0.13`; installed SDKs: none; front door and runtime-only smoke PASS |
| PS-0.11G physical package rehearsal | Five packaged demos PASS; BYOM external migration evaluation PASS; pilot rehearsal PASS; Windows remains package-only and Ubuntu claim limited to packaged Docker evaluation |
| PS-0.11G implementation PR / merge | PR #18 merged as `6eaac3f7de155d775032964739c832cca2317b6f`; merge tree `5b65c63a819bf1db7a06c29d36a52bc1e5d2598b`, identical to candidate |
| PS-0.11G review-sequence deviation | PR #18 merged before independent GO review; exact candidate push CI, identical PR-context tree, and identical merge tree were reviewed. Future implementation merges return to independent review before merge unless owner directs otherwise. |
| PS-0.11G formal decision | **Accepted — October 28, 2026; GO — controlled technical pilot/evaluation ready**, within documented support boundaries |
| PS-0.11G closure result | 40 PASS, 0 FAIL, 0 NOT YET; documentation/status closure only |
