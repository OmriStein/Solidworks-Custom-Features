using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using static MfgChamfer.Core;

namespace MfgChamfer
{
    // ------------------------------------------------------------------------------------------
    // Which numbers are used by SOLIDWORKS resource strings (see the command-search workaround)
    // ------------------------------------------------------------------------------------------
    static class ResourceIds
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int LoadString(IntPtr h, uint id, System.Text.StringBuilder sb, int max);
        static List<IntPtr> dlls;

        static bool Used(int id)
        {
            if (dlls == null)
            {
                dlls = new List<IntPtr>();
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                var lang = Path.Combine(exeDir, "lang", "english");
                try { if (Addin.Sw != null) { var l = Addin.Sw.GetCurrentLanguage(); if (Directory.Exists(Path.Combine(exeDir, "lang", l))) lang = Path.Combine(exeDir, "lang", l); } } catch { }
                if (Directory.Exists(lang))
                    foreach (var f in Directory.GetFiles(lang, "*.dll"))
                    {
                        var h = LoadLibraryEx(f, IntPtr.Zero, 2);   // LOAD_LIBRARY_AS_DATAFILE
                        if (h != IntPtr.Zero) dlls.Add(h);
                    }
            }
            var sb = new System.Text.StringBuilder(8);
            foreach (var h in dlls) if (LoadString(h, (uint)id, sb, 8) > 0) return true;
            return false;
        }

        // First ID >= id that no resource string uses (-1 if none within 2000)
        public static int NextFree(int id)
        {
            try { for (int i = id; i < id + 2000; i++) if (!Used(i)) return i; } catch (Exception e) { Core.Log("ResourceIds: " + e.Message); }
            return -1;
        }
    }
}
