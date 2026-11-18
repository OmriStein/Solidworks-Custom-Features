// CNC Chamfer - SOLIDWORKS add-in that adds a "manufacturable chamfer" feature.
//
// A 45 deg chamfer on straight 90 deg edges. Where an edge runs into a wall, the chamfer stops
// (tool radius + clearance) short of the wall and ends in the conical runout a 90 deg chamfer mill
// leaves, instead of the sharp dead-end corner the standard Chamfer makes.
//
// Build: build.bat     Register: register.bat (Administrator)     Unregister: register.bat u
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

[assembly: System.Reflection.AssemblyTitle("Custom-Features")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace MfgChamfer
{
    // ------------------------------------------------------------------------------------------
    // Parameters (stored on the feature, SI units)
    // ------------------------------------------------------------------------------------------
    public class ChamferParams
    {
        public double Size = 0.001;        // chamfer leg length
        public double ToolDia = 0.006;     // chamfer mill body diameter
        public double Clearance = 0.0005;  // extra gap between tool and wall
        public string Flips = "";          // per edge, in selection order: '1' = machine from the other face (also = edge count)
        public bool LegacyFlips;           // very old feature: one flag for all edges, no per-edge count
        public string Edges = "";          // end points of each edge at the last OK (m): "x0,y0,z0,x1,y1,z1;..." - to show missing ones
        public bool Blend = true;          // blend two perpendicular chamfers around an inside corner (old features: off)
        public bool Tangent;               // tangent propagation: the edges continuing the selected ones tangentially are chamfered too (not selected)
        public bool Flat = false;          // flat triangle where three chamfers meet at an outside corner (like native Chamfer)
        public double Angle = Math.PI / 4; // chamfer angle from the face the mill comes from (rad); a 3-axis mill then has 180 deg - 2 x this
        

        public bool FlipAt(int i) { return i < Flips.Length && Flips[i] == '1'; }

        public static readonly string[] Names = { "Size", "ToolDia", "Clearance", "Flips", "Blend", "Corner", "Edges", "Angle", "Tangent" };
        public static readonly int[] Types =
        {
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString
        };

        public string[] Values()
        {
            var c = CultureInfo.InvariantCulture;
            return new[] { Size.ToString("R", c), ToolDia.ToString("R", c), Clearance.ToString("R", c), Flips ?? "", Blend ? "1" : "0", Flat ? "1" : "0", Edges ?? "", Angle.ToString("R", c), Tangent ? "1" : "0" };
        }

        // Replaces the whole parameter set (also upgrades features made with older versions)
        public void Write(IMacroFeatureData d)
        {
            // Set by name (SetParameters alone was not picked up by ModifyDefinition).
            // Features from older versions have no "Flips" parameter yet: then redefine the whole set.
            string probe, probe2, probe3, probe4, probe5;
            d.GetStringByName("Flips", out probe);
            d.GetStringByName("Blend", out probe2);
            d.GetStringByName("Corner", out probe3);
            d.GetStringByName("Edges", out probe4);
            d.GetStringByName("Tangent", out probe5);
            double probeA; d.GetDoubleByName("Angle", out probeA);
            if (probe == null || probe2 == null || probe3 == null || probe4 == null || probe5 == null || probeA <= 0) d.SetParameters(Names, Types, Values());
            d.SetDoubleByName("Size", Size);
            d.SetDoubleByName("ToolDia", ToolDia);
            d.SetDoubleByName("Clearance", Clearance);
            d.SetStringByName("Flips", Flips ?? "");
            d.SetStringByName("Blend", Blend ? "1" : "0");
            d.SetStringByName("Corner", Flat ? "1" : "0");
            d.SetStringByName("Edges", Edges ?? "");
            d.SetDoubleByName("Angle", Angle);
            d.SetStringByName("Tangent", Tangent ? "1" : "0");
        }

        public static ChamferParams Read(IMacroFeatureData d)
        {
            var p = new ChamferParams();
            double v; int i; string f;
            d.GetDoubleByName("Size", out v); if (v > 0) p.Size = v;
            d.GetDoubleByName("ToolDia", out v); if (v > 0) p.ToolDia = v;
            d.GetDoubleByName("Clearance", out v); if (v >= 0) p.Clearance = v;
            d.GetStringByName("Flips", out f);
            if (!string.IsNullOrEmpty(f)) p.Flips = f;
            else { d.GetIntegerByName("Flip", out i); if (i != 0) { p.Flips = new string('1', 64); p.LegacyFlips = true; } }   // older versions: one flag for all
            string bl; d.GetStringByName("Blend", out bl); p.Blend = bl == "1";   // missing (older features) = off
            string co; d.GetStringByName("Corner", out co); p.Flat = co == "1";
            string ed; d.GetStringByName("Edges", out ed); p.Edges = ed ?? "";
            string tg; d.GetStringByName("Tangent", out tg); p.Tangent = tg == "1";
            d.GetDoubleByName("Angle", out v); if (v > 0.01 && v < 1.56) p.Angle = v;   // missing (older features) = 45 deg
            return p;
        }

        public ChamferParams Clone() { return (ChamferParams)MemberwiseClone(); }

        public static string EncodeEdges(IEnumerable<object> edges)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(";", edges.Select(o =>
            {
                var ab = Engine.Ends((IEdge)o);
                return string.Join(",", ab[0].Take(3).Concat(ab[1].Take(3)).Select(v => v.ToString("R", c)));
            }));
        }

        public static List<double[]> DecodeEdges(string s)
        {
            var res = new List<double[]>();
            foreach (var part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var v = part.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length == 6) res.Add(v);
            }
            return res;
        }
    }

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

        public static string IconDir
        {
            get { return Path.Combine(Path.GetDirectoryName(typeof(Addin).Assembly.Location), "icons"); }
        }

        public static string Icon(string name) { return Path.Combine(IconDir, name); }

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
            Engine.Log("SpacerCount read: " + spacerInfo + " (user " + System.Environment.UserName + ")");
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
                catch (Exception e) { Engine.Log("SpacerCount write failed: " + e.Message); }
                Engine.Log("command id " + cmdId + " collides, next free " + free + ", spacer " + spacer + " -> " + (spacer + free - cmdId) + " (restart SOLIDWORKS)");
            }
            else Engine.Log("command id " + cmdId + (free == cmdId ? " (free)" : " (not checked)") + ", spacer " + spacer);

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
                catch (Exception e) { Engine.Log("tab cleanup: " + e.Message); }
                featBox = featTab.AddCommandTabBox();
                onTab = featBox.AddCommands(new[] { cmdId }, new[] { (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow });
                Engine.Log("old boxes removed " + removed);
            }

            // No Insert-menu item: a plain menu item is a second search entry without "+", and add-ins can't
            // put a toolbar command inside the native Insert > Features submenu.
            Engine.Log("commands: cmd " + cmdId + ", on Features tab " + onTab);
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
            catch (Exception e) { Engine.Log("Copy/Paste: " + e); }
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
            string asmDir = Path.GetDirectoryName(t.Assembly.Location);
            RegistryKey k;
            try { k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\SolidWorks\Addins\" + id); }
            catch { k = Registry.CurrentUser.CreateSubKey(@"Software\SolidWorks\Addins\" + id); }
            using (k)
            {
                k.SetValue(null, 0, RegistryValueKind.DWord);
                k.SetValue("Title", AddinTitle);
                k.SetValue("Description", "Custom features for SOLIDWORKS (Insert > Custom-Features): CNC Chamfer");
                k.SetValue("Icon Path", Path.Combine(asmDir, @"icons\addin_16.png"));
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

    // ------------------------------------------------------------------------------------------
    // Macro feature handler (lives in the FeatureManager tree)
    // ------------------------------------------------------------------------------------------
    [ComVisible(true), Guid("5AC68A5E-93E8-44F3-A45D-EBF2DBD49154"), ProgId(MacroFeature.ProgIdName)]
    public class MacroFeature : ISwComFeature
    {
        public const string ProgIdName = "MfgChamfer.Feature";

        public static string[] Icons()
        {
            // regular / suppressed / highlighted for each size (SOLIDWORKS picks by DPI)
            var list = new List<string>();
            foreach (var s in new[] { 20, 32, 40 })
                foreach (var st in new[] { "reg", "sup", "hl" })
                    list.Add(Addin.Icon("feat_" + st + "_" + s + ".png"));
            return list.ToArray();
        }

        public object Edit(object app, object modelDoc, object feature)
        {
            try
            {
                var model = (IModelDoc2)modelDoc;
                var feat = (IFeature)feature;
                var data = (IMacroFeatureData)feat.GetDefinition();
                if (!data.AccessSelections(model, null)) return false;
                new ChamferPage((ISldWorks)app, model, feat, data).Show();
                return true;
            }
            catch (Exception e) { MessageBox.Show("CNC Chamfer:\n" + e); return false; }
        }

        public object Regenerate(object app, object modelDoc, object feature)
        {
            try { return Engine.Build((ISldWorks)app, (IFeature)feature); }
            catch (Exception e) { Engine.Log(e.ToString()); return "CNC Chamfer: " + e.Message; }
        }

        public object Security(object app, object modelDoc, object feature)
        {
            return (int)swMacroFeatureSecurityOptions_e.swMacroFeatureSecurityByDefault;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Which numbers are used by SOLIDWORKS resource strings (see the command-search workaround)
    // ------------------------------------------------------------------------------------------
    static class ResourceIds
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int LoadString(IntPtr h, uint id, System.Text.StringBuilder sb, int max);
        static List<IntPtr> dlls;

        static bool Used(int id)
        {
            if (dlls == null)
            {
                dlls = new List<IntPtr>();
                var exeDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
                var lang = Path.Combine(exeDir, "lang", "english");
                try { if (Addin.Sw != null) { var l = Addin.Sw.GetCurrentLanguage(); if (Directory.Exists(Path.Combine(exeDir, "lang", l))) lang = Path.Combine(exeDir, "lang", l); } } catch { }
                if (Directory.Exists(lang))
                    foreach (var f in Directory.GetFiles(lang, "*.dll"))
                    {
                        var h = LoadLibraryEx(f, IntPtr.Zero, 2);   // LOAD_LIBRARY_AS_DATAFILE
                        if (h != IntPtr.Zero) dlls.Add(h);
                    }
            }
            var sb = new System.Text.StringBuilder(8);
            foreach (var h in dlls) if (LoadString(h, (uint)id, sb, 8) > 0) return true;
            return false;
        }

        // First ID >= id that no resource string uses (-1 if none within 2000)
        public static int NextFree(int id)
        {
            try { for (int i = id; i < id + 2000; i++) if (!Used(i)) return i; } catch (Exception e) { Engine.Log("ResourceIds: " + e.Message); }
            return -1;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------------------------------
    public static partial class Engine
    {
        const double Tol = 1e-7;

        public static object Build(ISldWorks sw, IFeature feat)
        {
            var data = (IMacroFeatureData)feat.GetDefinition();
            var p = ChamferParams.Read(data);

            object objs, types, marks, views, comps;
            data.GetSelections3(out objs, out types, out marks, out views, out comps);
            var all = (objs as object[]) ?? new object[0];
            var mk = (marks as int[]) ?? new int[0];
            var edges = new List<IEdge>();
            for (int i = 0; i < all.Length; i++)
                if ((i < mk.Length ? mk[i] : 1) == 1 && all[i] is IEdge) edges.Add((IEdge)all[i]);
            if (edges.Count == 0) throw new Exception("no edges (references missing?)");
            // Like a native chamfer: an edge that disappeared (e.g. its feature was deleted) is an error, not silently skipped.
            // Flips holds one flag per edge selected at the last OK, so its length is the expected edge count.
            if (!p.LegacyFlips && p.Flips.Length > edges.Count)
                throw new Exception(string.Format("{0} of {1} edges missing (deleted or changed geometry) - edit the feature to reselect",
                    p.Flips.Length - edges.Count, p.Flips.Length));

            IBody2 src = null;
            object eb = data.EditBodies;
            var arr = eb as object[];
            if (arr != null && arr.Length > 0) src = arr[0] as IBody2;
            if (src == null) src = eb as IBody2;
            if (src == null) src = data.EditBody as IBody2;
            if (src == null) src = edges[0].GetBody() as IBody2;
            Log("EditBodies=" + (eb == null ? "null" : eb.GetType().FullName) + " src=" + (src != null));
            if (src == null) throw new Exception("no body to modify");
            // Keep the IDs of untouched faces: only the new chamfer/runout faces then belong to this feature,
            // so selecting it highlights just those (like native features), not the whole body.
            // Work on the edit body itself (SOLIDWORKS hands the macro feature its own copy). Copying it again
            // drops face identities, which made every face count as created by this feature.
            var work = src;
            var pristine = ((object[])((IBody2)src.Copy()).GetFaces()).Cast<IFace2>().ToList();   // for "which faces are new"

            var modeler = (IModeler)sw.GetModeler();
            var math = (IMathUtility)sw.GetMathUtility();

            int[] srcI; edges = Expand(edges, p, out srcI);   // tangent propagation: unselected tangent neighbours join in
            // Plan every edge on the untouched body first (edges stay valid), then cut.
            var plans = new List<EdgeCut>();
            var blends = FindBlends(edges, p, p.Blend, p.Flat, i => p.FlipAt(srcI[i]));
            for (int i = 0; i < edges.Count; i++)
            {
                try { plans.Add(PlanEdge(edges[i], p, p.FlipAt(srcI[i]), blends[i], modeler, math, null)); }
                catch (Exception ex) { throw new Exception("edge " + (i + 1) + ": " + ex.Message); }
            }
            for (int i = 0; i < plans.Count; i++)
            {
                try { work = ApplyCut(work, plans[i]); }
                catch (Exception ex) { throw new Exception("edge " + (i + 1) + ": " + ex.Message); }
            }

            // Face ownership: the new chamfer / runout faces belong to this feature (highlighted when it is
            // selected); faces that were already there keep their original feature.
            object faces, edgesNeed;
            data.GetEntitiesNeedUserId(work, out faces, out edgesNeed);
            var need = ((faces as object[]) ?? new object[0]).Cast<IFace2>().ToList();
            var fresh = ((object[])work.GetFaces()).Cast<IFace2>().Where(f => IsNewFace(f, pristine)).ToList();
            Log(string.Format("edges {0}, result faces {1}, need ids {2}, new faces {3}",
                edges.Count, ((object[])work.GetFaces()).Length, need.Count, fresh.Count));
            int id = 0;
            foreach (var f in need) data.SetFaceUserId((Face2)f, id++, 0);
            foreach (var f in fresh) if (!need.Any(n => n.IsSame(f))) data.SetFaceUserId((Face2)f, id++, 0);
            id = 0;
            foreach (var e in (edgesNeed as object[]) ?? new object[0]) data.SetEdgeUserId((Edge)e, id++, 0);
            return work;
        }

        // The selected edges plus, with tangent propagation, the edges that continue them tangentially; src[i] = the selected edge each one follows
        public static List<IEdge> Expand(List<IEdge> sel, ChamferParams p, out int[] src)
        {
            var all = new List<IEdge>(sel); var s = Enumerable.Range(0, sel.Count).ToList();
            if (p.Tangent)
                for (int i = 0; i < sel.Count; i++)
                    foreach (var n in TangentChain(sel[i]))
                        if (!all.Any(x => SameEdge(x, n))) { all.Add(n); s.Add(i); }
            src = s.ToArray();
            return all;
        }

        // Cut one edge's chamfer from body. Refuses if more material is removed than the chamfer and its runout
        // can account for - i.e. the tool would cut into a wall or other neighbouring geometry.
        public class EdgeCut { public List<IBody2> Tools; public double Expected; public double[] Normal, Dir; }   // Normal/Dir: chamfer plane normal and edge direction (preview hatch)

        public static EdgeCut PlanEdge(IEdge edge, ChamferParams p, bool flip, BlendInfo blend, IModeler modeler, IMathUtility math,
                                       List<double[][]> lines)
        {
            var expected = new double[1];
            var cut = new EdgeCut();
            cut.Tools = Tools(edge, p, modeler, math, flip, lines, expected, blend, cut).ToList();
            cut.Expected = expected[0];
            return cut;
        }

        // Blend: two selected perpendicular edges that meet at a vertex, share a face C, and whose other faces
        // block each other (inside corner on C, e.g. a step wall meeting the floor). The chamfer mill comes in
        // normal to C and turns around the corner: both chamfers run right up to the vertex and a full cone there
        // (axis normal to C, apex at chamfer depth) rounds the chamfer line on C with radius = chamfer size.
        public class Corner { public double[] V, DirA, DirB; public double Gamma { get { return Math.Acos(Math.Max(-1, Math.Min(1, Dot(DirA, DirB)))); } } }   // Gamma: angle between the two edges   // DirA/DirB: from V into the material, along each edge's line
        public class BlendInfo
        {
            public bool[] End = new bool[2];                     // start / end vertex is a blended corner
            public double[] ConeWall;                            // normal of the wall the blended rim runs into: a mill working from that wall is blocked by the floor instead
            public double[][] ConeAxis = new double[2][];
            public double[][] ConeDown = new double[2][];   // the way into the material below the rim's floor at that end (clips the corner cone to a quarter)        // arc ends: axis (into the material) of the corner cone, normal to the plane of both edges at the vertex
            public bool[] ConeEnd = new bool[2];                 // the end is flush at the vertex but still closes with the mill's own runout cone there
            public bool[] Join = new bool[2];                    // start / end vertex continues tangentially into another selected edge
            public double[] Axis;                                // tool axis (into the material) forced by the shared face
            public List<Corner> Corners = new List<Corner>();    // corner cones owned by this edge
            public List<double[][]> Flats = new List<double[][]>(); // flat outside corners owned by this edge: {V, n1, n2, n3}
        }

        public static BlendInfo[] FindBlends(IList<IEdge> edges, ChamferParams p, bool enabled, bool flat, Func<int, bool> flipAt)
        {
            var res = edges.Select(e => new BlendInfo()).ToArray();
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++)
                {
                    try
                    {
                        IEdge ei = edges[i], ej = edges[j];
                        bool li = ((ICurve)ei.GetCurve()).IsLine(), lj = ((ICurve)ej.GetCurve()).IsLine();   // an arc may take the place of a line
                        if ((!li && !((ICurve)ei.GetCurve()).IsCircle()) || (!lj && !((ICurve)ej.GetCurve()).IsCircle())) continue;
                        var pi = Ends(ei);
                        var pj = Ends(ej);
                        int ai = -1, aj = -1;
                        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) if (Dist(pi[a], pj[b]) < 1e-8) { ai = a; aj = b; }
                        if (ai < 0) continue;
                        var V = pi[ai];
                        Func<IEdge, double[][], int, bool, double[]> away = (e, pe, a, line) =>   // direction leaving V along the edge (an arc: its tangent, towards the other end)
                        {
                            var chord = Sub(pe[1 - a], pe[a]);
                            if (line) return Norm(chord);
                            var tg = TangentAt(e, pe[a]);
                            return Dot(tg, chord) < 0 ? Scale(tg, -1) : tg;
                        };
                        var ti = away(ei, pi, ai, li);
                        var tj = away(ej, pj, aj, lj);
                        if (!enabled || Math.Abs(Dot(ti, tj)) > 0.9999) continue;   // blending off / edges in line
                        var fi = ((object[])ei.GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
                        var fj = ((object[])ej.GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
                        int ci = -1, cj = -1;
                        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) if (fi[a].IsSame(fj[b])) { ci = a; cj = b; }
                        Func<IFace2, double[]> wallN = f => ((ISurface)f.GetSurface()).IsPlane() ? PlaneNormal(f) : FaceNormalAt(f, V);
                        if (ci >= 0 && !li && !lj) continue;   // two arcs only meet in the perpendicular (no shared face) case
                        if (ci < 0)
                        {
                            // No shared face: a rim edge (line or arc) whose face C is square to a straight edge rising from the
                            // same vertex (an arc rim of a floor running into a boss wall, with the boss's vertical edge standing on it).
                            for (int s = 0; s < 2; s++)
                            {
                                int a = s == 0 ? i : j, b = s == 0 ? j : i;
                                if (li != lj && !(b == i ? li : lj)) continue;   // an arc meeting a straight edge: the straight edge is the one rising from the arc's face (never the reverse, which would give the arc a second, vertical cone)
                                var fa = a == i ? fi : fj; var fb = b == i ? fi : fj;
                                var tA = a == i ? ti : tj; var tB = b == i ? ti : tj;
                                int endA = a == i ? ai : aj, endB = b == i ? ai : aj;
                                for (int k = 0; k < 2; k++)
                                {
                                    if (!((ISurface)fa[k].GetSurface()).IsPlane()) continue;
                                    var nCp = PlaneNormal(fa[k]);
                                    if (Dot(tB, nCp) < 0.9) continue;                  // the edge rises from C, square or leaning (drafted wall, up to ~25 deg)
                                    var nAW = wallN(fa[1 - k]);
                                    if (nAW == null || Math.Abs(Dot(nCp, nAW)) > 0.02) continue;   // the rim is a 90 deg edge
                                    if (!fb.Any(f => { var nW = wallN(f); return nW != null && Math.Abs(Dot(nCp, nW)) < 0.02 && Dot(nW, tA) > 0.02; })) continue;   // the rim runs into one of its walls
                                    var axc = Scale(nCp, -1);
                                    if (res[a].Axis != null && Dot(res[a].Axis, axc) < 0.99) continue;
                                    res[a].Axis = axc; res[a].End[endA] = true; res[b].End[endB] = true; res[b].ConeDown[endB] = axc;
                                    // the corner cone is the mill that works from the plane both edges leave V in (B arc or leaning line)
                                    {
                                        var cx = Cross(nCp, tA);   // square to the rim and in the floor plane: with a leaning B this stays horizontal like the wall's own corner cut
                                        var uC = Len(cx) > 1e-6 ? IntoFace(fa[k], nCp, tA, V, 1e-4) : null;
                                        if (uC != null) { cx = Norm(cx); res[b].ConeAxis[endB] = Dot(cx, uC) >= 0 ? cx : Scale(cx, -1); }
                                    }
                                    // the straight edge's mill stops with its centre at V: its cone (axis square to the wall) closes the chamfer there
                                    res[b].ConeEnd[endB] = true; res[b].ConeWall = fb.Select(wallN).FirstOrDefault(nW => nW != null && Math.Abs(Dot(nCp, nW)) < 0.02 && Dot(nW, tA) > 0.02);
                                    break;
                                }
                            }
                            continue;
                        }
                        var nC = PlaneNormal(fi[ci]);
                        var nA = wallN(fi[1 - ci]);
                        var nB = wallN(fj[1 - cj]);
                        if (nA == null || nB == null) continue;
                        if (Math.Abs(Dot(nC, nA)) > 0.02 || Math.Abs(Dot(nC, nB)) > 0.02) continue;   // both edges must be 90 deg
                        if (Dot(nA, tj) < 0.02 || Dot(nB, ti) < 0.02) continue;   // not an inside corner on C (each edge leaves V on the air side of the other's wall)
                        var axis = Scale(nC, -1);
                        if ((res[i].Axis != null && Dot(res[i].Axis, axis) < 0.99) || (res[j].Axis != null && Dot(res[j].Axis, axis) < 0.99))
                            continue;                                       // already blended on a different face
                        res[i].Axis = axis; res[j].Axis = axis;
                        res[i].End[ai] = true; res[j].End[aj] = true;
                        (li ? res[i] : res[j]).Corners.Add(new Corner { V = V, DirA = Scale(ti, -1), DirB = Scale(tj, -1) });   // a straight edge owns the cone
                    }
                    catch (Exception ex) { Log("blend search "+i+","+j+": "+ex.Message); }
                }
            FindJoins(edges, res);
            if (flat) FindFlatCorners(edges, p, flipAt, res);
            return res;
        }

        // Flat corner (native Chamfer style): three selected, mutually perpendicular edges meeting at an outside
        // corner V. The three chamfers alone meet in a point; this adds a cut by the plane through the three points
        // where the chamfer lines meet on each face: (p - V)·(n1 + n2 + n3) = -2d.
        // Legs of the chamfer an edge gets (same mill-face choice as Tools): dF on the mill's face fF, dG on the other.
        static void Legs(IEdge e, ChamferParams p, bool flip, out IFace2 fF, out double dF, out double dG)
        {
            var adj = (object[])e.GetTwoAdjacentFaces2();
            var f1 = (IFace2)adj[0]; var f2 = (IFace2)adj[1];
            var n1 = PlaneNormal(f1); var n2 = PlaneNormal(f2);
            bool fromF1;
            if (Math.Abs(n1[2] - n2[2]) > 0.3) fromF1 = n1[2] > n2[2]; else fromF1 = f1.GetArea() >= f2.GetArea();
            if (flip) fromF1 = !fromF1;
            fF = fromF1 ? f1 : f2;
            double phi = Math.PI - Math.Acos(Math.Max(-1, Math.Min(1, Dot(n1, n2))));
            dF = p.Size; dG = dF * Math.Sin(p.Angle) / Math.Sin(phi + p.Angle);
        }

        // Three selected edges meeting at an outside corner V (any angles): where the chamfer lines of two edges meet on
        // a face is T = V + (lb e_a + la e_b) / sin(delta); the corner is then cut flat by the plane through the three T.
        static void FindFlatCorners(IList<IEdge> edges, ChamferParams p, Func<int, bool> flipAt, BlendInfo[] res)
        {
            var ends = new List<Tuple<int, int, double[]>>();
            for (int i = 0; i < edges.Count; i++)
            {
                try
                {
                    if (!((ICurve)edges[i].GetCurve()).IsLine()) continue;
                    ends.Add(Tuple.Create(i, 0, (double[])((IVertex)edges[i].GetStartVertex()).GetPoint()));
                    ends.Add(Tuple.Create(i, 1, (double[])((IVertex)edges[i].GetEndVertex()).GetPoint()));
                }
                catch { }
            }
            var done = new List<double[]>();
            foreach (var e0 in ends)
            {
                try
                {
                    var V = e0.Item3;
                    if (done.Any(q => Dist(q, V) < 1e-8)) continue;
                    done.Add(V);
                    var at = ends.Where(e => Dist(e.Item3, V) < 1e-8).ToList();
                    if (at.Count != 3 || at.Select(e => e.Item1).Distinct().Count() != 3) continue;
                    if (at.Any(e => res[e.Item1].End[e.Item2])) continue;           // blended there
                    var t = at.Select(e =>
                    {
                        var ed = edges[e.Item1];
                        var other = (double[])((IVertex)(e.Item2 == 0 ? ed.GetEndVertex() : ed.GetStartVertex())).GetPoint();
                        return Norm(Sub(other, V));
                    }).ToArray();
                    var faces = new List<IFace2>();
                    foreach (var e in at)
                        foreach (IFace2 f in (object[])edges[e.Item1].GetTwoAdjacentFaces2())
                            if (!faces.Any(g => g.IsSame(f))) faces.Add(f);
                    if (faces.Count != 3) continue;
                    var n = faces.Select(PlaneNormal).ToArray();
                    // outside corner: each edge runs into the material side of the face it does not touch
                    bool convex = true;
                    for (int k = 0; k < 3 && convex; k++)
                    {
                        var adj = ((object[])edges[at[k].Item1].GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
                        for (int m = 0; m < 3; m++)
                            if (!adj.Any(a => a.IsSame(faces[m])) && Dot(t[k], n[m]) > -0.02) convex = false;
                    }
                    if (!convex) continue;
                    // leg of every edge's chamfer on every face it touches
                    var legOn = new double[3, 3];
                    for (int k = 0; k < 3; k++)
                    {
                        IFace2 fF; double dF, dG;
                        int idx = at[k].Item1;
                        Legs(edges[idx], p, flipAt(idx), out fF, out dF, out dG);
                        for (int m = 0; m < 3; m++) legOn[k, m] = fF.IsSame(faces[m]) ? dF : dG;
                    }
                    var T = new double[3][];
                    for (int m = 0; m < 3; m++)
                    {
                        var on = Enumerable.Range(0, 3).Where(k => ((object[])edges[at[k].Item1].GetTwoAdjacentFaces2()).Cast<IFace2>().Any(a => a.IsSame(faces[m]))).ToArray();
                        if (on.Length != 2) throw new Exception("corner topology");
                        int a1 = on[0], b1 = on[1];
                        double cd = Dot(t[a1], t[b1]), sd = Math.Sqrt(1 - cd * cd);
                        if (sd < 0.02) throw new Exception("degenerate corner");
                        T[m] = Add(V, Add(Scale(t[a1], legOn[b1, m] / sd), Scale(t[b1], legOn[a1, m] / sd)));
                    }
                    int owner = at.Min(e => e.Item1);
                    res[owner].Flats.Add(new[] { V, T[0], T[1], T[2] });
                }
                catch { }
            }
        }

        public static IBody2 ApplyCut(IBody2 body, EdgeCut c)
        {
            double before = Volume(body);
            foreach (var t in c.Tools)
            {
                int err;
                var res = body.Operations2((int)swBodyOperationType_e.SWBODYCUT, t, out err) as object[];
                if (res == null || res.Length == 0) throw new Exception("cut failed (error " + err + ")");
                body = Largest(res);
            }
            double removed = before - Volume(body);
            if (removed > c.Expected * 1.002 + 1e-12)
                throw new Exception("the chamfer mill would cut into a wall or other geometry - increase the clearance or use a smaller tool");
            return body;
        }

        // A face of the result is new (chamfer / runout) if a point on it is not on any original face.
        public static bool IsNewFace(IFace2 f, List<IFace2> origFaces)
        {
            var box = f.GetBox() as double[];
            if (box == null) return false;
            // A planar face lying in the plane of an original planar face is an old face (a non-convex top face has its
            // UV middle outside the trimmed face, which wrongly marked it new and drew its outline yellow).
            try
            {
                var sf = (ISurface)f.GetSurface();
                if (sf.IsPlane())
                {
                    var pp = (double[])sf.PlaneParams; var n = Norm(new[] { pp[0], pp[1], pp[2] }); var r0 = new[] { pp[3], pp[4], pp[5] };
                    foreach (var o in origFaces)
                    {
                        var so = (ISurface)o.GetSurface(); if (!so.IsPlane()) continue;
                        var po = (double[])so.PlaneParams;
                        if (Math.Abs(Dot(n, Norm(new[] { po[0], po[1], po[2] }))) > 0.999999 && Math.Abs(Dot(n, Sub(new[] { po[3], po[4], po[5] }, r0))) < 1e-7) return false;
                    }
                }
            }
            catch { }
            // interior point (UV middle): the box centre of a curved band is off the face, and its closest point lies on a shared edge
            double[] cp = null;
            try { var uv = f.GetUVBounds() as double[]; var ev = ((ISurface)f.GetSurface()).Evaluate((uv[0] + uv[1]) / 2, (uv[2] + uv[3]) / 2, 0, 0) as double[]; if (ev != null) cp = new[] { ev[0], ev[1], ev[2] }; } catch { }
            if (cp == null) cp = f.GetClosestPointOn((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2) as double[];
            if (cp == null) return false;
            foreach (var o in origFaces)
            {
                var q = o.GetClosestPointOn(cp[0], cp[1], cp[2]) as double[];
                if (q == null) continue;
                double dx = q[0] - cp[0], dy = q[1] - cp[1], dz = q[2] - cp[2];
                if (dx * dx + dy * dy + dz * dz < 1e-14) return false;
            }
            // Tiny trimmed faces (slivers/triangles): the UV middle can fall outside the face. Try the centre of its
            // own edge points, but only when that point really lies on the face (then it is a true interior point).
            try
            {
                var ps = new List<double[]>();
                foreach (IEdge e in (object[])f.GetEdges()) ps.AddRange(EdgePoints(e));
                if (ps.Count > 0)
                {
                    var c = new[] { ps.Average(q => q[0]), ps.Average(q => q[1]), ps.Average(q => q[2]) };
                    var onF = f.GetClosestPointOn(c[0], c[1], c[2]) as double[];
                    if (onF != null && Dist(onF, c) < 1e-9)
                        foreach (var o in origFaces)
                        {
                            var q = o.GetClosestPointOn(c[0], c[1], c[2]) as double[];
                            if (q != null && Dist(q, c) < 1e-7) return false;
                        }
                }
            }
            catch { }
            return true;
        }

        static double Volume(IBody2 b)
        {
            var mp = b.GetMassProperties(1) as double[];
            return mp != null ? mp[3] : 0;
        }

        public static void Log(string m)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "CustomFeatures.log"), DateTime.Now.ToString("HH:mm:ss ") + m + System.Environment.NewLine); } catch { }
        }

        public static IBody2 Largest(object[] res)
        {
            if (res.Length == 1) return (IBody2)res[0];
            IBody2 best = null; double bestV = -1;
            foreach (IBody2 b in res)
            {
                var mp = b.GetMassProperties(1) as double[];
                double v = mp != null ? mp[3] : 0;
                if (v > bestV) { bestV = v; best = b; }
            }
            return best;
        }

        // Tool bodies for one edge: chamfer prism along the kept length + a cone at each blocked end.
        // Cross-section (plane normal to the edge, O = the edge): the mill comes from face F (axis normal to F). The chamfer
        // line runs from A on F (Size from the edge) at the chamfer angle to B on the other face G; C lies outside the part.
        public static IEnumerable<IBody2> Tools(IEdge edge, ChamferParams p, IModeler modeler, IMathUtility math, bool flip,
                                                 List<double[][]> lines = null, double[] expected = null, BlendInfo blend = null, EdgeCut cut = null)
        {
            if (blend == null) blend = new BlendInfo();
            var curve = (ICurve)edge.GetCurve();
            if (curve.IsCircle()) return CircleTools(edge, p, modeler, math, expected, lines, blend, flip);
            if (!curve.IsLine()) throw new Exception("only straight edges and circular arcs are supported");
            var v0 = edge.GetStartVertex() as IVertex;
            var v1 = edge.GetEndVertex() as IVertex;
            if (v0 == null || v1 == null) throw new Exception("edge has no end points");
            var p0 = (double[])v0.GetPoint();
            var p1 = (double[])v1.GetPoint();
            var t = Sub(p1, p0);
            double len = Len(t);
            t = Scale(t, 1 / len);

            var adj = (object[])edge.GetTwoAdjacentFaces2();
            var f1 = (IFace2)adj[0];
            var f2 = (IFace2)adj[1];
            var n1 = PlaneNormal(f1);
            var n2 = PlaneNormal(f2);
            if (Math.Abs(Dot(n1, n2)) > 0.9999) throw new Exception("the two faces at the edge are tangent or parallel");

            // Direction from the edge into each face. Outside edge: each lies behind the plane of the other face.
            var mid = Scale(Add(p0, p1), 0.5);
            double probeD = Math.Min(p.Size, len) * 0.1;
            var u1 = IntoFace(f1, n1, t, mid, probeD);
            var u2 = IntoFace(f2, n2, t, mid, probeD);
            if (u1 == null || u2 == null || Dot(u1, n2) > -1e-6 || Dot(u2, n1) > -1e-6)
                throw new Exception("edge is concave (inside corner) - pick outside edges");

            // Which face the chamfer mill comes from: the more upward-facing one (+Z), else the larger face
            bool fromF1;
            if (Math.Abs(n1[2] - n2[2]) > 0.3) fromF1 = n1[2] > n2[2];
            else fromF1 = f1.GetArea() >= f2.GetArea();
            if (flip) fromF1 = !fromF1;
            if (blend.Axis != null) fromF1 = Dot(n1, blend.Axis) < -0.5;   // blended: tool comes from the shared face
            if (blend.ConeWall != null) fromF1 = Dot(n1, blend.ConeWall) < 0.5;   // blended with a rim: the mill works from the face that continues below the corner
            var nF = fromF1 ? n1 : n2; var nG = fromF1 ? n2 : n1;
            var uF = fromF1 ? u1 : u2; var uG = fromF1 ? u2 : u1;
            // blended with a rim at the floor: the chamfer legs are measured in the floor plane and swept along the edge, like the native corner
            var dnH = blend.ConeDown[0] ?? blend.ConeDown[1];
            bool horiz = dnH != null && (blend.ConeAxis[0] != null || blend.ConeAxis[1] != null);
            double[] hzv = dnH != null ? Norm(dnH) : null;
            if (horiz)
            {
                Func<double[], double[]> hproj = v => Norm(Sub(v, Scale(hzv, Dot(v, hzv))));
                uF = hproj(uF); uG = hproj(uG);
            }
            var axis = Scale(nF, -1);                                       // tool axis, pointing into the material

            double phi = Math.Acos(Math.Max(-1, Math.Min(1, Dot(uF, uG))));   // angle of the material wedge at the edge
            double beta = p.Angle;
            if (beta > Math.PI - phi - 0.002)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "chamfer angle must be below {0:0.#} deg on this {1:0.#} deg edge",
                    (Math.PI - phi) * 180 / Math.PI - 0.1, phi * 180 / Math.PI));
            double d1 = p.Size;                                    // leg on F
            double d2 = d1 * Math.Sin(beta) / Math.Sin(phi + beta);   // leg on G
            double hB = d2 * Math.Sin(phi);                        // depth of the chamfer bottom (on G) below F
            double rF = hB / Math.Tan(beta);                       // mill radius where its cone meets F
            if (p.ToolDia / 2 < rF - 1e-9)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "tool diameter must be at least {0:0.###} mm for this chamfer", 2 * rF * 1000));

            double stop = p.ToolDia / 2 + p.Clearance;
            bool e0 = blend.End[0], e1 = blend.End[1];
            bool b0 = !e0 && Blocked(v0, Scale(t, -1), n1, n2);
            bool b1 = !e1 && Blocked(v1, t, n1, n2);
            bool k0 = b0 || blend.ConeEnd[0], k1 = b1 || blend.ConeEnd[1];   // ends that close with a runout cone
            double ext = Math.Max(d1, d2);
            double s0 = e0 || blend.Join[0] ? 0 : b0 ? stop : -ext;       // blended / joined ends run exactly to the corner / neighbour
            double s1 = e1 || blend.Join[1] ? len : b1 ? len - stop : len + ext;
            if (s1 - s0 < 1e-6)
                throw new Exception(string.Format(CultureInfo.InvariantCulture,
                    "edge ({0:0.###} mm) too short for a {1:0.###} mm tool between walls", len * 1000, p.ToolDia * 1000));

            if (expected != null)
            {
                double inside = Math.Max(0, Math.Min(s1, len) - Math.Max(s0, 0));
                double aTri = 0.5 * d1 * d2 * Math.Sin(phi);
                Func<int, bool, double> endVol = (k, on) => !on ? 0 : blend.ConeAxis[k] != null ? Math.PI / 3 * Math.Pow(Math.Max(d1, d2), 3) : LugVolume(hB, beta, Dot(nF, nG));
                expected[0] = aTri * inside + endVol(0, k0) + endVol(1, k1)
                            + blend.Corners.Sum(c => (2 * Math.PI - c.Gamma) * d1 * d1 * hB / 6);   // the cone's share in the material (upper bound)
                // Open ends: the tool runs on past the end (prism extended by ext). Usually that is air, but it may
                // shave material the end meets at an angle (e.g. an existing chamfer) - a real run-off, not a wall.
                int open = (b0 || e0 || blend.Join[0] ? 0 : 1) + (b1 || e1 || blend.Join[1] ? 0 : 1);
                expected[0] += open * aTri * ext;
                foreach (var f in blend.Flats)   // corner tetrahedron V-T1-T2-T3
                    expected[0] += 1.5 * Math.Abs(Dot(Sub(f[1], f[0]), Cross(Sub(f[2], f[0]), Sub(f[3], f[0])))) / 6;
            }
            if (cut != null)
            {
                var m = Norm(Cross(t, Sub(Scale(uG, d2), Scale(uF, d1))));
                cut.Normal = Dot(m, Add(nF, nG)) < 0 ? Scale(m, -1) : m;
                cut.Dir = t;
            }
            if (lines != null)
            {
                PreviewLines(lines, p0, t, s0, s1, k0, k1, uF, uG, d2, phi, rF);
                foreach (var c in blend.Corners) CornerLines(lines, c, axis, p.Size, hB);
            }

            var list = new List<IBody2>();
            if (horiz)   // profile on the floor plane, swept along the (leaning) edge: the band ends on the floor like the native chamfer
            {
                var up = Scale(hzv, -1); var nU = Dot(up, t) >= 0 ? up : hzv;
                var cH = Scale(Add(nF, nG), ext); cH = Sub(cH, Scale(hzv, Dot(cH, hzv)));
                list.Add(PrismSheared(Add(p0, Scale(t, s0)), nU, t, s1 - s0, Scale(uF, d1), Scale(uG, d2), cH, uF, modeler, math));
            }
            else
                list.Add(Prism(Add(p0, Scale(t, s0)), t, s1 - s0, Scale(uF, d1), Scale(uG, d2), Scale(Add(nF, nG), ext), uF, modeler, math));
            Func<double, double[]> apexAt = s => Add(Add(p0, Scale(t, s)), Scale(uG, d2));   // the mill tip sits on the chamfer's bottom edge
            // a rim blended into this edge ends it with the corner cone (axis square to the rim, in the floor plane), not the mill's own tilted one
            Func<int, IBody2> endCone = k =>
            {
                var ca = blend.ConeAxis[k];
                if (ca == null) return Cone(apexAt(k == 0 ? s0 : s1), axis, hB, beta, stop, modeler);
                // the cone tangent to the chamfer plane along the line from its corner on F, with its apex on the axis through this end
                var mb = Norm(Cross(t, Sub(Scale(uG, d2), Scale(uF, d1))));
                double mc = Dot(mb, ca), sAx = Math.Abs(mc) > 0.2 ? d1 * Dot(mb, uF) / mc : -1;
                double h = sAx > 1e-9 ? sAx : Math.Max(d1, d2), alpha = sAx > 1e-9 ? Math.Asin(Math.Min(1, Math.Abs(mc))) : beta;
                var vk = Add(p0, Scale(t, k == 0 ? 0 : len));
                var cone = Cone(Add(vk, Scale(ca, h)), ca, h, Math.PI / 2 - alpha, 10 * Math.Max(Math.Max(d1, d2), hB), modeler);
                // only the quarter between the rim's end (into the wall) and below the floor is cut, like the native corner
                var dn = blend.ConeDown[k]; var u1 = Math.Abs(Dot(uF, ca)) <= Math.Abs(Dot(uG, ca)) ? uF : uG;
                if (dn != null)
                {
                    double L = 10 * Math.Max(Math.Max(d1, d2), hB);
                    try
                    {
                        double eps = 5e-5;   // keep clear of the wall and floor planes the quarter would otherwise share with the part
                        var o0 = Sub(Sub(Sub(vk, Scale(ca, L)), Scale(u1, eps)), Scale(dn, eps));
                        var wedge = Prism(o0, ca, L + h + 1e-4, new double[3], Scale(u1, L), Scale(dn, L), u1, modeler, math);
                        Func<IBody2, double> vol = b => ((double[])b.GetMassProperties(1))[3];
                        double vc = vol(cone), vw = vol(wedge);
                        int err = 0; var res = cone.Operations2(15901, wedge, out err) as object[];
                        if (res != null && res.Length > 0 && res[0] is IBody2 && vol((IBody2)res[0]) < vc * 0.9999 && vol((IBody2)res[0]) > 0) return (IBody2)res[0];
                    }
                    catch { }
                }
                return cone;
            };
            if (k0) list.Add(endCone(0));
            if (k1) list.Add(endCone(1));
            foreach (var c in blend.Corners) list.Add(Cone(Add(c.V, Scale(axis, hB)), axis, hB, beta, 10 * Math.Max(d1, hB), modeler));
            foreach (var f in blend.Flats) list.Add(FlatCornerTool(f, modeler, math));
            return list;
        }

        // Unit direction from the edge into face f (perpendicular to the edge direction t): the side that lies on the face
        static double[] IntoFace(IFace2 f, double[] n, double[] t, double[] mid, double d)
        {
            var v = Norm(Cross(t, n));
            foreach (var s in new[] { 1.0, -1.0 })
            {
                var pr = Add(mid, Scale(v, s * d));
                var cp = f.GetClosestPointOn(pr[0], pr[1], pr[2]) as double[];
                if (cp != null && Len(Sub(cp, pr)) < 1e-6) return Scale(v, s);
            }
            return null;
        }

        // Material the mill's cone removes beyond the end of the prism (half of its part inside the wedge):
        // slices of the cone (apex on the other face G, axis normal to F) cut by G's plane. cosPsi = nF . nG.
        static double LugVolume(double hB, double beta, double cosPsi)
        {
            double cot = 1 / Math.Tan(beta), sum = 0;
            const int n = 200;
            for (int i = 0; i < n; i++)
            {
                double h = hB * (i + 0.5) / n, r = h * cot, D = h * cosPsi, a;   // D: signed distance of the axis from G's plane (+ = outside)
                if (D >= r) a = 0;
                else if (D <= -r) a = Math.PI * r * r;
                else a = r * r * Math.Acos(D / r) - D * Math.Sqrt(r * r - D * D);
                sum += a;
            }
            return 0.5 * sum * hB / n;
        }

        // Triangle T1 T2 T3 (where the chamfer lines meet on each face) swept outwards past the corner.
        static IBody2 FlatCornerTool(double[][] f, IModeler modeler, IMathUtility math)
        {
            var V = f[0]; var a = f[1]; var b = f[2]; var c = f[3];
            var nrm = Norm(Cross(Sub(b, a), Sub(c, a)));
            var cen = Scale(Add(Add(a, b), c), 1.0 / 3);
            var s = Norm(Sub(V, cen));                               // from the cutting plane towards the corner
            double h = 2 * Math.Max(Len(Sub(V, a)), Math.Max(Len(Sub(V, b)), Len(Sub(V, c))));
            var surf = (ISurface)modeler.CreatePlanarSurface2(a, Dot(nrm, s) >= 0 ? nrm : Scale(nrm, -1), Norm(Sub(b, a)));
            var sheet = surf.CreateTrimmedSheet4(new[] { Seg(modeler, a, b), Seg(modeler, b, c), Seg(modeler, c, a) }, true) as IBody2;
            if (sheet == null) throw new Exception("could not build flat corner");
            var body = modeler.CreateExtrudedBody((Body2)sheet, (MathVector)math.CreateVector(s), h);
            if (body == null) throw new Exception("could not build flat corner");
            return body;
        }

        // Preview of a blended corner: the radius-d arc on the shared face and generator lines to the cone apex.
        static void CornerLines(List<double[][]> lines, Corner c, double[] axis, double d, double hB)
        {
            var u = c.DirB;
            var w = Norm(Sub(c.DirA, Scale(u, Dot(c.DirA, u))));
            double g = c.Gamma;
            Func<double, double[]> arc = th => Add(c.V, Add(Scale(u, d * Math.Cos(th)), Scale(w, d * Math.Sin(th))));
            var apex = Add(c.V, Scale(axis, hB));
            const int segs = 16;
            for (int i = 0; i < segs; i++)
                lines.Add(new[] { arc(g * i / segs), arc(g * (i + 1) / segs) });
            for (int i = 0; i <= 4; i++)
                lines.Add(new[] { apex, arc(g * i / 4) });
        }

        // Generator strokes on each runout cone (outline and hatch of the flat faces come from FaceLines).
        static void PreviewLines(List<double[][]> lines, double[] p0, double[] t, double s0, double s1, bool b0, bool b1,
                                 double[] uF, double[] uG, double d2, double phi, double rF)
        {
            foreach (var end in new[] { Tuple.Create(b0, s0, Scale(t, -1)), Tuple.Create(b1, s1, t) })
            {
                if (!end.Item1) continue;
                var S = Add(p0, Scale(t, end.Item2));
                var w = end.Item3;
                var apex = Add(S, Scale(uG, d2));
                var Z = Add(S, Scale(uF, d2 * Math.Cos(phi)));   // centre of the cone's circle on face F
                Func<double, double[]> arc = th => Add(Z, Add(Scale(w, rF * Math.Cos(th)), Scale(uF, rF * Math.Sin(th))));
                const int segs = 16;
                for (int i = 0; i < segs; i++)
                    lines.Add(new[] { arc(Math.PI / 2 * i / segs), arc(Math.PI / 2 * (i + 1) / segs) });
                for (int i = 0; i < 4; i++)
                    lines.Add(new[] { apex, arc(Math.PI / 2 * i / 4) });
            }
        }

        // An end is blocked when another face at the vertex faces back along the edge (a wall).
        static bool Blocked(IVertex v, double[] outDir, double[] n1, double[] n2) { return Blocker(v, outDir, n1, n2) != null; }

        // Normal of the wall that blocks the edge's end at v (null: open end)
        static double[] Blocker(IVertex v, double[] outDir, double[] n1, double[] n2)
        {
            var pt = (double[])v.GetPoint();
            foreach (IFace2 f in (object[])v.GetAdjacentFaces())
            {
                var n = FaceNormalAt(f, pt);
                if (n == null) continue;
                if (Dot(n, n1) > 0.999 || Dot(n, n2) > 0.999) continue;  // the edge's own faces
                if (Dot(n, outDir) < -0.5) return n;
            }
            return null;
        }

        static double[] PlaneNormal(IFace2 f)
        {
            var s = (ISurface)f.GetSurface();
            if (!s.IsPlane()) throw new Exception("both faces at the edge must be planar");
            return Norm((double[])f.Normal);
        }

        static double[] FaceNormalAt(IFace2 f, double[] pt)
        {
            var s = (ISurface)f.GetSurface();
            if (s.IsPlane()) return Norm((double[])f.Normal);
            var ev = s.EvaluateAtPoint(pt[0], pt[1], pt[2]) as double[];
            if (ev == null) return null;
            var n = Norm(new[] { ev[0], ev[1], ev[2] });
            return f.FaceInSurfaceSense() ? Scale(n, -1) : n;   // measured on a boss (false) and a bore (true): the raw normal is the face normal when the sense is false
        }

        // Triangle (A on face F, B on the other face, C outside), given as offsets from o, swept along the edge.
        // Like Prism, but the profile lies in the plane with normal n through o and is swept along t (not square to the profile)
        static IBody2 PrismSheared(double[] o, double[] n, double[] t, double len, double[] a, double[] b, double[] c, double[] refDir,
                                   IModeler modeler, IMathUtility math)
        {
            a = Add(o, a); b = Add(o, b); c = Add(o, c);
            var surf = (ISurface)modeler.CreatePlanarSurface2(o, n, refDir);
            var curves = new[] { Seg(modeler, a, b), Seg(modeler, b, c), Seg(modeler, c, a) };
            var sheet = surf.CreateTrimmedSheet4(curves, true) as IBody2;
            if (sheet == null) throw new Exception("could not build chamfer profile");
            var body = modeler.CreateExtrudedBody((Body2)sheet, (MathVector)math.CreateVector(t), len);
            if (body == null) throw new Exception("could not sweep chamfer profile");
            return body;
        }

        static IBody2 Prism(double[] o, double[] t, double len, double[] a, double[] b, double[] c, double[] refDir,
                            IModeler modeler, IMathUtility math)
        {
            a = Add(o, a); b = Add(o, b); c = Add(o, c);
            var surf = (ISurface)modeler.CreatePlanarSurface2(o, t, refDir);
            var curves = new[] { Seg(modeler, a, b), Seg(modeler, b, c), Seg(modeler, c, a) };
            var sheet = surf.CreateTrimmedSheet4(curves, true) as IBody2;
            if (sheet == null) throw new Exception("could not build chamfer profile");
            var dir = (MathVector)math.CreateVector(t);
            var body = modeler.CreateExtrudedBody((Body2)sheet, dir, len);
            if (body == null) throw new Exception("could not sweep chamfer profile");
            return body;
        }

        // Preview strokes taken from the computed result: outline of every new face, plus hatch lines along the
        // chamfered edge's direction on new flat faces. Matches the real geometry at any corner.
        public static void FaceLines(IBody2 result, List<IFace2> origFaces, IList<EdgeCut> plans, List<double[][]> lines)
        {
            // chamfer-plane normal -> direction of the edge that made it
            var dirs = plans.Where(c => c != null && c.Normal != null).Select(c => new[] { c.Normal, c.Dir }).ToList();
            foreach (IFace2 f in (object[])result.GetFaces())
            {
                if (!IsNewFace(f, origFaces)) continue;
                var segs = new List<double[][]>();
                foreach (IEdge e in (object[])f.GetEdges())
                {
                    var pts = EdgePoints(e);
                    for (int i = 0; i + 1 < pts.Count; i++) segs.Add(new[] { pts[i], pts[i + 1] });
                }
                lines.AddRange(segs);
                if (!((ISurface)f.GetSurface()).IsPlane() || segs.Count == 0) continue;
                var n = Norm((double[])f.Normal);
                var t = dirs.Where(d => Math.Abs(Dot(d[0], n)) > 0.999).Select(d => d[1]).FirstOrDefault();
                if (t == null) continue;
                var w = Norm(Cross(n, t));
                var q0 = segs[0][0];
                Func<double[], double> U = q => Dot(Sub(q, q0), t), Vv = q => Dot(Sub(q, q0), w);
                double vmin = segs.Min(sg => Math.Min(Vv(sg[0]), Vv(sg[1]))), vmax = segs.Max(sg => Math.Max(Vv(sg[0]), Vv(sg[1])));
                const int hatch = 4;
                for (int k = 1; k < hatch; k++)
                {
                    double v = vmin + (vmax - vmin) * k / hatch;
                    var us = new List<double>();
                    foreach (var sg in segs)
                    {
                        double va = Vv(sg[0]) - v, vb = Vv(sg[1]) - v;
                        if ((va < 0) == (vb < 0) || va == vb) continue;
                        double r = va / (va - vb);
                        us.Add(U(sg[0]) + (U(sg[1]) - U(sg[0])) * r);
                    }
                    us.Sort();
                    for (int i = 0; i + 1 < us.Count; i += 2)
                        lines.Add(new[] { Add(q0, Add(Scale(t, us[i]), Scale(w, v))), Add(q0, Add(Scale(t, us[i + 1]), Scale(w, v))) });
                }
            }
        }

        // Points along an edge, from its own start to end point (the curve's raw parameter range is not reliable
        // on temporary bodies, which gave stray strokes).
        static List<double[]> EdgePoints(IEdge e)
        {
            var c = (ICurve)e.GetCurve();
            // End points from the vertices: on result bodies the curve parameters can still span the tool body's
            // (longer) edge, which drew strokes past the part.
            var ab = Ends(e);
            var a = ab[0];
            var b = ab[1];
            if (c.IsLine()) return new List<double[]> { a, b };
            if (Dist(a, b) < 1e-9)   // closed edge (full circle): sample its whole curve range
            {
                var prm = e.GetCurveParams3();
                var ring = new List<double[]>();
                for (int i = 0; i <= 64; i++)
                {
                    var q = (double[])c.Evaluate2(prm.UMinValue + (prm.UMaxValue - prm.UMinValue) * i / 64, 0);
                    ring.Add(new[] { q[0], q[1], q[2] });
                }
                return ring;
            }
            if (c.IsCircle())   // arc: sample it by angle (the tessellation call returned nothing useful on result bodies)
            {
                var cp = (double[])c.CircleParams;
                var o = new[] { cp[0], cp[1], cp[2] };
                var ax = Norm(new[] { cp[3], cp[4], cp[5] });
                var ra = Sub(a, o);
                double th = Math.Atan2(Dot(Cross(ra, Sub(b, o)), ax), Dot(ra, Sub(b, o)));
                if (th < 0) th += 2 * Math.PI;
                foreach (var sweep in new[] { th, th - 2 * Math.PI })   // counter-clockwise or clockwise from a to b: the one that is the edge
                {
                    var mid = Add(o, Add(Scale(ra, Math.Cos(sweep / 2)), Scale(Cross(ax, ra), Math.Sin(sweep / 2))));
                    var cl = e.GetClosestPointOn(mid[0], mid[1], mid[2]) as double[];
                    if (cl == null || Dist(cl, mid) > 1e-6) continue;
                    var arcPts = new List<double[]>();
                    for (int i = 0; i <= 32; i++)
                    {
                        double ph = sweep * i / 32;
                        arcPts.Add(Add(o, Add(Scale(ra, Math.Cos(ph)), Scale(Cross(ax, ra), Math.Sin(ph)))));
                    }
                    return arcPts;
                }
            }
            var flat = c.GetTessPts(1e-5, 1e-4, a, b) as double[];
            var pts = new List<double[]>();
            if (flat != null)
                for (int i = 0; i + 2 < flat.Length; i += 3) pts.Add(new[] { flat[i], flat[i + 1], flat[i + 2] });
            if (pts.Count < 2) pts = new List<double[]> { a, b };
            return pts;
        }

        static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        public static Curve Segment(IModeler m, double[] a, double[] b) { return Seg(m, a, b); }
        public static double Dist(double[] a, double[] b) { return Len(Sub(a, b)); }

        static Curve Seg(IModeler m, double[] a, double[] b)
        {
            var line = (ICurve)m.CreateLine(a, Sub(b, a));
            return line.CreateTrimmedCurve2(a[0], a[1], a[2], b[0], b[1], b[2]);
        }

        // Chamfer-mill footprint: cone with its tip (apex) on the chamfer's bottom edge, axis normal to the mill's face,
        // hB deep below that face; half angle = 90 deg - chamfer angle.
        static IBody2 Cone(double[] apex, double[] axis, double hB, double beta, double stop, IModeler modeler)
        {
            double cot = 1 / Math.Tan(beta);
            // Start a bit outside the part, but never wider than the tool reaches (stop = distance to the wall),
            // otherwise the part of the cone above the surface would cut into the wall.
            double e = Math.Max(0, Math.Min(hB * 0.5, (stop - hB * cot - 1e-6) * Math.Tan(beta)));
            var baseC = Sub(apex, Scale(axis, hB + e));
            double H = hB + e;
            // true pointed cone (apex exactly at chamfer depth); fall back to a tiny tip if the kernel refuses
            var body = modeler.CreateBodyFromCone(new[] { baseC[0], baseC[1], baseC[2], axis[0], axis[1], axis[2], H * cot, 0.0, H }) as IBody2;
            if (body == null)
            {
                double rTop = hB * 1e-3;
                body = modeler.CreateBodyFromCone(new[] { baseC[0], baseC[1], baseC[2], axis[0], axis[1], axis[2], H * cot, rTop, H - rTop * Math.Tan(beta) }) as IBody2;
            }
            if (body == null) throw new Exception("could not build tool runout");
            return body;
        }

        static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
        static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }
        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }
        static double[] Norm(double[] a) { return Scale(a, 1 / Len(a)); }
    }

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

    // ------------------------------------------------------------------------------------------
    // PropertyManager page (insert + edit definition)
    // ------------------------------------------------------------------------------------------
    [ComVisible(true)]
    public class ChamferPage : IPropertyManagerPage2Handler9
    {
        const int IdSel = 10, IdSize = 20, IdDia = 21, IdClr = 22, IdFlip = 23, IdBlend = 24, IdFlat = 25, IdAngle = 26, IdTangent = 27, IdMenuReverse = 30;
        const string FlipCaption = "Reverse direction";
        readonly ISldWorks sw;
        readonly IModelDoc2 model;
        readonly IFeature feat;          // null when inserting
        readonly IMacroFeatureData data; // null when inserting
        readonly ChamferParams p;
        IPropertyManagerPage2 page;
        IPropertyManagerPageSelectionbox sel;
        IPropertyManagerPageNumberbox numSize, numAngle, numDia, numClr;
        IPropertyManagerPageCheckbox chkBlend, chkFlat, chkTangent;
        bool ok;
        List<object> edges = new List<object>();
        readonly List<IBody2> preview = new List<IBody2>();
        bool shown;
        readonly List<object> keep = new List<object>();   // group boxes and labels: SOLIDWORKS does not hold them, a GC that finalizes their wrappers stops all PropertyManager callbacks
        bool busy;                // re-entrancy guard for the preview
        IBody2 hidden;            // real body hidden during preview
        readonly Dictionary<string, bool> flips = new Dictionary<string, bool>();   // per edge (by geometry key)
        const string Hint = "Select outside edges (straight at any angle between the faces, or circle / arc edges of holes, bosses and pocket corners; select a whole tangent chain).  Ends that run into a wall stop at tool radius + clearance " +
                            "and finish with the cone the chamfer mill leaves (mill angle = 180° - 2 x chamfer angle).\n" +
                            "Reverse direction applies to the edge highlighted in the list (or the last one picked).";


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
                "Straight outside edges to chamfer. Highlight an edge in the list and tick Reverse direction to machine it from its other face.");
            ((IPropertyManagerPageControl)sel).SetStandardPictureLabel((int)swControlBitmapLabelType_e.swBitmapLabel_SelectEdge);
            sel.SingleEntityOnly = false;
            sel.Height = 36;
            sel.Mark = 1;
            sel.SetSelectionFilters(new[] { (int)swSelectType_e.swSelEDGES });
            var flipBtn = (IPropertyManagerPageBitmapButton)g1.AddControl2(IdFlip,
                (short)swPropertyManagerPageControlType_e.swControlType_BitmapButton, FlipCaption, left, cOpts,
                "Machine the highlighted edge from its other face (moves the runout cone to the other face). " +
                "Default: from the upward-facing (+Z) face, or the larger face.");
            flipBtn.SetStandardBitmaps((int)swPropertyManagerPageBitmapButtons_e.swBitmapButtonImage_reverse_direction);
            try   // under the selection icon, left of the list (the group below is laid out from this button, so the list is kept short)
            {
                var fc = (IPropertyManagerPageControl)flipBtn; var sc = (IPropertyManagerPageControl)sel;
                fc.Left = 4; fc.Top = (short)(sc.Top + 22);
            }
            catch (Exception ex) { Engine.Log("flip button place: " + ex.Message); }
            keep.Add(flipBtn);   // a control whose wrapper is collected stops responding (the button vanished)

            var g2 = (IPropertyManagerPageGroup)page.AddGroupBox(2, "Chamfer Parameters", gOpts); keep.Add(g2);
            chkTangent = (IPropertyManagerPageCheckbox)g2.AddControl2(IdTangent,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Tangent propagation", left, cOpts,
                "Like the native tools: the edges that continue the selected edges tangentially (a pocket outline of lines and arcs, the rim of a slot) are chamfered too, without being selected.");
            chkTangent.Checked = p.Tangent;
            numSize = Number(g2, IdSize, "Chamfer distance (on the mill's face)", p.Size, 0.00001, 1, swControlBitmapLabelType_e.swBitmapLabel_LinearDistance, cOpts, indent, left);
            numAngle = Number(g2, IdAngle, "Chamfer angle (from the mill's face)", p.Angle, 0.0174533, 1.5533, swControlBitmapLabelType_e.swBitmapLabel_AngularDistance, cOpts, indent, left,
                              swNumberboxUnitType_e.swNumberBox_Angle);
            chkBlend = (IPropertyManagerPageCheckbox)g2.AddControl2(IdBlend,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Blend chamfers at inside corners", left, cOpts,
                "Two selected edges that meet at an inside corner (e.g. a step wall and the floor, on the same side face) " +
                "are chamfered as one tool path from that side face: the chamfer turns around the corner with radius = chamfer distance " +
                "instead of stopping before the wall.");
            chkBlend.Checked = p.Blend;
            chkFlat = (IPropertyManagerPageCheckbox)g2.AddControl2(IdFlat,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, "Flat corner where 3 chamfers meet", left, cOpts,
                "Where three selected edges meet at an outside corner, finish it with a small flat triangle like the native Chamfer " +
                "(needs a 3D move on the machine). Off: the three chamfers meet in a point, as a chamfer mill tracing the edges leaves it.");
            chkFlat.Checked = p.Flat;

            var g3 = (IPropertyManagerPageGroup)page.AddGroupBox(3, "Chamfer Mill", gOpts); keep.Add(g3);
            numDia = Number(g3, IdDia, "Tool diameter", p.ToolDia, 0.0001, 1, swControlBitmapLabelType_e.swBitmapLabel_Diameter, cOpts, indent, left);
            numClr = Number(g3, IdClr, "Clearance to wall", p.Clearance, 0, 1, swControlBitmapLabelType_e.swBitmapLabel_LinearDistance1, cOpts, indent, left);

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
            var ab = Engine.Ends(e);
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
                double len = Engine.Dist(a, b);
                if (len < 1e-7) continue;
                // like the native "missing" line: many short dashes with gaps a bit shorter than the dashes
                int n = Math.Max(8, (int)Math.Round(len / 0.0005));
                n = Math.Min(n, 60);
                double period = len / n, dash = period * 0.55;
                for (double t = 0; t < len - 1e-9; t += period)
                {
                    double t1 = Math.Min(len, t + dash);
                    Func<double, double[]> at = u => new[] { a[0] + (b[0] - a[0]) * u / len, a[1] + (b[1] - a[1]) * u / len, a[2] + (b[2] - a[2]) * u / len };
                    var wire = modeler.CreateWireBody(new[] { Engine.Segment(modeler, at(t), at(t1)) },
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
            var ab = Engine.Ends(e);
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
            if (list.Count > 1 && value == null)   // an edge in a blend has only one valid direction: nothing to reverse
            {
                try { var bl = Engine.FindBlends(list, p, p.Blend, p.Flat, k => FlipOf(list[k])); if (bl[item].Axis != null || bl[item].End[0] || bl[item].End[1]) return; } catch { }
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
                try { DrawMissing((IModeler)sw.GetModeler()); } catch (Exception ex) { Engine.Log("missing: " + ex.Message); }
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
                    Engine.Log("preview problem: " + problem.Replace("\n", " | "));
                    Message(problem);
                    model.GraphicsRedraw2();
                    return;
                }

                var mp = model.MaterialPropertyValues as double[];
                int color = mp != null && mp.Length >= 3
                    ? (int)(mp[0] * 255) | ((int)(mp[1] * 255) << 8) | ((int)(mp[2] * 255) << 16)
                    : 0xB4B4B4;
                try { Engine.FaceLines(result, ((object[])orig.GetFaces()).Cast<IFace2>().ToList(), plans, lines); }
                catch (Exception ex) { Engine.Log("face lines: " + ex.Message); }
                result.Display3(model, color, (int)swTempBodySelectOptions_e.swTempBodySelectable);
                preview.Add(result);
                orig.DisableDisplay = true;
                hidden = orig;

                const int yellow = 0x00FFFF;   // COLORREF (BGR): R=255 G=255 B=0
                foreach (var ln in lines)
                {
                    if (Engine.Dist(ln[0], ln[1]) < 1e-7) continue;
                    var wire = modeler.CreateWireBody(new[] { Engine.Segment(modeler, ln[0], ln[1]) },
                        (int)swCreateWireBodyOptions_e.swCreateWireBodyByDefault) as IBody2;
                    if (wire == null) continue;
                    wire.Display3(model, yellow, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
                    preview.Add(wire);
                }

                Message(HintNow());
                OkCursor(true);
                model.GraphicsRedraw2();
            }
            catch (Exception ex) { problem = "Preview failed: " + ex.Message; Engine.Log("preview failed: " + ex); Message(problem); }
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
                                             swNumberboxUnitType_e unit = swNumberboxUnitType_e.swNumberBox_Length)
        {
            keep.Add(g.AddControl2(id + 100, (short)swPropertyManagerPageControlType_e.swControlType_Label, label, left, cOpts, ""));
            var nb = (IPropertyManagerPageNumberbox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox, label, indent, cOpts, label);
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
            Engine.Log(string.Format(CultureInfo.InvariantCulture, "modify: ok={0} size {1} -> {2}, dia {3}, clr {4}, flips '{5}', edges set {6}",
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
                Engine.Log("submit: edge="+(e!=null)+" face="+(f!=null)+" temp="+(b!=null && b.IsTemporaryBody()));
                if (b == null || !b.IsTemporaryBody()) return true;
                object real = hidden == null ? null : e != null ? (object)MapToReal(e, hidden) : MapFaceToReal(f, hidden);
                Engine.Log("submit: mapped="+(real!=null));
                if (real != null) { pending.Add(Tuple.Create(real, 1)); selectTimer.Start(); }
            }
            catch (Exception ex) { Engine.Log("submit failed: "+ex.Message); }
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
            if (best == null) Engine.Log("MapToReal: no real edge near preview edge");
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
