param(
    [Parameter(Mandatory = $true)]
    [string] $ArchivePath,

    [Parameter(Mandatory = $true)]
    [string] $ExtractionRoot,

    [Parameter(Mandatory = $true)]
    [string] $ResultFile,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedRevision
)

$ErrorActionPreference = "Stop"

function Invoke-PackagedCli {
    param([string] $CliPath, [string[]] $Arguments)

    Push-Location $script:PackageRoot
    try {
        $output = @(& dotnet $CliPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $text = $output -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw "Packaged CLI command 'dotnet $CliPath $($Arguments -join ' ')' failed with exit code $exitCode.`n$text"
    }
    return $text
}

function Invoke-PackagedJson {
    param([string] $CliPath, [string[]] $Arguments)

    $text = Invoke-PackagedCli $CliPath $Arguments
    try { return $text | ConvertFrom-Json }
    catch { throw "Packaged CLI did not return valid JSON for '$($Arguments -join ' ')'.`n$text" }
}

function Assert-Equal {
    param([object] $Actual, [object] $Expected, [string] $Description)

    if ($Actual -ne $Expected) {
        throw "$Description expected '$Expected', found '$Actual'."
    }
}

$archiveFullPath = [IO.Path]::GetFullPath($ArchivePath)
$extractionFullPath = [IO.Path]::GetFullPath($ExtractionRoot)
$resultFullPath = [IO.Path]::GetFullPath($ResultFile)
$checkoutPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..")).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not (Test-Path -LiteralPath $archiveFullPath -PathType Leaf)) { throw "Evaluation archive not found: $archiveFullPath" }
if ($extractionFullPath.StartsWith($checkoutPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package extraction must be outside the repository checkout."
}
if ($extractionFullPath.IndexOf(' ') -lt 0) {
    throw "The package smoke extraction path must contain a space."
}
if (Test-Path -LiteralPath $extractionFullPath) {
    throw "Refusing to reuse an existing extraction path: $extractionFullPath"
}

$archiveHash = (Get-FileHash -LiteralPath $archiveFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = [IO.File]::ReadAllText("$archiveFullPath.sha256").Trim()
if ($checksumText -notmatch '^(?<hash>[0-9a-f]{64})\s+') { throw "Package SHA-256 sidecar is invalid." }
Assert-Equal $archiveHash $Matches.hash "Package SHA-256 sidecar"

Expand-Archive -LiteralPath $archiveFullPath -DestinationPath $extractionFullPath
$packageRoot = Join-Path $extractionFullPath "ProofShift-Evaluation-PS-0.10G"
$cliPath = Join-Path $packageRoot "app/ProofShift.Cli.dll"
$modulePath = Join-Path $packageRoot "fixtures/ProofShift.EndToEnd.Tests.dll"
foreach ($requiredPath in @(
    $cliPath,
    $modulePath,
    (Join-Path $packageRoot "LICENSE"),
    (Join-Path $packageRoot "THIRD-PARTY-NOTICES.md"),
    (Join-Path $packageRoot "THIRD-PARTY-LICENSES"),
    (Join-Path $packageRoot "docs/EVALUATION.md"),
    (Join-Path $packageRoot "docs/DEMO_RUNBOOK.md"),
    (Join-Path $packageRoot "docs/PS0_10F_SCENARIO_INDEX.json")
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required packaged resource is missing: $requiredPath" }
}

$manifestPath = Join-Path $packageRoot "package-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Package manifest is missing." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Equal $manifest.sourceRevision $ExpectedRevision "Package source revision"
if (-not $manifest.sourceWorktreeClean) { throw "Package was not built from a clean source worktree." }
if (-not $manifest.contentSha256) { throw "Package content digest is missing." }
$script:PackageRoot = $packageRoot

foreach ($name in @("PROOFSHIFT_DEMO_REPOSITORY_ROOT", "PROOFSHIFT_DEMO_CLI_PATH", "PS09_INTEGRATED_RUN_DIRECTORY")) {
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}
$syntheticConfigurationValues = @{
    PS05_SQL_CONNECTION = "synthetic-placeholder"
    PS05_POSTGRES_CONNECTION = "synthetic-placeholder"
    PS05_SOURCE_FILES = "synthetic-placeholder"
    PS05_SOURCE_CSV = "synthetic-placeholder"
    PS05_SHADOW_FILES = "synthetic-placeholder"
    PS09_CORPUS_SOURCE_CONNECTION = "synthetic-placeholder"
    PS09_CORPUS_CSV_ROOT = "synthetic-placeholder"
    PS09_CORPUS_SOURCE_FILES_ROOT = "synthetic-placeholder"
    PS09_CORPUS_TARGET_CONNECTION = "synthetic-placeholder"
    PS09_CORPUS_TARGET_FILES_ROOT = "synthetic-placeholder"
}
foreach ($entry in $syntheticConfigurationValues.GetEnumerator()) {
    $envName = $entry.Key
    [Environment]::SetEnvironmentVariable($envName, $entry.Value, "Process")
}
$env:DOTNET_CLI_HOME = Join-Path $extractionFullPath "dotnet-cli-home"
$env:NUGET_PACKAGES = Join-Path $extractionFullPath "empty-nuget-cache"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

$forbiddenNames = @(
    ".git", ".proofshift", ".vs", ".vscode", ".ssh", ".aws", ".azure", ".nuget",
    "TestResults", "obj", "node_modules", "artifacts", "benchmarks", "benchmark",
    "workspaces", "workspace", "volumes", "logs", "cache", "caches"
)
$forbiddenFiles = @(Get-ChildItem -LiteralPath $packageRoot -Force -Recurse -File | Where-Object {
    $relative = [IO.Path]::GetRelativePath($packageRoot, $_.FullName)
    $segments = $relative -split '[\\/]'
    $_.Extension -in @(".cs", ".csproj", ".pdb", ".db", ".sqlite", ".wal", ".shm", ".pem", ".pfx", ".key", ".log", ".trx", ".coverage", ".dmp") -or
        $_.Name -like "*.runtimeconfig.dev.json" -or $_.Name -like ".env*" -or
        $_.Name -in @("id_rsa", "id_ed25519", "known_hosts", "secrets.json", "credentials", "credentials.json", "token.json") -or
        $_.Name -match '(?i)(private[-_]?key|service[-_]?account|cloud[-_]?credentials)' -or
        ($segments | Where-Object { $_ -in $forbiddenNames }).Count -gt 0
})
if ($forbiddenFiles.Count -gt 0) {
    $paths = $forbiddenFiles | ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName) }
    throw "Package contains forbidden source, state, log, database, or credential material: $($paths -join ', ')"
}

$version = Invoke-PackagedCli $cliPath @("--version")
if ($version -notmatch "ProofShift PS-0\.10G") { throw "Packaged version output was unexpected: $version" }
$help = Invoke-PackagedCli $cliPath @("demo", "help")
foreach ($command in @("demo list", "demo describe", "demo preflight", "demo run", "demo reset")) {
    if ($help -notmatch [Regex]::Escape($command)) { throw "Packaged demo help is missing '$command'." }
}
$scenarioList = @(Invoke-PackagedJson $cliPath @("demo", "list", "--json"))
$expectedScenarios = @("fhir", "hcm", "justice", "pension", "utility")
Assert-Equal $scenarioList.Count $expectedScenarios.Count "Installed scenario count"
foreach ($scenarioId in $expectedScenarios) {
    if ($scenarioList.id -notcontains $scenarioId) { throw "Packaged scenario '$scenarioId' is missing from the catalog." }
}

$descriptions = @{}
foreach ($scenarioId in $expectedScenarios) {
    $description = Invoke-PackagedJson $cliPath @("demo", "describe", $scenarioId, "--json")
    if ([string]::IsNullOrWhiteSpace($description.claimBoundary)) { throw "Scenario '$scenarioId' has no claim boundary." }
    $descriptions[$scenarioId] = $description
}
Assert-Equal $descriptions.pension.acceptedOutcome.expectedFindings 149 "Pension accepted findings"
Assert-Equal $descriptions.utility.acceptedOutcome.expectedFindings 26 "Utility accepted findings"
Assert-Equal $descriptions.justice.acceptedOutcome.expectedFindings 24 "Justice accepted findings"
Assert-Equal $descriptions.hcm.acceptedOutcome.expectedFindings 25 "HCM accepted findings"
Assert-Equal $descriptions.fhir.acceptedOutcome.expectedFindings 15 "FHIR accepted findings"

$samples = @(
    "scenarios/pension-modernization/ps010b/proofshift.yaml",
    "scenarios/justice-case-management/proofshift.yaml",
    "scenarios/hcm-erp-modernization/proofshift.yaml",
    "scenarios/pension-modernization/ps05/proofshift.yaml",
    "scenarios/pension-modernization/ps010b/proofshift-false-reverse.yaml"
)
foreach ($sample in $samples) {
    $samplePath = Join-Path $packageRoot $sample
    if (-not (Test-Path -LiteralPath $samplePath -PathType Leaf)) { throw "Shipped sample is missing: $sample" }
    $null = Invoke-PackagedCli $cliPath @("validate", $samplePath, "--json")
}

$scenarioResults = [System.Collections.Generic.List[object]]::new()
foreach ($scenarioId in $expectedScenarios) {
    $preflight = Invoke-PackagedJson $cliPath @("demo", "preflight", $scenarioId, "--json")
    if (-not $preflight.ready) { throw "Packaged scenario '$scenarioId' preflight was blocked." }

    $humanOutput = Invoke-PackagedCli $cliPath @("demo", "run", $scenarioId)
    if ($humanOutput -notmatch "Demo run: COMPLETED") { throw "Scenario '$scenarioId' did not produce the successful human-readable completion output." }

    $workspace = Join-Path $packageRoot ".proofshift/demos/$scenarioId"
    $runResultPath = Join-Path $workspace "demo-result.json"
    if (-not (Test-Path -LiteralPath $runResultPath -PathType Leaf)) { throw "Scenario '$scenarioId' machine-readable run result is missing." }
    $runResult = Get-Content -LiteralPath $runResultPath -Raw | ConvertFrom-Json
    Assert-Equal $runResult.status "completed" "Scenario '$scenarioId' run status"
    if (-not $runResult.stages -or @($runResult.stages).Count -eq 0) { throw "Scenario '$scenarioId' has no execution stages." }
    $failedStages = @($runResult.stages | Where-Object { $_.status -notin @("completed", "expected-state-observed") })
    if ($failedStages.Count -gt 0) { throw "Scenario '$scenarioId' has a stage that did not satisfy its manifest assertions." }

    $reportPaths = @($runResult.reports)
    if ($reportPaths.Count -eq 0) { throw "Scenario '$scenarioId' emitted no report paths." }
    foreach ($reportPath in $reportPaths) {
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Scenario '$scenarioId' report is missing: $reportPath" }
    }
    $textArtifacts = @($runResultPath) + $reportPaths + @($runResult.stages | Where-Object outputFile | ForEach-Object { $_.outputFile })
    foreach ($artifactPath in $textArtifacts | Select-Object -Unique) {
        if ([IO.Path]::GetExtension($artifactPath) -notin @(".json", ".txt", ".log")) { continue }
        $artifactText = [IO.File]::ReadAllText($artifactPath)
        if ($artifactText -match '(?im)\b(?:password|pwd)\s*=' -or
            $artifactText -match '(?i)OraclePS010C2026|Synthetic-PS010C-(?:Db2-Only|Db2-Reader)' -or
            $artifactText -match '(?i)PS05_(?:SQL|POSTGRES)_CONNECTION|PS09_CORPUS_(?:SOURCE|TARGET)_CONNECTION') {
            throw "Scenario '$scenarioId' emitted credential-like material in report/log '$artifactPath'."
        }
    }

    $scenarioOutcome = $null
    if ($scenarioId -eq "pension") {
        $summaryPath = Join-Path $workspace "integrated-assurance/demo-summary.json"
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) { throw "Pension machine-readable assurance summary is missing." }
        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        Assert-Equal $summary.checkpointArtifacts 8495 "Pension checkpoint artifacts"
        Assert-Equal $summary.correctedProjectionTargets 8595 "Pension corrected target artifacts"
        Assert-Equal $summary.defectiveBusinessDiscrepancies 149 "Pension defective findings"
        Assert-Equal $summary.defectiveQualification "NOT QUALIFIED" "Pension defective qualification"
        Assert-Equal $summary.correctedBusinessDiscrepancies 0 "Pension corrected findings"
        Assert-Equal $summary.unaccountedSources 0 "Pension source-accounting gaps"
        Assert-Equal $summary.unexplainedTargets 0 "Pension target-lineage gaps"
        Assert-Equal $summary.correctedQualification "QUALIFIED" "Pension corrected qualification"
        Assert-Equal $summary.falseReverseVerificationOutcome "PASSED" "Pension false-Reverse Verification"
        Assert-Equal $summary.falseReverseVerificationFailures 0 "Pension false-Reverse Verification failures"
        Assert-Equal $summary.falseReverseRejectedEdges 2 "Pension rejected false-Reverse edges"
        Assert-Equal $summary.falseReverseQualification "NOT QUALIFIED" "Pension false-Reverse Recovery qualification"
        if ($humanOutput -notmatch "NOT QUALIFIED" -or $humanOutput -notmatch "QUALIFIED" -or
            $humanOutput -notmatch "149" -or $humanOutput -notmatch "0") {
            throw "Pension human-readable output omitted its defective/corrected assurance outcomes."
        }
        $pensionReportObjects = @($reportPaths | Where-Object { $_ -like "*.json" } | ForEach-Object {
            Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json
        })
        $defectiveReport = $pensionReportObjects | Where-Object { $_.qualification -eq "NOT QUALIFIED" -and $_.businessDiscrepancyCount -eq 149 } | Select-Object -First 1
        $correctedReport = $pensionReportObjects | Where-Object { $_.qualification -eq "QUALIFIED" -and $_.businessDiscrepancyCount -eq 0 } | Select-Object -First 1
        $falseReverseReport = $pensionReportObjects | Where-Object { $_.verificationOutcome -eq "PASSED" -and $_.businessDiscrepancyCount -eq 2 } | Select-Object -First 1
        if ($null -eq $defectiveReport -or $null -eq $correctedReport -or $null -eq $falseReverseReport) {
            throw "Pension defective, corrected, or false-Reverse JSON report is missing."
        }
        Assert-Equal $correctedReport.rehearsalOutcome "Passed" "Pension Recovery rehearsal"
        Assert-Equal $correctedReport.failedRecoveryEdges 0 "Pension corrected Recovery edges"
        Assert-Equal $falseReverseReport.falseReversibleTransformations 2 "Pension false-Reverse declarations"
        Assert-Equal $falseReverseReport.failedRecoveryEdges 2 "Pension false-Reverse Recovery edges"
        if ($descriptions.pension.claimBoundary -notmatch "actuarial") { throw "Pension claim boundary was not present in the packaged description." }
        $scenarioOutcome = [ordered]@{
            seededDefects = $descriptions.pension.acceptedOutcome.seededDefects
            defectiveFindings = $summary.defectiveBusinessDiscrepancies
            correctedFindings = $summary.correctedBusinessDiscrepancies
            sourceArtifacts = $summary.checkpointArtifacts
            correctedTargetArtifacts = $summary.correctedProjectionTargets
            defectiveQualification = $summary.defectiveQualification
            correctedQualification = $summary.correctedQualification
            unaccountedSources = $summary.unaccountedSources
            unexplainedTargets = $summary.unexplainedTargets
            recoveryRehearsal = $correctedReport.rehearsalOutcome
            falseReverseVerification = $summary.falseReverseVerificationOutcome
            falseReverseVerificationFailures = $summary.falseReverseVerificationFailures
            falseReverseRejectedEdges = $summary.falseReverseRejectedEdges
            falseReverseQualification = $summary.falseReverseQualification
        }
    }
    else {
        $assuranceJsonPath = Join-Path $workspace "fixture/assurance-report.json"
        $assuranceTextPath = Join-Path $workspace "fixture/assurance-report.txt"
        foreach ($reportFile in @($assuranceJsonPath, $assuranceTextPath)) {
            if (-not (Test-Path -LiteralPath $reportFile -PathType Leaf)) { throw "Scenario '$scenarioId' assurance report is missing: $reportFile" }
        }
        $assurance = Get-Content -LiteralPath $assuranceJsonPath -Raw | ConvertFrom-Json
        $expectedFindings = $descriptions[$scenarioId].acceptedOutcome.expectedFindings
        $expectedDefects = $descriptions[$scenarioId].acceptedOutcome.seededDefects
        Assert-Equal $assurance.seededDefectCount $expectedDefects "$scenarioId seeded defects"
        Assert-Equal $assurance.defectiveExternalObservation.verificationFailureCount $expectedFindings "$scenarioId defective findings"
        Assert-Equal $assurance.correctedShadow.verificationFailureCount 0 "$scenarioId corrected failures"
        Assert-Equal $assurance.correctedShadow.unaccountedSourceCount 0 "$scenarioId source-accounting gaps"
        Assert-Equal $assurance.correctedShadow.unexplainedTargetCount 0 "$scenarioId target-lineage gaps"
        Assert-Equal $assurance.correctedShadow.recoveryOutcome "PASSED" "$scenarioId Recovery"
        Assert-Equal $assurance.correctedShadow.qualification "QUALIFIED" "$scenarioId corrected qualification"
        $humanReport = Get-Content -LiteralPath $assuranceTextPath -Raw
        if ($humanReport -notmatch "EXTERNAL TARGET OBSERVATIONS \(READ ONLY\)" -or
            $humanReport -notmatch "Source accounting:" -or $humanReport -notmatch "Target lineage:" -or
            $humanReport -notmatch "Qualification: QUALIFIED" -or
            $humanReport -notmatch [Regex]::Escape($descriptions[$scenarioId].claimBoundary)) {
            throw "Scenario '$scenarioId' human-readable report omitted assurance totals or its claim boundary."
        }
        $scenarioOutcome = [ordered]@{
            seededDefects = $assurance.seededDefectCount
            defectiveFindings = $assurance.defectiveExternalObservation.verificationFailureCount
            correctedFindings = $assurance.correctedShadow.verificationFailureCount
            sourceArtifacts = $assurance.checkpointArtifactCount
            correctedTargetArtifacts = $assurance.correctedShadow.targetArtifactCount
            defectiveQualification = "NOT QUALIFIED"
            correctedQualification = $assurance.correctedShadow.qualification
            unaccountedSources = $assurance.correctedShadow.unaccountedSourceCount
            unexplainedTargets = $assurance.correctedShadow.unexplainedTargetCount
            recoveryRehearsal = $assurance.correctedShadow.recoveryRehearsalOutcome
        }
    }

    $scenarioResults.Add([ordered]@{
        id = $scenarioId
        status = $runResult.status
        stageCount = @($runResult.stages).Count
        reportCount = $reportPaths.Count
        outcome = $scenarioOutcome
    })
}

$summaryOutput = [ordered]@{
    format = "proofshift-evaluation-package-smoke-v1"
    sourceRevision = $manifest.sourceRevision
    packageFile = [IO.Path]::GetFileName($archiveFullPath)
    packageSizeBytes = (Get-Item -LiteralPath $archiveFullPath).Length
    packageSha256 = $archiveHash
    packageContentSha256 = $manifest.contentSha256
    extractionRoot = $extractionFullPath
    scenarios = @($scenarioResults)
}
$resultParent = Split-Path -Parent $resultFullPath
if ($resultParent) { New-Item -ItemType Directory -Path $resultParent -Force | Out-Null }
[IO.File]::WriteAllText($resultFullPath, (ConvertTo-Json -InputObject $summaryOutput -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Output "Package SHA-256: $archiveHash"
Write-Output "Package content SHA-256: $($manifest.contentSha256)"
Write-Output "Fresh Windows package smoke: PASS ($($scenarioResults.Count) scenarios)"
Write-Output "Result: $resultFullPath"