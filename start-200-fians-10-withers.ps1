param([string]$GameDir=(& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir),[ValidateSet(200,600)][int]$FianCount=200)
$ErrorActionPreference='Stop'
$taskGameRoot=(Resolve-Path -LiteralPath $GameDir).Path
if(-not(Test-Path -LiteralPath (Join-Path $taskGameRoot 'Modules\Mycraft\ModuleData\mc_custom_troops.xml'))){throw 'Install the BannerCraft custom army package first.'}
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){
    Write-Output "Bannerlord is already running. Return to its main menu or custom-battle setup, then enter: mycraft.fians_wither 10 $FianCount"
    return
}
$taskModuleIds=foreach($taskModuleFolder in @('Bannerlord.Harmony','Native','SandBoxCore','CustomBattle','SandBox','Mycraft')){
    [xml]$taskModuleXml=Get-Content -LiteralPath (Join-Path $taskGameRoot "Modules\$taskModuleFolder\SubModule.xml") -Raw
    $taskId=[string]$taskModuleXml.Module.Id.value
    if([string]::IsNullOrWhiteSpace($taskId) -or $taskId.Contains('*')){throw "Invalid module Id: $taskModuleFolder"}
    $taskId
}
$taskNativeBin=Join-Path $taskGameRoot 'bin\Win64_Shipping_Client'
$taskModules='_MODULES_*'+($taskModuleIds -join '*')+'*_MODULES_'
$taskFlag=if($FianCount -eq 600){'--mycraft-600-fians-wither-10'}else{'--mycraft-fians-wither-10'}
$taskGameProcess=Start-Process -FilePath (Join-Path $taskNativeBin 'Bannerlord.exe') -WorkingDirectory $taskNativeBin -ArgumentList '/singleplayer',$taskModules,$taskFlag -WindowStyle Normal -PassThru
Write-Output "Bannerlord PID=$($taskGameProcess.Id); preset=$FianCount Fian Champions + player commander versus 10 Withers. Connect Minecraft with /mycraft connect."
