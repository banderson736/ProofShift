# ERP/HCM Assurance Pack

## Scope

`proofshift.hcm` provides representative ERP/HCM migration assurance for configured worker, employment, organizational, compensation, payroll, benefit, and leave invariants. It is not a full HR/payroll product and does not validate payroll tax or withholding, labor/benefit law, accounting certification, complete ERP reconciliation, or payroll-engine correctness. Fixture data is deterministic and synthetic.

## Concepts

`HCM.Worker`, `HCM.Employment`, `HCM.Position`, `HCM.Organization`, `HCM.CostCenter`, `HCM.Compensation`, `HCM.PayrollResult`, `HCM.BenefitEnrollment`, and `HCM.LeaveBalance`.

## Rules and Reuse

Generic source accounting, presence, uniqueness, attribute comparison, lineage, unexpected-target, and configured `effective-dated-interval` rules are reused. Pack-owned rules are:

- `hcm-reference-integrity`: descriptor-bound, globally ordered source-reference and referenced-key scans; it performs no lookup per worker or relationship.
- `hcm-current-employment`: derive the interval covering a configured as-of date from history, requiring exactly one per observed worker rather than trusting an `is_current` flag.
- `hcm-exact-value-fidelity`: compare compensation/leave quantities as exact `decimal` values.
- `hcm-payroll-reconciliation`: preserve worker, period, currency, gross, deductions and net exactly and validate the configured equation `gross - deductions = net`. It does not calculate taxes or payroll.

All HCM-owned rules declare fields and semantic scope; the generic interval rule declares owner grouping and start-date ordering. These cross-worker/reference scans execute globally. Overlap and continuity are explicit rule options, not universal HCM assumptions.

## Physical Scenario

`HcmPhysicalAssuranceTests` uses a PostgreSQL legacy source and a separate PostgreSQL target with existing connectors. Legacy column names and target names differ. Configured code maps convert worker status, cost center, position and benefit plan codes. The 5,000-worker corpus contains 40,575 business artifacts, including two effective-dated employment and compensation rows per worker, exact payroll results, and related organizations, positions, benefits and leave balances. Numeric fields are PostgreSQL `numeric` and `decimal`, never passed through floating point.

The corrected and defective external targets are independently populated from deterministic fixture transforms and observed by the target connector; they do not use Projection Journal state as actual truth. Equivalent corrected runs under separate run IDs assert equal target, Evidence and ledger fingerprints. Generic interval validation checks overlap; generic expected-vs-observed comparisons preserve employment, compensation and benefit effective boundaries and mapped codes. The corrected graph also runs through shadow Projection, Verification, Evidence persistence, Recovery assessment/rehearsal and qualification.

## Defect Corpus

The manifest defines 14 seeded defects and freezes exactly 25 Verification findings. It covers missing and duplicate workers, a missing historical employment segment, an overlapping interval, a broken organization reference, a wrong-but-valid cost-center assignment, worker and benefit code-map defects, exact compensation and effective-date defects, payroll net mismatch, wrong benefit owner, exact leave-balance discrepancy and an unexpected worker. Per-rule counts include the source-accounting, target-lineage and relationship consequences of deliberately missing rows.

The corrected external scenario asserts 0 Verification failures, 0 unaccounted sources, 0 unexplained targets, and complete expected lineage. The corrected shadow run requires Recovery assessment/rehearsal `PASSED` and qualification `QUALIFIED`.

## Configuration Example

The generic authoring example at `scenarios/hcm-erp-modernization/proofshift.yaml` binds the exact HCM pack version and secret references. Inspect it with:

```text
proofshift packs list
proofshift packs describe hcm
proofshift packs schema hcm
proofshift rules list
proofshift rules describe effective-dated-interval
proofshift config show scenarios/hcm-erp-modernization/proofshift.yaml --effective --json
```

## Limitations

The interval rules validate only configured fixture semantics. Compensation/payroll precision is proven for the selected PostgreSQL numeric values and test corpus, not every provider or magnitude. The physical probe is not a production SLA or payroll certification. ERP journal balancing is omitted because it did not add material proof to this representative slice. Production migration and rollback are out of scope.
