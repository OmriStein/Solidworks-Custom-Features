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
    // CNC Chamfer: planning and cutting the chamfer tools (straight edges, corner blends, flat corners)
    public static partial class Engine
    {
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
        public class Corner { public double[] V, DirA, DirB, NA, NB, V2; public bool Shifted; public double Gamma { get { return Math.Acos(Math.Max(-1, Math.Min(1, Dot(DirA, DirB)))); } } }   // Gamma: angle between the two edges   // DirA/DirB: from V into the material, along each edge's line

        public class BlendInfo
        {
            public double[][][] ConeGeom = new double[2][][];             // rim blends: {rim wall normal, the two wall normals of the rising edge} (clips the corner cone's preview to the part)
            public double[] EndShift = new double[2];                   // how far from that end the mill axis stops: V itself, or the turning point V2 with a tip offset
            public bool[] End = new bool[2];                     // start / end vertex is a blended corner
            public double[] ConeWall;                            // normal of the wall the blended rim runs into: a mill working from that wall is blocked by the floor instead
            public double[][] ConeAxis = new double[2][];
            public double[][] ConeDown = new double[2][];   // the way into the material below the rim's floor at that end (clips the corner cone to a quarter)        // arc ends: axis (into the material) of the corner cone, normal to the plane of both edges at the vertex
            public bool Perp;   // part of a rim/rising-edge corner blend: Reverse still applies to it

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
                                    res[a].Axis = axc; res[a].End[endA] = true; res[b].End[endB] = true; res[a].Perp = true; res[b].Perp = true; res[b].ConeDown[endB] = axc;
                                    // the corner cone is the mill that works from the plane both edges leave V in (B arc or leaning line)
                                    {
                                        var cx = Cross(nCp, tA);   // square to the rim and in the floor plane: with a leaning B this stays horizontal like the wall's own corner cut
                                        var uC = Len(cx) > 1e-6 ? IntoFace(fa[k], nCp, tA, V, 1e-4) : null;
                                        if (uC != null) { cx = Norm(cx); res[b].ConeAxis[endB] = Dot(cx, uC) >= 0 ? cx : Scale(cx, -1); }
                                    }
                                    // the straight edge's mill stops with its centre at V: its cone (axis square to the wall) closes the chamfer there
                                    res[b].ConeEnd[endB] = true; res[b].ConeWall = fb.Select(wallN).FirstOrDefault(nW => nW != null && Math.Abs(Dot(nCp, nW)) < 0.02 && Dot(nW, tA) > 0.02);
                                    res[b].ConeGeom[endB] = new[] { nAW, wallN(fb[0]), wallN(fb[1]) };
                                    if (p.TipOffset > 0) throw new Exception("tip offset is not available at a corner where an arc runs into a wall - set it to 0 or deselect that corner");
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
                        var corner = new Corner { V = V, DirA = Scale(ti, -1), DirB = Scale(tj, -1), NA = nA, NB = nB, V2 = V };   // NA / NB: outward normals of the two walls (in the plane of C)
                        if (li && lj && p.TipOffset > 0 && 1 + Dot(nA, nB) > 0.05)
                        {   // with a tip offset the mill axis stays offset/tan(angle) outside each wall: it turns the corner at V2, outside both
                            double kk = p.TipOffset / Math.Tan(p.Angle) / (1 + Dot(nA, nB));
                            corner.V2 = Add(V, Scale(Add(nA, nB), kk)); corner.Shifted = true;
                            res[i].EndShift[ai] = kk * Dot(nB, ti); res[j].EndShift[aj] = kk * Dot(nA, tj);
                        }
                        if (!corner.Shifted && li != lj && p.TipOffset > 0)
                        {   // an arc and a straight edge: the offset circle of the arc meets the offset line of the straight edge at V2
                            double del = p.TipOffset / Math.Tan(p.Angle);
                            int ia = li ? j : i, il = li ? i : j, ea = li ? aj : ai, el = li ? ai : aj;
                            var cpA = (double[])((ICurve)edges[ia].GetCurve()).CircleParams;
                            var cA = new[] { cpA[0], cpA[1], cpA[2] }; double Ra = cpA[6];
                            var nLine = li ? nA : nB; var nArc = li ? nB : nA; var tLine = li ? ti : tj;
                            var radV = Norm(Sub(Norm(Sub(V, cA)), Scale(nC, Dot(Norm(Sub(V, cA)), nC))));
                            double rhoT = Ra + del * Dot(nArc, radV);
                            var w0 = Sub(Add(V, Scale(nLine, del)), cA);
                            double bq = Dot(w0, tLine), disc = bq * bq - (Dot(w0, w0) - rhoT * rhoT);
                            if (disc >= 0 && rhoT > 1e-6)
                            {
                                double r1 = -bq + Math.Sqrt(disc), r2 = -bq - Math.Sqrt(disc), lam = Math.Abs(r1) < Math.Abs(r2) ? r1 : r2;
                                if (lam > 1e-9)
                                {
                                    var vp = Add(Add(V, Scale(nLine, del)), Scale(tLine, lam));
                                    double ang = Math.Acos(Math.Max(-1, Math.Min(1, Dot(Norm(Sub(V, cA)), Norm(Sub(vp, cA))))));
                                    corner.V2 = vp; corner.Shifted = true;
                                    res[il].EndShift[el] = lam; res[ia].EndShift[ea] = ang;   // straight edge: distance along it, arc: angle
                                }
                            }
                        }
                        (li ? res[i] : res[j]).Corners.Add(corner);   // a straight edge owns the cone
                    }
                    catch (Exception ex) when (ex.Message.StartsWith("tip offset is not available")) { throw; }
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

        // How far a face reaches from the edge midpoint along u (in its plane): the closest point on the face to a far point that way
        static double Reach(IFace2 f, double[] mid, double[] u)
        {
            var far = Add(mid, u);
            var q = f.GetClosestPointOn(far[0], far[1], far[2]) as double[];
            return q == null ? double.MaxValue : Dot(Sub(q, mid), u);
        }
        public static IBody2 ApplyCut(IBody2 body, EdgeCut c)
        {
            double before = Volume(body);
            foreach (var t in c.Tools)
            {
                int err;
                var res = body.Operations2((int)swBodyOperationType_e.SWBODYCUT, t, out err) as object[];
                if (res == null || res.Length == 0) throw new Exception("the chamfer could not be cut (SOLIDWORKS error " + err + ") - it probably does not fit the part: try a smaller size, a different angle or a larger clearance");
                body = Largest(res);
            }
            double removed = before - Volume(body);
            if (removed > c.Expected * 1.002 + 1e-12)
                throw new Exception("the chamfer mill would cut into a wall or other geometry - increase the clearance or use a smaller tool");
            return body;
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
            if (blend.Axis != null) { fromF1 = Dot(n1, blend.Axis) < -0.5; if (blend.Perp && flip) fromF1 = !fromF1; }   // blended: tool comes from the shared face (Reverse measures Size on the other one)
            if (blend.ConeWall != null) { fromF1 = Dot(n1, blend.ConeWall) < 0.5; if (flip) fromF1 = !fromF1; }   // blended with a rim: the mill works from the face that continues below the corner
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
            if (!horiz)   // say how big the chamfer may be instead of letting the cut fail with a bare error code
            {
                double rF1 = Reach(fromF1 ? f1 : f2, mid, uF), rG = Reach(fromF1 ? f2 : f1, mid, uG);
                if (d1 > rF1 + 1e-6 || d2 > rG + 1e-6)
                {
                    double maxSize = Math.Min(rF1, rG * Math.Sin(phi + beta) / Math.Sin(beta)) * 1000;
                    throw new Exception(string.Format(CultureInfo.InvariantCulture,
                        "chamfer does not fit: it needs {0:0.##} mm on the mill's face and {1:0.##} mm on the other face, which are only {2:0.##} and {3:0.##} mm wide here - use a size up to {4:0.##} mm at this angle{5}",
                        d1 * 1000, d2 * 1000, rF1 * 1000, rG * 1000, maxSize, d1 > rF1 + 1e-6 ? "" : ", or a smaller angle"));
                }
            }            double hB = d2 * Math.Sin(phi);                        // depth of the chamfer bottom (on G) below F
            double o = p.TipOffset;                                // the mill's apex sits this far below the chamfer's lower edge, on the extended chamfer line
            double rF = (hB + o) / Math.Tan(beta);                 // mill radius where its cone meets F
            var bma = Sub(Scale(uG, d2), Scale(uF, d1)); var lat0 = Sub(bma, Scale(axis, Dot(bma, axis)));
            var latU = Len(lat0) > 1e-12 ? Norm(lat0) : new double[3];   // outward, square to the axis: the way the apex slides along the chamfer line
            var latV = Scale(latU, o / Math.Tan(beta)); var dzV = Scale(axis, o);   // shift of the apex (and the mill axis) from the chamfer's lower edge
            if (p.ToolDia / 2 < rF - 1e-9)
                throw new Exception(string.Format(CultureInfo.InvariantCulture, "tool diameter must be at least {0:0.###} mm for this chamfer", 2 * rF * 1000));

            double stop = p.ToolDia / 2 + p.Clearance;
            bool e0 = blend.End[0], e1 = blend.End[1];
            bool b0 = !e0 && (Blocked(v0, Scale(t, -1), n1, n2) || !blend.Join[0] && RunsOn(v0, f1, f2, n1, n2));   // a rim that falls away past the end counts as a wall: stop short and leave with a cone
            bool b1 = !e1 && (Blocked(v1, t, n1, n2) || !blend.Join[1] && RunsOn(v1, f1, f2, n1, n2));
            bool k0 = b0 || blend.ConeEnd[0], k1 = b1 || blend.ConeEnd[1];   // ends that close with a runout cone
            double ext = Math.Max(d1, d2);
            var rel = new[] { new double[3], Scale(uF, d1), Scale(uG, d2) };   // chamfer triangle O, A, B relative to the edge's end
            bool open0 = !(b0 || e0 || blend.Join[0]), open1 = !(b1 || e1 || blend.Join[1]);
            double x0 = open0 ? OpenExt(v0, f1, f2, Scale(t, -1), rel, ext) : ext, x1 = open1 ? OpenExt(v1, f1, f2, t, rel, ext) : ext;   // open ends run on past the vertex, further where a draft leans away
            double s0 = e0 ? blend.EndShift[0] : blend.Join[0] ? 0 : b0 ? stop : -x0;       // blended / joined ends run exactly to the corner / neighbour
            double s1 = e1 ? len - blend.EndShift[1] : blend.Join[1] ? len : b1 ? len - stop : len + x1;
            if (s1 - s0 < 1e-6)
                throw new Exception(string.Format(CultureInfo.InvariantCulture,
                    "edge ({0:0.###} mm) too short for a {1:0.###} mm tool between walls", len * 1000, p.ToolDia * 1000));

            if (expected != null)
            {
                double inside = Math.Max(0, Math.Min(s1, len) - Math.Max(s0, 0));
                double aTri = 0.5 * d1 * d2 * Math.Sin(phi);
                Func<int, bool, double> endVol = (k, on) => !on ? 0 : blend.ConeAxis[k] != null ? Math.PI / 3 * Math.Pow(Math.Max(d1, d2), 3) : LugVolume(hB + o, beta, Dot(nF, nG), o, Dot(latV, nG));
                expected[0] = aTri * inside + endVol(0, k0) + endVol(1, k1)
                            + blend.Corners.Sum(c => c.Shifted ? Math.PI * rF * rF * (hB + o) / 3 : (2 * Math.PI - c.Gamma) * d1 * d1 * hB / 6);   // the cone's share in the material (upper bound)
                // Open ends: the tool runs on past the end (prism extended by ext). Usually that is air, but it may
                // shave material the end meets at an angle (e.g. an existing chamfer) - a real run-off, not a wall.
                expected[0] += aTri * ((open0 ? x0 : 0) + (open1 ? x1 : 0));
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
                PreviewLines(lines, p0, t, s0, s1, k0 && blend.ConeAxis[0] == null, k1 && blend.ConeAxis[1] == null, uF, uG, d2, phi, rF, latV, dzV, nG);
                for (int kc = 0; kc < 2; kc++)   // the corner cone that closes a rim blend: its real shape, not the mill's own runout cone
                {
                    var cax = blend.ConeAxis[kc]; if (cax == null || !(kc == 0 ? k0 : k1)) continue;
                    var mbp = Norm(Cross(t, Sub(Scale(uG, d2), Scale(uF, d1))));
                    double mcp = Dot(mbp, cax), sAp = Math.Abs(mcp) > 0.2 ? d1 * Dot(mbp, uF) / mcp : -1;
                    double hp = sAp > 1e-9 ? sAp : Math.Max(d1, d2), alp = sAp > 1e-9 ? Math.Asin(Math.Min(1, Math.Abs(mcp))) : beta;
                    var vkp = Add(p0, Scale(t, kc == 0 ? 0 : len));
                    var u1p = Math.Abs(Dot(uF, cax)) <= Math.Abs(Dot(uG, cax)) ? uF : uG;
                    ConeLines(lines, Add(vkp, Scale(cax, hp)), vkp, hp * Math.Tan(alp), cax, blend.ConeDown[kc] == null ? null : u1p, blend.ConeDown[kc], RimCornerOutside(vkp, blend.ConeDown[kc], blend.ConeGeom[kc]));
                }
                foreach (var c in blend.Corners) CornerLines(lines, c, axis, p.Size, hB, o, beta);
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
            Func<double, double[]> apexAt = s => Add(Add(Add(p0, Scale(t, s)), Scale(uG, d2)), Add(latV, dzV));   // the mill tip sits on the chamfer's bottom edge
            // a rim blended into this edge ends it with the corner cone (axis square to the rim, in the floor plane), not the mill's own tilted one
            Func<int, IBody2> endCone = k =>
            {
                var ca = blend.ConeAxis[k];
                if (ca == null) return Cone(apexAt(k == 0 ? s0 : s1), axis, hB + o, beta, stop, modeler);
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
            foreach (var c in blend.Corners) list.Add(c.Shifted ? Cone(Add(c.V2, Scale(axis, hB + o)), axis, hB + o, beta, 10 * Math.Max(d1, hB + o), modeler) : Cone(Add(c.V, Scale(axis, hB)), axis, hB, beta, 10 * Math.Max(d1, hB), modeler));
            foreach (var f in blend.Flats) list.Add(FlatCornerTool(f, modeler, math));
            return list;
        }

        // Material the mill's cone removes beyond the end of the prism (half of its part inside the wedge):
        // slices of the cone (apex on the other face G, axis normal to F) cut by G's plane. cosPsi = nF . nG.
        static double LugVolume(double hB, double beta, double cosPsi, double o = 0, double lat = 0)
        {
            double cot = 1 / Math.Tan(beta), sum = 0;
            const int n = 200;
            for (int i = 0; i < n; i++)
            {
                double h = hB * (i + 0.5) / n, r = h * cot, D = (h - o) * cosPsi + lat, a;   // D: signed distance of the axis from G's plane (+ = outside)
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
        static void CornerLines(List<double[][]> lines, Corner c, double[] axis, double d, double hB, double o, double beta)
        {
            var u = c.DirB;
            var w = Norm(Sub(c.DirA, Scale(u, Dot(c.DirA, u))));
            double g = c.Gamma;
            var ctr = c.Shifted ? c.V2 : c.V; double rr = c.Shifted ? (hB + o) / Math.Tan(beta) : d;   // with a tip offset the cone is centred on V2 and wider
            Func<double, double[]> arc = th => Add(ctr, Add(Scale(u, rr * Math.Cos(th)), Scale(w, rr * Math.Sin(th))));
            Func<double[], bool> air = x => Dot(Sub(x, c.V), c.NA) > 1e-6 && Dot(Sub(x, c.V), c.NB) > 1e-6;   // the air wedge outside both walls
            var apex = Add(ctr, Scale(axis, c.Shifted ? hB + o : hB));
            const int segs = 16;
            for (int i = 0; i < segs; i++)
                AddClippedFn(lines, arc(g * i / segs), arc(g * (i + 1) / segs), air);
            for (int i = 0; i <= 4; i++)
                AddClippedFn(lines, apex, arc(g * i / 4), air);
        }

        // The part near the vertex of a rim blend, as a test for preview strokes: below the floor it ends at the rim's wall, above it the two walls of the rising edge bound it
        static Func<double[], bool> RimCornerOutside(double[] V, double[] down, double[][] g)
        {
            if (g == null || down == null) return null;
            return x =>
            {
                var r = Sub(x, V);
                bool inMat;
                if (Dot(r, down) >= -1e-7) inMat = g[0] == null || Dot(r, g[0]) <= 1e-7;
                else inMat = (g[1] == null || Dot(r, g[1]) <= 1e-7) && (g[2] == null || Dot(r, g[2]) <= 1e-7);
                return !inMat;
            };
        }

        // Preview of a corner cone that closes a rim blend: the rim in the plane through vk square to ca (the quarter between u1 and dn when the cone is clipped to the corner, else all round) and generators to the apex
        static void ConeLines(List<double[][]> lines, double[] apex, double[] vk, double rr, double[] ca, double[] u1, double[] dn, Func<double[], bool> outside)
        {
            Func<double[], double[]> perp = v => { var w0 = Sub(v, Scale(ca, Dot(v, ca))); return Len(w0) > 1e-9 ? Norm(w0) : null; };
            double[] e1 = u1 == null ? null : perp(u1), e2 = dn == null ? null : perp(dn), w;
            double span = 2 * Math.PI;
            if (e1 != null && e2 != null)
            {
                var q = Sub(e2, Scale(e1, Dot(e1, e2)));
                if (Len(q) < 1e-9) return;
                w = Norm(q); span = Math.Acos(Math.Max(-1, Math.Min(1, Dot(e1, e2))));
            }
            else { e1 = perp(Cross(ca, new[] { 1.0, 0, 0 })) ?? perp(Cross(ca, new[] { 0, 1.0, 0 })); w = Cross(ca, e1); }
            Func<double, double[]> rim = th => Add(vk, Add(Scale(e1, rr * Math.Cos(th)), Scale(w, rr * Math.Sin(th))));
            const int segs = 16;
            Action<double[], double[]> add = (a0, b0) => { if (outside == null) lines.Add(new[] { a0, b0 }); else AddClippedFn(lines, a0, b0, outside); };
            for (int i = 0; i < segs; i++) add(rim(span * i / segs), rim(span * (i + 1) / segs));
            bool all = span > 6; int gens = all ? 8 : 4;
            for (int i = 0; i <= gens; i++) { if (all && i == gens) break; add(apex, rim(span * i / gens)); }
        }

        // Generator strokes on each runout cone (outline and hatch of the flat faces come from FaceLines).
        static void PreviewLines(List<double[][]> lines, double[] p0, double[] t, double s0, double s1, bool b0, bool b1,
                                 double[] uF, double[] uG, double d2, double phi, double rF, double[] lat, double[] dz, double[] nG)
        {
            foreach (var end in new[] { Tuple.Create(b0, s0, Scale(t, -1)), Tuple.Create(b1, s1, t) })
            {
                if (!end.Item1) continue;
                var S = Add(p0, Scale(t, end.Item2));
                var w = end.Item3;
                var apex = Add(Add(S, Scale(uG, d2)), Add(lat, dz));
                var Z = Add(Add(S, Scale(uF, d2 * Math.Cos(phi))), lat);   // centre of the cone's circle on face F
                Func<double, double[]> arc = th => Add(Z, Add(Scale(w, rF * Math.Cos(th)), Scale(uF, rF * Math.Sin(th))));
                const int segs = 16;
                for (int i = 0; i < segs; i++)
                    AddClipped(lines, arc(Math.PI / 2 * i / segs), arc(Math.PI / 2 * (i + 1) / segs), S, nG);
                for (int i = 0; i < 4; i++)
                    AddClipped(lines, apex, arc(Math.PI / 2 * i / 4), S, nG);
            }
        }

        // Open end: how far past the vertex the prism must run so the chamfer triangle (rel = O, A, B) clears every face that
        // leans away along outDir (a draft). Never less than min.
        // shortcut: uses the face's tangent plane at the vertex and caps at 100x min (draft under ~0.6 deg stops short), curved faces only approximately.
        static double OpenExt(IVertex v, IFace2 f1, IFace2 f2, double[] outDir, double[][] rel, double min)
        {
            var pt = (double[])v.GetPoint(); double a1 = f1.GetArea(), a2 = f2.GetArea(), s = min;
            foreach (IFace2 f in (object[])v.GetAdjacentFaces())
            {
                double a = f.GetArea();
                if (Math.Abs(a - a1) < 1e-12 || Math.Abs(a - a2) < 1e-12) continue;   // the edge's own faces
                var n = FaceNormalAt(f, pt); double c = n == null ? 0 : Dot(n, outDir);
                if (c < 0.02) continue;   // not leaning away (a wall or a face along the edge)
                foreach (var q in rel) s = Math.Max(s, -Dot(n, q) / c * 1.01);
            }
            return Math.Min(s, 100 * min);
        }

        // An end is blocked when another face at the vertex faces back along the edge (a wall).
        static bool Blocked(IVertex v, double[] outDir, double[] n1, double[] n2) { return Blocker(v, outDir, n1, n2) != null; }

        // A third face at the vertex continues one of the edge's faces smoothly (e.g. the top turns into a rounded rim): the edge does not end on a wall, the surface just falls away
        static bool RunsOn(IVertex v, IFace2 f1, IFace2 f2, double[] n1, double[] n2)
        {
            var pt = (double[])v.GetPoint(); double a1 = f1.GetArea(), a2 = f2.GetArea();
            foreach (IFace2 f in (object[])v.GetAdjacentFaces())
            {
                double a = f.GetArea();
                if (Math.Abs(a - a1) < 1e-12 || Math.Abs(a - a2) < 1e-12) continue;   // the edge's own faces
                var n = FaceNormalAt(f, pt);
                if (n != null && (Dot(n, n1) > 0.999 || Dot(n, n2) > 0.999)) return true;
            }
            return false;
        }

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
    }
}
