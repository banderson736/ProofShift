[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HundredKArtifactDirectory,
    [Parameter(Mandatory)]
    [string]$TwoHundredKArtifactDirectory,
    [Parameter(Mandatory)]
    [string]$MediumArtifactDirectory,
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [Parameter(Mandatory)]
    [string]$EstimatePath,
    [ValidateRange(1, [long]::MaxValue)]
    [long]$TargetGeneratedRecords = 1500000,
    [ValidateRange(25, 100)]
    [double]$SafetyMarginPercent = 25
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$mediumReviewPath = Join-Path $repositoryRoot 'docs/PS0_10D_MEDIUM_BENCHMARK.json'

function Read-BenchmarkArtifact([string]$Directory) {
    $summaryPath = Join-Path $Directory 'demo-summary.json'
    $samplesPath = Join-Path $Directory 'workload-samples.ndjson'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $samplesPath -PathType Leaf)) {
        throw "Benchmark summary or workload samples are missing from '$Directory'."
    }

    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $samples = @(Get-Content -LiteralPath $samplesPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { ConvertFrom-Json -InputObject $_ })
    if ($samples.Count -eq 0) { throw "No workload samples were found in '$samplesPath'." }

    [pscustomobject]@{ Directory = [System.IO.Path]::GetFullPath($Directory); Summary = $summary; Samples = $samples }
}

function Get-SampleMaximum($Samples, [string]$PropertyName) {
    $values = foreach ($sample in $Samples) {
        $value = $sample.PSObject.Properties[$PropertyName]
        if ($null -eq $value -or $null -eq $value.Value) { 0L }
        else { [long]$value.Value }
    }
    [long](($values | Measure-Object -Maximum).Maximum)
}

function Get-GrowthExponent([long]$AtHundredK, [long]$AtTwoHundredK) {
    if ($AtHundredK -le 0 -or $AtTwoHundredK -le 0) { return 1.0 }
    [math]::Max(1.0, [math]::Log([double]$AtTwoHundredK / $AtHundredK, 2))
}

function Measure-DirectoryBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 0L }
    $files = Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue
    [long](($files | Measure-Object -Property Length -Sum).Sum)
}

function Measure-OutputBytes($Artifact) {
    $directory = $Artifact.Directory
    $paths = @(
        'external-evidence',
        '.proofshift/verifications',
        '.proofshift/recovery',
        '.proofshift/reports'
    )
    $directoryBytes = 0L
    foreach ($relativePath in $paths) {
        $directoryBytes += Measure-DirectoryBytes (Join-Path $directory $relativePath)
    }
    foreach ($fileName in @('performance-run.json', 'performance-run.txt', 'demo-summary.json')) {
        $filePath = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            $directoryBytes += (Get-Item -LiteralPath $filePath).Length
        }
    }
    $directoryBytes
}

function Get-SourceRecordCount($Summary) {
    $counts = foreach ($property in $Summary.sourceRecords.PSObject.Properties) { [long]$property.Value }
    [long](($counts | Measure-Object -Sum).Sum)
}

function Get-SameSampleScratchBytes($Sample) {
    [long]$Sample.verificationExpectedWorksetBytes +
        [long]$Sample.verificationWorkspaceBytes +
        [long]$Sample.verificationPartitionBytes +
        [long]$Sample.verificationLedgerBytes +
        [long]$Sample.verificationLedgerStagingBytes +
        [long]$Sample.sqliteWalBytes +
        [long]$Sample.evidenceBytes +
        [long]$Sample.otherScratchBytes
}

$hundredK = Read-BenchmarkArtifact $HundredKArtifactDirectory
$twoHundredK = Read-BenchmarkArtifact $TwoHundredKArtifactDirectory
$medium = Read-BenchmarkArtifact $MediumArtifactDirectory
$mediumScaleRecords = Get-SourceRecordCount $medium.Summary
if ($mediumScaleRecords -ne 411000) {
    throw "Expected the measured 411,000-record Medium run; found $mediumScaleRecords records."
}
if ((Get-SourceRecordCount $hundredK.Summary) -ne 100000 -or
    (Get-SourceRecordCount $twoHundredK.Summary) -ne 200000) {
    throw 'The scaling profiles must be the measured 100k and 200k W8 full-pipeline runs.'
}
if ($hundredK.Summary.maxPartitionWorkers -ne 8 -or $twoHundredK.Summary.maxPartitionWorkers -ne 8 -or
    $medium.Summary.maxPartitionWorkers -ne 8 -or
    $hundredK.Summary.verificationExecutionMode -ne 'GlobalRuleReference' -or
    $twoHundredK.Summary.verificationExecutionMode -ne 'GlobalRuleReference' -or
    $medium.Summary.verificationExecutionMode -ne 'GlobalRuleReference') {
    throw 'All capacity baselines must use workers=8 and GlobalRuleReference.'
}

$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($outputDirectory.Length -gt 80) {
    throw "Large output path must be short (80 characters maximum); found $($outputDirectory.Length) characters."
}
if ((Test-Path -LiteralPath $outputDirectory -PathType Container) -and
    (Get-ChildItem -LiteralPath $outputDirectory -Force | Select-Object -First 1)) {
    throw "Large output directory is not empty: '$outputDirectory'."
}
$outputVolume = [System.IO.Path]::GetPathRoot($outputDirectory)
$tempVolume = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($env:TEMP))
if (-not [string]::Equals($outputVolume, $tempVolume, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Large output and TEMP must be on the same volume for the measured capacity estimate ($outputVolume vs $tempVolume)."
}

$mediumReview = Get-Content -LiteralPath $mediumReviewPath -Raw | ConvertFrom-Json
$observedMediumPeakVolumeGiB = [double]$mediumReview.resources.observedPeakVolumeUseGiB
if ($observedMediumPeakVolumeGiB -le 0) { throw 'The measured Medium volume high-water is missing.' }

$metricDefinitions = @(
    @{ Name = 'expectedCache'; Property = 'verificationExpectedWorksetBytes' },
    @{ Name = 'verificationWorkspace'; Property = 'verificationWorkspaceBytes' },
    @{ Name = 'sumPartitionDatabases'; Property = 'verificationPartitionBytes' },
    @{ Name = 'largestPartitionDatabase'; Property = 'maximumVerificationPartitionBytes' },
    @{ Name = 'ledgerStagingDatabase'; Property = 'verificationLedgerStagingBytes' },
    @{ Name = 'finalCanonicalLedger'; Property = 'verificationLedgerBytes' },
    @{ Name = 'totalWal'; Property = 'sqliteWalBytes' },
    @{ Name = 'evidenceOutput'; Property = 'evidenceBytes' },
    @{ Name = 'otherSampledScratch'; Property = 'otherScratchBytes' }
)
$categoryEstimates = foreach ($definition in $metricDefinitions) {
    $atHundredK = Get-SampleMaximum $hundredK.Samples $definition.Property
    $atTwoHundredK = Get-SampleMaximum $twoHundredK.Samples $definition.Property
    $atMedium = Get-SampleMaximum $medium.Samples $definition.Property
    $exponent = Get-GrowthExponent $atHundredK $atTwoHundredK
    $projected = [long][math]::Ceiling($atMedium * [math]::Pow($TargetGeneratedRecords / $mediumScaleRecords, $exponent))
    [pscustomobject]@{
        category = $definition.Name
        sampledField = $definition.Property
        measured100kBytes = $atHundredK
        measured200kBytes = $atTwoHundredK
        measuredMediumBytes = $atMedium
        conservativeGrowthExponent = [math]::Round($exponent, 6)
        projectedLargeBytes = $projected
    }
}

$reportOutputMeasurements = @(
    [pscustomobject]@{ Name = '100k'; Bytes = (Measure-OutputBytes $hundredK) },
    [pscustomobject]@{ Name = '200k'; Bytes = (Measure-OutputBytes $twoHundredK) },
    [pscustomobject]@{ Name = 'Medium'; Bytes = (Measure-OutputBytes $medium) }
)
$reportGrowth = Get-GrowthExponent $reportOutputMeasurements[0].Bytes $reportOutputMeasurements[1].Bytes
$reportOutputMediumBytes = [long]$reportOutputMeasurements[2].Bytes
$reportOutputLargeBytes = [long][math]::Ceiling($reportOutputMediumBytes *
    [math]::Pow($TargetGeneratedRecords / $mediumScaleRecords, $reportGrowth))
$categoryEstimates += [pscustomobject]@{
    category = 'reportAndPersistedOutputOverhead'
    sampledField = 'Measured on-disk Evidence/Recovery/reports/performance outputs'
    measured100kBytes = [long]$reportOutputMeasurements[0].Bytes
    measured200kBytes = [long]$reportOutputMeasurements[1].Bytes
    measuredMediumBytes = $reportOutputMediumBytes
    conservativeGrowthExponent = [math]::Round($reportGrowth, 6)
    projectedLargeBytes = $reportOutputLargeBytes
}

$categoryExponents = @($categoryEstimates | ForEach-Object { [double]$_.conservativeGrowthExponent })
$volumeGrowthExponent = ($categoryExponents | Measure-Object -Maximum).Maximum
$scaleFactor = $TargetGeneratedRecords / $mediumScaleRecords
$observedMediumVolumeBytes = [long][math]::Ceiling($observedMediumPeakVolumeGiB * 1GB)
$projectedVolumeBeforeMargin = [long][math]::Ceiling($observedMediumVolumeBytes * [math]::Pow($scaleFactor, $volumeGrowthExponent))
$safetyMarginBytes = [long][math]::Ceiling($projectedVolumeBeforeMargin * $SafetyMarginPercent / 100)
$estimatedRequiredBytes = $projectedVolumeBeforeMargin + $safetyMarginBytes

$mediumSameSampleScratchBytes = [long](($medium.Samples | ForEach-Object { Get-SameSampleScratchBytes $_ } |
    Measure-Object -Maximum).Maximum)
$sameSampleExponents = @{}
foreach ($definition in $metricDefinitions) {
    $sameSampleExponents[$definition.Property] = [double]($categoryEstimates |
        Where-Object { $_.sampledField -eq $definition.Property } | Select-Object -First 1).conservativeGrowthExponent
}
$projectedSameSampleScratch = foreach ($sample in $medium.Samples) {
    $projectedSample = 0.0
    foreach ($definition in $metricDefinitions) {
        $value = $sample.PSObject.Properties[$definition.Property]
        if ($null -ne $value -and $null -ne $value.Value) {
            $projectedSample += [double]$value.Value * [math]::Pow($scaleFactor, $sameSampleExponents[$definition.Property])
        }
    }
    $walValue = [double]$sample.sqliteWalBytes
    $walExponent = $sameSampleExponents.sqliteWalBytes
    $projectedSample += $walValue * ([math]::Pow($scaleFactor, $walExponent) - 1)
    [long][math]::Ceiling($projectedSample)
}
$sameSampleProjectedHighWater = [long](($projectedSameSampleScratch | Measure-Object -Maximum).Maximum)

$drive = [System.IO.DriveInfo]::new($outputVolume)
$freeBytesBeforeRun = [long]$drive.AvailableFreeSpace
$estimatePassed = $estimatedRequiredBytes -le $freeBytesBeforeRun
$estimate = [ordered]@{
    schema = 'proofshift-ps010d-scratch-capacity-estimate-v1'
    createdAt = [DateTimeOffset]::UtcNow
    workload = [ordered]@{
        targetGeneratedRecords = $TargetGeneratedRecords
        measuredMediumGeneratedRecords = $mediumScaleRecords
        scaleFactor = [math]::Round($scaleFactor, 6)
        partitionCount = 8
        maxPartitionWorkers = 8
        verificationRuleMode = 'GlobalRuleReference'
    }
    output = [ordered]@{
        outputDirectory = $outputDirectory
        outputVolume = $outputVolume
        temporaryVolume = $tempVolume
        configuredTempPath = [System.IO.Path]::GetFullPath($env:TEMP)
        outputPathCharacters = $outputDirectory.Length
    }
    measuredGrowth = [ordered]@{
        hundredKArtifactDirectory = $hundredK.Directory
        twoHundredKArtifactDirectory = $twoHundredK.Directory
        mediumArtifactDirectory = $medium.Directory
        growthExponent = [math]::Round($volumeGrowthExponent, 6)
        categoryEstimates = @($categoryEstimates)
        mediumSameSampleScratchHighWaterBytes = $mediumSameSampleScratchBytes
        projectedLargeSameSampleScratchHighWaterBytes = $sameSampleProjectedHighWater
        independentHighWaterValuesSummed = $false
    }
    capacity = [ordered]@{
        observedMediumPeakVolumeUseGiB = $observedMediumPeakVolumeGiB
        observedMediumPeakVolumeUseBytes = $observedMediumVolumeBytes
        projectionBeforeSafetyMarginBytes = $projectedVolumeBeforeMargin
        safetyMarginPercent = $SafetyMarginPercent
        safetyMarginBytes = $safetyMarginBytes
        estimatedRequiredScratchBytes = $estimatedRequiredBytes
        freeBytesBeforeRun = $freeBytesBeforeRun
        safeAvailableBytes = $freeBytesBeforeRun
        freeBytesAfterEstimate = $freeBytesBeforeRun - $estimatedRequiredBytes
        estimatePassed = $estimatePassed
        estimateMethod = 'Observed whole-volume Medium peak projected by the most conservative measured 100k-to-200k category growth exponent; named categories are separately projected for review, and sampled category peaks are combined only within the same sample.'
        marginRationale = 'The prior 20 GiB planning allowance understated observed Medium volume use of 23.56 GiB by 17.8%; the estimator uses a 25% margin.'
    }
}

$estimateJson = $estimate | ConvertTo-Json -Depth 12
$estimatePath = [System.IO.Path]::GetFullPath($EstimatePath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($estimatePath)) | Out-Null
[System.IO.File]::WriteAllText($estimatePath, $estimateJson, [System.Text.UTF8Encoding]::new($false))

$categoryEstimates | Format-Table category, measuredMediumBytes, conservativeGrowthExponent, projectedLargeBytes -AutoSize
[pscustomobject]@{
    EstimatePath = $estimatePath
    OutputDirectory = $outputDirectory
    OutputVolume = $outputVolume
    TargetGeneratedRecords = $TargetGeneratedRecords
    EstimatedRequiredGiB = [math]::Round($estimatedRequiredBytes / 1GB, 2)
    SafetyMarginPercent = $SafetyMarginPercent
    FreeBeforeRunGiB = [math]::Round($freeBytesBeforeRun / 1GB, 2)
    FreeAfterEstimateGiB = [math]::Round(($freeBytesBeforeRun - $estimatedRequiredBytes) / 1GB, 2)
    Passed = $estimatePassed
} | Format-List
if (-not $estimatePassed) {
    throw "Estimated Large requirement $estimatedRequiredBytes B exceeds available $freeBytesBeforeRun B on $outputVolume."
}