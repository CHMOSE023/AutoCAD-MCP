using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadMcp.Acad;
using AcadMcp.Mcp;
using AcadMcp.Tools;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: ExtensionApplication(typeof(AcadMcp.PluginEntry))]
[assembly: CommandClass(typeof(AcadMcp.PluginEntry))]

namespace AcadMcp
{
    /// <summary>
    /// 插件入口。NETLOAD 后自动启动内嵌的 MCP HTTP 服务；
    /// 也可用命令 MCPSTART / MCPSTOP / MCPSTATUS 手动控制。
    /// </summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private const string Host = "127.0.0.1";

        /// <summary>首选端口。被占用时依次向后探测，让多开的 AutoCAD 实例自动错开。</summary>
        private const int BasePort = 7130;

        /// <summary>最多探测几个端口（BasePort .. BasePort + PortProbeCount - 1）。</summary>
        private const int PortProbeCount = 10;

        private const string Path = "/mcp";

        private static HttpServer? _server;

        public void Initialize()
        {
            MainThread.EnsureInstalled();
            try
            {
                StartServer();
            }
            catch (Exception ex)
            {
                Log.Error("server", "自动启动失败：" + ex.Message);
                Print("自动启动失败：" + ex.Message);
                Print("可稍后手动执行命令 MCPSTART 重试。");
            }
        }

        public void Terminate()
        {
            if (_server != null) Log.Info("server", "AutoCAD 关闭，停止服务");
            _server?.Stop();
            _server = null;
        }

        [CommandMethod("MCPSTART")]
        public void McpStart()
        {
            if (_server != null && _server.IsRunning)
            {
                Print("已在运行：" + _server.Endpoint);
                return;
            }
            try
            {
                StartServer();
            }
            catch (Exception ex)
            {
                Print(ex.Message);
            }
        }

        [CommandMethod("MCPSTOP")]
        public void McpStop()
        {
            if (_server != null) Log.Info("server", "手动停止（MCPSTOP）");
            _server?.Stop();
            _server = null;
            Print("已停止。");
        }

        [CommandMethod("MCPSTATUS")]
        public void McpStatus()
        {
            if (_server != null && _server.IsRunning)
                Print("运行中：" + _server.Endpoint);
            else
                Print("未运行（执行 MCPSTART 启动）。");
            Print("eval_lisp：" + (Acad.Lisp.Enabled ? "已启用" : "关闭（MCPLISP 开启）"));
        }

        [CommandMethod("MCPLISP")]
        public void McpLisp()
        {
            bool on = Acad.Lisp.Toggle();
            Log.Info("server", "eval_lisp " + (on ? "启用" : "关闭") + "（MCPLISP）");
            if (on)
            {
                Print("⚠ eval_lisp 已启用。它可执行任意 AutoLISP —— 包括删文件、起进程、调 COM。");
                Print("  仅在信任对面模型/会话时开启。再次执行 MCPLISP 关闭。");
            }
            else
            {
                Print("eval_lisp 已关闭。");
            }
        }

        /// <summary>
        /// 内部命令：执行 plot_pdf 排队的打印任务。**不要手工调用** —— 它只在有待处理请求时做事。
        /// 存在的理由：PlotEngine 必须跑在文档上下文，而工具调用来自 HTTP 线程 / Application.Idle。
        /// 不加 CommandFlags.Session：那会让命令在应用上下文执行，正是要避开的东西。
        /// </summary>
        [CommandMethod("MCPPLOT")]
        public void McpPlot()
        {
            Acad.Plot.RunPending();
        }

        [CommandMethod("MCPREADONLY")]
        public void McpReadOnly()
        {
            bool ro = Mcp.Safety.ToggleReadOnly();
            Print(ro ? "只读模式已开启 —— 所有写工具被拒绝。再次执行 MCPREADONLY 关闭。" : "只读模式已关闭。");
        }

        [CommandMethod("MCPTOKEN")]
        public void McpToken()
        {
            var t = Mcp.Safety.Token;
            if (t != null)
            {
                Print("当前 HTTP token：" + t);
                Print("接入：claude mcp add --transport http autocad " + (_server?.Endpoint ?? $"http://{Host}:{BasePort}{Path}") +
                      " --header \"Authorization: Bearer " + t + "\"");
                Print("清除：MCPTOKENOFF");
            }
            else
            {
                var nt = Mcp.Safety.GenerateToken();
                Print("已生成 HTTP token：" + nt);
                Print("现在所有请求必须带 Authorization: Bearer <token>。");
                Print("接入：claude mcp add --transport http autocad " + (_server?.Endpoint ?? $"http://{Host}:{BasePort}{Path}") +
                      " --header \"Authorization: Bearer " + nt + "\"");
            }
        }

        [CommandMethod("MCPTOKENOFF")]
        public void McpTokenOff()
        {
            Mcp.Safety.ClearToken();
            Print("HTTP token 已清除，恢复无鉴权（仅本机 loopback）。");
        }

        private static void StartServer()
        {
            Mcp.Safety.ResetSession();
            Mcp.Safety.DocPathProvider = () =>
                Acad.MainThread.Invoke(() => AcApp.DocumentManager.MdiActiveDocument?.Name);

            var dispatcher = new McpDispatcher(ToolCatalog.Build());
            var server = StartListener(dispatcher);
            _server = server;

            Acad.Lisp.WarmUpCommandQueue();

            Log.Info("server", "启动 " + server.Endpoint);
            Print($"AutoCAD MCP 已启动：{server.Endpoint}");
            Print($"接入 Claude Code：claude mcp add --transport http autocad {server.Endpoint}");
            Print($"日志：{Log.CurrentFile}");
            if (Mcp.Safety.AuthRequired) Print("已启用 token 鉴权（MCPTOKEN 查看）。");
        }

        /// <summary>
        /// 从 BasePort 起逐个探测可用端口并启动监听。
        /// HttpListener 的前缀注册在 http.sys 里是全机独占的，多开 AutoCAD 时第二个实例
        /// 必然撞车（错误 183）；这里换个端口重试即可。权限类错误（5）不重试 —— 换端口治不了。
        /// </summary>
        private static HttpServer StartListener(McpDispatcher dispatcher)
        {
            for (int port = BasePort; port < BasePort + PortProbeCount; port++)
            {
                var server = new HttpServer(Host, port, Path, dispatcher);
                try
                {
                    server.Start();
                    if (port != BasePort)
                        Print($"端口 {BasePort} 已被占用（多半是另一个 AutoCAD 实例），改用 {port}。");
                    return server;
                }
                catch (PortInUseException)
                {
                    server.Dispose();
                    // 继续试下一个端口
                }
                catch
                {
                    server.Dispose();
                    throw;
                }
            }

            throw new InvalidOperationException(
                $"端口 {BasePort}-{BasePort + PortProbeCount - 1} 全部被占用，无法启动。\n" +
                $"关掉多余的 AutoCAD 实例后执行 MCPSTART 重试。");
        }

        /// <summary>写命令行提示。只应在 AutoCAD 主线程调用。</summary>
        internal static void Print(string message)
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[AutoCAD MCP] " + message);
            }
            catch
            {
                // 忽略：没有活动文档或非主线程时不打印
            }
        }
    }
}
