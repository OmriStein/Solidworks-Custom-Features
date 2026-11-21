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
        const string AddinTitle = "Custom-Features";        // name in Tools > Add-Ins (DLL and ProgIds keep "MfgChamfer" for existing parts)
        const int GroupId = 52711;
        public static ISldWorks Sw;
        // One entry per feature: the command group, toolbar, Features-tab box and Copy / Paste hook are built from this list.
        static readonly FeatureDef[] Features = { new ChamferDef(), new Din509Def() };   // shortcut: OnInsert0..3 below, so at most 4 features; add OnInsertN to go further
        ICommandManager cmdMgr;
        int cookie;
        static readonly int Part = (int)swDocumentTypes_e.swDocPART;

        // The icons are embedded in the DLL (resource "icons.<name>"); SOLIDWORKS only takes file paths, so they are copied to a per-user cache folder.
        public static string IconDir
        {
            get { return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "CustomFeatures", "icons"); }
        }

        public static string Icon(string name, string dir = null)
        {
            if (dir == null) dir = IconDir;
            var path = Path.Combine(dir, name);
            try
            {
                using (var s = typeof(Addin).Assembly.GetManifestResourceStream("icons." + name))
                {
                    if (s == null) return path;
                    var data = new byte[s.Length]; int n = 0;
                    while (n < data.Length) n += s.Read(data, n, data.Length - n);
                    if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(data)) { Directory.CreateDirectory(dir); File.WriteAllBytes(path, data); }
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
            catch (Exception e) { MessageBox.Show("Custom-Features failed to load:\n" + e); return false; }
        }

        // Placement (no own toolbar/tab): a button on the native Features CommandManager tab and an item in
        // Insert > Features, right after Chamfer. The native Fillet flyout itself can't be extended by add-ins.
        // Menu: a classic menu item placed inside SOLIDWORKS' own Insert > Features submenu, right after Chamfer.
        // In the part menus that submenu is "Featu&res" (accelerator R).
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

            foreach (var old in new[] { "Mfg" + " Chamfer" }.Concat(Features.Select(f => f.Name)))
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
            var grp = cmdMgr.CreateCommandGroup2(GroupId, ToolbarTitle, "Custom Features", "Custom features", -1, true, ref err);
            grp.IconList = icons;
            grp.MainIconList = new[] { 20, 32, 40, 64, 96, 128 }.Select(z => Icon("custom_features_" + z + ".png")).ToArray();   // the Custom-Features logo for the group itself
            var cmdIds = new List<int>();
            for (int i = 0; i < Features.Length; i++)
                cmdIds.Add(grp.AddCommandItem2(Features[i].Name, -1, Features[i].Hint, Features[i].Name, Features[i].IconIndex, "OnInsert" + i, "OnEnable", i + 1,
                    (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem)));
            grp.HasToolbar = true;      // required: without it the command can't go on the Features tab or get the "+"
            grp.HasMenu = true;
            grp.ShowInDocumentType = (int)swDocTemplateTypes_e.swDocTemplateTypePART;
            grp.Activate();
            cmdIds = cmdIds.Select(i => grp.get_CommandID(i)).ToList();   // the IDs exist once the group is active
            int shift = 0;
            foreach (var id in cmdIds)
            {
                int free = id > 0 ? ResourceIds.NextFree(id) : -1;
                if (free > id) shift = Math.Max(shift, free - id);
            }
            if (shift > 0)
            {
                // Still collides: grow the spacer for the next start (takes effect after restarting SOLIDWORKS)
                try { Registry.SetValue(SettingsKey, "SpacerCount", spacer + shift, RegistryValueKind.DWord); }
                catch (Exception e) { Core.Log("SpacerCount write failed: " + e.Message); }
                Core.Log("command ids " + string.Join(",", cmdIds) + " collide, spacer " + spacer + " -> " + (spacer + shift) + " (restart SOLIDWORKS)");
            }
            else Core.Log("command ids " + string.Join(",", cmdIds) + ", spacer " + spacer);

            bool onTab = false;
            featTab = cmdMgr.GetCommandTab(Part, "Features");
            if (featTab != null)
            {
                // Remove boxes left on the Features tab by earlier sessions/versions (they carry our IDs)
                var ours = new HashSet<int>(cmdIds) { grp.ToolbarId };
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
                onTab = featBox.AddCommands(cmdIds.ToArray(), cmdIds.Select(i => (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow).ToArray());
                Core.Log("old boxes removed " + removed);
            }

            // No Insert-menu item: a plain menu item is a second search entry without "+", and add-ins can't
            // put a toolbar command inside the native Insert > Features submenu.
            Core.Log("commands: " + string.Join(",", cmdIds) + ", on Features tab " + onTab);
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

        void Insert(int i)
        {
            try
            {
                var model = Sw.ActiveDoc as IModelDoc2;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocPART) return;
                Features[i].Insert(Sw, model);
            }
            catch (Exception e) { MessageBox.Show(Features[i].Name + ":\n" + e); }
        }

        // SOLIDWORKS calls a command back by method name, without arguments: one method per feature
        public void OnInsert0() { Insert(0); }
        public void OnInsert1() { Insert(1); }
        public void OnInsert2() { Insert(2); }
        public void OnInsert3() { Insert(3); }

        // ---- Ctrl+C / Ctrl+V: each feature decides whether Copy / Cut / Paste is its own (see FeatureDef.OnCommandPre) ----
        SldWorks swEvents;

        int OnCommandPre(int Command, int UserCommand)
        {
            try
            {
                if (PageOpen) return 0;
                if (Command != FeatureDef.CmdCopy && Command != FeatureDef.CmdCut && Command != FeatureDef.CmdPaste) return 0;
                var m = ActivePart();
                foreach (var f in Features) { int r = f.OnCommandPre(Sw, m, Command); if (r != 0) return r; }
            }
            catch (Exception e) { Core.Log("Copy/Paste: " + e); }
            return 0;
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
                k.SetValue("Description", "Custom features for SOLIDWORKS (Insert > Custom-Features): " + string.Join(", ", Features.Select(f => f.Name)));
                // next to the DLL, readable by every user (the add-in list reads it before the add-in loads; a per-user cache would be the installing admin's)
                k.SetValue("Icon Path", Icon("custom_features_16.png", Path.GetDirectoryName(new Uri(t.Assembly.CodeBase).LocalPath)));
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
