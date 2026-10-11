param([string]$GameDir = (& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue) { throw 'Close Bannerlord before model diagnostics.' }
$package = Join-Path $root '.build-game\model-debug-stage\Modules\Mycraft'
$debugModule = Join-Path $GameDir 'Modules\Mycraft.ModelDebug'
if (Test-Path -LiteralPath $debugModule) {
    $archive = Join-Path $root ('.build-game\module-backups\ModelDebug-' + [guid]::NewGuid().ToString('N'))
    Move-Item -LiteralPath $debugModule -Destination $archive
}
Copy-Item -LiteralPath $package -Destination $debugModule -Recurse
[xml]$manifest = Get-Content -LiteralPath (Join-Path $debugModule 'SubModule.xml') -Raw
$manifest.Module.Id.SetAttribute('value','Mycraft.ModelDebug')
$manifest.Module.Name.SetAttribute('value','Mycraft Model Diagnostics')
$manifest.Save((Join-Path $debugModule 'SubModule.xml'))
$bin = Join-Path $GameDir 'bin\Win64_Shipping_Client'
$testProcess = Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -ArgumentList '/singleplayer','_MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Mycraft.ModelDebug*_MODULES_','--mycraft-model-smoke' -WindowStyle Hidden -PassThru
Write-Output "Model diagnostic game PID=$($testProcess.Id); stable Mycraft package remains unchanged."
Write-Output (Join-Path $debugModule 'Logs\mycraft.log')
