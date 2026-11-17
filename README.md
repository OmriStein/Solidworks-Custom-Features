# SOLIDWORKS Custom Features

SOLIDWORKS 2026 add-in (C#, .NET Framework 4, x64) that hosts custom features under **Insert > Custom-Features**.

## CNC Chamfer
A 45° chamfer that can actually be machined with a 90° chamfer mill:
- Straight, convex, 90° edges.
- Ends that run into a wall stop at tool radius + clearance and finish with the cone the chamfer mill leaves.
- Per-edge tool direction (Reverse direction).
- Blend perpendicular chamfers around an inside corner (radius = chamfer distance).
- Live native-style preview, edit, Ctrl+C / Ctrl+V of the feature settings.

## Build and install
1. Close SOLIDWORKS.
2. `build.bat` builds `out\MfgChamfer.dll` (needs the Visual Studio C# compiler and the SOLIDWORKS API redist).
3. `register.bat` (run as administrator) registers the add-in; then enable it in Tools > Add-Ins.

Test parts are in `test\`.
