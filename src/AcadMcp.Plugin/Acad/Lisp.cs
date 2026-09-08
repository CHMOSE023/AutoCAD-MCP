using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using AcadMcp.Mcp;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 同步执行 AutoLISP（acedEvaluateLisp）。
    ///
    /// - <see cref="Run"/> / <see cref="RunOrThrow"/>：内部可信调用（如工具用 (vl-cmdf …) 执行原生命令），无门禁。
    /// - <see cref="Eval"/>：eval_lisp 工具入口 —— 任意代码执行，默认关闭，需 MCPLISP / 环境变量开启，完整代码入日志。
    /// </summary>
    internal static class Lisp
    {
        // accore.dll 导出（x64，C++ mangled）：int acedEvaluateLisp(const wchar_t*, resbuf*&)
        // 名字在 AutoCAD 2014 / 2018 / 2020 一致（dumpbin 验证）。成功返回码因版本而异（2014/2020 返回 1），不据此判定。
        [SuppressUnmanagedCodeSecurity]
        [DllImport("accore.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "?acedEvaluateLisp@@YAHPEB_WAEAPEAUresbuf@@@Z")]
        private static extern int acedEvaluateLisp(string lispLine, out IntPtr result);

        [SuppressUnmanagedCodeSecurity]
        [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acutRelRb")]
        private static extern int acutRelRb(IntPtr rb);

        // ---------- 开关（仅 eval_lisp 用）----------

        private static string FlagFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AcadMcp", "allow_lisp");

        public static bool Enabled
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("ACADMCP_ALLOW_LISP");
                if (env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
                    return true;
                try { return File.Exists(FlagFile); } catch { return false; }
            }
        }

        /// <summary>命令 MCPLISP 切换开关（立即生效，无需重载）。返回新状态。</summary>
        public static bool Toggle()
        {
            var f = FlagFile;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            if (File.Exists(f)) { File.Delete(f); return false; }
            File.WriteAllText(f, "enabled " + DateTime.Now.ToString("s"));
            return true;
        }

        // ---------- 核心执行 ----------

        public readonly struct Result
        {
            public readonly bool Ok;
            public readonly string Value;   // Ok=true 时是 vl-princ-to-string 的结果；Ok=false 时是错误信息
            public Result(bool ok, string value) { Ok = ok; Value = value; }
        }

        /// <summary>
        /// 同步执行一段可信的 LISP，取回其值。基础设施失败（P/Invoke、结果文件缺失）抛异常；
        /// LISP 自身报错返回 Ok=false + 错误信息。
        /// </summary>
        public static Result Run(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code 不能为空。");

            var doc = AcadContext.ActiveDocument;
            var outFile = Path.Combine(Path.GetTempPath(), "acadmcp-lisp-" + Guid.NewGuid().ToString("N") + ".out");
            var lispOut = outFile.Replace("\\", "/");

            // 包一层：vl-catch-all 捕错、把结果 vl-princ-to-string 后写文件
            var wrapped =
                "(progn (vl-load-com)" +
                "(setq #mcpr (vl-catch-all-apply (quote (lambda () " + code + "))))" +
                "(setq #mcpf (open \"" + lispOut + "\" \"w\"))" +
                "(if #mcpf (progn" +
                "  (if (vl-catch-all-error-p #mcpr)" +
                "    (progn (write-line \"ERR\" #mcpf) (write-line (vl-catch-all-error-message #mcpr) #mcpf))" +
                "    (progn (write-line \"OK\" #mcpf) (write-line (vl-princ-to-string #mcpr) #mcpf)))" +
                "  (close #mcpf)))" +
                "(princ))";

            try
            {
                int rc;
                using (doc.LockDocument())
                {
                    rc = acedEvaluateLisp(wrapped, out IntPtr res);
                    if (res != IntPtr.Zero) TryRelRb(res);
                }

                if (!File.Exists(outFile))
                    throw new InvalidOperationException(
                        $"LISP 未产出结果文件（acedEvaluateLisp rc={rc}）。可能是语法错误，或从当前上下文无法执行。");

                // AutoCAD 的 write-line 按系统 ANSI 写；先试 UTF-8，失败退回系统默认代码页
                string text;
                var bytes = File.ReadAllBytes(outFile);
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch { text = Encoding.Default.GetString(bytes); }

                var parts = text.Replace("\r\n", "\n").Split(new[] { '\n' }, 2);
                var status = parts.Length > 0 ? parts[0].Trim() : "";
                var body = parts.Length > 1 ? parts[1].TrimEnd('\n') : "";

                return status == "ERR" ? new Result(false, body) : new Result(true, body);
            }
            finally
            {
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        /// <summary>执行可信 LISP，LISP 报错则抛异常（工具内部用）。</summary>
        public static string RunOrThrow(string code)
        {
            var r = Run(code);
            if (!r.Ok)
                throw new InvalidOperationException("LISP: " + r.Value);
            return r.Value;
        }

        /// <summary>
        /// 把 LISP 送进 AutoCAD 命令队列执行（文档上下文，(command)/(vl-cmdf) 可用），HTTP 线程轮询结果文件。
        /// 用于 trim/extend/fillet/chamfer 这类交互命令。比 <see cref="Run"/> 慢（要等命令队列），但能跑命令。
        /// </summary>
        /// <summary>启动时预热命令队列（首次 SendStringToExecute 处理慢）。主线程调用。</summary>
        public static void WarmUpCommandQueue()
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("(princ)\n", false, false, false);
            }
            catch { }
        }

        public static Result RunViaCommandQueue(string code, int timeoutMs = 30000)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code 不能为空。");

            // 本方法跑在 HTTP 线程：MdiActiveDocument 在后台线程为 null，必须去主线程取引用
            var doc = MainThread.Invoke(() => AcadContext.ActiveDocument);
            var outFile = Path.Combine(Path.GetTempPath(), "acadmcp-cmd-" + Guid.NewGuid().ToString("N") + ".out");
            var lispOut = outFile.Replace("\\", "/");

            // 关键：直接把表达式送进命令行执行，不写 .lsp 也不 (load)
            //   —— (load 临时文件) 会触发 SECURELOAD 弹模态框，阻塞整个 AutoCAD。
            //   结果仍靠 (open ... "w") 写文件回传（普通文件 I/O，不受安全检查）。
            var expr =
                "(progn (vl-load-com)" +
                "(setq #cr (vl-catch-all-apply (quote (lambda () " + code + "))))" +
                "(setq #cf (open \"" + lispOut + "\" \"w\"))" +
                "(if #cf (progn" +
                "  (if (vl-catch-all-error-p #cr)" +
                "    (progn (write-line \"ERR\" #cf) (write-line (vl-catch-all-error-message #cr) #cf))" +
                "    (progn (write-line \"OK\" #cf) (write-line (vl-princ-to-string #cr) #cf)))" +
                "  (close #cf)))" +
                "(princ))\n";

            SweepStale();
            Log.Info("cmdqueue", "LISP: " + code.Replace("\r", " ").Replace("\n", " "));

            bool got = false;
            try
            {
                // activate=false：从后台线程 activate=true 会抛 eInvalidInput
                doc.SendStringToExecute(expr, false, false, false);

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline && !File.Exists(outFile))
                    System.Threading.Thread.Sleep(30);

                if (!File.Exists(outFile))
                {
                    Log.Error("cmdqueue", "超时未产出结果文件。expr=" + expr.Trim());
                    throw new InvalidOperationException(
                        $"命令 {timeoutMs}ms 内未产出结果（可能 handle 无效、命令进入子提示卡住，或几何不满足条件）。");
                }

                System.Threading.Thread.Sleep(20); // 等文件写完整
                got = true;

                string text;
                var bytes = File.ReadAllBytes(outFile);
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch { text = Encoding.Default.GetString(bytes); }

                var parts = text.Replace("\r\n", "\n").Split(new[] { '\n' }, 2);
                var status = parts.Length > 0 ? parts[0].Trim() : "";
                var body = parts.Length > 1 ? parts[1].TrimEnd('\n') : "";
                return status == "ERR" ? new Result(false, body) : new Result(true, body);
            }
            finally
            {
                if (got)
                    try { File.Delete(outFile); } catch { }
            }
        }

        // ---------- eval_lisp 工具入口 ----------

        public static string Eval(string code)
        {
            if (!Enabled)
                throw new InvalidOperationException(
                    "eval_lisp 未启用（任意代码执行，默认关闭）。在 AutoCAD 命令行执行 MCPLISP 开启，" +
                    "或设置环境变量 ACADMCP_ALLOW_LISP=1 后重启 AutoCAD。");

            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code 不能为空。");

            Log.Info("eval_lisp", "CODE: " + code.Replace("\r", " ").Replace("\n", " "));

            var r = Run(code);
            if (!r.Ok)
                return "LISP 错误：" + r.Value;
            return string.IsNullOrEmpty(r.Value) ? "(nil)" : r.Value;
        }

        private static void TryRelRb(IntPtr rb)
        {
            try { acutRelRb(rb); } catch { /* 泄漏一个 resbuf，可忽略 */ }
        }

        /// <summary>清理超过 5 分钟的遗留临时文件（超时的命令队列 .lsp/.out）。</summary>
        private static void SweepStale()
        {
            try
            {
                var cutoff = DateTime.Now.AddMinutes(-5);
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "acadmcp-cmd-*"))
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), "acadmcp-lisp-*"))
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
