@echo off
rem Builds out\CustomFeatures.dll (x64, .NET Framework 4.x); the SOLIDWORKS interop types are embedded (/link), so the DLL stands alone. Close SOLIDWORKS first if the add-in is loaded.
setlocal enabledelayedexpansion
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set SW=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist
set CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe
if not exist "%CSC%" set CSC=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
set NAME=%1
if "%NAME%"=="" set NAME=CustomFeatures
rem CFTest = test harness build: interop referenced, not embedded (C# cannot call embedded-interop generics across assemblies)
set IMP=/link
if /i "%NAME%"=="CFTest" set IMP=/r
if not exist out mkdir out
set RES=
for %%f in (icons\*.png) do set RES=!RES! /resource:"%%f",icons.%%~nxf
"%CSC%" /nologo /target:library /platform:x64 /optimize+ /nostdlib+ /out:out\%NAME%.dll ^
  /r:"%FW%\mscorlib.dll" /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" ^
  %IMP%:"%SW%\SolidWorks.Interop.sldworks.dll" %IMP%:"%SW%\SolidWorks.Interop.swpublished.dll" %IMP%:"%SW%\SolidWorks.Interop.swconst.dll" ^
  %RES% src\*.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built out\%NAME%.dll
