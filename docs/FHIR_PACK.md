# FHIR-Style Healthcare Assurance

`proofshift.fhir` version `0.10.0` adds an assurance pack for selected, synthetic FHIR R4-style resource data. It is a migration-assurance example, not a FHIR implementation or certification suite.

## Scope

The physical scenario streams a normalized NDJSON export through the existing structured-file connector and observes independently populated PostgreSQL resource tables and filesystem Binary payloads. It exercises Patient, Encounter, Observation, Condition, Consent, DocumentReference, and Binary concepts; graph transformations; exact source accounting and target lineage; shadow Projection, Verification, Evidence, and Recovery; and independent external-target observation.

The feed has one selected record per resource with `resourceType` and `id`, plus flattened fields for coding, Quantity, and references. This shape is deliberate: the current NDJSON selector supports nested object paths but does not index `coding[]` arrays. It is not a general FHIR JSON parser, Bundle reader, FHIR Bulk transport, or complete R4/profile/US Core validator. No new connector was added.

FHIR-style relative references are configured as `ResourceType/id`. The pack-owned `fhir-typed-reference-integrity` rule checks both the configured resource type and the key against ordered, independently observed target records. The rule does not claim terminology validation, clinical correctness, or medical appropriateness.

`fhir-quantity-fidelity` compares a graph-derived exact decimal with configured unit display, unit-system URI, and unit code fields. Code/system transformations are declared in the migration graph and compared through generic attribute verification. `fhir-instant-fidelity` compares the exact graph-derived text and requires an explicit `Z` or `+/-HH:mm` offset. The target stores these selected instants as text because PostgreSQL `timestamptz` preserves an instant but normalizes away the original offset spelling.

## Physical Evidence

The deterministic fixture contains 1,000 each of Patient, Encounter, Observation, Condition, Consent, and DocumentReference, plus 1,000 Binary payloads: 7,000 source artifacts and 7,000 corrected target artifacts. No clinical or customer data is used.

The defective target has nine seeded defects and exactly 15 findings:

| Rule | Findings |
|---|---:|
| `document-binary` | 1 |
| `encounter-patient` | 1 |
| `observation-code` | 1 |
| `observation-instant` | 1 |
| `observation-patient-match` | 1 |
| `observation-quantity` | 1 |
| `target-lineage` | 1 |
| `target-presence` | 3 |
| `unexpected-target` | 1 |
| `proofshift.verification.materialized-state` | 4 |
| **Total** | **15** |

The corrected independent target has no failed findings. Two corrected external runs with different run IDs have matching target, evidence, and ledger fingerprints. Corrected shadow Projection and Recovery assessment/rehearsal pass and the run is `QUALIFIED`. Filesystem readback verifies every observed Binary SHA-256; the defective case omits one payload.

The report explicitly limits its claims to configured migration invariants. The representative-scale fixture is a test probe, not an SLA or a claim of general FHIR conformance.
