@echo off
rem Builds outputs\release\GTAVRidingCompanion-Setup.exe. Extra options: -SkipTests, -Clean
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Build-Installer.ps1" %*
set CODE=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %CODE%
