using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MfgChamfer
{
    // Writes a feature's parameters back (ModifyDefinition). names / types / values are the same parallel
    // arrays the feature passes to InsertMacroFeature3. Parameter types: Double, Integer, anything else = string.
    public static class ParamIO
    {
        public static void Write(IMacroFeatureData d, string[] names, int[] types, string[] values)
        {
            // A feature made by an older version lacks some parameters: redefine the whole set first
            var have = new HashSet<string>();
            try { object nm, ty, vl; d.GetParameters(out nm, out ty, out vl); var a = nm as Array; if (a != null) foreach (var o in a) have.Add(o as string); } catch { }
            if (Array.Exists(names, n => !have.Contains(n))) d.SetParameters(names, types, values);
            // SetParameters alone is not picked up by ModifyDefinition: set every value by name as well
            for (int i = 0; i < names.Length; i++)
            {
                if (types[i] == (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeDouble) d.SetDoubleByName(names[i], double.Parse(values[i], CultureInfo.InvariantCulture));
                else if (types[i] == (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeInteger) d.SetIntegerByName(names[i], int.Parse(values[i], CultureInfo.InvariantCulture));
                else d.SetStringByName(names[i], values[i] ?? "");
            }
        }
    }
}