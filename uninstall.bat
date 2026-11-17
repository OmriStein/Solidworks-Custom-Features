@echo off
rem Removes the Custom-Features add-in registration (files are left in place). Close SOLIDWORKS first.
rem Asks for administrator rights. "uninstall.bat /q" = no pause at the end.
net session >nul 2>&1 || (
  if "%~1"=="" (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs") else (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs")
  exit /b
)
cd /d "%~dp0"
set RA=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
if exist out\CustomFeatures.dll ("%RA%" /nologo /unregister out\CustomFeatures.dll) else (echo out\CustomFeatures.dll not found.)
echo Uninstalled.
if /i not "%1"=="/q" pause
