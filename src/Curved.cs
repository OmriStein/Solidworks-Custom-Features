using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MfgChamfer
{
    // Circular edges: a planar face square to the circle's axis and a coaxial cylinder or cone (hole / boss rims, pocket
    // corner radii). The mill's axis is the circle's axis and the mill follows the circle, so the chamfer is a cone and
    // the tool is the chamfer's cross-section triangle revolved about that axis (full circle, or the arc's sector).
    public static partial class Engine
    {
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

        // Selected edges that continue each other tangentially (a pocket outline of lines and arcs): the tools of
        // such neighbours meet exactly at the shared end instead of running on past it.
        static void FindJoins(IList<IEdge> edges, BlendInfo[] res)
        {
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++)
                {
                    try
                    {
                        var ei = Ends(edges[i]); var ej = Ends(edges[j]);
                        if (Dist(ei[0], ei[1]) < 1e-9 || Dist(ej[0], ej[1]) < 1e-9) continue;   // closed edges have no ends
                        for (int a = 0; a < 2; a++)
                            for (int b = 0; b < 2; b++)
                            {
                                if (Dist(ei[a], ej[b]) > 1e-8) continue;
                                var ti = TangentAt(edges[i], ei[a]); var tj = TangentAt(edges[j], ej[b]);
                                if (ti == null || tj == null || Math.Abs(Dot(ti, tj)) < 0.9999 || !SharesFace(edges[i], edges[j])) continue;
                                res[i].Join[a] = true; res[j].Join[b] = true;
                            }
                    }
                    catch { }
                }
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
                            if (tn == null || Math.Abs(Dot(tc, tn)) < 0.9999 || !SharesFace(cur, n)) continue;
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
        static bool SameEdge(IEdge a, IEdge b)
        {
            var ea = Ends(a); var eb = Ends(b);
            bool ends = (Dist(ea[0], eb[0]) < 1e-8 && Dist(ea[1], eb[1]) < 1e-8) || (Dist(ea[0], eb[1]) < 1e-8 && Dist(ea[1], eb[0]) < 1e-8);
            if (!ends) return false;
            var ca = (ICurve)a.GetCurve(); var cb = (ICurve)b.GetCurve();
            if (ca.IsLine() != cb.IsLine()) return false;
            return ca.IsLine() || Math.Abs(((double[])ca.CircleParams)[6] - ((double[])cb.CircleParams)[6]) < 1e-8;
        }

        static double[] TangentAt(IEdge e, double[] V)
        {
            var c = (ICurve)e.GetCurve();
            if (c.IsLine()) { var q = Ends(e); return Norm(Sub(q[1], q[0])); }
            if (!c.IsCircle()) return null;
            var cp = (double[])c.CircleParams;
            return Norm(Cross(new[] { cp[3], cp[4], cp[5] }, Sub(V, new[] { cp[0], cp[1], cp[2] })));
        }

        static bool SharesFace(IEdge a, IEdge b)
        {
            var fa = (object[])a.GetTwoAdjacentFaces2(); var fb = (object[])b.GetTwoAdjacentFaces2();
            foreach (IFace2 x in fa) foreach (IFace2 y in fb) if (x.IsSame(y)) return true;
            return false;
        }

        static List<IBody2> CircleTools(IEdge edge, ChamferParams p, IModeler modeler, IMathUtility math,
                                        double[] expected, List<double[][]> lines = null, BlendInfo blend = null, bool flip = false)
        {
            if (blend != null && blend.Axis != null) flip = false;   // blended: the mill comes from the shared face
            var cp = (double[])((ICurve)edge.GetCurve()).CircleParams;   // centre xyz, axis xyz, radius
            var c = new[] { cp[0], cp[1], cp[2] };
            var ax = Norm(new[] { cp[3], cp[4], cp[5] });
            double R = cp[6];
            var ends = Ends(edge);
            var faces = ((object[])edge.GetTwoAdjacentFaces2()).Cast<IFace2>().ToArray();
            var fp = faces.FirstOrDefault(f => ((ISurface)f.GetSurface()).IsPlane());
            var fq = faces.FirstOrDefault(f => !((ISurface)f.GetSurface()).IsPlane());
            if (fp == null || fq == null) throw new Exception("a circular edge needs one planar face and one cylinder or cone");
            var nP = PlaneNormal(fp);
            if (Math.Abs(Dot(nP, ax)) < 0.9999) throw new Exception("the planar face must be square to the axis of the circular edge");
            var qs = (ISurface)fq.GetSurface();
            double[] qp;
            if (qs.IsCylinder()) qp = (double[])qs.CylinderParams;
            else if (qs.IsCone()) qp = (double[])qs.ConeParams;
            else throw new Exception("a circular edge needs a cylinder or cone around the same axis");
            var qa = Norm(new[] { qp[3], qp[4], qp[5] });
            if (Math.Abs(Dot(qa, ax)) < 0.9999 || Len(Cross(Sub(c, new[] { qp[0], qp[1], qp[2] }), qa)) > 1e-6)
                throw new Exception("the cylinder or cone is not coaxial with the circular edge");

            var up = nP;                                        // out of the planar face = along the mill axis, backwards
            var q = ends[0];
            var rq = Sub(Sub(q, c), Scale(up, Dot(Sub(q, c), up)));
            rq = Norm(rq);
            var yq = Cross(up, rq);                             // tangent of the circle at q
            // Probe the faces at the middle of an arc: at an end vertex a probe along the edge's neighbour lies on that neighbour
            var qm = q; var ym = yq;
            if (Dist(ends[0], ends[1]) > 1e-9)
            {
                var bs = Add(Norm(Sub(q, c)), Norm(Sub(ends[1], c)));
                var md = Len(bs) > 1e-6 ? Norm(bs) : yq;
                foreach (var sg in new[] { 1.0, -1.0 })
                {
                    var cd = Add(c, Scale(md, sg * R));
                    var cl = edge.GetClosestPointOn(cd[0], cd[1], cd[2]) as double[];
                    if (cl != null && Dist(cl, cd) < 1e-6) { qm = cd; ym = Norm(Cross(up, Norm(Sub(cd, c)))); break; }
                }
            }
            var nQ = FaceNormalAt(fq, qm);
            if (nQ == null) throw new Exception("cannot read the surface next to the circular edge");
            double probeD = p.Size * 0.1;
            var uP = IntoFace(fp, nP, ym, qm, probeD);
            var uQ = IntoFace(fq, nQ, ym, qm, probeD);
            if (uP == null || uQ == null || Dot(uP, nQ) > -1e-6 || Dot(uQ, nP) > -1e-6)
            {
                throw new Exception("edge is concave (inside corner) - pick outside edges");
            }

            double phi = Math.Acos(Math.Max(-1, Math.Min(1, Dot(uP, uQ))));
            double beta = p.Angle;
            if (beta > Math.PI - phi - 0.002)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "chamfer angle must be below {0:0.#} deg on this {1:0.#} deg edge",
                    (Math.PI - phi) * 180 / Math.PI - 0.1, phi * 180 / Math.PI));
            double d1 = p.Size, d2 = d1 * Math.Sin(beta) / Math.Sin(phi + beta), betaW = beta;   // betaW: chamfer angle from the wall face
            if (flip)   // Size measured on the other face (the mill's axis stays the circle's axis, so the cone angle follows the new line)
            {
                d2 = d1; d1 = d2 * Math.Sin(beta) / Math.Sin(phi + beta);
                beta = Math.Atan2(d2 * Math.Sin(phi), d1 - d2 * Math.Cos(phi));
            }
            double hB = d2 * Math.Sin(phi), rF = flip ? d1 * Math.Sin(phi) / Math.Tan(betaW) : hB / Math.Tan(beta);
            if (p.ToolDia / 2 < rF - 1e-9)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "tool diameter must be at least {0:0.###} mm for this chamfer", 2 * rF * 1000));

            // Half-plane through the axis (rho = distance from the axis, z along up, z = 0 in the planar face):
            // chamfer line from A (on the face) to B (on the cone / cylinder).
            double s = Dot(uP, rq) > 0 ? 1 : -1;               // +1: the planar face lies outside the circle (hole rim), -1: inside (boss)
            double rhoA = R + s * d1, rhoB = R + s * d2 * Math.Cos(phi), zB = -hB;
            if (rhoA < 1e-4 || rhoB < 1e-4) throw new Exception("circle too small for this chamfer");
            Func<double, double> rho = z => rhoB + (rhoA - rhoB) * (z - zB) / (0 - zB);
            double ext = Math.Max(d1, d2), zLow = zB - 1e-4, zTop = 0.5 * d1;
            while (zTop > 1e-6 && rho(zTop + 1e-4) < 1e-4) zTop *= 0.5;
            if (rho(zTop + 1e-4) < 1e-4) throw new Exception("circle too small for this chamfer");
            Func<double, double[]> at = z => Add(c, Scale(up, z));
            Func<double, double, IBody2> frustum = (z0, z1) =>
                modeler.CreateBodyFromCone(new[] { at(z0)[0], at(z0)[1], at(z0)[2], up[0], up[1], up[2], rho(z0), rho(z1), z1 - z0 }) as IBody2;
            IBody2 tool;
            if (s > 0) tool = frustum(zLow, zTop);              // hole rim: the cone itself (the bore is air already)
            else
            {
                double rMax = Math.Max(rhoA, rhoB) + ext;       // boss rim: the ring outside the cone
                var cyl = modeler.CreateBodyFromCyl(new[] { at(zLow)[0], at(zLow)[1], at(zLow)[2], up[0], up[1], up[2], rMax, zTop - zLow }) as IBody2;
                var keep = frustum(zLow - 1e-4, zTop + 1e-4);
                if (cyl == null || keep == null) throw new Exception("could not build the chamfer tool");
                int err;
                var ring = cyl.Operations2((int)swBodyOperationType_e.SWBODYCUT, keep, out err) as object[];
                if (ring == null || ring.Length == 0) throw new Exception("could not build the chamfer tool (error " + err + ")");
                tool = Largest(ring);
            }
            if (tool == null) throw new Exception("could not build the chamfer tool");

            double span = 2 * Math.PI, st0 = 0, st1 = 0, dir = 1;   // st0 / st1: how far the mill stops short of each end of an arc
            double stop = p.ToolDia / 2 + p.Clearance;
            var blk = new bool[2];
            var outD = new double[2][];
            Func<double, double[]> bAt = a => Add(Add(c, Scale(up, zB)), Add(Scale(rq, rhoB * Math.Cos(a)), Scale(yq, rhoB * Math.Sin(a))));   // mill tip at arc angle a
            if (Dist(ends[0], ends[1]) > 1e-9)                  // an arc: keep only its sector
            {
                // a point in the middle of the arc: on the bisector of its end radii, on whichever side lies on the edge
                var bis = Add(Norm(Sub(q, c)), Norm(Sub(ends[1], c)));
                var mdir = Len(bis) > 1e-6 ? Norm(bis) : yq;
                double[] m = null;
                foreach (var sgn in new[] { 1.0, -1.0 })
                {
                    var cand = Add(c, Scale(mdir, sgn * R));
                    var cl = edge.GetClosestPointOn(cand[0], cand[1], cand[2]) as double[];
                    if (cl != null && Dist(cl, cand) < 1e-6) { m = cand; break; }
                }
                if (m == null) throw new Exception("cannot find the middle of the arc");
                Func<double[], double> ang = pt => Math.Atan2(Dot(Sub(pt, c), yq), Dot(Sub(pt, c), rq));
                dir = ang(m) >= 0 ? 1 : -1;
                double aE = ang(ends[1]);
                if (dir > 0 && aE <= 0) aE += 2 * Math.PI;
                if (dir < 0 && aE >= 0) aE -= 2 * Math.PI;
                span = Math.Abs(aE);

                // Ends that run into a wall: the mill stops tool radius + clearance short of it and leaves with a cone
                for (int k = 0; k < 2; k++)
                {
                    if (blend != null && (blend.Join[k] || blend.End[k])) continue;   // continues into the next selected edge / ends in a blend corner
                    var vtx = ends[k];
                    var rv = Norm(Sub(Sub(vtx, c), Scale(up, Dot(Sub(vtx, c), up))));
                    var tv = Scale(Cross(up, rv), dir);             // direction of travel along the arc at this end
                    var od = k == 0 ? Scale(tv, -1) : tv;           // away from the arc
                    var nv = FaceNormalAt(fq, vtx);
                    var wn = nv == null ? null : Blocker((IVertex)(k == 0 ? edge.GetStartVertex() : edge.GetEndVertex()), od, nP, nv);
                    if (wn == null)
                    {
                        // Tangent run-on into an unselected face (e.g. arc into a flat wall): finish like a blocked end,
                        // as if a wall stood square to the arc at the vertex
                        try
                        {
                            foreach (IFace2 tf in (object[])((IVertex)(k == 0 ? edge.GetStartVertex() : edge.GetEndVertex())).GetAdjacentFaces())
                            {
                                if (Math.Abs(tf.GetArea() - fq.GetArea()) < 1e-12) continue;   // the arc's own face (same area)
                                var tn = FaceNormalAt(tf, vtx);
                                if (tn != null && Dot(tn, nv) > 0.999 && Math.Abs(Dot(tn, nP)) < 0.01) { wn = Scale(od, -1); break; }
                            }
                        }
                        catch { }
                    }
                    if (wn == null) continue;
                    int kk = k;
                    Func<double, double> D = u => Dot(Sub(bAt(dir * (kk == 0 ? u : span - u)), vtx), wn);   // clearance of the mill axis from the wall after walking u inwards
                    double u0 = -1, prev = 0;
                    const int steps = 720;
                    for (int i = 1; i <= steps && u0 < 0; i++)
                    {
                        double cur = span * i / steps;
                        if (D(cur) >= stop)
                        {
                            double lo = prev, hi = cur;
                            for (int it = 0; it < 40; it++) { double mid2 = 0.5 * (lo + hi); if (D(mid2) >= stop) hi = mid2; else lo = mid2; }
                            u0 = hi;
                        }
                        prev = cur;
                    }
                    if (u0 < 0) throw new Exception(string.Format(CultureInfo.InvariantCulture, "arc too short for a {0:0.###} mm tool between walls", p.ToolDia * 1000));
                    if (k == 0) st0 = u0; else st1 = u0;
                    blk[k] = true; outD[k] = od;
                }
                if (st0 + st1 > span - 1e-4)
                    throw new Exception(string.Format(CultureInfo.InvariantCulture, "arc too short for a {0:0.###} mm tool between walls", p.ToolDia * 1000));

                double rf = (Math.Max(Math.Max(rhoA, rhoB), R) + ext) * 1.5;
                double sp2 = span - st0 - st1;
                int n = (int)Math.Ceiling(sp2 / (Math.PI / 3));
                var o = at(zLow - 1e-3);
                var pts = new List<double[]> { o };
                for (int k = 0; k <= n; k++)
                {
                    double a = dir * (st0 + sp2 * k / n);
                    pts.Add(Add(o, Add(Scale(rq, rf * Math.Cos(a)), Scale(yq, rf * Math.Sin(a)))));
                }
                var segs = new List<Curve>();
                for (int k = 0; k < pts.Count; k++) segs.Add(Seg(modeler, pts[k], pts[(k + 1) % pts.Count]));
                var sheet = ((ISurface)modeler.CreatePlanarSurface2(o, up, rq)).CreateTrimmedSheet4(segs.ToArray(), true) as IBody2;
                if (sheet == null) throw new Exception("could not build the arc sector");
                var sector = modeler.CreateExtrudedBody((Body2)sheet, (MathVector)math.CreateVector(up), zTop - zLow + 2e-3) as IBody2;
                int err;
                var res = tool.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, sector, out err) as object[];
                if (res == null || res.Length == 0) throw new Exception("could not cut the tool to the arc (error " + err + ")");
                tool = Largest(res);
            }
            double sp = span - st0 - st1;
            var list = new List<IBody2> { tool };
            for (int k = 0; k < 2; k++)
                if (blk[k])   // reversed: the mill comes in square to the wall (5-axis), so its runout cone has a radial axis
                {
                    if (!flip) { list.Add(Cone(bAt(dir * (k == 0 ? st0 : span - st1)), Scale(up, -1), hB, beta, stop, modeler)); continue; }
                    double a = dir * (k == 0 ? st0 : span - st1);
                    var rad = Add(Scale(rq, Math.Cos(a)), Scale(yq, Math.Sin(a)));
                    list.Add(Cone(Add(c, Scale(rad, rhoA)), Scale(rad, s), d1 * Math.Sin(phi), betaW, stop, modeler));
                }
            // Pappus: triangle area x path length of its centroid; plus (an upper bound for) each runout cone
            double cot = 1 / Math.Tan(flip ? betaW : beta), hC = flip ? d1 * Math.Sin(phi) : hB;
            if (expected != null)
                expected[0] = 0.5 * d1 * d2 * Math.Sin(phi) * (R + rhoA + rhoB) / 3 * sp
                            + ((blk[0] ? 1 : 0) + (blk[1] ? 1 : 0)) * Math.PI * hC * hC * hC * cot * cot / 3;
            if (lines != null)
            {
                bool full = span > 2 * Math.PI - 1e-6;
                int gens = (int)Math.Max(2, Math.Ceiling(sp / (Math.PI / 6)));
                for (int k = 0; k <= gens; k++)
                {
                    if (full && k == gens) break;
                    double a = dir * (st0 + sp * k / gens);
                    lines.Add(new[] { bAt(a), Add(c, Add(Scale(rq, rhoA * Math.Cos(a)), Scale(yq, rhoA * Math.Sin(a)))) });
                }
                for (int k = 0; k < 2; k++)
                {
                    if (!blk[k]) continue;
                    double ak = dir * (k == 0 ? st0 : span - st1);
                    var radk = Add(Scale(rq, Math.Cos(ak)), Scale(yq, Math.Sin(ak)));
                    var apex = flip ? Add(c, Scale(radk, rhoA)) : bAt(ak);
                    var axk = flip ? Scale(radk, -s) : up;   // base side: opposite the axis passed to Cone
                    var zc = Add(apex, Scale(axk, flip ? d1 * Math.Sin(phi) : hB));   // the cone's base circle
                    var perp = Cross(axk, outD[k]);
                    Func<double, double[]> rim = th => Add(zc, Add(Scale(outD[k], rF * Math.Cos(th)), Scale(perp, rF * Math.Sin(th))));
                    const int segs = 16;
                    for (int i = 0; i < segs; i++)
                        lines.Add(new[] { rim(-Math.PI / 2 + Math.PI * i / segs), rim(-Math.PI / 2 + Math.PI * (i + 1) / segs) });
                    for (int i = 0; i <= 4; i++)
                        lines.Add(new[] { apex, rim(-Math.PI / 2 + Math.PI * i / 4) });
                }
            }
            return list;
        }
    }
}
