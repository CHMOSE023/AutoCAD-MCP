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
                        // 先把文件写出去：这一步是同步的，成败立刻知道，文件内容有保障
                        doc.Database.SaveAs(path, v);

                        // 但 Database.SaveAs 的语义只是"另存一份副本" —— 当前文档仍指向原来的，
                        // 修改标记也不清，标签页上继续挂着 *。要让文档真正切过去只能走 SAVEAS 命令。
                        // 难点是 FILEDIA=1（默认）时 SAVEAS 弹文件对话框，会把主线程冻住；
                        // 解法是把 FILEDIA 的关与开**和 SAVEAS 一起排进命令队列**——
                        // 队列顺序执行，恢复动作也在队列里，不依赖插件这边的时机控制。
                        string esc = path.Replace("\\", "/");
                        string switchNote;
                        try
                        {
                            doc.SendStringToExecute(
                                "_.FILEDIA 0\n_.SAVEAS\n\n" + esc + "\nY\n_.FILEDIA 1\n",
                                false, false, false);
                            switchNote = "当前文档正在切换到该文件（命令队列异步执行，稍后 get_status 可确认）。";
                        }
                        catch (System.Exception ex)
                        {
                            Mcp.Log.Error("save_as", "切换当前文档失败：" + ex.Message);
                            switchNote = "注意：文件已写出，但当前文档没能切过去（仍带 *），" +
                                         "要接着编辑请用 open_document 打开它。";
                        }

                        return $"已保存到：{path}（格式 {v}）。{switchNote}";
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
