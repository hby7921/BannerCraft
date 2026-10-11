param(
    [string]$ClientJar = (Join-Path $PSScriptRoot '../runtime/minecraft/downloads/minecraft-26.3-client.jar'),
    [string]$ModuleGui = (Join-Path $PSScriptRoot '../src/Mycraft/_Module/GUI'),
    [string]$PreviewDir = (Join-Path $PSScriptRoot '../.build-game/hud-preview')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing.Common
if (-not ('Mycraft.Visuals.MinecraftGuiProjection' -as [type])) {
    Add-Type -CompilerOptions '/nullable:enable' -Path (Join-Path $PSScriptRoot '../src/Mycraft/Visuals/VoxelGeometry.cs'), (Join-Path $PSScriptRoot '../src/Mycraft/Visuals/MinecraftGuiProjection.cs')
}
if (-not ('Mycraft.Building.BlockSurfaceGeometry' -as [type])) {
    Add-Type -CompilerOptions '/nullable:enable' -Path (Join-Path $PSScriptRoot '../src/Mycraft/Building/BlockGeometry.cs'), (Join-Path $PSScriptRoot '../src/Mycraft/Building/BlockModelSnapshot.cs'), (Join-Path $PSScriptRoot '../src/Mycraft/Building/BlockSurfaceGeometry.cs')
}
if (-not ('MycraftHudPixels' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Runtime.InteropServices -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class MycraftHudPixels {
    public static Bitmap Image(byte[] rgba, int size) {
        var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for(int y=0;y<size;y++) for(int x=0;x<size;x++) {
            int p=(y*size+x)*4;
            image.SetPixel(x,y,Color.FromArgb(rgba[p+3],rgba[p],rgba[p+1],rgba[p+2]));
        }
        return image;
    }
    public static byte[] Rgba(Bitmap bitmap) {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            var row = new byte[bitmap.Width * 4];
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++) {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++) {
                    int source = x * 4, target = (y * bitmap.Width + x) * 4;
                    rgba[target] = row[source + 2];
                    rgba[target + 1] = row[source + 1];
                    rgba[target + 2] = row[source];
                    rgba[target + 3] = row[source + 3];
                }
            }
            return rgba;
        } finally { bitmap.UnlockBits(data); }
    }
}
'@
}
$sheetDir = Join-Path $ModuleGui 'SpriteSheets\MycraftHUD'
[void](New-Item -ItemType Directory -Force -Path $sheetDir, $PreviewDir)
$archive = [System.IO.Compression.ZipFile]::OpenRead($ClientJar)
$sprites = [System.Collections.Generic.List[object]]::new()
$images = [System.Collections.Generic.Dictionary[string,System.Drawing.Bitmap]]::new()
$componentEvidence = [System.Collections.Generic.List[object]]::new()
$modelDefinitions = @{}

function Read-JarImage([string]$path) {
    $entry = $archive.GetEntry($path)
    if ($null -eq $entry) { throw "Missing MC texture: $path" }
    $stream = $entry.Open()
    try {
        $image = [System.Drawing.Bitmap]::new($stream)
        try { return [System.Drawing.Bitmap]::new($image) } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

function New-Canvas([int]$width, [int]$height) {
    return [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Read-JarJson([string]$path) {
    $entry = $archive.GetEntry($path)
    if ($null -eq $entry) { throw "Missing MC model: $path" }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}

function Get-JarSourceHash([string]$path) {
    $entry=$archive.GetEntry($path);if($null -eq $entry){throw "Missing source for hash: $path"}
    $stream=$entry.Open();$hash=[System.Security.Cryptography.SHA256]::Create()
    try{return [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant()}finally{$hash.Dispose();$stream.Dispose()}
}

function Draw-Pixels($canvas, $source, [int]$x, [int]$y, [int]$width, [int]$height, $sourceRect) {
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $dest = [System.Drawing.Rectangle]::new($x, $y, $width, $height)
        $graphics.DrawImage($source, $dest, $sourceRect.X, $sourceRect.Y, $sourceRect.Width, $sourceRect.Height, [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $graphics.Dispose() }
}

function Add-Sprite([string]$name, $image) {
    if ($images.ContainsKey($name)) { throw "Duplicate sprite: $name" }
    $images.Add($name, $image)
    $sprites.Add([pscustomobject]@{ Name=$name; Width=$image.Width; Height=$image.Height; Sheet=0; X=0; Y=0 })
}

function Add-JarSprite([string]$name, [string]$path, [int]$width, [int]$height, [bool]$firstFrame=$false) {
    $source = Read-JarImage $path
    try {
        $sourceHeight = $source.Height
        if ($firstFrame) { $sourceHeight = [Math]::Min($source.Width, $source.Height) }
        $image = New-Canvas $width $height
        Draw-Pixels $image $source 0 0 $width $height ([System.Drawing.Rectangle]::new(0, 0, $source.Width, $sourceHeight))
        Add-Sprite $name $image
    } finally { $source.Dispose() }
}

function Resolve-ItemModel([string]$location, [int]$depth=0) {
    if ($depth -gt 20) { throw "Recursive item model: $location" }
    if ($location -notmatch ':') { $location='minecraft:'+$location }
    if ($modelDefinitions.ContainsKey($location)) { return $modelDefinitions[$location] }
    $parts=$location.Split(':',2)
    if ($parts[1] -eq 'builtin/generated') {
        return [pscustomobject]@{ Textures=@{}; Elements=@(); Gui=[pscustomobject]@{rotation=@(0,0,0);translation=@(0,0,0);scale=@(1,1,1)}; Generated=$true; Flat=$true }
    }
    $definition=Read-JarJson "assets/$($parts[0])/models/$($parts[1]).json"
    $parent=if($definition.parent){Resolve-ItemModel $definition.parent ($depth+1)}else{$null}
    $textures=@{}
    if($parent){foreach($key in $parent.Textures.Keys){$textures[$key]=$parent.Textures[$key]}}
    if($definition.textures){foreach($property in $definition.textures.PSObject.Properties){$textures[$property.Name]=[string]$property.Value}}
    $elements=if($null -ne $definition.elements){@($definition.elements)}elseif($parent){@($parent.Elements)}else{@()}
    $gui=if($definition.display.gui){$definition.display.gui}elseif($parent){$parent.Gui}else{[pscustomobject]@{rotation=@(0,0,0);translation=@(0,0,0);scale=@(1,1,1)}}
    $flat=if($definition.gui_light){$definition.gui_light -eq 'front'}elseif($parent){$parent.Flat}else{$false}
    $model=[pscustomobject]@{Textures=$textures;Elements=$elements;Gui=$gui;Generated=($parent -and $parent.Generated);Flat=$flat}
    $modelDefinitions[$location]=$model
    return $model
}

function Resolve-ModelTexture($model,[string]$name) {
    for($depth=0;$name.StartsWith('#');$depth++) {
        if($depth -gt 20 -or -not $model.Textures.ContainsKey($name.Substring(1))){throw "Invalid item texture alias: $name"}
        $name=[string]$model.Textures[$name.Substring(1)]
    }
    if($name -notmatch ':'){$name='minecraft:'+$name}
    $parts=$name.Split(':',2)
    return "assets/$($parts[0])/textures/$($parts[1]).png"
}

function Transform-Element([float[]]$point,$rotation,[bool]$normal=$false) {
    if(-not $rotation){return ,$point}
    $origin=if($normal){[float[]]@(0,0,0)}else{[float[]]$rotation.origin}
    $x=$point[0]-$origin[0];$y=$point[1]-$origin[1];$z=$point[2]-$origin[2]
    $r=[double]$rotation.angle*[Math]::PI/180;$c=[Math]::Cos($r);$s=[Math]::Sin($r)
    $factor=if($rotation.rescale -and -not $normal){1/[Math]::Cos([Math]::Abs($r))}else{1}
    switch($rotation.axis) {
        'x' {$next=($y*$c-$z*$s)*$factor;$z=($y*$s+$z*$c)*$factor;$y=$next}
        'y' {$next=($x*$c+$z*$s)*$factor;$z=(-$x*$s+$z*$c)*$factor;$x=$next}
        'z' {$next=($x*$c-$y*$s)*$factor;$y=($x*$s+$y*$c)*$factor;$x=$next}
        default {throw "Unknown model rotation axis: $($rotation.axis)"}
    }
    return ,[float[]]@(($x+$origin[0]),($y+$origin[1]),($z+$origin[2]))
}

function Add-ComponentIcon([string]$id) {
    if($images.ContainsKey("mycraft_item_$id")){return}
    $dispatch=Read-JarJson "assets/minecraft/items/$id.json"
    if($dispatch.model.type -ne 'minecraft:model'){throw "Review dynamic GUI item dispatch before export: $id"}
    $model=Resolve-ItemModel $dispatch.model.model
    $paths=[System.Collections.Generic.List[string]]::new()
    if($model.Generated) {
        $image=New-Canvas 48 48
        foreach($layer in @($model.Textures.Keys | Where-Object {$_ -match '^layer[0-9]+$'} | Sort-Object)) {
            $path=Resolve-ModelTexture $model ('#'+$layer);$paths.Add($path);$source=Read-JarImage $path
            try{Draw-Pixels $image $source 0 0 48 48 ([System.Drawing.Rectangle]::new(0,0,$source.Width,[Math]::Min($source.Width,$source.Height)))}finally{$source.Dispose()}
        }
        Add-Sprite "mycraft_item_$id" $image
    } else {
        $snapshot=[Mycraft.Building.BlockModelSnapshot]::new()
        $textures=[System.Collections.Generic.List[Mycraft.Building.BlockModelSnapshot+Image]]::new()
        $quads=[System.Collections.Generic.List[Mycraft.Building.BlockModelSnapshot+Quad]]::new()
        foreach($element in $model.Elements) {
            $a=[float[]]$element.from;$b=[float[]]$element.to
            foreach($face in $element.faces.PSObject.Properties) {
                $path=Resolve-ModelTexture $model $face.Value.texture;$material=$paths.IndexOf($path)
                if($material -lt 0) {
                    $material=$paths.Count;$paths.Add($path);$source=Read-JarImage $path
                    try {
                        $texture=[Mycraft.Building.BlockModelSnapshot+Image]::new();$texture.Width=$source.Width;$texture.Height=[Math]::Min($source.Width,$source.Height)
                        $sourcePixels=[MycraftHudPixels]::Rgba($source);$texture.Pixels=[byte[]]::new($texture.Width*$texture.Height*4)
                        [Array]::Copy($sourcePixels,$texture.Pixels,$texture.Pixels.Length);$textures.Add($texture)
                    }finally{$source.Dispose()}
                }
                # FaceInfo's authored corner ordering, not a generic cube icon.
                switch($face.Name) {
                    'down' {$points=@(@($a[0],$a[1],$b[2]),@($a[0],$a[1],$a[2]),@($b[0],$a[1],$a[2]),@($b[0],$a[1],$b[2]));$normal=@(0,-1,0);$uv=@($a[0],(16-$b[2]),$b[0],(16-$a[2]))}
                    'up' {$points=@(@($a[0],$b[1],$a[2]),@($a[0],$b[1],$b[2]),@($b[0],$b[1],$b[2]),@($b[0],$b[1],$a[2]));$normal=@(0,1,0);$uv=@($a[0],$a[2],$b[0],$b[2])}
                    'north' {$points=@(@($b[0],$b[1],$a[2]),@($b[0],$a[1],$a[2]),@($a[0],$a[1],$a[2]),@($a[0],$b[1],$a[2]));$normal=@(0,0,-1);$uv=@((16-$b[0]),(16-$b[1]),(16-$a[0]),(16-$a[1]))}
                    'south' {$points=@(@($a[0],$b[1],$b[2]),@($a[0],$a[1],$b[2]),@($b[0],$a[1],$b[2]),@($b[0],$b[1],$b[2]));$normal=@(0,0,1);$uv=@($a[0],(16-$b[1]),$b[0],(16-$a[1]))}
                    'west' {$points=@(@($a[0],$b[1],$a[2]),@($a[0],$a[1],$a[2]),@($a[0],$a[1],$b[2]),@($a[0],$b[1],$b[2]));$normal=@(-1,0,0);$uv=@($a[2],(16-$b[1]),$b[2],(16-$a[1]))}
                    'east' {$points=@(@($b[0],$b[1],$b[2]),@($b[0],$a[1],$b[2]),@($b[0],$a[1],$a[2]),@($b[0],$b[1],$a[2]));$normal=@(1,0,0);$uv=@((16-$b[2]),(16-$b[1]),(16-$a[2]),(16-$a[1]))}
                    default {throw "Unknown model face: $($face.Name)"}
                }
                if($face.Value.uv){$uv=[float[]]$face.Value.uv}
                $quad=[Mycraft.Building.BlockModelSnapshot+Quad]::new();$quad.Material=$material;$quad.Tint=[uint32]::MaxValue;$quad.Vertices=[float[]]::new(20)
                if($null -ne $face.Value.tintindex -and $face.Value.tintindex -ge 0) {
                    $tint=$dispatch.model.tints[$face.Value.tintindex]
                    if($tint.type -ne 'minecraft:constant'){throw "Review native GUI tint source before export: $id"}
                    $quad.Tint=[uint32]([uint32]0xff000000 -bor [uint32]$tint.value)
                }
                $normal=Transform-Element ([float[]]$normal) $element.rotation $true
                $quad.NormalX=$normal[0];$quad.NormalY=$normal[1];$quad.NormalZ=$normal[2]
                $turn=if($face.Value.rotation){[int]$face.Value.rotation/90}else{0}
                for($v=0;$v -lt 4;$v++) {
                    $point=Transform-Element ([float[]]$points[$v]) $element.rotation
                    for($axis=0;$axis -lt 3;$axis++){$quad.Vertices[$v*5+$axis]=$point[$axis]/16}
                    $index=($v+$turn)%4
                    $quad.Vertices[$v*5+3]=$(if($index -le 1){$uv[0]}else{$uv[2]})/16
                    $quad.Vertices[$v*5+4]=$(if($index -eq 0 -or $index -eq 3){$uv[1]}else{$uv[3]})/16
                }
                $quads.Add($quad)
            }
        }
        $snapshot.Textures=$textures.ToArray();$snapshot.Quads=$quads.ToArray()
        $rotation=if($model.Gui.rotation){[float[]]$model.Gui.rotation}else{[float[]]@(0,0,0)}
        $translation=if($model.Gui.translation){[float[]]$model.Gui.translation}else{[float[]]@(0,0,0)}
        $scale=if($model.Gui.scale){[float[]]$model.Gui.scale}else{[float[]]@(1,1,1)}
        $rgba=[Mycraft.Building.BlockSurfaceGeometry]::ProjectGui($snapshot,$rotation,$translation,$scale,48,$model.Flat)
        Add-Sprite "mycraft_item_$id" ([MycraftHudPixels]::Image($rgba,48))
    }
    $sourceHashes=@{}
    foreach($path in $paths){$sourceHashes[$path]=Get-JarSourceHash $path}
    $componentEvidence.Add([ordered]@{id=$id;dispatchModel=$dispatch.model.model;generated=[bool]$model.Generated;gui=$model.Gui;textures=@($paths);textureSourceSha256=$sourceHashes})
}

function Add-WitherSkullIcon {
    $dispatch=Read-JarJson 'assets/minecraft/items/wither_skeleton_skull.json'
    if($dispatch.model.type -ne 'minecraft:special' -or $dispatch.model.model.type -ne 'minecraft:head' -or $dispatch.model.model.kind -ne 'wither_skeleton') {
        throw 'Wither skull dispatch changed; export its original special renderer again.'
    }
    # This fixture is generated by SkullModelExporter.java from the real client's
    # SkullModel.createMobHeadLayer(), polygon UVs and item special transformation.
    $fixture=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'fixtures/wither-skull-gui-model-26.3.json') | ConvertFrom-Json
    if($fixture.clientJarSha256 -ne (Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Wither skull geometry fixture belongs to a different client JAR; rerun SkullModelExporter.java.'
    }
    $gui=(Resolve-ItemModel $dispatch.model.base).Gui
    $path='assets/minecraft/textures/entity/skeleton/wither_skeleton.png'
    $source=Read-JarImage $path
    try {
        $texture=[Mycraft.Building.BlockModelSnapshot+Image]::new()
        $texture.Width=$source.Width;$texture.Height=$source.Height;$texture.Pixels=[MycraftHudPixels]::Rgba($source)
        $quads=[System.Collections.Generic.List[Mycraft.Building.BlockModelSnapshot+Quad]]::new()
        foreach($face in $fixture.faces) {
            $quad=[Mycraft.Building.BlockModelSnapshot+Quad]::new()
            $quad.Material=0;$quad.Tint=[uint32]::MaxValue;$quad.Vertices=[float[]]$face.vertices
            $quad.NormalX=$face.normal[0];$quad.NormalY=$face.normal[1];$quad.NormalZ=$face.normal[2];$quads.Add($quad)
        }
        $snapshot=[Mycraft.Building.BlockModelSnapshot]::new();$snapshot.Textures=@($texture);$snapshot.Quads=$quads.ToArray()
        $rgba=[Mycraft.Building.BlockSurfaceGeometry]::ProjectGui($snapshot,[float[]]$gui.rotation,[float[]]$gui.translation,[float[]]$gui.scale,48)
        Add-Sprite 'mycraft_item_wither_skeleton_skull' ([MycraftHudPixels]::Image($rgba,48))
        $images['mycraft_item_wither_skeleton_skull'].Save((Join-Path $PreviewDir 'wither-skull-gui.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    }finally{$source.Dispose()}
    $componentEvidence.Add([ordered]@{id='wither_skeleton_skull';dispatchModel=$dispatch.model.base;specialRenderer='SkullModel.createMobHeadLayer';gui=$gui;textures=@($path);textureSourceSha256=@{$path=(Get-JarSourceHash $path)}})
}

try {
    Add-JarSprite 'mycraft_hotbar' 'assets/minecraft/textures/gui/sprites/hud/hotbar.png' 546 66
    Add-JarSprite 'mycraft_selector' 'assets/minecraft/textures/gui/sprites/hud/hotbar_selection.png' 72 72
    foreach($state in @('background','progress')) {
        Add-JarSprite "mycraft_boss_purple_$state" "assets/minecraft/textures/gui/sprites/boss_bar/purple_$state.png" 546 15
    }
    foreach ($kind in @('heart','food','armor')) {
        foreach ($state in @('empty','half','full')) {
            $emptyPath = if ($kind -eq 'heart') { 'assets/minecraft/textures/gui/sprites/hud/heart/container.png' } else { "assets/minecraft/textures/gui/sprites/hud/${kind}_empty.png" }
            $empty = Read-JarImage $emptyPath
            $image = New-Canvas 27 27
            try { Draw-Pixels $image $empty 0 0 27 27 ([System.Drawing.Rectangle]::new(0,0,$empty.Width,$empty.Height)) } finally { $empty.Dispose() }
            if ($state -ne 'empty') {
                $path = if ($kind -eq 'heart') { "assets/minecraft/textures/gui/sprites/hud/heart/$state.png" } else { "assets/minecraft/textures/gui/sprites/hud/${kind}_${state}.png" }
                $fill = Read-JarImage $path
                try { Draw-Pixels $image $fill 0 0 27 27 ([System.Drawing.Rectangle]::new(0,0,$fill.Width,$fill.Height)) } finally { $fill.Dispose() }
            }
            Add-Sprite "mycraft_${kind}_${state}" $image
        }
    }
    $skipped = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in ($archive.Entries | Where-Object { $_.FullName -match '^assets/minecraft/textures/item/[^/]+\.png$' } | Sort-Object FullName)) {
        $source = Read-JarImage $entry.FullName
        try {
            if ($source.Width -ne 16 -or $source.Height -lt 16) { $skipped.Add($entry.FullName); continue }
            $hasPixels=$false
            for ($py=0; $py -lt 16 -and -not $hasPixels; $py++) {
                for ($px=0; $px -lt 16; $px++) {
                    if ($source.GetPixel($px,$py).A -gt 0) { $hasPixels=$true; break }
                }
            }
            if (-not $hasPixels) { $skipped.Add($entry.FullName); continue }
            $image = New-Canvas 48 48
            Draw-Pixels $image $source 0 0 48 48 ([System.Drawing.Rectangle]::new(0,0,16,16))
            $id = [System.IO.Path]::GetFileNameWithoutExtension($entry.FullName)
            Add-Sprite "mycraft_item_$id" $image
        } finally { $source.Dispose() }
    }
    # These cube_all block items inherit the installed client's GUI transform.
    # Project their full textured cuboid, never their flat block texture.
    $blockGui = (Read-JarJson 'assets/minecraft/models/block/block.json').display.gui
    foreach ($block in @('stone','dirt','cobblestone','oak_planks','bricks')) {
        if (-not $images.ContainsKey("mycraft_item_$block")) {
            $model = Read-JarJson "assets/minecraft/models/block/$block.json"
            if ($model.parent -notmatch '(^|:)block/cube_all$') { throw "Unsupported block model parent: $block" }
            $texturePath = 'assets/minecraft/textures/' + ($model.textures.all -replace '^minecraft:', '') + '.png'
            $source = Read-JarImage $texturePath
            try {
                $rgba = [Mycraft.Visuals.MinecraftGuiProjection]::Block([MycraftHudPixels]::Rgba($source),[float[]]$blockGui.rotation,[float[]]$blockGui.translation,[float[]]$blockGui.scale,48)
                Add-Sprite "mycraft_item_$block" ([MycraftHudPixels]::Image($rgba,48))
            } finally { $source.Dispose() }
        }
    }
    $tntModel = Read-JarJson 'assets/minecraft/models/block/tnt.json'
    if ($tntModel.parent -ne 'minecraft:block/cube_bottom_top') { throw 'TNT model parent changed; review its geometry before export.' }
    $tntFaces=@{}
    foreach ($face in @('side','top','bottom')) {
        $path='assets/minecraft/textures/' + ($tntModel.textures.$face -replace '^minecraft:', '') + '.png'
        $source=Read-JarImage $path
        try { $tntFaces[$face]=[MycraftHudPixels]::Rgba($source) } finally { $source.Dispose() }
    }
    $tntAtlas=[Mycraft.Visuals.VoxelGeometry]::CubeBottomTopAtlas($tntFaces.side,$tntFaces.top,$tntFaces.bottom)
    $rgba=[Mycraft.Visuals.MinecraftGuiProjection]::BlockBottomTop($tntAtlas,[float[]]$blockGui.rotation,[float[]]$blockGui.translation,[float[]]$blockGui.scale,48)
    Add-Sprite 'mycraft_item_tnt' ([MycraftHudPixels]::Image($rgba,48))
    $images['mycraft_item_tnt'].Save((Join-Path $PreviewDir 'tnt-gui.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    $tntViews=New-Canvas 512 256
    try {
        $graphics=[System.Drawing.Graphics]::FromImage($tntViews)
        try { $graphics.Clear([System.Drawing.Color]::FromArgb(255,62,65,69)) } finally { $graphics.Dispose() }
        $rotations=@([float[]]$blockGui.rotation,[float[]]@(-30,225,0))
        for ($v=0;$v -lt 2;$v++) {
            $pixels=[Mycraft.Visuals.MinecraftGuiProjection]::BlockBottomTop($tntAtlas,$rotations[$v],[float[]]$blockGui.translation,[float[]]$blockGui.scale,128)
            $image=[MycraftHudPixels]::Image($pixels,128)
            try { Draw-Pixels $tntViews $image ($v*256) 0 256 256 ([System.Drawing.Rectangle]::new(0,0,128,128)) } finally { $image.Dispose() }
        }
        $tntViews.Save((Join-Path $PreviewDir 'tnt-model-views.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $tntViews.Dispose() }
    # An unsupported item must be visibly missing, never impersonate stone/dirt.
    $missing = New-Canvas 48 48
    for ($y=0;$y -lt 48;$y++) {
        for ($x=0;$x -lt 48;$x++) {
            $color = if (([int][Math]::Floor($x/24) + [int][Math]::Floor($y/24)) % 2 -eq 0) { [System.Drawing.Color]::Magenta } else { [System.Drawing.Color]::Black }
            $missing.SetPixel($x,$y,$color)
        }
    }
    $missing.Save((Join-Path $PreviewDir 'missing-item.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    Add-Sprite 'mycraft_item_unknown' $missing
    Add-Sprite 'mycraft_item_block' ([System.Drawing.Bitmap]::new($images['mycraft_item_dirt']))
    # Special ShieldModel: both six-face cuboids and the original 64x64 UV atlas.
    $shieldGui = (Read-JarJson 'assets/minecraft/models/item/shield.json').display.gui
    if (-not $images.ContainsKey('mycraft_item_shield')) {
        $source = Read-JarImage 'assets/minecraft/textures/entity/shield/shield_base_nopattern.png'
        try {
            $rgba = [Mycraft.Visuals.MinecraftGuiProjection]::Shield([MycraftHudPixels]::Rgba($source),[float[]]$shieldGui.rotation,[float[]]$shieldGui.translation,[float[]]$shieldGui.scale,48)
            Add-Sprite 'mycraft_item_shield' ([MycraftHudPixels]::Image($rgba,48))
        } finally { $source.Dispose() }
    }
    foreach ($required in @('mycraft_item_bow','mycraft_item_arrow','mycraft_item_diamond_sword','mycraft_item_tnt')) {
        if (-not $images.ContainsKey($required)) { throw "Required icon missing: $required" }
    }
    foreach($component in @('dispenser','dropper','lever','redstone_torch','tripwire_hook','observer','piston','sticky_piston',
        'redstone_lamp','redstone_block','daylight_detector','note_block','crafter','stone_button','oak_button','stone_pressure_plate',
        'oak_pressure_plate','light_weighted_pressure_plate','heavy_weighted_pressure_plate','target','rail','powered_rail','detector_rail','activator_rail')) {
        Add-ComponentIcon $component
    }
    Add-ComponentIcon 'soul_sand'
    Add-WitherSkullIcon
    $images['mycraft_item_soul_sand'].Save((Join-Path $PreviewDir 'soul-sand-gui.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    $componentPreview=New-Canvas 576 96
    try {
        $index=0
        foreach($component in @('dispenser','dropper','lever','redstone','redstone_torch','repeater','comparator','observer','piston','sticky_piston','redstone_lamp','redstone_block',
            'daylight_detector','note_block','crafter','stone_button','oak_button','stone_pressure_plate','target','rail','powered_rail','detector_rail','activator_rail','tripwire_hook')) {
            Draw-Pixels $componentPreview $images['mycraft_item_'+$component] (($index%12)*48) ([Math]::Floor($index/12)*48) 48 48 ([System.Drawing.Rectangle]::new(0,0,48,48));$index++
        }
        $componentPreview.Save((Join-Path $PreviewDir 'redstone-components.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    }finally{$componentPreview.Dispose()}
    # Recorded real client snapshots provide a visual regression preview of
    # source alpha/tint. This optional development fixture never enters a save.
    foreach($power in @('power0','power15')) {
        $fixture=Join-Path $PSScriptRoot "../tests/Building/Fixtures/redstone_wire_$power.bin"
        if(Test-Path -LiteralPath $fixture) {
            $wire=[Mycraft.Building.BlockModelSnapshot]::Parse([IO.File]::ReadAllBytes($fixture))
            $rgba=[Mycraft.Building.BlockSurfaceGeometry]::ProjectGui($wire,[float[]]@(90,0,0),[float[]]@(0,0,0),[float[]]@(.9,.9,.9),128)
            $wireImage=[MycraftHudPixels]::Image($rgba,128)
            try{$wireImage.Save((Join-Path $PreviewDir "redstone-wire-$power.png"),[System.Drawing.Imaging.ImageFormat]::Png)}finally{$wireImage.Dispose()}
        }
    }

    $sheetSize = 2048
    $sheets = [System.Collections.Generic.List[System.Drawing.Bitmap]]::new()
    $sheets.Add((New-Canvas $sheetSize $sheetSize))
    $sheet = 1; $x = 2; $y = 2; $rowHeight = 0
    foreach ($sprite in $sprites) {
        if (($x + $sprite.Width + 2) -gt $sheetSize) { $x=2; $y += $rowHeight + 4; $rowHeight=0 }
        if (($y + $sprite.Height + 2) -gt $sheetSize) { $sheets.Add((New-Canvas $sheetSize $sheetSize)); $sheet++; $x=2; $y=2; $rowHeight=0 }
        $sprite.Sheet=$sheet; $sprite.X=$x; $sprite.Y=$y
        $image=$images[$sprite.Name]
        Draw-Pixels $sheets[$sheet-1] $image $x $y $sprite.Width $sprite.Height ([System.Drawing.Rectangle]::new(0,0,$sprite.Width,$sprite.Height))
        $x += $sprite.Width + 4
        $rowHeight=[Math]::Max($rowHeight,$sprite.Height)
    }
    $settings=[System.Xml.XmlWriterSettings]::new()
    $settings.Indent=$true
    $settings.Encoding=[System.Text.UTF8Encoding]::new($false)
    $writer=[System.Xml.XmlWriter]::Create((Join-Path $ModuleGui 'MycraftSpriteData.xml'),$settings)
    try {
        $writer.WriteStartDocument(); $writer.WriteStartElement('SpriteData')
        $writer.WriteComment('Locally generated from Minecraft 26.3 client JAR. SHA256: ' + (Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash)
        $writer.WriteStartElement('SpriteCategories'); $writer.WriteStartElement('SpriteCategory')
        $writer.WriteElementString('Name','MycraftHUD'); $writer.WriteElementString('SpriteSheetCount',$sheets.Count.ToString())
        for ($i=1; $i -le $sheets.Count; $i++) {
            $writer.WriteStartElement('SpriteSheetSize'); $writer.WriteAttributeString('ID',$i.ToString()); $writer.WriteAttributeString('Width',$sheetSize.ToString()); $writer.WriteAttributeString('Height',$sheetSize.ToString()); $writer.WriteEndElement()
        }
        $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteStartElement('SpriteParts')
        foreach ($sprite in $sprites) {
            $writer.WriteStartElement('SpritePart')
            foreach ($pair in @(@('SheetID',$sprite.Sheet),@('Name',$sprite.Name),@('Width',$sprite.Width),@('Height',$sprite.Height),@('SheetX',$sprite.X),@('SheetY',$sprite.Y),@('CategoryName','MycraftHUD'))) { $writer.WriteElementString($pair[0],$pair[1].ToString()) }
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement(); $writer.WriteStartElement('Sprites')
        foreach ($sprite in $sprites) { $writer.WriteStartElement('GenericSprite'); $writer.WriteElementString('Name',$sprite.Name); $writer.WriteElementString('SpritePartName',$sprite.Name); $writer.WriteEndElement() }
        $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndDocument()
    } finally { $writer.Dispose() }
    for ($i=0; $i -lt $sheets.Count; $i++) {
        $base=Join-Path $sheetDir ('MycraftHUD_' + ($i+1))
        $sheets[$i].Save($base + '.png',[System.Drawing.Imaging.ImageFormat]::Png)
        $rgba=[MycraftHudPixels]::Rgba($sheets[$i])
        [System.IO.File]::WriteAllBytes($base + '.rgba',$rgba)
        if ($rgba.Length -ne $sheetSize*$sheetSize*4) { throw 'Invalid RGBA length' }
    }
    $preview=New-Canvas 640 200
    $graphics=[System.Drawing.Graphics]::FromImage($preview)
    try { $graphics.Clear([System.Drawing.Color]::FromArgb(255,54,70,52)) } finally { $graphics.Dispose() }
    Draw-Pixels $preview $images['mycraft_hotbar'] 47 116 546 66 ([System.Drawing.Rectangle]::new(0,0,546,66))
    Draw-Pixels $preview $images['mycraft_selector'] 44 113 72 72 ([System.Drawing.Rectangle]::new(0,0,72,72))
    $demoIcons=@('diamond_sword','flint_and_steel','shield','stone','tnt','cobblestone','oak_planks','bricks','firework_rocket')
    for ($i=0; $i -lt 9; $i++) { Draw-Pixels $preview $images['mycraft_item_'+$demoIcons[$i]] (56+$i*60) 125 48 48 ([System.Drawing.Rectangle]::new(0,0,48,48)) }
    foreach ($row in @(@('armor',27),@('heart',57),@('food',87))) {
        for ($i=0; $i -lt 10; $i++) {
            $state=if ($i -lt 7) { 'full' } elseif ($i -eq 7) { 'half' } else { 'empty' }
            Draw-Pixels $preview $images['mycraft_'+$row[0]+'_'+$state] (53+$i*24) $row[1] 27 27 ([System.Drawing.Rectangle]::new(0,0,27,27))
        }
    }
    $preview.Save((Join-Path $PreviewDir 'mc-hud.png'),[System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()
    $manifest=[ordered]@{ source='Minecraft Java 26.3 local client JAR'; sourceJarSha256=(Get-FileHash -LiteralPath $ClientJar -Algorithm SHA256).Hash; category='MycraftHUD'; sheetCount=$sheets.Count; sheetWidth=$sheetSize; sheetHeight=$sheetSize; spriteCount=$sprites.Count; rawFormat='RGBA8, row-major, top-down, straight alpha'; upscale=3; itemFrame='first 16x16 frame'; blockGui=$blockGui; shieldGui=$shieldGui; tntTextures=$tntModel.textures; projectedModels=@('stone','dirt','cobblestone','oak_planks','bricks','tnt','shield'); skippedNon16Textures=@($skipped); limitations=@('Five cube_all items and TNT cube_bottom_top use model-based GUI projection; other model-only items still need native model rendering.','Shield uses complete cuboid geometry with source GUI rotation/scale, centered and fitted to its fixed 16px sprite bounds.','CPU face lighting approximates Minecraft shading; glint, banner patterns and dynamic item models require native rendering.','Animated and tint-dependent items use their first untinted texture frame.','Local Demo assets only; not a redistributable release package.') }
    $manifest['componentModels']=@($componentEvidence)
    [System.IO.File]::WriteAllText((Join-Path $PreviewDir 'manifest.json'),($manifest | ConvertTo-Json -Depth 5),[System.Text.UTF8Encoding]::new($false))
    Write-Output "Generated $($sprites.Count) sprites on $($sheets.Count) ${sheetSize}x${sheetSize} sheet(s)."
    Write-Output "Preview: $(Join-Path $PreviewDir 'mc-hud.png')"
} finally {
    $archive.Dispose()
    foreach ($image in $images.Values) { $image.Dispose() }
    if ($null -ne $sheets) { foreach ($image in $sheets) { $image.Dispose() } }
}
