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
    // DIN 509:2006-12 shaft undercuts, forms E F G H. Dimensions in mm as printed (Table 1); the feature stores metres.
    public class Din509Row
    {
        public char Form; public double R, T1, T2, F;   // F = 0 for G and H: derived from the geometry
        public double DOver, DUpTo;                     // d1 range: over DOver up to DUpTo
        public int Series; public bool Higher;          // radius series (1 preferred), "higher alternating loads"
        public bool Fits(double d1mm) { return d1mm > DOver && d1mm <= DUpTo; }
        public override string ToString()
        {
            return R.ToString("0.0#", CultureInfo.InvariantCulture) + " x " + T1.ToString("0.0#", CultureInfo.InvariantCulture)
                 + (Series == 2 ? "  (series 2)" : "") + (Higher ? "  higher load" : "") + "  d1 " + (DOver + 0) + (DUpTo > 1e5 ? "+" : "-" + DUpTo);
        }
    }

    public static class Din509Table
    {
        const double I = 1e9;
        static Din509Row Q(char form, double r, int s, double t1, double t2, double f, double over, double upTo, bool higher)
        { return new Din509Row { Form = form, R = r, Series = s, T1 = t1, T2 = t2, F = f, DOver = over, DUpTo = upTo, Higher = higher }; }

        // r, series, t1, t2, f, d1 over, up to, higher load
        public static readonly Din509Row[] Rows =
        {
            Q('E', 0.2, 2, 0.1, 0, 1, 1.6, 3, false), Q('E', 0.4, 1, 0.2, 0, 2, 3, 18, false), Q('E', 0.6, 2, 0.2, 0, 2, 10, 18, false),
            Q('E', 0.6, 2, 0.3, 0, 2.5, 18, 80, false), Q('E', 0.8, 1, 0.3, 0, 2.5, 18, 80, false), Q('E', 1, 2, 0.2, 0, 2.5, 18, 50, true),
            Q('E', 1, 2, 0.4, 0, 4, 80, I, false), Q('E', 1.2, 1, 0.2, 0, 2.5, 18, 50, true), Q('E', 1.2, 1, 0.4, 0, 4, 80, I, false),
            Q('E', 1.6, 1, 0.3, 0, 4, 50, 80, true), Q('E', 2.5, 1, 0.4, 0, 5, 80, 125, true), Q('E', 4, 1, 0.5, 0, 7, 125, I, true),
            Q('F', 0.2, 2, 0.1, 0.1, 1, 1.6, 3, false), Q('F', 0.4, 1, 0.2, 0.1, 2, 3, 18, false), Q('F', 0.6, 2, 0.2, 0.1, 2, 10, 18, false),
            Q('F', 0.6, 2, 0.3, 0.2, 2.5, 18, 80, false), Q('F', 0.8, 1, 0.3, 0.2, 2.5, 18, 80, false), Q('F', 1, 2, 0.2, 0.1, 2.5, 18, 50, true),
            Q('F', 1, 2, 0.4, 0.3, 4, 80, I, false), Q('F', 1.2, 1, 0.2, 0.1, 2.5, 18, 50, true), Q('F', 1.2, 1, 0.4, 0.3, 4, 80, I, false),
            Q('F', 1.6, 1, 0.3, 0.2, 4, 50, 80, true), Q('F', 2.5, 1, 0.4, 0.3, 5, 80, 125, true), Q('F', 4, 1, 0.5, 0.3, 7, 125, I, true),
            Q('G', 0.4, 1, 0.2, 0.2, 0, 3, 18, false),
            Q('H', 0.8, 1, 0.3, 0.05, 0, 18, 80, false), Q('H', 1.2, 1, 0.3, 0.05, 0, 18, 50, true),
        };

        // Flank angles (deg): shaft side and wall side (wall flank leans from the wall plane; E has no wall cut)
        public static double ShaftAngle(char form) { return form == 'G' ? 17.5 : 15; }
        public static double WallAngle(char form) { return form == 'E' ? 0 : form == 'F' ? 8 : form == 'G' ? 17.5 : 15; }

        // Rows of one form that fit the diameter (all rows of the form when d1mm <= 0 or nothing fits); the default first
        public static List<Din509Row> For(char form, double d1mm)
        {
            var all = Rows.Where(r => r.Form == form).ToList();
            var fit = d1mm > 0 ? all.Where(r => r.Fits(d1mm)).ToList() : all;
            if (fit.Count == 0) fit = all;
            return fit.OrderBy(r => r.Higher).ThenBy(r => r.Series).ThenBy(r => r.R).ThenBy(r => r.T1).ToList();
        }

        // Profile in the half-plane through the shaft axis. a = along the shaft from the shoulder wall, r = distance from the axis.
        // R = shaft radius, rr = undercut radius, t1 = depth, t2 = recess into the wall, f = width (F / E; G / H derive it). Metres.
        // Returns segments { a1, r1, a2, r2, ca, cr } (NaN centre = line) of the closed cutting profile; fOut = the width the profile has at the shaft surface.
        public static List<double[]> Profile(char form, double R, double rr, double t1, double t2, double f, out double fOut)
        {
            double al = ShaftAngle(form) * Math.PI / 180, th = WallAngle(form) * Math.PI / 180;
            double cosT = Math.Cos(th), sinT = Math.Sin(th), cosA = Math.Cos(al), sinA = Math.Sin(al);
            double yc = t1 - rr;                                   // depth of the arc centres (above the groove floor)
            double sa = rr * cosT - (form == 'E' ? 0 : t2);        // corner arc centre, along the shaft
            bool single = form == 'G' || form == 'H';              // one arc from wall flank to shaft flank
            double sc = single ? sa : f - (yc * cosA + rr) / sinA; // centre of the arc at the shaft flank
            if (sc < sa - 1e-9) throw new Exception("width f is too small for this radius and depth");
            double pS = sa - rr * cosT, pY = yc + rr * sinT;       // where the wall flank leaves the corner arc
            double qS = sc + rr * sinA, qY = yc + rr * cosA;       // where the shaft flank leaves its arc
            fOut = qS + qY / Math.Tan(al);                         // flank meets the shaft surface (y = 0)
            double yTop, wTopS = 0;
            if (form == 'E') yTop = -0.5 * (rr - t1);
            else { yTop = pY - t2 / Math.Tan(th) - 1e-4; wTopS = pS + (pY - yTop) * Math.Tan(th); }   // 0.1 mm above where the wall flank leaves the wall plane
            double fTopS = qS + (qY - yTop) / Math.Tan(al);
            Func<double, double> rad = y => R - y;
            var seg = new List<double[]>();
            double n = double.NaN;
            double[] start;
            if (form == 'E') { start = new[] { sa - Math.Sqrt(rr * rr - (yTop - yc) * (yTop - yc)), rad(yTop) }; }
            else { start = new[] { wTopS, rad(yTop) }; seg.Add(new[] { wTopS, rad(yTop), pS, rad(pY), n, n }); }
            if (single) seg.Add(new[] { pS, rad(pY), qS, rad(qY), sa, rad(yc) });
            else
            {
                double sp = form == 'E' ? start[0] : pS, rp = form == 'E' ? start[1] : rad(pY);
                seg.Add(new[] { sp, rp, sa, rad(t1), sa, rad(yc) });
                if (sc > sa + 1e-9) seg.Add(new[] { sa, rad(t1), sc, rad(t1), n, n });
                seg.Add(new[] { sc, rad(t1), qS, rad(qY), sc, rad(yc) });
            }
            seg.Add(new[] { qS, rad(qY), fTopS, rad(yTop), n, n });
            seg.Add(new[] { fTopS, rad(yTop), start[0], start[1], n, n });
            return seg;
        }
    }
}