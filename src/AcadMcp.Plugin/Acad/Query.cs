using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    /// <summary>进程内"当前选择集"—— select 工具写入，修改类工具用 useSelection 读取。</summary>
    internal static class SelectionStore
    {
        private static List<string> _handles = new List<string>();
        private static DateTime _at = DateTime.MinValue;

        public static void Set(IEnumerable<string> handles)
        {
            _handles = new List<string>(handles);
            _at = DateTime.Now;
        }

        public static IReadOnlyList<string> Get() => _handles;
        public static bool HasSelection => _handles.Count > 0;
        public static string Describe() =>
            _handles.Count == 0 ? "（空）" : $"{_handles.Count} 个实体（选于 {_at:HH:mm:ss}）";
    }

    internal static class Query
    {
        public static string Status()
        {
            var mdi = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (mdi == null)
            {
                return new JObject
                {
                    ["ready"] = false,
                    ["reason"] = "AutoCAD 当前没有打开的图形。请在 AutoCAD 中新建或打开一个 DWG 后再操作。",
                }.ToString(Newtonsoft.Json.Formatting.Indented);
            }

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            var o = new JObject
            {
                ["ready"] = true,
                ["drawing"] = doc.Name,
                ["saved"] = System.IO.Path.IsPathRooted(doc.Name),
                ["units"] = db.Insunits.ToString(),
                ["space"] = db.TileMode ? "model" : "paper",
                ["layout"] = LayoutManager.Current.CurrentLayout,
                ["openDocuments"] = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.Count,
            };

            // Application.Idle 运行在应用上下文，UpdateExt 会写 sysvar，必须锁文档
            using (doc.LockDocument())
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    int layerCount = 0;
                    foreach (ObjectId _ in lt) layerCount++;
                    o["layerCount"] = layerCount;

                    var clayer = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
                    o["currentLayer"] = clayer.Name;
                    tr.Commit();
                }

                db.UpdateExt(false);
            }

            o["extMin"] = $"{db.Extmin.X:0.###},{db.Extmin.Y:0.###}";
            o["extMax"] = $"{db.Extmax.X:0.###},{db.Extmax.Y:0.###}";
            o["selection"] = SelectionStore.Describe();
            o["evalLisp"] = Lisp.Enabled;
            o["readOnly"] = Mcp.Safety.ReadOnly;
            o["authRequired"] = Mcp.Safety.AuthRequired;
            o["backups"] = Mcp.Safety.DescribeBackups();
            o["marks"] = Mcp.Safety.DescribeMarks();
            o["logFile"] = Mcp.Log.CurrentFile;
            o["logWrites"] = Mcp.Log.WriteCount;
            if (Mcp.Log.LastWriteError != null) o["logError"] = Mcp.Log.LastWriteError;
            return o.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        public static string Entities(string? type, string? layer, int limit)
        {
            if (limit <= 0 || limit > 5000) limit = 500;

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var arr = new JArray();
            int returned = 0, matched = 0;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = Space.Current(tr, db, OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;

                    string dxf = ent.GetRXClass().DxfName;
                    string clr = ent.GetType().Name;

                    if (type != null
                        && !dxf.Equals(type, StringComparison.OrdinalIgnoreCase)
                        && !clr.Equals(type, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (layer != null && !ent.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matched++;
                    if (returned >= limit) continue;

                    var item = new JObject
                    {
                        ["handle"] = ent.Handle.ToString(),
                        ["type"] = clr,
                        ["dxf"] = dxf,
                        ["layer"] = ent.Layer,
                    };
                    try
                    {
                        var ext = ent.GeometricExtents;
                        item["bbox"] = $"{ext.MinPoint.X:0.###},{ext.MinPoint.Y:0.###} .. {ext.MaxPoint.X:0.###},{ext.MaxPoint.Y:0.###}";
                    }
                    catch { /* 某些实体无几何范围 */ }

                    arr.Add(item);
                    returned++;
                }
                tr.Commit();
            }

            var result = new JObject
            {
                // 扫的是**当前空间**，不是整张图。切到布局之后这里只看得见图纸空间的东西，
                // 模型空间的实体一个都不会出现——不写明这一点，Agent 会以为图被清空了。
                ["space"] = Space.CurrentName(db),
                ["matched"] = matched,
                ["returned"] = returned,
                ["truncated"] = matched > returned,
                ["entities"] = arr,
            };
            return result.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// 按条件选择模型空间实体，结果存入 <see cref="SelectionStore"/>（后续修改类工具可用 useSelection 引用）。
        /// window/crossing：用实体包围盒判断（完全在内 / 相交）。
        /// </summary>
        public static string Select(string? type, string? layer, int? colorIndex, string? blockName,
            double[]? win, bool crossing)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var handles = new List<string>();
            var sample = new JArray();

            double wx1 = 0, wy1 = 0, wx2 = 0, wy2 = 0;
            bool useWin = win != null && win.Length == 4;
            if (useWin)
            {
                wx1 = Math.Min(win![0], win[2]); wx2 = Math.Max(win[0], win[2]);
                wy1 = Math.Min(win[1], win[3]); wy2 = Math.Max(win[1], win[3]);
            }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = Space.Current(tr, db, OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;

                    string dxf = ent.GetRXClass().DxfName;
                    string clr = ent.GetType().Name;

                    if (type != null
                        && !dxf.Equals(type, StringComparison.OrdinalIgnoreCase)
                        && !clr.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                    if (layer != null && !ent.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)) continue;
                    if (colorIndex.HasValue && ent.ColorIndex != colorIndex.Value) continue;
                    if (blockName != null)
                    {
                        if (!(ent is BlockReference br) || !br.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    if (useWin)
                    {
                        Extents3d ext;
                        try { ext = ent.GeometricExtents; }
                        catch { continue; }
                        bool inside = ext.MinPoint.X >= wx1 && ext.MaxPoint.X <= wx2
                                   && ext.MinPoint.Y >= wy1 && ext.MaxPoint.Y <= wy2;
                        bool intersects = ext.MinPoint.X <= wx2 && ext.MaxPoint.X >= wx1
                                       && ext.MinPoint.Y <= wy2 && ext.MaxPoint.Y >= wy1;
                        if (crossing ? !intersects : !inside) continue;
                    }

                    var h = ent.Handle.ToString();
                    handles.Add(h);
                    if (sample.Count < 50)
                        sample.Add(new JObject { ["handle"] = h, ["type"] = clr, ["layer"] = ent.Layer });
                }
                tr.Commit();
            }

            SelectionStore.Set(handles);

            return new JObject
            {
                ["count"] = handles.Count,
                ["handles"] = new JArray(handles),
                ["sample"] = sample,
                // 只在当前空间里选。切到布局之后选不到模型空间的实体，这不是 bug，
                // 是与 AutoCAD 的 SELECT 一致的行为——但必须说出来，否则「怎么一个都选不中」会被当成故障。
                ["space"] = Space.CurrentName(db),
                ["note"] = handles.Count == 0
                    ? "没有匹配的实体，当前选择集已清空。"
                    : "已设为当前选择集，move/copy/rotate/scale/mirror/erase_entity/hatch 可传 useSelection:true 引用。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }
    }
}
