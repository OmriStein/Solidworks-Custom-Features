# Per-user registration (no admin needed). Usage: register-user.ps1 [-Dll path] [-Unregister]
param([string]$Dll = (Join-Path $PSScriptRoot 'out\MfgChamfer.dll'), [switch]$Unregister)
$id   = '{35EB7F02-FCBB-4389-918E-1CF435CB89A1}'
$ids  = @($id, '{5AC68A5E-93E8-44F3-A45D-EBF2DBD49154}')
$progIds = @('MfgChamfer.Addin', 'MfgChamfer.Feature')
if ($Unregister) {
  foreach ($c in $ids) { Remove-Item "HKCU:\Software\Classes\CLSID\$c" -Recurse -Force -ErrorAction SilentlyContinue }
  foreach ($p in $progIds) { Remove-Item "HKCU:\Software\Classes\$p" -Recurse -Force -ErrorAction SilentlyContinue }
  Remove-Item "HKCU:\Software\SolidWorks\Addins\$id","HKCU:\Software\SolidWorks\AddInsStartup\$id" -Recurse -Force -ErrorAction SilentlyContinue
  "Unregistered."; return
}
$Dll = (Resolve-Path $Dll).Path
$ra  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
$reg = Join-Path $env:TEMP 'MfgChamfer.reg'
& $ra /nologo /codebase $Dll "/regfile:$reg" | Out-Null
(Get-Content $reg) -replace 'HKEY_CLASSES_ROOT', 'HKEY_CURRENT_USER\Software\Classes' | Set-Content $reg -Encoding Unicode
reg.exe import $reg 2>&1 | Out-Null
$k = "HKCU:\Software\SolidWorks\Addins\$id"
New-Item $k -Force | Out-Null
Set-ItemProperty $k '(default)' 0 -Type DWord
Set-ItemProperty $k 'Title' 'Mfg Chamfer'
Set-ItemProperty $k 'Description' 'Manufacturable chamfers that stop before walls with a chamfer-mill runout'
Set-ItemProperty $k 'Icon Path' (Join-Path (Split-Path $Dll) 'icons\addin_16.png')
$s = "HKCU:\Software\SolidWorks\AddInsStartup\$id"
New-Item $s -Force | Out-Null
Set-ItemProperty $s '(default)' 1 -Type DWord
"Registered $Dll for the current user."
