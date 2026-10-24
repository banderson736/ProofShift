# PS-0.11A — Generic Operator Reporting & Comparison

## Status

In Progress. This slice makes the ordinary persisted `report` and `compare` commands domain-pack-neutral. It does not change accepted Verification, Evidence, Recovery, checkpoint, graph, projection, or domain semantics. The milestone is ready for review only after its implementation, tests, documentation, and candidate CI evidence are complete; implementation must not mark it Accepted.

## Objective and scope

An operator must be able to report on and compare persisted assurance runs for any of the five installed domain packs through the normal ProofShift CLI. Generic orchestration must not branch on concrete pack IDs/types, depend on the Pension assembly, or depend on demo manifests or EndToEnd fixtures. The accepted PS-0.10G evaluator and package behavior remains unchanged.

The ordinary commands remain:

```text
proofshift report <config> --run <dry-run-id> [--json]
proofshift compare <config> --before <dry-run-id> --after <dry-run-id> [--json]
```

The configured project supplies the exact pack ID/version and claim boundary. If current architecture cannot give a single unambiguous primary pack for a report, reporting fails closed with an actionable diagnostic.

## Persisted reporting architecture

The generic report path loads and validates the ordinary project configuration, compiles the current graph, and resolves the exact configured pack/version through the existing pack registry. It then reads the requested run's persisted Verification Evidence and Recovery artifacts, verifies their integrity, and checks all available bindings: project/configuration, graph, checkpoint/source, projection or independent observation, rule set, Verification run/Evidence, Recovery evidence/rehearsal, and dry-run context.

Reporting must not rerun Verification. Evidence, Verification ledger/run metadata, and Recovery artifacts remain the authoritative persisted facts. A rendered report is deterministic, presentation-only output; it is neither persisted semantic state nor an input to qualification. If a small read-only accessor is required for already-persisted metadata, add only that generic accessor and reuse existing persisted formats rather than creating a duplicate persistence model.

The report binds its schema/version, project identity, resolved pack ID/version/display name, and configured claim boundary. Where authoritative persisted state supports them, it reports configuration and graph hashes, checkpoint/source and projection/observation identity, rule-set identity, Verification run/outcome/Evidence fingerprint, deterministically grouped failed findings and stable Evidence references, source-accounting and target-lineage state/gaps, Recovery assessment/rehearsal, qualification, Recovery Evidence fingerprint, dry-run fingerprint, and useful selected-pack concepts/rule metadata. Human and JSON rendering must be deterministic and use only the selected pack's terminology. Raw expected/actual sensitive values and resolved secrets must not appear.

The generic business-discrepancy count reflects the total failed Verification Evidence records; it does not deduplicate multiple findings into pack-specific seeded-defect categories. False-Reverse and other Recovery readiness issues remain separately reported in the Recovery section and are not added to that count. Domain-specific demo summaries may therefore use different defect totals while preserving the same Verification evidence and qualification.

Pack claim boundaries are required pack metadata. Persisted Evidence context carries the provider versions resolved for the Verification run; this metadata is not part of Evidence canonicalization and does not change Evidence fingerprints. Recovery's generic read-only summary exposes existing persisted assessment, Projection/checkpoint identity, and Verification-ledger receipt data. These durable contracts are recorded in [ADR-0030](adr/0030-generic-persisted-report-context.md).

## Comparison semantics

The generic comparison reads and validates both persisted contexts using the same configuration and pack-resolution rules. It reports before/after qualification and whether it changed; configuration/graph, source fingerprint, checkpoint semantics, projected and observed target semantics, rule-set, Verification Evidence, Recovery Evidence, Recovery policy/readiness, source-accounting gaps, and target-lineage gaps; and findings resolved or introduced by stable rule/code. It provides independent difference attribution. Checkpoint semantics include source fingerprints, source-node/endpoint identity, selector and identity fields, connector/provider/version, consistency strategies, cross-system atomicity, replayability, and artifact coverage. They exclude checkpoint IDs, capture timestamps, byte-storage details, and segment references/names. Run identifiers alone—including Verification, projection/observation, checkpoint, and dry-run IDs—are presentation context, not semantic differences.

Run IDs alone are not semantic differences. Comparison does not rerun Verification and never compares raw sensitive record values. Human and JSON output are deterministic.

## Fail-closed behavior and secret safety

Tests must verify actionable failures for invalid run IDs; missing Evidence; corrupted Evidence manifest/segment; missing or corrupted Recovery artifacts; configuration or graph mismatch; unsupported/mismatched pack version; persisted context belonging to another selected project; and ambiguous unsupported pack context where applicable. Diagnostics must not disclose secrets or raw expected/actual values. Persisted artifact integrity and context binding are checked before rendering.

## Required pack and regression coverage

The ordinary report and compare implementations must work through the same generic orchestration for `proofshift.pension`, `proofshift.utility`, `proofshift.justice`, `proofshift.hcm`, and `proofshift.fhir`, using accepted physical assurance scenarios and persisted artifacts where practical. The implementation under test must not depend on demo/test harness code.

Preserve exact accepted scenario semantics:

| Pack | Defective result | Corrected result |
|---|---:|---|
| Pension | 149 defective findings | 0 findings; `QUALIFIED` |
| Utility | 14 seeded defects / 26 findings | 0 failures; `QUALIFIED` |
| Justice | 15 seeded defects / 24 findings | 0 failures; `QUALIFIED` |
| HCM | 14 seeded defects / 25 findings | 0 failures; `QUALIFIED` |
| FHIR | 9 seeded defects / 15 findings | 0 failures; `QUALIFIED` |

Pension false-Reverse Verification remains passing while Recovery rejects the two unsafe Reverse declarations. Do not alter any count or domain outcome to accommodate reporting. Existing PS-0.10G evaluator demos and package smoke remain green; product-version changes are deferred.

## Architecture protections

Extend conformance/genericity tests to prove:

- generic Reporting has no concrete-pack dependency;
- generic CLI report/compare orchestration has no concrete-pack semantic branches;
- generic report models contain no hard-coded installed-domain terminology;
- concrete packs remain independent of one another;
- persisted Evidence and Recovery remain authoritative inputs.

Do not weaken existing assembly-reference or semantic-string guards.

## Out of scope

No new connectors or packs, FHIR Bulk, generic REST, web UI, API/service hosting, authentication/authorization, AI authoring, production migration execution or rollback, distributed workers, broad Verification performance tuning, large-scale reruns, demo-fixture separation, release-version changes, or major refactoring of `SqliteVerificationWorkspace`, `VerificationService`, or `RecoveryService`.

## Acceptance for review

- Both ordinary commands use one generic persisted path across all five packs with no concrete-pack branching or demo/test dependency.
- Persisted Evidence and Recovery integrity and all supported run/context bindings are verified before rendering.
- Reporting does not rerun Verification; rendered output remains presentation-only.
- Human and JSON report/comparison outputs are deterministic, secret-safe, and free of foreign-domain terminology.
- Required fail-closed, context-mismatch, pack-genericity, and authority tests pass.
- All exact five-pack defective/corrected semantics and PS-0.10G demo/package regressions remain unchanged.
- Full local solution tests pass; RemoteStorage, Oracle, Db2, and required remote GitHub Actions acceptance pass with zero required skips.
- Candidate SHA and CI run are recorded for formal review. Do not self-accept or merge the milestone.
