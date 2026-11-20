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
    // Base of the PropertyManager page handlers: page creation, the keep list and the handler members a page does not need.
    // A page overrides only what it uses. Group boxes, labels and bitmap buttons must be added to `keep` (or created through
    // Number): SOLIDWORKS does not hold them, and a GC that finalizes their COM wrappers stops all page callbacks.
    [ComVisible(true)]
    public abstract class FeaturePage : IPropertyManagerPage2Handler9
    {
        protected IPropertyManagerPage2 page;
        protected readonly List<object> keep = new List<object>();
        string pageTitle;

        protected void CreatePage(ISldWorks sw, string title, string hint)
        {
            int err = 0;
            int opts = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                             swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage);
            pageTitle = title;
            page = (IPropertyManagerPage2)sw.CreatePropertyManagerPage(title, opts, this, ref err);
            if (page == null) throw new Exception("could not create PropertyManager page (" + err + ")");
            try { page.SetTitleBitmap2(Addin.Icon("pm_title.png")); } catch { }
            Message(hint);
        }

        protected void Message(string text)
        {
            page.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swImportantMessageBox,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, pageTitle);
        }

        protected IPropertyManagerPageNumberbox Number(IPropertyManagerPageGroup g, int id, string label, double val, double min, double max,
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

        // --- handler members (all optional) -----------------------------------------------
        public virtual void OnClose(int Reason) { }
        public virtual void AfterClose() { }
        public virtual void AfterActivation() { }
        public virtual bool OnHelp() { return true; }
        public virtual bool OnPreviousPage() { return true; }
        public virtual bool OnNextPage() { return true; }
        public virtual bool OnPreview() { return true; }
        public virtual void OnWhatsNew() { }
        public virtual void OnUndo() { }
        public virtual void OnRedo() { }
        public virtual bool OnTabClicked(int Id) { return true; }
        public virtual void OnGroupExpand(int Id, bool Expanded) { }
        public virtual void OnGroupCheck(int Id, bool Checked) { }
        public virtual void OnCheckboxCheck(int Id, bool Checked) { }
        public virtual void OnOptionCheck(int Id) { }
        public virtual void OnButtonPress(int Id) { }
        public virtual void OnTextboxChanged(int Id, string Text) { }
        public virtual void OnNumberboxChanged(int Id, double Value) { }
        public virtual void OnComboboxEditChanged(int Id, string Text) { }
        public virtual void OnComboboxSelectionChanged(int Id, int Item) { }
        public virtual void OnListboxSelectionChanged(int Id, int Item) { }
        public virtual void OnSelectionboxFocusChanged(int Id) { }
        public virtual void OnSelectionboxListChanged(int Id, int Count) { }
        public virtual void OnSelectionboxCalloutCreated(int Id) { }
        public virtual void OnSelectionboxCalloutDestroyed(int Id) { }
        public virtual bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText) { return true; }
        public virtual void OnSliderPositionChanged(int Id, double Value) { }
        public virtual void OnSliderTrackingCompleted(int Id, double Value) { }
        public virtual bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) { return false; }
        public virtual void OnPopupMenuItem(int Id) { }
        public virtual void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public virtual void OnGainedFocus(int Id) { }
        public virtual void OnLostFocus(int Id) { }
        public virtual int OnActiveXControlCreated(int Id, bool Status) { return 0; }
        public virtual int OnWindowFromHandleControlCreated(int Id, bool Status) { return 0; }
        public virtual void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public virtual void OnNumberBoxTrackingCompleted(int Id, double Value) { }
    }
}
