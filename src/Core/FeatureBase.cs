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
    // Base of the macro-feature COM classes (one per feature): what every feature shares - icons, edit definition, rebuild, security.
    // The derived class carries [ComVisible(true), Guid, ProgId] and supplies the title, the rebuild and the page.
    [ComVisible(false)]
    public abstract class FeatureBase : ISwComFeature
    {
        protected abstract string Title { get; }   // used in messages ("CNC Chamfer")
        protected abstract object Build(ISldWorks sw, IFeature feat);   // Regenerate: the new body, or an error string
        protected abstract void ShowPage(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data);   // Edit definition

        // regular / suppressed / highlighted for each size (SOLIDWORKS picks by DPI): icons\<prefix>_reg_20.png ...
        public static string[] Icons(string prefix)
        {
            var list = new List<string>();
            foreach (var s in new[] { 20, 32, 40 })
                foreach (var st in new[] { "reg", "sup", "hl" })
                    list.Add(Addin.Icon(prefix + "_" + st + "_" + s + ".png"));
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
                ShowPage((ISldWorks)app, model, feat, data);
                return true;
            }
            catch (Exception e) { MessageBox.Show(Title + ":\n" + e); return false; }
        }

        public object Regenerate(object app, object modelDoc, object feature)
        {
            try { return Build((ISldWorks)app, (IFeature)feature); }
            catch (Exception e) { Log(e.ToString()); return Title + ": " + e.Message; }
        }

        public object Security(object app, object modelDoc, object feature)
        {
            return (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
        }
    }
}
