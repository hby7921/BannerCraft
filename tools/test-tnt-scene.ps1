param([string]$GameDir=(& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name bannerlordGameDir),[switch]$DeathSmoke,[switch]$Visible,[switch]$WeaponSmoke)
$ErrorActionPreference='Stop'
$workspaceRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$gameRoot=(Resolve-Path -LiteralPath $GameDir).Path
if(Get-Process -Name Bannerlord,Bannerlord.Native -ErrorAction SilentlyContinue){throw 'A game is running; cannot launch isolated TNT scene smoke.'}
$modulesRoot=Join-Path $gameRoot 'Modules'
$target=[IO.Path]::GetFullPath((Join-Path $modulesRoot 'Mycraft.TntSmoke'))
if(-not $target.StartsWith($modulesRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Diagnostic module path escaped Modules.'}
if(Test-Path -LiteralPath $target){throw 'Archive the previous exact Mycraft.TntSmoke diagnostic folder before another run.'}
$package=Join-Path $workspaceRoot '.build-game\stage\Modules\Mycraft'
$receipts=Join-Path $workspaceRoot 'fabric\build\tnt-native-events.jsonl'
if(-not(Test-Path -LiteralPath $receipts)){throw 'Run the native TNT GameTest first to produce its real explosion damage receipts.'}
Copy-Item -LiteralPath $package -Destination $target -Recurse
Copy-Item -LiteralPath $receipts -Destination (Join-Path $target 'tnt-native-events.jsonl')
if($WeaponSmoke){
    foreach($taskWireFixture in @('redstone-wire-power0.bin','redstone-wire-power15.bin')){
        $taskWireSourceName=$taskWireFixture.Replace('-','_')
        Copy-Item -LiteralPath (Join-Path $workspaceRoot "tests\Building\Fixtures\$taskWireSourceName") -Destination (Join-Path $target $taskWireFixture)
    }
}
$manifestPath=Join-Path $target 'SubModule.xml'
$document=[System.Xml.Linq.XDocument]::Load($manifestPath)
$document.Root.Element('Id').SetAttributeValue('value','Mycraft.TntSmoke')
$document.Root.Element('Name').SetAttributeValue('value','Mycraft TNT Scene Diagnostics')
$settings=[System.Xml.XmlWriterSettings]::new();$settings.Encoding=[Text.UTF8Encoding]::new($false);$settings.Indent=$true
$writer=[System.Xml.XmlWriter]::Create($manifestPath,$settings)
try{$document.Save($writer)}finally{$writer.Dispose()}
$bin=Join-Path $gameRoot 'bin\Win64_Shipping_Client'
$smokeArguments=@('/singleplayer','_MODULES_*Bannerlord.Harmony*Native*SandBoxCore*CustomBattle*Mycraft.TntSmoke*_MODULES_','--mycraft-model-smoke','--mycraft-tnt-scene-smoke')
if($DeathSmoke){$smokeArguments+='--mycraft-tnt-death-smoke'}
if($WeaponSmoke){$smokeArguments+='--mycraft-weapon-scene-smoke'}
$windowStyle=if($Visible){'Normal'}else{'Hidden'}
$process=Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -WindowStyle $windowStyle -PassThru -ArgumentList $smokeArguments
"TNT smoke PID=$($process.Id)"
"Log=$(Join-Path $target 'Logs\mycraft.log')"
"This process uses only the diagnostic module; no network bridge or user save is opened."
