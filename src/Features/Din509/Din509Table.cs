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
        // R = shaft (or bore) radius, bore = undercut in a bore, rr = undercut radius, t1 = depth, t2 = recess into the wall (to the deepest point of
        // the corner arc), f = width (E / F; G / H derive it). Metres. Wall flank (F G H) is tangent to the corner arc; the shaft flank is a straight
        // line to the shaft surface. Returns segments { a1, r1, a2, r2, ca, cr } (NaN centre = line) of the closed cutting profile; fOut = the width at the surface.
        public static List<double[]> Profile(char form, double R, double rr, double t1, double t2, double f, bool bore, out double fOut)
        {
            if (form == 'E') t2 = 0;                               // E has no wall recess (a stored default must not leak in)
            double al = ShaftAngle(form) * Math.PI / 180, th = WallAngle(form) * Math.PI / 180;
            double cosT = Math.Cos(th), sinT = Math.Sin(th), cosA = Math.Cos(al), sinA = Math.Sin(al);
            double yc = t1 - rr;                                   // depth of the arc centres (above the groove floor)
            double sa = rr - t2;                                   // corner arc centre, along the shaft (t2 = deepest point of the arc in the wall)
            bool single = form == 'G' || form == 'H';              // one arc from wall flank to shaft flank
            double sc = single ? sa : f - (yc * cosA + rr) / sinA; // centre of the arc at the shaft flank
            if (sc < sa - 1e-9) throw new Exception("width f is too small for this radius and depth");
            double pS = sa - rr * cosT, pY = yc - rr * sinT;       // where the wall flank is tangent to the corner arc (E: on the wall plane)
            if (form != 'E' && pS >= 0) throw new Exception("t2 is too small for this wall angle");
            double qS = sc + rr * sinA, qY = yc + rr * cosA;       // where the shaft flank leaves its arc
            fOut = qS + qY / Math.Tan(al);                         // flank meets the shaft surface (y = 0)
            const double h = 1e-4;                                 // the flank runs 0.1 mm past the surface (a cut face ending exactly on the cylinder breaks the boolean)
            double ex = qS + (qY + h) / Math.Tan(al);
            double tlS, tlY;                                       // top of the profile, in air
            if (form == 'E') { tlY = 0.5 * yc; tlS = sa - Math.Sqrt(rr * rr - (tlY - yc) * (tlY - yc)); }   // E: stay off the wall plane, Fill() closes the corner
            else { tlY = pY - (-pS) / Math.Tan(th) - h; tlS = pS + (pY - tlY) * Math.Tan(th); }             // 0.1 mm above where the wall flank leaves the wall plane
            Func<double, double> rad = y => bore ? R + y : R - y;   // bore: the groove goes outward from the hole wall
            var seg = new List<double[]>();
            double n = double.NaN;
            if (form != 'E') seg.Add(new[] { tlS, rad(tlY), pS, rad(pY), n, n });
            double sp = form == 'E' ? tlS : pS, yp = form == 'E' ? tlY : pY;
            if (single) seg.Add(new[] { sp, rad(yp), qS, rad(qY), sa, rad(yc) });
            else
            {
                seg.Add(new[] { sp, rad(yp), sa, rad(t1), sa, rad(yc) });
                if (sc > sa + 1e-9) seg.Add(new[] { sa, rad(t1), sc, rad(t1), n, n });
                seg.Add(new[] { sc, rad(t1), qS, rad(qY), sc, rad(yc) });
            }
            seg.Add(new[] { qS, rad(qY), ex, rad(-h), n, n });
            seg.Add(new[] { ex, rad(-h), tlS, rad(tlY), n, n });
            foreach (var s in seg) if (s[1] <= 1e-6 || s[3] <= 1e-6) throw new Exception("the bore is too small for this undercut");
            return seg;
        }

        // y (below the shaft surface, so negative = above it) of the circle where the undercut leaves the shoulder wall: E at the tangent point, F G H where the wall flank leaves the wall plane
        public static double WallDepth(char form, double rr, double t1, double t2)
        {
            if (form == 'E') return t1 - rr;
            double th = WallAngle(form) * Math.PI / 180, yc = t1 - rr, sa = rr - t2;
            return yc - rr * Math.Sin(th) - (rr * Math.Cos(th) - sa) / Math.Tan(th);
        }

        // Material that closes the corner between the shoulder wall and the corner arc when the arc meets the wall plane above the shaft
        // surface (E always; H): the wall then runs on into the arc. Null when there is nothing to fill. Added after the cut; it reaches
        // 0.1 mm into the collar and 0.2 mm into the shaft so the union overlaps.
        public static List<double[]> Fill(char form, double R, double rr, double t1, double t2, bool bore)
        {
            if (form == 'E') t2 = 0;
            double yc = t1 - rr, sa = rr - t2;
            double yw = yc + Math.Sqrt(Math.Max(0, rr * rr - sa * sa));   // the arc meets the wall plane at this depth
            if (yw >= -1e-9) return null;
            double s0 = sa - Math.Sqrt(rr * rr - yc * yc);                // the arc crosses the shaft surface here
            const double d = 1e-4, a = 2e-4;
            Func<double, double> rad = y => bore ? R + y : R - y;
            double n = double.NaN;
            var seg = new List<double[]>
            {
                new[] { -d, rad(yw), 0, rad(yw), n, n },
                new[] { 0, rad(yw), s0, rad(0), sa, rad(yc) },
                new[] { s0, rad(0), s0, rad(a), n, n },
                new[] { s0, rad(a), -d, rad(a), n, n },
                new[] { -d, rad(a), -d, rad(yw), n, n },
            };
            foreach (var s in seg) if (s[1] <= 1e-6 || s[3] <= 1e-6) throw new Exception("the bore is too small for this undercut");
            return seg;
        }
    }
}
