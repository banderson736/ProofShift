param(
    [string]$OutputDirectory = ".proofshift/pension-demo",
    [bool]$WithDocker = $true
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repositoryRoot "src/ProofShift.Cli/ProofShift.Cli.csproj"
$testProject = Join-Path $repositoryRoot "tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj"
$absoluteOutputDirectory = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
Push-Location $repositoryRoot
try {
    Write-Host "Generating versioned synthetic pension data at $absoluteOutputDirectory"
    & dotnet run --project $cliProject -- demo generate $absoluteOutputDirectory --scale fast --seed 20261003
    if ($LASTEXITCODE -ne 0) { throw "Pension demo generation failed with exit code $LASTEXITCODE." }

    Write-Host "Validating the exact generator and defect manifest"
    & dotnet test --project $testProject --filter-class ProofShift.EndToEnd.Tests.PensionSyntheticDataTests --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Pension generator tests failed with exit code $LASTEXITCODE." }

    Write-Host "Running pension rules against the independent target fixture"
    & dotnet test --project $testProject --filter-class ProofShift.EndToEnd.Tests.PensionSemanticRuleTests --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Pension semantic-rule tests failed with exit code $LASTEXITCODE." }

    if ($WithDocker) {
        $integratedDirectory = Join-Path $absoluteOutputDirectory "integrated-assurance"
        if ((Test-Path $integratedDirectory -PathType Container) -and
            ((Get-ChildItem -Force $integratedDirectory | Measure-Object).Count -gt 0)) {
            throw "Integrated assurance directory is not empty; choose a new OutputDirectory: $integratedDirectory"
        }
        New-Item -ItemType Directory -Force -Path $integratedDirectory | Out-Null
        $previousIntegratedDirectory = $env:PS09_INTEGRATED_RUN_DIRECTORY
        try {
            $env:PS09_INTEGRATED_RUN_DIRECTORY = $integratedDirectory
            Write-Host "Running physical SQL Server/CSV/filesystem to PostgreSQL/filesystem assurance flow"
            & dotnet test --project $testProject --filter-class ProofShift.EndToEnd.Tests.PensionExternalCorpusVerificationTests --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Integrated pension assurance test failed with exit code $LASTEXITCODE." }
        }
        finally {
            if ($null -eq $previousIntegratedDirectory) {
                Remove-Item Env:PS09_INTEGRATED_RUN_DIRECTORY -ErrorAction SilentlyContinue
            } else {
                $env:PS09_INTEGRATED_RUN_DIRECTORY = $previousIntegratedDirectory
            }
        }

        $summaryPath = Join-Path $integratedDirectory "demo-summary.json"
        if (-not (Test-Path $summaryPath -PathType Leaf)) { throw "Integrated assurance summary is missing: $summaryPath" }
        $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
        Push-Location $integratedDirectory
        try {
            Write-Host "Reporting defective run $($summary.defectiveDryRunId)"
            & dotnet run --project $cliProject --no-build -- report $summary.defectiveDryRunId
            if ($LASTEXITCODE -ne 0) { throw "Defective pension report failed with exit code $LASTEXITCODE." }
            & dotnet run --project $cliProject --no-build -- report $summary.defectiveDryRunId --json
            if ($LASTEXITCODE -ne 0) { throw "Defective pension machine report failed with exit code $LASTEXITCODE." }

            Write-Host "Reporting corrected run $($summary.correctedDryRunId)"
            & dotnet run --project $cliProject --no-build -- report $summary.correctedDryRunId
            if ($LASTEXITCODE -ne 0) { throw "Corrected pension report failed with exit code $LASTEXITCODE." }
            & dotnet run --project $cliProject --no-build -- report $summary.correctedDryRunId --json
            if ($LASTEXITCODE -ne 0) { throw "Corrected pension machine report failed with exit code $LASTEXITCODE." }

            Write-Host "Comparing persisted defective and corrected runs"
            & dotnet run --project $cliProject --no-build -- compare $summary.defectiveDryRunId $summary.correctedDryRunId
            if ($LASTEXITCODE -ne 0) { throw "Pension run comparison failed with exit code $LASTEXITCODE." }
            & dotnet run --project $cliProject --no-build -- compare $summary.defectiveDryRunId $summary.correctedDryRunId --json
            if ($LASTEXITCODE -ne 0) { throw "Pension machine comparison failed with exit code $LASTEXITCODE." }
        }
        finally {
            Pop-Location
        }
    }

    if ($WithDocker) {
        Write-Host "Complete fast pension assurance demo completed. Persisted runs and reports are under $integratedDirectory/.proofshift."
    } else {
        Write-Host "Generator and semantic-rule checks completed. Run with -WithDocker:$true for the complete physical assurance flow."
    }
}
finally {
    Pop-Location
}
