param(
    [Parameter(Mandatory = $true)]
    [string] $BuildA,

    [Parameter(Mandatory = $true)]
    [string] $BuildB,

    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "linux-x64")]
    [string] $TargetPlatform
)

$ErrorActionPreference = "Stop"

function Read-Build {
    param([string] $Root)

    $archives = @(Get-ChildItem -LiteralPath $Root -Filter "*.zip" -File -Recurse)
    if ($archives.Count -ne 1) {
        throw "Expected exactly one package archive under '$Root'; found $($archives.Count)."
    }
    $archive = $archives[0]
    $provenancePath = [IO.Path]::ChangeExtension($archive.FullName, ".provenance.json")
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "Provenance sidecar is missing for '$($archive.Name)'."
    }
    $actualArchiveHash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.archiveSha256 -ne $actualArchiveHash) {
        throw "Archive hash does not match provenance for '$($archive.Name)'."
    }
    if ($provenance.targetPlatform -ne $TargetPlatform) {
        throw "Expected $TargetPlatform provenance; found '$($provenance.targetPlatform)'."
    }
    if ($provenance.archiveFile -ne $archive.Name) {
        throw "Provenance archive filename does not match '$($archive.Name)'."
    }
    [PSCustomObject]@{
        Archive = $archive
        Provenance = $provenance
        ArchiveSha256 = $actualArchiveHash
    }
}

$first = Read-Build $BuildA
$second = Read-Build $BuildB
if ($first.ArchiveSha256 -ne $second.ArchiveSha256) {
    throw "$TargetPlatform independent package archive SHA-256 values differ."
}

$fields = @(
    "productVersion",
    "targetPlatform",
    "archiveFile",
    "archiveSha256",
    "contentSha256",
    "sourceRevision",
    "sourceTree",
    "dependencyLockSha256",
    "buildSdkVersion",
    "targetFramework",
    "packageFormat"
)
foreach ($field in $fields) {
    if ($first.Provenance.$field -ne $second.Provenance.$field) {
        throw "$TargetPlatform provenance field '$field' differs between independent builds."
    }
}

Write-Output "$TargetPlatform independent reproducibility: PASS"
Write-Output "Build A archive SHA-256: $($first.ArchiveSha256)"
Write-Output "Build B archive SHA-256: $($second.ArchiveSha256)"
Write-Output "Build A content SHA-256: $($first.Provenance.contentSha256)"
Write-Output "Build B content SHA-256: $($second.Provenance.contentSha256)"
Write-Output "Provenance fields compared: $($fields -join ', ')"
