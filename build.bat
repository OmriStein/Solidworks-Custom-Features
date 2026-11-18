@echo off
rem Builds out\CustomFeatures.dll (x64, .NET Framework 4.x). Close SOLIDWORKS first if the add-in is loaded.
setlocal
cd /d "%~dp0"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set SW=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist
set CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe
if not exist "%CSC%" set CSC=C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
set NAME=%1
if "%NAME%"=="" set NAME=CustomFeatures
if not exist out mkdir out
if not exist out\icons mkdir out\icons
copy /y icons\*.png out\icons >nul
copy /y "%SW%\SolidWorks.Interop.sldworks.dll" out >nul
copy /y "%SW%\SolidWorks.Interop.swpublished.dll" out >nul
copy /y "%SW%\SolidWorks.Interop.swconst.dll" out >nul
"%CSC%" /nologo /target:library /platform:x64 /optimize+ /nostdlib+ /out:out\%NAME%.dll ^
  /r:"%FW%\mscorlib.dll" /r:"%FW%\System.dll" /r:"%FW%\System.Core.dll" /r:"%FW%\System.Windows.Forms.dll" /r:"%FW%\System.Drawing.dll" ^
  /r:out\SolidWorks.Interop.sldworks.dll /r:out\SolidWorks.Interop.swpublished.dll /r:out\SolidWorks.Interop.swconst.dll ^
  src\*.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built out\%NAME%.dll
