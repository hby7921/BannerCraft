param([string]$GameDir=(& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir),[ValidateSet(200,300)][int]$FianCount=200)
$ErrorActionPreference='Stop'
$taskGameRoot=(Resolve-Path -LiteralPath $GameDir).Path
if(-not(Test-Path -LiteralPath (Join-Path $taskGameRoot 'Modules\Mycraft\ModuleData\mc_custom_troops.xml'))){
    throw 'The 200 Fians / 3 Withers preset is not installed yet. Close both games and run install-200-fians-3-withers.cmd first.'
}
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){
    Write-Output "Bannerlord is already running. Return to the main menu or custom-battle setup, then enter: mycraft.fians_wither 3 $FianCount"
    return
}
$taskModuleIds=foreach($taskModuleFolder in @('Bannerlord.Harmony','Native','SandBoxCore','CustomBattle','SandBox','Mycraft')){
    $taskManifest=Join-Path $taskGameRoot "Modules\$taskModuleFolder\SubModule.xml"
    [xml]$taskModuleXml=Get-Content -LiteralPath $taskManifest -Raw
    $taskId=[string]$taskModuleXml.Module.Id.value
    if([string]::IsNullOrWhiteSpace($taskId) -or $taskId.Contains('*')){throw "Invalid module Id: $taskModuleFolder"}
    $taskId
}
$taskNativeBin=Join-Path $taskGameRoot 'bin\Win64_Shipping_Client'
$taskModules='_MODULES_*'+($taskModuleIds -join '*')+'*_MODULES_'
$taskFlag=if($FianCount -eq 300){'--mycraft-300-fians-wither-3'}else{'--mycraft-fians-wither'}
$taskGameProcess=Start-Process -FilePath (Join-Path $taskNativeBin 'Bannerlord.exe') -WorkingDirectory $taskNativeBin `
    -ArgumentList '/singleplayer',$taskModules,$taskFlag -WindowStyle Normal -PassThru
Write-Output "Bannerlord PID=$($taskGameProcess.Id); preset=$FianCount Fian Champions + player commander versus 3 Withers. Connect Minecraft with /mycraft connect."
