using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    internal static class Modify
    {
        public static string GetEntity(string handle)
        {
            var db = AcadContext.ActiveDocument.Database;
            using (AcadContext.ActiveDocument.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");

                var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                var o = new JObject
                {
                    ["handle"] = ent.Handle.ToString(),
                    ["type"] = ent.GetType().Name,
                    ["dxf"] = ent.GetRXClass().DxfName,
                    ["layer"] = ent.Layer,
                    ["color"] = ent.Color.ToString(),
                    ["linetype"] = ent.Linetype,
                };
                try
                {
                    var e = ent.GeometricExtents;
                    o["bbox"] = $"{e.MinPoint.X:0.###},{e.MinPoint.Y:0.###} .. {e.MaxPoint.X:0.###},{e.MaxPoint.Y:0.###}";
                }
                catch { }

                switch (ent)
                {
                    case Line ln:
                        o["start"] = $"{ln.StartPoint.X:0.###},{ln.StartPoint.Y:0.###}";
                        o["end"] = $"{ln.EndPoint.X:0.###},{ln.EndPoint.Y:0.###}";
                        o["length"] = Math.Round(ln.Length, 4);
                        break;
                    case Circle c:
                        o["center"] = $"{c.Center.X:0.###},{c.Center.Y:0.###}";
                        o["radius"] = Math.Round(c.Radius, 4);
                        break;
                    case Autodesk.AutoCAD.DatabaseServices.Polyline pl:
                        o["vertices"] = pl.NumberOfVertices;
                        o["closed"] = pl.Closed;
                        o["length"] = Math.Round(pl.Length, 4);
                        break;
                    case DBText tx:
                        o["text"] = tx.TextString;
                        o["height"] = Math.Round(tx.Height, 4);
                        o["position"] = $"{tx.Position.X:0.###},{tx.Position.Y:0.###}";
                        break;
                    case BlockReference br:
                        o["blockName"] = br.Name;
                        o["position"] = $"{br.Position.X:0.###},{br.Position.Y:0.###}";
                        o["rotationDeg"] = Math.Round(br.Rotation * 180.0 / Math.PI, 4);
                        break;
                }
                tr.Commit();
                return o.ToString(Newtonsoft.Json.Formatting.Indented);
            }
        }

        public static string Move(IReadOnlyList<string> handles, double dx, double dy)
            => Transform(handles, Matrix3d.Displacement(new Vector3d(dx, dy, 0)), "移动");

        public static string Rotate(IReadOnlyList<string> handles, double baseX, double baseY, double angleDeg)
            => Transform(handles,
                Matrix3d.Rotation(angleDeg * Math.PI / 180.0, Vector3d.ZAxis, new Point3d(baseX, baseY, 0)),
                "旋转");

        public static string Scale(IReadOnlyList<string> handles, double baseX, double baseY, double factor)
        {
            if (factor <= 0) throw new ArgumentException("缩放比例必须大于 0。");
            return Transform(handles, Matrix3d.Scaling(factor, new Point3d(baseX, baseY, 0)), "缩放");
        }

        public static string Copy(string handle, double dx, double dy, int count)
        {
            if (count < 1) count = 1;
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var newHandles = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");

                var src = (Entity)tr.GetObject(id, OpenMode.ForRead);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (int i = 1; i <= count; i++)
                {
                    var clone = (Entity)src.Clone();
                    clone.TransformBy(Matrix3d.Displacement(new Vector3d(dx * i, dy * i, 0)));
                    ms.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    newHandles.Add(clone.Handle.ToString());
                }
                tr.Commit();
            }
            return $"已复制 {newHandles.Count} 份，新 handle：{string.Join(", ", newHandles)}";
        }

        public static string Offset(string handle, double distance, double? sideX, double? sideY)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var newHandles = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");

                if (!(tr.GetObject(id, OpenMode.ForRead) is Curve curve))
                    throw new ArgumentException("offset 只支持曲线类实体（Line / Polyline / Circle / Arc）。");

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                double d = distance;
                // 指定了参考侧点时，按点在曲线哪侧决定正负偏移
                if (sideX is double sx && sideY is double sy)
                {
                    var p = new Point3d(sx, sy, 0);
                    var closest = curve.GetClosestPointTo(p, false);
                    var deriv = curve.GetFirstDerivative(closest);
                    var toSide = p - closest;
                    var cross = deriv.CrossProduct(toSide);
                    if (cross.Z < 0) d = -Math.Abs(distance);
                    else d = Math.Abs(distance);
                }

                foreach (DBObject obj in curve.GetOffsetCurves(d))
                {
                    if (obj is Entity e)
                    {
                        ms.AppendEntity(e);
                        tr.AddNewlyCreatedDBObject(e, true);
                        newHandles.Add(e.Handle.ToString());
                    }
                }
                tr.Commit();
            }

            return newHandles.Count > 0
                ? $"已偏移，新 handle：{string.Join(", ", newHandles)}"
                : "偏移未产生结果（距离过大或几何不允许）。";
        }

        public static string Mirror(IReadOnlyList<string> handles, double x1, double y1, double x2, double y2, bool keepSource)
        {
            var axis = new Line3d(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            var m = Matrix3d.Mirroring(axis);

            if (!keepSource)
                return Transform(handles, m, "镜像");

            // 保留源：克隆 + 变换 + 追加
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var newHandles = new List<string>();
            var notFound = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id)) { notFound.Add(h); continue; }
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity src)) { notFound.Add(h); continue; }
                    var clone = (Entity)src.Clone();
                    clone.TransformBy(m);
                    ms.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    newHandles.Add(clone.Handle.ToString());
                }
                tr.Commit();
            }

            var msg = $"已镜像 {newHandles.Count} 个实体（保留源），新 handle：{string.Join(", ", newHandles)}";
            if (notFound.Count > 0) msg += $" 未找到：{string.Join(", ", notFound)}";
            return msg;
        }

        internal static string Transform(IReadOnlyList<string> handles, Matrix3d m, string verb)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            int done = 0;
            var notFound = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id)) { notFound.Add(h); continue; }
                    if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity ent)
                    {
                        ent.TransformBy(m);
                        done++;
                    }
                    else notFound.Add(h);
                }
                tr.Commit();
            }

            var msg = $"已{verb} {done} 个实体。";
            if (notFound.Count > 0) msg += $" 未找到：{string.Join(", ", notFound)}";
            return msg;
        }
    }
}
