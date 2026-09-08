using System;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>
    /// 一个 MCP 工具：名称、描述、输入 JSON-Schema、处理函数。
    /// 处理函数返回 MCP content 块数组（<see cref="ToolResult"/> 提供构造助手）。
    /// </summary>
    internal sealed class Tool
    {
        public string Name { get; }
        public string Description { get; }
        public JObject InputSchema { get; }
        public Func<JObject, JArray> Handler { get; }

        /// <summary>危险工具（如 run_command）：日志里打 DANGER 标记，后续可接审批。</summary>
        public bool Dangerous { get; }

        public Tool(string name, string description, JObject inputSchema, Func<JObject, JArray> handler, bool dangerous = false)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema;
            Handler = handler;
            Dangerous = dangerous;
        }

        /// <summary>便捷重载：处理函数只返回文本，自动包成一个 text 块。</summary>
        public Tool(string name, string description, JObject inputSchema, Func<JObject, string> textHandler, bool dangerous = false)
            : this(name, description, inputSchema, a => ToolResult.Text(textHandler(a)), dangerous)
        {
        }
    }
}
