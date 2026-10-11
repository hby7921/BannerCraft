param(
    [string]$ClientJar = (Join-Path $PSScriptRoot '../runtime/minecraft/downloads/minecraft-26.3-client.jar'),
    [string]$VisualsDir = (Join-Path $PSScriptRoot '../src/Mycraft/_Module/Visuals'),
    [string]$PreviewDir = (Join-Path $PSScriptRoot '../.build-game/model-preview'),
    [string]$SkinFile = (& (Join-Path $PSScriptRoot 'Get-LocalSetting.ps1') -Name skinFile)
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing.Common
if (-not ('Mycraft.Visuals.MinecraftGuiProjection' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot '../src/Mycraft/Visuals/VoxelGeometry.cs'), (Join-Path $PSScriptRoot '../src/Mycraft/Visuals/MinecraftGuiProjection.cs')
}
if (-not ('MycraftModelPixels' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Runtime.InteropServices -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class MycraftModelPixels {
    public static Bitmap Image(byte[] rgba, int size) {
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for(int y=0;y<size;y++) for(int x=0;x<size;x++) {
            int p=(y*size+x)*4;
            image.SetPixel(x,y,Color.FromArgb(rgba[p+3],rgba[p],rgba[p+1],rgba[p+2]));
        }
        return image;
    }
    public static byte[] Rgba(Bitmap image) {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            var row = new byte[image.Width * 4];
            var result = new byte[image.Width * image.Height * 4];
            for (int y = 0; y < image.Height; y++) {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < image.Width; x++) {
                    int source = x * 4, target = (y * image.Width + x) * 4;
                    result[target] = row[source + 2];
                    result[target + 1] = row[source + 1];
                    result[target + 2] = row[source];
                    result[target + 3] = row[source + 3];
                }
            }
            return result;
        } finally { image.UnlockBits(data); }
    }
}
'@
}
$itemDir = Join-Path $VisualsDir 'items'
[void](New-Item -ItemType Directory -Force -Path $VisualsDir, $itemDir, $PreviewDir)
$archive = [System.IO.Compression.ZipFile]::OpenRead($ClientJar)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$items = [System.Collections.Generic.List[object]]::new()
$skipped = [System.Collections.Generic.List[string]]::new()

function Read-JarImage([string]$path) {
    $entry = $archive.GetEntry($path)
    if ($null -eq $entry) { throw "Missing Minecraft image: $path" }
    $stream = $entry.Open()
    try {
        $loaded = [System.Drawing.Bitmap]::new($stream)
        try { return [System.Drawing.Bitmap]::new($loaded) } finally { $loaded.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-JarEntryHash([string]$path) {
    $entry = $archive.GetEntry($path)
    if ($null -eq $entry) { throw "Missing Minecraft entry: $path" }
    $stream = $entry.Open()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.Convert]::ToHexString($sha.ComputeHash($stream)) }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function New-Canvas([int]$width, [int]$height) {
    return [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Draw-Pixels($destination, $source, [int]$x, [int]$y, [int]$width, [int]$height, $sourceRect) {
    $graphics = [System.Drawing.Graphics]::FromImage($destination)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $graphics.DrawImage($source, [System.Drawing.Rectangle]::new($x, $y, $width, $height), $sourceRect.X, $sourceRect.Y, $sourceRect.Width, $sourceRect.Height, [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $graphics.Dispose() }
}

function Export-Item([string]$id, $image, [string]$source, [bool]$animated) {
    if ($image.Width -ne 16 -or $image.Height -ne 16) { throw "Invalid item dimensions: $id" }
    $rgba = [MycraftModelPixels]::Rgba($image)
    if ($rgba.Length -ne 1024) { throw "Invalid item byte length: $id" }
    $path = Join-Path $itemDir ($id + '.pixels')
    [System.IO.File]::WriteAllBytes($path, $rgba)
    $opaque = 0; $partial = 0
    for ($index = 3; $index -lt $rgba.Length; $index += 4) {
        if ($rgba[$index] -eq 255) { $opaque++ }
        elseif ($rgba[$index] -gt 0) { $partial++ }
    }
    $items.Add([ordered]@{ id=$id; file="items/$id.pixels"; source=$source; animated=$animated; opaquePixels=$opaque; partialAlphaPixels=$partial; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash })
}

try {
    $skinSource = 'assets/minecraft/textures/entity/player/wide/steve.png'
    if ($SkinFile -and (Test-Path -LiteralPath $SkinFile)) {
        $skinSource = (Resolve-Path -LiteralPath $SkinFile).Path
        $loadedSkin = [System.Drawing.Bitmap]::new($skinSource)
        try { $skin = [System.Drawing.Bitmap]::new($loadedSkin) } finally { $loadedSkin.Dispose() }
        $skinSourceHash = (Get-FileHash -LiteralPath $skinSource -Algorithm SHA256).Hash
    } else {
        $skin = Read-JarImage $skinSource
        $skinSourceHash = Get-JarEntryHash $skinSource
    }
    try {
        if ($skin.Width -eq 64 -and $skin.Height -eq 32) {
            # Legacy skins share right-limb UVs. Expand them into the modern independent left limbs.
            $modern = New-Canvas 64 64
            Draw-Pixels $modern $skin 0 0 64 32 ([System.Drawing.Rectangle]::new(0,0,64,32))
            foreach ($limb in @(@(0,16,16,48), @(40,16,32,48))) {
                $sx=$limb[0]; $sy=$limb[1]; $dx=$limb[2]; $dy=$limb[3]
                foreach ($face in @(@(4,0,4,4,4,0), @(8,0,4,4,8,0), @(8,4,4,12,0,4), @(4,4,4,12,4,4), @(0,4,4,12,8,4), @(12,4,4,12,12,4))) {
                    $copy = $skin.Clone([System.Drawing.Rectangle]::new($sx+$face[0],$sy+$face[1],$face[2],$face[3]), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                    try {
                        $copy.RotateFlip([System.Drawing.RotateFlipType]::RotateNoneFlipX)
                        Draw-Pixels $modern $copy ($dx+$face[4]) ($dy+$face[5]) $face[2] $face[3] ([System.Drawing.Rectangle]::new(0,0,$face[2],$face[3]))
                    } finally { $copy.Dispose() }
                }
            }
            $skin.Dispose()
            $skin = $modern
        }
        if ($skin.Width -ne 64 -or $skin.Height -ne 64) { throw 'Expected modern 64x64 wide Steve skin.' }
        $skinPng = Join-Path $VisualsDir 'steve.png'
        $skin.Save($skinPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $skin.Save((Join-Path $PreviewDir 'steve.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $rgba = [MycraftModelPixels]::Rgba($skin)
        if ($rgba.Length -ne 16384) { throw 'Invalid Steve RGBA length.' }
        [System.IO.File]::WriteAllBytes((Join-Path $VisualsDir 'steve.rgba'), $rgba)

        $preview = New-Canvas 800 576
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($preview)
            try { $graphics.Clear([System.Drawing.Color]::FromArgb(255, 62, 65, 69)) } finally { $graphics.Dispose() }
            Draw-Pixels $preview $skin 16 16 512 512 ([System.Drawing.Rectangle]::new(0,0,64,64))
            # Front-view layout uses the wide (4-pixel arm) modern skin UVs.
            $parts = @(
                @(8,8,8,8, 8,0),
                @(20,20,8,12, 8,8),
                @(44,20,4,12, 4,8),
                @(36,52,4,12, 16,8),
                @(4,20,4,12, 8,20),
                @(20,52,4,12, 12,20),
                @(40,8,8,8, 8,0),
                @(20,36,8,12, 8,8),
                @(44,36,4,12, 4,8),
                @(52,52,4,12, 16,8),
                @(4,36,4,12, 8,20),
                @(4,52,4,12, 12,20)
            )
            foreach ($part in $parts) {
                Draw-Pixels $preview $skin (544 + $part[4]*10) (64 + $part[5]*10) ($part[2]*10) ($part[3]*10) ([System.Drawing.Rectangle]::new($part[0],$part[1],$part[2],$part[3]))
            }
            $preview.Save((Join-Path $PreviewDir 'steve-layout.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $preview.Dispose() }
    } finally { $skin.Dispose() }

    foreach ($entry in ($archive.Entries | Where-Object { $_.FullName -match '^assets/minecraft/textures/item/[^/]+\.png$' } | Sort-Object FullName)) {
        $source = Read-JarImage $entry.FullName
        try {
            if ($source.Width -ne 16 -or $source.Height -lt 16 -or ($source.Height % 16) -ne 0) { $skipped.Add($entry.FullName); continue }
            $id = [System.IO.Path]::GetFileNameWithoutExtension($entry.FullName)
            $frame = $source.Clone([System.Drawing.Rectangle]::new(0,0,16,16), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try { Export-Item $id $frame $entry.FullName ($source.Height -gt 16) } finally { $frame.Dispose() }
        } finally { $source.Dispose() }
    }
    if (-not ($items | Where-Object { $_.id -eq 'shield' })) {
        $shieldSource = 'assets/minecraft/textures/entity/shield/shield_base_nopattern.png'
        $source = Read-JarImage $shieldSource
        try {
            if ($source.Width -ne 64 -or $source.Height -ne 64) { throw 'Expected Minecraft 64x64 shield atlas' }
            $shieldPixels = [MycraftModelPixels]::Rgba($source)
            [System.IO.File]::WriteAllBytes((Join-Path $VisualsDir 'shield.rgba'), $shieldPixels)
            $source.Save((Join-Path $VisualsDir 'shield.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            $entry = $archive.GetEntry('assets/minecraft/models/item/shield.json')
            $reader = [System.IO.StreamReader]::new($entry.Open())
            try { $shieldGui = ($reader.ReadToEnd() | ConvertFrom-Json).display.gui } finally { $reader.Dispose() }
            $shield = [MycraftModelPixels]::Image([Mycraft.Visuals.MinecraftGuiProjection]::Shield($shieldPixels,[float[]]$shieldGui.rotation,[float[]]$shieldGui.translation,[float[]]$shieldGui.scale,16),16)
            try {
                Export-Item 'shield' $shield ($shieldSource + ' [complete ShieldModel GUI projection; held model uses shield.rgba]') $false
                $shield.Save((Join-Path $PreviewDir 'shield.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $shield.Dispose() }
            $views = New-Canvas 768 256
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($views)
                try { $graphics.Clear([System.Drawing.Color]::FromArgb(255,62,65,69)) } finally { $graphics.Dispose() }
                $rotations = @([float[]]@(0,0,0), [float[]]@(0,150,0), [float[]]$shieldGui.rotation)
                for ($v=0;$v -lt 3;$v++) {
                    $view = [MycraftModelPixels]::Image([Mycraft.Visuals.MinecraftGuiProjection]::Shield($shieldPixels,$rotations[$v],[float[]]@(0,0,0),[float[]]@(.65,.65,.65),128),128)
                    try { Draw-Pixels $views $view ($v*256) 0 256 256 ([System.Drawing.Rectangle]::new(0,0,128,128)) } finally { $view.Dispose() }
                }
                $views.Save((Join-Path $PreviewDir 'shield-model-views.png'), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $views.Dispose() }
        } finally { $source.Dispose() }
    }
    foreach ($required in @('diamond_sword','bow','arrow','shield')) {
        if (-not (Test-Path -LiteralPath (Join-Path $itemDir ($required + '.pixels')))) { throw "Required item missing: $required" }
    }
    $itemPreview = New-Canvas 640 96
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($itemPreview)
        try { $graphics.Clear([System.Drawing.Color]::FromArgb(255,62,65,69)) } finally { $graphics.Dispose() }
        $previewIds = @('diamond_sword','iron_sword','bow','arrow','shield','diamond_pickaxe','stone_axe','bread')
        for ($itemIndex = 0; $itemIndex -lt $previewIds.Count; $itemIndex++) {
            $pixels = [System.IO.File]::ReadAllBytes((Join-Path $itemDir ($previewIds[$itemIndex] + '.pixels')))
            $image = New-Canvas 16 16
            try {
                for ($py=0; $py -lt 16; $py++) {
                    for ($px=0; $px -lt 16; $px++) {
                        $i = ($py*16+$px)*4
                        $image.SetPixel($px,$py,[System.Drawing.Color]::FromArgb($pixels[$i+3],$pixels[$i],$pixels[$i+1],$pixels[$i+2]))
                    }
                }
                Draw-Pixels $itemPreview $image (8+$itemIndex*80) 16 64 64 ([System.Drawing.Rectangle]::new(0,0,16,16))
            } finally { $image.Dispose() }
        }
        $itemPreview.Save((Join-Path $PreviewDir 'held-items.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $itemPreview.Dispose() }

    $manifest = [ordered]@{
        source='Minecraft Java 26.3 local client JAR'; sourceJarSha256=(Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash
        skinSource=$skinSource; skinSourceSha256=$skinSourceHash; skinPngSha256=(Get-FileHash -LiteralPath $skinPng -Algorithm SHA256).Hash
        skinRgbaSha256=(Get-FileHash -LiteralPath (Join-Path $VisualsDir 'steve.rgba') -Algorithm SHA256).Hash
        skinWidth=64; skinHeight=64; skinModel='wide'; skinBytes=16384
        shieldSource=$shieldSource; shieldSourceSha256=(Get-JarEntryHash $shieldSource)
        shieldRgbaSha256=(Get-FileHash -LiteralPath (Join-Path $VisualsDir 'shield.rgba') -Algorithm SHA256).Hash
        shieldModel='Minecraft 26.3 ShieldModel.createLayer: plate(-6,-11,-2;12,22,1) UV(0,0), handle(-1,-3,-1;2,6,6) UV(26,0); atlas64x64'
        rawFormat='RGBA8, row-major, top-down, straight alpha; no header'
        itemWidth=16; itemHeight=16; itemBytes=1024; itemFrame='first 16x16 frame'; itemCount=$items.Count
        items=@($items); skippedNon16Textures=@($skipped)
        limitations=@('Local Demo only: generated Mojang assets must not be packaged for redistribution.', 'Block textures are exported separately by build-block-assets.ps1.', 'Animated textures use their first frame; tint-dependent textures retain untinted colors.', 'Shield geometry and atlas are complete; banner patterns and enchantment glint are not exported.', 'Composite/layered item models, bow draw stages and armor models are not assembled by this exporter.')
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $VisualsDir 'manifest.json'), $json, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $PreviewDir 'manifest.json'), $json, $utf8)
    Write-Output "Steve: $VisualsDir\steve.rgba (16384 bytes, modern wide 64x64 UV layout)"
    Write-Output "Items: $($items.Count) files, 1024 bytes per 16x16 RGBA image"
    Write-Output "Previews: $PreviewDir\steve-layout.png and $PreviewDir\held-items.png"
} finally { $archive.Dispose() }
