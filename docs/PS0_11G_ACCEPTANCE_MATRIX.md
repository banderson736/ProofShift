# PS-0.11G Acceptance Matrix — Pilot Readiness

## Status

**In Progress. Not self-accepted.** Starting `main` is
`810f2f450176bde1eee0edcc200650d633b8f04b` with tree
`8ec082eb21683dec06ee48971f49e0d088cdea64`. All gates are `NOT YET` until
verified evidence is recorded. No gate may be inferred from prior milestones.

| Group | Gate | Required conclusion | Status | Evidence |
|---|---|---|---|---|
| Baseline | BASE1 | PS-0.11F implementation and acceptance-record closure are merged and authoritative. | NOT YET | |
| Baseline | BASE2 | PS-0.11F acceptance is 45 PASS, 0 FAIL, 0 NOT YET; closure is `810f2f450176bde1eee0edcc200650d633b8f04b`, tree `8ec082eb21683dec06ee48971f49e0d088cdea64`. | NOT YET | |
| Baseline | BASE3 | ADR-0035 remains Proposed unless separately directed. | NOT YET | |
| Status | STAT1 | README, AGENTS, Copilot instructions, roadmap, PS-0.11F scope, and PS-0.11F matrix agree on F acceptance and G current assignment. | NOT YET | |
| Status | STAT2 | PS-0.11G status is In Progress and not self-accepted; PS-1.0 remains unstarted. | NOT YET | |
| Front door | CLI1 | `--help` and `help` exit 0, write help to stdout, and write nothing to stderr. | NOT YET | |
| Front door | CLI2 | No arguments and an invalid command preserve invalid-usage behavior. | NOT YET | |
| Front door | CLI3 | Root help discovers all required commands/aliases and does not expose milestone identifiers. | NOT YET | |
| Front door | CLI4 | Help distinguishes read-only external Verification from shadow dry-run Recovery qualification. | NOT YET | |
| Front door | CLI5 | Existing `demo --help` behavior remains valid. | NOT YET | |
| Package docs | DOC1 | Root `START_HERE.md` is included in both platform archives and answers all required evaluator questions. | NOT YET | |
| Package docs | DOC2 | Pilot evaluator guide documents both supported tracks and their boundaries. | NOT YET | |
| Package docs | DOC3 | Pilot intake checklist captures fit/access/evidence/environment topics without requesting production credentials. | NOT YET | |
| Package docs | DOC4 | All relative links in the five required operator Markdown files resolve within the extracted package. | NOT YET | |
| Package docs | DOC5 | Documented command forms are checked against the CLI surface and exercised where practical. | NOT YET | |
| Package docs | DOC6 | Primary evaluator documents do not require internal milestones, source paths, test projects, or developer tools. | NOT YET | |
| Windows | WIN1 | Fresh Windows runner verifies package identity, provenance, x64 runtime, and denylist from an extracted package. | NOT YET | |
| Windows | WIN2 | Windows package-only smoke checks root help/version, demo help/list/describe, sample config validation, docs links, and notices. | NOT YET | |
| Runtime-only | RT1 | Isolated Linux .NET runtime 10.0.13 environment has no SDK. | NOT YET | |
| Runtime-only | RT2 | Runtime-only package accepts version, JSON version, root help, demo list, and demo description. | NOT YET | |
| Demos | DEMO1 | Pension preserves 149 defective discrepancies, zero corrected, corrected `QUALIFIED`, and false-Reverse distinction. | NOT YET | |
| Demos | DEMO2 | Utility preserves 14 seeded defects, 26 findings, zero corrected, `QUALIFIED`. | NOT YET | |
| Demos | DEMO3 | Justice preserves 15 seeded defects, 24 findings, zero corrected, `QUALIFIED`. | NOT YET | |
| Demos | DEMO4 | HCM preserves 14 seeded defects, 25 findings, zero corrected, `QUALIFIED`. | NOT YET | |
| Demos | DEMO5 | FHIR preserves 9 seeded defects, 15 findings, zero corrected, `QUALIFIED`. | NOT YET | |
| BYOM | BYOM1 | Extracted package workflow accounts for 102 source artifacts and observes 100 defective/102 corrected targets. | NOT YET | |
| BYOM | BYOM2 | Four seeded defect categories preserve six Evidence findings: 2 MissingTarget, 2 AttributeMismatch, 2 UnaccountedArtifact. | NOT YET | |
| BYOM | BYOM3 | Corrected run passes with zero Verification failures, unaccounted sources, and unexplained targets; no Recovery qualification. | NOT YET | |
| BYOM | BYOM4 | Corrected repeat uses fresh run IDs and stable target/Evidence semantic fingerprints. | NOT YET | |
| Independence | IND1 | Source is physically unavailable after checkpoint; no live-source fallback occurs. | NOT YET | |
| Independence | IND2 | Target observer is read-only; DML and DDL are denied; target state is unchanged by ProofShift. | NOT YET | |
| Independence | IND3 | External migration fixture is invoked separately; no Projection or Recovery artifacts/qualification are created. | NOT YET | |
| Reporting | REP1 | Persisted Evidence/report/compare work from stored state and retain exact findings/fingerprints without raw-value leakage. | NOT YET | |
| Reporting | REP2 | Machine-readable rehearsal result is emitted without secrets, raw migration values, or temporary paths. | NOT YET | |
| Security | SEC1 | Synthetic-only data, safe diagnostics, secret references, and package denylist checks pass. | NOT YET | |
| Reproducibility | REPRO1 | Independent Windows A/B builds match archive SHA, content SHA, and deterministic provenance. | NOT YET | |
| Reproducibility | REPRO2 | Independent Linux A/B builds match archive SHA, content SHA, and deterministic provenance. | NOT YET | |
| Governance | GOV1 | All eleven existing protected checks remain configured and exact-candidate push/PR checks pass. | NOT YET | |
| Claims | CLAIM1 | Package, platform, pilot, Recovery, distribution, and product limitations are truthful and consistent. | NOT YET | |
| Final review | REVIEW1 | Independent reviewer can make a controlled-pilot GO/NO-GO decision from the package and evidence. | NOT YET | |

## Evidence log

| Evidence | Value |
|---|---|
| PS-0.11F implementation candidate | `9748d22889dce85584915dfea06df037f925bf30`, tree `1d96d7f44133789831cb17c3951b6dc5a3187daf` |
| PS-0.11F implementation merge | `e811c08729609afdd3a2cfdf4cb7634012871205`, same tree |
| PS-0.11F acceptance-record merge | `810f2f450176bde1eee0edcc200650d633b8f04b`, tree `8ec082eb21683dec06ee48971f49e0d088cdea64` |
| PS-0.11F acceptance result | 45 PASS, 0 FAIL, 0 NOT YET |
| PS-0.11G starting branch | `ps-0.11g-pilot-readiness-acceptance` |
| PS-0.11G starting main/tree | `810f2f450176bde1eee0edcc200650d633b8f04b` / `8ec082eb21683dec06ee48971f49e0d088cdea64` |
| PS-0.11G candidate, CI, package evidence | NOT YET |
| PS-0.11G status | In Progress; not self-accepted |
