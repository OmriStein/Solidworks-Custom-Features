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
    // Macro feature handler (lives in the FeatureManager tree)
    // ------------------------------------------------------------------------------------------
    [ComVisible(true), Guid("5AC68A5E-93E8-44F3-A45D-EBF2DBD49154"), ProgId(MacroFeature.ProgIdName)]
    public class MacroFeature : ISwComFeature
    {
        public const string ProgIdName = "MfgChamfer.Feature";

        public static string[] Icons()
        {
            // regular / suppressed / highlighted for each size (SOLIDWORKS picks by DPI)
            var list = new List<string>();
            foreach (var s in new[] { 20, 32, 40 })
                foreach (var st in new[] { "reg", "sup", "hl" })
                    list.Add(Addin.Icon("feat_" + st + "_" + s + ".png"));
            return list.ToArray();
        }

        public object Edit(object app, object modelDoc, object feature)
        {
            try
            {
                var model = (IModelDoc2)modelDoc;
                var feat = (IFeature)feature;
                var data = (IMacroFeatureData)feat.GetDefinition();
                if (!data.AccessSelections(model, null)) return false;
                new ChamferPage((ISldWorks)app, model, feat, data).Show();
                return true;
            }
            catch (Exception e) { MessageBox.Show("CNC Chamfer:\n" + e); return false; }
        }

        public object Regenerate(object app, object modelDoc, object feature)
        {
            try { return Engine.Build((ISldWorks)app, (IFeature)feature); }
            catch (Exception e) { Core.Log(e.ToString()); return "CNC Chamfer: " + e.Message; }
        }

        public object Security(object app, object modelDoc, object feature)
        {
            return (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
        }
    }
}
