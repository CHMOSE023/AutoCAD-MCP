using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 图形单位：INSUNITS（一个图形单位代表的现实长度）、LUNITS/LUPREC（长度显示格式）、
    /// AUNITS/AUPREC（角度显示格式），以及长度换算。
    ///
    /// 注意：绘图工具的坐标始终是**图形单位**，本模块不改变已有几何，只影响插入缩放与显示格式。
    /// </summary>
    internal static class Units
    {
        /// <summary>INSUNITS 值 -> 1 单位等于多少米。键即 UnitsValue 的整数值。</summary>
        private static readonly Dictionary<int, double> ToMeters = new Dictionary<int, double>
        {
            [0] = 0,                     // Unitless：无量纲
            [1] = 0.0254,                // Inches
            [2] = 0.3048,                // Feet
            [3] = 1609.344,              // Miles
            [4] = 0.001,                 // Millimeters
            [5] = 0.01,                  // Centimeters
            [6] = 1.0,                   // Meters
            [7] = 1000.0,                // Kilometers
            [8] = 2.54e-8,               // Microinches
            [9] = 2.54e-5,               // Mils
            [10] = 0.9144,               // Yards
            [11] = 1e-10,                // Angstroms
            [12] = 1e-9,                 // Nanometers
            [13] = 1e-6,                 // Microns
            [14] = 0.1,                  // Decimeters
            [15] = 10.0,                 // Dekameters
            [16] = 100.0,                // Hectometers
            [17] = 1e9,                  // Gigameters
            [18] = 1.495978707e11,       // Astronomical units
            [19] = 9.4607304725808e15,   // Light years
            [20] = 3.0856775814913673e16,// Parsecs
            [21] = 0.30480060960121924,  // US Survey Feet（AutoCAD 2017+）
            [22] = 0.0254000508001016,   // US Survey Inch
            [23] = 0.9144018288036576,   // US Survey Yard
            [24] = 1609.3472186944375,   // US Survey Mile
        };

        /// <summary>常用别名 -> UnitsValue 整数值。</summary>
        private static readonly Dictionary<string, int> Alias = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["unitless"] = 0, ["none"] = 0,
            ["in"] = 1, ["inch"] = 1, ["inches"] = 1, ["英寸"] = 1,
            ["ft"] = 2, ["foot"] = 2, ["feet"] = 2, ["英尺"] = 2,
            ["mi"] = 3, ["mile"] = 3, ["miles"] = 3, ["英里"] = 3,
            ["mm"] = 4, ["millimeter"] = 4, ["millimeters"] = 4, ["毫米"] = 4,
            ["cm"] = 5, ["centimeter"] = 5, ["centimeters"] = 5, ["厘米"] = 5,
            ["m"] = 6, ["meter"] = 6, ["meters"] = 6, ["米"] = 6,
            ["km"] = 7, ["kilometer"] = 7, ["kilometers"] = 7, ["千米"] = 7, ["公里"] = 7,
            ["mil"] = 9, ["mils"] = 9,
            ["yd"] = 10, ["yard"] = 10, ["yards"] = 10, ["码"] = 10,
            ["nm"] = 12, ["nanometer"] = 12, ["nanometers"] = 12, ["纳米"] = 12,
            ["um"] = 13, ["micron"] = 13, ["microns"] = 13, ["微米"] = 13,
            ["dm"] = 14, ["decimeter"] = 14, ["decimeters"] = 14, ["分米"] = 14,
        };

        private static readonly string[] LengthFormat =
            { "", "科学计数", "小数", "工程（英尺+英寸小数）", "建筑（英尺+英寸分数）", "分数" };

        private static readonly string[] AngleFormat =
            { "十进制度", "度/分/秒", "百分度", "弧度", "勘测单位" };

        // ---------- 查询 ----------

        public static string Get()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                int ins = (int)db.Insunits;
                int lunits = ToInt(AcApp.GetSystemVariable("LUNITS"));
                int luprec = ToInt(AcApp.GetSystemVariable("LUPREC"));
                int aunits = ToInt(AcApp.GetSystemVariable("AUNITS"));
                int auprec = ToInt(AcApp.GetSystemVariable("AUPREC"));
                int measurement = ToInt(AcApp.GetSystemVariable("MEASUREMENT"));

                var o = new JObject
                {
                    ["insunits"] = db.Insunits.ToString(),
                    ["insunitsValue"] = ins,
                    ["unitMeaning"] = ins == 0
                        ? "无量纲：图块插入不做单位缩放，1 图形单位 = 1 个抽象单位。"
                        : "1 图形单位 = " + Describe(1, ins) + "。",
                    ["measurement"] = measurement == 1 ? "公制（1）" : "英制（0）",
                    ["lengthFormat"] = $"LUNITS={lunits}（{Name(LengthFormat, lunits)}），LUPREC={luprec}（小数位）",
                    ["angleFormat"] = $"AUNITS={aunits}（{Name(AngleFormat, aunits)}），AUPREC={auprec}",
                    ["ltscale"] = Sysvars.Format(AcApp.GetSystemVariable("LTSCALE")),
                    ["dimscale"] = Sysvars.Format(AcApp.GetSystemVariable("DIMSCALE")),
                    ["note"] = "所有绘图工具的坐标 / 长度参数都是图形单位，与此处设置无关；" +
                               "INSUNITS 只影响插入图块 / 外部参照时的自动缩放，以及 convert_length 的默认源单位。",
                };
                return o.ToString(Newtonsoft.Json.Formatting.Indented);
            }
        }

        // ---------- 设置 ----------

        public static string Set(string? insunits, int? lunits, int? luprec, int? aunits, int? auprec)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var changes = new List<string>();

            using (doc.LockDocument())
            {
                if (insunits != null)
                {
                    int v = ParseUnit(insunits);
                    var old = db.Insunits;
                    db.Insunits = (UnitsValue)v;
                    changes.Add($"INSUNITS: {old} -> {db.Insunits}");
                }

                if (lunits.HasValue)
                {
                    Require(lunits.Value, 1, 5, "lunits");
                    changes.Add(SetVar("LUNITS", (short)lunits.Value));
                }
                if (luprec.HasValue)
                {
                    Require(luprec.Value, 0, 8, "luprec");
                    changes.Add(SetVar("LUPREC", (short)luprec.Value));
                }
                if (aunits.HasValue)
                {
                    Require(aunits.Value, 0, 4, "aunits");
                    changes.Add(SetVar("AUNITS", (short)aunits.Value));
                }
                if (auprec.HasValue)
                {
                    Require(auprec.Value, 0, 8, "auprec");
                    changes.Add(SetVar("AUPREC", (short)auprec.Value));
                }
            }

            if (changes.Count == 0)
                throw new ArgumentException("没有传入任何要修改的项（insunits / lunits / luprec / aunits / auprec）。");

            return "已更新单位设置：\n  " + string.Join("\n  ", changes) +
                   "\n注意：这不会缩放已有几何，只影响插入缩放与数值显示格式。";
        }

        // ---------- 换算 ----------

        /// <summary>长度换算。from 省略时用图形当前 INSUNITS。</summary>
        public static string Convert(double value, string? from, string to)
        {
            int fromV;
            string fromLabel;
            if (from != null)
            {
                fromV = ParseUnit(from);
                fromLabel = ((UnitsValue)fromV).ToString();
            }
            else
            {
                fromV = MainThread.Invoke(() => (int)AcadContext.ActiveDocument.Database.Insunits);
                fromLabel = ((UnitsValue)fromV) + "（图形 INSUNITS）";
                if (fromV == 0)
                    throw new ArgumentException(
                        "图形 INSUNITS 是 Unitless，无法推断源单位。请显式传 from（如 mm）。");
            }

            int toV = ParseUnit(to);
            if (fromV == 0 || toV == 0)
                throw new ArgumentException("Unitless（无量纲）不能参与换算。");

            double meters = value * ToMeters[fromV];
            double result = meters / ToMeters[toV];

            return new JObject
            {
                ["value"] = value,
                ["from"] = fromLabel,
                ["to"] = ((UnitsValue)toV).ToString(),
                ["result"] = result,
                ["text"] = $"{Fmt(value)} {Short(fromV)} = {Fmt(result)} {Short(toV)}",
            }.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        // ---------- 辅助 ----------

        /// <summary>把 mm / Millimeters / 4 解析为 UnitsValue 的整数值。</summary>
        internal static int ParseUnit(string s)
        {
            var t = (s ?? string.Empty).Trim();
            if (t.Length == 0) throw new ArgumentException("单位不能为空。");

            if (Alias.TryGetValue(t, out int aliased)) return aliased;

            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            {
                if (!ToMeters.ContainsKey(num))
                    throw new ArgumentException($"INSUNITS 值 {num} 超出已知范围 0-24。");
                return num;
            }

            if (Enum.TryParse(t, ignoreCase: true, result: out UnitsValue uv) && ToMeters.ContainsKey((int)uv))
                return (int)uv;

            throw new ArgumentException(
                $"无法识别的单位 '{s}'。可用：mm / cm / m / km / in / ft / yd / mi，或 INSUNITS 数值 0-24。");
        }

        private static string SetVar(string name, short v)
        {
            var old = AcApp.GetSystemVariable(name);
            AcApp.SetSystemVariable(name, v);
            return $"{name}: {old} -> {v}";
        }

        private static void Require(int v, int lo, int hi, string name)
        {
            if (v < lo || v > hi)
                throw new ArgumentException($"{name} 必须在 {lo}-{hi} 之间，收到 {v}。");
        }

        private static int ToInt(object v) => v is short s ? s : v is int i ? i : System.Convert.ToInt32(v);

        private static string Name(string[] table, int idx)
            => idx >= 0 && idx < table.Length ? table[idx] : "未知";

        /// <summary>把 n 个某单位的长度描述成人类可读的文字。</summary>
        private static string Describe(double n, int unit)
        {
            double meters = n * ToMeters[unit];
            double mm = meters * 1000.0;
            return Math.Abs(mm) < 1000
                ? $"{Fmt(mm)} 毫米（{(UnitsValue)unit}）"
                : $"{Fmt(meters)} 米（{(UnitsValue)unit}）";
        }

        private static string Short(int unit) => unit switch
        {
            1 => "in", 2 => "ft", 3 => "mi", 4 => "mm", 5 => "cm", 6 => "m", 7 => "km",
            9 => "mil", 10 => "yd", 12 => "nm", 13 => "um", 14 => "dm",
            _ => ((UnitsValue)unit).ToString(),
        };

        private static string Fmt(double d)
        {
            if (d == 0) return "0";
            double a = Math.Abs(d);
            if (a >= 1e6 || a < 1e-4) return d.ToString("0.######e+0", CultureInfo.InvariantCulture);
            return d.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
