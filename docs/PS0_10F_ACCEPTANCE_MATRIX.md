# PS-0.10F Cross-Domain Assurance Acceptance Matrix

Status: PS-0.10F In Progress and unaccepted. F1 Domain Pack Conformance + Genericity Guardrails through F5 Healthcare/FHIR are locally validated; stop for review after F5 and do not begin F6 without a new explicit assignment. This matrix is authoritative and records `PASS`, `FAIL`, or `NOT YET`; later slices are not pre-accepted.

| # | Acceptance item | Status | Evidence or gap |
|---:|---|---|---|
| 1 | Reusable pack conformance harness | PASS | `DomainPackConformanceHarness` consumes an `IDomainPack` factory without domain vocabulary and checks provider/rule/schema/plan contracts. |
| 2 | Generic core has no concrete pack dependencies | PASS | Runtime assembly-reference guard covers Domain, Configuration, Graph, Engine, Verification, Evidence, Recovery, Reporting and connector abstractions. |
| 3 | No installed pack semantic strings in generic runtime | PASS | Compiled assembly scan checks installed semantic types, rule IDs, pack IDs and pack-specific underscored fields; docs/tests are excluded. |
| 4 | Pension passes generic conformance | PASS | Focused conformance/architecture class: 4 passed, 0 failed, 0 skipped. |
| 5 | Utility pack resolves by ID/version | PASS | Shared conformance class: 5 passed, 0 failed, 0 skipped; exact `proofshift.utility` version selection. |
| 6 | Utility rule descriptors complete | PASS | Shared conformance harness validates provider metadata, field bindings, generated schema and plan compilation. |
| 7 | Utility exact defective corpus | PASS | Physical scenario locks 14 manifest entries and exact findings: two missing targets, two service-point/account references, four meter-read findings, and exact attribute, uniqueness, aggregate and unexpected-target counts. |
| 8 | Utility corrected scenario has zero defects | PASS | Independently populated corrected PostgreSQL/filesystem target has zero failed findings. |
| 9 | Utility external-target Verification | PASS | Physical scenario captures SQL Server/CSV/filesystem checkpoint and verifies independently populated PostgreSQL/filesystem state. |
| 10 | Utility Recovery and qualification | PASS | Corrected shadow projection and Recovery complete with `QUALIFIED`; defective external target fails as expected. |
| 11 | Utility representative-scale probe | PASS | Physical probe covers 1,000 accounts, 2,000 meter reads, 100 documents and 10,200 expected target artifacts; streaming rule paths exercised without an SLA claim. |
| 12 | Justice pack resolves by ID/version | PASS | Shared conformance class resolves exact `proofshift.justice` version with Pension and Utility installed. |
| 13 | Justice rule descriptors complete | PASS | Shared harness validates Justice metadata, generated schema, reference-field bindings and Verification plan compilation. |
| 14 | Justice exact defective corpus | PASS | Frozen 15-defect manifest asserts exactly 24 findings with exact per-rule counts, including explicit missing-case and orphan-party consequences. |
| 15 | Justice corrected scenario has zero defects | PASS | Corrected external target and shadow Verification have 0 failures, 0 unaccounted sources, 0 unexplained targets, and 0 unexpected artifacts. |
| 16 | Justice external-target Verification | PASS | Two PostgreSQL instances plus filesystem; target rows are independently loaded from deterministic fixture data and observed without Projection Journal truth. |
| 17 | Justice Recovery and qualification | PASS | Generic Recovery assessment and rehearsal pass; corrected dry run is `QUALIFIED`; Evidence and Recovery artifacts are persisted. |
| 18 | Justice representative-scale probe | PASS | 2,000 cases and related records produce 16,200 source artifacts and 16,200 expected target artifacts; no SLA claim. |
| 19 | ERP/HCM pack resolves by ID/version | PASS | Generic registry and CLI expose exact `proofshift.hcm` version `0.10.0`. |
| 20 | ERP/HCM rule descriptors complete | PASS | Shared conformance compiles all four HCM rule factories; descriptors declare fields and global execution. HCM also reuses the generic interval rule descriptor. |
| 21 | ERP/HCM exact defective corpus | PASS | Frozen 14-defect manifest asserts exactly 25 findings with exact per-rule counts, including missing-worker/history accounting and lineage consequences. |
| 22 | ERP/HCM corrected scenario has zero defects | PASS | Corrected independent target and shadow Verification have zero failed findings, unaccounted sources, unexplained targets or unexpected artifacts. |
| 23 | ERP/HCM external-target Verification | PASS | Independent source/target PostgreSQL instances; corrected and defective targets are populated from fixture transforms, not Projection Journal. |
| 24 | ERP/HCM Recovery and qualification | PASS | Generic Recovery assessment and rehearsal pass; corrected shadow run is `QUALIFIED`; Evidence and Recovery artifacts are persisted. |
| 25 | ERP/HCM effective-date proof | PASS | Two employment and compensation periods per worker; exact date fidelity, sequential inclusive intervals, overlap rejection, as-of current employment derivation, and benefit enrollment effective-period preservation. |
| 26 | ERP/HCM exact financial proof | PASS | Typed PostgreSQL numeric/decimal values; exact annual amount and fractional hourly rate plus gross-deductions-net reconciliation are asserted. |
| 27 | ERP/HCM representative-scale probe | PASS | 5,000 workers and 40,575 source/expected target artifacts; 6m37s local Docker run, diagnostic only and not an SLA. |
| 28 | FHIR pack resolves by ID/version | PASS | `proofshift.fhir` 0.10.0 resolves through the pack registry and generic CLI; metadata declares all seven handled concepts, including Binary. |
| 29 | FHIR rule descriptors complete | PASS | Shared pack conformance validates three pack-owned rule descriptors, field bindings, generated schema and execution plan. |
| 30 | FHIR exact defective corpus | PASS | Nine frozen defects produce exactly 15 findings with exact per-rule counts, including accounting, lineage and presence consequences. |
| 31 | FHIR corrected scenario has zero defects | PASS | Independently populated corrected PostgreSQL/filesystem targets have zero failed findings. |
| 32 | FHIR external-target Verification | PASS | NDJSON/filesystem checkpoint and independent PostgreSQL/filesystem target observation; two corrected runs have matching target/evidence/ledger fingerprints. |
| 33 | FHIR Recovery and qualification | PASS | Corrected shadow Projection, persisted Evidence, Recovery assessment/rehearsal pass and qualification is `QUALIFIED`. |
| 34 | FHIR reference integrity | PASS | Pack-owned configured `ResourceType/id` checks catch wrong resource type and missing Binary target; a valid-but-wrong Patient identity is caught by expected-state comparison. |
| 35 | FHIR quantity/unit semantics | PASS | Exact decimal plus unit display, UCUM system and unit code are checked; configured coding system/code maps are compared against graph-derived expectations. |
| 36 | FHIR temporal semantics | PASS | Offset-bearing text is preserved exactly; the defect substitutes a different offset spelling for the same instant and is rejected. |
| 37 | FHIR representative-scale probe | PASS | 1,000 each of six resource types plus 1,000 Binary payloads: 7,000 source and 7,000 corrected-target artifacts; diagnostic only, not an SLA. |
| 38 | Five packs register and resolve generically | NOT YET | Pension, Utility, Justice and HCM are installed; Healthcare/FHIR remains. |
| 39 | Rule IDs and versions bind to pack metadata | NOT YET | Metadata/provider binding is validated for four packs; F5 and five-pack proof remain. |
| 40 | No undeclared field reads across all packs | NOT YET | Descriptors/plans are exercised for four packs; F5 proof remains. |
| 41 | Deterministic semantic findings across packs | NOT YET | Cross-domain scenarios not implemented. |
| 42 | Generic Evidence across packs | NOT YET | Cross-domain scenarios not implemented. |
| 43 | Generic Recovery without pack branches | NOT YET | Cross-domain scenarios not implemented. |
| 44 | Reports use correct pack terminology | NOT YET | Pension report is pack-owned; generic pack-driven report dispatch remains for F6. |
| 45 | Existing authoring works for all packs | PASS | FHIR generic CLI list/describe/schema smoke test passes; conformance confirms provider-owned descriptors and schema. Cross-pack schema-isolation remains row 46. |
| 46 | Configuration schemas are pack-isolated | NOT YET | Cross-pack isolation not yet exercised. |
| 47 | No N+1 regression at representative scale | NOT YET | New pack scale probes not run. |
| 48 | No unbounded pack-specific materialization | NOT YET | New pack scale probes not run. |
| 49 | Pension exact 149/0 regression | PASS | Pension Fast: 2 passed, 0 failed/skipped; 149 defective, 0 corrected, 0 unaccounted/unexplained, corrected `QUALIFIED`. |
| 50 | Existing connector regression after F implementation | NOT YET | Final RemoteStorage, Oracle, and Db2 reruns are required at F acceptance. |
| 51 | RemoteStorage passes | NOT YET | Final F acceptance run pending. |
| 52 | Oracle integration passes | NOT YET | Final F acceptance run pending. |
| 53 | Db2 integration passes | NOT YET | Final F acceptance run pending. |
| 54 | Full local suite passes | NOT YET | F1 focused tests pass; full local suite pending. |
| 55 | Remote CI has zero required skips | NOT YET | Final F acceptance CI pending. |
| 56 | Documentation complete for all packs | PASS | [FHIR pack scope and evidence](FHIR_PACK.md) documents selected-feed limitations, rule semantics, exact defect counts and claim boundaries. |
| 57 | Dependency review | PASS | F1 introduced no package dependencies. Recheck if later slices add packages. |
| 58 | Final candidate SHA recorded | NOT YET | No F candidate commit yet. |
| 59 | PS-0.10G not begun | PASS | No PS-0.10G work started. |
| 60 | Production migration/rollback not added | PASS | All current work remains assurance/rehearsal only. |

## F1 Evidence

- Pack metadata is immutable, deterministically ordered, and binds identity/version, concepts, provider/rule versions, schema contributions, capabilities and authoring metadata.
- `PackRegistry` verifies metadata against the installed provider and preserves exact-version fail-closed resolution.
- Generic conformance/architecture tests pass 3/3 with zero failures/skips.
- Pension semantic rules pass 4/4; Pension report aggregation passes 1/1.
- Pension Fast physical scenario passes 2/2 with exact 149/0 semantics and corrected `QUALIFIED`.
- CLI build passes after moving Pension report implementation into the pack.

## F2 Evidence

- Utility metadata, provider/rule versions, schema contributions, and field worksets pass the shared conformance harness with Pension installed.
- The deterministic physical fixture spans SQL Server, CSV, filesystem documents, and an independently populated PostgreSQL/filesystem target. It includes 1,000 accounts, 2,000 meter reads, and 100 documents.
- The 14-entry defect manifest includes the dangling service-point reference caused by the missing account. Exact assertions cover per-rule findings, including both missing targets, both broken account references, three meter-read value mismatches plus one chronology defect, two payment aggregate groups, and corrected zero-failure behavior.
- Focused results: conformance 5 passed, 0 failed, 0 skipped; physical Utility scenario 1 passed, 0 failed, 0 skipped. Corrected external state has no failed findings; corrected shadow projection/Recovery is `QUALIFIED`.
- This is a representative correctness probe, not a performance SLA. Full local suite, RemoteStorage, Oracle, Db2 and remote CI remain final acceptance gates.

## F3 Evidence

- Frozen Justice concept set: Person, Case, CaseParty, Charge, Filing, Hearing, Disposition, Sentence and Document.
- Generic primitives provide source accounting, target lineage/presence, unexpected-target checks and case/person uniqueness. Justice-owned rules provide configured reference integrity, expected-vs-observed attribute equality and fixture-event ordering.
- The physical scenario also verifies exact filing-date preservation, case-status/disposition code maps, party/case/person/charge/sentence/document relationships and external document SHA-256 readback.
- Physical outcomes: corrected external Verification passes; the defective target produces exactly 24 findings from 15 seeded defects; corrected shadow Evidence and Recovery assessment/rehearsal pass with `QUALIFIED`.
- Focused tests: Justice pack tests 3 passed; shared conformance/genericity 6 passed; Justice physical assurance 1 passed. All had 0 failures and 0 skips.
- The pack report explicitly limits claims to configured migration invariants and excludes legal, jurisdictional, court-rule, sentencing, charge-validity, filing-sufficiency and retention validation.

## F4 Evidence

- Frozen HCM concept set: Worker, Employment, Position, Organization, CostCenter, Compensation, PayrollResult, BenefitEnrollment and LeaveBalance. No ERP journal concept was added.
- Generic source accounting, presence, uniqueness, attribute comparison, lineage, unexpected-target and effective-dated interval rules are reused. HCM-owned rules provide configured relationship scans, as-of current employment derivation, exact decimal fidelity and configured gross-minus-deductions payroll reconciliation.
- PostgreSQL physical fixture uses separate source and target instances and 5,000 deterministic workers, two historical employment/compensation segments each, with 40,575 total business artifacts. It includes mapped employment/status/position/cost-center/benefit/pay-basis values and checks benefit effective-from/to preservation.
- The frozen 14-defect manifest asserts exactly 25 findings, including missing-worker/history accounting cascades, overlapping interval plus boundary-fidelity findings, one orphaned organization reference, one wrong-but-valid cost-center mapping, code/compensation/payroll/benefit/leave defects, and unexpected-target consequences.
- Corrected external Verification, repeated target/evidence/ledger fingerprints, shadow Verification, Evidence persistence, Recovery assessment/rehearsal and `QUALIFIED` all pass. Exact `1,234,567.8901` compensation and `37.6250` hourly rate are preserved; payroll decimals reconcile exactly.
- Focused results: HCM pack tests 3 passed; shared conformance/genericity 7 passed; Verification tests 48 passed; HCM physical 1 passed in 6m37s; Utility physical 1 passed; Justice physical 1 passed; Pension Fast 1 passed. All had zero failures/skips.
- Generic `packs list`, `packs describe`, `packs schema`, `rules describe` and effective config smoke checks resolve HCM without secret values. The report explicitly excludes tax/payroll/legal and accounting certification claims.

## F5 Evidence

- `proofshift.fhir` version `0.10.0` contributes Patient, Encounter, Observation, Condition, Consent, DocumentReference and Binary concepts without new connectors or generic-runtime branches.
- The normalized NDJSON fixture uses 1,000 of each tabular resource plus 1,000 filesystem Binary payloads. The existing connector streams selected flattened fields; it does not index FHIR `coding[]` arrays. This is not raw Bundle/Bulk support or FHIR/profile/US Core certification.
- Pack-owned rules validate configured typed `ResourceType/id` references, exact decimal quantity plus configured unit identity, and offset-bearing instant text. Generic attribute comparison checks graph-derived coding transformations and valid-but-wrong references.
- Corrected independent external targets pass twice with deterministic target/evidence/ledger fingerprints and verified Binary SHA-256 readback. Nine seeded defects produce exactly 15 findings with the frozen per-rule manifest. Corrected shadow Projection, Evidence and Recovery assessment/rehearsal pass with `QUALIFIED`.
- Focused results: FHIR physical assurance 1 passed in 1m43s; FHIR CLI authoring 1 passed; shared domain-pack conformance 8 passed, including all five packs and genericity guards. Final local/vendor/remote gates remain NOT YET.

## Slice Order

F1 through F5 are locally validated. Stop for review here; do not begin F6 Cross-Domain Assurance without a new explicit assignment. PS-0.10F remains In Progress and unaccepted until all matrix gates pass review.
