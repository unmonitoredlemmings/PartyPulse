[CmdletBinding()]
param(
    [string] $Repository = 'unmonitoredlemmings/PartyPulse',
    [string] $Branch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This only prepares local files. Creating a repository and pushing are separate steps.
if ($Repository -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$' -or
    ($Repository.Split('/')[1] -in @('.', '..'))) {
    throw 'Repository must be a GitHub owner/repository name, for example unmonitoredlemmings/PartyPulse.'
}
if ($Branch -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$' -or
    $Branch.Contains('..') -or $Branch -eq 'HEAD' -or
    @($Branch.Split('/') | Where-Object { $_.EndsWith('.') -or $_.EndsWith('.lock', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
    throw 'Branch must contain safe slash-separated Git branch segments using letters, digits, periods, underscores, or hyphens.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $projectRoot 'PartyPulse\PartyPulse.csproj'
$distributionPath = Join-Path $projectRoot 'dist'
$releaseDirectory = Join-Path $projectRoot 'releases'
$repositoryManifestPath = Join-Path $projectRoot 'repo.json'

function Get-NormalizedVersion([string] $Value) {
    $parsed = $null
    if (-not [Version]::TryParse($Value, [ref] $parsed) -or $parsed.Build -lt 0) {
        throw "Expected a numeric version with at least three parts, got '$Value'."
    }
    return [Version]::new($parsed.Major, $parsed.Minor, $parsed.Build, [Math]::Max(0, $parsed.Revision))
}

function Read-ArchiveJson($Archive, [string] $EntryName) {
    $matches = @($Archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $EntryName })
    if ($matches.Count -ne 1 -or $matches[0].Length -eq 0) {
        throw "Package must contain exactly one nonempty '$EntryName' at its expected path."
    }
    $reader = [IO.StreamReader]::new($matches[0].Open())
    try {
        return ($reader.ReadToEnd() | ConvertFrom-Json)
    } finally {
        $reader.Dispose()
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$versionNodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($versionNodes.Count -ne 1) { throw 'The project must declare one unambiguous Version before preparing a release.' }
$projectVersion = Get-NormalizedVersion $versionNodes[0].InnerText
$assemblyNameNode = $project.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
$internalName = if ($null -ne $assemblyNameNode) { $assemblyNameNode.InnerText } else { [IO.Path]::GetFileNameWithoutExtension($projectPath) }
$displayNameNode = $project.SelectSingleNode('/Project/PropertyGroup/Name')
$apiNode = $project.SelectSingleNode('/Project/PropertyGroup/DalamudApiLevel')
if ($null -ne $apiNode) {
    $expectedApi = [int] $apiNode.InnerText
} elseif ($project.Project.GetAttribute('Sdk') -match '^Dalamud\.NET\.Sdk/(\d+)\.') {
    $expectedApi = [int] $Matches[1]
} else {
    throw 'Cannot determine the expected Dalamud API level from the project SDK or DalamudApiLevel property.'
}

$archives = @(Get-ChildItem -LiteralPath $distributionPath -Filter "$internalName-*.zip" -File)
if ($archives.Count -eq 0) { throw 'No built plugin package exists in dist. Run .\build.ps1 first.' }
$candidates = foreach ($file in $archives) {
    $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $manifest = Read-ArchiveJson $archive "$internalName.json"
        [pscustomobject]@{
            Path = $file.FullName
            LastWriteTimeUtc = $file.LastWriteTimeUtc
            Version = Get-NormalizedVersion ([string] $manifest.AssemblyVersion)
        }
    } finally {
        $archive.Dispose()
    }
}
$selected = $candidates | Sort-Object -Property Version, LastWriteTimeUtc -Descending | Select-Object -First 1
$archive = [IO.Compression.ZipFile]::OpenRead($selected.Path)
try {
    $manifest = Read-ArchiveJson $archive "$internalName.json"
    $packageVersion = Get-NormalizedVersion ([string] $manifest.AssemblyVersion)
    if ($manifest.InternalName -cne $internalName) { throw 'Package InternalName does not match the project assembly name.' }
    if ($null -ne $displayNameNode -and $manifest.Name -cne $displayNameNode.InnerText) {
        throw 'Package display Name does not match the project Name.'
    }
    if ($packageVersion -ne $projectVersion) {
        throw "Latest built package is $packageVersion, but the project is $projectVersion. Rebuild before publishing."
    }
    if ([int] $manifest.DalamudApiLevel -ne $expectedApi) {
        throw "Package targets Dalamud API $($manifest.DalamudApiLevel), but the project expects API $expectedApi."
    }
    if ($packageVersion.Revision -ne 0) {
        throw 'Three-part release filenames require an AssemblyVersion revision of zero to avoid filename collisions.'
    }

    $entries = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $entryPath = $entry.FullName.Replace('\', '/')
        if ($entryPath.StartsWith('/') -or $entryPath.Contains(':') -or
            @($entryPath.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -gt 0) {
            throw "Package contains an unsafe entry path: $entryPath"
        }
        if ($entryPath.EndsWith('/')) { continue }
        if ($entries.ContainsKey($entryPath)) { throw "Package contains duplicate entry: $entryPath" }
        $entries.Add($entryPath, $entry.Length)
    }

    $required = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @("$internalName.dll", "$internalName.json", "$internalName.deps.json", 'PartyPulse.Core.dll')) {
        [void] $required.Add($name)
    }
    foreach ($content in $project.SelectNodes('/Project/ItemGroup/Content[@CopyToOutputDirectory or CopyToOutputDirectory]')) {
        $copySetting = $content.SelectSingleNode('CopyToOutputDirectory')
        $copyMode = if ($null -ne $copySetting) { $copySetting.InnerText } else { $content.GetAttribute('CopyToOutputDirectory') }
        if ($copyMode -eq 'Never') { continue }
        $link = $content.SelectSingleNode('Link')
        $linkPath = if ($null -ne $link) { $link.InnerText } else { $content.GetAttribute('Link') }
        $contentPath = if (-not [string]::IsNullOrEmpty($linkPath)) { $linkPath } else { $content.GetAttribute('Include') }
        [void] $required.Add($contentPath.Replace('\', '/'))
    }
    $dependencies = Read-ArchiveJson $archive "$internalName.deps.json"
    $runtimeTarget = $dependencies.targets.PSObject.Properties[[string] $dependencies.runtimeTarget.name]
    if ($null -eq $runtimeTarget) { throw 'The package dependency manifest does not contain its declared runtime target.' }
    foreach ($library in $runtimeTarget.Value.PSObject.Properties) {
        $runtime = $library.Value.PSObject.Properties['runtime']
        if ($null -eq $runtime) { continue }
        foreach ($assembly in $runtime.Value.PSObject.Properties) {
            $assemblyName = ($assembly.Name.Replace('\', '/').Split('/'))[-1]
            if ($assemblyName -ne '_._') { [void] $required.Add($assemblyName) }
        }
    }
    foreach ($name in $required) {
        if (-not $entries.ContainsKey($name) -or $entries[$name] -le 0) {
            throw "Package is missing required nonempty file '$name'. Rebuild the complete plugin package."
        }
    }
} finally {
    $archive.Dispose()
}

$releaseVersion = '{0}.{1}.{2}' -f $packageVersion.Major, $packageVersion.Minor, $packageVersion.Build
$releaseFilename = "$internalName-$releaseVersion.zip"
$encodedRepository = ($Repository.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
$encodedBranch = ($Branch.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
$downloadUrl = 'https://raw.githubusercontent.com/{0}/{1}/releases/{2}' -f $encodedRepository, $encodedBranch, [Uri]::EscapeDataString($releaseFilename)

# Copy metadata from the package itself so the repository index cannot drift from the DLL release.
$repositoryEntry = [ordered]@{}
foreach ($property in $manifest.PSObject.Properties) { $repositoryEntry[$property.Name] = $property.Value }
$repositoryEntry['RepoUrl'] = "https://github.com/$encodedRepository"
$repositoryEntry['IsHide'] = $false
$repositoryEntry['IsTestingExclusive'] = $false
$repositoryEntry['DownloadLinkInstall'] = $downloadUrl
$repositoryEntry['DownloadLinkUpdate'] = $downloadUrl
$repositoryEntry['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$repositoryJson = ConvertTo-Json -InputObject @($repositoryEntry) -Depth 20

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$releasePath = Join-Path $releaseDirectory $releaseFilename
Copy-Item -LiteralPath $selected.Path -Destination $releasePath -Force
[IO.File]::WriteAllText($repositoryManifestPath, $repositoryJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

Write-Host "Validated package: $($selected.Path)"
Write-Host "Release package: $releasePath"
Write-Host "Repository index: $repositoryManifestPath"
Write-Host "Dalamud repository URL: https://raw.githubusercontent.com/$encodedRepository/$encodedBranch/repo.json"
Write-Host 'Local files are prepared. Nothing has been uploaded or pushed.'
