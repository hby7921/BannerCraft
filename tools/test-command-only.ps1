param(
    [string]$GameDir=(& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir),
    [string]$PythonExe='python',
    [int]$Port=38769,
    [switch]$Visible
)
$ErrorActionPreference='Stop'
$workspaceRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$gameRoot=(Resolve-Path -LiteralPath $GameDir).Path
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){throw 'A game is running; cannot launch isolated command-only diagnostics.'}
if($Port -lt 1 -or $Port -gt 65535){throw 'Diagnostic port must be in 1..65535.'}
$pythonCommand=Get-Command $PythonExe -ErrorAction Stop
$modulesRoot=Join-Path $gameRoot 'Modules'
$target=[IO.Path]::GetFullPath((Join-Path $modulesRoot 'Mycraft.CommandSmoke'))
if(-not $target.StartsWith($modulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic module path escaped Modules.'}
if(Test-Path -LiteralPath $target){
    if(Test-Path -LiteralPath (Join-Path $target 'SubModule.xml')){throw 'Archive the previous exact Mycraft.CommandSmoke diagnostic folder before another run.'}
    $residualRoot=(Resolve-Path -LiteralPath $target).Path
    if($residualRoot -ne $target -or -not $residualRoot.StartsWith($modulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Residual diagnostic module resolved outside its exact Modules path.'
    }
    $residualEntries=@(Get-ChildItem -LiteralPath $residualRoot -Force -Recurse)
    $residualFiles=@($residualEntries | Where-Object {-not $_.PSIsContainer})
    if(((Get-Item -LiteralPath $residualRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @($residualEntries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0){
        throw 'Residual diagnostic module contains a reparse point; automatic archive refused.'
    }
    $residualBinPrefix=(Join-Path $residualRoot 'bin')+[IO.Path]::DirectorySeparatorChar
    if($residualFiles.Count -eq 0 -or @($residualFiles | Where-Object {
        $_.Extension -notin '.dll','.pdb' -or
        -not $_.FullName.StartsWith($residualBinPrefix,[StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0){throw 'Diagnostic folder without manifest contains more than stale DLL/PDB files; automatic archive refused.'}
    $archiveRoot=[IO.Path]::GetFullPath((Join-Path $workspaceRoot '.build-game\module-backups'))
    if(-not $archiveRoot.StartsWith($workspaceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Archive root escaped workspace.'}
    [void][IO.Directory]::CreateDirectory($archiveRoot)
    $archiveRoot=(Resolve-Path -LiteralPath $archiveRoot).Path
    $archivePath=[IO.Path]::GetFullPath((Join-Path $archiveRoot ('CommandSmoke-StaleBin-'+[Guid]::NewGuid().ToString('N'))))
    if(-not $archivePath.StartsWith($archiveRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        -not $archiveRoot.StartsWith($workspaceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $archivePath)){throw 'Stale diagnostic archive destination is unsafe or already exists.'}
    Move-Item -LiteralPath $residualRoot -Destination $archivePath
    "Archived stale diagnostic DLL/PDB only: $archivePath"
}
$package=Join-Path $workspaceRoot '.build-game\stage\Modules\Mycraft'
if(-not(Test-Path -LiteralPath (Join-Path $package 'SubModule.xml'))){throw 'Build the current staged native module first.'}
Copy-Item -LiteralPath $package -Destination $target -Recurse
$manifestPath=Join-Path $target 'SubModule.xml'
$document=[System.Xml.Linq.XDocument]::Load($manifestPath)
$document.Root.Element('Id').SetAttributeValue('value','Mycraft.CommandSmoke')
$document.Root.Element('Name').SetAttributeValue('value','BannerCraft Command-only Diagnostics')
$settings=[System.Xml.XmlWriterSettings]::new();$settings.Encoding=[Text.UTF8Encoding]::new($false);$settings.Indent=$true
$writer=[System.Xml.XmlWriter]::Create($manifestPath,$settings)
try{$document.Save($writer)}finally{$writer.Dispose()}
$configPath=Join-Path $target 'Config\demo.json'
$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.enabled=$true;$config.port=$Port;$config.modelTestLights=$false
[IO.File]::WriteAllText($configPath,($config | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$logPath=Join-Path $target 'Logs\mycraft.log'
# Do not let copied staged log receipts satisfy this run's peer gate.
if(Test-Path -LiteralPath $logPath){[IO.File]::WriteAllText($logPath,'',[Text.UTF8Encoding]::new($false))}
$bin=Join-Path $gameRoot 'bin\Win64_Shipping_Client'
$arguments=@('/singleplayer','_MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Sandbox*Mycraft.CommandSmoke*_MODULES_','--mycraft-model-smoke','--mycraft-command-only-smoke')
$windowStyle=if($Visible){'Normal'}else{'Hidden'}
$gameProcess=Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -WindowStyle $windowStyle -PassThru -ArgumentList $arguments
$peerPath=Join-Path $PSScriptRoot 'demo-bridge\command_only_smoke_peer.py'
$outputPath=Join-Path $workspaceRoot '.build-game\command-only-peer.log'
$errorPath=Join-Path $workspaceRoot '.build-game\command-only-peer.err.log'
$peerArguments=@('-u',('"'+$peerPath+'"'),'--port',$Port,'--log',('"'+$logPath+'"'),'--timeout','300')
$peerProcess=Start-Process -FilePath $pythonCommand.Source -WorkingDirectory $workspaceRoot -WindowStyle Hidden -PassThru -ArgumentList $peerArguments -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
[pscustomobject]@{GamePid=$gameProcess.Id;PeerPid=$peerProcess.Id;Log=$logPath;PeerLog=$outputPath;PeerErrors=$errorPath;Module=$target;Port=$Port}
'No user save is opened. Only these diagnostic processes were launched; this script stops or moves no game process/module.'
