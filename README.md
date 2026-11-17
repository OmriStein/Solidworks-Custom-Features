# SOLIDWORKS Custom Features

SOLIDWORKS 2026 add-in (C#, .NET Framework 4, x64) that hosts custom features under **Insert > Custom-Features**.

## CNC Chamfer
A 45° chamfer that can actually be machined with a 90° chamfer mill:
- Straight, convex, 90° edges.
- Ends that run into a wall stop at tool radius + clearance and finish with the cone the chamfer mill leaves.
- Per-edge tool direction (Reverse direction).
- Blend perpendicular chamfers around an inside corner (radius = chamfer distance).
- Optional flat corner where three chamfers meet (like the native Chamfer).
- Live native-style preview, edit, Ctrl+C / Ctrl+V of the feature settings.

## Install / uninstall
Close SOLIDWORKS, then run `install.bat` (asks for administrator rights). It builds `out\CustomFeatures.dll`
(needs the Visual Studio C# compiler and the SOLIDWORKS API redist) and registers it. In SOLIDWORKS the add-in
is listed as **Custom-Features** in Tools > Add-Ins.

`uninstall.bat` removes the registration.

For development, `build.bat` only rebuilds the DLL (SOLIDWORKS must be closed, it locks the DLL);
`dev.ps1` closes SOLIDWORKS, rebuilds and restarts it. Test parts are in `test\`.
