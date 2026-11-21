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
        const string Hint = "Select the circular edge where the shaft meets the shoulder (the inner corner), then the undercut form and size.\n" +
                            "Sizes listed fit the shaft diameter (DIN 509, Table 1).";
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
        bool ok, loading;

        public Din509Page(ISldWorks sw, IModelDoc2 model, IFeature feat, IMacroFeatureData data)
        {
            this.sw = sw; this.model = model; this.feat = feat; this.data = data;
            p = data != null ? Din509Params.Read(data) : Din509Def.Last;
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
                double d1 = 0;
                if (edge != null)
                    try { d1 = 2000 * Din509Engine.ShaftRadius(edge); Message(Hint); }
                    catch (Exception ex) { Message(ex.Message); edge = null; }
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

        public override void OnSelectionboxListChanged(int Id, int Count) { if (!loading) Refill(); }

        public override void OnComboboxSelectionChanged(int Id, int Item)
        {
            if (loading) return;
            if (Id == IdForm) { p.Form = "EFGH"[Math.Max(0, Item)].ToString(); Refill(); }
            else if (Id == IdSize && Item >= 0 && Item < rows.Count) { cur = rows[Item]; p.R = cur.R / 1000; p.T1 = cur.T1 / 1000; }
        }

        public override void OnClose(int Reason)
        {
            ok = Reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;
            if (!ok) return;
            edge = PickedEdge();
            if (edge == null || cur == null)
            {
                try { Message("Select the circular shoulder edge first."); } catch { }
                ok = false;
                throw new COMException("", 1);   // S_FALSE: keep the page open
            }
        }

        public override void AfterClose()
        {
            Addin.PageOpen = false;
            try
            {
                if (!ok) { if (data != null) data.ReleaseSelectionAccess(); return; }
                var np = Din509Params.From(cur);
                Din509Def.Last = np;
                if (feat == null) Din509Def.InsertFeature(sw, model, edge, np);
                else
                {
                    var objs = new object[] { edge };
                    var wrapped = objs.Select(o => new DispatchWrapper(o)).ToArray();
                    data.SetSelections2(wrapped, new[] { 1 }, null);
                    np.Write(data);
                    feat.ModifyDefinition(data, model, null);
                }
                model.GraphicsRedraw2();
            }
            catch (Exception e) { MessageBox.Show(Din509Def.Title + ":\n" + e); }
        }
    }
}