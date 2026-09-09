using System;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcp.Acad
{
    internal static class Layers
    {
        public static string List()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var sb = new StringBuilder();
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                var currentId = db.Clayer;
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    sb.Append(ltr.Name.PadRight(12))
                      .Append(" color=").Append(ltr.Color.ColorIndex.ToString().PadRight(4));

                    // 线宽 / 线型也列出来 —— 否则配完线宽没法确认是否生效（曾经只能靠 eval_lisp 读 DXF 370）
                    sb.Append(" lw=");
                    sb.Append(ltr.LineWeight == LineWeight.ByLineWeightDefault
                        ? "默认".PadRight(6)
                        : ($"{(int)ltr.LineWeight / 100.0:0.00}mm").PadRight(6));

                    string ltName = "Continuous";
                    if (tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord lr)
                        ltName = lr.Name;
                    sb.Append(" lt=").Append(ltName);

                    if (ltr.IsOff) sb.Append(" [off]");
                    if (ltr.IsLocked) sb.Append(" [locked]");
                    if (ltr.IsFrozen) sb.Append(" [frozen]");
                    if (id == currentId) sb.Append(" [current]");
                    sb.AppendLine();
                }
                tr.Commit();
            }
            return sb.ToString().TrimEnd();
        }

        public static string Create(string name, int? colorIndex, string? linetype, double? lineWeightMm)
        {
            Validate(name);
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                bool existed = EnsureLayer(tr, db, name, colorIndex);

                string ltNote = "";
                string lwNote = "";
                if (!string.IsNullOrWhiteSpace(linetype) || lineWeightMm.HasValue)
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);

                    if (!string.IsNullOrWhiteSpace(linetype))
                    {
                        ltr.LinetypeObjectId = EnsureLinetype(tr, db, linetype!);
                        ltNote = $"，线型 {linetype}";
                    }

                    if (lineWeightMm.HasValue)
                    {
                        var lw = ToLineWeight(lineWeightMm.Value);
                        ltr.LineWeight = lw;
                        lwNote = $"，线宽 {(int)lw / 100.0:0.00}mm";
                    }
                }

                tr.Commit();

                string tip = "";
                if (ltNote.Length > 0)
                    tip += " 点划线 / 虚线在大比例图上可能看不出间隔，用 set_sysvar 调 LTSCALE（1:1000 的总平面设 1000）。";
                if (lwNote.Length > 0)
                    tip += " 线宽默认不在屏幕上显示，set_sysvar LWDISPLAY=1 才看得见（打印时始终生效）。";

                return (existed
                        ? $"图层 '{name}' 已存在" + (colorIndex.HasValue ? "，颜色已更新" : "")
                        : $"已创建图层 '{name}'") + ltNote + lwNote + "。" + tip;
            }
        }

        /// <summary>
        /// 毫米线宽 -> AutoCAD 的 LineWeight 枚举（枚举值就是百分之一毫米）。
        /// 不是标准档位就近取，因为 AutoCAD 只认这些固定档。
        /// 单线表达的建筑图靠线宽分层次：承重墙柱 0.7 / 隔墙 0.35 / 家具填充 0.18 / 轴线标注 0.13。
        /// </summary>
        internal static LineWeight ToLineWeight(double mm)
        {
            if (mm < 0) return LineWeight.ByLayer;

            int want = (int)Math.Round(mm * 100);
            int[] steps = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

            int best = steps[0];
            foreach (var s in steps)
                if (Math.Abs(s - want) < Math.Abs(best - want)) best = s;

            return (LineWeight)best;
        }

        /// <summary>列出图形中已加载的线型。</summary>
        public static string ListLinetypes()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var sb = new StringBuilder();
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                foreach (ObjectId id in ltt)
                {
                    var rec = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    sb.Append(rec.Name);
                    if (!string.IsNullOrWhiteSpace(rec.Comments)) sb.Append("  ").Append(rec.Comments);
                    sb.AppendLine();
                }
                tr.Commit();
            }
            return sb.ToString().TrimEnd() +
                   "\n\n未列出的线型可直接在 create_layer 的 linetype 里写名字，会自动从 acadiso.lin / acad.lin 加载。" +
                   "\n常用：CENTER（中心线）、DASHED（虚线）、HIDDEN（隐藏线）、PHANTOM（双点划线）、DIVIDE（分界线）。";
        }

        /// <summary>
        /// 确保线型已加载并返回其 ObjectId。图形里没有就从线型库文件加载
        /// （公制 acadiso.lin 优先，找不到再试 acad.lin）。
        /// </summary>
        internal static ObjectId EnsureLinetype(Transaction tr, Database db, string name)
        {
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name)) return ltt[name];

            foreach (var file in new[] { "acadiso.lin", "acad.lin" })
            {
                try
                {
                    db.LoadLineTypeFile(name, file);
                    ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                    if (ltt.Has(name)) return ltt[name];
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // 这个库里没有这个线型，试下一个
                }
            }

            throw new ArgumentException(
                $"线型 '{name}' 在图形和 acadiso.lin / acad.lin 里都找不到。用 list_linetypes 查看已加载的线型。");
        }

        public static string SetCurrent(string name)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(name))
                    throw new ArgumentException($"图层 '{name}' 不存在。可先调用 create_layer。");
                db.Clayer = lt[name];
                tr.Commit();
            }
            return $"当前图层已设为 '{name}'。";
        }

        /// <summary>确保图层存在（不存在则创建），可选设置颜色。<returns>true 表示调用前已存在</returns></summary>
        internal static bool EnsureLayer(Transaction tr, Database db, string name, int? colorIndex)
        {
            Validate(name);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);

            ObjectId ltrId;
            bool existed = lt.Has(name);
            if (existed)
            {
                ltrId = lt[name];
            }
            else
            {
                var ltr = new LayerTableRecord { Name = name };
                ltrId = lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }

            if (colorIndex.HasValue)
            {
                short aci = (short)Math.Max(1, Math.Min(255, colorIndex.Value));
                var ltr = (LayerTableRecord)tr.GetObject(ltrId, OpenMode.ForWrite);
                ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, aci);
            }

            return existed;
        }

        private static void Validate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("图层名不能为空。");
            if (name.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' }) >= 0)
                throw new ArgumentException("图层名包含非法字符（< > / \\ \" : ; ? * | , = ` 之一）。");
        }
    }
}
