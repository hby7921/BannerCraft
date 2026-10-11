param([string]$GameDir=(& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir))
$ErrorActionPreference='Stop'
$taskGameRoot=(Resolve-Path -LiteralPath $GameDir).Path
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){
    Write-Output 'Bannerlord is already running.'
    return
}
# Read the exact installed module IDs; SandBox is the folder, Sandbox is its Id.
$taskModuleIds=foreach($taskModuleFolder in @('Bannerlord.Harmony','Native','SandBoxCore','CustomBattle','SandBox','Mycraft')){
    $taskManifest=Join-Path $taskGameRoot "Modules\$taskModuleFolder\SubModule.xml"
    [xml]$taskModuleXml=Get-Content -LiteralPath $taskManifest -Raw
    $taskId=[string]$taskModuleXml.Module.Id.value
    if([string]::IsNullOrWhiteSpace($taskId) -or $taskId.Contains('*')){throw "Invalid module Id: $taskModuleFolder"}
    $taskId
}
$taskNativeBin=Join-Path $taskGameRoot 'bin\Win64_Shipping_Client'
$taskModules='_MODULES_*'+($taskModuleIds -join '*')+'*_MODULES_'
$taskGameProcess=Start-Process -FilePath (Join-Path $taskNativeBin 'Bannerlord.exe') -WorkingDirectory $taskNativeBin `
    -ArgumentList '/singleplayer',$taskModules -WindowStyle Normal -PassThru
Write-Output "Bannerlord PID=$($taskGameProcess.Id); modules=$($taskModuleIds -join ', ')"
