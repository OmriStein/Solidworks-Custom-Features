@echo off
rem Machine-wide registration. Run as Administrator. "register.bat u" unregisters.
net session >nul 2>&1 || (echo Run this as Administrator. & pause & exit /b 1)
set RA=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
if /i "%1"=="u" ("%RA%" /unregister "%~dp0out\MfgChamfer.dll") else ("%RA%" /codebase "%~dp0out\MfgChamfer.dll")
pause
