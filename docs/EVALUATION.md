# ProofShift Evaluation Kit

The PS-0.10G evaluation package is a binary-only, offline-after-build evaluation kit. It contains the published ProofShift CLI, the compiled MTP fixture module used by the demo catalog, selected synthetic sample projects, demo manifests, and third-party notices. It does not contain repository source, customer data, credentials, database images, or production migration tooling.

## Requirements

- Windows x64 is the only packaged evaluation path claimed by PS-0.10G. Clean Windows runner acceptance is pending; Linux packaged evaluation is not claimed. Ordinary Linux CI is not product-support evidence.
- .NET 10 SDK/runtime and a Docker-compatible runtime. The test fixtures pull their pinned SQL Server/PostgreSQL containers when a scenario runs; no container image is redistributed.
- Network access on first demo run may be needed by Docker to pull those test images. The package itself contains the NuGet runtime dependencies for the published CLI and fixture module.

## Run

Set the archive path to the ZIP you received and extract it to a new, empty path. This example deliberately includes a space and stays short enough for the physical fixtures; deeply nested paths can exceed SQLite's legacy path limit.

```powershell
$archive = Join-Path $env:USERPROFILE "Downloads\ProofShift-Evaluation-PS-0.10G.zip"
$extractRoot = "C:\ProofShift Evaluation"
if (Test-Path -LiteralPath $extractRoot) { throw "Choose a new empty extraction directory." }
Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
$packageRoot = Join-Path $extractRoot "ProofShift-Evaluation-PS-0.10G"

Push-Location $packageRoot
try {
	dotnet .\app\ProofShift.Cli.dll --version
	dotnet .\app\ProofShift.Cli.dll demo help
	dotnet .\app\ProofShift.Cli.dll demo list
	dotnet .\app\ProofShift.Cli.dll demo describe pension
	dotnet .\app\ProofShift.Cli.dll demo preflight pension
	dotnet .\app\ProofShift.Cli.dll demo run pension

	$placeholderReferences = @(
	    "PS05_SQL_CONNECTION", "PS05_POSTGRES_CONNECTION", "PS05_SOURCE_FILES",
	    "PS05_SOURCE_CSV", "PS05_SHADOW_FILES", "PS09_CORPUS_SOURCE_CONNECTION",
	    "PS09_CORPUS_CSV_ROOT", "PS09_CORPUS_SOURCE_FILES_ROOT",
	    "PS09_CORPUS_TARGET_CONNECTION", "PS09_CORPUS_TARGET_FILES_ROOT"
	)
	foreach ($name in $placeholderReferences) { Set-Item -Path "Env:$name" -Value "synthetic-placeholder" }

	dotnet .\app\ProofShift.Cli.dll validate scenarios/pension-modernization/ps010b/proofshift.yaml --json
	dotnet .\app\ProofShift.Cli.dll validate scenarios/justice-case-management/proofshift.yaml --json
	dotnet .\app\ProofShift.Cli.dll validate scenarios/hcm-erp-modernization/proofshift.yaml --json
	dotnet .\app\ProofShift.Cli.dll validate scenarios/pension-modernization/ps05/proofshift.yaml --json
	dotnet .\app\ProofShift.Cli.dll validate scenarios/pension-modernization/ps010b/proofshift-false-reverse.yaml --json
}
finally {
	Pop-Location
}
```

Use `utility`, `justice`, `hcm`, or `fhir` in place of `pension` for a compact cross-domain scenario. Every run is non-interactive. The default text output prints the main findings/qualification facts and paths to human-readable and JSON artifacts; `--json` emits the complete machine-readable run result.

The package includes five root project configurations shown above. The synthetic placeholder references are only to satisfy configuration parsing; they are not credentials, are not used to connect to systems, and do not configure runtime connectors. The false-Reverse configuration may emit the existing `PSRULE012` source-field warning while remaining valid.

The run prints whether the expected defective state was observed. `NOT QUALIFIED` for the seeded defective observation is a successful demo result; the corrected shadow must report `QUALIFIED`. Inspect the printed report paths and machine-readable stage results under `.proofshift/demos/<id>`.

To inspect and repeat a run, use the same explicit package root. Copy any artifacts you need first; reset removes only the matching marker-owned workspace, and run refuses to overwrite one:

```powershell
Push-Location $packageRoot
try {
	dotnet .\app\ProofShift.Cli.dll demo reset pension
	dotnet .\app\ProofShift.Cli.dll demo run pension
}
finally {
	Pop-Location
}
```

Troubleshooting:

- If preflight reports Docker unavailable, start a Docker-compatible runtime, confirm `docker info` succeeds, then rerun `demo preflight <id>`.
- If preflight reports an occupied required port, stop the process using that port and repeat preflight; do not bypass the check.
- If the CLI path is missing, confirm `$packageRoot` points to the extracted `ProofShift-Evaluation-PS-0.10G` directory and that `app\ProofShift.Cli.dll` exists there.
- If a run says the workspace already exists, inspect and retain any wanted reports, then use `demo reset <id>` from that package root before retrying.
- Windows may deny symbolic-link creation to unprivileged test processes. The three local test-suite skips for that capability are not demo failures; the evaluator demo itself does not require creating symlinks.

For binary fixture stages, the result records elapsed fixture-process time and sampled peak working set. This is the MTP test process only; Docker container memory and whole-system peak memory are not included. Treat the measurements as host-specific diagnostics, not resource guarantees or SLAs.

### Windows Package Rehearsal Measurements

Measured from the extracted binary-only package on Windows with .NET SDK/runtime 10.0.303 and Docker Engine 29.6.1. The sampled working set is the MTP fixture process only and excludes the SQL Server/PostgreSQL containers; sampling is every 100 ms, so brief peaks can be missed.

| Scenario | Fixture process time | Sampled peak working set |
|---|---:|---:|
| Pension | 136,678 ms | 218,161,152 bytes |
| Utility | 66,094 ms | 200,216,576 bytes |
| Justice | 79,014 ms | 191,954,944 bytes |
| HCM | 323,922 ms | 218,202,112 bytes |
| FHIR-style | 89,085 ms | 181,067,776 bytes |

These are diagnostics from one Windows host, not fresh-hardware guarantees or container/whole-system sizing guidance. They are not fresh-machine evidence. CPU utilization, whole-run disk high-water, and Docker container memory were not measured, so no sizing claim is made for those dimensions. Linux packaged evaluation remains unclaimed.

## Assurance Boundary

All demo data is synthetic. External target observations are independently populated and read-only to Verification. A separate corrected shadow run exercises Projection and Recovery. ProofShift does not perform the external migration, and technical qualification is not regulatory, legal, actuarial, clinical, payroll, or business approval. Per-pack exclusions are shown by `demo describe <id>` and in each report.

The Pension flagship asserts 8,495 source checkpoint artifacts, 8,595 corrected target artifacts, 149 defective findings, and corrected qualification. Its false-Reverse example passes Verification with zero Verification failures while Recovery rejects two unsafe Reverse declarations. Utility, Justice, HCM, and selected FHIR-style kits use the accepted PS-0.10F physical fixtures and the exact counts listed in `demo describe`.

## Package Provenance

The ZIP has a deterministic entry order and timestamps. `package-manifest.json` records the source Git revision, content digest, entry point, and allowlist/denylist; the adjacent `.sha256` file verifies the archive. `THIRD-PARTY-NOTICES.md` and `THIRD-PARTY-LICENSES/` are generated from the resolved CLI and fixture-module NuGet dependency graphs. Review the published package terms before redistribution; the notice generator does not make a legal determination.

To build the package from a development checkout, use Windows PowerShell 5.1 or newer:

```powershell
./scripts/package-evaluation.ps1 -OutputFile "$env:TEMP/ProofShift-Evaluation-PS-0.10G.zip"
```

## Product Limits

- PS-0.10G claims only the packaged Windows evaluation path; Linux packaged evaluation is not validated or claimed.
- There is no web UI or hosted service, no production migration execution or rollback, no FHIR Bulk support, and no generic REST connector.
- Demo data is deterministic and synthetic. Domain packs demonstrate representative configured assurance, not complete industry coverage or legal, regulatory, actuarial, clinical, payroll, or other certification.
- The evaluation package is not authorization for paid trials or redistribution. The repository license is all-rights-reserved; commercial use and redistribution require separate authorization and review.