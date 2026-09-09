using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 布局（图纸空间）与浮动视口。
    ///
    /// 术语：模型空间画实体（1:1 真实尺寸），布局是"图纸"，布局上的**视口**是开在图纸上的窗，
    /// 按某个比例展示模型空间的一块区域。出图 = 打印布局。
    /// </summary>
    internal static class Layouts
    {
        public const string ModelName = "Model";

        // ---------- 查询 ----------

        public static string List(bool includeViewports)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var lm = LayoutManager.Current;
            var arr = new JArray();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var items = new List<Layout>();
                foreach (DBDictionaryEntry e in dict)
                    if (tr.GetObject(e.Value, OpenMode.ForRead) is Layout lay) items.Add(lay);

                items.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

                foreach (var lay in items)
                {
                    var vpIds = lay.GetViewports();
                    int floating = 0;
                    foreach (ObjectId vid in vpIds)
                        if (tr.GetObject(vid, OpenMode.ForRead) is Viewport v && v.Number != 1) floating++;

                    var o = new JObject
                    {
                        ["name"] = lay.LayoutName,
                        ["current"] = string.Equals(lay.LayoutName, lm.CurrentLayout, StringComparison.OrdinalIgnoreCase),
                        ["isModel"] = lay.ModelType,
                        ["tabOrder"] = lay.TabOrder,
                        ["plotDevice"] = lay.PlotConfigurationName,
                        ["paperSize"] = lay.CanonicalMediaName,
                        ["paperMm"] = $"{lay.PlotPaperSize.X:0.#} x {lay.PlotPaperSize.Y:0.#}",
                        ["rotation"] = lay.PlotRotation.ToString(),
                        ["viewports"] = floating,
                    };

                    if (includeViewports && floating > 0)
                        o["viewportList"] = ViewportArray(tr, lay);

                    arr.Add(o);
                }
                tr.Commit();
            }

            return new JObject
            {
                ["current"] = lm.CurrentLayout,
                ["count"] = arr.Count,
                ["layouts"] = arr,
                ["note"] = "viewports 只统计浮动视口（不含图纸本身那个 Number=1 的视口）。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        public static string ListViewports(string? layoutName)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lay = ResolveLayout(tr, db, layoutName);
                var arr = ViewportArray(tr, lay);
                tr.Commit();

                return new JObject
                {
                    ["layout"] = lay.LayoutName,
                    ["count"] = arr.Count,
                    ["viewports"] = arr,
                }.ToString(Newtonsoft.Json.Formatting.Indented);
            }
        }

        // ---------- 布局增删与切换 ----------

        public static string SetCurrent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空。");
            var doc = AcadContext.ActiveDocument;
            var lm = LayoutManager.Current;

            using (doc.LockDocument())
            {
                string target = ResolveName(doc.Database, name);
                if (string.Equals(lm.CurrentLayout, target, StringComparison.OrdinalIgnoreCase))
                    return $"已经在 '{target}'，未切换。";

                string old = lm.CurrentLayout;
                lm.CurrentLayout = target;
                return $"当前布局：{old} -> {target}（TILEMODE={(target == ModelName ? 1 : 0)}）";
            }
        }

        public static string Create(string name, string? plotDevice, string? paperSize, bool landscape, bool setCurrent)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空。");
            name = name.Trim();
            if (string.Equals(name, ModelName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("布局名不能是 Model（那是模型空间）。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var lm = LayoutManager.Current;

            using (doc.LockDocument())
            {
                foreach (var existing in Names(db))
                    if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException($"布局 '{name}' 已存在。");

                lm.CreateLayout(name);

                string applied = "（沿用默认打印设置）";
                if (plotDevice != null || paperSize != null || landscape)
                    applied = Plot.ApplyPageSetup(name, plotDevice, paperSize, landscape);

                if (setCurrent) lm.CurrentLayout = name;

                // AutoCAD 新建布局时会自带一个铺满图纸的默认视口，说明一下免得叠加出两个
                int autoViewports = 0;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lay = (Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead);
                    foreach (ObjectId vid in lay.GetViewports())
                        if (tr.GetObject(vid, OpenMode.ForRead) is Viewport v && v.Number != 1) autoViewports++;
                    tr.Commit();
                }

                return $"已创建布局 '{name}'。{applied}" + (setCurrent ? " 已切为当前布局。" : "") +
                       (autoViewports > 0
                           ? $" AutoCAD 已自带 {autoViewports} 个默认视口（用 list_viewports 看，可 set_viewport 改或 erase_entity 删）。"
                           : "");
            }
        }

        public static string Delete(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name 不能为空。");
            if (string.Equals(name, ModelName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不能删除模型空间。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var lm = LayoutManager.Current;

            using (doc.LockDocument())
            {
                var names = Names(db);
                names.Remove(ModelName);
                if (names.Count <= 1)
                    throw new InvalidOperationException("图形至少要保留一个布局，拒绝删除最后一个。");

                if (!lm.GetLayoutId(name).IsValid)
                    throw new ArgumentException($"布局 '{name}' 不存在。用 list_layouts 查看。");

                if (string.Equals(lm.CurrentLayout, name, StringComparison.OrdinalIgnoreCase))
                    lm.CurrentLayout = ModelName;   // 不能删当前布局

                lm.DeleteLayout(name);
                return $"已删除布局 '{name}'。";
            }
        }

        // ---------- 视口 ----------

        /// <summary>
        /// 在布局上新建浮动视口。centerX/centerY/width/height 是**图纸坐标（毫米）**；
        /// scale=100 表示 1:100（1 图纸单位 = 100 模型单位）；viewCenter 是视口对准的模型空间点。
        /// </summary>
        public static string AddViewport(string? layoutName, double cx, double cy, double w, double h,
            double? scale, double? viewCx, double? viewCy, bool locked)
        {
            if (w <= 0 || h <= 0) throw new ArgumentException("width / height 必须大于 0。");
            if (scale.HasValue && scale.Value <= 0) throw new ArgumentException("scale 必须大于 0（1:scale）。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var lm = LayoutManager.Current;

            using (doc.LockDocument())
            {
                string target;
                using (var tr0 = db.TransactionManager.StartTransaction())
                {
                    target = ResolveLayout(tr0, db, layoutName).LayoutName;
                    tr0.Commit();
                }
                if (string.Equals(target, ModelName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("模型空间不能加浮动视口。先 create_layout / set_layout 到一个布局。");

                // 视口的 On 只在其所属布局为当前布局时可靠生效，因此先切过去
                string previous = lm.CurrentLayout;
                if (!string.Equals(previous, target, StringComparison.OrdinalIgnoreCase))
                    lm.CurrentLayout = target;

                string handle;
                string onNote = "";
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lay = (Layout)tr.GetObject(lm.GetLayoutId(target), OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);

                    var vp = new Viewport
                    {
                        CenterPoint = new Point3d(cx, cy, 0),
                        Width = w,
                        Height = h,
                    };
                    btr.AppendEntity(vp);
                    tr.AddNewlyCreatedDBObject(vp, true);

                    try { vp.On = true; }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        onNote = $" 注意：视口未能打开（{ex.ErrorStatus}），可能超过 MAXACTVP 上限；" +
                                 "切到该布局后用 set_viewport on:true 重试。";
                    }

                    if (viewCx.HasValue || viewCy.HasValue)
                        vp.ViewCenter = new Point2d(viewCx ?? vp.ViewCenter.X, viewCy ?? vp.ViewCenter.Y);

                    if (scale.HasValue)
                        vp.CustomScale = 1.0 / scale.Value;

                    if (locked) vp.Locked = true;

                    handle = vp.Handle.ToString();
                    tr.Commit();
                }

                string switched = string.Equals(previous, target, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $" 当前布局已从 '{previous}' 切到 '{target}'。";

                return $"已在布局 '{target}' 创建视口 handle={handle}，" +
                       $"图纸位置 ({cx:0.#},{cy:0.#}) 尺寸 {w:0.#}x{h:0.#}" +
                       (scale.HasValue ? $"，比例 1:{scale.Value:0.###}" : "") +
                       (locked ? "，已锁定" : "") + "。" + switched + onNote;
            }
        }

        /// <summary>改视口：比例、对准的模型点、图纸位置尺寸、开关、锁定。</summary>
        public static string SetViewport(string handle, double? scale, double? viewCx, double? viewCy,
            double? cx, double? cy, double? w, double? h, bool? on, bool? locked)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var changes = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}。用 list_viewports 获取视口 handle。");

                if (!(tr.GetObject(id, OpenMode.ForWrite) is Viewport vp))
                    throw new ArgumentException($"handle {handle} 不是视口（Viewport）。");
                if (vp.Number == 1)
                    throw new ArgumentException("这是图纸本身的视口（Number=1），不能改。");

                // 锁定的视口不能改比例 / 视图，先临时解锁
                bool wasLocked = vp.Locked;
                if (wasLocked && (scale.HasValue || viewCx.HasValue || viewCy.HasValue)) vp.Locked = false;

                if (on.HasValue)
                {
                    try { vp.On = on.Value; changes.Add(on.Value ? "已打开" : "已关闭"); }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"打开/关闭视口失败（{ex.ErrorStatus}）：先用 set_layout 切到该视口所在的布局再试" +
                            "（超过 MAXACTVP 上限时也会失败）。");
                    }
                }
                if (cx.HasValue || cy.HasValue)
                {
                    var p = vp.CenterPoint;
                    vp.CenterPoint = new Point3d(cx ?? p.X, cy ?? p.Y, p.Z);
                    changes.Add($"图纸位置 -> ({vp.CenterPoint.X:0.#},{vp.CenterPoint.Y:0.#})");
                }
                if (w.HasValue) { vp.Width = w.Value; changes.Add($"宽 -> {w.Value:0.#}"); }
                if (h.HasValue) { vp.Height = h.Value; changes.Add($"高 -> {h.Value:0.#}"); }
                if (viewCx.HasValue || viewCy.HasValue)
                {
                    var c = vp.ViewCenter;
                    vp.ViewCenter = new Point2d(viewCx ?? c.X, viewCy ?? c.Y);
                    changes.Add($"对准模型点 -> ({vp.ViewCenter.X:0.###},{vp.ViewCenter.Y:0.###})");
                }
                if (scale.HasValue)
                {
                    if (scale.Value <= 0) throw new ArgumentException("scale 必须大于 0（1:scale）。");
                    vp.CustomScale = 1.0 / scale.Value;
                    changes.Add($"比例 -> 1:{scale.Value:0.###}");
                }

                bool finalLocked = locked ?? wasLocked;
                vp.Locked = finalLocked;
                if (locked.HasValue) changes.Add(locked.Value ? "已锁定" : "已解锁");

                tr.Commit();
            }

            if (changes.Count == 0) return $"视口 {handle}：没有传入任何要改的项。";
            return $"视口 {handle}：" + string.Join("，", changes) + "。";
        }

        // ---------- 内部辅助 ----------

        private static JArray ViewportArray(Transaction tr, Layout lay)
        {
            var arr = new JArray();
            foreach (ObjectId vid in lay.GetViewports())
            {
                if (!(tr.GetObject(vid, OpenMode.ForRead) is Viewport vp) || vp.Number == 1) continue;

                double cs = vp.CustomScale;
                arr.Add(new JObject
                {
                    ["handle"] = vp.Handle.ToString(),
                    ["number"] = vp.Number,
                    ["on"] = vp.On,
                    ["locked"] = vp.Locked,
                    ["paperCenter"] = $"{vp.CenterPoint.X:0.###},{vp.CenterPoint.Y:0.###}",
                    ["paperSize"] = $"{vp.Width:0.###} x {vp.Height:0.###}",
                    ["viewCenter"] = $"{vp.ViewCenter.X:0.###},{vp.ViewCenter.Y:0.###}",
                    ["viewHeight"] = Math.Round(vp.ViewHeight, 4),
                    ["scale"] = cs > 0 ? $"1:{1.0 / cs:0.###}" : "自定义/标准比例",
                    ["layer"] = vp.Layer,
                });
            }
            return arr;
        }

        /// <summary>layoutName 为空时取当前布局（模型空间时报错说明）。</summary>
        private static Layout ResolveLayout(Transaction tr, Database db, string? layoutName)
        {
            var lm = LayoutManager.Current;
            string name = layoutName != null ? ResolveName(db, layoutName) : lm.CurrentLayout;

            var id = lm.GetLayoutId(name);
            if (!id.IsValid)
                throw new ArgumentException($"布局 '{name}' 不存在。用 list_layouts 查看可用布局。");
            return (Layout)tr.GetObject(id, OpenMode.ForRead);
        }

        /// <summary>把用户给的名字规范化到实际布局名（大小写不敏感；model/模型 -> Model）。</summary>
        internal static string ResolveName(Database db, string name)
        {
            var n = name.Trim();
            if (string.Equals(n, ModelName, StringComparison.OrdinalIgnoreCase) || n == "模型")
                return ModelName;

            foreach (var existing in Names(db))
                if (string.Equals(existing, n, StringComparison.OrdinalIgnoreCase))
                    return existing;

            throw new ArgumentException($"布局 '{name}' 不存在。用 list_layouts 查看可用布局。");
        }

        internal static List<string> Names(Database db)
        {
            var list = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                    if (tr.GetObject(e.Value, OpenMode.ForRead) is Layout lay) list.Add(lay.LayoutName);
                tr.Commit();
            }
            return list;
        }
    }
}
