using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    internal static class Measure
    {
        public static string Distance(double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            if (ang < 0) ang += 360;
            return new JObject
            {
                ["distance"] = Math.Round(dist, 6),
                ["dx"] = Math.Round(dx, 6),
                ["dy"] = Math.Round(dy, 6),
                ["angleDeg"] = Math.Round(ang, 6),
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        public static string Area(string handle)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");

                var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                double area, perimeter = double.NaN;

                switch (ent)
                {
                    case Circle c:
                        area = Math.PI * c.Radius * c.Radius;
                        perimeter = 2 * Math.PI * c.Radius;
                        break;
                    case Autodesk.AutoCAD.DatabaseServices.Polyline pl:
                        area = pl.Area;                 // 未闭合时 AutoCAD 按首尾相连算
                        perimeter = pl.Length;
                        break;
                    case Region rg:
                        area = rg.Area;
                        break;
                    case Hatch ht:
                        area = ht.Area;
                        break;
                    case Ellipse el:
                        // 完整椭圆面积
                        double major = el.MajorRadius, minor = el.MinorRadius;
                        area = Math.PI * major * minor;
                        break;
                    default:
                        try { area = ent.GetType().GetProperty("Area")?.GetValue(ent) is double a ? a : double.NaN; }
                        catch { area = double.NaN; }
                        break;
                }

                tr.Commit();

                if (double.IsNaN(area))
                    throw new InvalidOperationException($"实体类型 {ent.GetType().Name} 无法计算面积。");

                var o = new JObject
                {
                    ["handle"] = handle,
                    ["type"] = ent.GetType().Name,
                    ["area"] = Math.Round(area, 6),
                };
                if (!double.IsNaN(perimeter)) o["perimeter"] = Math.Round(perimeter, 6);
                return o.ToString(Newtonsoft.Json.Formatting.Indented);
            }
        }
    }
}
