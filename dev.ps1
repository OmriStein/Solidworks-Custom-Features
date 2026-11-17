# Dev loop: close SOLIDWORKS, rebuild, relaunch, open the test part
$r = $PSScriptRoot
Stop-Process -Name sldworks -Force -ErrorAction SilentlyContinue; Start-Sleep 4
cmd /c "`"$r\build.bat`"" ; if ($LASTEXITCODE) { return }
Remove-Item "$env:TEMP\MfgChamfer.log" -ErrorAction SilentlyContinue
Start-Process 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe'
