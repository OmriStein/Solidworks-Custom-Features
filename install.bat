@echo off
rem Installs the Custom-Features add-in for SOLIDWORKS (all users): builds out\CustomFeatures.dll and registers it.
rem Close SOLIDWORKS first. Asks for administrator rights. "install.bat /q" = no pause at the end.
net session >nul 2>&1 || (
  if "%~1"=="" (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs") else (powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs")
  exit /b
)
cd /d "%~dp0"
tasklist /fi "imagename eq SLDWORKS.exe" | find /i "SLDWORKS.exe" >nul && (echo Close SOLIDWORKS first. & goto end)
call build.bat
if not exist out\CustomFeatures.dll (echo No out\CustomFeatures.dll - build failed. & goto end)
set RA=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
rem older builds were called MfgChamfer.dll: remove that registration first
if exist out\MfgChamfer.dll ("%RA%" /nologo /unregister out\MfgChamfer.dll >nul 2>&1 & del out\MfgChamfer.dll)
"%RA%" /nologo /codebase out\CustomFeatures.dll || (echo Registration failed. & goto end)
echo Installed. Start SOLIDWORKS - "Custom-Features" is in Tools ^> Add-Ins, the commands under Insert ^> Custom-Features.
:end
if /i not "%1"=="/q" pause
