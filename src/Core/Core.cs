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

namespace MfgChamfer
{
    // Shared helpers of the add-in (all features)
    public static class Core
    {
        public static void Log(string m)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "CustomFeatures.log"), DateTime.Now.ToString("HH:mm:ss ") + m + System.Environment.NewLine); } catch { }
        }
    }
}
