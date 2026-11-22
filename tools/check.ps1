<#
.SYNOPSIS
    Repository checks that run without the game. Run before committing and before publishing.
.DESCRIPTION
    - PublishConfiguration.xml is valid, has the required fields and an x.y.z ModVersion.
    - Every listed thumbnail/screenshot exists and is under Paradox Mods' 2.1 MB image limit.
    - Every option in LiveTrafficSettings.cs has a label and description, and every dropdown value has a text.
    - The mod page and README don't use the trademarked name "Google".
    The mod itself is built with tools\build.ps1 (needs the game's DLLs and the modding toolchain).
.EXAMPLE
    .\tools\check.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repo 'LiveTrafficRouting'
$script:errors = 0

function Fail([string]$file, [string]$message) {
    $script:errors++
    Write-Host "FAIL  $file`: $message" -ForegroundColor Red
}
function Pass([string]$message) { Write-Host "ok    $message" }

# --- Publish configuration -------------------------------------------------------------------------
$configRel = 'LiveTrafficRouting/Properties/PublishConfiguration.xml'
try {
    [xml]$config = Get-Content (Join-Path $repo $configRel) -Raw -Encoding UTF8
    Pass 'PublishConfiguration.xml is valid XML'
} catch {
    Fail $configRel "Not valid XML: $($_.Exception.Message)"
    $config = $null
}

$longDescription = ''
if ($config) {
    $p = $config.Publish
    foreach ($field in 'DisplayName', 'ShortDescription', 'ModVersion', 'GameVersion', 'Thumbnail') {
        if ([string]::IsNullOrWhiteSpace($p.$field.Value)) { Fail $configRel "<$field Value=...> is empty" }
    }
    if ($p.ModVersion.Value -notmatch '^\d+\.\d+\.\d+$') { Fail $configRel "ModVersion '$($p.ModVersion.Value)' must look like 1.2.3" }
    else { Pass "ModVersion $($p.ModVersion.Value)" }

    $images = @($p.Thumbnail.Value) + @($p.Screenshot | ForEach-Object { $_.Value }) | Where-Object { $_ }
    foreach ($image in $images) {
        $file = Join-Path $projectDir $image
        if (-not (Test-Path $file)) { Fail $configRel "Image not found: $image"; continue }
        $mb = (Get-Item $file).Length / 1MB
        if ($mb -gt 2.1) { Fail "LiveTrafficRouting/$image" ('{0:N2} MB is over the 2.1 MB Paradox Mods limit; save it as JPG' -f $mb) }
        else { Pass ('{0} ({1:N2} MB)' -f $image, $mb) }
    }

    # A text-only element comes back as a plain string; one with attributes or children as an XmlElement.
    $longDescription = if ($p.LongDescription -is [string]) { $p.LongDescription } else { $p.LongDescription.InnerText }
    if ([string]::IsNullOrWhiteSpace($longDescription) -or $longDescription -match 'Work in progress') {
        Fail $configRel 'LongDescription is empty or still the placeholder'
    }
    $changeLog = if ($p.ChangeLog -is [string]) { $p.ChangeLog } else { $p.ChangeLog.InnerText }
    if ([string]::IsNullOrWhiteSpace($changeLog)) { Fail $configRel 'ChangeLog is empty' }
}

# --- Settings have texts ---------------------------------------------------------------------------
$settingsRel = 'LiveTrafficRouting/LiveTrafficSettings.cs'
$localeRel = 'LiveTrafficRouting/LiveTrafficLocale.cs'
$settings = Get-Content (Join-Path $repo $settingsRel) -Raw -Encoding UTF8
$locale = Get-Content (Join-Path $repo $localeRel) -Raw -Encoding UTF8

# Options are the properties marked [SettingsUISection(...)]; other attributes may sit in between.
$options = [regex]::Matches($settings, '\[SettingsUISection[^\]]*\](?:\s*\[[^\]]*\])*\s*public\s+[\w<>]+\s+(\w+)\s*\r?\n') |
    ForEach-Object { $_.Groups[1].Value }
if (-not $options) { Fail $settingsRel 'No [SettingsUISection] options found (did the file layout change?)' }
foreach ($option in $options) {
    foreach ($kind in 'Label', 'Desc') {
        if ($locale -notmatch "GetOption${kind}LocaleID\(nameof\(LiveTrafficSettings\.$option\)\)") {
            Fail $localeRel "Option '$option' has no $kind text."
        }
    }
}
Pass "$(@($options).Count) options checked for label and description texts"

# Dropdown values: every member of each enum used by a setting needs a text.
$enums = [regex]::Matches($settings, 'public enum (\w+)\s*\{([^}]*)\}')
foreach ($e in $enums) {
    $name = $e.Groups[1].Value
    $members = $e.Groups[2].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    foreach ($m in $members) {
        if ($locale -notmatch "GetEnumValueLocaleID\($name\.$m\)") { Fail $localeRel "Dropdown value $name.$m has no text." }
    }
    Pass "${name}: $(@($members).Count) dropdown values have texts"
}

# --- No trademarked names --------------------------------------------------------------------------
$readme = Get-Content (Join-Path $repo 'README.md') -Raw -Encoding UTF8
foreach ($pair in @(@('mod page', "$($config.Publish.DisplayName.Value) $($config.Publish.ShortDescription.Value) $longDescription"), @('README.md', $readme), @('settings texts', $locale))) {
    if ($pair[1] -match '(?i)google') { Fail $pair[0] 'Mentions "Google": say "navigation app" instead (trademark).' }
}
Pass 'No trademarked names'

if ($script:errors -gt 0) {
    Write-Host "$($script:errors) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
exit 0
