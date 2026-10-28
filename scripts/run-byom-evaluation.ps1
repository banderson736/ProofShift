param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $WorkRoot
)

$ErrorActionPreference = "Stop"

function Invoke-Docker {
    param([string[]] $Arguments)

    $output = @(& docker @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $output -join [Environment]::NewLine
    if ($exitCode -ne 0) {
        throw "BYOM container operation failed with exit code $exitCode. $text"
    }
    return $text
}

function Invoke-Product {
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
    $text = $output -join [Environment]::NewLine
    if ($exitCode -ne $ExpectedExitCode) {
        throw "Packaged ProofShift command returned exit code $exitCode; expected $ExpectedExitCode. $text"
    }
    return $text
}

function Invoke-ProductJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)

    $text = Invoke-Product $Arguments $ExpectedExitCode
    try { return $text | ConvertFrom-Json }
    catch { throw "Packaged ProofShift command did not return valid JSON. $text" }
}

function Assert-Equal {
    param([object] $Actual, [object] $Expected, [string] $Description)

    if ($Actual -ne $Expected) {
        throw "$Description expected '$Expected', found '$Actual'."
    }
}

function Get-HostPort {
    param([string] $ContainerName, [string] $ContainerPort)

    $mapping = Invoke-Docker @("port", $ContainerName, $ContainerPort)
    if ($mapping -notmatch '127\.0\.0\.1:(?<port>\d+)') {
        throw "Could not determine the local host port for a BYOM service."
    }
    return $Matches.port
}

function Wait-ForSqlServer {
    param([string] $ContainerName, [string] $Password)

    $sqlcmd = "/opt/mssql-tools18/bin/sqlcmd"
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        $null = & docker exec --env "SQLCMDPASSWORD=$Password" $ContainerName $sqlcmd `
            -S localhost -U sa -C -b -Q "SELECT 1" 2>$null
        if ($LASTEXITCODE -eq 0) { return $sqlcmd }
        Start-Sleep -Seconds 3
    }
    throw "Synthetic SQL Server did not become ready before the BYOM setup timeout."
}

function Wait-ForPostgres {
    param([string] $ContainerName)

    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    while ([DateTime]::UtcNow -lt $deadline) {
        $null = & docker exec $ContainerName pg_isready -U postgres -d byom 2>$null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    throw "Synthetic PostgreSQL did not become ready before the BYOM setup timeout."
}

function Invoke-ObserverQuery {
    param([string] $ContainerName, [string] $Password, [string] $Query)

    return (Invoke-Docker @("exec", "--env", "PGPASSWORD=$Password", $ContainerName,
        "psql", "-At", "-U", "proofshift_observer", "-d", "byom", "-c", $Query)).Trim()
}

function Assert-ObserverWriteDenied {
    param([string] $ContainerName, [string] $Password, [string] $Operation, [string] $Statement)

    $transaction = "BEGIN; $Statement; ROLLBACK;"
    $null = & docker exec --env "PGPASSWORD=$Password" $ContainerName "psql" `
        "-v" "ON_ERROR_STOP=1" "-U" "proofshift_observer" "-d" "byom" "-c" $transaction 2>$null
    if ($LASTEXITCODE -eq 0) {
        throw "Read-only target observer unexpectedly executed $Operation."
    }
}

$script:PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$script:WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (Test-Path -LiteralPath $script:WorkRoot) {
    throw "BYOM work directory must be new; refusing to reuse existing evaluator state."
}
$scenarioRoot = Join-Path $script:PackageRoot "scenarios/byom-public-works"
$discoveryConfig = Join-Path $scenarioRoot "discovery/proofshift.yaml"
$script:CliPath = Join-Path $script:PackageRoot "app/ProofShift.Cli.dll"
foreach ($requiredPath in @(
    $discoveryConfig,
    (Join-Path $scenarioRoot "database/sqlserver-source.sql"),
    (Join-Path $scenarioRoot "database/postgres-target-schema.sql"),
    (Join-Path $scenarioRoot "database/external-load-defective.sql"),
    (Join-Path $scenarioRoot "database/external-load-corrected.sql"),
    (Join-Path $scenarioRoot "reviewed-mappings.csv"),
    (Join-Path $scenarioRoot "expectations.json"),
    $script:CliPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "BYOM package input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Path $script:WorkRoot | Out-Null
$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$sourceContainer = "proofshift-byom-source-$suffix"
$targetContainer = "proofshift-byom-target-$suffix"
$sourcePassword = "Aa1!" + [Guid]::NewGuid().ToString("N") + "Zz"
$adminPassword = "Aa1!" + [Guid]::NewGuid().ToString("N") + "Yy"
$observerPassword = "Aa1!" + [Guid]::NewGuid().ToString("N") + "Xx"
$sourceConnectionName = "PROOFSHIFT_SOURCE_CONNECTION"
$targetConnectionName = "PROOFSHIFT_TARGET_CONNECTION"
$sourceDiscovery = Join-Path $script:WorkRoot "source-discovery"
$targetDiscovery = Join-Path $script:WorkRoot "target-discovery"
$scaffoldRoot = Join-Path $script:WorkRoot "scaffold"
$project = Join-Path $script:WorkRoot "reviewed-project"
$sourceConnection = $null
$adminConnection = $null
$observerConnection = $null
$expectations = Get-Content -LiteralPath (Join-Path $scenarioRoot "expectations.json") -Raw | ConvertFrom-Json
Assert-Equal $expectations.format "proofshift-byom-expectations-v1" "BYOM expectation format"

try {
    $null = Invoke-Docker @("run", "--detach", "--name", $sourceContainer,
        "--publish", "127.0.0.1::1433", "--env", "ACCEPT_EULA=Y",
        "--env", "MSSQL_SA_PASSWORD=$sourcePassword",
        "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
    $null = Invoke-Docker @("run", "--detach", "--name", $targetContainer,
        "--publish", "127.0.0.1::5432", "--env", "POSTGRES_DB=byom",
        "--env", "POSTGRES_USER=postgres", "--env", "POSTGRES_PASSWORD=$adminPassword",
        "postgres:16-alpine")
    $sqlcmd = Wait-ForSqlServer $sourceContainer $sourcePassword
    Wait-ForPostgres $targetContainer
    $sourcePort = Get-HostPort $sourceContainer "1433/tcp"
    $targetPort = Get-HostPort $targetContainer "5432/tcp"

    $sourceSql = Join-Path $scenarioRoot "database/sqlserver-source.sql"
    $targetSql = Join-Path $script:WorkRoot "postgres-target-schema.sql"
    $schemaTemplate = [IO.File]::ReadAllText((Join-Path $scenarioRoot "database/postgres-target-schema.sql"))
    [IO.File]::WriteAllText($targetSql, $schemaTemplate.Replace("__BYOM_OBSERVER_PASSWORD__", $observerPassword),
        [Text.UTF8Encoding]::new($false))
    $null = Invoke-Docker @("cp", $sourceSql, "${sourceContainer}:/tmp/byom-source.sql")
    $null = Invoke-Docker @("cp", $targetSql, "${targetContainer}:/tmp/byom-target-schema.sql")
    $null = Invoke-Docker @("exec", "--env", "SQLCMDPASSWORD=$sourcePassword", $sourceContainer, $sqlcmd,
        "-S", "localhost", "-U", "sa", "-C", "-b", "-i", "/tmp/byom-source.sql")
    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$adminPassword", $targetContainer,
        "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "byom", "-f", "/tmp/byom-target-schema.sql")

    $sourceConnection = "Server=127.0.0.1,$sourcePort;Database=byom;User Id=sa;Password=$sourcePassword;Encrypt=False;TrustServerCertificate=True;"
    $adminConnection = "Host=127.0.0.1;Port=$targetPort;Database=byom;Username=postgres;Password=$adminPassword;SSL Mode=Disable;"
    $observerConnection = "Host=127.0.0.1;Port=$targetPort;Database=byom;Username=proofshift_observer;Password=$observerPassword;SSL Mode=Disable;"
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $sourceConnection, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $adminConnection, "Process")

    $null = Invoke-Product @("discover", $discoveryConfig, "--system", "source", "--endpoint", "records",
        "--output", $sourceDiscovery)
    $null = Invoke-Product @("discover", $discoveryConfig, "--system", "target", "--endpoint", "records",
        "--output", $targetDiscovery)
    $null = Invoke-Product @("discovery", "diff", $sourceDiscovery, $targetDiscovery)
    $null = Invoke-Product @("scaffold", "--source-discovery", $sourceDiscovery, "--target-discovery", $targetDiscovery,
        "--output", $scaffoldRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $scaffoldRoot "proofshift.yaml") -PathType Leaf)) {
        throw "Scaffold did not emit the expected review draft."
    }
    $null = Invoke-Product @("mapping", "import", "--source-discovery", $sourceDiscovery,
        "--target-discovery", $targetDiscovery, "--csv", (Join-Path $scenarioRoot "reviewed-mappings.csv"),
        "--output", $project, "--target-role", "target", "--verification-baseline", "generic")
    $configurationPath = Join-Path $project "proofshift.yaml"
    $validation = Invoke-ProductJson @("validate", $configurationPath, "--strict", "--json")
    Assert-Equal $validation.valid $true "Strict reviewed-mapping validation"
    $null = Invoke-Product @("explain", $configurationPath, "project")
    $null = Invoke-Product @("config", "show", $configurationPath, "--effective")
    $checkpoint = Invoke-ProductJson @("snapshot", $configurationPath, "--json")
    Assert-Equal $checkpoint.status "complete" "BYOM source checkpoint status"
    Assert-Equal $checkpoint.capturedArtifacts $expectations.source.total "BYOM source checkpoint artifact count"

    $null = Invoke-Docker @("stop", $sourceContainer)
    $sourceState = Invoke-Docker @("inspect", "--format", "{{.State.Running}}", $sourceContainer)
    Assert-Equal $sourceState.Trim() "false" "Source state after checkpoint"
    [Environment]::SetEnvironmentVariable($targetConnectionName, $observerConnection, "Process")

    $targetStateQuery = @"
SELECT
    (SELECT count(*) FROM public.assets)::text || ':' ||
    (SELECT count(*) FROM public.work_orders)::text || ':' ||
    (SELECT count(*) FROM public.work_order_history)::text || ':' ||
    md5(
        coalesce((SELECT string_agg(row_to_json(t)::text, E'\n' ORDER BY asset_id) FROM public.assets t), '') || E'\n' ||
        coalesce((SELECT string_agg(row_to_json(t)::text, E'\n' ORDER BY work_order_id) FROM public.work_orders t), '') || E'\n' ||
        coalesce((SELECT string_agg(row_to_json(t)::text, E'\n' ORDER BY history_id) FROM public.work_order_history t), '')
    )
"@
    $permissionsQuery = "SELECT has_table_privilege(current_user,'public.assets','INSERT') OR has_table_privilege(current_user,'public.assets','UPDATE') OR has_table_privilege(current_user,'public.assets','DELETE') OR has_table_privilege(current_user,'public.work_orders','INSERT') OR has_table_privilege(current_user,'public.work_orders','UPDATE') OR has_table_privilege(current_user,'public.work_orders','DELETE') OR has_table_privilege(current_user,'public.work_order_history','INSERT') OR has_table_privilege(current_user,'public.work_order_history','UPDATE') OR has_table_privilege(current_user,'public.work_order_history','DELETE')"
    $null = Invoke-Docker @("cp", (Join-Path $scenarioRoot "database/external-load-defective.sql"),
        "${targetContainer}:/tmp/byom-external-load-defective.sql")
    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$adminPassword", $targetContainer,
        "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "byom", "-f", "/tmp/byom-external-load-defective.sql")
    Assert-Equal (Invoke-ObserverQuery $targetContainer $observerPassword $permissionsQuery) "f" "Read-only target DML privileges"
    $observerWriteProbes = @(
        @{ Operation = "INSERT"; Sql = "INSERT INTO public.assets(asset_id,asset_name,asset_type,active) VALUES ('Z999','Probe','TEST',true)" },
        @{ Operation = "UPDATE"; Sql = "UPDATE public.assets SET active = active WHERE asset_id = 'A001'" },
        @{ Operation = "DELETE"; Sql = "DELETE FROM public.assets WHERE asset_id = 'A001'" },
        @{ Operation = "CREATE"; Sql = "CREATE TABLE public.proofshift_observer_probe(id integer)" },
        @{ Operation = "ALTER"; Sql = "ALTER TABLE public.assets ADD COLUMN proofshift_observer_probe integer" },
        @{ Operation = "DROP"; Sql = "DROP TABLE public.assets" }
    )
    foreach ($probe in $observerWriteProbes) {
        Assert-ObserverWriteDenied $targetContainer $observerPassword $probe.Operation $probe.Sql
    }
    $defectiveTargetState = Invoke-ObserverQuery $targetContainer $observerPassword $targetStateQuery
    Assert-Equal (($defectiveTargetState -split ':')[0..2] -join ':') `
        "$($expectations.defectiveTarget.assets):$($expectations.defectiveTarget.workOrders):$($expectations.defectiveTarget.history)" `
        "Defective target artifact counts"

    $defective = Invoke-ProductJson @("verify-external", $configurationPath, "--checkpoint", $checkpoint.checkpointId,
        "--observation", "vendor-trial-defective", "--json") 1
    Assert-Equal $defective.verificationOutcome "failed" "Defective external Verification outcome"
    Assert-Equal $defective.sourceArtifactCount $expectations.source.total "Defective run source artifact count"
    Assert-Equal $defective.targetArtifactCount $expectations.defectiveTarget.total "Defective run target artifact count"
    Assert-Equal $defective.verificationFailureCount $expectations.defectiveTarget.verificationFailedRules "Defective run failed rule count"
    $stateAfterDefective = Invoke-ObserverQuery $targetContainer $observerPassword $targetStateQuery
    Assert-Equal $stateAfterDefective $defectiveTargetState "Target state after read-only defective Verification"

    $defectiveReport = Invoke-ProductJson @("report", $configurationPath, "--verification-run",
        $defective.verificationRunId, "--json")
    $defectiveEvidence = Invoke-ProductJson @("evidence", $configurationPath, "--run",
        $defective.verificationRunId, "--json")
    Assert-Equal $defectiveEvidence.manifest.complete $true "Persisted Evidence completeness"
    Assert-Equal (@($defectiveEvidence.records).Count) $defectiveEvidence.manifest.recordCount "Persisted Evidence record count"
    $defectGroups = @($defectiveReport.failedFindingGroups.PSObject.Properties)
    $defectCount = ($defectGroups | Measure-Object -Property Value -Sum).Sum
    Assert-Equal $defectCount $expectations.defectiveTarget.findingRecords "Evidence-derived seeded defect count"
    $findingCodeCounts = [ordered]@{}
    foreach ($expectedFinding in $expectations.defectiveTarget.findingCodeCounts.PSObject.Properties) {
        $actualCount = ($defectGroups | Where-Object { $_.Name -match [Regex]::Escape($expectedFinding.Name) } |
            Measure-Object -Property Value -Sum).Sum
        if ($actualCount -ne $expectedFinding.Value) {
            $observedGroups = ConvertTo-Json -InputObject $defectiveReport.failedFindingGroups -Compress
            throw "Finding code '$($expectedFinding.Name)' total expected '$($expectedFinding.Value)', found '$actualCount'. Observed rule/code groups: $observedGroups"
        }
        $findingCodeCounts[$expectedFinding.Name] = $actualCount
    }
    $reportText = ConvertTo-Json -InputObject $defectiveReport -Depth 20 -Compress
    if ($reportText -match "Synthetic Asset|Synthetic work order|Synthetic history event") {
        throw "Verification-only report exposed expected or observed business values."
    }
    Assert-Equal $defectiveReport.recoveryAssessed $false "External report Recovery boundary"
    Assert-Equal $defectiveReport.qualificationAvailable $false "External report qualification boundary"

    $null = Invoke-Docker @("cp", (Join-Path $scenarioRoot "database/external-load-corrected.sql"),
        "${targetContainer}:/tmp/byom-external-load-corrected.sql")
    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$adminPassword", $targetContainer,
        "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "byom", "-f", "/tmp/byom-external-load-corrected.sql")
    $correctedTargetState = Invoke-ObserverQuery $targetContainer $observerPassword $targetStateQuery
    Assert-Equal (($correctedTargetState -split ':')[0..2] -join ':') `
        "$($expectations.correctedTarget.assets):$($expectations.correctedTarget.workOrders):$($expectations.correctedTarget.history)" `
        "Corrected target artifact counts"
    $corrected = Invoke-ProductJson @("verify-external", $configurationPath, "--checkpoint", $checkpoint.checkpointId,
        "--observation", "vendor-trial-corrected", "--json")
    Assert-Equal $corrected.verificationOutcome "passed" "Corrected external Verification outcome"
    Assert-Equal $corrected.verificationFailureCount $expectations.correctedTarget.verificationFailedRules "Corrected Verification failure count"
    Assert-Equal $corrected.sourceArtifactCount $expectations.source.total "Corrected source artifact count"
    Assert-Equal $corrected.targetArtifactCount $expectations.correctedTarget.total "Corrected target artifact count"
    Assert-Equal $corrected.accountedSourceCount $expectations.correctedTarget.total "Corrected accounted source count"
    Assert-Equal $corrected.unaccountedSourceCount $expectations.correctedTarget.unaccountedSources "Corrected unaccounted source count"
    Assert-Equal $corrected.lineagedTargetCount $expectations.correctedTarget.total "Corrected lineaged target count"
    Assert-Equal $corrected.unexplainedTargetCount $expectations.correctedTarget.unexplainedTargets "Corrected unexplained target count"
    Assert-Equal $corrected.recoveryAssessed $expectations.correctedTarget.recoveryAssessed "Corrected Recovery boundary"
    Assert-Equal $corrected.qualificationAvailable $expectations.correctedTarget.qualificationAvailable "Corrected qualification boundary"
    Assert-Equal $corrected.targetFingerprint "ead980fa890238f036d08a54c13884bddac439816e3cd03d7b2d134c093d1fa8" `
        "Accepted corrected BYOM target fingerprint"
    Assert-Equal $corrected.evidenceFingerprint "d38c6e38af55aa171218becf12cad4344a85ff0d6666f87d43d93dc259424ccc" `
        "Accepted corrected BYOM Evidence fingerprint"
    Assert-Equal (Invoke-ObserverQuery $targetContainer $observerPassword $permissionsQuery) "f" "Read-only target DML privileges after Verification"
    Assert-Equal (Invoke-ObserverQuery $targetContainer $observerPassword $targetStateQuery) $correctedTargetState "Target state after read-only corrected Verification"

    $correctedReport = Invoke-ProductJson @("report", $configurationPath, "--verification-run",
        $corrected.verificationRunId, "--json")
    $comparison = Invoke-ProductJson @("compare", $configurationPath, "--before-verification",
        $defective.verificationRunId, "--after-verification", $corrected.verificationRunId, "--json")
    Assert-Equal (($comparison.findingsResolved | Measure-Object -Property Count -Sum).Sum) `
        $expectations.defectiveTarget.findingRecords "Resolved seeded findings"
    Assert-Equal (@($comparison.findingsIntroduced).Count) 0 "Introduced findings"
    Assert-Equal $comparison.targetFingerprintChanged $true "Defective-to-corrected target fingerprint change"
    Assert-Equal $comparison.evidenceFingerprintChanged $true "Defective-to-corrected Evidence fingerprint change"
    Assert-Equal $comparison.verificationOutcomeChanged $true "Defective-to-corrected outcome change"
    Assert-Equal $comparison.sourceFingerprintChanged $false "Defective-to-corrected source fingerprint stability"
    Assert-Equal $comparison.checkpointSemanticsChanged $false "Defective-to-corrected checkpoint semantics stability"
    Assert-Equal $comparison.configurationHashChanged $false "Defective-to-corrected configuration stability"
    Assert-Equal $comparison.graphHashChanged $false "Defective-to-corrected graph stability"
    Assert-Equal $comparison.ruleSetFingerprintChanged $false "Defective-to-corrected rule-set stability"
    Assert-Equal $comparison.providerVersionsChanged $false "Defective-to-corrected provider stability"
    Assert-Equal $comparison.recoveryComparison "not applicable to verification-only comparison" "Verification-only Recovery comparison boundary"
    if ($defectiveTargetState -eq $correctedTargetState) {
        throw "The externally corrected target did not change its deterministic state fingerprint."
    }

    $null = Invoke-Docker @("exec", "--env", "PGPASSWORD=$adminPassword", $targetContainer,
        "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "byom", "-f", "/tmp/byom-external-load-corrected.sql")
    $repeated = Invoke-ProductJson @("verify-external", $configurationPath, "--checkpoint", $checkpoint.checkpointId,
        "--observation", "vendor-trial-corrected-repeat", "--json")
    Assert-Equal $repeated.targetFingerprint $corrected.targetFingerprint "Corrected target fingerprint repeatability"
    Assert-Equal $repeated.evidenceFingerprint $corrected.evidenceFingerprint "Corrected Evidence fingerprint repeatability"
    if ($repeated.verificationRunId -eq $corrected.verificationRunId -or
        $repeated.observationRunId -eq $corrected.observationRunId) {
        throw "Repeated corrected Verification did not receive fresh run identities."
    }
    Assert-Equal $repeated.verificationOutcome $corrected.verificationOutcome "Corrected repeat outcome"
    if ((Test-Path -LiteralPath (Join-Path $project ".proofshift/projections")) -or
        (Test-Path -LiteralPath (Join-Path $project ".proofshift/recovery"))) {
        throw "External Verification created Projection or Recovery artifacts."
    }
    Assert-Equal (Invoke-ObserverQuery $targetContainer $observerPassword $targetStateQuery) $correctedTargetState "Target state after repeated read-only Verification"

    return [pscustomobject]@{
        status = "PASS"
        projectId = "authored-project"
        sourceArtifacts = $checkpoint.capturedArtifacts
        defectiveTargetArtifacts = $defective.targetArtifactCount
        correctedTargetArtifacts = $corrected.targetArtifactCount
        seededFindingCount = $defectCount
        seededFindingCodeCounts = $findingCodeCounts
        defectiveVerificationFailures = $defective.verificationFailureCount
        correctedFailureCount = $corrected.verificationFailureCount
        correctedUnaccountedSources = $corrected.unaccountedSourceCount
        correctedUnexplainedTargets = $corrected.unexplainedTargetCount
        correctedVerificationOutcome = $corrected.verificationOutcome
        recoveryAssessed = $corrected.recoveryAssessed
        qualificationAvailable = $corrected.qualificationAvailable
        correctedTargetFingerprint = $corrected.targetFingerprint
        correctedEvidenceFingerprint = $corrected.evidenceFingerprint
        repeatedTargetFingerprint = $repeated.targetFingerprint
        repeatedEvidenceFingerprint = $repeated.evidenceFingerprint
        sourceOfflineAfterCheckpoint = $true
        targetMutationPrivilegesDenied = $true
        readOnlyTargetProven = $true
        targetMutationDetected = $false
        targetUnchangedByProofShift = $true
        sourceOfflineProven = $true
        externalMigrationFixtureUsed = $true
        projectionArtifactsDetected = $false
        recoveryArtifactsDetected = $false
        projectionAndRecoveryArtifactsCreated = $false
    }
}
finally {
    [Environment]::SetEnvironmentVariable($sourceConnectionName, $null, "Process")
    [Environment]::SetEnvironmentVariable($targetConnectionName, $null, "Process")
    foreach ($containerName in @($sourceContainer, $targetContainer)) {
        if ($containerName) {
            $null = & docker rm --force --volumes $containerName 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Could not remove the BYOM-owned container '$containerName'."
            }
        }
    }
}
