#requires -Version 7.0


<#
.SYNOPSIS
Build BannerCraft's local Minecraft assets from a player-supplied client JAR.

.DESCRIPTION
Uses the existing asset generators in this checkout. All generated assets,
previews, Java classes and test fixtures stay in this checkout. The script does
not download files, install a module or launch either game.

Requires Windows, PowerShell 7, Python 3 with Pillow, a JDK capable of running
Minecraft Java 26.3 (JDK 25 or newer), and that client's local library JARs.
Existing generated assets in this checkout are overwritten.

.PARAMETER ClientJar
Path to your local Minecraft Java 26.3 client JAR.

.PARAMETER Jdk
JDK root directory containing bin/java.exe and bin/javac.exe.

.PARAMETER Libraries
Directory containing the client's dependency JARs, searched recursively.
For Prism Launcher, use its libraries directory.

.PARAMETER PythonExe
Python executable path or command name. Defaults to python. Pillow must already
be installed in that interpreter.

.EXAMPLE
pwsh -File ./tools/prepare-local-assets.ps1 -ClientJar 'C:/Games/Minecraft/26.3.jar' -Jdk 'C:/Java/jdk-25' -Libraries 'C:/Games/PrismLauncher/libraries'

.NOTES
Outputs: src/Mycraft/_Module/Visuals, src/Mycraft/_Module/GUI/SpriteSheets,
src/Mycraft/_Module/GUI/MycraftSpriteData.xml, tools/fixtures,
tests/EntityVisuals/Fixtures and .build-game.
GenericMobVisuals fixtures require the separate native model oracle workflow.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ClientJar,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Jdk,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Libraries,

    [ValidateNotNullOrEmpty()]
    [string]$PythonExe = 'python'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The existing asset generators require Windows and PowerShell 7.' }

function Resolve-RequiredPath([string]$Path, [string]$Kind, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType $Kind)) {
        throw "$Label does not exist or is not a $($Kind.ToLowerInvariant()): $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Invoke-NativeTool([string]$Executable, [string[]]$Arguments, [string]$Label) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit code $LASTEXITCODE)." }
}

function Quote-JavaArgument([string]$Value) {
    # Forward slashes also work on Windows and avoid Java argfile backslash escapes.
    return '"' + $Value.Replace('\', '/').Replace('"', '\"') + '"'
}

$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ClientJar = Resolve-RequiredPath $ClientJar 'Leaf' 'ClientJar'
$Jdk = Resolve-RequiredPath $Jdk 'Container' 'Jdk'
$Libraries = Resolve-RequiredPath $Libraries 'Container' 'Libraries'
$java = Resolve-RequiredPath (Join-Path $Jdk 'bin/java.exe') 'Leaf' 'Java executable'
$javac = Resolve-RequiredPath (Join-Path $Jdk 'bin/javac.exe') 'Leaf' 'Java compiler'
$python = (Get-Command -Name $PythonExe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

$requiredSources = @(
    'tools/SkullModelExporter.java',
    'tools/EntityModelExporter.java',
    'tools/build-mc-model.ps1',
    'tools/build-block-assets.ps1',
    'tools/build-tnt-effects.ps1',
    'tools/build-entity-assets.py',
    'tools/build-weapon-assets.py',
    'tools/build-mc-hud.ps1',
    'src/Mycraft/Visuals/VoxelGeometry.cs',
    'src/Mycraft/Visuals/MinecraftGuiProjection.cs',
    'src/Mycraft/Building/BlockGeometry.cs',
    'src/Mycraft/Building/BlockModelSnapshot.cs',
    'src/Mycraft/Building/BlockSurfaceGeometry.cs'
)
foreach ($source in $requiredSources) {
    [void](Resolve-RequiredPath (Join-Path $workspace $source) 'Leaf' 'Generator source')
}

$libraryJars = @(Get-ChildItem -LiteralPath $Libraries -File -Recurse -Filter '*.jar' |
    Where-Object {
        $_.FullName.Replace('\', '/') -notmatch '/com/mojang/minecraft/' -and
        -not $_.FullName.Equals($ClientJar, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object FullName)
if ($libraryJars.Count -eq 0) { throw "No dependency JARs found in Libraries: $Libraries" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing.Common
$archive = [IO.Compression.ZipFile]::OpenRead($ClientJar)
try {
    $versionEntry = $archive.GetEntry('version.json')
    if ($null -eq $versionEntry) { throw 'ClientJar is missing Minecraft version.json.' }
    $reader = [IO.StreamReader]::new($versionEntry.Open())
    try { $version = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($version.id -ne '26.3') { throw "The asset generators are pinned to Minecraft Java 26.3; ClientJar reports '$($version.id)'." }
    foreach ($entry in @('assets/minecraft/textures/entity/player/wide/steve.png', 'assets/minecraft/items/wither_skeleton_skull.json')) {
        if ($null -eq $archive.GetEntry($entry)) { throw "ClientJar is missing $entry" }
    }
} finally { $archive.Dispose() }
Invoke-NativeTool $python @('-c', 'import sys; assert sys.version_info.major == 3, "Python 3 is required"; from PIL import Image; print("Python and Pillow ready (" + Image.__version__ + ").")') 'Python/Pillow preflight'

$scratch = Join-Path $workspace '.build-game/asset-preparation/skull-exporter'
$fixtureDir = Join-Path $PSScriptRoot 'fixtures'
$fixture = Join-Path $fixtureDir 'wither-skull-gui-model-26.3.json'
$visuals = Join-Path $workspace 'src/Mycraft/_Module/Visuals'
$moduleGui = Join-Path $workspace 'src/Mycraft/_Module/GUI'
$previewRoot = Join-Path $workspace '.build-game/asset-preparation/previews'
[void](New-Item -ItemType Directory -Force -Path $scratch, $fixtureDir)

# Java argfiles keep large launcher library classpaths below the command-line limit.
$classPath = (@($ClientJar) + @($libraryJars.FullName)) -join ';'
$javacArgs = Join-Path $scratch 'javac.args'
$javaArgs = Join-Path $scratch 'java.args'
$utf8 = [Text.UTF8Encoding]::new($false)
$compileArguments = @(
    '-encoding', 'UTF-8', '-classpath', (Quote-JavaArgument $classPath),
    '-d', (Quote-JavaArgument $scratch),
    (Quote-JavaArgument (Join-Path $PSScriptRoot 'SkullModelExporter.java'))
)
[IO.File]::WriteAllText($javacArgs, ($compileArguments -join "`n") + "`n", $utf8)
$runArguments = @(
    '-classpath', (Quote-JavaArgument ($classPath + ';' + $scratch)),
    'SkullModelExporter', (Quote-JavaArgument $ClientJar), (Quote-JavaArgument $fixture)
)
[IO.File]::WriteAllText($javaArgs, ($runArguments -join "`n") + "`n", $utf8)

Write-Host '[1/7] Exporting the native wither skull GUI geometry.'
Invoke-NativeTool $javac @('@' + $javacArgs) 'Skull exporter compilation'
Invoke-NativeTool $java @('@' + $javaArgs) 'Skull geometry export'
[void](Resolve-RequiredPath $fixture 'Leaf' 'Generated skull fixture')

Write-Host '[2/7] Exporting the default Steve skin, held items and shield.'
# These shared sources contain nullable annotations; older generators omit the flag.
if (-not ('Mycraft.Visuals.MinecraftGuiProjection' -as [type])) {
    Add-Type -CompilerOptions '/nullable:enable' -Path (Join-Path $workspace 'src/Mycraft/Visuals/VoxelGeometry.cs'), (Join-Path $workspace 'src/Mycraft/Visuals/MinecraftGuiProjection.cs')
}
& (Join-Path $PSScriptRoot 'build-mc-model.ps1') -ClientJar $ClientJar -VisualsDir $visuals -PreviewDir (Join-Path $previewRoot 'model') -SkinFile ''
Write-Host '[3/7] Exporting blocks and the TNT atlas.'
& (Join-Path $PSScriptRoot 'build-block-assets.ps1') -ClientJar $ClientJar -OutputDir (Join-Path $visuals 'blocks')
Write-Host '[4/7] Exporting TNT explosion and smoke frames.'
& (Join-Path $PSScriptRoot 'build-tnt-effects.ps1') -ClientJar $ClientJar -OutputDir (Join-Path $visuals 'particles') -PreviewDir (Join-Path $previewRoot 'tnt')
Write-Host '[5/7] Exporting entity models, textures and pose fixtures.'
Invoke-NativeTool $python @((Join-Path $PSScriptRoot 'build-entity-assets.py'), '--client-jar', $ClientJar, '--jdk', $Jdk, '--libraries', $Libraries, '--output', (Join-Path $visuals 'entities')) 'Entity asset export'
Write-Host '[6/7] Exporting held spear and projectile textures.'
Invoke-NativeTool $python @((Join-Path $PSScriptRoot 'build-weapon-assets.py'), '--client-jar', $ClientJar, '--output', (Join-Path $visuals 'weapons')) 'Weapon asset export'
Write-Host '[7/7] Building HUD sprite sheets and sprite metadata.'
& (Join-Path $PSScriptRoot 'build-mc-hud.ps1') -ClientJar $ClientJar -ModuleGui $moduleGui -PreviewDir (Join-Path $previewRoot 'hud')

foreach ($output in @(
    'src/Mycraft/_Module/Visuals/manifest.json',
    'src/Mycraft/_Module/Visuals/blocks/tnt.manifest.json',
    'src/Mycraft/_Module/Visuals/particles/explosion.manifest.json',
    'src/Mycraft/_Module/Visuals/entities/manifest.json',
    'src/Mycraft/_Module/Visuals/weapons/manifest.json',
    'src/Mycraft/_Module/GUI/MycraftSpriteData.xml',
    'src/Mycraft/_Module/GUI/SpriteSheets/MycraftHUD/MycraftHUD_1.png',
    'src/Mycraft/_Module/GUI/SpriteSheets/MycraftHUD/MycraftHUD_1.rgba',
    'tests/EntityVisuals/Fixtures/skeleton-pose-oracle.json',
    'tests/EntityVisuals/Fixtures/boat-pose-oracle.json'
)) {
    [void](Resolve-RequiredPath (Join-Path $workspace $output) 'Leaf' 'Generated asset')
}
Write-Host "BannerCraft local assets are ready in $workspace"
Write-Host "Previews: $previewRoot"
