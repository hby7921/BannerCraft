param(
    [string]$GameDir=(& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [string]$StageDir=(Join-Path $PSScriptRoot '..\.build-game\entity-stage'),
    [string]$PythonExe='python',
    [int]$Port=38779,
    [switch]$Visible
)
$ErrorActionPreference='Stop'
$workspaceRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$gameRoot=(Resolve-Path -LiteralPath $GameDir).Path
$stageRoot=(Resolve-Path -LiteralPath $StageDir).Path
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){throw 'A user game is running; isolated entity combat smoke refuses to launch.'}
if($Port -lt 1 -or $Port -gt 65535){throw 'Diagnostic port must be in 1..65535.'}
$pythonCommand=Get-Command $PythonExe -ErrorAction Stop
$nativeManifest=[System.Xml.Linq.XDocument]::Load((Join-Path $gameRoot 'Modules\Native\SubModule.xml'))
$nativeVersion=$nativeManifest.Root.Element('Version').Attribute('value').Value
if(-not $nativeVersion.StartsWith('v1.4.8',[StringComparison]::OrdinalIgnoreCase)){throw "This staged diagnostic requires the actual current native game v1.4.8, found $nativeVersion."}
$package=Join-Path $stageRoot 'Modules\Mycraft'
$packageManifest=Join-Path $package 'SubModule.xml'
if(-not(Test-Path -LiteralPath $packageManifest)){throw 'Build the current host into the explicit entity-stage GameFolder first.'}
$document=[System.Xml.Linq.XDocument]::Load($packageManifest)
$dllName=$document.Root.Element('SubModules').Element('SubModule').Element('DLLName').Attribute('value').Value
$stagedBin=[IO.Path]::GetFullPath((Join-Path $package 'bin\Win64_Shipping_Client'))
$dllPath=[IO.Path]::GetFullPath((Join-Path $stagedBin $dllName))
if(-not $dllPath.StartsWith($stagedBin+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    -not(Test-Path -LiteralPath $dllPath)){throw 'Manifest DLLName does not point to the actual staged current-game DLL.'}
$modulesRoot=Join-Path $gameRoot 'Modules'
$target=[IO.Path]::GetFullPath((Join-Path $modulesRoot 'Mycraft.EntitySmoke'))
if(-not $target.StartsWith($modulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Entity diagnostic path escaped exact Modules root.'}
if(Test-Path -LiteralPath $target){throw 'Archive the previous exact Mycraft.EntitySmoke diagnostic folder before another run.'}
Copy-Item -LiteralPath $package -Destination $target -Recurse
$document.Root.Element('Id').SetAttributeValue('value','Mycraft.EntitySmoke')
$document.Root.Element('Name').SetAttributeValue('value','BannerCraft Entity Combat Diagnostics')
$settings=[System.Xml.XmlWriterSettings]::new();$settings.Encoding=[Text.UTF8Encoding]::new($false);$settings.Indent=$true
$writer=[System.Xml.XmlWriter]::Create((Join-Path $target 'SubModule.xml'),$settings)
try{$document.Save($writer)}finally{$writer.Dispose()}
$configPath=Join-Path $target 'Config\demo.json'
$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.enabled=$true;$config.port=$Port;$config.modelTestLights=$false
[IO.File]::WriteAllText($configPath,($config | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$logs=Join-Path $target 'Logs';[void][IO.Directory]::CreateDirectory($logs)
$logPath=Join-Path $logs 'mycraft.log'
$controlPath=Join-Path $logs 'entity-smoke-control.json'
$resultPath=Join-Path $logs 'entity-smoke-peer-result.json'
[IO.File]::WriteAllText($logPath,'',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($controlPath,'{}',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($resultPath,'{}',[Text.UTF8Encoding]::new($false))
$bin=Join-Path $gameRoot 'bin\Win64_Shipping_Client'
$arguments=@('/singleplayer','_MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*Mycraft.EntitySmoke*_MODULES_','--mycraft-model-smoke','--mycraft-entity-scene-smoke')
$windowStyle=if($Visible){'Normal'}else{'Hidden'}
$gameProcess=Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -WindowStyle $windowStyle -PassThru -ArgumentList $arguments
$peerPath=Join-Path $PSScriptRoot 'demo-bridge\entity_combat_smoke_peer.py'
$outputPath=Join-Path $workspaceRoot '.build-game\entity-combat-peer.log'
$errorPath=Join-Path $workspaceRoot '.build-game\entity-combat-peer.err.log'
$peerArguments=@('-u',('"'+$peerPath+'"'),'--port',$Port,'--log',('"'+$logPath+'"'),'--control',('"'+$controlPath+'"'),'--result',('"'+$resultPath+'"'),'--timeout','300')
$peerProcess=Start-Process -FilePath $pythonCommand.Source -WorkingDirectory $workspaceRoot -WindowStyle Hidden -PassThru -ArgumentList $peerArguments -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
[pscustomobject]@{GamePid=$gameProcess.Id;PeerPid=$peerProcess.Id;Log=$logPath;PeerLog=$outputPath;PeerErrors=$errorPath;Control=$controlPath;Result=$resultPath;Module=$target;Port=$Port;DLLName=$dllName;NativeVersion=$nativeVersion}
'Only the isolated diagnostic module and synthetic loopback peer were launched. No MC client/user save opened; no existing game process/module stopped or altered.'
