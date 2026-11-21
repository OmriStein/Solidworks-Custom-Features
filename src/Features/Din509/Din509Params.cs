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
    // Stored settings of a DIN 509 undercut (lengths in metres, as SOLIDWORKS holds them)
    public class Din509Params
    {
        public static readonly string[] Names = { "Form", "R", "T1", "T2", "F" };
        public static readonly int[] Types = { (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString, D, D, D, D };
        const int D = (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble;
        public string Form = "F";
        public double R = 0.0004, T1 = 0.0002, T2 = 0.0001, F = 0.002;

        public char FormChar { get { return Form.Length > 0 ? Form[0] : 'F'; } }
        public string[] Values()
        {
            Func<double, string> s = v => v.ToString("R", CultureInfo.InvariantCulture);
            return new[] { Form, s(R), s(T1), s(T2), s(F) };
        }
        public void Write(IMacroFeatureData d) { ParamIO.Write(d, Names, Types, Values()); }

        public static Din509Params Read(IMacroFeatureData d)
        {
            var p = new Din509Params();
            double v = 0; string s;
            d.GetStringByName("Form", out s); if (!string.IsNullOrEmpty(s)) p.Form = s;
            v = 0; d.GetDoubleByName("R", out v); if (v > 0) p.R = v;
            v = 0; d.GetDoubleByName("T1", out v); if (v > 0) p.T1 = v;
            v = 0; d.GetDoubleByName("T2", out v); if (v > 0) p.T2 = v;
            v = 0; d.GetDoubleByName("F", out v); if (v > 0) p.F = v;
            return p;
        }

        public static Din509Params From(Din509Row r)
        {
            return new Din509Params { Form = r.Form.ToString(), R = r.R / 1000, T1 = r.T1 / 1000, T2 = r.T2 / 1000, F = r.F / 1000 };
        }
    }
}