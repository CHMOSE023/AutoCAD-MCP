using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    internal static class JsonRpc
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        public static JObject Result(JToken? id, JToken result) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = result,
        };

        public static JObject Error(JToken? id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };
    }

    /// <summary>参数错误 → JSON-RPC InvalidParams。Message 为面向用户的中文说明。</summary>
    internal sealed class McpParamException : System.Exception
    {
        public McpParamException(string message) : base(message) { }
    }
}
