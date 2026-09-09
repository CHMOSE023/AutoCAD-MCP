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

        /// <summary>
        /// 把已有实体改到指定图层，可顺带覆盖颜色 / 线宽。
        ///
        /// 为什么需要：图层是按图层配线宽、按图层做校验的前提。批量插块时若没指定图层，
        /// 块引用会全落在当时的当前图层上（实测有 102 个块跑到 DIM 和 0 层），
        /// 事后既查不出越界也吃不到线宽 —— 那次只能靠 eval_lisp + entmod 绕，
        /// 而 eval_lisp 默认是关的，不该成为常规操作的依赖。
        /// </summary>
        public static string SetLayer(IReadOnlyList<string> handles, string layer,
            int? colorIndex, double? lineWeightMm, bool byLayerColor)
        {
            if (string.IsNullOrWhiteSpace(layer)) throw new ArgumentException("layer 不能为空。");
            if (handles.Count == 0) throw new ArgumentException("需要至少一个实体 handle。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            int changed = 0;
            var notFound = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                Layers.EnsureLayer(tr, db, layer, null);

                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id)) { notFound.Add(h); continue; }
                    if (!(tr.GetObject(id, OpenMode.ForWrite) is Entity ent)) { notFound.Add(h); continue; }

                    ent.Layer = layer;

                    if (byLayerColor)
                        ent.ColorIndex = 256;                      // 256 = ByLayer
                    else if (colorIndex.HasValue)
                        ent.ColorIndex = Math.Max(0, Math.Min(256, colorIndex.Value));

                    if (lineWeightMm.HasValue)
                        ent.LineWeight = Layers.ToLineWeight(lineWeightMm.Value);

                    changed++;
                }
                tr.Commit();
            }

            var msg = $"已把 {changed} 个实体改到图层 '{layer}'";
            if (byLayerColor) msg += "，颜色设为 ByLayer";
            else if (colorIndex.HasValue) msg += $"，颜色索引 {colorIndex.Value}";
            if (lineWeightMm.HasValue) msg += $"，线宽 {lineWeightMm.Value:0.00}mm";
            msg += "。";
            if (notFound.Count > 0) msg += $" 未找到：{string.Join(", ", notFound)}";
            return msg;
        }

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
