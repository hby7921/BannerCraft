param([string]$GameDir=(& (Join-Path $PSScriptRoot 'tools\Get-LocalSetting.ps1') -Name bannerlordGameDir))
& (Join-Path $PSScriptRoot 'start-200-fians-3-withers.ps1') -GameDir $GameDir -FianCount 300
