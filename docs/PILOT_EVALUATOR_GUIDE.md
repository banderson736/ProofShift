# Pilot Evaluator Guide

This guide is for a technically competent evaluator using the extracted
ProofShift evaluation package. It does not require repository, source, test,
or IDE access. Use deterministic synthetic or separately authorized
non-production data only. Do not put credentials in project files or logs;
use environment-backed secret references and distinct least-privilege
credentials.

Start with [`START_HERE.md`](../START_HERE.md) and the detailed
[evaluation guide](EVALUATION.md). The package is a framework-dependent .NET
10 x64 CLI. The runtime is required; the SDK is not required for ordinary
package use. Full physical scenario rehearsal is validated only on Ubuntu
with Linux-container Docker. Windows acceptance is limited to package-only
checks.
In command blocks, `proofshift` is shorthand for
`dotnet app/ProofShift.Cli.dll`, run from the extracted package root.

## Track A — Product demonstration

The packaged demonstrations use deterministic fixtures. From the extracted
package root:

```text
proofshift --help
proofshift version --json
proofshift demo list
proofshift demo describe <id>
proofshift demo preflight <id>
proofshift demo run <id>
proofshift demo reset <id>
```

The evaluator workflow includes these documented command forms:

```text
proofshift validate <config> --strict
proofshift snapshot <config>
proofshift verify-external <config> --checkpoint <checkpoint-id> --observation <label>
proofshift evidence <config> --run <verification-run-id>
proofshift report <config> --verification-run <verification-run-id>
proofshift compare <config> --before-verification <defective-verification-run-id> --after-verification <corrected-verification-run-id>
```

Choose a scenario from `demo list`. `demo describe` explains its accepted
synthetic outcomes and claim boundary. `demo preflight` checks prerequisites;
do not bypass a reported blocker. `demo run` executes the isolated packaged
scenario. Inspect the printed report paths and files under
`.proofshift/demos/<id>`. Preserve outputs before `demo reset <id>`; reset is
for repeating the demonstration from a clean workspace.

A defective result can intentionally be `NOT QUALIFIED`: the demonstration
successfully detects the seeded defects. For Pension, the defective state has
149 business discrepancies and is not qualified; the corrected shadow has
zero and is qualified. The false-Reverse example demonstrates that
`Verification: PASSED` does not mean `Recovery: QUALIFIED`.

The accepted physical demonstrations retain these outcomes:

| Scenario | Seeded findings | Corrected state |
|---|---:|---|
| Pension | 149 discrepancies | 0 discrepancies; `QUALIFIED` |
| Utility | 14 seeded; 26 findings | 0 corrected; `QUALIFIED` |
| Justice | 15 seeded; 24 findings | 0 corrected; `QUALIFIED` |
| HCM | 14 seeded; 25 findings | 0 corrected; `QUALIFIED` |
| FHIR | 9 seeded; 15 findings | 0 corrected; `QUALIFIED` |

These are configured synthetic scenario outcomes, not statements about a
real migration or complete domain coverage.

## Track B — Bring Your Own Migration

This track tests independent assurance around a migration performed by a
separate process. ProofShift does **not** perform the migration.

```text
discover
  → review/scaffold/import mappings
  → validate --strict
  → snapshot
  → stop or isolate the source
  → run the external migration separately
  → observe the target read-only with verify-external
  → inspect persisted evidence/report
  → correct the external migration separately
  → verify-external again
  → compare persisted verification runs
```

Follow the complete [BYOM workflow](BYOM_EVALUATION.md). Its normal command
sequence uses `discover`, `discovery diff`, `scaffold`, `mapping import`,
`validate --strict`, `explain`, `config show`, `snapshot`, `verify-external`,
`evidence`, `report --verification-run`, and
`compare --before-verification ... --after-verification ...`. Review mapping
suggestions; they are not approvals. Keep the migration tool's write-capable
credential separate from ProofShift's read-only target observer. Prove that
the source is unavailable after checkpointing and that ProofShift does not
change target state.

Persisted Evidence records the configured source accounting, target lineage,
rules, findings, and fingerprints. Reports and comparisons load persisted
state and do not rerun Verification. Store the package workspace or copy the
printed report/Evidence artifacts to your approved retention location. Never
include secrets or raw sensitive values in a handoff bundle.

External Verification is not a migration execution record. It does not
observe vendor execution lineage, mutate the target, perform Projection, or
assess Recovery. Verification-only results have no Recovery qualification.
`QUALIFIED` is available only in the distinct shadow dry-run path and only
for the configured checks; it is not production or business approval.

## Supported boundary and known limitations

- CLI-first product; framework-dependent .NET 10 x64 package.
- Windows evidence is package-only. Ubuntu evidence covers the packaged
  Docker evaluation workflow only; general Linux support is not claimed.
- No production migration execution and no external-target rollback.
- No web UI, hosted service, authentication, or role-based access control.
- No FHIR Bulk or generic REST connector.
- No automatic semantic inference.
- No regulatory or domain certification and no SLA.
- Technical pilot readiness is distinct from production readiness.
- New connectors, domain packs, or unsupported capabilities are not promised
  as part of a pilot.

## Distribution boundary

ProofShift remains proprietary. This evaluation does not authorize public
release, Git tagging, a GitHub Release, NuGet/container publication, signed
commercial release, paid trial, redistribution, or server bundling. Public
repository visibility is not an open-source license. Review `LICENSE` and
`THIRD-PARTY-NOTICES.md` shipped with the package.
