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
    // Geometry helpers shared by all features: vector math, face / edge queries, tool-body builders
    public static class Geo
    {
        public const double Tol = 1e-7;

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

        public static double Volume(IBody2 b)
        {
            var mp = b.GetMassProperties(1) as double[];
            return mp != null ? mp[3] : 0;
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

        // Unit direction from the edge into face f (perpendicular to the edge direction t): the side that lies on the face
        public static double[] IntoFace(IFace2 f, double[] n, double[] t, double[] mid, double d)
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

        // Like AddClipped, but the part is given as a test (outside(x) = the point is not in the part); the stroke is cut where it crosses the boundary
        public static void AddClippedFn(List<double[][]> lines, double[] a, double[] b, Func<double[], bool> outside)
        {
            const int n = 16;
            Func<int, double[]> P = i => Add(a, Scale(Sub(b, a), (double)i / n));
            double[] start = outside(P(0)) ? null : P(0);
            for (int i = 1; i <= n; i++)
            {
                var p0 = P(i - 1); var p1 = P(i);
                bool o1 = outside(p1);
                if (start != null && o1)
                {
                    double lo = 0, hi = 1;
                    for (int it = 0; it < 12; it++) { double m = 0.5 * (lo + hi); if (outside(Add(p0, Scale(Sub(p1, p0), m)))) hi = m; else lo = m; }
                    lines.Add(new[] { start, Add(p0, Scale(Sub(p1, p0), lo)) });
                    start = null;
                }
                else if (start == null && !o1)
                {
                    double lo = 0, hi = 1;
                    for (int it = 0; it < 12; it++) { double m = 0.5 * (lo + hi); if (outside(Add(p0, Scale(Sub(p1, p0), m)))) lo = m; else hi = m; }
                    start = Add(p0, Scale(Sub(p1, p0), hi));
                }
            }
            if (start != null) lines.Add(new[] { start, b });
        }

        // A preview stroke, cut off where it would leave the part (beyond the plane of the other face: s = a point on that plane, n = its outward normal)
        public static void AddClipped(List<double[][]> lines, double[] a, double[] b, double[] s, double[] n)
        {
            const double eps = 1e-7;
            double da = Dot(Sub(a, s), n), db = Dot(Sub(b, s), n);   // > 0: outside the part
            if (da > eps && db > eps) return;
            if (da > eps || db > eps)
            {
                double u = da / (da - db);
                var m = Add(a, Scale(Sub(b, a), u));
                if (da > eps) a = m; else b = m;
            }
            lines.Add(new[] { a, b });
        }

        public static double[] PlaneNormal(IFace2 f)
        {
            var s = (ISurface)f.GetSurface();
            if (!s.IsPlane()) throw new Exception("both faces at the edge must be planar");
            return Norm((double[])f.Normal);
        }

        public static double[] FaceNormalAt(IFace2 f, double[] pt)
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
        public static IBody2 PrismSheared(double[] o, double[] n, double[] t, double len, double[] a, double[] b, double[] c, double[] refDir,
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

        public static IBody2 Prism(double[] o, double[] t, double len, double[] a, double[] b, double[] c, double[] refDir,
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

        // Points along an edge, from its own start to end point (the curve's raw parameter range is not reliable
        // on temporary bodies, which gave stray strokes).
        public static List<double[]> EdgePoints(IEdge e)
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

        public static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        public static Curve Segment(IModeler m, double[] a, double[] b) { return Seg(m, a, b); }

        public static double Dist(double[] a, double[] b) { return Len(Sub(a, b)); }

        public static Curve Seg(IModeler m, double[] a, double[] b)
        {
            var line = (ICurve)m.CreateLine(a, Sub(b, a));
            return line.CreateTrimmedCurve2(a[0], a[1], a[2], b[0], b[1], b[2]);
        }

        // Solid of revolution (360 deg) from a closed profile in the half-plane through the axis (origin o, direction ax, radial direction rd).
        // seg = { a1, r1, a2, r2, ca, cr }: a = along the axis, r = distance from it; ca/cr = arc centre (NaN: straight line).
        // Each segment is revolved into a sheet (line -> plane / cylinder / cone, arc -> torus) and the sheets are knitted into a solid.
        public static IBody2 Revolve(IModeler m, double[] o, double[] ax, double[] rd, IList<double[]> segs)
        {
            Func<double, double, double[]> P = (a, r) => Add(o, Add(Scale(ax, a), Scale(rd, r)));
            var sheets = new List<Body2>();
            foreach (var s in segs)
            {
                var p = P(s[0], s[1]); var q = P(s[2], s[3]);
                ICurve c;
                if (double.IsNaN(s[4])) c = ((ICurve)m.CreateLine(p, Sub(q, p))).CreateTrimmedCurve2(p[0], p[1], p[2], q[0], q[1], q[2]);
                else
                {
                    var cc = P(s[4], s[5]);
                    double cross = (s[0] - s[4]) * (s[3] - s[5]) - (s[1] - s[5]) * (s[2] - s[4]);   // sense of the arc in the (a, r) plane
                    var nrm = Cross(ax, rd); if (cross < 0) nrm = Scale(nrm, -1);
                    c = ((ICurve)m.CreateArc(cc, nrm, Dist(cc, p), p, q)).CreateTrimmedCurve2(p[0], p[1], p[2], q[0], q[1], q[2]);
                }
                double u0, u1; bool closed, periodic;
                c.GetEndParams(out u0, out u1, out closed, out periodic);
                var surf = (ISurface)m.CreateRevolutionSurface(c, o, ax, new[] { u0, u1 });
                var sheet = surf == null ? null : m.CreateSheetFromSurface(surf, new[] { u0, u1, 0, 2 * Math.PI }) as Body2;
                if (sheet == null) throw new Exception("could not revolve the undercut profile");
                sheets.Add(sheet);
            }
            int err = 0;
            var res = m.CreateBodiesFromSheets2(sheets.ToArray(), (int)swSheetSewingOption_e.swSewToSolid, 1e-6, ref err) as object[];
            if (res == null || res.Length != 1 || ((IBody2)res[0]).GetType() != (int)swBodyType_e.swSolidBody) throw new Exception("could not close the undercut profile (sewing error " + err + ")");
            return (IBody2)res[0];
        }
        // Chamfer-mill footprint: cone with its tip (apex) on the chamfer's bottom edge, axis normal to the mill's face,
        // hB deep below that face; half angle = 90 deg - chamfer angle.
        public static IBody2 Cone(double[] apex, double[] axis, double hB, double beta, double stop, IModeler modeler)
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

        public static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }

        public static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }

        public static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }

        public static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }

        public static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }

        public static double[] Norm(double[] a) { return Scale(a, 1 / Len(a)); }

        // Start and end point of an edge (the same point twice for a closed edge)
        public static double[][] Ends(IEdge e)
        {
            var v0 = e.GetStartVertex() as IVertex;
            var v1 = e.GetEndVertex() as IVertex;
            if (v0 != null && v1 != null) return new[] { (double[])v0.GetPoint(), (double[])v1.GetPoint() };
            var cp = e.GetCurveParams3();
            var a = (double[])((ICurve)e.GetCurve()).Evaluate2(cp.UMinValue, 0);
            var q = new[] { a[0], a[1], a[2] };
            return new[] { q, q };
        }

        // Edges that continue start tangentially through shared vertices (lines and arcs), the whole chain, not start itself
        public static List<IEdge> TangentChain(IEdge start)
        {
            var chain = new List<IEdge> { start };
            for (int i = 0; i < chain.Count && chain.Count < 200; i++)
            {
                var cur = chain[i];
                foreach (var v in new[] { cur.GetStartVertex() as IVertex, cur.GetEndVertex() as IVertex })
                {
                    if (v == null) continue;
                    try
                    {
                        var pt = (double[])v.GetPoint();
                        var tc = TangentAt(cur, pt);
                        if (tc == null) continue;
                        foreach (IEdge n in (object[])v.GetEdges())
                        {
                            if (chain.Any(x => SameEdge(x, n))) continue;
                            var tn = TangentAt(n, pt);
                            if (tn == null || Math.Abs(Dot(tc, tn)) < 0.9999 || !TangentFaces(cur, n, pt)) continue;
                            chain.Add(n);
                        }
                    }
                    catch { }
                }
            }
            chain.RemoveAt(0);
            return chain;
        }

        // Same edge: same end points and same kind of curve (an arc and its complement differ in radius/line-ness)
        public static bool SameEdge(IEdge a, IEdge b)
        {
            var ea = Ends(a); var eb = Ends(b);
            bool ends = (Dist(ea[0], eb[0]) < 1e-8 && Dist(ea[1], eb[1]) < 1e-8) || (Dist(ea[0], eb[1]) < 1e-8 && Dist(ea[1], eb[0]) < 1e-8);
            if (!ends) return false;
            var ca = (ICurve)a.GetCurve(); var cb = (ICurve)b.GetCurve();
            if (ca.IsLine() != cb.IsLine()) return false;
            return ca.IsLine() || Math.Abs(((double[])ca.CircleParams)[6] - ((double[])cb.CircleParams)[6]) < 1e-8;
        }

        public static double[] TangentAt(IEdge e, double[] V)
        {
            var c = (ICurve)e.GetCurve();
            if (c.IsLine()) { var q = Ends(e); return Norm(Sub(q[1], q[0])); }
            if (!c.IsCircle()) return null;
            var cp = (double[])c.CircleParams;
            return Norm(Cross(new[] { cp[3], cp[4], cp[5] }, Sub(V, new[] { cp[0], cp[1], cp[2] })));
        }

        // Native-style propagation: the edges share a face and their other faces run tangentially into each other at pt
        // (same rule that keeps a straight edge from dragging in the collinear edge of a different wall).
        public static bool TangentFaces(IEdge a, IEdge b, double[] pt)
        {
            try
            {
                var fa = ((object[])a.GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
                var fb = ((object[])b.GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
                Func<IFace2, double[]> nrm = f => ((ISurface)f.GetSurface()).IsPlane() ? PlaneNormal(f) : FaceNormalAt(f, pt);
                for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++)
                    if (fa[x].IsSame(fb[y]))
                    {
                        IFace2 oa = fa[1 - x], ob = fb[1 - y];
                        if (oa.IsSame(ob) || Dot(nrm(oa), nrm(ob)) > 0.9999) return true;
                    }
            }
            catch { }
            return false;
        }

        public static bool SharesFace(IEdge a, IEdge b)
        {
            var fa = (object[])a.GetTwoAdjacentFaces2(); var fb = (object[])b.GetTwoAdjacentFaces2();
            foreach (IFace2 x in fa) foreach (IFace2 y in fb) if (x.IsSame(y)) return true;
            return false;
        }
    }
}
