# Start Here — ProofShift Evaluation Kit

ProofShift is a CLI-first migration assurance tool. It checks whether a
configured source-to-target migration preserves the artifacts and meaning
described by your project rules, and records the result as persistent Evidence.
It observes and reports; it is not a general-purpose migration executor.

## Before you begin

- Use a 64-bit Windows or Linux x64 package with the compatible .NET 10
  runtime. The package is framework-dependent; ordinary CLI use does not
  require the .NET SDK.
- The package identity, source revision, platform, dependency-lock digest, and
  package content digest are recorded in `package-manifest.json` and the
  adjacent provenance and SHA-256 files. Confirm the archive name and verify
  the adjacent `<archive>.sha256` value before extraction:

  ```powershell
  $archive = Join-Path $env:USERPROFILE "Downloads\ProofShift-Evaluation-0.1.0-preview.1-win-x64.zip"
  $expected = ((Get-Content "$archive.sha256" -Raw) -split '\s+')[0].ToLowerInvariant()
  $actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $expected) { throw "Package checksum does not match." }
  "Package checksum verified: $actual"
  ```

  ```bash
  archive="$HOME/Downloads/ProofShift-Evaluation-0.1.0-preview.1-linux-x64.zip"
  (cd "$(dirname "$archive")" && sha256sum --check "$(basename "$archive").sha256")
  ```
- Extract to a new directory. Keep generated project state, reports, and
  Evidence in the package workspace or another location you control.

## Try a demonstration

From the extracted package root:

```text
dotnet app/ProofShift.Cli.dll --help
dotnet app/ProofShift.Cli.dll version --json
dotnet app/ProofShift.Cli.dll demo list
dotnet app/ProofShift.Cli.dll demo describe pension
dotnet app/ProofShift.Cli.dll demo preflight pension
dotnet app/ProofShift.Cli.dll demo run pension
```

The defective observation is intentionally expected to report findings and
`NOT QUALIFIED`; detecting the seeded defects is a successful demonstration.
The corrected shadow result should be `QUALIFIED` under the configured rules.
Reports and generated files are written under `.proofshift`; the CLI prints
the artifact paths. Use `demo reset <id>` before a fresh repeat, after keeping
any output you want to retain.

## Evaluate a migration you ran elsewhere

Use the Bring Your Own Migration workflow to discover and checkpoint a
synthetic/non-production source, review mappings, run your migration with a
separate external tool, then observe the target using read-only credentials.
ProofShift does not perform that migration. Use `verify-external` to observe
the target read-only; it persists Evidence that you can report or compare.
Follow the [BYOM workflow](docs/BYOM_EVALUATION.md) for the complete sequence.
It does not create Projection artifacts or assess or qualify Recovery.

`QUALIFIED` is an outcome of the separate shadow dry-run workflow: configured
Verification and Recovery checks passed for that run. It is not a general
business, regulatory, production, or rollback approval. For external
Verification, qualification is unavailable.

Read [the evaluation guide](docs/EVALUATION.md), [the BYOM guide](docs/BYOM_EVALUATION.md),
[the pilot evaluator guide](docs/PILOT_EVALUATOR_GUIDE.md), and
[the pilot intake checklist](docs/PILOT_INTAKE_CHECKLIST.md) before arranging
a controlled evaluation. Also review the [license](LICENSE) and
[third-party notices](THIRD-PARTY-NOTICES.md).

## Limits

The supported product is a CLI-first, framework-dependent .NET 10 x64
evaluation package. Windows acceptance is package-only. Physical scenario
acceptance is limited to the packaged Docker evaluation on Ubuntu; this is not
a general Linux-support claim. There is no web UI, hosted service,
authentication/RBAC, generic REST connector, or FHIR Bulk support. ProofShift
does not execute production migrations or external-target rollback, perform
automatic semantic inference, or provide regulatory/domain certification or
deployment certification, or an SLA. Technical pilot readiness is not
production readiness.

This proprietary preview package is not authorization for a public release,
redistribution, server bundling, or paid trial. Public repository visibility
does not make ProofShift open source.
