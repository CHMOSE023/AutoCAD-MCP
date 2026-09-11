using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 空间校验：房间有没有重叠、有没有跑出轮廓、该挨着的是不是真挨着。
    ///
    /// 为什么要有这组工具：面积和数量对不代表画对了。实测里翻过两次车 ——
    /// 车位块插反方向撞上行道树、二层房间跑到外墙线以外，两次的面积校核都是满分。
    /// 这类错只有看图才发现，而看图没法自动化。这三个检查把"位置对不对"也变成可判定的。
    ///
    /// 判定都基于**包围盒**（轴对齐矩形），对建筑平面里的矩形房间足够准；
    /// 斜放或异形的房间会偏保守（可能多报重叠），结果里会注明。
    /// </summary>
    internal static class Check
    {
        private readonly struct Box
        {
            public readonly string Handle, Type, Layer;
            public readonly double MinX, MinY, MaxX, MaxY;
            public readonly string? Label;

            public Box(string handle, string type, string layer, Extents3d e, string? label)
            {
                Handle = handle; Type = type; Layer = layer; Label = label;
                MinX = e.MinPoint.X; MinY = e.MinPoint.Y;
                MaxX = e.MaxPoint.X; MaxY = e.MaxPoint.Y;
            }

            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;
            public string Range => $"{MinX:0.#},{MinY:0.#} .. {MaxX:0.#},{MaxY:0.#}";
            public string Name => Label ?? $"{Type} {Handle}";
        }

        /// <summary>重叠判定的容差：小于这个值的搭接当作共边，不算重叠（房间贴墙共线是正常的）。</summary>
        private const double Tol = 1.0;

        /// <summary>
        /// 检查一组实体两两之间有没有重叠。典型用法：把所有房间放一层，查功能分区有没有画重。
        /// </summary>
        public static string Overlap(IReadOnlyList<string> handles, string? layer, double minOverlapArea)
        {
            var boxes = Collect(handles, layer);
            if (boxes.Count < 2)
                return new JObject
                {
                    ["checked"] = boxes.Count,
                    ["result"] = "PASS",
                    ["note"] = "不足 2 个实体，无从比较。",
                }.ToString(Newtonsoft.Json.Formatting.Indented);

            var hits = new JArray();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i];
                    var b = boxes[j];

                    double ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
                    double oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
                    if (ox <= Tol || oy <= Tol) continue;         // 不相交，或只是共边

                    double area = ox * oy;
                    if (area < minOverlapArea) continue;

                    hits.Add(new JObject
                    {
                        ["a"] = a.Name,
                        ["b"] = b.Name,
                        ["overlap"] = $"{ox:0.#} x {oy:0.#}",
                        ["overlapArea"] = Math.Round(area, 2),
                    });
                }

            return new JObject
            {
                ["checked"] = boxes.Count,
                ["result"] = hits.Count == 0 ? "PASS" : "FAIL",
                ["overlaps"] = hits,
                ["note"] = hits.Count == 0
                    ? "两两都不重叠（共边不算）。"
                    : "存在重叠。按包围盒判定，斜放 / 异形实体可能误报，可结合 capture_view 复核。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// 检查一组实体是否都落在某个边界实体内部。典型用法：房间是否都在建筑轮廓 / 用地红线内。
        /// </summary>
        public static string Inside(IReadOnlyList<string> handles, string? layer, string boundaryHandle)
        {
            Box bound;
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, boundaryHandle, out var bid))
                    throw new ArgumentException($"未找到边界 handle {boundaryHandle}");
                if (!(tr.GetObject(bid, OpenMode.ForRead) is Entity be))
                    throw new ArgumentException($"handle {boundaryHandle} 不是实体。");
                try { bound = new Box(be.Handle.ToString(), be.GetType().Name, be.Layer, be.GeometricExtents, null); }
                catch { throw new ArgumentException("边界实体没有几何范围，无法作为边界。"); }
                tr.Commit();
            }

            var boxes = Collect(handles, layer).Where(b => b.Handle != bound.Handle).ToList();
            var outside = new JArray();

            foreach (var b in boxes)
            {
                double outLeft = bound.MinX - b.MinX;   // 正数 = 左边出界
                double outRight = b.MaxX - bound.MaxX;
                double outBottom = bound.MinY - b.MinY;
                double outTop = b.MaxY - bound.MaxY;
                double worst = Math.Max(Math.Max(outLeft, outRight), Math.Max(outBottom, outTop));
                if (worst <= Tol) continue;

                var dirs = new List<string>();
                if (outLeft > Tol) dirs.Add($"左 {outLeft:0.#}");
                if (outRight > Tol) dirs.Add($"右 {outRight:0.#}");
                if (outBottom > Tol) dirs.Add($"下 {outBottom:0.#}");
                if (outTop > Tol) dirs.Add($"上 {outTop:0.#}");

                outside.Add(new JObject
                {
                    ["entity"] = b.Name,
                    ["range"] = b.Range,
                    ["outBy"] = string.Join("、", dirs),
                });
            }

            return new JObject
            {
                ["boundary"] = bound.Range,
                ["checked"] = boxes.Count,
                ["result"] = outside.Count == 0 ? "PASS" : "FAIL",
                ["outside"] = outside,
                ["note"] = outside.Count == 0
                    ? "全部在边界内。"
                    : "有实体越界，outBy 是各方向超出的距离。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// 检查两个实体是否相邻（包围盒共边或搭接）。
        /// 对应任务书的功能关系图 —— 候车大厅要挨着检票、检票要挨着发车站台，这类要求可以逐条核。
        /// gap 是允许的缝隙（墙厚、走廊宽度）。
        /// </summary>
        public static string Adjacency(string handleA, string handleB, double gap)
        {
            var boxes = Collect(new[] { handleA, handleB }, null);
            if (boxes.Count != 2)
                throw new ArgumentException("需要两个有效且有几何范围的实体 handle。");

            var a = boxes[0];
            var b = boxes[1];

            // 两个方向分别算：重叠为正、有缝为负
            double ox = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double oy = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);

            bool overlapping = ox > Tol && oy > Tol;

            // 相邻 = 一个方向有投影重叠，另一个方向的间距在 gap 之内
            double gapX = -ox;   // 正数表示 X 方向分开这么远
            double gapY = -oy;
            bool adjX = oy > Tol && gapX <= gap + Tol && gapX >= -Tol;   // 左右挨着
            bool adjY = ox > Tol && gapY <= gap + Tol && gapY >= -Tol;   // 上下挨着

            string verdict, why;
            if (overlapping)
            {
                verdict = "OVERLAP";
                why = $"两者重叠 {ox:0.#} x {oy:0.#}，不是相邻而是压在一起。";
            }
            else if (adjX || adjY)
            {
                verdict = "PASS";
                why = adjX
                    ? $"左右相邻，水平间距 {Math.Max(gapX, 0):0.#}，竖向搭接长度 {oy:0.#}。"
                    : $"上下相邻，竖向间距 {Math.Max(gapY, 0):0.#}，水平搭接长度 {ox:0.#}。";
            }
            else
            {
                verdict = "FAIL";
                double dx = Math.Max(gapX, 0), dy = Math.Max(gapY, 0);
                why = (ox <= Tol && oy <= Tol)
                    ? $"两个方向都不搭接（错开 {dx:0.#} x {dy:0.#}），只是斜对角，不算相邻。"
                    : $"间距 {Math.Max(dx, dy):0.#} 超过允许的 {gap:0.#}。";
            }

            return new JObject
            {
                ["a"] = a.Name,
                ["aRange"] = a.Range,
                ["b"] = b.Name,
                ["bRange"] = b.Range,
                ["gapAllowed"] = gap,
                ["result"] = verdict,
                ["detail"] = why,
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>按 handle 列表或图层名收集实体的包围盒；文字类实体用其内容当标签，便于看懂结果。</summary>
        private static List<Box> Collect(IReadOnlyList<string> handles, string? layer)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var list = new List<Box>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ids = new List<ObjectId>();

                if (handles != null && handles.Count > 0)
                {
                    foreach (var h in handles)
                    {
                        if (!Draw.TryGetObjectId(db, h, out var id))
                            throw new ArgumentException($"未找到 handle {h}");
                        ids.Add(id);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(layer))
                {
                    var ms = Space.Current(tr, db, OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity e
                            && e.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                            ids.Add(id);
                    }
                }
                else
                {
                    throw new ArgumentException("需要 handles 或 layer 之一，用来指定要检查的实体。");
                }

                foreach (var id in ids)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                    Extents3d ext;
                    try { ext = ent.GeometricExtents; }
                    catch { continue; }   // 没有几何范围的跳过（空文字等）

                    string? label = ent switch
                    {
                        DBText t => t.TextString,
                        MText m => m.Text,
                        BlockReference br => br.Name,
                        _ => null,
                    };
                    list.Add(new Box(ent.Handle.ToString(), ent.GetType().Name, ent.Layer, ext, label));
                }
                tr.Commit();
            }

            return list;
        }
    }
}
