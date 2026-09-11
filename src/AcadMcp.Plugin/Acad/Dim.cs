using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Acad
{
    /// <summary>标注。用 .NET 原生标注实体（当前 DIMSTYLE）。</summary>
    internal static class Dim
    {
        private static readonly Draw.ConfigureEntity SetDimStyle = (ent, tr, db) =>
        {
            if (ent is Dimension d)
            {
                d.SetDatabaseDefaults();
                d.DimensionStyle = db.Dimstyle;
            }
        };

        public static string Linear(double x1, double y1, double x2, double y2, double dimX, double dimY, double rotationDeg, string? layer)
        {
            var d = new RotatedDimension(
                rotationDeg * Math.PI / 180.0,
                new Point3d(x1, y1, 0), new Point3d(x2, y2, 0), new Point3d(dimX, dimY, 0),
                "", ObjectId.Null);
            return "handle=" + Draw.Append(d, layer, SetDimStyle);
        }

        public static string Aligned(double x1, double y1, double x2, double y2, double dimX, double dimY, string? layer)
        {
            var d = new AlignedDimension(
                new Point3d(x1, y1, 0), new Point3d(x2, y2, 0), new Point3d(dimX, dimY, 0),
                "", ObjectId.Null);
            return "handle=" + Draw.Append(d, layer, SetDimStyle);
        }

        public static string Angular(double vx, double vy, double x1, double y1, double x2, double y2, double arcX, double arcY, string? layer)
        {
            var d = new Point3AngularDimension(
                new Point3d(vx, vy, 0),
                new Point3d(x1, y1, 0), new Point3d(x2, y2, 0),
                new Point3d(arcX, arcY, 0),
                "", ObjectId.Null);
            return "handle=" + Draw.Append(d, layer, SetDimStyle);
        }

        public static string Radius(string handle, string? layer)
        {
            var (center, radius) = ReadCircular(handle);
            var chord = new Point3d(center.X + radius, center.Y, 0);
            var d = new RadialDimension(center, chord, radius * 0.4, "", ObjectId.Null);
            return "handle=" + Draw.Append(d, layer, SetDimStyle);
        }

        public static string Diameter(string handle, string? layer)
        {
            var (center, radius) = ReadCircular(handle);
            var chord = new Point3d(center.X + radius, center.Y, 0);
            var far = new Point3d(center.X - radius, center.Y, 0);
            var d = new DiametricDimension(chord, far, radius * 0.4, "", ObjectId.Null);
            return "handle=" + Draw.Append(d, layer, SetDimStyle);
        }

        public static string Leader(IReadOnlyList<(double X, double Y)> pts, string text, double height, string? layer)
        {
            if (pts.Count < 2) throw new ArgumentException("引线至少需要 2 个点。");
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("引线文字不能为空。");
            if (height <= 0) height = 2.5;

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = Space.Current(tr, db, OpenMode.ForWrite);

                string ly = null!;
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    Layers.EnsureLayer(tr, db, layer!, null);
                    ly = layer!;
                }

                // 末端放 MText 作为注释
                var last = pts[pts.Count - 1];
                var mt = new MText
                {
                    Location = new Point3d(last.X, last.Y, 0),
                    TextHeight = height,
                    Contents = text,
                    Attachment = AttachmentPoint.MiddleLeft,
                    TextStyleId = TextStyles.EnsureUnicodeStyle(tr, db),
                };
                if (ly != null) mt.Layer = ly;
                ms.AppendEntity(mt);
                tr.AddNewlyCreatedDBObject(mt, true);

                var leader = new Autodesk.AutoCAD.DatabaseServices.Leader();
                leader.SetDatabaseDefaults();
                leader.DimensionStyle = db.Dimstyle;
                foreach (var p in pts)
                    leader.AppendVertex(new Point3d(p.X, p.Y, 0));
                leader.HasArrowHead = true;
                if (ly != null) leader.Layer = ly;
                ms.AppendEntity(leader);
                tr.AddNewlyCreatedDBObject(leader, true);

                leader.Annotation = mt.ObjectId;
                leader.EvaluateLeader();

                string handle = leader.Handle.ToString();
                tr.Commit();
                return $"已创建引线，handle={handle}（文字 handle={mt.Handle}）";
            }
        }

        private static (Point3d center, double radius) ReadCircular(string handle)
        {
            var db = AcadContext.ActiveDocument.Database;
            using (AcadContext.ActiveDocument.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");
                var ent = tr.GetObject(id, OpenMode.ForRead);
                (Point3d, double) result = ent switch
                {
                    Circle c => (c.Center, c.Radius),
                    Arc a => (a.Center, a.Radius),
                    _ => throw new ArgumentException($"{handle} 不是圆或圆弧。"),
                };
                tr.Commit();
                return result;
            }
        }
    }
}
