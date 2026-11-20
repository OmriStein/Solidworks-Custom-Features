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
    public class MacroFeature : FeatureBase   // (the class name is part of the COM registration of existing installs: keep it)
    {
        public const string ProgIdName = "MfgChamfer.Feature";

        protected override string Title { get { return ChamferDef.Title; } }
        protected override object Build(ISldWorks sw, IFeature feat) { return Engine.Build(sw, feat); }
        protected override void ShowPage(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data) { new ChamferPage(sw, model, feat, data).Show(); }
    }
}
