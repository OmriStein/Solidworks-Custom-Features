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
        

        public double TipOffset;           // the mill's apex sits this far (m, along the axis) below the chamfer's lower edge: the chamfer plane moves in by it

        public bool FlipAt(int i) { return i < Flips.Length && Flips[i] == '1'; }

        public static readonly string[] Names = { "Size", "ToolDia", "Clearance", "Flips", "Blend", "Corner", "Edges", "Angle", "Tangent", "TipOffset" };
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
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString,
            (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble
        };

        public string[] Values()
        {
            var c = CultureInfo.InvariantCulture;
            return new[] { Size.ToString("R", c), ToolDia.ToString("R", c), Clearance.ToString("R", c), Flips ?? "", Blend ? "1" : "0", Flat ? "1" : "0", Edges ?? "", Angle.ToString("R", c), Tangent ? "1" : "0", TipOffset.ToString("R", c) };
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
            bool hasTip = false;
            try { object nm, ty, vl; d.GetParameters(out nm, out ty, out vl); var na = nm as Array; if (na != null) foreach (var o in na) if ("TipOffset".Equals(o)) hasTip = true; } catch { }
            if (probe == null || probe2 == null || probe3 == null || probe4 == null || probe5 == null || probeA <= 0 || !hasTip) d.SetParameters(Names, Types, Values());
            d.SetDoubleByName("Size", Size);
            d.SetDoubleByName("ToolDia", ToolDia);
            d.SetDoubleByName("Clearance", Clearance);
            d.SetStringByName("Flips", Flips ?? "");
            d.SetStringByName("Blend", Blend ? "1" : "0");
            d.SetStringByName("Corner", Flat ? "1" : "0");
            d.SetStringByName("Edges", Edges ?? "");
            d.SetDoubleByName("Angle", Angle);
            d.SetDoubleByName("TipOffset", TipOffset);
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
            d.GetDoubleByName("Angle", out v); if (v > 0.01 && v < 1.56) p.Angle = v;
            v = 0; d.GetDoubleByName("TipOffset", out v); if (v > 0) p.TipOffset = v;   // missing (older features) = 0   // missing (older features) = 45 deg
            return p;
        }

        public ChamferParams Clone() { return (ChamferParams)MemberwiseClone(); }

        public static string EncodeEdges(IEnumerable<object> edges)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(";", edges.Select(o =>
            {
                var ab = Geo.Ends((IEdge)o);
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
}
