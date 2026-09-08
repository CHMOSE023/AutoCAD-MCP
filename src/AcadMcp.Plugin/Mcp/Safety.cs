using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>
    /// P2.5 安全网：只读模式、HTTP token 鉴权、会话前备份、命名 undo 标记。
    /// 状态是进程内静态的；命令 MCPREADONLY / MCPTOKEN 控制。
    /// </summary>
    internal static class Safety
    {
        // ---- 工具分类 ----
        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_status", "list_layers", "query_entities", "capture_view", "get_entity",
            "list_blocks", "get_log", "select", "measure_distance", "measure_area",
        };

        public static bool IsWrite(string tool) => !ReadOnlyTools.Contains(tool);

        // ---- 只读模式 ----
        public static bool ReadOnly { get; private set; }

        public static bool ToggleReadOnly()
        {
            ReadOnly = !ReadOnly;
            Log.Info("safety", "只读模式 " + (ReadOnly ? "开启" : "关闭"));
            return ReadOnly;
        }

        // ---- HTTP token ----
        private static string Dir => Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AcadMcp");

        private static string TokenFile => Path.Combine(Dir, "token");

        /// <summary>当前 token（环境变量 ACADMCP_TOKEN 优先，其次 token 文件）。null = 不鉴权。</summary>
        public static string? Token
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ACADMCP_TOKEN");
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
                try { return File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null; }
                catch { return null; }
            }
        }

        public static bool AuthRequired => !string.IsNullOrEmpty(Token);

        /// <summary>生成一个新 token 并写文件，返回它。</summary>
        public static string GenerateToken()
        {
            var t = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(TokenFile, t);
            Log.Info("safety", "已设置 HTTP token");
            return t;
        }

        public static void ClearToken()
        {
            try { if (File.Exists(TokenFile)) File.Delete(TokenFile); } catch { }
            Log.Info("safety", "已清除 HTTP token");
        }

        // ---- 会话前备份 ----
        private static bool _backedUp;
        public static bool SessionBackedUp => _backedUp;

        /// <summary>由插件注入：返回当前 dwg 的磁盘路径（内部自行处理主线程编组）。</summary>
        public static Func<string?>? DocPathProvider;

        public static void ResetSession()
        {
            _backedUp = false;
            Marks.Clear();
            _markSeq = 0;
        }

        /// <summary>首个写操作前调用：把 dwg 磁盘上"最后保存"的版本复制一份。幂等。</summary>
        public static void EnsureSessionBackupIfNeeded()
        {
            if (_backedUp) return;
            _backedUp = true;

            string? docPath;
            try { docPath = DocPathProvider?.Invoke(); }
            catch { return; }

            if (string.IsNullOrEmpty(docPath) || !Path.IsPathRooted(docPath) || !File.Exists(docPath))
                return;

            var bak = docPath + $".mcpbak-{DateTime.Now:yyyyMMdd-HHmmss}.dwg";
            try
            {
                File.Copy(docPath!, bak, overwrite: false);
                Log.Info("safety", "会话备份: " + bak);
            }
            catch (Exception ex)
            {
                Log.Error("safety", "会话备份失败: " + ex.Message);
            }
        }

        // ---- 命名标记（对应 AutoCAD 的 UNDO _Mark 栈，rollback = UNDO _Back 弹一层）----
        private static readonly List<(int Id, string Label, DateTime At)> Marks =
            new List<(int, string, DateTime)>();
        private static int _markSeq;

        public static int AddMark(string? label)
        {
            int id = ++_markSeq;
            Marks.Add((id, string.IsNullOrWhiteSpace(label) ? "mark" + id : label!.Trim(), DateTime.Now));
            return id;
        }

        public static bool HasMark => Marks.Count > 0;

        public static string PopMark()
        {
            if (Marks.Count == 0) return "(无)";
            var m = Marks[Marks.Count - 1];
            Marks.RemoveAt(Marks.Count - 1);
            return $"{m.Label}（#{m.Id}, {m.At:HH:mm:ss}）";
        }

        public static string DescribeMarks()
        {
            if (Marks.Count == 0) return "（无）";
            return string.Join(" / ", Marks.Select(m => $"{m.Label}#{m.Id}"));
        }
    }
}
