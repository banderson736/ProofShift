# Utility CIS and Billing Assurance

`proofshift.utility` provides configured migration assurance for representative customer, account, service-point, meter, read, rate, invoice, payment, adjustment, document metadata, and payload invariants. It is not tariff validation or regulatory billing certification. Fixture identities and document content are synthetic.

## Physical Scenario

`UtilityPhysicalAssuranceTests` uses SQL Server, CSV, and filesystem sources with an independently populated PostgreSQL/filesystem target. The deterministic fixture contains 1,000 customers, 1,000 accounts, 1,000 service points, 1,000 meters, 2,000 meter reads, 1,000 invoices, 1,000 payments, 1,000 adjustments, 1,000 rate assignments, and 100 document metadata/payload pairs: 10,200 source artifacts and 10,200 corrected target artifacts.

The corrected external target is observed through the registered target connectors rather than Projection Journal state. The corrected shadow pipeline uses generic Projection, Verification, Evidence, and Recovery and reaches `QUALIFIED`. Utility references use the shared ordered `OrderedReferenceMatcher` in `Packs.Abstractions`; Utility retains its plain configured-key interpretation and domain-specific Evidence.

## Defect Corpus

The frozen manifest contains 14 seeded defects and exactly 26 Verification findings. The business-rule assertions lock two missing-target findings, duplicate customer identity, two account-reference findings, one meter assignment mismatch, four meter-read findings (three value mismatches and one ordering defect), rate-code mapping, invoice and adjustment totals, payment ownership and group totals, document ownership, and unexpected-target detection. Additional generic accounting/lineage consequences are included in the total. Every failure carries rule version, artifact inputs, and a human-readable explanation.

The scenario is a correctness probe, not a throughput SLA. It does not certify a tariff schedule, rate calculation, customer eligibility, regulatory reporting, or production billing outcomes.
