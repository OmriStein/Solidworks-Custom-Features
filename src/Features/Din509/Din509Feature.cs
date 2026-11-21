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

        static Din509Params Clip;   // settings copied with Ctrl+C on a DIN 509 Undercut

        // Ctrl+C / Ctrl+V as for the CNC Chamfer (SOLIDWORKS cannot copy macro features):
        //   Copy  with a DIN 509 Undercut (tree item or one of its faces) selected -> remember its form and size
        //   Paste with exactly one edge selected and settings remembered           -> new undercut at that shoulder edge
        public override int OnCommandPre(ISldWorks sw, IModelDoc2 m, int Command)
        {
            if (m == null) return 0;
            var sm = (ISelectionMgr)m.SelectionManager;
            if (Command == CmdCopy || Command == CmdCut)
            {
                var f = SelectedUndercut(sm);
                if (f == null) { Clip = null; return 0; }          // something else copied: let SW handle it
                if (Command == CmdCut) return 0;
                Clip = Din509Params.Read((IMacroFeatureData)f.GetDefinition());
                return 1;
            }
            if (Command == CmdPaste && Clip != null)
            {
                if (sm.GetSelectedObjectCount2(-1) != 1 || sm.GetSelectedObjectType3(1, -1) != (int)swSelectType_e.swSelEDGES) return 0;
                var edge = (IEdge)sm.GetSelectedObject6(1, -1);
                try { Din509Engine.ShaftRadius(edge); }
                catch (Exception ex) { sw.SendMsgToUser2(Title + ": " + ex.Message + ".", (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk); return 1; }
                InsertFeature(sw, m, edge, Clip);
                return 1;
            }
            return 0;
        }

        // The selected DIN 509 Undercut: the feature itself (tree) or the feature owning a selected face
        static IFeature SelectedUndercut(ISelectionMgr sm)
        {
            if (sm.GetSelectedObjectCount2(-1) != 1) return null;
            var o = sm.GetSelectedObject6(1, -1);
            var f = o as IFeature;
            var face = o as IFace2;
            if (f == null && face != null) f = face.GetFeature() as IFeature;
            if (f == null || f.GetTypeName2() != "MacroFeature") return null;
            var d = f.GetDefinition() as IMacroFeatureData;
            return d != null && d.GetProgId() == Din509Engine.ProgIdName ? f : null;
        }
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