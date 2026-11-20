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
    // Add-in: command group, toolbar, CommandManager tab
    // ------------------------------------------------------------------------------------------
    [ComVisible(true), Guid("35EB7F02-FCBB-4389-918E-1CF435CB89A1"), ProgId("MfgChamfer.Addin")]
    public class Addin : ISwAddin
    {
        public const string Title = "CNC Chamfer";          // the feature / command
        const string AddinTitle = "Custom-Features";        // name in Tools > Add-Ins (DLL and ProgIds keep "MfgChamfer" for existing parts)
        const int GroupId = 52711;
        public static ISldWorks Sw;
        public static ChamferParams Last = new ChamferParams();
        public static ChamferParams Clip;      // settings copied with "Copy CNC Chamfer"
        ICommandManager cmdMgr;
        int cookie;
        static readonly int Part = (int)swDocumentTypes_e.swDocPART;

        // The icons are embedded in the DLL (resource "icons.<name>"); SOLIDWORKS only takes file paths, so they are copied to a per-user cache folder.
        public static string IconDir
        {
            get { return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CustomFeatures", "icons"); }
        }

        public static string Icon(string name)
        {
            var path = Path.Combine(IconDir, name);
            try
            {
                using (var s = typeof(Addin).Assembly.GetManifestResourceStream("icons." + name))
                {
                    if (s == null) return path;
                    var data = new byte[s.Length]; int n = 0;
                    while (n < data.Length) n += s.Read(data, n, data.Length - n);
                    if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(data)) { Directory.CreateDirectory(IconDir); File.WriteAllBytes(path, data); }
                }
            }
            catch { }   // a locked cache file keeps its old icon
            return path;
        }

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            try
            {
                Sw = (ISldWorks)ThisSW;
                Sw.SetAddinCallbackInfo2(0, this, Cookie);
                cookie = Cookie;
                cmdMgr = Sw.GetCommandManager(Cookie);
                AddCommands();
                swEvents = (SldWorks)ThisSW;
                swEvents.CommandOpenPreNotify += OnCommandPre;
                return true;
            }
            catch (Exception e) { MessageBox.Show("CNC Chamfer failed to load:\n" + e); return false; }
        }

        // Placement (no own toolbar/tab): a button on the native Features CommandManager tab and an item in
        // Insert > Features, right after Chamfer. The native Fillet flyout itself can't be extended by add-ins.
        // Menu: a classic menu item placed inside SOLIDWORKS' own Insert > Features submenu, right after Chamfer.
        // In the part menus that submenu is "Featu&res" (accelerator R).
        const string MenuPath = Title + "...@Featu&res@&Insert";
        // Name of the hidden toolbar that carries the command (also its category in Tools > Customize > Commands).
        // Deliberately not "CNC Chamfer" so searching "cnc" lists the command only once.
        // Shared group for all of Omri's custom features: one "Custom-Features" drop-down on the Features tab
        // (like Fillet's), one toolbar, one Customize category. Future features go into this same add-in and
        // get their own AddCommandItem2 line in AddCommands().
        // Hidden toolbar + menu that carry the command. Named differently from the command so searching "cnc"
        // finds the command itself (menu+toolbar item = own search entry with "+"), not the toolbar.
        // "&Insert\\..." puts the menu under Insert (a path title creates that submenu); without a path it
        // lands under Tools. The toolbar / Customize category shows the last part: "Custom-Features".
        const string ToolbarTitle = "&Insert\\Custom-Features";
        ICommandTab featTab;
        ICommandTabBox featBox;
        bool usedGroup = true;

        const int FlyoutId = 52712;
        const int SpacerId = 52713;
        const string SettingsKey = @"HKEY_CURRENT_USER\Software\MfgChamfer";
        public void OnSpacer() { }
        public int OnSpacerEnable() { return 0; }   // drop-down from an earlier version; removed on load

        void AddCommands()
        {
            var icons = new[] { 20, 32, 40, 64, 96, 128 }.Select(s => Icon("toolbar_" + s + ".png")).ToArray();
            const string hint = "Chamfer for CNC: stops before walls and ends with the runout a chamfer mill leaves";

            foreach (var old in new[] { "Mfg" + " Chamfer", Title })
            {
                var t = cmdMgr.GetCommandTab(Part, old);
                if (t != null) cmdMgr.RemoveCommandTab(t);
            }

            // The command (Features tab button, Shortcut Bar "+", search). It needs its own (hidden) toolbar for that;
            // no menu of its own, because a command-group menu can't go inside a native submenu.
            try { cmdMgr.RemoveFlyoutGroup(FlyoutId); } catch { }
            // SOLIDWORKS bug workaround: command search labels an add-in menu command with whatever SOLIDWORKS
            // resource string has the same number as the command ID (ID 41658 showed "%1 has been moved to %2...").
            // If our ID collides, a hidden spacer group takes up IDs so the real command lands on a free one.
            // Removing and re-creating the group in the same session gave command ID 0 (v37), so the spacer size is
            // remembered in HKCU\Software\MfgChamfer\SpacerCount and applied at startup, before the real group.
            int spacer = 0;
            string spacerInfo;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\MfgChamfer"))
                {
                    object v = k == null ? null : k.GetValue("SpacerCount");
                    spacerInfo = k == null ? "key missing" : v == null ? "value missing" : v.GetType().Name + " " + v;
                    if (v != null) spacer = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception e) { spacerInfo = "read failed: " + e.GetType().Name + " " + e.Message; }
            Core.Log("SpacerCount read: " + spacerInfo + " (user " + System.Environment.UserName + ")");
            if (spacer < 0 || spacer > 1000) spacer = 0;
            if (spacer > 0)
            {
                int e2 = 0;
                var sp = cmdMgr.CreateCommandGroup2(SpacerId, "CNC Chamfer spacer", "", "", -1, true, ref e2);
                for (int i = 0; i < spacer; i++) sp.AddCommandItem2("spacer" + i, -1, "", "", 0, "OnSpacer", "OnSpacerEnable", 100 + i, 0);
                sp.HasMenu = false; sp.HasToolbar = false;
                sp.Activate();
            }
            else { try { cmdMgr.RemoveCommandGroup2(SpacerId, false); } catch { } }

            int err = 0;
            var grp = cmdMgr.CreateCommandGroup2(GroupId, ToolbarTitle, ToolbarTitle, "Custom features", -1, true, ref err);
            grp.IconList = icons;
            grp.MainIconList = icons;
            int idx = grp.AddCommandItem2(Title, -1, hint, Title, 0, "OnInsert", "OnEnable", 1,
                (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem));
            grp.HasToolbar = true;      // required: without it the command can't go on the Features tab or get the "+"
            grp.HasMenu = true;
            grp.ShowInDocumentType = (int)swDocTemplateTypes_e.swDocTemplateTypePART;
            grp.Activate();
            int cmdId = grp.get_CommandID(idx);
            int free = cmdId > 0 ? ResourceIds.NextFree(cmdId) : -1;
            if (free > cmdId)
            {
                // Still collides: grow the spacer for the next start (takes effect after restarting SOLIDWORKS)
                try { Registry.SetValue(SettingsKey, "SpacerCount", spacer + (free - cmdId), RegistryValueKind.DWord); }
                catch (Exception e) { Core.Log("SpacerCount write failed: " + e.Message); }
                Core.Log("command id " + cmdId + " collides, next free " + free + ", spacer " + spacer + " -> " + (spacer + free - cmdId) + " (restart SOLIDWORKS)");
            }
            else Core.Log("command id " + cmdId + (free == cmdId ? " (free)" : " (not checked)") + ", spacer " + spacer);

            bool onTab = false;
            featTab = cmdMgr.GetCommandTab(Part, "Features");
            if (featTab != null)
            {
                // Remove boxes left on the Features tab by earlier sessions/versions (they carry our IDs)
                var ours = new HashSet<int> { cmdId, grp.ToolbarId };
                int removed = 0;
                try
                {
                    foreach (var o in ((object[])featTab.CommandTabBoxes()) ?? new object[0])
                    {
                        var b = (ICommandTabBox)o;
                        object ids, styles;
                        b.GetCommands(out ids, out styles);
                        var arr = ids as int[];
                        if (arr != null && arr.Any(i => ours.Contains(i))) { featTab.RemoveCommandTabBox((CommandTabBox)b); removed++; }
                    }
                }
                catch (Exception e) { Core.Log("tab cleanup: " + e.Message); }
                featBox = featTab.AddCommandTabBox();
                onTab = featBox.AddCommands(new[] { cmdId }, new[] { (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow });
                Core.Log("old boxes removed " + removed);
            }

            // No Insert-menu item: a plain menu item is a second search entry without "+", and add-ins can't
            // put a toolbar command inside the native Insert > Features submenu.
            Core.Log("commands: cmd " + cmdId + ", on Features tab " + onTab);
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (featTab != null && featBox != null) featTab.RemoveCommandTabBox((CommandTabBox)featBox);
                try { cmdMgr.RemoveFlyoutGroup(FlyoutId); } catch { }
                if (usedGroup) cmdMgr.RemoveCommandGroup2(GroupId, false);
                try { cmdMgr.RemoveCommandGroup2(SpacerId, false); } catch { }
                if (swEvents != null) swEvents.CommandOpenPreNotify -= OnCommandPre;
                Marshal.ReleaseComObject(cmdMgr);
            }
            catch { }
            cmdMgr = null;
            Sw = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        public void OnInsert()
        {
            try
            {
                var model = Sw.ActiveDoc as IModelDoc2;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART) return;
                new ChamferPage(Sw, model, null, null).Show();
            }
            catch (Exception e) { MessageBox.Show("CNC Chamfer:\n" + e); }
        }

        // ---- Ctrl+C / Ctrl+V -------------------------------------------------------------------
        // SOLIDWORKS can't put macro features on its clipboard ("This item cannot be copied"), so the add-in
        // catches the Copy / Paste commands before they run:
        //   Copy  with an CNC Chamfer (tree item or one of its faces) selected -> remember its settings
        //   Paste with only edges selected and settings remembered            -> new CNC Chamfer on those edges
        // Everything else goes to SOLIDWORKS as usual.
        SldWorks swEvents;
        const int CmdCopy = 583, CmdCut = 584, CmdPaste = 585;   // swCommands_e

        int OnCommandPre(int Command, int UserCommand)
        {
            try
            {
                if (PageOpen) return 0;
                if (Command == CmdCopy || Command == CmdCut)
                {
                    var m = ActivePart();
                    var f = m == null ? null : SelectedMfgFeature(m);
                    if (f == null) { Clip = null; return 0; }          // something else copied: let SW handle it
                    if (Command == CmdCut) return 0;
                    Clip = ChamferParams.Read((IMacroFeatureData)f.GetDefinition());
                    Clip.Flips = ""; Clip.Edges = "";
                    return 1;                                           // handled - don't run SW's copy
                }
                if (Command == CmdPaste && Clip != null)
                {
                    var m = ActivePart();
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
                    Inserter.Insert(Sw, m, edges, Clip.Clone());
                    return 1;
                }
            }
            catch (Exception e) { Core.Log("Copy/Paste: " + e); }
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

        IModelDoc2 ActivePart()
        {
            var m = Sw == null ? null : Sw.ActiveDoc as IModelDoc2;
            return m != null && m.GetType() == Part ? m : null;
        }

        // Enable callbacks run on SOLIDWORKS idle, including while a PropertyManager page is closing.
        // Keep them trivial (no selection queries) - querying the selection there crashed SOLIDWORKS.
        public static bool PageOpen;

        public int OnEnable()
        {
            try { return !PageOpen && ActivePart() != null ? 1 : 0; } catch { return 0; }
        }

        // Registration: HKLM when run as admin (RegAsm), otherwise per-user.
        [ComRegisterFunction]
        public static void Register(Type t)
        {
            string id = "{" + t.GUID.ToString().ToUpper() + "}";
            RegistryKey k;
            try { k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + id); }
            catch { k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\Addins\" + id); }
            using (k)
            {
                k.SetValue(null, 0, RegistryValueKind.DWord);
                k.SetValue("Title", AddinTitle);
                k.SetValue("Description", "Custom features for SOLIDWORKS (Insert > Custom-Features): CNC Chamfer");
                k.SetValue("Icon Path", Icon("addin_16.png"));
            }
            using (var s = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\AddInsStartup\" + id))
                s.SetValue(null, 1, RegistryValueKind.DWord);
        }

        [ComUnregisterFunction]
        public static void Unregister(Type t)
        {
            string id = "{" + t.GUID.ToString().ToUpper() + "}";
            try { Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\" + id, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\Addins\" + id, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\" + id, false); } catch { }
        }
    }
}
