@echo off
cd /d "%~dp0"
if not exist "current-build.txt" powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
if not exist "current-build.txt" exit /b 1
set /p APPDIR=<current-build.txt
if not exist "%APPDIR%\ObsiDopf.exe" exit /b 1
start "" "%APPDIR%\ObsiDopf.exe"



