using System;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>构造 MCP tools/call 的 content 块数组。</summary>
    internal static class ToolResult
    {
        public static JArray Text(string text) => new JArray
        {
            new JObject { ["type"] = "text", ["text"] = text ?? string.Empty },
        };

        public static JArray Image(byte[] bytes, string mimeType) => new JArray
        {
            new JObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(bytes),
                ["mimeType"] = mimeType,
            },
        };

        /// <summary>一段说明文字 + 一张图。</summary>
        public static JArray TextAndImage(string text, byte[] bytes, string mimeType) => new JArray
        {
            new JObject { ["type"] = "text", ["text"] = text ?? string.Empty },
            new JObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(bytes),
                ["mimeType"] = mimeType,
            },
        };
    }
}
