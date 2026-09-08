using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Acad
{
    internal static class ViewDoc
    {
        public static string ZoomExtents()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            using (doc.LockDocument())
            {
                db.UpdateExt(true);
                Point3d min = db.Extmin, max = db.Extmax;
                double w = max.X - min.X, h = max.Y - min.Y;
                if (w <= 1e-9 || h <= 1e-9)
                    return "图形为空或范围无效，未缩放。";

                using (var view = ed.GetCurrentView())
                {
                    view.CenterPoint = new Point2d((min.X + max.X) / 2.0, (min.Y + max.Y) / 2.0);
                    view.Width = w * 1.05;
                    view.Height = h * 1.05;
                    ed.SetCurrentView(view);
                }
                return $"已缩放到范围 ({min.X:0.#},{min.Y:0.#}) - ({max.X:0.#},{max.Y:0.#})。";
            }
        }

        public static string Save()
        {
            var doc = AcadContext.ActiveDocument;
            if (!Path.IsPathRooted(doc.Name))
                throw new InvalidOperationException("当前图形尚未保存过，请先在 AutoCAD 中执行『另存为』。");

            // QSAVE 走命令队列，稳定可靠；不阻塞当前 Idle 回调。
            doc.SendStringToExecute("_.QSAVE ", true, false, false);
            return "已发送保存命令：" + doc.Name;
        }

        public static string SaveAs(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path 不能为空。");
            if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                path += ".dwg";
            if (!Path.IsPathRooted(path))
                throw new ArgumentException("path 必须是绝对路径，例如 D:\\work\\plan.dwg");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var doc = AcadContext.ActiveDocument;
            using (doc.LockDocument())
            {
                // DwgVersion.Current 在部分版本（如 2014）会报 eInvalidDwgVersion，按新→旧回退到一个能写的格式
                DwgVersion[] candidates =
                {
                    DwgVersion.AC1032, DwgVersion.AC1027, DwgVersion.AC1024, DwgVersion.AC1021,
                };
                System.Exception? last = null;
                foreach (var v in candidates)
                {
                    try
                    {
                        doc.Database.SaveAs(path, v);
                        return $"已保存到：{path}（格式 {v}）";
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        last = ex; // 该版本不被当前 AutoCAD 支持，试下一个
                    }
                }
                throw last ?? new InvalidOperationException("无法确定可用的 DWG 保存版本。");
            }
        }

        public static string Undo(int steps)
        {
            if (steps < 1) steps = 1;
            if (steps > 200) steps = 200;
            var doc = AcadContext.ActiveDocument;
            // _.U 单步从插件调不生效；_.UNDO <N> 才行（粒度按 AutoCAD 的 undo 记录，可能与工具调用数不完全 1:1）
            doc.SendStringToExecute($"_.UNDO {steps}\n", true, false, false);
            return $"已发送 UNDO {steps}（异步执行；粒度由 AutoCAD 决定，可能撤销更多或更少，配合 query_entities / capture_view 确认）。";
        }

        public static string RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("command 不能为空。");

            var doc = AcadContext.ActiveDocument;
            string payload = command.EndsWith("\n") || command.EndsWith(" ")
                ? command
                : command + " ";

            doc.SendStringToExecute(payload, true, false, true);
            return "已发送命令（异步执行）：" + command;
        }
    }
}
