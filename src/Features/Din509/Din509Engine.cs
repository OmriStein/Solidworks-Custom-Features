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
    public static class Din509Engine
    {
        public const string ProgIdName = "MfgChamfer.Din509";

        public static object Build(ISldWorks sw, IFeature feat)
        {
            var data = (IMacroFeatureData)feat.GetDefinition();
            var p = Din509Params.Read(data);
            var edge = SelectedEdge(data);
            if (edge == null) throw new Exception("shoulder edge missing (deleted or changed geometry) - edit the feature to reselect");

            IBody2 src = null;
            object eb = data.EditBodies;
            var arr = eb as object[];
            if (arr != null && arr.Length > 0) src = arr[0] as IBody2;
            if (src == null) src = eb as IBody2;
            if (src == null) src = data.EditBody as IBody2;
            if (src == null) throw new Exception("no body to modify");
            var pristineBody = (IBody2)src.Copy();   // kept alive until the end: its faces are compared with the new ones
            var pristine = ((object[])pristineBody.GetFaces()).Cast<IFace2>().ToList();

            var work = Cut((IModeler)sw.GetModeler(), src, edge, p);

            object faces, edgesNeed;
            data.GetEntitiesNeedUserId(work, out faces, out edgesNeed);
            var need = ((faces as object[]) ?? new object[0]).Cast<IFace2>().ToList();
            var fresh = ((object[])work.GetFaces()).Cast<IFace2>().Where(f => IsNewFace(f, pristine)).ToList();
            int id = 0;
            foreach (var f in need) data.SetFaceUserId((Face2)f, id++, 0);
            foreach (var f in fresh) if (!need.Any(n => n.IsSame(f))) data.SetFaceUserId((Face2)f, id++, 0);
            id = 0;
            foreach (var e in (edgesNeed as object[]) ?? new object[0]) data.SetEdgeUserId((Edge)e, id++, 0);
            GC.KeepAlive(pristineBody);
            return work;
        }

        // The undercut cut into src (edge only supplies the geometry: it may belong to another body). Throws when it cannot be built.
        public static IBody2 Cut(IModeler modeler, IBody2 src, IEdge edge, Din509Params p)
        {
            var tool = Tool(modeler, edge, p);
            var fill = Fill(modeler, edge, p);   // closes the corner between the wall and the corner arc; built before the cut (the edge belongs to the body being cut)
            double before = Volume(src), toolVol = Volume(tool);
            int err;
            var res = src.Operations2((int)swBodyOperationType_e.SWBODYCUT, tool, out err) as object[];
            if (res == null || res.Length == 0) throw new Exception("cut failed (error " + err + ")");
            var work = Largest(res);
            double removed = before - Volume(work);
            if (removed <= 1e-15) throw new Exception("the undercut removes nothing");
            if (removed > toolVol * 1.002) throw new Exception("the undercut cuts more than its profile");
            if (fill != null)
            {
                var res2 = work.Operations2((int)swBodyOperationType_e.SWBODYADD, fill, out err) as object[];
                if (res2 == null || res2.Length == 0) throw new Exception("closing the wall corner failed (error " + err + ")");
                work = Largest(res2);
            }
            return work;
        }

        // Yellow preview strokes on the new faces of the cut result: their outline (each edge once), and across every face that lies between two
        // circles about the shaft axis, strokes joining them (the face's hatch).
        public static List<IBody2> Strokes(IModeler m, IEdge edge, IBody2 result, List<IFace2> origFaces)
        {
            double[] o, ax, rd; double R; bool bore;
            Frame(edge, out o, out ax, out rd, out R, out bore);
            var u = Cross(ax, rd);
            Func<double, double, double, double[]> P = (a, r, ang) => Add(Add(o, Scale(ax, a)), Scale(Add(Scale(rd, Math.Cos(ang)), Scale(u, Math.Sin(ang))), r));
            var res = new List<IBody2>();
            Action<double[], double[]> line = (p0, p1) =>
            {
                if (Dist(p0, p1) < 1e-7) return;
                var w = m.CreateWireBody(new[] { Seg(m, p0, p1) }, (int)swCreateWireBodyOptions_e.swCreateWireBodyByDefault) as IBody2;
                if (w != null) res.Add(w);
            };
            var seen = new HashSet<string>();
            Func<double[], string> key = q => Math.Round(q[0] * 1e7) + "," + Math.Round(q[1] * 1e7) + "," + Math.Round(q[2] * 1e7);
            foreach (IFace2 f in (object[])result.GetFaces())
            {
                if (!IsNewFace(f, origFaces)) continue;
                var rings = new List<double[]>();   // { axial position, radius } of the circles bounding the face
                foreach (IEdge e in (object[])f.GetEdges())
                {
                    var pts = EdgePoints(e);
                    var k = key(pts[0]) + ">" + key(pts[pts.Count / 2]) + ">" + key(pts[pts.Count - 1]);
                    if (seen.Add(k)) for (int i = 0; i + 1 < pts.Count; i++) line(pts[i], pts[i + 1]);
                    var c = (ICurve)e.GetCurve();
                    if (!c.IsCircle()) continue;
            var cp = (double[])c.CircleParams;
                    double a = Dot(Sub(new[] { cp[0], cp[1], cp[2] }, o), ax);
                    if (!rings.Any(q => Math.Abs(q[0] - a) < 1e-7 && Math.Abs(q[1] - cp[6]) < 1e-7)) rings.Add(new[] { a, cp[6] });
                }
                if (rings.Count != 2) continue;
                for (int k = 0; k < 16; k++) { double ang = k * 2 * Math.PI / 16; line(P(rings[0][0], rings[0][1], ang), P(rings[1][0], rings[1][1], ang)); }
            }
            return res;
        }
        public static IEdge SelectedEdge(IMacroFeatureData data)
        {
            object objs, types, marks, views, comps;
            data.GetSelections3(out objs, out types, out marks, out views, out comps);
            var all = (objs as object[]) ?? new object[0];
            var mk = (marks as int[]) ?? new int[0];
            for (int i = 0; i < all.Length; i++)
                if ((i < mk.Length ? mk[i] : 1) == 1 && all[i] is IEdge) return (IEdge)all[i];
            return null;
        }

        // Shaft (or bore) radius of a shoulder edge; throws when it is not the inner corner of a shoulder
        public static double ShaftRadius(IEdge edge)
        {
            double[] o, ax, rd; double R; bool bore; Frame(edge, out o, out ax, out rd, out R, out bore);
            return R;
        }

        // The undercut's frame at a shoulder edge: o = centre of the edge circle (in the wall plane), ax = along the shaft away from the
        // wall, rd = radial, R = shaft radius
        static void Frame(IEdge edge, out double[] o, out double[] ax, out double[] rd, out double R, out bool bore)
        {
            var c = (ICurve)edge.GetCurve();
            if (!c.IsCircle()) throw new Exception("select the circular edge where the shaft or bore meets the shoulder");
            var en = Ends(edge);   // a shoulder is a whole circle: an arc (a pocket corner, say) has two different ends
            if (Dist(en[0], en[en.Length - 1]) > 1e-7) throw new Exception("select the complete circular edge, all the way round the shaft or bore (not an arc such as a pocket corner)");
            var cp = (double[])c.CircleParams;
            o = new[] { cp[0], cp[1], cp[2] };
            R = cp[6];
            var axis = Norm(new[] { cp[3], cp[4], cp[5] });
            var adj = (object[])edge.GetTwoAdjacentFaces2();
            IFace2 plane = null, cyl = null;
            foreach (IFace2 f in adj)
            {
                var s = (ISurface)f.GetSurface();
                if (s.IsPlane()) plane = f; else if (s.IsCylinder()) cyl = f;
            }
            if (plane == null || cyl == null) throw new Exception("the edge must lie between a cylinder (shaft or bore) and a plane (the shoulder)");
            var pt = Ends(edge)[0];
            rd = Norm(Sub(pt, o));
            var nWall = FaceNormalAt(plane, pt);
            ax = Dot(axis, nWall) >= 0 ? axis : Scale(axis, -1);
            var nCyl = FaceNormalAt(cyl, pt);
            var bx = (double[])cyl.GetBox();   // the shaft or bore must run away from the wall: its face lies on the ax side of the edge
            if (Dot(Sub(new[] { (bx[0] + bx[3]) / 2, (bx[1] + bx[4]) / 2, (bx[2] + bx[5]) / 2 }, o), ax) <= 0)
                throw new Exception("select the inner corner of a shoulder: the shaft or bore must run away from the wall");
            bore = Dot(nCyl, rd) < 0;   // cylinder face turned towards the axis: a hole
        }

        public static IBody2 Fill(IModeler m, IEdge edge, Din509Params p)
        {
            double[] o, ax, rd; double R; bool bore;
            Frame(edge, out o, out ax, out rd, out R, out bore);
            var prof = Din509Table.Fill(p.FormChar, R, p.R, p.T1, p.T2, bore);
            return prof == null ? null : Revolve(m, o, ax, rd, prof);
        }

        public static IBody2 Tool(IModeler m, IEdge edge, Din509Params p)
        {
            double[] o, ax, rd; double R; bool bore;
            Frame(edge, out o, out ax, out rd, out R, out bore);
            double fOut;
            var prof = Din509Table.Profile(p.FormChar, R, p.R, p.T1, p.T2, p.F, bore, out fOut);
            return Revolve(m, o, ax, rd, prof);
        }
    }
}
