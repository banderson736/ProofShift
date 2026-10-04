param(
    [string]$OutputDirectory = ".proofshift/pension-demo",
    [switch]$WithDocker
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$absoluteOutputDirectory = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
Push-Location $repositoryRoot
try {
    Write-Host "Generating versioned synthetic pension data at $absoluteOutputDirectory"
    dotnet run --project src/ProofShift.Cli -- demo generate $absoluteOutputDirectory --scale fast --seed 20261003
    if ($LASTEXITCODE -ne 0) { throw "Pension demo generation failed with exit code $LASTEXITCODE." }

    Write-Host "Validating the exact generator and defect manifest"
    dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.PensionSyntheticDataTests --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Pension generator tests failed with exit code $LASTEXITCODE." }

    Write-Host "Running pension rules against the independent target fixture"
    dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.PensionSemanticRuleTests --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Pension semantic-rule tests failed with exit code $LASTEXITCODE." }

    if ($WithDocker) {
        Write-Host "Running Docker-backed CLI, dry-run report, and recovery scenarios"
        dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj --filter-class ProofShift.EndToEnd.Tests.ShadowProjectionPensionIntegrationTests --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Docker-backed pension scenarios failed with exit code $LASTEXITCODE." }
    }

    Write-Host "Demo data and evidence checks completed. Inspect demo-manifest.json under $absoluteOutputDirectory."
}
finally {
    Pop-Location
}
