param(
    [Parameter(Mandatory = $true)]
    [string] $OutputFile
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    param([string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Copy-RepositoryPath {
    param([string] $RelativePath, [string] $DestinationRoot)

    $source = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required package input is missing: $RelativePath"
    }
    $destination = Join-Path $DestinationRoot $RelativePath
    $parent = Split-Path -Parent $destination
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
}

function Get-RelativePath {
    param([string] $Root, [string] $Path)

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    $rootUri = [Uri]::new($rootFull)
    $pathUri = [Uri]::new($pathFull)
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Get-Sha256Hex {
    param([byte[]] $Bytes)

    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace("-", "").ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-NuGetPackages {
    param([string] $ProjectPath)

    $listing = & dotnet list $ProjectPath package --include-transitive --format json 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect the restored package graph for '$ProjectPath'." }
    $document = ($listing -join [Environment]::NewLine) | ConvertFrom-Json
    foreach ($project in $document.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($propertyName in @("topLevelPackages", "transitivePackages")) {
                $property = $framework.PSObject.Properties[$propertyName]
                if ($null -eq $property) { continue }
                foreach ($package in $property.Value) {
                    $version = $package.resolvedVersion
                    if ([string]::IsNullOrWhiteSpace($version)) { $version = $package.requestedVersion }
                    if ([string]::IsNullOrWhiteSpace($version)) {
                        throw "Package '$($package.id)' has no resolved version in the dependency graph."
                    }
                    [PSCustomObject]@{ Id = $package.id; Version = $version }
                }
            }
        }
    }
}

function Write-ThirdPartyNotices {
    param([string] $StageRoot)

    $projectPaths = @(
        "src/ProofShift.Cli/ProofShift.Cli.csproj",
        "src/ProofShift.DemoRunner/ProofShift.DemoRunner.csproj",
        "src/ProofShift.DemoScenarios/ProofShift.DemoScenarios.csproj"
    )
    $packages = @{}
    foreach ($projectPath in $projectPaths) {
        foreach ($package in Get-NuGetPackages $projectPath) {
            $packages["$($package.Id.ToLowerInvariant())/$($package.Version.ToLowerInvariant())"] = $package
        }
    }

    if ($env:NUGET_PACKAGES) {
        $cacheRoot = $env:NUGET_PACKAGES
    }
    elseif ($env:USERPROFILE) {
        $cacheRoot = Join-Path $env:USERPROFILE ".nuget/packages"
    }
    else {
        $cacheRoot = Join-Path $HOME ".nuget/packages"
    }
    $noticeRoot = Join-Path $StageRoot "THIRD-PARTY-LICENSES"
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Third-Party Notices")
    $lines.Add("")
    $lines.Add("This notice is generated from the resolved NuGet dependency graphs of the published CLI and non-test DemoRunner.")
    $lines.Add("License metadata is reproduced as published by package owners; inclusion is not legal advice or a general redistribution determination.")
    $lines.Add("")
    $lines.Add("| Package | Version | NuGet license metadata | Upstream | Local license/notice files |")
    $lines.Add("|---|---|---|---|---|")

    foreach ($key in @($packages.Keys | Sort-Object -CaseSensitive)) {
        $package = $packages[$key]
        $packageRoot = Join-Path (Join-Path $cacheRoot $package.Id.ToLowerInvariant()) $package.Version.ToLowerInvariant()
        $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter "*.nuspec" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $nuspec) { throw "Resolved NuGet package '$key' has no local nuspec metadata." }
        [xml] $metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $licenseNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrlNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $projectUrlNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='projectUrl']")
        $licenseValue = if ($licenseNode) { $licenseNode.InnerText } elseif ($licenseUrlNode) { $licenseUrlNode.InnerText } else { "UNSPECIFIED: manual review required" }
        $upstream = if ($projectUrlNode) { $projectUrlNode.InnerText } elseif ($licenseUrlNode) { $licenseUrlNode.InnerText } else { "not declared" }
        $licenseFiles = @()
        if ($licenseNode -and $licenseNode.GetAttribute("type") -eq "file") {
            $declaredPath = Join-Path $packageRoot ($licenseNode.InnerText -replace '^[.][\\/]', '')
            if (Test-Path -LiteralPath $declaredPath -PathType Leaf) { $licenseFiles += Get-Item -LiteralPath $declaredPath }
        }
        if ($licenseFiles.Count -eq 0) {
            $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
                $_.BaseName -match '^(LICENSE|NOTICE|COPYING|COPYRIGHT)([-.]|$)'
            })
        }
        $packageNoticeRoot = Join-Path $noticeRoot "$($package.Id)/$($package.Version)"
        $copiedPaths = [System.Collections.Generic.List[string]]::new()
        foreach ($licenseFile in $licenseFiles) {
            $relativeLicensePath = Get-RelativePath $packageRoot $licenseFile.FullName
            $destination = Join-Path $packageNoticeRoot $relativeLicensePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination -Force
            $copiedPaths.Add("THIRD-PARTY-LICENSES/$($package.Id)/$($package.Version)/$($relativeLicensePath -replace '\\','/')")
        }
        $fileList = if ($copiedPaths.Count -gt 0) { $copiedPaths -join ", " } else { "none included" }
        $lines.Add("| $($package.Id) | $($package.Version) | $licenseValue | $upstream | $fileList |")
    }

    [IO.File]::WriteAllLines((Join-Path $StageRoot "THIRD-PARTY-NOTICES.md"), $lines,
        [Text.UTF8Encoding]::new($false))
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$architecture = [string] [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ([string]::IsNullOrWhiteSpace($architecture)) {
    $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
}
if ($architecture -notin @("X64", "AMD64", "x86_64")) {
    throw "Evaluation packages are currently built for x64 hosts only; found '$architecture'."
}
$targetPlatform = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    "win-x64"
}
elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
    "linux-x64"
}
else {
    throw "Evaluation packages are supported only on Windows and Linux hosts."
}
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite existing package output '$outputPath'." }
$outputParent = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) "ProofShift-Evaluation-PS-0.10G-Staging"
if (Test-Path -LiteralPath $stagingRoot) {
    throw "A package staging directory already exists at '$stagingRoot'. Inspect it before retrying; concurrent package builds are not supported."
}
$packageRoot = Join-Path $stagingRoot "ProofShift-Evaluation-PS-0.10G"
$temporaryZip = Join-Path $outputParent ".$([IO.Path]::GetFileName($outputPath)).$([Guid]::NewGuid().ToString('N')).tmp"

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Push-Location $repositoryRoot
    try {
        Invoke-DotNet @("restore", "src/ProofShift.Cli/ProofShift.Cli.csproj")
        Invoke-DotNet @("restore", "src/ProofShift.DemoRunner/ProofShift.DemoRunner.csproj")

        Invoke-DotNet @("publish", "src/ProofShift.Cli/ProofShift.Cli.csproj", "--configuration", "Release",
            "--no-restore", "--output", (Join-Path $packageRoot "app"), "-p:UseAppHost=false",
            "-p:DebugSymbols=false", "-p:DebugType=None", "-p:ContinuousIntegrationBuild=true")

        Invoke-DotNet @("publish", "src/ProofShift.DemoRunner/ProofShift.DemoRunner.csproj",
            "--configuration", "Release", "--no-restore", "--output", (Join-Path $packageRoot "demo"),
            "-p:UseAppHost=false", "-p:DebugSymbols=false", "-p:DebugType=None",
            "-p:ContinuousIntegrationBuild=true")
    }
    finally {
        Pop-Location
    }

    foreach ($relativePath in @(
        "LICENSE",
        "global.json",
        "docs/PS0_10F_SCENARIO_INDEX.json",
        "docs/demo/DEMO_SCENARIO_MANIFEST.md",
        "docs/demo/CROSS_DOMAIN_DEMOS.md",
        "docs/EVALUATION.md",
        "docs/DEMO_RUNBOOK.md",
        "scenarios"
    )) {
        Copy-RepositoryPath $relativePath $packageRoot
    }

    $packagedGlobalJsonPath = Join-Path $packageRoot "global.json"
    $sourceGlobalJson = Get-Content -LiteralPath $packagedGlobalJsonPath -Raw | ConvertFrom-Json
    if ($null -eq $sourceGlobalJson.sdk) { throw "The repository global.json has no SDK configuration." }
    $packagedGlobalJson = [ordered]@{ sdk = $sourceGlobalJson.sdk }
    [IO.File]::WriteAllText($packagedGlobalJsonPath,
        (ConvertTo-Json -InputObject $packagedGlobalJson -Depth 10),
        [Text.UTF8Encoding]::new($false))

    Write-ThirdPartyNotices $packageRoot

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
        $_.Extension -in @(".cs", ".csproj", ".pdb") -or $_.Name -like "*.runtimeconfig.dev.json"
    })
    if ($forbiddenFiles.Count -gt 0) {
        $paths = $forbiddenFiles | ForEach-Object { Get-RelativePath $packageRoot $_.FullName }
        throw "Package denylist violation: $($paths -join ', ')"
    }
    $forbiddenTestFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
        $_.Name -match '^(ProofShift\.EndToEnd\.Tests|xunit|Microsoft\.Testing|Microsoft\.NET\.Test|testhost|testplatform)'
    })
    if ($forbiddenTestFiles.Count -gt 0) {
        $paths = $forbiddenTestFiles | ForEach-Object { Get-RelativePath $packageRoot $_.FullName }
        throw "Package test-runtime denylist violation: $($paths -join ', ')"
    }
    $forbiddenDirectories = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -Directory | Where-Object {
        $relative = Get-RelativePath $packageRoot $_.FullName
        $nativeDriverBin = $relative -match '^(app|demo)[\\/]clidriver[\\/]bin$'
        $_.Name -in @(".git", ".proofshift", "TestResults", "obj", "fixtures") -or $_.Name -eq "bin" -and -not $nativeDriverBin
    })
    if ($forbiddenDirectories.Count -gt 0) {
        $paths = $forbiddenDirectories | ForEach-Object { Get-RelativePath $packageRoot $_.FullName }
        throw "Package directory denylist violation: $($paths -join ', ')"
    }

    $excludedSegments = @(".git", ".proofshift", "obj", "TestResults", "node_modules", "artifacts")
    $packageFiles = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
        $relative = Get-RelativePath $packageRoot $_.FullName
        $segments = $relative -split '[\\/]'
        -not ($segments | Where-Object { $_ -in $excludedSegments })
    })
    $relativePaths = @($packageFiles | ForEach-Object { Get-RelativePath $packageRoot $_.FullName })
    [Array]::Sort($relativePaths, [StringComparer]::Ordinal)
    $hashBuilder = [Text.StringBuilder]::new()
    foreach ($relative in $relativePaths) {
        $filePath = Join-Path $packageRoot $relative
        $hash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        [void] $hashBuilder.Append($relative.Replace('\','/')).Append("`t").Append($hash).Append("`n")
    }
    $contentHash = Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($hashBuilder.ToString()))
    $revision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not identify the source revision." }
    $worktreeStatus = @(& git -C $repositoryRoot status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw "Could not identify the source worktree state." }
    $packageManifest = [ordered]@{
        format = "proofshift-evaluation-package-v2"
        productVersion = "PS-0.10G"
        targetPlatform = $targetPlatform
        sourceRevision = $revision
        sourceWorktreeClean = ($worktreeStatus.Count -eq 0)
        contentSha256 = $contentHash
        entryPoint = "dotnet app/ProofShift.Cli.dll"
        scenarioRunner = "demo/ProofShift.DemoRunner.dll (non-test runtime)"
        allowlist = @("app/*.dll and runtime assets", "demo/*.dll/.json and runtime assets", "scenarios/**", "docs/EVALUATION.md", "docs/DEMO_RUNBOOK.md", "docs/demo/DEMO_SCENARIO_MANIFEST.md", "docs/demo/CROSS_DOMAIN_DEMOS.md", "docs/PS0_10F_SCENARIO_INDEX.json", "LICENSE", "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-LICENSES/**", "global.json")
        denylist = @("source code", "test assemblies and test-platform dependencies", "PDB/debug symbols", ".git", ".proofshift", "TestResults", "benchmark outputs", "vendor container images", "secrets", "local environment files")
    }
    $manifestJson = ConvertTo-Json -InputObject $packageManifest -Depth 20
    [IO.File]::WriteAllText((Join-Path $packageRoot "package-manifest.json"), $manifestJson, [Text.UTF8Encoding]::new($false))

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $allPaths = @((Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
            Get-RelativePath $packageRoot $_.FullName
        }))
        [Array]::Sort($allPaths, [StringComparer]::Ordinal)
        foreach ($relative in $allPaths) {
            $entryName = "ProofShift-Evaluation-PS-0.10G/" + $relative.Replace('\','/')
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $inputStream = [IO.File]::OpenRead((Join-Path $packageRoot $relative))
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally {
        $archive.Dispose()
    }

    Move-Item -LiteralPath $temporaryZip -Destination $outputPath
    $zipHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$outputPath.sha256", "$zipHash  $([IO.Path]::GetFileName($outputPath))`n", [Text.Encoding]::ASCII)
    Write-Output "Package: $outputPath"
    Write-Output "SHA-256: $zipHash"
    Write-Output "Content: $contentHash"
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}