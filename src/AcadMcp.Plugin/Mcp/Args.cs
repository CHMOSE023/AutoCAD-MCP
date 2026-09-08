using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>从 tools/call 的 arguments 中安全读取参数。缺失/类型错误 → McpParamException。</summary>
    internal static class Args
    {
        public static double Num(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null)
                throw new McpParamException($"缺少必填参数：{key}");
            try { return t.Value<double>(); }
            catch { throw new McpParamException($"参数 {key} 必须是数字"); }
        }

        public static double NumOr(JObject a, string key, double def)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<double>(); }
            catch { throw new McpParamException($"参数 {key} 必须是数字"); }
        }

        public static double? NumOrNull(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<double>(); }
            catch { throw new McpParamException($"参数 {key} 必须是数字"); }
        }

        public static int IntOr(JObject a, string key, int def)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<int>(); }
            catch { throw new McpParamException($"参数 {key} 必须是整数"); }
        }

        public static int? IntOrNull(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<int>(); }
            catch { throw new McpParamException($"参数 {key} 必须是整数"); }
        }

        public static bool BoolOr(JObject a, string key, bool def)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<bool>(); }
            catch { throw new McpParamException($"参数 {key} 必须是布尔值"); }
        }

        public static string Str(JObject a, string key)
        {
            var s = (string?)a[key];
            if (string.IsNullOrEmpty(s))
                throw new McpParamException($"缺少必填参数：{key}");
            return s!;
        }

        public static string? StrOrNull(JObject a, string key)
        {
            var s = (string?)a[key];
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
