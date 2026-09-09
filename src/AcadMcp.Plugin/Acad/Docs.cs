using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 多文档：列出 / 切换 / 打开 / 新建 / 关闭。
    ///
    /// 其余所有工具都作用于**当前活动文档**（MdiActiveDocument）。同时开多张图时，
    /// 先用 activate_document 切过去，再操作 —— 这就是本插件的"多文档隔离"模型：
    /// 一次只有一个目标文档，切换是显式的、可审计的。
    /// </summary>
    internal static class Docs
    {
        public static string List()
        {
            var dm = AcApp.DocumentManager;
            var active = dm.MdiActiveDocument;
            var arr = new JArray();

            int i = 0;
            foreach (Document d in dm)
            {
                bool isActive = ReferenceEquals(d, active);
                var o = new JObject
                {
                    ["index"] = i++,
                    ["name"] = d.Name,
                    ["fileName"] = Path.IsPathRooted(d.Name) ? Path.GetFileName(d.Name) : d.Name,
                    ["saved"] = Path.IsPathRooted(d.Name),
                    ["readOnly"] = d.IsReadOnly,
                    ["active"] = isActive,
                };
                if (isActive)
                {
                    // DBMOD 只反映当前文档，对非活动文档没有意义
                    try { o["modified"] = ToInt(AcApp.GetSystemVariable("DBMOD")) != 0; }
                    catch { }
                }
                arr.Add(o);
            }

            return new JObject
            {
                ["count"] = arr.Count,
                ["active"] = active?.Name ?? "(无)",
                ["documents"] = arr,
                ["note"] = "所有工具作用于 active 文档；用 activate_document 切换（传 index 或文件名片段）。",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>按 index 或文件名（片段，大小写不敏感）切换活动文档。</summary>
        public static string Activate(string? nameOrPath, int? index)
        {
            var dm = AcApp.DocumentManager;
            var docs = new List<Document>();
            foreach (Document d in dm) docs.Add(d);

            if (docs.Count == 0)
                throw new InvalidOperationException("AutoCAD 当前没有打开的图形。");

            Document target;
            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= docs.Count)
                    throw new ArgumentException($"index 超出范围 0-{docs.Count - 1}。用 list_documents 查看。");
                target = docs[index.Value];
            }
            else if (!string.IsNullOrWhiteSpace(nameOrPath))
            {
                var key = nameOrPath!.Trim();
                var hits = docs.FindAll(d =>
                    d.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hits.Count == 0)
                    throw new ArgumentException($"没有匹配 '{key}' 的已打开图形。用 list_documents 查看。");
                if (hits.Count > 1)
                    throw new ArgumentException(
                        $"'{key}' 匹配到 {hits.Count} 个图形：{string.Join(" / ", hits.ConvertAll(d => Path.GetFileName(d.Name)))}。请给更精确的名字或用 index。");
                target = hits[0];
            }
            else
            {
                throw new ArgumentException("需要 name 或 index 之一。");
            }

            if (ReferenceEquals(dm.MdiActiveDocument, target))
                return $"已经是活动文档：{target.Name}";

            string old = dm.MdiActiveDocument?.Name ?? "(无)";
            dm.MdiActiveDocument = target;
            return $"活动文档：{old} -> {target.Name}";
        }

        public static string Open(string path, bool readOnly)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空。");
            var p = path.Trim();
            if (!p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) && !Path.HasExtension(p)) p += ".dwg";
            if (!Path.IsPathRooted(p))
                throw new ArgumentException("path 必须是绝对路径，例如 D:\\work\\plan.dwg");
            if (!File.Exists(p))
                throw new ArgumentException($"文件不存在：{p}");

            var dm = AcApp.DocumentManager;
            foreach (Document d in dm)
            {
                if (string.Equals(Path.GetFullPath(d.Name), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase))
                {
                    dm.MdiActiveDocument = d;
                    return $"该图形已经打开，已切为活动文档：{d.Name}";
                }
            }

            var doc = dm.Open(p, readOnly);
            return $"已打开{(readOnly ? "（只读）" : "")}并激活：{doc?.Name ?? p}";
        }

        public static string New(string? template)
        {
            var dm = AcApp.DocumentManager;
            string tpl = string.IsNullOrWhiteSpace(template) ? "acadiso.dwt" : template!.Trim();

            if (Path.IsPathRooted(tpl) && !File.Exists(tpl))
                throw new ArgumentException($"样板文件不存在：{tpl}");

            Document doc;
            try
            {
                doc = dm.Add(tpl);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new InvalidOperationException(
                    $"新建图形失败（{ex.ErrorStatus}）：样板 '{tpl}' 可能不存在。试试 acad.dwt / acadiso.dwt，或给样板的绝对路径。");
            }

            dm.MdiActiveDocument = doc;
            return $"已新建图形（样板 {tpl}）并激活：{doc.Name}。尚未保存，用 save_as 存盘。";
        }

        /// <summary>关闭一个文档。save=true 存盘后关；save=false 丢弃修改（要 force=true）。</summary>
        public static string Close(string? nameOrPath, int? index, bool save, bool force)
        {
            var dm = AcApp.DocumentManager;
            var docs = new List<Document>();
            foreach (Document d in dm) docs.Add(d);

            if (docs.Count <= 1)
                throw new InvalidOperationException("这是最后一个打开的图形，拒绝关闭（AutoCAD 会失去操作目标）。");

            Document target;
            if (index.HasValue)
            {
                if (index.Value < 0 || index.Value >= docs.Count)
                    throw new ArgumentException($"index 超出范围 0-{docs.Count - 1}。");
                target = docs[index.Value];
            }
            else if (!string.IsNullOrWhiteSpace(nameOrPath))
            {
                var key = nameOrPath!.Trim();
                var hits = docs.FindAll(d => d.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hits.Count != 1)
                    throw new ArgumentException(hits.Count == 0
                        ? $"没有匹配 '{key}' 的已打开图形。"
                        : $"'{key}' 匹配到 {hits.Count} 个图形，请更精确或用 index。");
                target = hits[0];
            }
            else
            {
                target = dm.MdiActiveDocument ?? throw new InvalidOperationException("没有活动文档。");
            }

            string name = target.Name;

            if (save)
            {
                if (!Path.IsPathRooted(name))
                    throw new InvalidOperationException("该图形从未保存过，没有可写回的路径。先 save_as，或用 save:false + force:true 丢弃。");
                target.CloseAndSave(name);
                return $"已保存并关闭：{name}";
            }

            if (!force)
                throw new InvalidOperationException(
                    $"save:false 会丢弃 {Path.GetFileName(name)} 的未保存修改。确认要丢弃请再传 force:true。");

            target.CloseAndDiscard();
            return $"已关闭并丢弃未保存修改：{name}";
        }

        private static int ToInt(object v) => v is short s ? s : v is int i ? i : Convert.ToInt32(v);
    }
}
