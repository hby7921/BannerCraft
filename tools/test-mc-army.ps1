param(
    [string]$GameDir=(& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [string]$StageDir=(Join-Path $PSScriptRoot '..\.build-game\army-stage'),
    [string]$PythonExe='python',
    [int]$Port=38780,
    [switch]$Visible
)
$ErrorActionPreference='Stop'
$taskWorkspaceRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$taskGameRoot=(Resolve-Path -LiteralPath $GameDir).Path
$taskStageRoot=(Resolve-Path -LiteralPath $StageDir).Path
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){throw 'A user Bannerlord game is running; army diagnostic refuses to launch.'}
if($Port -lt 1 -or $Port -gt 65535){throw 'Diagnostic port must be in 1..65535.'}
$taskPythonCommand=Get-Command $PythonExe -ErrorAction Stop
$taskNativeManifest=[System.Xml.Linq.XDocument]::Load((Join-Path $taskGameRoot 'Modules\Native\SubModule.xml'))
$taskNativeVersion=$taskNativeManifest.Root.Element('Version').Attribute('value').Value
if(-not $taskNativeVersion.StartsWith('v1.4.8',[StringComparison]::OrdinalIgnoreCase)){throw "Army diagnostic requires installed v1.4.8, found $taskNativeVersion."}
$taskPackage=[IO.Path]::GetFullPath((Join-Path $taskStageRoot 'Modules\Mycraft'))
$taskPackageManifest=Join-Path $taskPackage 'SubModule.xml'
if(-not(Test-Path -LiteralPath $taskPackageManifest)){throw 'Build the current module into explicit army-stage before this diagnostic.'}
$taskDocument=[System.Xml.Linq.XDocument]::Load($taskPackageManifest)
$taskDllName=$taskDocument.Root.Element('SubModules').Element('SubModule').Element('DLLName').Attribute('value').Value
$taskStagedBin=[IO.Path]::GetFullPath((Join-Path $taskPackage 'bin\Win64_Shipping_Client'))
$taskDllPath=[IO.Path]::GetFullPath((Join-Path $taskStagedBin $taskDllName))
if(-not $taskDllPath.StartsWith($taskStagedBin+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    -not(Test-Path -LiteralPath $taskDllPath)){throw 'Staged manifest DLLName does not point to its actual current-game DLL.'}
$taskRequiredXml=@{'SPCultures'='mc_custom_culture';'NPCCharacters'='mc_custom_troops'}
foreach($taskXmlType in $taskRequiredXml.Keys){
    $taskXmlNode=$taskDocument.Root.Element('Xmls').Elements('XmlNode') | Where-Object {
        $_.Element('XmlName').Attribute('id').Value -eq $taskXmlType -and $_.Element('XmlName').Attribute('path').Value -eq $taskRequiredXml[$taskXmlType]
    }
    if(-not $taskXmlNode -or $taskXmlNode.Element('IncludedGameTypes').Element('GameType').Attribute('value').Value -ne 'CustomGame'){
        throw "Missing custom-only staged $taskXmlType registration."
    }
}
$taskModulesRoot=[IO.Path]::GetFullPath((Join-Path $taskGameRoot 'Modules'))
$taskTarget=[IO.Path]::GetFullPath((Join-Path $taskModulesRoot 'Mycraft.ArmySmoke'))
if(-not $taskTarget.StartsWith($taskModulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic target escaped game Modules root.'}
if(Test-Path -LiteralPath $taskTarget){
    $taskResolvedTarget=(Resolve-Path -LiteralPath $taskTarget).Path
    if(-not $taskResolvedTarget.StartsWith($taskModulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Resolved diagnostic target escaped game Modules root.'}
    $taskExistingManifest=[System.Xml.Linq.XDocument]::Load((Join-Path $taskResolvedTarget 'SubModule.xml'))
    if($taskExistingManifest.Root.Element('Id').Attribute('value').Value -ne 'Mycraft.ArmySmoke'){throw 'Existing diagnostic folder is not the owned Mycraft.ArmySmoke module.'}
    $taskBackupRoot=[IO.Path]::GetFullPath((Join-Path $taskWorkspaceRoot '.build-game\module-backups'))
    [void][IO.Directory]::CreateDirectory($taskBackupRoot)
    $taskBackup=[IO.Path]::GetFullPath((Join-Path $taskBackupRoot ('Mycraft.ArmySmoke-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))))
    if(-not $taskBackup.StartsWith($taskBackupRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic backup escaped workspace backup root.'}
    Move-Item -LiteralPath $taskResolvedTarget -Destination $taskBackup
}
Copy-Item -LiteralPath $taskPackage -Destination $taskTarget -Recurse
$taskDocument.Root.Element('Id').SetAttributeValue('value','Mycraft.ArmySmoke')
$taskDocument.Root.Element('Name').SetAttributeValue('value','BannerCraft Native Army Diagnostics')
$taskSettings=[System.Xml.XmlWriterSettings]::new();$taskSettings.Encoding=[Text.UTF8Encoding]::new($false);$taskSettings.Indent=$true
$taskWriter=[System.Xml.XmlWriter]::Create((Join-Path $taskTarget 'SubModule.xml'),$taskSettings)
try{$taskDocument.Save($taskWriter)}finally{$taskWriter.Dispose()}
$taskConfigPath=Join-Path $taskTarget 'Config\demo.json'
$taskConfig=Get-Content -LiteralPath $taskConfigPath -Raw | ConvertFrom-Json
$taskConfig.enabled=$true;$taskConfig.port=$Port;$taskConfig.modelTestLights=$false
[IO.File]::WriteAllText($taskConfigPath,($taskConfig | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$taskLogs=Join-Path $taskTarget 'Logs';[void][IO.Directory]::CreateDirectory($taskLogs)
$taskLogPath=Join-Path $taskLogs 'mycraft.log'
$taskControlPath=Join-Path $taskLogs 'mc-army-smoke-control.json'
$taskResultPath=Join-Path $taskLogs 'mc-army-smoke-peer-result.json'
foreach($taskJsonPath in @($taskControlPath,$taskResultPath)){[IO.File]::WriteAllText($taskJsonPath,'{}',[Text.UTF8Encoding]::new($false))}
$taskBin=Join-Path $taskGameRoot 'bin\Win64_Shipping_Client'
$taskModuleIds=foreach($taskModuleFolder in @('Bannerlord.Harmony','Native','SandBoxCore','CustomBattle','SandBox')){
    $taskOriginalManifest=[System.Xml.Linq.XDocument]::Load((Join-Path $taskModulesRoot "$taskModuleFolder\SubModule.xml"))
    $taskOriginalId=$taskOriginalManifest.Root.Element('Id').Attribute('value').Value
    if([string]::IsNullOrWhiteSpace($taskOriginalId) -or $taskOriginalId.Contains('*')){throw "Invalid installed module id: $taskModuleFolder"}
    $taskOriginalId
}
$taskModuleIds+='Mycraft.ArmySmoke'
$taskModules='_MODULES_*'+($taskModuleIds -join '*')+'*_MODULES_'
$taskArguments=@('/singleplayer',$taskModules,'--mycraft-fians-wither','--mycraft-army-smoke')
$taskWindowStyle=if($Visible){'Normal'}else{'Hidden'}
$taskGameProcess=Start-Process -FilePath (Join-Path $taskBin 'Bannerlord.exe') -WorkingDirectory $taskBin -WindowStyle $taskWindowStyle -PassThru -ArgumentList $taskArguments
$taskPeerPath=Join-Path $PSScriptRoot 'demo-bridge\mc_army_smoke_peer.py'
$taskOutputPath=Join-Path $taskWorkspaceRoot '.build-game\mc-army-peer.log'
$taskErrorPath=Join-Path $taskWorkspaceRoot '.build-game\mc-army-peer.err.log'
$taskPeerArguments=@('-u',('"'+$taskPeerPath+'"'),'--port',$Port,'--log',('"'+$taskLogPath+'"'),'--control',('"'+$taskControlPath+'"'),'--result',('"'+$taskResultPath+'"'),'--timeout','300')
$taskPeerProcess=Start-Process -FilePath $taskPythonCommand.Source -WorkingDirectory $taskWorkspaceRoot -WindowStyle Hidden -PassThru -ArgumentList $taskPeerArguments -RedirectStandardOutput $taskOutputPath -RedirectStandardError $taskErrorPath
$taskLaunch=[pscustomobject]@{GamePid=$taskGameProcess.Id;PeerPid=$taskPeerProcess.Id;Log=$taskLogPath;PeerLog=$taskOutputPath;PeerErrors=$taskErrorPath;Control=$taskControlPath;Result=$taskResultPath;Module=$taskTarget;Port=$Port;DLLName=$taskDllName;NativeVersion=$taskNativeVersion}
[IO.File]::WriteAllText((Join-Path $taskWorkspaceRoot '.build-game\mc-army-smoke-launched.json'),($taskLaunch | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$taskLaunch
'Only the isolated Mycraft.ArmySmoke module and synthetic loopback peer were launched. Current Minecraft, user saves and installed BannerCraft were preserved.'
