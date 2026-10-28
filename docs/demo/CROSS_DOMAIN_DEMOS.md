# Compact Cross-Domain Demos

The Utility, Justice, HCM, and FHIR-style kits are compact operator entry points, not reduced datasets or performance claims. Each run creates an isolated `.proofshift/demos/<id>` workspace and captures machine-readable and human-readable assurance reports. The physical fixtures provision deterministic synthetic source/target data and assert their domain-specific findings and corrected Recovery results.

| Demo | Pack | Source / target artifacts | Seeded defects / findings | Corrected result | Claim boundary |
|---|---|---:|---:|---|---|
| `utility` | `proofshift.utility` 0.10.0 | 10,200 / 10,200 | 14 / 26 | 0 failures, accounting gaps, or lineage gaps; Recovery `PASSED`; `QUALIFIED` | Configured Utility migration invariants only; no tariff or regulatory billing certification. |
| `justice` | `proofshift.justice` 0.10.0 | 16,200 / 16,200 | 15 / 24 | 0 failures, accounting gaps, or lineage gaps; Recovery `PASSED`; `QUALIFIED` | Configured migration invariants only; not law, jurisdiction, court-rule, sentencing, charge-validity, filing-sufficiency, or records-retention validation. |
| `hcm` | `proofshift.hcm` 0.10.0 | 40,575 / 40,575 | 14 / 25 | 0 failures, accounting gaps, or lineage gaps; Recovery `PASSED`; `QUALIFIED` | Configured HCM migration invariants only; not payroll tax/withholding, labor/benefit law, accounting certification, complete ERP reconciliation, or payroll-engine correctness. |
| `fhir` | `proofshift.fhir` 0.10.0 | 7,000 / 7,000 | 9 / 15 | 0 failures, accounting gaps, or lineage gaps; Recovery `PASSED`; `QUALIFIED` | Selected synthetic FHIR-style migration invariants only; not FHIR/profile/US Core certification, terminology validation, clinical correctness, or medical appropriateness. |

Run one kit from the extracted evaluation package root:

```powershell
dotnet app/ProofShift.Cli.dll demo list
dotnet app/ProofShift.Cli.dll demo describe utility
dotnet app/ProofShift.Cli.dll demo preflight utility
dotnet app/ProofShift.Cli.dll demo run utility
```

Use `justice`, `hcm`, or `fhir` in place of `utility` for the other kits. Docker-compatible services and .NET 10 are required. Preflight reports missing prerequisites before a run allocates its workspace.

External targets are populated independently and observed read-only by Verification; a separate corrected shadow run exercises Projection and Recovery. ProofShift does not perform the external migration. The generic report separates both external observations from corrected shadow Projection/Recovery, presents source accounting and target lineage totals, and lists value-free Evidence IDs with rule/code navigation. The CLI Evidence stage verifies the selected external report's integrity; the Recovery stage reads the qualified corrected dry run. Reports are under the scenario-owned workspace; `demo reset <id>` removes only that marker-owned directory after path/reparse-point checks.
