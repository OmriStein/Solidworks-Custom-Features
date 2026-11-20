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
    // Feature creation (shared by the PropertyManager page and Paste)
    // ------------------------------------------------------------------------------------------
    public static class Inserter
    {
        public static IFeature Insert(ISldWorks sw, IModelDoc2 model, IList<object> edges, ChamferParams p)
        {
            var body = ((IEdge)edges[0]).GetBody();
            if (p.Flips.Length < edges.Count) p.Flips = p.Flips.PadRight(edges.Count, '0');   // one flag per edge (= edge count)
            p.Edges = ChamferParams.EncodeEdges(edges);
            var sm = (ISelectionMgr)model.SelectionManager;
            model.ClearSelection2(true);
            var sd = (SelectData)sm.CreateSelectData();
            sd.Mark = 1;
            foreach (var e in edges) ((IEntity)e).Select4(true, sd);
            var f = model.FeatureManager.InsertMacroFeature3("CNC Chamfer", MacroFeature.ProgIdName, null,
                ChamferParams.Names, ChamferParams.Types, p.Values(), null, null,
                new[] { new DispatchWrapper(body) }, MacroFeature.Icons(), (int)swMacroFeatureOptions_e.swMacroFeatureByDefault);
            model.ClearSelection2(true);
            if (f == null)
                sw.SendMsgToUser2("CNC Chamfer: the feature could not be created.", (int)swMessageBoxIcon_e.swMbStop, (int)swMessageBoxBtn_e.swMbOk);
            else
                ReportError(sw, f);
            return f;
        }

        public static void ReportError(ISldWorks sw, IFeature f)
        {
            bool warn;
            int code = f.GetErrorCode2(out warn);
            if (code != 0 && !warn)
                sw.SendMsgToUser2("CNC Chamfer: rebuild error - hover the feature in the tree for details.",
                    (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
        }

        // The CNC Chamfer feature selected in the tree / graphics, or null
        public static IFeature SelectedFeature(IModelDoc2 model)
        {
            var sm = (ISelectionMgr)model.SelectionManager;
            if (sm.GetSelectedObjectCount2(-1) < 1) return null;
            var f = sm.GetSelectedObject6(1, -1) as IFeature;
            if (f == null || f.GetTypeName2() != "MacroFeature") return null;
            var d = f.GetDefinition() as IMacroFeatureData;
            return d != null && d.GetProgId() == MacroFeature.ProgIdName ? f : null;
        }

        public static List<object> SelectedEdges(IModelDoc2 model)
        {
            var list = new List<object>();
            var sm = (ISelectionMgr)model.SelectionManager;
            int n = sm.GetSelectedObjectCount2(-1);
            for (int i = 1; i <= n; i++)
                if (sm.GetSelectedObjectType3(i, -1) == (int)swSelectType_e.swSelEDGES)
                    list.Add(sm.GetSelectedObject6(i, -1));
            return list;
        }
    }
}
