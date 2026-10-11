param(
    [string]$ClientJar = (Join-Path $PSScriptRoot '../runtime/minecraft/downloads/minecraft-26.3-client.jar'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '../src/Mycraft/_Module/Visuals/blocks')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing.Common
if (-not ('Mycraft.Visuals.VoxelGeometry' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../src/Mycraft/Visuals/VoxelGeometry.cs')
}
[void](New-Item -ItemType Directory -Force -Path $OutputDir)
$archive = [System.IO.Compression.ZipFile]::OpenRead($ClientJar)
function Read-BlockPixels([string]$path) {
    $entry = $archive.GetEntry($path)
    if ($null -eq $entry) { throw "Missing block texture $path" }
    $stream = $entry.Open()
    try {
        $bitmap = [System.Drawing.Bitmap]::new($stream)
        try {
            if ($bitmap.Width -ne 16 -or $bitmap.Height -lt 16) { throw "Invalid texture size for $path" }
            $pixels = [byte[]]::new(1024)
            for ($y=0; $y -lt 16; $y++) {
                for ($x=0; $x -lt 16; $x++) {
                    $p=$bitmap.GetPixel($x,$y); $i=($y*16+$x)*4
                    $pixels[$i]=$p.R; $pixels[$i+1]=$p.G; $pixels[$i+2]=$p.B; $pixels[$i+3]=$p.A
                }
            }
            return ,$pixels
        } finally { $bitmap.Dispose() }
    } finally { $stream.Dispose() }
}
try {
    foreach ($name in @('stone','dirt','cobblestone','oak_planks','bricks')) {
        [IO.File]::WriteAllBytes((Join-Path $OutputDir "$name.rgba"),(Read-BlockPixels "assets/minecraft/textures/block/$name.png"))
    }
    $reader=[IO.StreamReader]::new($archive.GetEntry('assets/minecraft/models/block/tnt.json').Open())
    try { $tnt=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($tnt.parent -ne 'minecraft:block/cube_bottom_top') { throw 'TNT model parent changed; review its geometry before export.' }
    $faces=@{}
    foreach ($face in @('side','top','bottom')) {
        $path='assets/minecraft/textures/' + ($tnt.textures.$face -replace '^minecraft:', '') + '.png'
        $faces[$face]=Read-BlockPixels $path
        [IO.File]::WriteAllBytes((Join-Path $OutputDir "tnt_$face.rgba"),$faces[$face])
    }
    $atlas=[Mycraft.Visuals.VoxelGeometry]::CubeBottomTopAtlas($faces.side,$faces.top,$faces.bottom)
    [IO.File]::WriteAllBytes((Join-Path $OutputDir 'tnt.atlas.rgba'),$atlas)
    $manifest=[ordered]@{ source='Minecraft Java 26.3 local client JAR'; sourceJarSha256=(Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash; model='assets/minecraft/models/block/tnt.json'; textures=$tnt.textures; atlas='tnt.atlas.rgba'; atlasWidth=64; atlasHeight=64; side=@(1,1,16,16); top=@(19,1,16,16); bottom=@(37,1,16,16); edgePadding=1; rawFormat='RGBA8 top-down'; atlasSha256=(Get-FileHash -LiteralPath (Join-Path $OutputDir 'tnt.atlas.rgba') -Algorithm SHA256).Hash }
    [IO.File]::WriteAllText((Join-Path $OutputDir 'tnt.manifest.json'),($manifest|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
} finally { $archive.Dispose() }
Write-Output 'Exported five cube textures and TNT side/top/bottom with a padded 64x64 held-model atlas.'
