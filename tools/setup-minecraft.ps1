param([string]$Root = (Join-Path $PSScriptRoot '..\runtime\minecraft'))

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$downloads = Join-Path $Root 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

function Get-VerifiedDownload {
    param([string]$Url, [string]$Name, [string]$Algorithm, [string]$Expected)
    $destination = Join-Path $downloads $Name
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm $Algorithm).Hash -eq $Expected) {
            Write-Output "Verified existing: $Name"
            return
        }
        throw "Existing download has a different checksum: $destination"
    }
    $partial = "$destination.partial"
    & curl.exe --fail --location --retry 3 --connect-timeout 30 --max-time 600 --output $partial $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Name" }
    if ((Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash -ne $Expected) {
        throw "Checksum mismatch: $partial"
    }
    Move-Item -LiteralPath $partial -Destination $destination
    Write-Output "Downloaded and verified: $Name"
}

Get-VerifiedDownload 'https://github.com/PrismLauncher/PrismLauncher/releases/download/11.1.1/PrismLauncher-Windows-MSVC-Portable-11.1.1.zip' 'PrismLauncher-Windows-MSVC-Portable-11.1.1.zip' 'SHA256' 'ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7'
Get-VerifiedDownload 'https://piston-data.mojang.com/v1/objects/e877b6a07acd633fb3bb475002175cec036e7b87/client.jar' 'minecraft-26.3-client.jar' 'SHA1' 'e877b6a07acd633fb3bb475002175cec036e7b87'
Get-VerifiedDownload 'https://cdn.modrinth.com/data/P7dR8mSH/versions/bNnaTiuM/fabric-api-0.161.0%2B26.3.jar' 'fabric-api-0.161.0+26.3.jar' 'SHA512' 'ed6b2586d6fde11fde8472f5a527c51e99b67026e46f94d4bfd85e7e28ce5ee299173ee16ad576ceb51f39f98d30a811086a6deb1a86a524859cc16e12da109d'
Get-VerifiedDownload 'https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.4.1%2B1/OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip' 'OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip' 'SHA256' '00c847d804f4a78e9f04f2683faf14fed898535b177b7fc704486cb0284e9283'

$loaderUrl = 'https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar'
$loaderHash = (Invoke-RestMethod -Uri "$loaderUrl.sha256" -TimeoutSec 30).ToString().Trim().Split(' ')[0]
if ($loaderHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid Fabric checksum response' }
Get-VerifiedDownload $loaderUrl 'fabric-loader-0.19.5.jar' 'SHA256' $loaderHash

$launcher = Join-Path $Root 'PrismLauncher'
$javaRoot = Join-Path $Root 'java'
if (-not (Test-Path -LiteralPath (Join-Path $launcher 'prismlauncher.exe'))) {
    Expand-Archive -LiteralPath (Join-Path $downloads 'PrismLauncher-Windows-MSVC-Portable-11.1.1.zip') -DestinationPath $launcher
}
if (-not (Test-Path -LiteralPath (Join-Path $javaRoot 'jdk-25.0.4.1+1\bin\java.exe'))) {
    Expand-Archive -LiteralPath (Join-Path $downloads 'OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip') -DestinationPath $javaRoot
}
$mods = Join-Path $launcher 'instances\Mycraft-Demo\.minecraft\mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
$fabricApi = Join-Path $mods 'fabric-api-0.161.0+26.3.jar'
if (-not (Test-Path -LiteralPath $fabricApi)) {
    Copy-Item -LiteralPath (Join-Path $downloads 'fabric-api-0.161.0+26.3.jar') -Destination $fabricApi
}
Write-Output "Prepared runtime: $Root"
Write-Output 'Minecraft libraries and assets still need to be fetched by the launcher.'
Write-Output 'Sign in with your Minecraft Java account in Prism before playing.'
