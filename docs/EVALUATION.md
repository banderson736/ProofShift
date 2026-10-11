# ProofShift Evaluation Kit

The PS-0.10G evaluation package is a binary-only, offline-after-build evaluation kit. Separate Windows x64 and Linux x64 archives are built with `scripts/package-evaluation.ps1` on their target operating systems from the same candidate commit. Each contains the published ProofShift CLI, the compiled MTP fixture module used by the demo catalog, selected synthetic sample projects, demo manifests, and third-party notices. The package manifest records its target platform and source revision. Packages do not contain repository source, customer data, credentials, database images, or production migration tooling.

## Clean CI Acceptance

The acceptance workflow uses two fresh standard GitHub-hosted jobs against the same candidate SHA:

- `windows-latest` builds the Windows package twice, checks byte-identical archives, extracts outside the checkout into a path with spaces, and uses only the packaged CLI to check help/version/demo discovery/descriptions and all shipped sample configurations. Root `--help` currently prints usage with the CLI's existing exit code 2; smoke validates the usage text and records that code, while `demo --help` must exit successfully. It validates package contents and license notices. It does not require Docker or run physical demos.
- `ubuntu-latest` verifies Linux-container Docker, builds the Linux package twice, extracts it outside the checkout into a path with spaces, and runs the full Docker-backed packaged Pension, Utility, Justice, HCM, and FHIR scenarios. It checks exact scenario outcomes, reports, and Pension reset/repeat semantic equivalence.

The Windows result proves the Windows package path only; it does not prove that the Docker-backed demo stack runs on Windows. The Ubuntu result proves the packaged Docker-backed evaluation workflow on Ubuntu; it is not a general-purpose Linux product-support claim. Both campaign jobs are pending until the split workflow passes on one exact candidate commit.

## Requirements

- The Windows package requires the .NET 10 runtime. Docker is not required for its clean Windows package-only acceptance check.
- The Linux packaged-demo workflow requires the .NET 10 SDK/runtime and Linux-container Docker. Running full Docker-backed demos manually on Windows also requires a compatible local Docker setup, but that path is not part of the Windows clean-runner acceptance claim.
- Network access on the first Linux demo run may be needed to pull the synthetic SQL Server/PostgreSQL test images; no image is redistributed. The package contains the NuGet runtime dependencies for the published CLI and fixture module.

## Run

Set the archive path to the ZIP you received and extract it to a new, empty path. This example deliberately includes a space and stays short enough for the physical fixtures; deeply nested paths can exceed SQLite's legacy path limit.
These PowerShell commands show an operator running the full demos on a Windows machine with Docker. The GitHub-hosted Windows acceptance job stops after package-only checks; the clean Docker-backed campaign runs on Ubuntu.

```powershell
$archive = Join-Path $env:USERPROFILE "Downloads\ProofShift-Evaluation-PS-0.10G-win-x64.zip"
$extractRoot = "C:\ProofShift Evaluation"
if (Test-Path -LiteralPath $extractRoot) { throw "Choose a new empty extraction directory." }
Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
$packageRoot = Join-Path $extractRoot "ProofShift-Evaluation-PS-0.10G"

Push-Location $packageRoot
try {
	dotnet .\app\ProofShift.Cli.dll --version
	dotnet .\app\ProofShift.Cli.dll --help
	dotnet .\app\ProofShift.Cli.dll demo --help
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

For the Linux package, derive the package root explicitly after extracting the platform-specific ZIP outside the checkout. The following Bash example uses only the extracted CLI and packaged sample paths:

```bash
archive="$HOME/Downloads/ProofShift-Evaluation-PS-0.10G-linux-x64.zip"
extract_root="/tmp/ProofShift Evaluation"
test ! -e "$extract_root"
mkdir -p "$extract_root"
unzip -q "$archive" -d "$extract_root"
package_root="$extract_root/ProofShift-Evaluation-PS-0.10G"
proofshift="$package_root/app/ProofShift.Cli.dll"
cd "$package_root"

dotnet "$proofshift" --version
dotnet "$proofshift" --help
dotnet "$proofshift" demo --help
dotnet "$proofshift" demo list
for demo in pension utility justice hcm fhir; do
	dotnet "$proofshift" demo describe "$demo"
done

export PS05_SQL_CONNECTION=synthetic-placeholder
export PS05_POSTGRES_CONNECTION=synthetic-placeholder
export PS05_SOURCE_FILES=synthetic-placeholder
export PS05_SOURCE_CSV=synthetic-placeholder
export PS05_SHADOW_FILES=synthetic-placeholder
export PS09_CORPUS_SOURCE_CONNECTION=synthetic-placeholder
export PS09_CORPUS_CSV_ROOT=synthetic-placeholder
export PS09_CORPUS_SOURCE_FILES_ROOT=synthetic-placeholder
export PS09_CORPUS_TARGET_CONNECTION=synthetic-placeholder
export PS09_CORPUS_TARGET_FILES_ROOT=synthetic-placeholder

for sample in \
	scenarios/pension-modernization/ps010b/proofshift.yaml \
	scenarios/justice-case-management/proofshift.yaml \
	scenarios/hcm-erp-modernization/proofshift.yaml \
	scenarios/pension-modernization/ps05/proofshift.yaml \
	scenarios/pension-modernization/ps010b/proofshift-false-reverse.yaml; do
	dotnet "$proofshift" validate "$package_root/$sample" --json
done

dotnet "$proofshift" demo run pension
dotnet "$proofshift" demo reset pension
dotnet "$proofshift" demo run pension
for demo in utility justice hcm fhir; do
	dotnet "$proofshift" demo run "$demo"
done
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

These are diagnostics from one Windows host, not fresh-hardware guarantees or container/whole-system sizing guidance. They are not fresh-machine evidence. CPU utilization, whole-run disk high-water, and Docker container memory were not measured, so no sizing claim is made for those dimensions. The split Windows/Ubuntu CI campaign remains pending; general-purpose Linux product support remains unclaimed.

The split CI jobs record the package filename, size, SHA-256, candidate SHA, runner OS/CPU/memory/free disk, Docker engine details, and per-fixture runtime/sampled peak working set where available. Ubuntu records Docker disk usage after demo execution. These are diagnostics from the actual acceptance runners, not SLAs or production-sizing claims; Docker metrics are not reported for the Windows package-only job.

## Assurance Boundary

All demo data is synthetic. External target observations are independently populated and read-only to Verification. A separate corrected shadow run exercises Projection and Recovery. ProofShift does not perform the external migration, and technical qualification is not regulatory, legal, actuarial, clinical, payroll, or business approval. Per-pack exclusions are shown by `demo describe <id>` and in each report.

The Pension flagship asserts 8,495 source checkpoint artifacts, 8,595 corrected target artifacts, 149 defective findings, and corrected qualification. Its false-Reverse example passes Verification with zero Verification failures while Recovery rejects two unsafe Reverse declarations. Utility, Justice, HCM, and selected FHIR-style kits use the accepted PS-0.10F physical fixtures and the exact counts listed in `demo describe`.

## Package Provenance

The ZIP has a deterministic entry order and timestamps. `package-manifest.json` records the source Git revision, content digest, entry point, and allowlist/denylist; the adjacent `.sha256` file verifies the archive. `THIRD-PARTY-NOTICES.md` and `THIRD-PARTY-LICENSES/` are generated from the resolved CLI and fixture-module NuGet dependency graphs. Review the published package terms before redistribution; the notice generator does not make a legal determination.

To build the platform package from a development checkout, run the existing builder on the target OS. Windows PowerShell 5.1 or newer builds Windows x64; PowerShell 7 builds Linux x64:

```powershell
./scripts/package-evaluation.ps1 -OutputFile "$env:TEMP/ProofShift-Evaluation-PS-0.10G-win-x64.zip"
```

```bash
pwsh -File scripts/package-evaluation.ps1 -OutputFile "/tmp/ProofShift-Evaluation-PS-0.10G-linux-x64.zip"
```

The package manifest records `targetPlatform: win-x64` or `linux-x64`. The platform archives have independent SHA-256 values; reproducibility is checked within each platform, not between platforms.

## Product Limits

- PS-0.10G's clean acceptance design separately validates the Windows package on Windows and the Docker-backed packaged evaluation workflow on Ubuntu. This does not claim the five-domain Docker stack was validated on Windows or that ProofShift has general Linux product support.
- There is no web UI or hosted service, no production migration execution or rollback, no FHIR Bulk support, and no generic REST connector.
- Demo data is deterministic and synthetic. Domain packs demonstrate representative configured assurance, not complete industry coverage or legal, regulatory, actuarial, clinical, payroll, or other certification.
- The evaluation package is not authorization for paid trials or redistribution. The repository license is all-rights-reserved; commercial use and redistribution require separate authorization and review.