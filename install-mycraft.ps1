param(
    [string]$GameDir = (& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [string]$PackageDir = (Join-Path $PSScriptRoot '.build-game\stage\Modules\Mycraft')
)
$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $PackageDir 'SubModule.xml'))) { throw "Package incomplete: $PackageDir" }
if (Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue) { throw 'Close Bannerlord before installing.' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PackageDir 'SubModule.xml') -Raw
if ($manifest.Module.Id.value -ne 'Mycraft') { throw 'Package is not BannerCraft.' }
$dllName = [string]$manifest.Module.SubModules.SubModule.DLLName.value
if (-not (Test-Path -LiteralPath (Join-Path $PackageDir 'Visuals\shield.rgba'))) { throw 'Package missing full shield atlas.' }
if (-not (Test-Path -LiteralPath (Join-Path $PackageDir 'Visuals\blocks\tnt.atlas.rgba'))) { throw 'Package missing TNT model atlas.' }
if ($dllName -notmatch '^Mycraft\.v[0-9.]+\.dll$') { throw 'Invalid DLLName in package.' }
foreach ($relative in @("bin\Win64_Shipping_Client\$dllName", 'GUI\Prefabs\MCHotbar.xml', 'GUI\Prefabs\MCInventory.xml','Config\demo.json', 'GUI\MycraftSpriteData.xml', 'GUI\SpriteSheets\MycraftHUD\MycraftHUD_1.rgba', 'Visuals\steve.rgba', 'Visuals\items\diamond_sword.pixels','Visuals\blocks\stone.rgba','Visuals\blocks\dirt.rgba','Visuals\blocks\cobblestone.rgba','Visuals\blocks\oak_planks.rgba','Visuals\blocks\bricks.rgba')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageDir $relative))) { throw "Package missing $relative" }
}
$target = Join-Path $GameDir 'Modules\Mycraft'
$target = [IO.Path]::GetFullPath($target)
$expectedTarget = [IO.Path]::GetFullPath((Join-Path $GameDir 'Modules\Mycraft'))
if ($target -ne $expectedTarget) { throw 'Module target escaped the intended game directory.' }
$backup = $null
$backupDirectory = Join-Path $PSScriptRoot '.build-game\module-backups'
$backupDirectory = [IO.Path]::GetFullPath($backupDirectory)
if (-not $backupDirectory.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup target escaped the workspace.' }
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
if (Test-Path -LiteralPath $target) {
    $backup = Join-Path $backupDirectory ('Mycraft-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $target -Destination $backup
    Write-Output "Backed up existing module to $backup"
}
try { Copy-Item -LiteralPath $PackageDir -Destination $target -Recurse }
catch {
    # Preserve an incomplete copy for diagnosis, then restore the previous module.
    if (Test-Path -LiteralPath $target) {
        Move-Item -LiteralPath $target -Destination (Join-Path $backupDirectory ('incomplete-' + [guid]::NewGuid().ToString('N')))
    }
    if ($backup -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $target }
    throw
}
Write-Output "Installed BannerCraft to $target"
Write-Output "Enable 'BannerCraft' and 'Bannerlord.Harmony' in the launcher before starting the game."
