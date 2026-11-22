<#
.SYNOPSIS
    Publishes Live Traffic Routing to Paradox Mods, after checking the things that make publishing fail.
.DESCRIPTION
    Runs tools\check.ps1, checks that the game is closed and the work is committed, shows the version, then runs the
    publish profile. Publishing uploads under the Paradox account signed in to the game.
    After uploading code, the commit is tagged vX.Y.Z (and the tag is pushed if the repo has an 'origin' remote).
.PARAMETER PublishProfile
    PublishNewMod: create the mod on Paradox Mods (first release only; afterwards put the new ModId in
                   PublishConfiguration.xml). Default while ModId is empty.
    PublishNewVersion: upload a new version of the existing mod. Default once ModId is set.
    UpdatePublishedConfiguration: update only the description, images, tags and links (no code, no tag).
.EXAMPLE
    .\tools\publish.ps1
.EXAMPLE
    .\tools\publish.ps1 -PublishProfile UpdatePublishedConfiguration
#>
param(
    [ValidateSet('', 'PublishNewMod', 'PublishNewVersion', 'UpdatePublishedConfiguration')]
    [string]$PublishProfile = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repo 'LiveTrafficRouting'
$project = Join-Path $projectDir 'LiveTrafficRouting.csproj'
$configPath = Join-Path $projectDir 'Properties\PublishConfiguration.xml'

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (Get-Process -Name 'Cities2' -ErrorAction SilentlyContinue) {
    Write-Error 'Cities: Skylines II is running and locks the mod files. Close the game, then publish again.'
    exit 1
}

# Same checks as before a commit: images under 2.1 MB, texts for every option, no trademarked names.
& powershell -NoProfile -File (Join-Path $PSScriptRoot 'check.ps1')
if ($LASTEXITCODE -ne 0) { Write-Error 'Checks failed (see above). Fix them before publishing.'; exit 1 }

[xml]$config = Get-Content $configPath -Raw -Encoding UTF8
$modId = $config.Publish.ModId.Value
if (-not $PublishProfile) { $PublishProfile = if ($modId) { 'PublishNewVersion' } else { 'PublishNewMod' } }
if ($PublishProfile -eq 'PublishNewMod' -and $modId) {
    Write-Error "ModId is already set ($modId). Use PublishNewVersion to update the existing mod."
    exit 1
}
if ($PublishProfile -ne 'PublishNewMod' -and -not $modId) {
    Write-Error 'ModId is empty in PublishConfiguration.xml. Publish with PublishNewMod first, then set the ModId.'
    exit 1
}

$version = $config.Publish.ModVersion.Value
$tag = "v$version"
$uploadsCode = $PublishProfile -ne 'UpdatePublishedConfiguration'
$hasOrigin = [bool](git -C $repo remote 2>$null | Where-Object { $_ -eq 'origin' })

if ($uploadsCode) {
    # What gets published must match a commit, so the tag points at the exact code players get.
    if (git -C $repo status --porcelain) {
        Write-Error 'There are uncommitted changes. Commit them first so the published version matches a commit.'
        exit 1
    }
    if ($hasOrigin) { git -C $repo fetch --quiet --tags origin 2>$null }
    if (git -C $repo tag --list $tag) {
        Write-Error "Version $version is already released (tag $tag exists). Bump <ModVersion> and write a <ChangeLog> first."
        exit 1
    }
}

Write-Host "Publishing '$($config.Publish.DisplayName.Value)' version $version with $PublishProfile (ModId: $(if ($modId) { $modId } else { 'new' }), access: $($config.Publish.AccessLevel.Value))"
& $dotnet publish $project "/p:PublishProfile=$PublishProfile" 2>&1 |
    Select-String -Pattern 'error|Mod published|publishing process|Could not publish|Auto logged in|Build FAILED|ModId|mod id' |
    ForEach-Object { $_.Line }
$code = $LASTEXITCODE
if ($code -ne 0) { exit $code }

if ($uploadsCode) {
    git -C $repo tag -a $tag -m "Live Traffic Routing $version (published to Paradox Mods)"
    if ($hasOrigin) {
        git -C $repo push origin $tag
        if ($LASTEXITCODE -ne 0) { Write-Warning "Published, but pushing tag $tag failed. Run: git push origin $tag" }
        else { Write-Host "Tagged $tag and pushed it." }
    } else {
        Write-Host "Tagged $tag (no 'origin' remote, so the tag stays local)."
    }
}
if ($PublishProfile -eq 'PublishNewMod') {
    Write-Host 'First release: copy the new mod id from Paradox Mods into <ModId Value="..."/> in PublishConfiguration.xml and commit it.'
}
exit 0
