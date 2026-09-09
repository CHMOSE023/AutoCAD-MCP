using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcp.Acad
{
    internal static class Draw
    {
        /// <summary>把实体加入模型空间前的可选配置钩子（在事务内、Commit 前执行）。</summary>
        internal delegate void ConfigureEntity(Entity ent, Transaction tr, Database db);

        public static string AddLine(double x1, double y1, double x2, double y2, string? layer)
            => Append(new AcadDb.Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), layer);

        public static string AddCircle(double cx, double cy, double r, string? layer)
        {
            if (r <= 0) throw new ArgumentException("半径必须大于 0。");
            return Append(new AcadDb.Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, r), layer);
        }

        public static string AddPolyline(IReadOnlyList<(double X, double Y)> pts, bool closed, string? layer)
        {
            if (pts.Count < 2) throw new ArgumentException("多段线至少需要 2 个点。");
            var pl = new AcadDb.Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
            pl.Closed = closed;
            return Append(pl, layer);
        }

        public static string AddText(double x, double y, double height, string content, string? layer, double rotationDeg)
        {
            if (height <= 0) throw new ArgumentException("字高必须大于 0。");
            var t = new DBText
            {
                Position = new Point3d(x, y, 0),
                Height = height,
                TextString = content ?? string.Empty,
                Rotation = rotationDeg * Math.PI / 180.0,
            };
            return Append(t, layer, (ent, tr, db) =>
                ((DBText)ent).TextStyleId = TextStyles.EnsureUnicodeStyle(tr, db));
        }

        public static string AddMText(double x, double y, double width, string content, double height, string? layer, double rotationDeg)
        {
            if (height <= 0) throw new ArgumentException("字高必须大于 0。");
            var m = new MText
            {
                Location = new Point3d(x, y, 0),
                Width = width < 0 ? 0 : width,   // 0 = 不自动换行
                TextHeight = height,
                Contents = content ?? string.Empty,
                Rotation = rotationDeg * Math.PI / 180.0,
                Attachment = AttachmentPoint.TopLeft,
            };
            return Append(m, layer, (ent, tr, db) =>
                ((MText)ent).TextStyleId = TextStyles.EnsureUnicodeStyle(tr, db));
        }

        public static string AddArc(double cx, double cy, double r, double startDeg, double endDeg, string? layer)
        {
            if (r <= 0) throw new ArgumentException("半径必须大于 0。");
            double s = startDeg * Math.PI / 180.0, e = endDeg * Math.PI / 180.0;
            return Append(new Arc(new Point3d(cx, cy, 0), r, s, e), layer);
        }

        public static string AddEllipse(double cx, double cy, double majorX, double majorY, double ratio, string? layer)
        {
            var major = new Vector3d(majorX, majorY, 0);
            if (major.Length < 1e-9) throw new ArgumentException("长轴向量不能为零。");
            if (ratio <= 0 || ratio > 1) throw new ArgumentException("轴比 ratio 必须在 (0, 1]。");
            var el = new Ellipse(new Point3d(cx, cy, 0), Vector3d.ZAxis, major, ratio, 0, 2 * Math.PI);
            return Append(el, layer);
        }

        /// <summary>
        /// 画样条曲线（NURBS）。useControlPoints=false 走拟合点（曲线严格穿过每个点，等价 SPLINE 命令的“拟合”），
        /// =true 走控制点（曲线被控制点拉扯、一般不经过它们，等价 SPLINE 的 CV / “控制点”方式）。
        /// </summary>
        public static string AddSpline(
            IReadOnlyList<(double X, double Y)> pts, bool useControlPoints, bool closed, int degree,
            double fitTolerance, (double X, double Y)? startTangent, (double X, double Y)? endTangent, string? layer)
        {
            if (degree < 1 || degree > 11)
                throw new ArgumentException("degree（阶次）必须在 1..11，常用 3（三次样条）。");
            if (fitTolerance < 0)
                throw new ArgumentException("fitTolerance 不能为负。");
            if (startTangent.HasValue != endTangent.HasValue)
                throw new ArgumentException("起点切向与终点切向必须成对给出（四个分量一起传，或都不传）。");

            var p3 = new Point3dCollection();
            foreach (var p in pts) p3.Add(new Point3d(p.X, p.Y, 0));

            Spline sp = useControlPoints
                ? FromControlPoints(p3, closed, degree, startTangent.HasValue)
                : FromFitPoints(p3, closed, degree, fitTolerance, startTangent, endTangent);

            return Append(sp, layer);
        }

        /// <summary>拟合点方式：曲线严格穿过每个点。闭合走 isPeriodic 构造，切向走带切向的重载。</summary>
        private static Spline FromFitPoints(
            Point3dCollection p3, bool closed, int degree, double fitTolerance,
            (double X, double Y)? startTangent, (double X, double Y)? endTangent)
        {
            if (p3.Count < 2)
                throw new ArgumentException("拟合点方式下至少需要 2 个点。");

            if (startTangent.HasValue)
            {
                if (closed)
                    throw new ArgumentException("闭合样条不能同时指定起终点切向，二选一。");
                var st = new Vector3d(startTangent.Value.X, startTangent.Value.Y, 0);
                var et = new Vector3d(endTangent!.Value.X, endTangent.Value.Y, 0);
                if (st.Length < 1e-9 || et.Length < 1e-9)
                    throw new ArgumentException("切向向量不能为零。");
                return new Spline(p3, st, et, KnotParameterizationEnum.Chord, degree, fitTolerance);
            }

            if (closed && p3.Count < 3)
                throw new ArgumentException("闭合样条至少需要 3 个点。");

            // 第二个参数是 isPeriodic：闭合样条不要把起点重复写在末尾，AutoCAD 自己接上
            return new Spline(p3, closed, KnotParameterizationEnum.Chord, degree, fitTolerance);
        }

        /// <summary>控制点（CV）方式：手工造节点矢量。开口用夹紧节点；闭合把首 degree 个控制点绕接到末尾再配均匀节点。</summary>
        private static Spline FromControlPoints(Point3dCollection p3, bool closed, int degree, bool hasTangent)
        {
            int n = p3.Count;
            if (hasTangent)
                throw new ArgumentException("切向只对拟合点方式（method=fit）有效，控制点方式请去掉切向参数。");

            // 闭合时首尾会绕接（wrap），所以比开口少要一个点
            int least = closed ? degree : degree + 1;
            if (n < least)
                throw new ArgumentException(
                    $"控制点方式下点数至少为 {least}，当前只有 {n} 个。降低 degree 或多给几个点。");

            var cps = new Point3dCollection();
            for (int i = 0; i < n; i++) cps.Add(p3[i]);

            var knots = new DoubleCollection();
            if (closed)
            {
                // 闭合：把首 degree 个控制点接到末尾（wrap），再配 0,1,2,… 的均匀节点。
                // 有效参数区间是 [t_degree, t_(n+degree)]，正好跑满一圈、首尾点重合，AutoCAD 会自己置上 closed 位。
                //
                // 不要指望构造函数的 closed / periodic 两个参数：实测（AutoCAD 2020）传 true 会被直接忽略，
                // AutoCAD 只认节点矢量，把有效区间外的部分裁掉 —— 4 个控制点配 0..7 的均匀节点，
                // 有效区间只剩 [3,4]，曲线退化成一小段（DXF 70 位也不会置 closed）。
                for (int i = 0; i < degree; i++) cps.Add(p3[i]);
                int m = cps.Count;                              // = n + degree
                for (int i = 0; i <= m + degree; i++) knots.Add(i);   // 共 n + 2*degree + 1 个
            }
            else
            {
                // 夹紧（clamped）节点矢量：两端各重复 degree 次，中间 0..n-degree，合计 n+degree+1 个
                for (int i = 0; i < degree; i++) knots.Add(0.0);
                for (int i = 0; i <= n - degree; i++) knots.Add(i);
                for (int i = 0; i < degree; i++) knots.Add(n - degree);
            }

            var weights = new DoubleCollection();
            for (int i = 0; i < cps.Count; i++) weights.Add(1.0);

            try
            {
                return new Spline(degree, false, false, false, cps, knots, weights, 1e-9, 1e-10);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new InvalidOperationException(
                    $"按控制点创建样条失败（{ex.ErrorStatus}）。可改用拟合点方式：method=\"fit\"。", ex);
            }
        }

        public static string AddPoint(double x, double y, string? layer)
            => Append(new DBPoint(new Point3d(x, y, 0)), layer);

        public static string AddXline(double x, double y, double dirX, double dirY, string? layer)
        {
            var d = new Vector3d(dirX, dirY, 0);
            if (d.Length < 1e-9) throw new ArgumentException("方向向量不能为零。");
            return Append(new Xline { BasePoint = new Point3d(x, y, 0), UnitDir = d.GetNormal() }, layer);
        }

        public static string AddRay(double x, double y, double dirX, double dirY, string? layer)
        {
            var d = new Vector3d(dirX, dirY, 0);
            if (d.Length < 1e-9) throw new ArgumentException("方向向量不能为零。");
            return Append(new Ray { BasePoint = new Point3d(x, y, 0), UnitDir = d.GetNormal() }, layer);
        }

        public static string Erase(IReadOnlyList<string> handles)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            int erased = 0;
            var notFound = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var h in handles)
                {
                    if (!TryGetObjectId(db, h, out var id))
                    {
                        notFound.Add(h);
                        continue;
                    }
                    if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent)
                    {
                        ent.Erase();
                        erased++;
                    }
                    else
                    {
                        notFound.Add(h);
                    }
                }
                tr.Commit();
            }

            var msg = $"已删除 {erased} 个实体。";
            if (notFound.Count > 0)
                msg += $" 未找到：{string.Join(", ", notFound)}";
            return msg;
        }

        internal static string Append(Entity ent, string? layer, ConfigureEntity? configure = null)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                if (!string.IsNullOrWhiteSpace(layer))
                {
                    Layers.EnsureLayer(tr, db, layer!, null);
                    ent.Layer = layer!;
                }

                configure?.Invoke(ent, tr, db);

                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                string handle = ent.Handle.ToString();
                tr.Commit();
                return handle;
            }
        }

        internal static bool TryGetObjectId(Database db, string handleHex, out ObjectId id)
        {
            id = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(handleHex)) return false;
            try
            {
                long value = Convert.ToInt64(handleHex.Trim(), 16);
                var handle = new Handle(value);
                return db.TryGetObjectId(handle, out id) && !id.IsNull;
            }
            catch
            {
                return false;
            }
        }
    }
}
