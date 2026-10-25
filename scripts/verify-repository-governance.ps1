param(
    [string] $Repository = "banderson736/ProofShift"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$requiredChecks = @(
    "build-and-test",
    "dependency-review",
    "remote-storage-integration",
    "oracle-integration",
    "db2-integration",
    "windows-package-reproducibility-b",
    "linux-package-reproducibility-b",
    "windows-package-smoke",
    "linux-package-demos",
    "package-reproducibility",
    "linux-runtime-only-smoke"
)
$results = [System.Collections.Generic.List[string]]::new()
$mismatches = 0

function Add-Result {
    param(
        [bool] $Passed,
        [string] $Name,
        [string] $Detail
    )

    $status = if ($Passed) { "PASS" } else { "FAIL" }
    if (-not $Passed) { $script:mismatches++ }
    $script:results.Add("$status - $Name`: $Detail")
}

function Invoke-GitHubApi {
    param([string] $Path)

    $output = & gh api $Path --header "Accept: application/vnd.github+json" 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = ($output -join " ")
        if ($message -match "HTTP 401|HTTP 403|Resource not accessible|Must have admin rights") {
            throw [UnauthorizedAccessException]::new(
                "GitHub read permission is unavailable for '$Path'. Authenticate gh with repository-admin read access and retry."
            )
        }
        throw [InvalidOperationException]::new(
            "GitHub API read failed for '$Path'. Check gh authentication, repository access, and API availability."
        )
    }

    $json = $output -join [Environment]::NewLine
    try {
        return ,(ConvertFrom-Json -InputObject $json)
    }
    catch {
        throw [InvalidDataException]::new("GitHub API returned invalid JSON for '$Path'.")
    }
}

function Get-Rule {
    param(
        [object[]] $Rules,
        [string] $Type
    )

    return $Rules | Where-Object { $_.type -eq $Type } | Select-Object -First 1
}

try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw [UnauthorizedAccessException]::new(
            "GitHub CLI (gh) is required for read-only settings verification. Install gh, authenticate with repository-admin read access, and retry."
        )
    }

    $repositoryState = Invoke-GitHubApi "repos/$Repository"
    Add-Result ($repositoryState.visibility -eq "public") "Repository visibility" "$($repositoryState.visibility)"
    Add-Result ($repositoryState.default_branch -eq "main") "Default branch" "$($repositoryState.default_branch)"

    foreach ($setting in @(
        @{ Name = "Merge commits"; Property = "allow_merge_commit"; Expected = $true },
        @{ Name = "Squash merge"; Property = "allow_squash_merge"; Expected = $false },
        @{ Name = "Rebase merge"; Property = "allow_rebase_merge"; Expected = $false },
        @{ Name = "Delete branch on merge"; Property = "delete_branch_on_merge"; Expected = $true }
    )) {
        $property = $repositoryState.PSObject.Properties[$setting.Property]
        if ($null -eq $property) {
            throw [UnauthorizedAccessException]::new(
                "GitHub did not expose '$($setting.Property)' for this identity. Confirm repository-admin read permission."
            )
        }
        Add-Result ($property.Value -eq $setting.Expected) $setting.Name "$($property.Value)"
    }

    $rulesets = @(Invoke-GitHubApi "repos/$Repository/rulesets")
    $ruleset = $rulesets | Where-Object { $_.name -eq "main-protection" } | Select-Object -First 1
    if ($null -eq $ruleset) {
        Add-Result $false "main-protection ruleset" "No ruleset with that name exists."
    }
    else {
        $details = Invoke-GitHubApi "repos/$Repository/rulesets/$($ruleset.id)"
        Add-Result ($details.enforcement -eq "active") "Ruleset enforcement" "$($details.enforcement)"
        Add-Result ($details.target -eq "branch") "Ruleset target type" "$($details.target)"
        $branchIncludes = @($details.conditions.ref_name.include)
        Add-Result ($branchIncludes -contains "refs/heads/main") "Ruleset branch target" ($branchIncludes -join ", ")

        $rules = @($details.rules)
        $pullRequest = Get-Rule $rules "pull_request"
        if ($null -eq $pullRequest) {
            Add-Result $false "Pull request requirement" "No pull_request rule is configured."
        }
        else {
            Add-Result ($pullRequest.parameters.required_approving_review_count -eq 0) "Required approvals" "$($pullRequest.parameters.required_approving_review_count)"
            Add-Result (-not $pullRequest.parameters.require_code_owner_review) "CODEOWNER approval" "$($pullRequest.parameters.require_code_owner_review)"
        }

        $statusRule = Get-Rule $rules "required_status_checks"
        if ($null -eq $statusRule) {
            Add-Result $false "Required status checks" "No required_status_checks rule is configured."
        }
        else {
            $contexts = @($statusRule.parameters.required_status_checks | ForEach-Object { $_.context })
            $missing = @($requiredChecks | Where-Object { $_ -notin $contexts })
            $extra = @($contexts | Where-Object { $_ -notin $requiredChecks })
            $strict = $statusRule.parameters.strict_required_status_checks_policy
            Add-Result ($missing.Count -eq 0 -and $extra.Count -eq 0) "Required status-check names" "Missing=[$($missing -join ', ')]; Extra=[$($extra -join ', ')]"
            Add-Result ($strict -eq $true) "Current-main status policy" "$strict"
        }

        Add-Result ($null -ne (Get-Rule $rules "deletion")) "Branch deletion blocked" "deletion rule present"
        Add-Result ($null -ne (Get-Rule $rules "non_fast_forward")) "Force-push/non-fast-forward blocked" "non_fast_forward rule present"

        $bypassActors = @($details.bypass_actors)
        $invalidBypass = @($bypassActors | Where-Object {
            $_.actor_type -ne "RepositoryRole" -or $_.actor_id -ne 5
        })
        Add-Result ($invalidBypass.Count -eq 0) "Ruleset bypass scope" "No bypass or repository-administrator-only bypass"
    }

    $privateReporting = Invoke-GitHubApi "repos/$Repository/private-vulnerability-reporting"
    Add-Result ($privateReporting.enabled -eq $true) "Private vulnerability reporting" "$($privateReporting.enabled)"

    $security = $repositoryState.security_and_analysis
    if ($null -eq $security) {
        throw [UnauthorizedAccessException]::new(
            "GitHub did not expose security_and_analysis settings. Confirm repository-admin read permission and feature eligibility."
        )
    }
    $secretScanning = $security.secret_scanning
    $pushProtection = $security.secret_scanning_push_protection
    if ($null -eq $secretScanning -or $null -eq $pushProtection) {
        throw [UnauthorizedAccessException]::new(
            "GitHub did not expose secret-scanning settings. Confirm repository-admin read permission and feature eligibility."
        )
    }
    Add-Result ($secretScanning.status -eq "enabled") "Secret scanning" "$($secretScanning.status)"
    Add-Result ($pushProtection.status -eq "enabled") "Secret scanning push protection" "$($pushProtection.status)"

    foreach ($result in $results) { Write-Output $result }
    if ($mismatches -gt 0) {
        [Console]::Error.WriteLine("$mismatches repository governance setting(s) do not match policy.")
        exit 1
    }
}
catch [UnauthorizedAccessException] {
    [Console]::Error.WriteLine("Permission or feature availability blocker: $($_.Exception.Message)")
    exit 2
}
catch {
    [Console]::Error.WriteLine("Governance verification could not complete: $($_.Exception.Message)")
    exit 2
}
