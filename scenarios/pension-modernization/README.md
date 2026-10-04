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
