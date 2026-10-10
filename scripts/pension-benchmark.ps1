[CmdletBinding()]
param(
    [ValidateSet('fast', 'probe-50k', 'probe-100k', 'probe-200k', 'medium', 'large-acceptance')]
    [string]$Scale = 'fast',
    [string]$OutputDirectory,
    [string]$CapacityEstimatePath,
    [ValidateSet(1, 4, 8)]
    [int]$PartitionCount = 1,
    [ValidateSet(1, 2, 4, 6, 8)]
    [int]$PartitionWorkers = 1,
    [ValidateSet('GlobalRuleReference', 'PartitionLocalExperimental')]
    [string]$VerificationMode = 'GlobalRuleReference'
)

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$requestedOutputDirectory = $OutputDirectory
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ($Scale -eq 'large-acceptance') {
        throw 'Large acceptance requires an explicit short output directory and a passing scratch-capacity estimate.'
    }
    $OutputDirectory = Join-Path $env:TEMP ("ProofShift-PS010A-{0}-{1}" -f $Scale, [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($Scale -eq 'large-acceptance') {
    if ([string]::IsNullOrWhiteSpace($CapacityEstimatePath) -or
        -not (Test-Path -LiteralPath $CapacityEstimatePath -PathType Leaf)) {
        throw 'Large acceptance requires -CapacityEstimatePath from scripts/estimate-pension-scratch.ps1.'
    }
    $capacityEstimate = Get-Content -LiteralPath $CapacityEstimatePath -Raw | ConvertFrom-Json
    if ($capacityEstimate.schema -ne 'proofshift-ps010d-scratch-capacity-estimate-v1' -or
        -not $capacityEstimate.capacity.estimatePassed -or
        $capacityEstimate.workload.targetGeneratedRecords -ne 1500000 -or
        -not [string]::Equals([System.IO.Path]::GetFullPath($capacityEstimate.output.outputDirectory),
            $OutputDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Large acceptance requires a passing 1.5M estimate bound to this exact output directory.'
    }
    if ($OutputDirectory.Length -gt 80) { throw 'Large acceptance output directory must be no longer than 80 characters.' }
    $outputVolume = [System.IO.Path]::GetPathRoot($OutputDirectory)
    $freeBytesBeforeRun = [long]([System.IO.DriveInfo]::new($outputVolume).AvailableFreeSpace)
    if ([long]$capacityEstimate.capacity.estimatedRequiredScratchBytes -gt $freeBytesBeforeRun) {
        throw "Large scratch preflight no longer fits: required $($capacityEstimate.capacity.estimatedRequiredScratchBytes) B, free $freeBytesBeforeRun B."
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($CapacityEstimatePath)) {
    throw '-CapacityEstimatePath is only valid for -Scale large-acceptance.'
}

$previousScale = $env:PS010A_BENCHMARK_SCALE
$previousDirectory = $env:PS09_INTEGRATED_RUN_DIRECTORY
$previousPartitions = $env:PS010D_PARTITION_COUNT
$previousExecutionMode = $env:PS010D_VERIFICATION_EXECUTION_MODE
$previousPartitionWorkers = $env:PS010D_PARTITION_WORKERS
$exitCode = 1
try {
    $env:PS010A_BENCHMARK_SCALE = $Scale
    $env:PS09_INTEGRATED_RUN_DIRECTORY = Join-Path $OutputDirectory 'integrated-assurance'
    $env:PS010D_PARTITION_COUNT = $PartitionCount.ToString()
    $env:PS010D_VERIFICATION_EXECUTION_MODE = $VerificationMode
    $env:PS010D_PARTITION_WORKERS = $PartitionWorkers.ToString()
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
    $env:PS010D_PARTITION_COUNT = $previousPartitions
    $env:PS010D_VERIFICATION_EXECUTION_MODE = $previousExecutionMode
    $env:PS010D_PARTITION_WORKERS = $previousPartitionWorkers
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
