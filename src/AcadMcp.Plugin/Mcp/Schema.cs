using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>手写 JSON-Schema 的小助手（tools/list 的 inputSchema）。</summary>
    internal static class Schema
    {
        public readonly struct Prop
        {
            public readonly string Name;
            public readonly string Type;      // "string" | "number" | "integer" | "boolean"
            public readonly string Desc;
            public readonly bool Required;

            public Prop(string name, string type, string desc, bool required)
            {
                Name = name; Type = type; Desc = desc; Required = required;
            }
        }

        public static Prop P(string name, string type, string desc, bool required = false)
            => new Prop(name, type, desc, required);

        public static JObject Object(params Prop[] props)
        {
            var properties = new JObject();
            var required = new JArray();

            foreach (var p in props)
            {
                properties[p.Name] = new JObject { ["type"] = p.Type, ["description"] = p.Desc };
                if (p.Required) required.Add(p.Name);
            }

            var o = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            if (required.Count > 0) o["required"] = required;
            return o;
        }

        /// <summary>往一个已构造的 object schema 里塞一个自定义属性（如数组），返回原对象。</summary>
        public static JObject With(JObject objectSchema, string propName, JObject propSchema)
        {
            ((JObject)objectSchema["properties"]!)[propName] = propSchema;
            return objectSchema;
        }

        /// <summary>字符串数组属性 schema。</summary>
        public static JObject StrArray(string desc) => new JObject
        {
            ["type"] = "array",
            ["items"] = new JObject { ["type"] = "string" },
            ["description"] = desc,
        };

        /// <summary>布尔属性 schema。</summary>
        public static JObject Bool(string desc) => new JObject
        {
            ["type"] = "boolean",
            ["description"] = desc,
        };

        /// <summary>数字二元组数组 schema（点列表 [[x,y],...]）。</summary>
        public static JObject PointArray(string desc) => new JObject
        {
            ["type"] = "array",
            ["description"] = desc,
            ["items"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 2,
            },
        };

        /// <summary>给 object schema 追加 handle / handles / useSelection 三件套，用于修改类工具。</summary>
        public static JObject Selectable(JObject objectSchema)
        {
            With(objectSchema, "handle", new JObject { ["type"] = "string", ["description"] = "单个实体 handle" });
            With(objectSchema, "handles", StrArray("多个实体 handle"));
            With(objectSchema, "useSelection", Bool("用当前选择集（select 的结果）代替 handle/handles"));
            return objectSchema;
        }
    }
}
