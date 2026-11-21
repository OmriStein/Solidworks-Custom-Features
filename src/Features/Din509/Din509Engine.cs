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
            var pristine = ((object[])((IBody2)src.Copy()).GetFaces()).Cast<IFace2>().ToList();

            var tool = Tool((IModeler)sw.GetModeler(), edge, p);
            double before = Volume(src), toolVol = Volume(tool);
            int err;
            var res = src.Operations2((int)swBodyOperationType_e.SWBODYCUT, tool, out err) as object[];
            if (res == null || res.Length == 0) throw new Exception("cut failed (error " + err + ")");
            var work = Largest(res);
            double removed = before - Volume(work);
            if (removed <= 1e-15) throw new Exception("the undercut removes nothing");
            if (removed > toolVol * 1.002) throw new Exception("the undercut cuts more than its profile");

            object faces, edgesNeed;
            data.GetEntitiesNeedUserId(work, out faces, out edgesNeed);
            var need = ((faces as object[]) ?? new object[0]).Cast<IFace2>().ToList();
            var fresh = ((object[])work.GetFaces()).Cast<IFace2>().Where(f => IsNewFace(f, pristine)).ToList();
            int id = 0;
            foreach (var f in need) data.SetFaceUserId((Face2)f, id++, 0);
            foreach (var f in fresh) if (!need.Any(n => n.IsSame(f))) data.SetFaceUserId((Face2)f, id++, 0);
            id = 0;
            foreach (var e in (edgesNeed as object[]) ?? new object[0]) data.SetEdgeUserId((Edge)e, id++, 0);
            return work;
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
            bore = Dot(nCyl, rd) < 0;   // cylinder face turned towards the axis: a hole
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