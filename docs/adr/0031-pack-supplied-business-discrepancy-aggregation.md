# ADR-0031: Pack-supplied business-discrepancy aggregation

- Status: Proposed for PS-0.11A
- Date: 2026-10-24

## Context

The generic persisted report originally defined its business-discrepancy count as every failed Verification Evidence record. This changed the accepted Pension report result: Pension's established report selects business-defect Evidence codes and adds false-Reverse Recovery edges, while the generic report exposed all failed Evidence. For the accepted defective Pension scenario, the raw failed-Evidence count is 213 but the established business-discrepancy result is 149. The false-Reverse scenario has passing Verification and two Recovery findings, and its accepted business-discrepancy result is 2.

PS-0.11A must preserve the accepted PS-0.10G package behavior and scenario semantics while keeping persisted report orchestration neutral to concrete packs.

## Decision

- Keep `VerificationFailureCount` as the raw number of failed Verification Evidence records.
- Require every installed pack to provide immutable `BusinessDiscrepancyAggregationMetadata` as part of its pack metadata.
- The metadata declares either all failed Evidence or an explicit set of Evidence codes to include. It can also declare whether the already-persisted false-Reverse Recovery edge count contributes to the business-discrepancy total.
- Generic Reporting applies the selected pack's metadata to failed Evidence counts by code and the existing Recovery summary. It does not inspect pack IDs, rule types, or concrete pack assemblies.
- Pension supplies the Evidence codes from the existing Pension report aggregation and includes false-Reverse Recovery edges. Utility, Justice, HCM, and FHIR use all failed Evidence and do not add Recovery counts.
- Keep the metadata outside Evidence, Recovery, and semantic fingerprints. The report remains deterministic presentation output based on the exact selected pack version and integrity-validated persisted artifacts.

## Alternatives

- Change the accepted Pension package assertions to use all 213 failures: rejected because it changes PS-0.10G report behavior and conflates raw rule findings with the accepted business-discrepancy total.
- Add a Pension-specific branch to generic Reporting: rejected because it violates pack independence and creates a concrete domain dependency in the generic reporting path.
- Store an additional report artifact: rejected because the aggregation is deterministic pack metadata applied to already-persisted Evidence and Recovery, not new authoritative run state.
- Keep Recovery findings excluded from every business-discrepancy total: rejected because the accepted false-Reverse Pension report counts the two invalid Recovery edges while Verification passes.

## Consequences

- Reports distinguish raw Verification failures from pack-defined business discrepancies.
- The generic runtime can preserve established pack-specific reporting meaning without containing domain-specific logic.
- A pack version binds its aggregation declaration; changing that declaration changes the installed pack metadata contract.
- The persisted Evidence and Recovery formats, their fingerprints, verification behavior, qualification, and PS-0.10G package assertions remain unchanged.
- Pack conformance and persisted-report tests must verify deterministic metadata and the exact accepted counts.

## Validation

The five-pack conformance test verifies each pack's declared aggregation mode. Pension report coverage verifies 149 defective, zero corrected, and two false-Reverse discrepancies; required packaged demo CI remains necessary to confirm PS-0.10G compatibility. PS-0.11A remains In Progress pending all acceptance gates and formal review.
