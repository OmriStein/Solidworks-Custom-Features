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
    [ComVisible(true)]
    public class Din509Page : FeaturePage
    {
        const int IdSel = 10, IdForm = 20, IdSize = 21;
        static readonly string[] FormNames = { "E   no wall cut", "F   wall flank 8\u00B0", "G   small included angle (55\u00B0)", "H   large included angle (60\u00B0)" };
        const string Hint = "Select the circular edge where the shaft or bore meets the shoulder (the inner corner), then the undercut form and size.\n" +
                            "Sizes listed fit the diameter (DIN 509, Table 1; for bores the standard applies the same values by analogy).";
        readonly ISldWorks sw;
        readonly IModelDoc2 model;
        readonly IFeature feat;            // null when inserting
        readonly IMacroFeatureData data;   // null when inserting
        readonly Din509Params p;
        IPropertyManagerPageSelectionbox sel;
        IPropertyManagerPageCombobox cbForm, cbSize;
        List<Din509Row> rows = new List<Din509Row>();
        Din509Row cur;
        IEdge edge;
        bool ok, loading, shown, busy;
        string problem, edgeError;                    // why the current choice cannot be built (OK keeps the page open)
        readonly List<IBody2> preview = new List<IBody2>();   // temporary bodies: the cut part and the yellow strokes
        IBody2 hidden, pristine;                              // the real body while it is hidden for the preview, and a copy kept for comparing faces
        readonly List<object> pending = new List<object>();
        readonly System.Windows.Forms.Timer selectTimer = new System.Windows.Forms.Timer { Interval = 1 };

        public Din509Page(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data)
        {
            this.sw = sw; this.model = model; this.feat = feat; this.data = data;
            p = data != null ? Din509Params.Read(data) : Din509Def.Last; selectTimer.Tick += SelectPending;
        }

        public void Show()
        {
            CreatePage(sw, Din509Def.Title, Hint, "din509_title.png");
            int gOpts = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);
            int cOpts = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
            short left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            short indent = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent;

            var g1 = (IPropertyManagerPageGroup)page.AddGroupBox(1, "Shoulder Edge", gOpts); keep.Add(g1);
            sel = (IPropertyManagerPageSelectionbox)g1.AddControl2(IdSel, (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox, "Edge", indent, cOpts,
                "The circular edge between the shaft and the shoulder.");
            ((IPropertyManagerPageControl)sel).SetStandardPictureLabel((int)swControlBitmapLabelType_e.swBitmapLabel_SelectEdge);
            sel.SingleEntityOnly = true;
            sel.Height = 20;
            sel.Mark = 1;
            sel.SetSelectionFilters(new[] { (int)swSelectType_e.swSelEDGES });

            var g2 = (IPropertyManagerPageGroup)page.AddGroupBox(2, "Undercut", gOpts); keep.Add(g2);
            keep.Add(g2.AddControl2(IdForm + 100, (short)swPropertyManagerPageControlType_e.swControlType_Label, "Form", left, cOpts, ""));
            cbForm = (IPropertyManagerPageCombobox)g2.AddControl2(IdForm, (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Form", indent, cOpts, "DIN 509 form");
            cbForm.AddItems(FormNames);
            int fi = Math.Max(0, "EFGH".IndexOf(p.FormChar));
            cbForm.CurrentSelection = (short)fi;
            keep.Add(g2.AddControl2(IdSize + 100, (short)swPropertyManagerPageControlType_e.swControlType_Label, "Size (radius x depth)", left, cOpts, ""));
            cbSize = (IPropertyManagerPageCombobox)g2.AddControl2(IdSize, (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Size", indent, cOpts, "radius r x depth t1 (mm)");

            Addin.PageOpen = true;
            page.Show2(0);
            sel.SetSelectionFocus();

            if (data != null)   // edit definition: put the stored edge back
            {
                var e = Din509Engine.SelectedEdge(data);
                if (e != null)
                {
                    var sd = (SelectData)((ISelectionMgr)model.SelectionManager).CreateSelectData();
                    sd.Mark = 1;
                    ((IEntity)e).Select4(true, sd);
                }
            }
            Refill();
            shown = true;
            UpdatePreview();
        }

        IEdge PickedEdge()
        {
            var sm = (ISelectionMgr)model.SelectionManager;
            if (sm.GetSelectedObjectCount2(1) < 1 || sm.GetSelectedObjectType3(1, 1) != (int)swSelectType_e.swSelEDGES) return null;
            return sm.GetSelectedObject6(1, 1) as IEdge;
        }

        // Size list for the chosen form and the picked shaft (keeps the current size when it is still listed)
        void Refill()
        {
            loading = true;
            try
            {
                edge = PickedEdge();
                edgeError = null;
                double d1 = 0;
                if (edge != null)
                    try { d1 = 2000 * Din509Engine.ShaftRadius(edge); Message(Hint); }
                    catch (Exception ex) { edgeError = ex.Message; Message(ex.Message); edge = null; }
                char form = "EFGH"[Math.Max(0, (int)cbForm.CurrentSelection)];
                rows = Din509Table.For(form, d1);
                cbSize.Clear();
                cbSize.AddItems(rows.Select(r => r.ToString()).ToArray());
                int k = rows.FindIndex(r => Math.Abs(r.R / 1000 - p.R) < 1e-7 && Math.Abs(r.T1 / 1000 - p.T1) < 1e-7);
                cbSize.CurrentSelection = (short)Math.Max(0, k);
                cur = rows[Math.Max(0, k)];
            }
            finally { loading = false; }
        }

        public override void OnSelectionboxListChanged(int Id, int Count) { if (!loading) { Refill(); UpdatePreview(); } }

        public override void OnComboboxSelectionChanged(int Id, int Item)
        {
            if (loading) return;
            if (Id == IdForm) { p.Form = "EFGH"[Math.Max(0, Item)].ToString(); Refill(); }
            else if (Id == IdSize && Item >= 0 && Item < rows.Count) { cur = rows[Item]; p.R = cur.R / 1000; p.T1 = cur.T1 / 1000; }
            UpdatePreview();
        }

        // Live preview (native style, like the CNC chamfer): the part with the undercut cut in, the real part hidden, and yellow strokes
        // on the new faces (their outline and strokes across). A choice that cannot be built is reported instead of previewed.
        void UpdatePreview()
        {
            if (busy) return;
            busy = true;
            try
            {
                ClearPreview();
                problem = edgeError;
                OkCursor(false);
                if (!shown) return;
                if (edge == null || cur == null) { model.GraphicsRedraw2(); return; }
                try
                {
                    var modeler = (IModeler)sw.GetModeler();
                    var np = Din509Params.From(cur);
                    var orig = (IBody2)edge.GetBody();
                    pristine = (IBody2)orig.Copy();
                    var origFaces = ((object[])pristine.GetFaces()).Cast<IFace2>().ToList();
                    var result = Din509Engine.Cut(modeler, (IBody2)orig.Copy(), edge, np);
                    var mp = model.MaterialPropertyValues as double[];
                    int color = mp != null && mp.Length >= 3 ? (int)(mp[0] * 255) | ((int)(mp[1] * 255) << 8) | ((int)(mp[2] * 255) << 16) : 0xB4B4B4;
                    bool sectioned = Core.Sectioned(model);   // a temporary body ignores the section, so the cut-away part would show: draw the strokes only
                    if (!sectioned)
                    {
                        result.Display3(model, color, (int)swTempBodySelectOptions_e.swTempBodySelectable);
                        preview.Add(result);
                        orig.DisableDisplay = true;
                        hidden = orig;
                    }
                    var strokes = Din509Engine.Strokes(modeler, edge, result, origFaces);
                    foreach (var w in strokes)
                    {
                        w.Display3(model, 0x00FFFF, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);   // COLORREF (BGR): yellow
                        preview.Add(w);
                    }
                    Message(Hint);
                    OkCursor(true);
                }
                catch (Exception ex) { ClearPreview(); problem = ex.Message; Message(problem); Core.Log("din509 preview: " + ex.Message); }
                model.GraphicsRedraw2();
            }
            finally { busy = false; }
        }

        void ClearPreview()
        {
            foreach (var b in preview) { try { b.Hide(model); } catch { } }
            preview.Clear();
            if (hidden != null) { try { hidden.DisableDisplay = false; } catch { } hidden = null; }
        }

        // While the preview is shown the real part is hidden, so clicks land on the preview: select the real edge it lies on instead.
        public override bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText)
        {
            try
            {
                var e = Selection as IEdge;
                var b = e != null ? e.GetBody() as IBody2 : null;
                if (b == null || !b.IsTemporaryBody()) return true;
                var real = hidden == null ? null : MapToReal(e, hidden);
                if (real != null) { pending.Add(real); selectTimer.Start(); }
            }
            catch (Exception ex) { Core.Log("din509 submit: " + ex.Message); }
            return false;
        }

        void SelectPending(object sender, EventArgs args)
        {
            selectTimer.Stop();
            if (!shown || pending.Count == 0) { pending.Clear(); return; }
            var e = pending[pending.Count - 1]; pending.Clear();
            var sd = (SelectData)((ISelectionMgr)model.SelectionManager).CreateSelectData();
            sd.Mark = 1;
            ((IEntity)e).Select4(false, sd);
        }

        // The real edge a preview edge lies on: the one closest to three points along it (all within 4 mm). New edges map to nothing.
        static IEdge MapToReal(IEdge temp, IBody2 real)
        {
            var cp = temp.GetCurveParams3();
            var c = (ICurve)temp.GetCurve();
            var pts = new List<double[]>();
            foreach (double f in new[] { 0.25, 0.5, 0.75 })
            {
                var q = c.Evaluate2(cp.UMinValue + f * (cp.UMaxValue - cp.UMinValue), 0) as double[];
                if (q == null) return null;
                pts.Add(q);
            }
            IEdge best = null; double bd = 4e-3 * 4e-3;
            foreach (IEdge e in (object[])real.GetEdges())
            {
                double worst = 0;
                foreach (var q in pts)
                {
                    var r = e.GetClosestPointOn(q[0], q[1], q[2]) as double[];
                    if (r == null) { worst = double.MaxValue; break; }
                    worst = Math.Max(worst, (r[0] - q[0]) * (r[0] - q[0]) + (r[1] - q[1]) * (r[1] - q[1]) + (r[2] - q[2]) * (r[2] - q[2]));
                }
                if (worst < bd) { bd = worst; best = e; }
            }
            return best;
        }
        // Right-click in the graphics area = OK (native "OK" cursor) once there is something valid to build
        void OkCursor(bool on)
        {
            try { page.SetCursor((int)(on ? swPropertyManagerPageCursors_e.swPropertyManagerPageCursors_Okay : swPropertyManagerPageCursors_e.swPropertyManagerPageCursors_None)); } catch { }
        }

        // Native-style "Rebuild Errors" balloon, pointing at where OK was clicked
        void ShowTip(string why)
        {
            try
            {
                var pt = Cursor.Position;
                sw.ShowBubbleTooltipAt2(pt.X + 4, pt.Y, (int)swArrowPosition.swArrowLeftTop, "Rebuild Errors", "Could not build the " + Din509Def.Title + ":\n" + why,
                    (int)swBitMaps.swBitMapTreeError, "", "", 0, 0, "", "");
            }
            catch { }
        }

        public override void OnClose(int Reason)
        {
            ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (ok)
            {
                edge = PickedEdge();
                string why = edge == null || cur == null ? "Select the circular shoulder edge first." : edgeError ?? problem;
                if (why == null) try { Din509Engine.ShaftRadius(edge); } catch (Exception ex) { why = ex.Message; }   // re-check the edge that is picked right now
                if (why != null)
                {
                    try { Message(why); } catch { }
                    ShowTip(why);
                    ok = false;
                    throw new COMException("", 1);   // S_FALSE: keep the page open
                }
            }
            shown = false;
            if (!ok) ClearPreview();   // OK: the preview stays until the feature has rebuilt (AfterClose)
        }

        public override void AfterClose()
        {
            Addin.PageOpen = false;
            IModelView view = null;
            try
            {
                if (!ok) { ClearPreview(); if (data != null) data.ReleaseSelectionAccess(); return; }
                var np = Din509Params.From(cur);
                Din509Def.Last = np;
                // Freeze the view, rebuild, then drop the preview and redraw once.
                try { view = model.ActiveView as IModelView; if (view != null) view.EnableGraphicsUpdate = false; } catch { view = null; }
                if (hidden != null) { try { hidden.DisableDisplay = false; } catch { } hidden = null; }   // show the real part again before it rebuilds
                if (feat == null) Din509Def.InsertFeature(sw, model, edge, np);
                else
                {
                    var objs = new object[] { edge };
                    var wrapped = objs.Select(o => new DispatchWrapper(o)).ToArray();
                    data.SetSelections2(wrapped, new[] { 1 }, null);
                    np.Write(data);
                    feat.ModifyDefinition(data, model, null);
                }
            }
            catch (Exception e) { ClearPreview(); MessageBox.Show(Din509Def.Title + ":\n" + e); }
            finally
            {
                ClearPreview();
                if (view != null) try { view.EnableGraphicsUpdate = true; } catch { }
                try { model.GraphicsRedraw2(); } catch { }
            }
        }
    }
}
