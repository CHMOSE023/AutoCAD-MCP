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
