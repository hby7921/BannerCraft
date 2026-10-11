param(
    [string]$GameDir = (& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir)
)
$ErrorActionPreference = 'Stop'
$target = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'Modules\Mycraft'
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Output "Removed $target"
} else {
    Write-Output "BannerCraft module not installed."
}
Get-ChildItem -LiteralPath (Join-Path (Split-Path $target -Parent) '.') -Filter 'Mycraft.bak-*' -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force; Write-Output "Removed backup $($_.Name)" }
