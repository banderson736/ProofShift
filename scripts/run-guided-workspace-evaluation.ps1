param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $WorkRoot,

    [Parameter(Mandatory = $true)]
    [string] $SourceContainer,

    [Parameter(Mandatory = $true)]
    [string] $SourcePort,

    [Parameter(Mandatory = $true)]
    [string] $TargetContainer
)

$ErrorActionPreference = "Stop"

function Assert-Equal {
    param([object] $Actual, [object] $Expected, [string] $Description)

    if ($Actual -ne $Expected) {
        throw "$Description expected '$Expected', found '$Actual'."
    }
}

function Invoke-Docker {
    param([string[]] $Arguments)

    $output = @(& docker @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Workspace fixture operation failed with exit code $LASTEXITCODE."
    }
    return ($output -join [Environment]::NewLine).Trim()
}

function Invoke-Packaged {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)

    Push-Location $script:PackageRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $output = @(& dotnet $script:CliPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }
    if ($exitCode -ne $ExpectedExitCode) {
        $commandName = if ($Arguments[0] -eq "workspace" -and $Arguments[1] -eq "evidence" -and
            $Arguments.Count -gt 2) {
            "workspace evidence $($Arguments[2])"
        }
        elseif ($Arguments[0] -eq "workspace" -and $Arguments.Count -gt 1) {
            "workspace $($Arguments[1])"
        }
        else {
            $Arguments[0]
        }
        $safeDiagnostic = $output | ForEach-Object { "$_" } |
            Where-Object { $_ -match '^\s*PS[A-Z0-9]+:' } | Select-Object -First 1
        $diagnosticText = if ($safeDiagnostic) { " $($safeDiagnostic.Trim())" } else { "" }
        throw "Packaged $commandName command returned exit code $exitCode (expected $ExpectedExitCode).$diagnosticText"
    }
    $text = $output -join [Environment]::NewLine
    $script:CommandOutputs.Add($text)
    return $text
}

function Invoke-PackagedJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)

    $text = Invoke-Packaged $Arguments $ExpectedExitCode
    try { return $text | ConvertFrom-Json }
    catch {
        $command = $Arguments -join " "
        $parseExceptionType = $_.Exception.GetType().Name
        $safeDiagnostic = $text -split '\r?\n' |
            Where-Object { $_ -match '^\s*PS[A-Z0-9]+:' } | Select-Object -First 1
        $diagnosticText = if ($safeDiagnostic) { " $($safeDiagnostic.Trim())" } else { "" }
        throw "Packaged workspace command '$command' did not return valid JSON ($parseExceptionType).$diagnosticText Output omitted."
    }
}

function Invoke-PackagedWorkspaceRun {
    param(
        [string] $Workspace,
        [string] $CheckpointId,
        [string] $Purpose,
        [string] $Baseline,
        [string] $Review,
        [int] $ExpectedExitCode = 0
    )

    $arguments = @("workspace", "run", $Workspace, "--json")
    if ($CheckpointId) { $arguments += @("--checkpoint", $CheckpointId) }
    if ($Baseline) { $arguments += @("--baseline", $Baseline) }
    if ($Review) { $arguments += @("--review", $Review) }
    Push-Location $script:PackageRoot
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $output = @(& dotnet $script:CliPath @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Pop-Location
    }
    if ($exitCode -ne $ExpectedExitCode) {
        $resultLines = @($output | ForEach-Object { "$_" } | Where-Object {
            $_ -match '^\s*\{.*"type"\s*:\s*"result"'
        })
        $runState = "unavailable"
        $disposition = "unavailable"
        $failureCode = "unavailable"
        $verificationOutcome = "unavailable"
        $recoveryQualification = "unavailable"
        if ($resultLines.Count -eq 1) {
            try {
                $runResult = $resultLines[0] | ConvertFrom-Json -ErrorAction Stop
                if ($runResult.state) { $runState = "$($runResult.state)" }
                if ($runResult.run.disposition) { $disposition = "$($runResult.run.disposition)" }
                else { $disposition = "not-established" }
                if ($runResult.run.failureCode) { $failureCode = "$($runResult.run.failureCode)" }
                else { $failureCode = "none" }
                if ($runResult.run.verificationOutcome) {
                    $verificationOutcome = "$($runResult.run.verificationOutcome)"
                }
                if ($runResult.run.recoveryQualification) {
                    $recoveryQualification = "$($runResult.run.recoveryQualification)"
                }
            }
            catch [System.Management.Automation.RuntimeException] {
                $runState = "result-json-invalid"
            }
        }
        throw "Packaged $Purpose guided workspace run returned exit code $exitCode (expected $ExpectedExitCode); state=$runState disposition=$disposition failureCode=$failureCode verification=$verificationOutcome recovery=$recoveryQualification; output omitted."
    }
    $script:CommandOutputs.Add(($output -join [Environment]::NewLine))
    $resultLines = @($output | ForEach-Object { "$_" } | Where-Object {
        $_ -match '^\s*\{.*"type"\s*:\s*"result"'
    })
    if ($resultLines.Count -ne 1) {
        throw "Packaged guided workspace run did not emit exactly one final result record."
    }
    try { return $resultLines[0] | ConvertFrom-Json }
    catch { throw "Packaged guided workspace run emitted invalid result JSON." }
}

function Get-RunManifest {
    param([string] $Workspace, [string] $RunPath)

    $path = Join-Path $Workspace ($RunPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $path = Join-Path $path "run.json"
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-RunDirectory {
    param([string] $Workspace, [string] $RunId)

    return Join-Path (Join-Path $Workspace "runs") $RunId
}

function Get-ShadowBudgetTotal {
    param([string] $ProjectionRunId)

    $schema = "proofshift_shadow_$($ProjectionRunId.Replace('-', ''))"
    $query = "SELECT count(*)::text || ':' || sum(budget_amount)::numeric(12,2)::text FROM `"$schema`".`"asset_budgets`""
    return (Invoke-Docker @("exec", "--env", "PGPASSWORD=$script:TargetPassword",
        $script:TargetContainer, "psql", "-At", "-U", "postgres", "-d", "byom_shadow", "-c", $query)).Trim()
}

function Assert-PackageValid {
    param([string] $PackagePath, [string] $Disposition)

    $validation = Invoke-PackagedJson @("workspace", "evidence", "validate-package", $PackagePath, "--json")
    Assert-Equal $validation.valid $true "$Disposition package offline validation"
    Assert-Equal $validation.disposition $Disposition "$Disposition package disposition"
    Assert-Equal $validation.executionState "Completed" "$Disposition package execution state"
    return $validation
}

function Assert-PackageContainsNoCredentials {
    param([string] $PackagePath, [string[]] $Secrets, [string] $Name)

    $extract = Join-Path $script:WorkRoot "inspect-$Name"
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $extract
    try {
        foreach ($file in Get-ChildItem -LiteralPath $extract -File -Recurse) {
            $content = [IO.File]::ReadAllText($file.FullName)
            foreach ($secret in $Secrets) {
                if ($content.Contains($secret)) {
                    throw "A configured test credential appeared in the $Name evidence package."
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    }
}

function Assert-TamperedPackageInvalid {
    param(
        [string] $Name,
        [string] $SourcePackage,
        [string] $OtherRunPackage
    )

    $extract = Join-Path $script:WorkRoot "tamper-$Name"
    $archive = Join-Path $script:WorkRoot "tamper-$Name.zip"
    Expand-Archive -LiteralPath $SourcePackage -DestinationPath $extract
    switch ($Name) {
        "modified-evidence" {
            $path = Join-Path $extract "artifacts/evidence/evidence.ndjson"
            $bytes = [IO.File]::ReadAllBytes($path)
            if ($bytes.Length -eq 0) { throw "Evidence tamper fixture is unexpectedly empty." }
            $bytes[0] = $bytes[0] -bxor 1
            [IO.File]::WriteAllBytes($path, $bytes)
        }
        "deleted-evidence" {
            Remove-Item -LiteralPath (Join-Path $extract "artifacts/evidence/evidence.ndjson") -Force
        }
        "modified-summary" {
            $path = Join-Path $extract "artifacts/summary.md"
            $bytes = [IO.File]::ReadAllBytes($path)
            if ($bytes.Length -eq 0) { throw "Summary tamper fixture is unexpectedly empty." }
            $bytes[0] = $bytes[0] -bxor 1
            [IO.File]::WriteAllBytes($path, $bytes)
        }
        "substituted-run" {
            $other = Join-Path $script:WorkRoot "tamper-substitute-source"
            Expand-Archive -LiteralPath $OtherRunPackage -DestinationPath $other
            Copy-Item -LiteralPath (Join-Path $other "artifacts/run.json") `
                -Destination (Join-Path $extract "artifacts/run.json") -Force
            Remove-Item -LiteralPath $other -Recurse -Force
        }
        "modified-package-manifest" {
            $path = Join-Path $extract "package.json"
            $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $manifest.packageIdentity = ("a" * 64)
            [IO.File]::WriteAllText($path, ($manifest | ConvertTo-Json -Depth 30),
                [Text.UTF8Encoding]::new($false))
        }
        default { throw "Unknown package tamper case." }
    }
    Compress-Archive -Path (Join-Path $extract "*") -DestinationPath $archive -CompressionLevel Optimal
    $validation = Invoke-PackagedJson @("workspace", "evidence", "validate-package", $archive, "--json") 1
    Assert-Equal $validation.valid $false "$Name tamper rejection"
    Remove-Item -LiteralPath $extract -Recurse -Force
    Remove-Item -LiteralPath $archive -Force
}

function Start-DefectiveWorkspaceReplay {
    param([string] $Workspace, [string] $CheckpointId)

    $existingRunManifests = @{}
    foreach ($path in Get-ChildItem -LiteralPath (Join-Path $Workspace "runs") -Filter "run.json" -File -Recurse) {
        $existingRunManifests[$path.FullName] = $true
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new("dotnet")
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $quotedArguments = @($script:CliPath, "workspace", "run", $Workspace,
        "--checkpoint", $CheckpointId, "--json") | ForEach-Object {
        '"' + $_.Replace('"', '\"') + '"'
    }
    $startInfo.Arguments = $quotedArguments -join " "
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start packaged guided replay." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    try {
        $runDirectory = $null
        $mutated = $false
        $deadline = [DateTime]::UtcNow.AddMinutes(5)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $manifestFiles = @(Get-ChildItem -LiteralPath (Join-Path $Workspace "runs") `
                -Filter "run.json" -File -Recurse | Where-Object {
                    -not $existingRunManifests.ContainsKey($_.FullName)
                } | Sort-Object LastWriteTimeUtc -Descending)
            $candidate = $null
            if ($manifestFiles.Count -gt 0) {
                try {
                    $candidate = Get-Content -LiteralPath $manifestFiles[0].FullName -Raw | ConvertFrom-Json
                }
                catch {
                    Start-Sleep -Milliseconds 20
                }
            }
            if ($candidate) {
                $projectionCompleted = @($candidate.progress | Where-Object {
                    $_.stage -eq "projection" -and $_.status -eq "completed"
                }).Count -gt 0
                if ($projectionCompleted) {
                    $runDirectory = Split-Path -Parent $manifestFiles[0].FullName
                    $schema = "proofshift_shadow_$($candidate.projectionRunId.Replace('-', ''))"
                    $mutation = "UPDATE `"$schema`".`"asset_budgets`" SET `"budget_amount`" = `"budget_amount`" + 1 WHERE `"budget_id`" = 'B001'"
                    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$script:TargetPassword",
                        $script:TargetContainer, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
                        "-d", "byom_shadow", "-c", $mutation)
                    $actual = Get-ShadowBudgetTotal $candidate.projectionRunId
                    Assert-Equal $actual `
                        "$($expectations.shadow.budgetRows):$($expectations.shadow.defectiveBudgetTotal)" `
                        "Mutated guided-replay shadow target total"
                    $mutated = $true
                    break
                }
            }
            Start-Sleep -Milliseconds 20
        }
        if (-not $mutated) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            throw "Could not mutate the run-isolated shadow target after Projection and before Verification."
        }
        if (-not $process.WaitForExit(300000)) {
            $process.Kill()
            throw "Defective guided replay exceeded the package workflow timeout."
        }
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $script:CommandOutputs.Add($stdout)
        $script:CommandOutputs.Add($stderr)
        if ($process.ExitCode -ne 1) {
            throw "Defective guided replay returned unexpected exit code $($process.ExitCode); output omitted."
        }
        $resultLines = @($stdout -split '\r?\n' | Where-Object {
            $_ -match '^\s*\{.*"type"\s*:\s*"result"'
        })
        if ($resultLines.Count -ne 1) {
            throw "Defective guided replay did not emit exactly one result record."
        }
        return [pscustomobject]@{
            Result = ($resultLines[0] | ConvertFrom-Json)
            RunDirectory = $runDirectory
            StandardError = $stderr
        }
    }
    catch {
        if (-not $process.HasExited) {
            try { $process.Kill(); $process.WaitForExit() }
            catch [InvalidOperationException] {}
        }
        throw
    }
}

$script:PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$script:WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$script:SourceContainer = $SourceContainer
$script:TargetContainer = $TargetContainer
$script:SourcePassword = [Environment]::GetEnvironmentVariable("PROOFSHIFT_TEST_SHADOW_SOURCE_PASSWORD")
$script:TargetPassword = [Environment]::GetEnvironmentVariable("PROOFSHIFT_TEST_SHADOW_ADMIN_PASSWORD")
$script:CliPath = Join-Path $script:PackageRoot "app/ProofShift.Cli.dll"
$script:CommandOutputs = [Collections.Generic.List[string]]::new()
$sourceConnectionName = "PROOFSHIFT_SHADOW_SOURCE_CONNECTION"
$targetConnectionName = "PROOFSHIFT_SHADOW_TARGET_CONNECTION"
$sourceConnection = [Environment]::GetEnvironmentVariable($sourceConnectionName)
$targetConnection = [Environment]::GetEnvironmentVariable($targetConnectionName)
if ([string]::IsNullOrWhiteSpace($script:SourcePassword) -or
    [string]::IsNullOrWhiteSpace($script:TargetPassword) -or
    [string]::IsNullOrWhiteSpace($sourceConnection) -or
    [string]::IsNullOrWhiteSpace($targetConnection)) {
    throw "Guided workspace fixture credentials are unavailable."
}
$unavailableSourceConnection = $sourceConnection.Replace(",$SourcePort;", ",1;")
if ($unavailableSourceConnection -eq $sourceConnection) {
    throw "Could not derive the bounded unavailable-source endpoint from the fixture connection."
}
$unavailableSourceConnection = $unavailableSourceConnection.TrimEnd(";") + ";Connect Timeout=1;"

$scenarioRoot = Join-Path $script:PackageRoot "scenarios/byom-public-works/shadow"
$expectations = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $scenarioRoot) "expectations.json") -Raw |
    ConvertFrom-Json
Assert-Equal $expectations.format "proofshift-byom-expectations-v1" "BYOM expectation format"
$workspace = Join-Path $script:WorkRoot "guided-workspace"
try {
    $null = Invoke-Packaged @("workspace", "init", $workspace)
    Copy-Item -Path (Join-Path $scenarioRoot "*") -Destination $workspace -Recurse -Force

    [Environment]::SetEnvironmentVariable($sourceConnectionName, $sourceConnection, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $targetConnection, "Process")
    $initialSourceDiscovery = Join-Path (Join-Path $workspace "discovery") "source-initial"
    $initialShadowDiscovery = Join-Path (Join-Path $workspace "discovery") "shadow-initial"
    $null = Invoke-Packaged @("discover", (Join-Path $workspace "proofshift.yaml"), "--system", "source",
        "--endpoint", "records", "--output", $initialSourceDiscovery, "--json")
    $null = Invoke-Packaged @("discover", (Join-Path $workspace "proofshift.yaml"), "--system", "shadow",
        "--endpoint", "records", "--output", $initialShadowDiscovery, "--json")

    [Environment]::SetEnvironmentVariable($sourceConnectionName, $null, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $null, "Process")
    $structure = Invoke-PackagedJson @("workspace", "validate", $workspace, "--json")
    Assert-Equal $structure.valid $true "Offline workspace validation"
    $offlinePreflight = Invoke-PackagedJson @("workspace", "preflight", $workspace, "--json") 1
    Assert-Equal $offlinePreflight.connectivityAttempted $false "Default offline preflight connectivity"
    Assert-Equal $offlinePreflight.status "BLOCKED" "Offline preflight without declared credentials"
    if (@($offlinePreflight.checks | Where-Object {
        $_.category -eq "connectivity" -and $_.status -ne "NOT_CHECKED"
    }).Count -gt 0) {
        throw "Offline preflight did not preserve the unprobed connectivity state."
    }
    $plan = Invoke-PackagedJson @("workspace", "plan", $workspace, "--json") 1
    Assert-Equal $plan.completed $true "Effective workspace plan"
    if ($plan.plan.fingerprint -notmatch '^[0-9a-f]{64}$') {
        throw "Effective workspace plan fingerprint is not a lowercase SHA-256 digest."
    }

    $rulesPath = Join-Path $workspace "rules/rules.yaml"
    $originalRules = [IO.File]::ReadAllText($rulesPath)
    $annotationReason = "Seeded budget correction demonstrates annotation without waiver."
    if ($originalRules.IndexOf($annotationReason, [StringComparison]::Ordinal) -lt 0) {
        throw "Expected-exception plan-fingerprint fixture was not found."
    }
    $modifiedRules = $originalRules.Replace($annotationReason, "Changed test annotation identity input.")
    [IO.File]::WriteAllText($rulesPath, $modifiedRules, [Text.UTF8Encoding]::new($false))
    $changedPlan = Invoke-PackagedJson @("workspace", "plan", $workspace, "--json") 1
    if ($changedPlan.plan.fingerprint -eq $plan.plan.fingerprint) {
        throw "Changing expected-exception identity did not change the effective plan fingerprint."
    }
    [IO.File]::WriteAllText($rulesPath, $originalRules, [Text.UTF8Encoding]::new($false))
    $plan = Invoke-PackagedJson @("workspace", "plan", $workspace, "--json") 1

    [Environment]::SetEnvironmentVariable($sourceConnectionName, $sourceConnection, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $targetConnection, "Process")
    $connectedPreflight = Invoke-PackagedJson @("workspace", "preflight", $workspace, "--connect", "--json")
    Assert-Equal $connectedPreflight.connectivityAttempted $true "Explicit connected preflight"
    if ($connectedPreflight.status -ne "READY") { throw "Provider-backed connected preflight did not report READY." }
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $unavailableSourceConnection, "Process")
    $unavailablePreflight = Invoke-PackagedJson @("workspace", "preflight", $workspace, "--connect", "--json") 1
    if ($unavailablePreflight.status -eq "READY") { throw "Unavailable SQL Server endpoint incorrectly reported READY." }
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $sourceConnection, "Process")

    $cleanResult = Invoke-PackagedWorkspaceRun -Workspace $workspace -CheckpointId $null -Purpose "clean run"
    Assert-Equal $cleanResult.state "Completed" "Clean guided run execution state"
    Assert-Equal $cleanResult.run.disposition "QUALIFIED" "Clean guided run disposition"
    Assert-Equal $cleanResult.run.checkpointId.Length 36 "Clean guided run checkpoint identity"
    Assert-Equal $cleanResult.run.replayedCheckpoint $false "Fresh clean checkpoint capture"
    Assert-Equal $cleanResult.run.planFingerprint $plan.plan.fingerprint "Run/effective-plan fingerprint binding"
    $cleanManifest = Get-RunManifest $workspace $cleanResult.runPath
    Assert-Equal $cleanManifest.state "Completed" "Persisted clean run state"
    Assert-Equal $cleanManifest.disposition "QUALIFIED" "Persisted clean run disposition"
    $cleanRunDirectory = Get-RunDirectory $workspace $cleanManifest.runId
    $cleanProjectionId = $cleanManifest.projectionRunId.Replace("-", "")
    $cleanProjectionPath = Join-Path (Join-Path $cleanRunDirectory ".proofshift/projections") `
        (Join-Path $cleanProjectionId "run.json")
    $cleanProjection = Get-Content -LiteralPath $cleanProjectionPath -Raw | ConvertFrom-Json
    Assert-Equal $cleanProjection.sourceArtifactCount $expectations.shadow.sourceArtifacts "Clean projection source record count"
    Assert-Equal $cleanProjection.targetArtifactCount $expectations.shadow.sourceArtifacts "Clean projection target record count"
    Assert-Equal (Get-ShadowBudgetTotal $cleanManifest.projectionRunId) `
        "$($expectations.shadow.budgetRows):$($expectations.shadow.budgetTotal)" "Clean projected budget aggregate"
    $cleanPackage = Join-Path (Join-Path $workspace "evidence") "$($cleanManifest.runId).zip"
    $cleanPackageResult = Invoke-PackagedJson @("workspace", "evidence", "package", $workspace,
        $cleanManifest.runId, "--json")
    $cleanValidation = Assert-PackageValid $cleanPackage "QUALIFIED"
    $knownSecrets = @($script:SourcePassword, $script:TargetPassword, $sourceConnection, $targetConnection)
    Assert-PackageContainsNoCredentials $cleanPackage $knownSecrets "clean"
    $cleanSummary = Invoke-PackagedJson @("workspace", "summary", $workspace, $cleanManifest.runId, "--json")
    if (-not (Test-Path -LiteralPath $cleanSummary.summaryPath -PathType Leaf)) {
        throw "Clean customer summary was not written by the extracted package."
    }
    Assert-Equal $cleanPackageResult.packageIdentity $cleanValidation.packageIdentity "Clean package identity validation"

    $cleanBaseline = Invoke-PackagedJson @("workspace", "baseline", "promote", $workspace,
        $cleanManifest.runId, "--json")
    Assert-Equal $cleanBaseline.promotionStatus "Promoted" "Initial clean baseline promotion"
    Assert-Equal $cleanBaseline.disposition "QUALIFIED" "Initial baseline disposition"
    Assert-Equal $cleanBaseline.evidencePackageIdentity $cleanValidation.packageIdentity "Initial baseline package identity"
    $cleanBaselineRecordPath = Join-Path (Join-Path $workspace ".proofshift/baselines") `
        "$($cleanBaseline.baselineId).json"
    $cleanBaselineRecordHash = (Get-FileHash -LiteralPath $cleanBaselineRecordPath -Algorithm SHA256).Hash

    $schemaDriftSql = "ALTER TABLE public.assets ADD COLUMN pilot_schema_review text"
    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$script:TargetPassword",
        $script:TargetContainer, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "byom_shadow", "-c", $schemaDriftSql)
    $updatedShadowDiscovery = Join-Path (Join-Path $workspace "discovery") "shadow-after"
    $null = Invoke-Packaged @("discover", (Join-Path $workspace "proofshift.yaml"), "--system", "shadow",
        "--endpoint", "records", "--output", $updatedShadowDiscovery, "--json")
    $updatedShadowArtifact = Get-Content -LiteralPath (Join-Path $updatedShadowDiscovery "manifest.json") -Raw |
        ConvertFrom-Json
    $driftPlan = Invoke-PackagedJson @("workspace", "plan", $workspace, "--baseline", "active", "--json") 1
    Assert-Equal $driftPlan.semanticDrift.status "ReviewRequired" "Physical-discovery drift classification"
    if (@($driftPlan.semanticDrift.changes | Where-Object {
        $_.category -eq "discovery" -and $_.kind -eq "field-added"
    }).Count -eq 0) {
        throw "Workspace planning did not identify the added physical target column."
    }

    $planReview = Invoke-PackagedJson @("workspace", "review-plan", $workspace,
        "--baseline", "active", "--json")
    Assert-Equal $planReview.review.baselineId $cleanBaseline.baselineId "Plan review baseline identity"
    Assert-Equal $planReview.review.driftStatus "ReviewRequired" "Exact physical-drift review status"
    $candidateResult = Invoke-PackagedWorkspaceRun -Workspace $workspace -CheckpointId $null `
        -Purpose "baseline-governed candidate" -Baseline $cleanBaseline.baselineId `
        -Review $planReview.review.reviewId
    Assert-Equal $candidateResult.state "Completed" "Baseline-governed candidate execution state"
    Assert-Equal $candidateResult.run.disposition "QUALIFIED" "Baseline-governed candidate disposition"
    Assert-Equal $candidateResult.run.baselineId $cleanBaseline.baselineId "Candidate baseline binding"
    Assert-Equal $candidateResult.baselineComparison.outcomeClassification "ChangedStillQualified" `
        "Completed run's persisted baseline comparison"
    $candidateManifest = Get-RunManifest $workspace $candidateResult.runPath
    Assert-Equal $candidateManifest.preRunSemanticDriftStatus "ReviewRequired" "Candidate pre-run physical drift status"
    Assert-Equal $candidateManifest.reviewRecordId $planReview.review.reviewId "Candidate exact review binding"
    $candidatePackage = Join-Path (Join-Path $workspace "evidence") "$($candidateManifest.runId).zip"
    $candidatePackageResult = Invoke-PackagedJson @("workspace", "evidence", "package", $workspace,
        $candidateManifest.runId, "--json")
    $candidateValidation = Assert-PackageValid $candidatePackage "QUALIFIED"
    Assert-Equal $candidatePackageResult.packageIdentity $candidateValidation.packageIdentity "Candidate package identity"
    Assert-PackageContainsNoCredentials $candidatePackage $knownSecrets "baseline-governed-candidate"

    $governedPackageComparison = Invoke-PackagedJson @("workspace", "evidence", "compare-packages",
        $cleanPackage, $candidatePackage, "--json")
    Assert-Equal $governedPackageComparison.comparisonMode "BaselineGoverned" "Offline package comparison mode"
    Assert-Equal $governedPackageComparison.baselineGovernanceBindingValid $true "Offline baseline package binding"
    Assert-Equal $governedPackageComparison.semanticDriftStatus "ReviewRequired" "Packaged physical-drift status"
    Assert-Equal $governedPackageComparison.reviewDecision "Reviewed" "Packaged exact-review decision"
    Assert-Equal $governedPackageComparison.outcomeClassification "ChangedStillQualified" `
        "Baseline-governed candidate outcome"
    if (@($governedPackageComparison.semanticDriftChanges | Where-Object {
        $_.category -eq "discovery" -and $_.kind -eq "field-added"
    }).Count -eq 0) {
        throw "Offline package comparison omitted the reviewed physical schema drift."
    }

    $candidateSummary = Invoke-PackagedJson @("workspace", "summary", $workspace,
        $candidateManifest.runId, "--json")
    Assert-Equal $candidateSummary.outcomeClassification "ChangedStillQualified" "Candidate summary outcome"
    $runHistory = Invoke-PackagedJson @("workspace", "runs", $workspace, "--json")
    $candidateHistory = @($runHistory | Where-Object { $_.runId -eq $candidateManifest.runId })
    Assert-Equal $candidateHistory.Count 1 "Candidate run-history entry count"
    Assert-Equal $candidateHistory[0].trusted $true "Candidate history trust"
    Assert-Equal $candidateHistory[0].packageStatus "Valid" "Candidate history package status"
    Assert-Equal $candidateHistory[0].comparisonStatus "Valid" "Candidate history comparison status"
    Assert-Equal $candidateHistory[0].outcomeClassification "ChangedStillQualified" "Candidate history outcome"

    $newBaseline = Invoke-PackagedJson @("workspace", "baseline", "promote", $workspace,
        $candidateManifest.runId, "--json")
    Assert-Equal $newBaseline.promotionStatus "Promoted" "Candidate baseline promotion"
    Assert-Equal $newBaseline.previousActiveBaselineId $cleanBaseline.baselineId "Baseline progression predecessor"
    Assert-Equal $newBaseline.promotionComparisonOutcomeClassification "ChangedStillQualified" `
        "Promotion preserves candidate outcome"
    Assert-Equal $newBaseline.disposition "QUALIFIED" "Promotion preserves candidate disposition"
    Assert-Equal (Get-FileHash -LiteralPath $cleanBaselineRecordPath -Algorithm SHA256).Hash `
        $cleanBaselineRecordHash "Historical baseline record immutability"
    $idempotentPromotion = Invoke-PackagedJson @("workspace", "baseline", "promote", $workspace,
        $candidateManifest.runId, "--json")
    Assert-Equal $idempotentPromotion.promotionStatus "AlreadyActive" "Active-run promotion idempotency"
    $historicalPromotion = Invoke-Packaged @("workspace", "baseline", "promote", $workspace,
        $cleanManifest.runId, "--json") 1
    if ([string]::IsNullOrWhiteSpace($historicalPromotion)) {
        throw "Historical baseline re-promotion returned no diagnostic."
    }
    $baselineHistory = Invoke-PackagedJson @("workspace", "baseline", "list", $workspace, "--json")
    Assert-Equal $baselineHistory.Count 2 "Explicit baseline progression history count"
    $selectedBaseline = @($baselineHistory | Where-Object { $_.isActive -eq $true })
    Assert-Equal $selectedBaseline.Count 1 "Active baseline history selection count"
    Assert-Equal $selectedBaseline[0].baselineId $newBaseline.baselineId "Latest baseline selection"

    $derivedWorkspace = Join-Path $script:WorkRoot "derived-workspace"
    $null = Invoke-Packaged @("workspace", "init", $derivedWorkspace, "--from", $workspace)
    $derivedValidation = Invoke-PackagedJson @("workspace", "validate", $derivedWorkspace, "--json")
    Assert-Equal $derivedValidation.valid $true "Derived workspace structural validation"
    $sourceWorkspaceManifest = Get-Content -LiteralPath (Join-Path $workspace "proofshift.workspace.json") -Raw |
        ConvertFrom-Json
    $derivedWorkspaceManifest = Get-Content -LiteralPath (Join-Path $derivedWorkspace "proofshift.workspace.json") -Raw |
        ConvertFrom-Json
    if ($sourceWorkspaceManifest.workspaceId -eq $derivedWorkspaceManifest.workspaceId) {
        throw "Derived workspace reused the source workspace identity."
    }
    $sourceConfigurationBytes = [IO.File]::ReadAllBytes((Join-Path $workspace "proofshift.yaml"))
    $derivedConfigurationBytes = [IO.File]::ReadAllBytes((Join-Path $derivedWorkspace "proofshift.yaml"))
    Assert-Equal ([Convert]::ToBase64String($derivedConfigurationBytes)) `
        ([Convert]::ToBase64String($sourceConfigurationBytes)) "Derived workspace project configuration"
    foreach ($emptyPath in @("discovery", "runs", "evidence", ".proofshift/baselines", ".proofshift/reviews")) {
        $path = Join-Path $derivedWorkspace $emptyPath
        if (Test-Path -LiteralPath $path) {
            $entries = @(Get-ChildItem -LiteralPath $path -Force -ErrorAction SilentlyContinue)
            if ($entries.Count -gt 0) { throw "Derived workspace copied runtime state into '$emptyPath'." }
        }
    }

    $packageExtract = Join-Path $script:WorkRoot "clean-package-inspection"
    Expand-Archive -LiteralPath $cleanPackage -DestinationPath $packageExtract
    $cleanPackageManifest = Get-Content -LiteralPath (Join-Path $packageExtract "package.json") -Raw | ConvertFrom-Json
    foreach ($omittedRole in @("source-data", "checkpoint-payload", "projected-target-data")) {
        $omittedArtifact = @($cleanPackageManifest.artifacts | Where-Object { $_.role -eq $omittedRole })
        if ($omittedArtifact.Count -ne 1 -or $omittedArtifact[0].classification -ne "omitted" -or
            $omittedArtifact[0].path) {
            throw "Evidence package did not explicitly omit '$omittedRole'."
        }
    }
    Remove-Item -LiteralPath $packageExtract -Recurse -Force

    $stop = Invoke-Docker @("stop", $script:SourceContainer)
    Assert-Equal $stop $script:SourceContainer "Stop original SQL Server source"
    Assert-Equal (Invoke-Docker @("inspect", "--format", "{{.State.Running}}", $script:SourceContainer)) `
        "false" "Original source container state"
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync("127.0.0.1", [int]$SourcePort)
        $connected = $connectTask.Wait(2000) -and $client.Connected
    }
    catch { $connected = $false }
    finally { $client.Dispose() }
    if ($connected) { throw "Original SQL Server remained reachable after its source container stopped." }
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $null, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $targetConnection, "Process")

    $checkpoint = $cleanManifest.checkpointId
    $replayResult = Invoke-PackagedWorkspaceRun -Workspace $workspace -CheckpointId $checkpoint -Purpose "offline replay"
    Assert-Equal $replayResult.state "Completed" "Offline replay execution state"
    Assert-Equal $replayResult.run.disposition "QUALIFIED" "Offline replay disposition"
    Assert-Equal $replayResult.run.replayedCheckpoint $true "Offline replay flag"
    Assert-Equal $replayResult.run.checkpointId $checkpoint "Offline replay checkpoint identity"
    if ($replayResult.run.runId -eq $cleanManifest.runId -or
        $replayResult.run.projectionRunId -eq $cleanManifest.projectionRunId) {
        throw "Replay did not create distinct run and projection identities."
    }
    $replayManifest = Get-RunManifest $workspace $replayResult.runPath
    if (@($replayManifest.progress | Where-Object {
        $_.stage -eq "checkpoint" -and $_.status -eq "captured"
    }).Count -gt 0) {
        throw "Checkpoint replay incorrectly recorded a fresh source capture."
    }
    $replayPackage = Join-Path (Join-Path $workspace "evidence") "replay.zip"
    $null = Invoke-PackagedJson @("workspace", "summary", $workspace, $replayManifest.runId, "--json")
    $null = Invoke-PackagedJson @("workspace", "evidence", "package", $workspace, $replayManifest.runId,
        "--output", $replayPackage, "--json")
    $replayValidation = Assert-PackageValid $replayPackage "QUALIFIED"
    $cleanReplayComparison = Invoke-PackagedJson @("workspace", "compare", $workspace,
        $cleanManifest.runId, $replayManifest.runId, "--json")
    Assert-Equal $cleanReplayComparison.dispositionChanged $false "Clean and replay disposition comparison"
    Assert-Equal $cleanReplayComparison.executionModeChanged $true "Fresh-capture/replay execution-mode comparison"
    Assert-Equal $cleanReplayComparison.configurationChanged $false "Clean and replay configuration comparison"
    Assert-Equal $cleanReplayComparison.planChanged $false "Clean and replay plan comparison"
    Assert-Equal $cleanReplayComparison.sourcePopulationChanged $false "Clean and replay source-population comparison"
    if (@($cleanReplayComparison.findings | Where-Object { $_.category -ne "Persisting" }).Count -gt 0) {
        throw "Clean and replay comparison reported a changed finding set."
    }
    Assert-Equal $cleanReplayComparison.semanticallyEquivalent $false `
        "Fresh-capture/replay equivalence boundary"

    $defectiveExecution = Start-DefectiveWorkspaceReplay $workspace $checkpoint
    $defectiveResult = $defectiveExecution.Result
    Assert-Equal $defectiveResult.state "Completed" "Defective guided run execution state"
    Assert-Equal $defectiveResult.run.disposition "NOT QUALIFIED" "Defective guided run disposition"
    Assert-Equal $defectiveResult.run.checkpointId $checkpoint "Defective run checkpoint ancestry"
    $defectiveManifest = Get-RunManifest $workspace $defectiveResult.runPath
    $verificationPath = Join-Path (Get-RunDirectory $workspace $defectiveManifest.runId) "evidence"
    $verificationId = $defectiveManifest.verificationRunId.Replace("-", "")
    $evidencePath = Join-Path (Join-Path $verificationPath $verificationId) "evidence.ndjson"
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        $evidenceManifestPath = Join-Path (Join-Path $verificationPath $verificationId) "manifest.json"
        $evidenceManifest = Get-Content -LiteralPath $evidenceManifestPath -Raw | ConvertFrom-Json
        $evidencePath = Join-Path (Join-Path $verificationPath $verificationId) $evidenceManifest.segmentFile
    }
    $evidenceRecords = @(Get-Content -LiteralPath $evidencePath | ForEach-Object { $_ | ConvertFrom-Json })
    $actualFailedRules = @($evidenceRecords | Where-Object { $_.result -eq "Fail" } |
        ForEach-Object { $_.ruleId } | Sort-Object -Unique)
    $expectedFailedRules = @($expectations.shadow.defectiveFailedRuleIds | Sort-Object)
    Assert-Equal (ConvertTo-Json -InputObject $actualFailedRules -Compress) `
        (ConvertTo-Json -InputObject $expectedFailedRules -Compress) "Defective exact failed rule set"
    $annotatedFinding = @($evidenceRecords | Where-Object {
        $_.ruleId -eq "budget-attributes" -and $_.result -eq "Fail"
    })
    $annotation = @($evidenceRecords | Where-Object {
        $_.code -eq "ExpectedExceptionAnnotated" -and
            $_.result -eq "Warning" -and @($_.inputs | Where-Object {
                $_.evidenceId -eq $annotatedFinding[0].id
            }).Count -gt 0
    })
    if ($annotatedFinding.Count -ne 1 -or $annotation.Count -ne 1) {
        throw "Expected-exception annotation did not match the failed finding without waiving it."
    }
    if (@($evidenceRecords | Where-Object {
        $_.ruleId -eq "proofshift.verification.materialized-state" -and
            $_.result -eq "Fail" -and $_.code -eq "MaterializedTargetChanged"
    }).Count -ne 1) {
        throw "Out-of-band target mutation was not detected as MaterializedTargetChanged."
    }
    $defectivePackage = Join-Path (Join-Path $workspace "evidence") "defective.zip"
    $null = Invoke-PackagedJson @("workspace", "summary", $workspace, $defectiveManifest.runId, "--json")
    $defectivePackageResult = Invoke-PackagedJson @("workspace", "evidence", "package", $workspace,
        $defectiveManifest.runId, "--output", $defectivePackage, "--json")
    $defectiveValidation = Assert-PackageValid $defectivePackage "NOT QUALIFIED"
    Assert-PackageContainsNoCredentials $defectivePackage $knownSecrets "defective"
    $defectiveSummaryPath = Join-Path (Get-RunDirectory $workspace $defectiveManifest.runId) "summary.md"
    $defectiveSummary = [IO.File]::ReadAllText($defectiveSummaryPath)
    foreach ($rule in $expectedFailedRules) {
        if (-not $defectiveSummary.Contains("``$rule``")) {
            throw "Defective summary omitted failed rule '$rule'."
        }
    }
    if ($defectiveSummary -notmatch "Expected exceptions waive no findings" -or
        $defectiveSummary -notmatch "Materialized-state/integrity change detected: yes") {
        throw "Defective summary omitted expected-exception or materialized-state diagnostics."
    }
    foreach ($secret in $knownSecrets) {
        if ($defectiveSummary.Contains($secret) -or $defectivePackageResult.packagePath.Contains($secret)) {
            throw "A configured test credential appeared in defective summary or package output."
        }
    }

    $comparison = Invoke-PackagedJson @("workspace", "compare", $workspace,
        $cleanManifest.runId, $defectiveManifest.runId, "--json")
    Assert-Equal $comparison.dispositionChanged $true "Clean-to-defective disposition comparison"
    if (@($comparison.findings | Where-Object { $_.Category -eq "Added" -or $_.category -eq "Added" }).Count -eq 0) {
        throw "Clean-to-defective comparison did not identify added findings."
    }
    if (-not $comparison.materializedStateFindingAdded) {
        throw "Clean-to-defective comparison did not identify the materialized-state finding."
    }

    foreach ($tamper in @("modified-evidence", "deleted-evidence", "modified-summary",
            "substituted-run", "modified-package-manifest")) {
        Assert-TamperedPackageInvalid $tamper $cleanPackage $defectivePackage
    }

    $preview = Invoke-PackagedJson @("workspace", "cleanup", $workspace, $cleanManifest.runId, "--json")
    Assert-Equal $preview.executed $false "Cleanup preview"
    Assert-Equal $preview.resources.Count 1 "Cleanup preview resource count"
    Assert-Equal $preview.resources[0].type "run-temporary" "Cleanup preview resource type"
    if ($preview.resources[0].path -notmatch '/temporary$') {
        throw "Cleanup preview included a path outside the run-local temporary area."
    }
    if (@($preview.resources | Where-Object {
        $_.type -ne "run-temporary" -or $_.path -notmatch '/temporary$' -or
            $_.path -match 'postgres|checkpoint|evidence|source'
    }).Count -gt 0) {
        throw "Cleanup preview included a target, source, checkpoint, or Evidence resource."
    }
    $executed = Invoke-PackagedJson @("workspace", "cleanup", $workspace, $cleanManifest.runId,
        "--execute", "--json")
    Assert-Equal $executed.executed $true "Owned run-temporary cleanup execution"
    if (Test-Path -LiteralPath (Join-Path $cleanRunDirectory "temporary")) {
        throw "Owned run temporary directory remained after cleanup."
    }
    foreach ($preserved in @("run.json", "summary.md")) {
        if (-not (Test-Path -LiteralPath (Join-Path $cleanRunDirectory $preserved) -PathType Leaf)) {
            throw "Cleanup removed preserved run artifact '$preserved'."
        }
    }
    if (-not (Test-Path -LiteralPath $cleanPackage -PathType Leaf)) {
        throw "Cleanup removed or invalidated the retained evidence package."
    }
    $postCleanupValidation = Assert-PackageValid $cleanPackage "QUALIFIED"
    Assert-Equal $postCleanupValidation.valid $true "Retained post-cleanup package validation"
    $cleanVerificationId = $cleanManifest.verificationRunId.Replace("-", "")
    if (-not (Test-Path -LiteralPath (Join-Path (Join-Path $cleanRunDirectory "evidence") `
            (Join-Path $cleanVerificationId "manifest.json")) -PathType Leaf)) {
        throw "Cleanup removed retained Verification Evidence."
    }
    $targetSchema = "proofshift_shadow_$($cleanManifest.projectionRunId.Replace('-', ''))"
    $targetSchemaExists = Invoke-Docker @("exec", "--env", "PGPASSWORD=$script:TargetPassword",
        $script:TargetContainer, "psql", "-At", "-U", "postgres", "-d", "byom_shadow",
        "-c", "SELECT count(*) FROM pg_namespace WHERE nspname = '$targetSchema'")
    Assert-Equal $targetSchemaExists "1" "Configured shadow target preservation"
    $secondCleanup = Invoke-PackagedJson @("workspace", "cleanup", $workspace, $cleanManifest.runId,
        "--execute", "--json")
    Assert-Equal $secondCleanup.safeToExecute $true "Idempotent second cleanup"
    Assert-Equal $secondCleanup.resources[0].status "AlreadyAbsent" "Already-absent cleanup status"

    $unownedTemporary = Join-Path (Get-RunDirectory $workspace $replayManifest.runId) "temporary"
    $ownershipMarker = Join-Path $unownedTemporary "proofshift-owned-temporary.json"
    if (-not (Test-Path -LiteralPath $ownershipMarker -PathType Leaf)) {
        throw "Completed replay run is missing its expected temporary ownership marker."
    }
    Remove-Item -LiteralPath $ownershipMarker -Force
    $unownedFile = Join-Path $unownedTemporary "unowned-scratch.txt"
    [IO.File]::WriteAllText($unownedFile, "preserve")
    $unownedCleanup = Invoke-PackagedJson @("workspace", "cleanup", $workspace,
        $replayManifest.runId, "--execute", "--json") 1
    Assert-Equal $unownedCleanup.executed $false "Unowned cleanup refusal"
    Assert-Equal $unownedCleanup.resources[0].status "OwnershipUnverified" "Unowned cleanup status"
    if (-not (Test-Path -LiteralPath $unownedFile -PathType Leaf)) {
        throw "Cleanup removed temporary data without a matching ownership marker."
    }
    Remove-Item -LiteralPath $unownedFile -Force
    Remove-Item -LiteralPath $unownedTemporary -Recurse -Force

    [Environment]::SetEnvironmentVariable($sourceConnectionName, $unavailableSourceConnection, "Process")
    $incomplete = Invoke-PackagedWorkspaceRun -Workspace $workspace -CheckpointId $null `
        -Purpose "unavailable-source" -ExpectedExitCode 1
    if ($incomplete.state -notin @("PreflightBlocked", "Failed", "Interrupted") -or $incomplete.run.disposition) {
        throw "Unavailable-source run fabricated a final business disposition."
    }
    $incompleteSummary = Invoke-PackagedJson @("workspace", "summary", $workspace,
        $incomplete.run.runId, "--json")
    $incompleteSummaryText = [IO.File]::ReadAllText($incompleteSummary.summaryPath)
    if ($incompleteSummaryText -notmatch "Final business disposition: \*\*not established\*\*") {
        throw "Incomplete-run summary fabricated a business disposition."
    }
    $incompletePackage = Join-Path (Join-Path $workspace "evidence") "incomplete-diagnostic.zip"
    $null = Invoke-PackagedJson @("workspace", "evidence", "package", $workspace,
        $incomplete.run.runId, "--output", $incompletePackage, "--diagnostic", "--json")
    $incompleteValidation = Invoke-PackagedJson @("workspace", "evidence", "validate-package",
        $incompletePackage, "--json")
    Assert-Equal $incompleteValidation.valid $true "Incomplete diagnostic package validation"
    Assert-Equal $incompleteValidation.executionState $incomplete.run.state "Incomplete diagnostic package state"
    Assert-Equal $incompleteValidation.disposition $null "Incomplete diagnostic package disposition"

    foreach ($secret in $knownSecrets) {
        foreach ($output in $script:CommandOutputs) {
            if ($output -match [Regex]::Escape($secret)) {
                throw "A configured test credential appeared in packaged workspace command output."
            }
        }
    }

    return [pscustomobject]@{
        status = "PASS"
        workspaceValidation = "PASS"
        defaultPreflightOfflineWithoutCredentials = "PASS"
        connectedPreflight = "PASS"
        unavailableConnectedPreflight = "PASS"
        planFingerprint = $plan.plan.fingerprint
        expectedExceptionChangesPlanFingerprint = $true
        cleanRunId = $cleanManifest.runId
        cleanCheckpointId = $cleanManifest.checkpointId
        cleanProjectionRunId = $cleanManifest.projectionRunId
        cleanState = $cleanManifest.state
        cleanDisposition = $cleanManifest.disposition
        cleanBaselineId = $cleanBaseline.baselineId
        cleanBaselineDisposition = $cleanBaseline.disposition
        cleanSourceArtifactCount = 114
        cleanBudgetRowsAndAggregate = "12:12780.00"
        initialDiscoveryFingerprints = $cleanManifest.discoveryFingerprints
        physicalSchemaDriftFingerprint = $updatedShadowArtifact.fingerprint
        physicalSchemaDriftReviewed = $planReview.review.driftStatus -eq "ReviewRequired"
        baselineGovernedCandidateRunId = $candidateManifest.runId
        baselineGovernedCandidateDisposition = $candidateManifest.disposition
        baselineGovernedCandidateOutcome = $candidateSummary.outcomeClassification
        baselineGovernedCandidatePackageValidation = $candidateValidation.valid
        offlineBaselineGovernedComparison = $governedPackageComparison.comparisonMode
        offlineBaselineComparisonOutcome = $governedPackageComparison.outcomeClassification
        candidatePromotedAsBaselineId = $newBaseline.baselineId
        historicalBaselinePreserved = $true
        activePromotionIdempotent = $idempotentPromotion.promotionStatus -eq "AlreadyActive"
        historicalRepromotionRejected = $historicalPromotion -ne $null
        baselineHistoryCount = $baselineHistory.Count
        activeBaselineId = $selectedBaseline[0].baselineId
        derivedWorkspaceId = $derivedWorkspaceManifest.workspaceId
        derivedWorkspaceConfigurationReused = $true
        derivedWorkspaceRuntimeStateCopied = $false
        replayRunId = $replayManifest.runId
        replayCheckpointId = $replayManifest.checkpointId
        sourceUnavailableBeforeReplay = $true
        replayDisposition = $replayManifest.disposition
        freshReplayDispositionMatches = -not $cleanReplayComparison.dispositionChanged
        freshReplayModeDifferenceReported = $cleanReplayComparison.executionModeChanged
        freshReplayPlanUnchanged = -not $cleanReplayComparison.planChanged
        defectiveRunId = $defectiveManifest.runId
        defectiveState = $defectiveManifest.state
        defectiveDisposition = $defectiveManifest.disposition
        defectiveBudgetRowsAndAggregate = "$($expectations.shadow.budgetRows):$($expectations.shadow.defectiveBudgetTotal)"
        defectiveFailedRuleIds = $actualFailedRules
        expectedExceptionMatchedWithoutWaiver = $true
        materializedTargetChangeDetected = $true
        cleanPackageValidation = $cleanValidation.valid
        replayPackageValidation = $replayValidation.valid
        defectivePackageValidation = $defectiveValidation.valid
        incompleteDiagnosticPackageValidatedWithoutDisposition = $incompleteValidation.valid -and
            $null -eq $incompleteValidation.disposition
        tamperRejections = @("modified-evidence", "deleted-evidence", "modified-summary",
            "substituted-run", "modified-package-manifest")
        rawSourceDataRoleAbsent = $true
        cleanupPreview = $preview.resources
        cleanupExecuted = $executed.executed
        cleanupIdempotent = $secondCleanup.safeToExecute
        cleanupUnownedResourceRejected = $unownedCleanup.resources[0].status -eq "OwnershipUnverified"
        incompleteRunHasNoDisposition = $true
        externalTargetCleanupNotAttempted = $true
    }
}
finally {
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $sourceConnection, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $targetConnection, "Process")
}
