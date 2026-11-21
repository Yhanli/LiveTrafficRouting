<#
.SYNOPSIS
    Builds Live Traffic Routing and deploys it to the game's local Mods folder.
.DESCRIPTION
    The build copies the mod into %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\LiveTrafficRouting.
    That copy fails while the game is running (it locks the mod files), so this script checks first.
.PARAMETER Configuration
    Release (default) or Debug.
.EXAMPLE
    .\tools\build.ps1
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'LiveTrafficRouting\LiveTrafficRouting.csproj'

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue) {
    Write-Error 'Cities: Skylines II is running and locks the mod files. Close the game, then build again.'
    exit 1
}

# The in-game statistics panel (UI module) is built first; the C# build copies UI\build into the mod folder.
$ui = Join-Path $repo 'UI'
if (Test-Path (Join-Path $ui 'package.json')) {
    Push-Location $ui
    try {
        if (-not (Test-Path 'node_modules')) { npm install --no-audit --no-fund | Out-Null }
        $uiOutput = npm run build 2>&1
        if ($LASTEXITCODE -ne 0) {
            $uiOutput | Select-Object -Last 30
            Write-Error 'UI module build failed.'
            exit 1
        }
        Write-Host 'UI module built.'
    }
    finally { Pop-Location }
}

$output = & $dotnet build $project -c $Configuration 2>&1
$code = $LASTEXITCODE

# The post-processor prints hundreds of lines; show only what matters.
$output | Select-String -Pattern 'error|warning CS|Build succeeded|Build FAILED|Warning\(s\)|Error\(s\)' |
    ForEach-Object { $_.Line } | Select-Object -Unique

if ($code -eq 0) {
    $deployed = Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\LiveTrafficRouting'
    Write-Host "Deployed to $deployed. Start the game to load it."
}
exit $code
