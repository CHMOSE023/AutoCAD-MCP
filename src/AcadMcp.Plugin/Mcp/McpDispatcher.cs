using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>处理 MCP JSON-RPC 消息：initialize / tools.list / tools.call / ping。</summary>
    internal sealed class McpDispatcher
    {
        public const string ServerName = "autocad-mcp";
        public const string ServerVersion = "0.1.0";

        private static readonly string[] SupportedProtocols = { "2025-06-18", "2025-03-26" };

        private readonly Dictionary<string, Tool> _tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        private readonly JArray _toolListCache;

        public McpDispatcher(IEnumerable<Tool> tools)
        {
            _toolListCache = new JArray();
            foreach (var t in tools)
            {
                _tools[t.Name] = t;
                _toolListCache.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema,
                });
            }
        }

        /// <summary>
        /// 处理单条 JSON-RPC 消息。返回 null 表示这是通知或响应（HTTP 层应回 202）。
        /// </summary>
        public JObject? Handle(JObject msg)
        {
            var id = msg["id"];
            var method = (string?)msg["method"];

            // 没有 method 字段 = 这是一条响应对象，服务端忽略
            if (method == null)
                return null;

            bool isNotification = id == null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return JsonRpc.Result(id, Initialize(msg["params"] as JObject));

                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return null;

                    case "ping":
                        return JsonRpc.Result(id, new JObject());

                    case "tools/list":
                        return JsonRpc.Result(id, new JObject { ["tools"] = _toolListCache });

                    case "tools/call":
                        return JsonRpc.Result(id, ToolsCall(msg["params"] as JObject));

                    default:
                        return isNotification ? null
                            : JsonRpc.Error(id, JsonRpc.MethodNotFound, $"未知方法：{method}");
                }
            }
            catch (McpParamException ex)
            {
                return isNotification ? null : JsonRpc.Error(id, JsonRpc.InvalidParams, ex.Message);
            }
            catch (Exception ex)
            {
                return isNotification ? null : JsonRpc.Error(id, JsonRpc.InternalError, ex.Message);
            }
        }

        private static JObject Initialize(JObject? p)
        {
            var requested = (string?)p?["protocolVersion"];
            string version = Array.IndexOf(SupportedProtocols, requested) >= 0
                ? requested!
                : SupportedProtocols[0];

            return new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject { ["tools"] = new JObject() },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion,
                },
                ["instructions"] =
                    "通过工具操作当前打开的 AutoCAD 图形。坐标与尺寸使用当前图形单位。" +
                    "写操作会立即修改图纸；建议先用 get_status / list_layers / query_entities 了解现状。",
            };
        }

        private JObject ToolsCall(JObject? p)
        {
            var name = (string?)p?["name"]
                ?? throw new McpParamException("缺少 params.name");

            if (!_tools.TryGetValue(name, out var tool))
                throw new McpParamException($"未知工具：{name}");

            var args = p!["arguments"] as JObject ?? new JObject();
            var argsJson = args.ToString(Newtonsoft.Json.Formatting.None);

            bool isWrite = Safety.IsWrite(name);

            // 只读模式：拒绝写工具
            if (isWrite && Safety.ReadOnly)
            {
                Log.Call(name, false, 0, argsJson, tool.Dangerous, "只读模式已开启");
                return new JObject
                {
                    ["content"] = ToolResult.Text("只读模式已开启，写操作被拒绝。在 AutoCAD 命令行执行 MCPREADONLY 关闭。"),
                    ["isError"] = true,
                };
            }

            // 首个写操作前：自动备份当前 dwg
            if (isWrite)
                Safety.EnsureSessionBackupIfNeeded();

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var content = tool.Handler(args);
                sw.Stop();
                Log.Call(name, true, sw.ElapsedMilliseconds, argsJson, tool.Dangerous, null);
                return new JObject { ["content"] = content };
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log.Call(name, false, sw.ElapsedMilliseconds, argsJson, tool.Dangerous, ex.Message);

                // MCP 约定：工具执行错误通过 isError + content 返回，而非 JSON-RPC error
                var text = "错误：" + ex.Message;
                var hint = Hint(ex.Message);
                if (hint != null) text += "\n提示：" + hint;

                return new JObject
                {
                    ["content"] = ToolResult.Text(text),
                    ["isError"] = true,
                };
            }
        }

        private static string? Hint(string msg)
        {
            var m = msg ?? "";
            if (m.Contains("主线程") && m.Contains("未处理"))
                return "AutoCAD 可能有模态对话框或正忙，切到 AutoCAD 按 ESC 关闭对话框后重试。";
            if (m.Contains("没有打开的图形"))
                return "先在 AutoCAD 中新建或打开一个 DWG。";
            if (m.Contains("eLockViolation") || m.Contains("锁定"))
                return "文档被锁，稍等片刻重试；确认没有正在进行的命令。";
            if (m.Contains("未找到") || m.Contains("handent") || m.Contains("handle"))
                return "handle 可能不存在或已被删除，用 query_entities / select 重新获取。";
            if (m.Contains("eInvalidInput") || m.Contains("eNotApplicable"))
                return "参数或目标实体类型不适用于该操作。";
            if (m.Contains("eKeyNotFound") || m.Contains("不存在"))
                return "先用 list_layers / list_blocks 确认名称。";
            return null;
        }
    }
}
