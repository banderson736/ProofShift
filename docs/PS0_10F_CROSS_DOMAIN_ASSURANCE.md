# PS-0.10F Cross-Domain Assurance

Status: F6 Cross-Domain Assurance locally complete; PS-0.10F remains In Progress pending final acceptance closure. The authoritative exact physical scenario index is [PS0_10F_SCENARIO_INDEX.json](PS0_10F_SCENARIO_INDEX.json). Stop for review here; PS-0.10G and final acceptance publication are out of scope.

## Why These Domains

The five slices deliberately stress different migration semantics without combining them into a synthetic mega-project:

- Pension stresses long-lived employment/status history, financial transactions, beneficiary relationships, document preservation, and recovery classification.
- Utility CIS/Billing stresses meter chronology, customer/account relationships, exact billing aggregates, CSV mappings, and documents.
- Justice stresses case/party relationships, event chronology, code mappings, and binary documents.
- ERP/HCM stresses effective-dated employment/compensation, organizational references, exact decimals, and payroll equations.
- Healthcare/FHIR stresses typed resource references, exact Quantity/unit identity, coding transformations, offset-bearing instants, and Binary payloads.

They are separate projects and runs. The evidence supports configured synthetic migration invariants, not universal domain support or certification.

## Physical Scenario Summary

| Pack | Source / corrected target artifacts | Seeded defects | Findings | Corrected | Shadow recovery | Local duration |
|---|---:|---:|---:|---|---|---:|
| Pension | 8,495 / 8,595 | 149 | 149 | 0 failures, 0 unaccounted, 0 unexplained | PASSED; `QUALIFIED`; false-Reverse remains not qualified | 129.934 s |
| Utility | 10,200 / 10,200 | 14 | 26 | 0 failures | PASSED; `QUALIFIED` | 68.974 s |
| Justice | 16,200 / 16,200 | 15 | 24 | 0 failures | PASSED; `QUALIFIED` | 96.730 s |
| HCM | 40,575 / 40,575 | 14 | 25 | 0 failures, 0 unaccounted, 0 unexplained | PASSED; `QUALIFIED` | 364.936 s |
| FHIR | 7,000 / 7,000 | 9 | 15 | 0 failures, 0 unaccounted, 0 unexplained | PASSED; `QUALIFIED` | 93.951 s |

Timings are individual local Docker-backed test durations, diagnostic only and not an SLA. The index links each entry to its authoritative physical test and records connector topology and claim boundary.

## Shared Architecture Evidence

All five IDs (`proofshift.pension`, `proofshift.utility`, `proofshift.justice`, `proofshift.hcm`, and `proofshift.fhir`) are registered in the same CLI composition root and resolve through `PackRegistry` and `VerificationRuleRegistry`. The aggregate test checks exact ID/version selection, metadata, global rule-type uniqueness, provider/rule versions, schema ownership, unknown ID/version errors, and cross-pack rule rejection. Enumeration is tested for deterministic ordering. Sequential HCM/FHIR selection checks for registry contamination.

The same reusable conformance harness constructs descriptors and `VerificationExecutionPlan` worksets for every pack. Generic assembly checks include Domain, Configuration, Graph, Engine, Verification, Evidence, Recovery, Projection, Snapshots, Reporting, and connector abstractions. Concrete pack assemblies are checked against one another. Genericity checks allow semantic identifiers as data but fail if installed pack terms appear in generic runtime binaries or if generic projects reference concrete packs. `ProofShift.Reporting` depends on the shared `Packs.Abstractions` metadata contract, not a concrete pack.

Utility, Justice, HCM, and FHIR share `OrderedReferenceMatcher` in `Packs.Abstractions` for descriptor-bound ordered source/target scans. Utility, Justice, and HCM supply their configured key interpretation; FHIR alone parses `ResourceType/id` into a key. Generic Verification sees neither a FHIR type parser nor a domain branch. The helper performs ordered streaming matching and issues no per-artifact database queries.

The generic `effective-dated-interval` rule remains in Verification and is configured only with target node, semantic type, identity, owner, inclusive date bounds, and optional continuity. It contains no HCM semantics; generic assembly semantic-string checks include installed pack terms. Its workspace policy tests cover sequential intervals, inclusive overlap, open ends, invalid bounds, and configured gaps.

## Expected State, Evidence, and Recovery

For every pack, expected target values are derived from checkpoint plus graph transforms/configuration. Corrected Utility, Justice, HCM, and FHIR external targets are independently populated and observed; they do not rely on Projection Journal truth. The generic no-Projection-manifest/journal regression directly covers mapped-attribute comparison, and physical external scenarios exercise it with domain data.

One generic disposition/lineage ledger accounts for corrected sources and expected targets across the five scenarios. One Evidence graph model represents rule identity/version, semantic scope, artifact references, expected/actual values, result, and human-readable explanation. Physical tests assert exact defects and evidence quality; source/target accounting and lineage gaps are zero on corrected scenarios.

Every corrected shadow scenario uses the same generic Recovery classification, assessment, rehearsal where configured, evidence persistence, and qualification. No pack owns a checkpoint, workspace, Evidence store, or qualification service. Pension retains its accepted false-Reverse regression.

## Reporting and Authoring

`CrossDomainAssuranceReport` is one metadata-driven report model in generic Reporting. It combines pack metadata, the Verification runtime's pack/rule-version fingerprints, generic findings/accounting/lineage totals, Recovery outcomes, and a caller-supplied claim boundary. The five physical tests inspect this same model and reject foreign-domain labels. Existing pack-owned report objects remain unchanged where already present; no new pack renderer was added.

The CLI matrix exercises deterministic `packs list`, `packs describe`, `packs schema`, `rules list`, representative `rules describe`, and `config show --effective` for all five pack selections. The schemas expose only each selected pack's rules plus shared generic options. Effective output is checked for selected pack ID/version, selected rules, and absence of resolved secret values.

## Determinism, Performance, and Security

Corrected repeat runs retain the semantic fingerprints already covered by each physical scenario; Utility now also asserts repeated target, Evidence, and ledger fingerprints. Run IDs and observation timestamps remain run-bound. Justice reference scans, HCM history/reference scans, Utility aggregate/reference scans, and FHIR typed references use ordered worksets rather than a database lookup per artifact. The review found no pack-owned workspace/database/cache/checkpoint format or whole-dataset rule materialization. SQL statement counts and memory bounds were not added as new instrumentation; the performance evidence is code-path inspection plus the representative physical probes, not an asymptotic guarantee.

F1-F6 adds no external NuGet package. New F6 code depends only on repository projects. All fixtures and documents are deterministic synthetic data. The generic report aggregates findings rather than copying patient, court, employee, or customer field values into a new report surface.

## ADR and Closure

ADR-0012's external-target boundary matches the tested generic mapped-attribute path. ADR-0027's concrete-pack/core separation is enforced by the expanded dependency and semantic-string checks. ADR-0028's interval rule remains generic and configured. No ADR was added or marked Accepted for F6.

F6 local completion is distinct from PS-0.10F acceptance. The full local suite, RemoteStorage, Oracle, Db2, final remote CI, candidate commit/SHA, and push remain `NOT YET` closure gates and are not run or authorized by this slice. No PS-0.10G work, production migration, or rollback was started.
