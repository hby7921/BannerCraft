param(
    [string]$ClientJar = (Join-Path $PSScriptRoot '../runtime/minecraft/downloads/minecraft-26.3-client.jar'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '../src/Mycraft/_Module/Visuals/particles'),
    [string]$PreviewDir = (Join-Path $PSScriptRoot '../.build-game/tnt-preview')
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing.Common
[void](New-Item -ItemType Directory -Force -Path $OutputDir,$PreviewDir)
$archive=[IO.Compression.ZipFile]::OpenRead($ClientJar)
$preview=[Drawing.Bitmap]::new(512,512,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($preview)
$graphics.Clear([Drawing.Color]::FromArgb(255,52,58,65))
$graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::Half
$frames=[Collections.Generic.List[object]]::new()
$smokeFrames=[Collections.Generic.List[object]]::new()
try {
    $reader=[IO.StreamReader]::new($archive.GetEntry('assets/minecraft/particles/explosion.json').Open())
    try {$definition=$reader.ReadToEnd()|ConvertFrom-Json} finally {$reader.Dispose()}
    if($definition.textures.Count -ne 16){throw 'Minecraft explosion sprite sequence changed.'}
    for($frame=0;$frame -lt 16;$frame++) {
        $id=$definition.textures[$frame] -replace '^minecraft:',''
        if($id -ne "explosion_$frame"){throw 'Minecraft explosion frame order changed.'}
        $path="assets/minecraft/textures/particle/$id.png"
        $stream=$archive.GetEntry($path).Open()
        try {
            $bitmap=[Drawing.Bitmap]::new($stream)
            try {
                if($bitmap.Width -ne 32 -or $bitmap.Height -ne 32){throw "Unexpected explosion frame size: $path"}
                $pixels=[byte[]]::new(4096)
                for($y=0;$y -lt 32;$y++){for($x=0;$x -lt 32;$x++){
                    $color=$bitmap.GetPixel($x,$y);$p=($y*32+$x)*4
                    $pixels[$p]=$color.R;$pixels[$p+1]=$color.G;$pixels[$p+2]=$color.B;$pixels[$p+3]=$color.A
                }}
                $output=Join-Path $OutputDir "$id.rgba"
                [IO.File]::WriteAllBytes($output,$pixels)
                $bitmap.Save((Join-Path $PreviewDir "$id.png"),[Drawing.Imaging.ImageFormat]::Png)
                $graphics.DrawImage($bitmap,[Drawing.Rectangle]::new(($frame%4)*128,[int][Math]::Floor($frame/4)*128,128,128),0,0,32,32,[Drawing.GraphicsUnit]::Pixel)
                $frames.Add([ordered]@{source=$path;output="$id.rgba";sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash})
            } finally {$bitmap.Dispose()}
        } finally {$stream.Dispose()}
    }
    $reader=[IO.StreamReader]::new($archive.GetEntry('assets/minecraft/particles/smoke.json').Open())
    try {$smokeDefinition=$reader.ReadToEnd()|ConvertFrom-Json} finally {$reader.Dispose()}
    if($smokeDefinition.textures.Count -ne 8){throw 'Minecraft smoke sprite sequence changed.'}
    for($frame=0;$frame -lt 8;$frame++) {
        $id=$smokeDefinition.textures[$frame] -replace '^minecraft:',''
        if($id -ne ('generic_'+(7-$frame))){throw 'Minecraft smoke frame order changed.'}
        $path="assets/minecraft/textures/particle/$id.png"
        $stream=$archive.GetEntry($path).Open()
        try {
            $bitmap=[Drawing.Bitmap]::new($stream)
            try {
                if($bitmap.Width -ne 8 -or $bitmap.Height -ne 8){throw "Unexpected smoke frame size: $path"}
                $pixels=[byte[]]::new(256)
                for($y=0;$y -lt 8;$y++){for($x=0;$x -lt 8;$x++){
                    $color=$bitmap.GetPixel($x,$y);$p=($y*8+$x)*4
                    $pixels[$p]=$color.R;$pixels[$p+1]=$color.G;$pixels[$p+2]=$color.B;$pixels[$p+3]=$color.A
                }}
                $output=Join-Path $OutputDir "smoke_$frame.rgba"
                [IO.File]::WriteAllBytes($output,$pixels)
                $smokeFrames.Add([ordered]@{source=$path;output="smoke_$frame.rgba";sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash})
            } finally {$bitmap.Dispose()}
        } finally {$stream.Dispose()}
    }
    $preview.Save((Join-Path $PreviewDir 'explosion-frames.png'),[Drawing.Imaging.ImageFormat]::Png)
    $manifest=[ordered]@{source='Minecraft Java 26.3 local client JAR';sourceJarSha256=(Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash;sequence='assets/minecraft/particles/explosion.json';width=32;height=32;frames=@($frames);rawFormat='RGBA8 top-down';timing='HugeExplosionSeedParticle emits 6 particles per tick for 8 ticks; HugeExplosionParticle lives 6-9 ticks';audio='Minecraft client owns native TNT prime/explosion audio; Bannerlord does not duplicate it'}
    $manifest['smokeSequence']='assets/minecraft/particles/smoke.json'
    $manifest['smokeWidth']=8;$manifest['smokeHeight']=8;$manifest['smokeFrames']=@($smokeFrames)
    [IO.File]::WriteAllText((Join-Path $OutputDir 'explosion.manifest.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    Write-Output 'Exported 16 explosion and 8 smoke original Minecraft RGBA frames. Audio remains owned by Minecraft.'
    Write-Output (Join-Path $PreviewDir 'explosion-frames.png')
} finally {$graphics.Dispose();$preview.Dispose();$archive.Dispose()}
