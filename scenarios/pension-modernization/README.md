# Pension Modernization Synthetic Scenario

This scenario will become the deterministic PS-0 end-to-end demonstration.

## Intended source topology

- SQL Server legacy pension data
- file/document storage

## Intended target topology

- PostgreSQL shadow pension model
- filesystem/object-store shadow document store

## Domain concepts

- Pension.Member
- Pension.Employment
- Pension.Contribution
- Pension.ServiceCredit
- Pension.Beneficiary
- Pension.RetirementElection
- Pension.BenefitPayment
- Pension.Document

## Dataset behavior

The final generator should support 100,000 members and millions of financial/history records with a fixed seed. Development begins with a 10-member vertical slice.

## PS-0.5 Shadow Projection fixture

The runnable multi-file project is `ps05/proofshift.yaml`. It combines SQL Server `dbo.MEMBER`, a ten-row semicolon CSV, and one synthetic document; graph paths project into PostgreSQL template tables and a per-run filesystem shadow directory namespaced by graph target node. Apply `ps05/database/sqlserver-source.sql` to a synthetic source database and `ps05/database/postgres-shadow-template.sql` to the target database. Configure `PS05_SQL_CONNECTION`, `PS05_POSTGRES_CONNECTION`, `PS05_SOURCE_FILES`, `PS05_SOURCE_CSV`, and `PS05_SHADOW_FILES` as described in `docs/CONFIGURATION.md`.

The supplemental `member_id` target column is text to match CsvHelper's textual source values and preserve identifier formatting, including leading zeros; PS-0.5 does not infer numeric conversions from database column types.

Run `proofshift validate`, `proofshift plan`, and `proofshift inspect` against that manifest before `proofshift project <path-to-ps05/proofshift.yaml> [--json]`. The seeded `MEMBER_STATUS = X` is intentionally unmapped, so the first projection must return `Failed` with the preceding output and journal retained. Correct that source row to `A` or `R` and project again; the rerun receives a separate PostgreSQL schema and filesystem directory. A projected result is not verified data.

## Initial 10-member seed defects

- one missing member;
- one invalid status transformation;
- one capitalization-only difference that should normalize and pass.

Later defect counts are specified in `docs/PS0_SPECIFICATION.md`.

## PS-0.8 Recovery Readiness fixture

The PS-0.5 synthetic fixture now declares `restore` for the member split because trimming and normalizing the source name are lossy, `reverse` for the field-preserving supplemental map, and `restore` for the copied document archive. The target recovery checkpoint is separate from the source checkpoint: PostgreSQL captures the isolated per-run schema, and filesystem recovery captures the isolated per-run directory and identity index.

After a clean checkpoint-backed projection and verification, `proofshift dry-run <path-to-ps05/proofshift.yaml> --json` captures target baselines, applies a deterministic shadow-only mutation, restores and re-reads both target backends, then records dry-run qualification. `proofshift recovery <path-to-ps05/proofshift.yaml> --run <dry-run-id> --json` verifies persisted recovery-artifact integrity before reporting it.

The end-to-end false-Reverse scenario includes two deliberately lossy mappings: a many-to-one status map and a pass-through code map. Projection and Verification succeed from the matching checkpoint/graph, while Recovery rejects both configured inverses and the dry run is not qualified.

## PS-0.9 Public Pension Assurance vertical

The versioned clean generator and independent v1 defect injector are described in [the demo guide](../../docs/demo/PENSION_DEMO.md) and pack contract (`docs/PENSION_PACK.md`). Generate CSV fixtures with:

```powershell
dotnet run --project src/ProofShift.Cli -- demo generate .proofshift/pension-demo --scale fast --seed 20261003
```

Output separates clean legacy `source/`, corrected semantic `target-corrected/`, and injected `target-defective/` data. `demo-manifest.json` records versions, scale, seed, counts, and source fingerprint. The direct-target rule test resolves Pension rules against externally supplied target records without invoking Projection.

The integrated fast-corpus test composes physical SQL Server, CSV, and filesystem source endpoints with PostgreSQL and filesystem shadow targets. It checkpoints 8,495 artifacts, including 275 binary payloads, and projects 8,595 targets. Corrected and projected-defective runs use the generic Projection, Verification, and Recovery services; persisted reports and the before/after comparison assert 149 discrepancies resolved to zero. The two false-Reverse edges remain executable and are rejected by Recovery. Run the operator flow with `./scripts/pension-demo.ps1 -OutputDirectory <new-empty-directory>`. PS-0.9 was accepted after Docker-backed [GitHub Actions run 37238973286, job 111543651181](https://github.com/banderson736/ProofShift/actions/runs/37238973286/job/111543651181) passed 113 tests with zero failures and zero skips.
