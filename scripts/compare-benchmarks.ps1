[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After
)

function Read-PerformanceRun([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Performance JSON was not found: $fullPath"
    }
    $run = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($run.format -ne 'proofshift-performance-run-v1') {
        throw "Unsupported performance format in $fullPath"
    }
    return $run
}

function Get-StageTotals($Run) {
    $totals = @{}
    foreach ($group in ($Run.stages | Group-Object -Property kind,name,connectorId,nodeKey,edgeName,ruleId)) {
        $stage = $group.Group[0]
        $key = @($stage.kind, $stage.name, $stage.connectorId, $stage.nodeKey, $stage.edgeName, $stage.ruleId) -join '|'
        $totals[$key] = [pscustomobject]@{
            Kind = $stage.kind
            Name = $stage.name
            ConnectorId = $stage.connectorId
            NodeKey = $stage.nodeKey
            EdgeName = $stage.edgeName
            RuleId = $stage.ruleId
            Runs = $group.Count
            ElapsedMicroseconds = [long](($group.Group | Measure-Object -Property elapsedMicroseconds -Sum).Sum)
            Artifacts = [long](($group.Group | Measure-Object -Property artifactCount -Sum).Sum)
            Bytes = [long](($group.Group | Measure-Object -Property byteCount -Sum).Sum)
        }
    }
    return $totals
}

$beforeRun = Read-PerformanceRun $Before
$afterRun = Read-PerformanceRun $After
$beforeStages = Get-StageTotals $beforeRun
$afterStages = Get-StageTotals $afterRun
$stageKeys = @($beforeStages.Keys + $afterStages.Keys | Sort-Object -Unique)
$stageComparison = foreach ($key in $stageKeys) {
    $left = $beforeStages[$key]
    $right = $afterStages[$key]
    $elapsedBefore = if ($null -eq $left) { 0L } else { $left.ElapsedMicroseconds }
    $elapsedAfter = if ($null -eq $right) { 0L } else { $right.ElapsedMicroseconds }
    $artifactsBefore = if ($null -eq $left) { 0L } else { $left.Artifacts }
    $artifactsAfter = if ($null -eq $right) { 0L } else { $right.Artifacts }
    $bytesBefore = if ($null -eq $left) { 0L } else { $left.Bytes }
    $bytesAfter = if ($null -eq $right) { 0L } else { $right.Bytes }
    $elapsed = if ($right) { $right.ElapsedMicroseconds } else { $left.ElapsedMicroseconds }
    $artifacts = if ($right) { $right.Artifacts } else { $left.Artifacts }
    [pscustomobject]@{
        Key = $key
        Kind = if ($right) { $right.Kind } else { $left.Kind }
        Name = if ($right) { $right.Name } else { $left.Name }
        BeforeMilliseconds = [math]::Round($elapsedBefore / 1000, 3)
        AfterMilliseconds = [math]::Round($elapsedAfter / 1000, 3)
        DeltaMilliseconds = [math]::Round(($elapsedAfter - $elapsedBefore) / 1000, 3)
        BeforeArtifacts = $artifactsBefore
        AfterArtifacts = $artifactsAfter
        BeforeBytes = $bytesBefore
        AfterBytes = $bytesAfter
        AfterArtifactsPerSecond = if ($elapsed -gt 0) { [math]::Round($artifacts / ($elapsed / 1000000), 2) } else { $null }
    }
}

$result = [pscustomobject]@{
    format = 'proofshift-performance-comparison-v1'
    before = [pscustomobject]@{
        scenario = $beforeRun.scenario
        elapsedMilliseconds = [math]::Round($beforeRun.elapsedMicroseconds / 1000, 3)
        peakWorkingSetBytes = $beforeRun.maximumObservedWorkingSetBytes
        managedHeapBytesAtCompletion = $beforeRun.managedHeapBytesAtCompletion
        temporaryWorkspacePeakBytes = $beforeRun.temporaryWorkspacePeakBytes
    }
    after = [pscustomobject]@{
        scenario = $afterRun.scenario
        elapsedMilliseconds = [math]::Round($afterRun.elapsedMicroseconds / 1000, 3)
        peakWorkingSetBytes = $afterRun.maximumObservedWorkingSetBytes
        managedHeapBytesAtCompletion = $afterRun.managedHeapBytesAtCompletion
        temporaryWorkspacePeakBytes = $afterRun.temporaryWorkspacePeakBytes
    }
    stages = @($stageComparison)
}

$result | ConvertTo-Json -Depth 6
