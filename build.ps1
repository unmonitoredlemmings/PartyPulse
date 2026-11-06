[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$siblingTools = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\.tools'))
$localTools = if (Test-Path -LiteralPath (Join-Path $siblingTools 'dotnet\dotnet.exe')) { $siblingTools } else { Join-Path $projectRoot '.build' }
$dotnet = if (Test-Path -LiteralPath (Join-Path $localTools 'dotnet\dotnet.exe')) { Join-Path $localTools 'dotnet\dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$saved = @{}
foreach ($name in @('APPDATA','LOCALAPPDATA','DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','DALAMUD_HOME','TEMP','TMP')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    if (-not $env:DALAMUD_HOME) { $env:DALAMUD_HOME = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev' }
    $env:DOTNET_CLI_HOME = Join-Path $localTools 'cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:NUGET_PACKAGES = Join-Path $localTools 'nuget'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $localTools 'nuget-http'
    $env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'appdata'
    $env:LOCALAPPDATA = Join-Path $env:DOTNET_CLI_HOME 'localappdata'
    $env:TEMP = Join-Path $localTools 'temp'
    $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force -Path $env:TEMP,$env:APPDATA,$env:LOCALAPPDATA | Out-Null

    & $dotnet build (Join-Path $projectRoot 'PartyPulse.slnx') -c Release --nologo -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. A .NET 10 SDK and a Dalamud API 15 installation are required.' }
    & $dotnet run --project (Join-Path $projectRoot 'PartyPulse.Tests\PartyPulse.Tests.csproj') -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Health checks failed.' }
    & $dotnet run --project (Join-Path $projectRoot 'PartyPulse.AudioTests\PartyPulse.AudioTests.csproj') -c Release --no-build --no-restore -- (Join-Path $projectRoot 'PartyPulse\Assets\low-hp-pokemon.mp3') (Join-Path $projectRoot 'PartyPulse\Assets\uwu-hannah.mp3')
    if ($LASTEXITCODE -ne 0) { throw 'Audio checks failed.' }

    $distribution = Join-Path $projectRoot 'dist'
    New-Item -ItemType Directory -Force -Path $distribution | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'PartyPulse\bin\Release\PartyPulse\latest.zip') -Destination (Join-Path $distribution 'PartyPulse-1.0.2.zip') -Force
    Write-Host "Plugin package: $(Join-Path $distribution 'PartyPulse-1.0.2.zip')"
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
