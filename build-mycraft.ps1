param(
    [string]$GameDir = (& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [ValidateSet('Beta_Debug', 'Beta_Release')][string]$Configuration = 'Beta_Release',
    [string]$Stage = (Join-Path $PSScriptRoot '.build-game\stage')
)
$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
New-Item -ItemType Directory -Path $Stage -Force | Out-Null
$project = Join-Path $PSScriptRoot 'src\Mycraft\Mycraft.csproj'
& dotnet build $project -c $Configuration -p:Platform=x64 `
    -p:UseLocalGameReferences=true -p:LocalGameDir="$GameDir" -p:GameFolder="$Stage" --nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
[xml]$submodule = Get-Content -LiteralPath (Join-Path $Stage 'Modules\Mycraft\SubModule.xml') -Raw
$dllName = [string]$submodule.Module.SubModules.SubModule.DLLName.value
if ($dllName -notmatch '^Mycraft\.v[0-9.]+\.dll$') { throw 'Invalid staged DLLName.' }
$dll = Get-Item -LiteralPath (Join-Path $Stage "Modules\Mycraft\bin\Win64_Shipping_Client\$dllName")
if (-not (Test-Path -LiteralPath (Join-Path $Stage 'Modules\Mycraft\Visuals\shield.rgba'))) { throw 'Full shield atlas missing from staging.' }
if (-not (Test-Path -LiteralPath (Join-Path $Stage 'Modules\Mycraft\Visuals\blocks\tnt.atlas.rgba'))) { throw 'TNT model atlas missing from staging.' }
for($frame=0;$frame -lt 16;$frame++) {
    if(-not(Test-Path -LiteralPath (Join-Path $Stage "Modules\Mycraft\Visuals\particles\explosion_$frame.rgba"))){throw "TNT explosion frame $frame missing."}
}
for($frame=0;$frame -lt 8;$frame++) {
    $smokeFrame=Join-Path $Stage "Modules\Mycraft\Visuals\particles\smoke_$frame.rgba"
    if(-not(Test-Path -LiteralPath $smokeFrame) -or (Get-Item -LiteralPath $smokeFrame).Length -ne 256){throw "TNT smoke frame $frame missing or invalid."}
}
$legacyCharacterFile = Join-Path $Stage 'Modules\Mycraft\ModuleData\mc_characters.xml'
if (Test-Path -LiteralPath $legacyCharacterFile) { Remove-Item -LiteralPath $legacyCharacterFile -Force }
foreach ($relative in @('GUI\Prefabs\MCHotbar.xml', 'GUI\Prefabs\MCInventory.xml', 'Config\demo.json', 'GUI\MycraftSpriteData.xml', 'GUI\SpriteSheets\MycraftHUD\MycraftHUD_1.rgba', 'Visuals\steve.rgba', 'Visuals\items\diamond_sword.pixels', 'Visuals\blocks\stone.rgba','Visuals\blocks\dirt.rgba','Visuals\blocks\cobblestone.rgba','Visuals\blocks\oak_planks.rgba','Visuals\blocks\bricks.rgba')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Stage "Modules\Mycraft\$relative"))) { throw "Staging incomplete: $relative" }
}
Write-Output "Verified staging: $($dll.Name)"
