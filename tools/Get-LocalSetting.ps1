<#
.SYNOPSIS
Read a BannerCraft path from private local configuration or an environment variable.
.DESCRIPTION
The optional .bannercraft.local.json file lives at the checkout root and is ignored
by Git. Environment variables take precedence. No machine-specific path is built in.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('bannerlordGameDir', 'skinFile')]
    [string]$Name
)

$ErrorActionPreference = 'Stop'
$environmentName = if ($Name -eq 'bannerlordGameDir') { 'BANNERLORD_GAME_DIR' } else { 'BANNERCRAFT_SKIN_FILE' }
$value = [Environment]::GetEnvironmentVariable($environmentName)
if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }

$settingsFile = Join-Path $PSScriptRoot '..\.bannercraft.local.json'
if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
    $settings = Get-Content -LiteralPath $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $property = $settings.PSObject.Properties[$Name]
    if ($null -ne $property -and $null -ne $property.Value) {
        if ($property.Value -isnot [string]) { throw "Local setting '$Name' must be a path string." }
        if (-not [string]::IsNullOrWhiteSpace($property.Value)) { return $property.Value }
    }
}

if ($Name -eq 'skinFile') { return '' }
throw 'Specify -GameDir (or -GameRoot), set BANNERLORD_GAME_DIR, or configure bannerlordGameDir in .bannercraft.local.json.'
