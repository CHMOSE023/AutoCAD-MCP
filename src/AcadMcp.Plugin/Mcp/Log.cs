using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AcadMcp.Mcp
{
    /// <summary>
    /// 线程安全的滚动文件日志：%LOCALAPPDATA%\AcadMcp\logs\mcp-yyyyMMdd.log。
    /// 记录服务启停、每次 tools/call（工具名 / 结果 / 耗时 / 参数摘要 / 危险标记）、HTTP 错误。
    /// 日志写入失败绝不影响功能。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string Dir = ResolveDir();

        /// <summary>最近一次写日志失败的异常（供 get_status 诊断）。</summary>
        public static string? LastWriteError;
        public static long WriteCount;

        private static string ResolveDir()
        {
            foreach (var baseDir in new[]
            {
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                SafeGetFolder(Environment.SpecialFolder.LocalApplicationData),
                SafeGetFolder(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("TEMP"),
                Path.GetTempPath(),
            })
            {
                if (string.IsNullOrWhiteSpace(baseDir) || !Path.IsPathRooted(baseDir)) continue;
                try
                {
                    var d = Path.Combine(baseDir, "AcadMcp", "logs");
                    Directory.CreateDirectory(d);
                    return d;
                }
                catch { /* 试下一个 */ }
            }
            return Path.GetTempPath();
        }

        private static string? SafeGetFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return null; }
        }

        public static string CurrentFile =>
            Path.Combine(Dir, "mcp-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

        public static void Info(string category, string message) => Write(category, message);

        public static void Error(string category, string message) => Write(category, "ERR " + message);

        public static void Call(string tool, bool ok, long elapsedMs, string? argsJson, bool dangerous, string? errNote)
        {
            var sb = new StringBuilder();
            sb.Append(tool.PadRight(16)).Append(' ')
              .Append((ok ? "ok" : "ERR").PadRight(4)).Append(' ')
              .Append(elapsedMs.ToString().PadLeft(5)).Append("ms  ")
              .Append(Truncate(argsJson, 300));
            if (!ok && !string.IsNullOrEmpty(errNote)) sb.Append("  -> ").Append(Truncate(errNote, 200));
            if (dangerous) sb.Append("  DANGER");
            Write("tools/call", sb.ToString());
        }

        /// <summary>返回今天日志的最后 n 行。</summary>
        public static string Tail(int n)
        {
            if (n <= 0) n = 50;
            if (n > 2000) n = 2000;
            try
            {
                var file = CurrentFile;
                if (!File.Exists(file)) return "(今天还没有日志)";
                lock (Gate)
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, new UTF8Encoding(false));
                    var all = new List<string>();
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        all.Add(line);
                        if (all.Count > n + 200) all.RemoveRange(0, all.Count - n);
                    }
                    return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - n)));
                }
            }
            catch (Exception ex)
            {
                return "读取日志失败：" + ex.Message;
            }
        }

        private static void Write(string category, string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                       + "  " + category.PadRight(11) + " " + message + Environment.NewLine;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(CurrentFile, line, new UTF8Encoding(false));
                    WriteCount++;
                }
            }
            catch (Exception ex)
            {
                LastWriteError = ex.GetType().Name + ": " + ex.Message + "  (path=" + CurrentFile + ")";
                try { System.Diagnostics.Trace.WriteLine("[AcadMcp] " + line); } catch { }
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s!.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
