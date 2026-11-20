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
    // CNC Chamfer as seen by the add-in host: command text, new-feature page, Copy / Paste
    public class ChamferDef : FeatureDef
    {
        public const string Title = "CNC Chamfer";
        public static ChamferParams Last = new ChamferParams();   // settings of the last OK: start values of the next page
        static ChamferParams Clip;                                  // settings copied with "Copy CNC Chamfer"

        public override string Name { get { return Title; } }
        public override string Hint { get { return "Chamfer for CNC: stops before walls and ends with the runout a chamfer mill leaves"; } }
        public override void Insert(ISldWorks sw, IModelDoc2 model) { new ChamferPage(sw, model, null, null).Show(); }

        // Ctrl+C / Ctrl+V: SOLIDWORKS can't put macro features on its clipboard ("This item cannot be copied"), so the add-in
        // catches the Copy / Paste commands before they run:
        //   Copy  with a CNC Chamfer (tree item or one of its faces) selected -> remember its settings
        //   Paste with only edges selected and settings remembered            -> new CNC Chamfer on those edges
        // Everything else goes to SOLIDWORKS as usual.
        public override int OnCommandPre(ISldWorks sw, IModelDoc2 m, int Command)
        {
            if (Command == CmdCopy || Command == CmdCut)
            {
                var f = m == null ? null : SelectedMfgFeature(m);
                if (f == null) { Clip = null; return 0; }          // something else copied: let SW handle it
                if (Command == CmdCut) return 0;
                Clip = ChamferParams.Read((IMacroFeatureData)f.GetDefinition());
                Clip.Flips = ""; Clip.Edges = "";
                return 1;                                           // handled - don't run SW's copy
            }
            if (Command == CmdPaste && Clip != null)
            {
                if (m == null) return 0;
                var sm = (ISelectionMgr)m.SelectionManager;
                int n = sm.GetSelectedObjectCount2(-1);
                if (n == 0) return 0;
                var edges = new List<object>();
                for (int i = 1; i <= n; i++)
                {
                    if (sm.GetSelectedObjectType3(i, -1) != (int)swSelectType_e.swSelEDGES) return 0;   // not only edges
                    edges.Add(sm.GetSelectedObject6(i, -1));
                }
                Inserter.Insert(sw, m, edges, Clip.Clone());
                return 1;
            }
            return 0;
        }

        // The selected CNC Chamfer: the feature itself (tree) or the feature owning a selected face
        static IFeature SelectedMfgFeature(IModelDoc2 m)
        {
            var sm = (ISelectionMgr)m.SelectionManager;
            if (sm.GetSelectedObjectCount2(-1) != 1) return null;
            var o = sm.GetSelectedObject6(1, -1);
            var f = o as IFeature;
            var face = o as IFace2;
            if (f == null && face != null) f = face.GetFeature() as IFeature;
            if (f == null || f.GetTypeName2() != "MacroFeature") return null;
            var d = f.GetDefinition() as IMacroFeatureData;
            return d != null && d.GetProgId() == MacroFeature.ProgIdName ? f : null;
        }
    }
}
