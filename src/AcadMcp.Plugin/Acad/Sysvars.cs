using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcRtException = Autodesk.AutoCAD.Runtime.Exception;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 读写 AutoCAD 系统变量（GETVAR / SETVAR 的 .NET 对应）。
    /// 类型从当前值推断：短整型 / 整型 / 双精度 / 字符串 / 点。
    /// </summary>
    internal static class Sysvars
    {
        /// <summary>不传 names 时返回的常用变量清单（覆盖绘图状态、单位、标注、显示）。</summary>
        private static readonly string[] Common =
        {
            "DWGNAME", "DWGPREFIX", "CTAB", "TILEMODE",
            "CLAYER", "CECOLOR", "CELTYPE", "CELTSCALE", "CELWEIGHT",
            "INSUNITS", "MEASUREMENT", "LUNITS", "LUPREC", "AUNITS", "AUPREC", "ANGBASE", "ANGDIR",
            "LTSCALE", "DIMSCALE", "DIMSTYLE", "CANNOSCALE",
            "TEXTSIZE", "TEXTSTYLE",
            "OSMODE", "ORTHOMODE", "SNAPMODE", "PDMODE", "PDSIZE", "FILLETRAD",
            "VIEWCTR", "VIEWSIZE", "EXTMIN", "EXTMAX",
            "CMDECHO", "FILEDIA", "BACKGROUNDPLOT", "SECURELOAD",
        };

        /// <summary>读取系统变量。names 为空则读常用清单。读不到的变量记入 errors，不中断。</summary>
        public static string Get(IReadOnlyList<string> names)
        {
            var list = (names == null || names.Count == 0) ? Common : names;

            var values = new JObject();
            var errors = new JObject();

            // 读变量本身不改图形，但 Application.Idle 是应用上下文，读文档相关变量需要文档锁
            var doc = AcadContext.ActiveDocument;
            using (doc.LockDocument())
            {
                foreach (var raw in list)
                {
                    var name = (raw ?? string.Empty).Trim().ToUpperInvariant();
                    if (name.Length == 0) continue;
                    try
                    {
                        values[name] = Format(AcApp.GetSystemVariable(name));
                    }
                    catch (AcRtException ex)
                    {
                        errors[name] = ex.ErrorStatus == ErrorStatus.InvalidInput
                            ? "无此系统变量（或当前上下文不可读）"
                            : ex.Message;
                    }
                    catch (System.Exception ex)
                    {
                        errors[name] = ex.Message;
                    }
                }
            }

            var o = new JObject { ["count"] = values.Count, ["values"] = values };
            if (errors.Count > 0) o["errors"] = errors;
            if (names == null || names.Count == 0)
                o["note"] = "这是常用变量清单；传 names 可读任意变量（如 [\"DIMTXT\",\"PLINEWID\"]）。";
            return o.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>设置一个系统变量。值按变量当前类型转换；只读变量会报错。</summary>
        public static string Set(string rawName, JToken value)
        {
            var name = (rawName ?? string.Empty).Trim().ToUpperInvariant();
            if (name.Length == 0) throw new ArgumentException("name 不能为空。");
            if (value == null || value.Type == JTokenType.Null)
                throw new ArgumentException("value 不能为空。");

            var doc = AcadContext.ActiveDocument;
            using (doc.LockDocument())
            {
                object current;
                try { current = AcApp.GetSystemVariable(name); }
                catch (AcRtException)
                {
                    throw new InvalidOperationException($"无此系统变量：{name}。用 get_sysvars 查看常用变量名。");
                }

                object converted = Coerce(name, current, value);

                try
                {
                    AcApp.SetSystemVariable(name, converted);
                }
                catch (AcRtException ex)
                {
                    throw new InvalidOperationException(
                        $"设置 {name} 失败（{ex.ErrorStatus}）：该变量可能是只读的，或值超出允许范围。当前值 {Format(current)}。");
                }

                var now = AcApp.GetSystemVariable(name);
                return $"{name}: {Format(current)} -> {Format(now)}";
            }
        }

        /// <summary>把 JSON 值转成该变量当前值的 CLR 类型。</summary>
        private static object Coerce(string name, object current, JToken value)
        {
            try
            {
                switch (current)
                {
                    case short _: return (short)value.Value<int>();
                    case int _: return value.Value<int>();
                    case long _: return value.Value<long>();
                    case double _: return value.Value<double>();
                    case string _: return value.Value<string>() ?? string.Empty;
                    case Point3d _: return ParsePoint3d(value);
                    case Point2d _:
                    {
                        var p = ParsePoint3d(value);
                        return new Point2d(p.X, p.Y);
                    }
                    default:
                        return value.ToObject<object>()!;
                }
            }
            catch (System.Exception)
            {
                throw new ArgumentException(
                    $"{name} 需要 {TypeName(current)} 类型的值（当前 {Format(current)}），传入的是 {value.Type}。");
            }
        }

        private static Point3d ParsePoint3d(JToken value)
        {
            if (value is JArray arr && arr.Count >= 2)
                return new Point3d(arr[0]!.Value<double>(), arr[1]!.Value<double>(),
                    arr.Count > 2 ? arr[2]!.Value<double>() : 0);

            var parts = (value.Value<string>() ?? string.Empty).Split(',');
            if (parts.Length < 2) throw new FormatException("点值需要 [x,y] 数组或 \"x,y\" 字符串");
            return new Point3d(double.Parse(parts[0]), double.Parse(parts[1]),
                parts.Length > 2 ? double.Parse(parts[2]) : 0);
        }

        private static string TypeName(object v) => v switch
        {
            short _ => "整数（short）",
            int _ => "整数",
            long _ => "整数",
            double _ => "实数",
            string _ => "字符串",
            Point3d _ => "三维点",
            Point2d _ => "二维点",
            _ => v?.GetType().Name ?? "未知",
        };

        /// <summary>把系统变量值转成 JSON 友好的形式。</summary>
        internal static JToken Format(object v)
        {
            switch (v)
            {
                case null: return JValue.CreateNull();
                case short s: return new JValue((int)s);
                case int i: return new JValue(i);
                case long l: return new JValue(l);
                case double d: return new JValue(d);
                case string str: return new JValue(str);
                case Point3d p: return new JValue($"{p.X:0.####},{p.Y:0.####},{p.Z:0.####}");
                case Point2d p2: return new JValue($"{p2.X:0.####},{p2.Y:0.####}");
                default: return new JValue(v.ToString());
            }
        }
    }
}
