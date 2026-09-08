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
                    sb.Append(ltr.Name)
                      .Append("  color=").Append(ltr.Color.ColorIndex);
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

        public static string Create(string name, int? colorIndex)
        {
            Validate(name);
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                bool existed = EnsureLayer(tr, db, name, colorIndex);
                tr.Commit();
                return existed
                    ? $"图层 '{name}' 已存在" + (colorIndex.HasValue ? "，颜色已更新。" : "。")
                    : $"已创建图层 '{name}'。";
            }
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
