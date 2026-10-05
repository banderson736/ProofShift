[CmdletBinding()]
param(
    [ValidateSet('fast', 'medium')]
    [string]$Scale = 'fast',
    [string]$OutputDirectory
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:TEMP ("ProofShift-PS010A-{0}-{1}" -f $Scale, [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$previousScale = $env:PS010A_BENCHMARK_SCALE
$previousDirectory = $env:PS09_INTEGRATED_RUN_DIRECTORY
$exitCode = 1
try {
    $env:PS010A_BENCHMARK_SCALE = $Scale
    $env:PS09_INTEGRATED_RUN_DIRECTORY = Join-Path $OutputDirectory 'integrated-assurance'
    Push-Location $repositoryRoot
    try {
        dotnet test --project tests/ProofShift.EndToEnd.Tests/ProofShift.EndToEnd.Tests.csproj `
            --filter-class ProofShift.EndToEnd.Tests.PensionExternalCorpusVerificationTests --no-restore
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PS010A_BENCHMARK_SCALE = $previousScale
    $env:PS09_INTEGRATED_RUN_DIRECTORY = $previousDirectory
}

if ($exitCode -ne 0) {
    exit $exitCode
}

$artifactRoot = Join-Path $OutputDirectory 'integrated-assurance'
$summaryPath = Join-Path $artifactRoot 'demo-summary.json'
$performancePath = Join-Path $artifactRoot 'performance-run.json'
$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$performance = Get-Content -LiteralPath $performancePath -Raw | ConvertFrom-Json

function Measure-DirectoryBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return [long]0 }
    return [long]((Get-ChildItem -LiteralPath $Path -File -Recurse | Measure-Object -Property Length -Sum).Sum)
}

$summary | Add-Member -NotePropertyName sourceByteCount -NotePropertyValue ([long]((Measure-DirectoryBytes (Join-Path $artifactRoot 'source-csv')) + (Measure-DirectoryBytes (Join-Path $artifactRoot 'source-files')))) -Force
$summary | Add-Member -NotePropertyName checkpointBytes -NotePropertyValue (Measure-DirectoryBytes (Join-Path $artifactRoot 'checkpoints')) -Force
$ledgerBytes = [long]((Get-ChildItem -LiteralPath $artifactRoot -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ledger.sqlite*' } | Measure-Object -Property Length -Sum).Sum)
$summary | Add-Member -NotePropertyName verificationLedgerBytes -NotePropertyValue $ledgerBytes -Force
$evidenceBytes = [long]((Get-ChildItem -LiteralPath $artifactRoot -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'evidence.ndjson' } | Measure-Object -Property Length -Sum).Sum)
$summary | Add-Member -NotePropertyName evidenceArtifactBytes -NotePropertyValue $evidenceBytes -Force
$summary | Add-Member -NotePropertyName recoveryArtifactBytes -NotePropertyValue (Measure-DirectoryBytes (Join-Path $artifactRoot '.proofshift\recovery')) -Force
$summary | Add-Member -NotePropertyName managedHeapBytesAtCompletion -NotePropertyValue $performance.managedHeapBytesAtCompletion -Force
$summary | Add-Member -NotePropertyName maximumObservedWorkingSetBytes -NotePropertyValue $performance.maximumObservedWorkingSetBytes -Force
$summary | Add-Member -NotePropertyName temporaryWorkspacePeakBytes -NotePropertyValue $performance.temporaryWorkspacePeakBytes -Force
$summaryJson = $summary | ConvertTo-Json -Depth 100
[System.IO.File]::WriteAllText($summaryPath, $summaryJson, [System.Text.UTF8Encoding]::new($false))

Write-Output "Benchmark artifacts: $artifactRoot"
