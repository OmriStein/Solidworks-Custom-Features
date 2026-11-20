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
    // PropertyManager page (insert + edit definition)
    // ------------------------------------------------------------------------------------------
    [ComVisible(true)]
    public class ChamferPage : IPropertyManagerPage2Handler9
    {
        const int IdSel = 10, IdSize = 20, IdDia = 21, IdClr = 22, IdFlip = 23, IdBlend = 24, IdFlat = 25, IdAngle = 26, IdTangent = 27, IdTip = 28, IdMenuReverse = 30;
        const string FlipCaption = "Reverse direction";
        readonly ISldWorks sw;
        readonly IModelDoc2 model;
        readonly IFeature feat;          // null when inserting
        readonly IMacroFeatureData data; // null when inserting
        readonly ChamferParams p;
        IPropertyManagerPage2 page;
        IPropertyManagerPageSelectionbox sel;
        IPropertyManagerPageNumberbox numSize, numAngle, numDia, numClr, numTip;
        IPropertyManagerPageCheckbox chkBlend, chkFlat, chkTangent;
        bool ok;
        List<object> edges = new List<object>();
        readonly List<IBody2> preview = new List<IBody2>();
        bool shown;
        readonly List<object> keep = new List<object>();   // group boxes and labels: SOLIDWORKS does not hold them, a GC that finalizes their wrappers stops all PropertyManager callbacks
        bool busy;                // re-entrancy guard for the preview
        IBody2 hidden;            // real body hidden during preview
        readonly Dictionary<string, bool> flips = new Dictionary<string, bool>();   // per edge (by geometry key)
        const string Hint = "Select the edges to chamfer: straight or arc edges, at any angle between the faces.\n" +
                            "Ends that run into a wall stop short by tool radius + clearance and finish with the mill's cone.\n" +
                            "Reverse direction applies to the highlighted edge.";


        public ChamferPage(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data)
        {
            this.sw = sw; this.model = model; this.feat = feat; this.data = data;
            selectTimer.Tick += SelectPending;
            p = data != null ? ChamferParams.Read(data) : Addin.Last.Clone();
            p.Flips = data != null ? p.Flips : "";
        }

        public void Show()
        {
            int err = 0;
            int opts = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage);
            page = (IPropertyManagerPage2)sw.CreatePropertyManagerPage(Addin.Title, opts, this, ref err);
            if (page == null) throw new Exception("could not create PropertyManager page (" + err + ")");
            try { page.SetTitleBitmap2(Addin.Icon("pm_title.png")); } catch { }
            Message(Hint);

            int gOpts = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);
            int cOpts = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
            short left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            short indent = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent;

            var g1 = (IPropertyManagerPageGroup)page.AddGroupBox(1, "Items To Chamfer", gOpts); keep.Add(g1);
            sel = (IPropertyManagerPageSelectionbox)g1.AddControl2(IdSel,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox, "Edges", indent, cOpts,
                "Edges to chamfer.");
            ((IPropertyManagerPageControl)sel).SetStandardPictureLabel((int)swControlBitmapLabelType_e.swBitmapLabel_SelectEdge);
            sel.SingleEntityOnly = false;
            sel.Height = 36;
            sel.Mark = 1;
            sel.SetSelectionFilters(new[] { (int)swSelectType_e.swSelEDGES });
            var flipBtn = (IPropertyManagerPageBitmapButton)g1.AddControl2(IdFlip,
                (short)swPropertyManagerPageControlType_e.swControlType_BitmapButton, FlipCaption, left, cOpts,
                "Machine the highlighted edge from its other face.");
            flipBtn.SetStandardBitmaps((int)swPropertyManagerPageBitmapButtons_e.swBitmapButtonImage_reverse_direction);
            try   // under the selection icon, left of the list (the group below is laid out from this button, so the list is kept short)
            {
                var fc = (IPropertyManagerPageControl)flipBtn; var sc = (IPropertyManagerPageControl)sel;
                fc.Left = 4; fc.Top = (short)(sc.Top + 22);
            }
            catch (Exception ex) { Core.Log("flip button place: " + ex.Message); }
            keep.Add(flipBtn);   // a control whose wrapper is collected stops responding (the button vanished)

            var g2 = (IPropertyManagerPageGroup)page.AddGroupBox(2, "Chamfer Parameters", gOpts); keep.Add(g2);
            chkTangent = (IPropertyManagerPageCheckbox)g2.AddControl2(IdTangent,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Tangent propagation", left, cOpts,
                "Also chamfer edges that continue the selection tangentially.");
            chkTangent.Checked = p.Tangent;
            numSize = Number(g2, IdSize, "Chamfer distance (on the mill's face)", p.Size, 0.00001, 1, swControlBitmapLabelType_e.swBitmapLabel_LinearDistance, cOpts, indent, left,
                          swNumberboxUnitType_e.swNumberBox_Length, "Chamfer width on the mill's face.");
            numAngle = Number(g2, IdAngle, "Chamfer angle (from the mill's face)", p.Angle, 0.0174533, 1.5533, swControlBitmapLabelType_e.swBitmapLabel_AngularDistance, cOpts, indent, left,
                              swNumberboxUnitType_e.swNumberBox_Angle, "Chamfer angle from the mill's face.");
            chkBlend = (IPropertyManagerPageCheckbox)g2.AddControl2(IdBlend,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Blend chamfers at inside corners", left, cOpts,
                "Chamfer two edges meeting at an inside corner as one path that turns around the corner.");
            chkBlend.Checked = p.Blend;
            chkFlat = (IPropertyManagerPageCheckbox)g2.AddControl2(IdFlat,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Flat corner where 3 chamfers meet", left, cOpts,
                "Finish a 3-chamfer outside corner with a small flat triangle (needs a 3D move). Off: the chamfers meet in a point.");
            chkFlat.Checked = p.Flat;

            var g3 = (IPropertyManagerPageGroup)page.AddGroupBox(3, "Chamfer Mill", gOpts); keep.Add(g3);
            numDia = Number(g3, IdDia, "Tool diameter", p.ToolDia, 0.0001, 1, swControlBitmapLabelType_e.swBitmapLabel_Diameter, cOpts, indent, left, swNumberboxUnitType_e.swNumberBox_Length, "Chamfer mill diameter.");
            numTip = Number(g3, IdTip, "Tip offset (below the chamfer's lower edge)", p.TipOffset, 0, 1, swControlBitmapLabelType_e.swBitmapLabel_LinearDistance1, cOpts, indent, left,
                            swNumberboxUnitType_e.swNumberBox_Length, "Mill tip sits this far below the chamfer's lower edge. Not available where an arc runs into a wall.");
            numClr = Number(g3, IdClr, "Clearance to wall", p.Clearance, 0, 1, swControlBitmapLabelType_e.swBitmapLabel_LinearDistance1, cOpts, indent, left, swNumberboxUnitType_e.swNumberBox_Length, "Gap kept between the mill and a wall.");

            Addin.PageOpen = true;
            page.Show2(0);
            shown = true;
            sel.SetSelectionFocus();

            // Edit definition: put the stored edges (and their reverse flags) back
            if (data != null)
            {
                object objs, types, marks, views, comps;
                data.GetSelections3(out objs, out types, out marks, out views, out comps);
                var sm = (ISelectionMgr)model.SelectionManager;
                var all = (objs as object[]) ?? new object[0];
                var mk = (marks as int[]) ?? new int[0];
                var got = new List<IEdge>();
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i] as IEdge;
                    if (e != null && (i < mk.Length ? mk[i] : 1) == 1) got.Add(e);
                }
                // Match the edges SOLIDWORKS still finds to the stored ones (by end points, then in order), so the
                // reverse flags stay with the right edge and the ones that are gone can be shown like native "Missing".
                var stored = ChamferParams.DecodeEdges(p.Edges);
                int expected = Math.Max(p.LegacyFlips ? 0 : p.Flips.Length, stored.Count);
                var used = new bool[Math.Max(expected, got.Count)];
                var slot = Enumerable.Repeat(-1, got.Count).ToArray();
                for (int j = 0; j < got.Count; j++)
                    for (int s = 0; s < stored.Count; s++)
                        if (!used[s] && SameEdge(got[j], stored[s])) { slot[j] = s; used[s] = true; break; }
                // Edges that grew or shrank (e.g. the step was deleted and the floor edge now runs the full width): pair
                // with the stored edge on the same line - parallel, stored midpoint on the edge - closest first.
                for (int j = 0; j < got.Count; j++)
                {
                    if (slot[j] >= 0) continue;
                    int best = -1; double bestD = 1e-6;
                    for (int s = 0; s < stored.Count; s++)
                    {
                        if (used[s]) continue;
                        double d = OnSameLine(got[j], stored[s]);
                        if (d < bestD) { bestD = d; best = s; }
                    }
                    if (best >= 0) { slot[j] = best; used[best] = true; }
                }
                for (int j = 0; j < got.Count; j++)
                    if (slot[j] < 0) { int s = Array.IndexOf(used, false); if (s < 0) s = j; slot[j] = s; if (s < used.Length) used[s] = true; }
                missingCount = Math.Max(0, expected - got.Count);
                missingNames.Clear(); missing.Clear();
                for (int s = 0; s < expected; s++)
                    if (!used[s]) { missingNames.Add("Edge" + (s + 1)); if (s < stored.Count) missing.Add(stored[s]); }
                loading = true;
                try
                {
                    for (int j = 0; j < got.Count; j++)
                    {
                        flips[Key(got[j])] = p.FlipAt(slot[j]);
                        var sd = (SelectData)sm.CreateSelectData();
                        sd.Mark = 1;
                        ((IEntity)got[j]).Select4(true, sd);
                    }
                }
                finally { loading = false; }
            }
            UpdatePreview();
        }

        // Edges of the edited feature that no longer exist: drawn as red dashed lines where they were (like native).
        readonly List<double[]> missing = new List<double[]>();
        readonly List<string> missingNames = new List<string>();
        int missingCount;
        bool loading;

        static bool SameEdge(IEdge e, double[] s)
        {
            var ab = Geo.Ends(e);
            var a = ab[0]; var b = ab[1];
            Func<double[], int, double> d = (q, o) => Math.Sqrt(Math.Pow(q[0] - s[o], 2) + Math.Pow(q[1] - s[o + 1], 2) + Math.Pow(q[2] - s[o + 2], 2));
            return (d(a, 0) < 1e-7 && d(b, 3) < 1e-7) || (d(a, 3) < 1e-7 && d(b, 0) < 1e-7);
        }

        // Distance from the stored segment's midpoint to the edge if both are parallel lines, else infinity
        static double OnSameLine(IEdge e, double[] s)
        {
            try
            {
                var a = (double[])((IVertex)e.GetStartVertex()).GetPoint();
                var b = (double[])((IVertex)e.GetEndVertex()).GetPoint();
                double[] u = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, w = { s[3] - s[0], s[4] - s[1], s[5] - s[2] };
                double lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]), lw = Math.Sqrt(w[0] * w[0] + w[1] * w[1] + w[2] * w[2]);
                if (lu < 1e-9 || lw < 1e-9 || Math.Abs((u[0] * w[0] + u[1] * w[1] + u[2] * w[2]) / (lu * lw)) < 0.9999) return double.MaxValue;
                var m = new[] { (s[0] + s[3]) / 2, (s[1] + s[4]) / 2, (s[2] + s[5]) / 2 };
                var q = e.GetClosestPointOn(m[0], m[1], m[2]) as double[];
                if (q == null) return double.MaxValue;
                return Math.Sqrt(Math.Pow(q[0] - m[0], 2) + Math.Pow(q[1] - m[1], 2) + Math.Pow(q[2] - m[2], 2));
            }
            catch { return double.MaxValue; }
        }

        string HintNow()
        {
            if (missingCount == 0) return Hint;
            var names = missingNames.Count > 0 ? string.Join(", ", missingNames) : missingCount + " edge(s)";
            return "Missing: " + names + (missing.Count > 0 ? " (red dashed line)" : "") +
                   " - its geometry was deleted or changed. Pick a replacement edge, or press OK to continue without it.\n\n" + Hint;
        }

        void DrawMissing(IModeler modeler)
        {
            const int red = 0x0000FF;   // COLORREF (BGR)
            foreach (var m in missing)
            {
                var a = new[] { m[0], m[1], m[2] };
                var b = new[] { m[3], m[4], m[5] };
                double len = Geo.Dist(a, b);
                if (len < 1e-7) continue;
                // like the native "missing" line: many short dashes with gaps a bit shorter than the dashes
                int n = Math.Max(8, (int)Math.Round(len / 0.0005));
                n = Math.Min(n, 60);
                double period = len / n, dash = period * 0.55;
                for (double t = 0; t < len - 1e-9; t += period)
                {
                    double t1 = Math.Min(len, t + dash);
                    Func<double, double[]> at = u => new[] { a[0] + (b[0] - a[0]) * u / len, a[1] + (b[1] - a[1]) * u / len, a[2] + (b[2] - a[2]) * u / len };
                    var wire = modeler.CreateWireBody(new[] { Geo.Segment(modeler, at(t), at(t1)) },
                        (int)swCreateWireBodyOptions_e.swCreateWireBodyByDefault) as IBody2;
                    if (wire == null) continue;
                    wire.Display3(model, red, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
                    preview.Add(wire);
                }
            }
        }

        void Message(string text)
        {
            page.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swImportantMessageBox,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, Addin.Title);
        }

        static string Key(IEdge e)
        {
            var ab = Geo.Ends(e);
            var a = ab[0]; var b = ab[1];
            Func<double, string> r = v => Math.Round(v * 1e6).ToString(CultureInfo.InvariantCulture);
            return r(a[0] + b[0]) + "," + r(a[1] + b[1]) + "," + r(a[2] + b[2]);
        }

        bool FlipOf(IEdge e) { bool f; return flips.TryGetValue(Key(e), out f) && f; }

        List<IEdge> SelectedEdges()
        {
            var list = new List<IEdge>();
            var sm = (ISelectionMgr)model.SelectionManager;
            int n = sm.GetSelectedObjectCount2(1);
            for (int i = 1; i <= n; i++)
                if (sm.GetSelectedObjectType3(i, 1) == (int)swSelectType_e.swSelEDGES)
                {
                    var e = sm.GetSelectedObject6(i, 1) as IEdge;
                    if (e != null) list.Add(e);
                }
            return list;
        }

        // The edge the Reverse checkbox applies to: the one highlighted in the list, else the last one picked.
        // sel.CurrentSelection crashes SOLIDWORKS (access violation) when called while the list is being updated
        // (list-changed callback, page close, timers). Only read it from direct user clicks (checkbox / menu item).
        int CurrentItem(int count, bool readHighlight)
        {
            int i = -1;
            if (readHighlight && count > 0)
                try { i = sel.CurrentSelection; } catch { }
            return i >= 0 && i < count ? i : count - 1;
        }

        void ToggleCurrent(bool? value)
        {
            var list = SelectedEdges();
            int item = CurrentItem(list.Count, true);
            if (item < 0) return;
            if (list.Count > 1 && value == null)   // an inside-corner blend has only one valid direction: nothing to reverse
            {
                try { var bl = Engine.FindBlends(list, p, p.Blend, p.Flat, k => FlipOf(list[k])); if (!bl[item].Perp && (bl[item].Axis != null || bl[item].End[0] || bl[item].End[1])) return; } catch { }
            }
            var key = Key(list[item]);
            flips[key] = value ?? !FlipOf(list[item]);
            UpdatePreview();
        }

        // Live preview (native style): chamfered body with yellow outline/hatch strokes on the new faces.
        bool again;               // an update was requested while one was running
        void UpdatePreview()
        {
            if (busy) { again = true; return; }
            busy = true;
            try
            {
                int n = 0;
                do { again = false; DoUpdatePreview(); } while (again && shown && ++n < 3);
            }
            finally { busy = false; }
        }

        // Like native features: OK stays enabled; pressing it with a problem shows a tip and keeps the page open.
        string problem;

        void DoUpdatePreview()
        {
            ClearPreview();
            if (!shown) return;
            try
            {
                try { DrawMissing((IModeler)sw.GetModeler()); } catch (Exception ex) { Core.Log("missing: " + ex.Message); }
                var cur = p.Clone();
                var sel = SelectedEdges();
                problem = null;
                OkCursor(false);
                if (sel.Count == 0) { Message(HintNow()); model.GraphicsRedraw2(); return; }
                var modeler = (IModeler)sw.GetModeler();
                var math = (IMathUtility)sw.GetMathUtility();
                var errors = new List<string>();

                // Same cut as the rebuild, edge by edge
                var orig = sel[0].GetBody() as IBody2;
                var result = (IBody2)orig.Copy();
                var lines = new List<double[][]>();
                var plans = new List<Engine.EdgeCut>();
                int[] srcI; var all = Engine.Expand(sel, cur, out srcI);
                var blends = Engine.FindBlends(all, cur, cur.Blend, cur.Flat, k => FlipOf(sel[srcI[k]]));
                for (int k = 0; k < all.Count; k++)
                {
                    try { plans.Add(Engine.PlanEdge(all[k], cur, FlipOf(sel[srcI[k]]), blends[k], modeler, math, lines)); }
                    catch (Exception ex) { errors.Add("Edge " + (k + 1) + ": " + ex.Message); plans.Add(null); }
                }
                for (int k = 0; k < plans.Count; k++)
                {
                    if (plans[k] == null) continue;
                    try { result = Engine.ApplyCut(result, plans[k]); }
                    catch (Exception ex) { errors.Add("Edge " + (k + 1) + ": " + ex.Message); }
                }

                // Anything wrong: no preview at all, explain why, and block OK
                if (errors.Count > 0)
                {
                    problem = string.Join("\n", errors);
                    Core.Log("preview problem: " + problem.Replace("\n", " | "));
                    Message(problem);
                    model.GraphicsRedraw2();
                    return;
                }

                var mp = model.MaterialPropertyValues as double[];
                int color = mp != null && mp.Length >= 3
                    ? (int)(mp[0] * 255) | ((int)(mp[1] * 255) << 8) | ((int)(mp[2] * 255) << 16)
                    : 0xB4B4B4;
                try { Engine.FaceLines(result, ((object[])orig.GetFaces()).Cast<IFace2>().ToList(), plans, lines); }
                catch (Exception ex) { Core.Log("face lines: " + ex.Message); }
                result.Display3(model, color, (int)swTempBodySelectOptions_e.swTempBodySelectable);
                preview.Add(result);
                orig.DisableDisplay = true;
                hidden = orig;

                const int yellow = 0x00FFFF;   // COLORREF (BGR): R=255 G=255 B=0
                foreach (var ln in lines)
                {
                    if (Geo.Dist(ln[0], ln[1]) < 1e-7) continue;
                    var wire = modeler.CreateWireBody(new[] { Geo.Segment(modeler, ln[0], ln[1]) },
                        (int)swCreateWireBodyOptions_e.swCreateWireBodyByDefault) as IBody2;
                    if (wire == null) continue;
                    wire.Display3(model, yellow, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
                    preview.Add(wire);
                }

                Message(HintNow());
                OkCursor(true);
                model.GraphicsRedraw2();
            }
            catch (Exception ex) { problem = "Preview failed: " + ex.Message; Core.Log("preview failed: " + ex); Message(problem); }
        }

        // Native-style "Rebuild Errors" balloon, pointing at where OK was clicked
        void ShowTip(string why)
        {
            try
            {
                var pt = Cursor.Position;
                sw.ShowBubbleTooltipAt2(pt.X + 4, pt.Y, (int)swArrowPosition.swArrowLeftTop, "Rebuild Errors",
                    "Could not build the CNC Chamfer:\n" + why, (int)swBitMaps.swBitMapTreeError, "", "", 0, 0, "", "");
            }
            catch { }
        }

        // Right-click in the graphics area = OK (native "OK" cursor) once there is something valid to build
        void OkCursor(bool on)
        {
            try { page.SetCursor((int)(on ? swPropertyManagerPageCursors_e.swPropertyManagerPageCursors_Okay
                                          : swPropertyManagerPageCursors_e.swPropertyManagerPageCursors_None)); } catch { }
        }

        void ClearPreview()
        {
            foreach (var b in preview) { try { b.Hide(model); } catch { } }
            preview.Clear();
            if (hidden != null) { try { hidden.DisableDisplay = false; } catch { } hidden = null; }
        }

        IPropertyManagerPageNumberbox Number(IPropertyManagerPageGroup g, int id, string label, double val, double min, double max,
                                             swControlBitmapLabelType_e pic, int cOpts, short indent, short left,
                                             swNumberboxUnitType_e unit = swNumberboxUnitType_e.swNumberBox_Length, string tip = null)
        {
            keep.Add(g.AddControl2(id + 100, (short)swPropertyManagerPageControlType_e.swControlType_Label, label, left, cOpts, ""));
            var nb = (IPropertyManagerPageNumberbox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox, label, indent, cOpts, tip ?? label);
            ((IPropertyManagerPageControl)nb).SetStandardPictureLabel((int)pic);
            bool ang = unit == swNumberboxUnitType_e.swNumberBox_Angle;
            nb.SetRange2((int)unit, min, max, true, ang ? Math.PI / 180 : 0.0005, ang ? Math.PI / 36 : 0.001, ang ? Math.PI / 1800 : 0.0001);   // exact 1 / 5 / 0.1 deg steps
            nb.Value = val;
            return nb;
        }

        public void OnClose(int Reason)
        {
            if (Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay)
            {
                string why = problem;
                if (why == null && SelectedEdges().Count == 0) why = "Select at least one edge to chamfer.";
                if (why != null)
                {
                    ShowTip(why);
                    throw new COMException("", 1);   // S_FALSE: keep the PropertyManager page open
                }
            }
            shown = false;
            ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (!ok) { ClearPreview(); return; }
            // OK: the preview stays on screen until the feature has rebuilt (AfterClose), so the old geometry never flashes.
            var list = SelectedEdges();
            edges.AddRange(list.Cast<object>());
            p.Flips = new string(list.Select(e => FlipOf(e) ? '1' : '0').ToArray());
            p.Edges = ChamferParams.EncodeEdges(list.Cast<object>());
        }

        public void AfterClose()
        {
            Addin.PageOpen = false;
            try
            {
                if (!ok || edges.Count == 0)
                {
                    ClearPreview();
                    if (data != null) data.ReleaseSelectionAccess();
                    if (ok) sw.SendMsgToUser2("CNC Chamfer: no edges selected.", (int)swMessageBoxIcon_e.swMbWarning, (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }
                var last = p.Clone(); last.Flips = "";
                Addin.Last = last;
                // Freeze the view, show the real body again, rebuild, then drop the preview and redraw once.
                IModelView view = null;
                try { view = model.ActiveView as IModelView; if (view != null) view.EnableGraphicsUpdate = false; } catch { view = null; }
                try
                {
                    if (hidden != null) { try { hidden.DisableDisplay = false; } catch { } hidden = null; }
                    if (feat == null) Insert(); else Modify();
                }
                finally
                {
                    ClearPreview();
                    if (view != null) try { view.EnableGraphicsUpdate = true; } catch { }
                    model.GraphicsRedraw2();
                }
            }
            catch (Exception e) { ClearPreview(); MessageBox.Show("CNC Chamfer:\n" + e); }
        }

        void Insert() { Inserter.Insert(sw, model, edges, p); }

        void Modify()
        {
            var objs = edges.ToArray();
            var marks = Enumerable.Repeat(1, objs.Length).ToArray();
            // COM objects must go in as DispatchWrapper[] (a plain object[] is silently ignored - same as EditBodies)
            var wrapped = objs.Select(o => new DispatchWrapper(o)).ToArray();
            data.SetSelections2(wrapped, marks, null);
            if (data.GetSelectionCount() != objs.Length)
                data.SetSelections(wrapped, marks);
            p.Write(data);
            bool okMod = feat.ModifyDefinition(data, model, null);
            var back = ChamferParams.Read((IMacroFeatureData)feat.GetDefinition());
            Core.Log(string.Format(CultureInfo.InvariantCulture, "modify: ok={0} size {1} -> {2}, dia {3}, clr {4}, flips '{5}', edges set {6}",
                okMod, p.Size, back.Size, back.ToolDia, back.Clearance, back.Flips, objs.Length));
            Inserter.ReportError(sw, feat);
        }

        // --- unused handler members -------------------------------------------------------
        public void AfterActivation() { }
        public bool OnHelp() { return true; }
        public bool OnPreviousPage() { return true; }
        public bool OnNextPage() { return true; }
        public bool OnPreview() { return true; }
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) { return true; }
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }
        public void OnCheckboxCheck(int Id, bool Checked)
        {
            if (Id == IdBlend) { p.Blend = Checked; UpdatePreview(); }
            else if (Id == IdFlat) { p.Flat = Checked; UpdatePreview(); }
            else if (Id == IdTangent) { p.Tangent = Checked; UpdatePreview(); }
        }
        public void OnOptionCheck(int Id) { }
        public void OnButtonPress(int Id) { if (Id == IdFlip) ToggleCurrent(null); }
        public void OnTextboxChanged(int Id, string Text) { }
        public void OnNumberboxChanged(int Id, double Value)
        {
            if (Id == IdSize) p.Size = Value;
            else if (Id == IdDia) p.ToolDia = Value;
            else if (Id == IdClr) p.Clearance = Value;
            else if (Id == IdTip) p.TipOffset = Value;
            else if (Id == IdAngle)
            {
                p.Angle = Math.Round(Value * 180 / Math.PI, 4) * Math.PI / 180;   // drop float noise (31.00000043 deg)
            }
            UpdatePreview();
        }
        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnComboboxSelectionChanged(int Id, int Item) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnSelectionboxFocusChanged(int Id) { }
        public void OnSelectionboxListChanged(int Id, int Count)
        {
            if (!loading && missingCount > 0) { missingCount = 0; missing.Clear(); missingNames.Clear(); }
            UpdatePreview();
        }
        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText)
        {
            // While the preview body is shown the real body is hidden, so clicks land on the preview.
            // Map a clicked preview edge back to the real edge and select that instead.
            try
            {
                var e = Selection as IEdge;
                var f = Selection as IFace2;
                var b = e != null ? e.GetBody() as IBody2 : f != null ? f.GetBody() as IBody2 : null;
                Core.Log("submit: edge="+(e!=null)+" face="+(f!=null)+" temp="+(b!=null && b.IsTemporaryBody()));
                if (b == null || !b.IsTemporaryBody()) return true;
                object real = hidden == null ? null : e != null ? (object)MapToReal(e, hidden) : MapFaceToReal(f, hidden);
                Core.Log("submit: mapped="+(real!=null));
                if (real != null) { pending.Add(Tuple.Create(real, 1)); selectTimer.Start(); }
            }
            catch (Exception ex) { Core.Log("submit failed: "+ex.Message); }
            return false;
        }

        readonly List<Tuple<object, int>> pending = new List<Tuple<object, int>>();
        readonly System.Windows.Forms.Timer selectTimer = new System.Windows.Forms.Timer { Interval = 1 };

        void SelectPending(object sender, EventArgs args)
        {
            selectTimer.Stop();
            if (!shown || pending.Count == 0) { pending.Clear(); return; }
            var items = pending.ToList(); pending.Clear();
            var sm = (ISelectionMgr)model.SelectionManager;
            foreach (var it in items)
            {
                var sd = (SelectData)sm.CreateSelectData();
                sd.Mark = it.Item2;
                ((IEntity)it.Item1).Select4(true, sd);
            }
        }

        // The real planar face a preview face lies on (same plane, overlapping)
        static IFace2 MapFaceToReal(IFace2 temp, IBody2 real)
        {
            var box = temp.GetBox() as double[];
            if (box == null) return null;
            var pt = temp.GetClosestPointOn((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2) as double[];
            if (pt == null) return null;
            foreach (IFace2 f in (object[])real.GetFaces())
            {
                var q = f.GetClosestPointOn(pt[0], pt[1], pt[2]) as double[];
                if (q == null) continue;
                double dx = q[0] - pt[0], dy = q[1] - pt[1], dz = q[2] - pt[2];
                if (dx * dx + dy * dy + dz * dz < 1e-14) return f;
            }
            return null;
        }

        // The real edge that a preview edge lies on (unchanged or trimmed edges); null for new chamfer edges
        static IEdge MapToReal(IEdge temp, IBody2 real)
        {
            // Preview wires run parallel to their real edge, offset by up to ~tool radius: take the real edge
            // closest to three points along the preview edge (all within 4 mm).
            // shortcut: parallel real edges closer than 4 mm would be ambiguous, only matters for tiny parts.
            var cp = temp.GetCurveParams3();
            var c = (ICurve)temp.GetCurve();
            var pts = new List<double[]>();
            foreach (double f in new[] { 0.25, 0.5, 0.75 })
            {
                var p = c.Evaluate2(cp.UMinValue + f * (cp.UMaxValue - cp.UMinValue), 0) as double[];
                if (p == null) return null;
                pts.Add(p);
            }
            IEdge best = null; double bd = 4e-3 * 4e-3;
            foreach (IEdge e in (object[])real.GetEdges())
            {
                double worst = 0;
                foreach (var p in pts)
                {
                    var q = e.GetClosestPointOn(p[0], p[1], p[2]) as double[];
                    if (q == null) { worst = double.MaxValue; break; }
                    double dx = q[0] - p[0], dy = q[1] - p[1], dz = q[2] - p[2];
                    worst = Math.Max(worst, dx * dx + dy * dy + dz * dz);
                }
                if (worst < bd) { bd = worst; best = e; }
            }
            if (best == null) Core.Log("MapToReal: no real edge near preview edge");
            return best;
        }        public int OnActiveXControlCreated(int Id, bool Status) { return 0; }
        public void OnSliderPositionChanged(int Id, double Value) { }
        public void OnSliderTrackingCompleted(int Id, double Value) { }
        public bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) { return false; }
        public void OnPopupMenuItem(int Id) { if (Id == IdMenuReverse) ToggleCurrent(null); }
        public void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public void OnGainedFocus(int Id) { }
        public void OnLostFocus(int Id) { }
        public int OnWindowFromHandleControlCreated(int Id, bool Status) { return 0; }
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnNumberBoxTrackingCompleted(int Id, double Value) { }
    }
}
