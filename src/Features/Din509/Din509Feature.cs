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
using static MfgChamfer.Geo;
namespace MfgChamfer
{
    [ComVisible(true), Guid("0B6F9C2E-4E7A-4D52-9A0C-3F1D8E5B7A64"), ProgId(Din509Engine.ProgIdName)]
    public class Din509Feature : FeatureBase
    {
        protected override string Title { get { return Din509Def.Title; } }
        protected override object Build(ISldWorks sw, IFeature feat) { return Din509Engine.Build(sw, feat); }
        protected override void ShowPage(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data) { new Din509Page(sw, model, feat, data).Show(); }
    }

    public class Din509Def : FeatureDef
    {
        public const string Title = "DIN 509 Undercut";
        public static Din509Params Last = new Din509Params();   // settings of the last OK: start values of the next page

        public override string Name { get { return Title; } }
        public override string Hint { get { return "Undercut to DIN 509 (forms E, F, G, H) at the inner corner of a shaft shoulder"; } }
        public override int IconIndex { get { return 1; } }
        public override void Insert(ISldWorks sw, IModelDoc2 model) { new Din509Page(sw, model, null, null).Show(); }

        public static IFeature InsertFeature(ISldWorks sw, IModelDoc2 model, IEdge edge, Din509Params p)
        {
            var body = edge.GetBody();
            var sm = (ISelectionMgr)model.SelectionManager;
            model.ClearSelection2(true);
            var sd = (SelectData)sm.CreateSelectData();
            sd.Mark = 1;
            ((IEntity)edge).Select4(true, sd);
            var f = model.FeatureManager.InsertMacroFeature3(Title, Din509Engine.ProgIdName, null,
                Din509Params.Names, Din509Params.Types, p.Values(), null, null,
                new[] { new DispatchWrapper(body) }, FeatureBase.Icons("din509"), (int)swMacroFeatureOptions_e.swMacroFeatureByDefault);
            model.ClearSelection2(true);
            if (f == null) sw.SendMsgToUser2(Title + ": the feature could not be created.", (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            else
            {
                bool warn; int code = f.GetErrorCode2(out warn);
                if (code != 0 && !warn) sw.SendMsgToUser2(Title + ": rebuild error - hover the feature in the tree for details.", (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
            }
            return f;
        }
    }
}