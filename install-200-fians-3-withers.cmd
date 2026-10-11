@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-demo.ps1" -PackageDir "%~dp0.build-game\army-stage\Modules\Mycraft"
if errorlevel 1 pause
