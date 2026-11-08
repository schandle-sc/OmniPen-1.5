@echo off
title OmniPen 1.5 - Build
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
