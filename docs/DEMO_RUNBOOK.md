# Demo Runbook

Run one scenario at a time from the extracted evaluation package root. The demos use Docker-backed synthetic fixtures; cold image startup varies and the runtime estimates are diagnostic, not SLAs.

## Presenter Flow

1. Show `demo list`, then `demo describe pension`. State the configured synthetic claim boundary before starting the run.
2. Run `demo preflight pension` and resolve any blocked requirement before allocating a workspace.
3. Run `demo run pension`. Point out the 149 defective findings and `NOT QUALIFIED` result, then the corrected zero discrepancies/accounting/lineage gaps and `QUALIFIED` Recovery.
4. Show the false-Reverse report: Verification passes with zero Verification failures, while Recovery rejects two unsafe Reverse edges and leaves the dry run `NOT QUALIFIED`.
5. Open the defective and corrected report paths, then compare the two run IDs to review remediation and Evidence attribution.
6. Demonstrate `demo describe utility`, `demo describe justice`, `demo describe hcm`, and `demo describe fhir`; run one compact kit as time permits. Their reports separate read-only external observations from corrected shadow Projection/Recovery and provide Evidence IDs for navigation.

## Repeat and Cleanup

`demo run <id>` refuses to overwrite an existing workspace. To repeat a scenario, inspect/copy any wanted reports, then run:

```powershell
dotnet app/ProofShift.Cli.dll demo reset pension
dotnet app/ProofShift.Cli.dll demo run pension
```

Reset is restricted to `.proofshift/demos/<id>` after ownership-marker and reparse-point checks. It does not stop unrelated containers or delete files outside that scenario workspace. Testcontainers removes containers when the fixture completes; if the host or process is forcibly terminated, inspect Docker before retrying.

## Operator Notes

- The demo uses the accepted deterministic fixtures; do not present the external target as a migration executed by ProofShift.
- Finding summaries omit raw record values. Use report Evidence IDs and the `evidence` command for integrity-checked detail.
- Keep each pack's `claimBoundary` visible. Selected FHIR-style scenarios are not FHIR/profile certification; HCM is not payroll-engine or tax certification; Justice is not legal validation; Utility is not tariff/regulatory billing certification; Pension is not actuarial or benefit-calculation certification.
- The report distinguishes external target Verification from the corrected shadow Recovery assessment. Do not conflate their run IDs or qualification states.
- Binary fixture stages report elapsed time and a sampled peak working set for the MTP fixture process; container memory is excluded. Record those values only as host-specific diagnostics, not a service level or workload guarantee.