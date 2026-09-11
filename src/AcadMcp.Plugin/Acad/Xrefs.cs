using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 外部参照（xref）：附着 / 列出 / 重载 / 卸载 / 拆离 / 绑定。
    /// xref 在宿主图里表现为一个特殊图块定义（IsFromExternalReference），插入点是一个块引用。
    /// </summary>
    internal static class Xrefs
    {
        public static string List()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var arr = new JArray();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!btr.IsFromExternalReference) continue;

                    var refIds = btr.GetBlockReferenceIds(true, true);
                    var handles = new JArray();
                    foreach (ObjectId rid in refIds)
                        if (tr.GetObject(rid, OpenMode.ForRead) is BlockReference br)
                            handles.Add(br.Handle.ToString());

                    arr.Add(new JObject
                    {
                        ["name"] = btr.Name,
                        ["path"] = btr.PathName,
                        ["resolvedPath"] = ResolvePath(doc, btr.PathName) ?? "(找不到文件)",
                        ["type"] = btr.IsFromOverlayReference ? "overlay（覆盖）" : "attach（附着）",
                        ["status"] = btr.XrefStatus.ToString(),
                        ["unloaded"] = btr.IsUnloaded,
                        ["insertCount"] = handles.Count,
                        ["insertHandles"] = handles,
                    });
                }
                tr.Commit();
            }

            return new JObject
            {
                ["count"] = arr.Count,
                ["xrefs"] = arr,
                ["note"] = arr.Count == 0 ? "当前图形没有外部参照。" : "name 可直接用于 reload / unload / detach / bind_xref。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>附着一个 DWG 作为外部参照，并在模型空间插入一个引用。</summary>
        public static string Attach(string path, double x, double y, double scale, double rotationDeg,
            bool overlay, string? name)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空。");
            if (scale <= 0) throw new ArgumentException("scale 必须大于 0。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            string full = ResolvePath(doc, path)
                ?? throw new ArgumentException($"找不到文件：{path}（相对路径按当前 dwg 所在目录解析）。");

            if (Path.IsPathRooted(doc.Name) &&
                string.Equals(Path.GetFullPath(full), Path.GetFullPath(doc.Name), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不能把图形自己附着为外部参照。");

            string blockName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(full)
                : name!.Trim();

            using (doc.LockDocument())
            {
                ObjectId btrId = overlay
                    ? db.OverlayXref(full, blockName)
                    : db.AttachXref(full, blockName);

                if (btrId.IsNull)
                    throw new InvalidOperationException($"附着失败：{full}（文件可能损坏，或已被其他方式引用）。");

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = Space.Current(tr, db, OpenMode.ForWrite);

                    var br = new BlockReference(new Point3d(x, y, 0), btrId)
                    {
                        ScaleFactors = new Scale3d(scale),
                        Rotation = rotationDeg * Math.PI / 180.0,
                    };
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    var handle = br.Handle.ToString();
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    string status = btr.XrefStatus.ToString();
                    tr.Commit();

                    return $"已{(overlay ? "覆盖" : "附着")}外部参照 '{btr.Name}' <- {full}\n" +
                           $"插入点 ({x:0.###},{y:0.###})，比例 {scale:0.###}，旋转 {rotationDeg:0.##}°，" +
                           $"块引用 handle={handle}，状态 {status}。";
                }
            }
        }

        /// <summary>重载 / 卸载 / 拆离。names 为空表示全部。</summary>
        public static string Operate(string op, IReadOnlyList<string> names)
        {
            op = (op ?? "").Trim().ToLowerInvariant();

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                var ids = new ObjectIdCollection();
                var picked = new List<string>();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var found = new List<string>();
                    foreach (ObjectId id in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!btr.IsFromExternalReference) continue;
                        found.Add(btr.Name);

                        bool wanted = names.Count == 0 ||
                            names.Any(n => string.Equals(n, btr.Name, StringComparison.OrdinalIgnoreCase));
                        if (!wanted) continue;

                        ids.Add(id);
                        picked.Add(btr.Name);
                    }
                    tr.Commit();

                    if (found.Count == 0)
                        throw new InvalidOperationException("当前图形没有外部参照。");
                    if (ids.Count == 0)
                        throw new ArgumentException(
                            $"没有匹配的外部参照。现有：{string.Join(" / ", found)}。");
                }

                switch (op)
                {
                    case "reload":
                        db.ReloadXrefs(ids);
                        return $"已重载 {ids.Count} 个外部参照：{string.Join("、", picked)}。";

                    case "unload":
                        db.UnloadXrefs(ids);
                        return $"已卸载 {ids.Count} 个外部参照（定义保留，不显示）：{string.Join("、", picked)}。";

                    case "detach":
                        foreach (ObjectId id in ids) db.DetachXref(id);
                        return $"已拆离 {ids.Count} 个外部参照（定义与引用一并删除）：{string.Join("、", picked)}。";

                    default:
                        throw new ArgumentException($"op 只能是 reload / unload / detach，收到 '{op}'。");
                }
            }
        }

        /// <summary>绑定：把 xref 内容变成宿主图自己的图块。insertBind=true 时不加前缀（等同 XREF Bind Insert）。</summary>
        public static string Bind(IReadOnlyList<string> names, bool insertBind)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                var ids = new ObjectIdCollection();
                var picked = new List<string>();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!btr.IsFromExternalReference) continue;
                        if (names.Count != 0 &&
                            !names.Any(n => string.Equals(n, btr.Name, StringComparison.OrdinalIgnoreCase))) continue;

                        if (btr.XrefStatus != XrefStatus.Resolved)
                            throw new InvalidOperationException(
                                $"外部参照 '{btr.Name}' 当前状态是 {btr.XrefStatus}，先 reload 成功后再绑定。");

                        ids.Add(id);
                        picked.Add(btr.Name);
                    }
                    tr.Commit();
                }

                if (ids.Count == 0)
                    throw new ArgumentException("没有匹配的、已解析的外部参照。用 list_xrefs 查看。");

                db.BindXrefs(ids, insertBind);
                return $"已绑定 {ids.Count} 个外部参照为本地图块（{(insertBind ? "Insert 方式，不加前缀" : "Bind 方式，符号名加 $0$ 前缀")}）：" +
                       string.Join("、", picked) + "。";
            }
        }

        /// <summary>相对路径按当前 dwg 目录解析；返回 null 表示文件不存在。</summary>
        private static string? ResolvePath(Autodesk.AutoCAD.ApplicationServices.Document doc, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var p = path.Trim();
            if (!p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(p))
                p += ".dwg";

            if (Path.IsPathRooted(p))
                return File.Exists(p) ? p : null;

            if (Path.IsPathRooted(doc.Name))
            {
                var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(doc.Name)!, p));
                if (File.Exists(candidate)) return candidate;
            }

            return File.Exists(p) ? Path.GetFullPath(p) : null;
        }
    }
}
