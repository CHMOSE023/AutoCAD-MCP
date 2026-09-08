using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Acad
{
    internal static class Blocks
    {
        /// <summary>用一组已有实体定义一个新图块。keepSource=false 时把源实体替换为原位块引用。</summary>
        public static string Define(string name, IReadOnlyList<string> handles, double baseX, double baseY, bool keepSource)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("图块名不能为空。");
            if (handles.Count == 0) throw new ArgumentException("需要至少一个实体 handle。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var basePt = new Point3d(baseX, baseY, 0);

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                if (bt.Has(name))
                    throw new ArgumentException($"图块 '{name}' 已存在。换个名字或先删除。");

                var srcIds = new ObjectIdCollection();
                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id))
                        throw new ArgumentException($"未找到 handle {h}");
                    srcIds.Add(id);
                }

                var btr = new BlockTableRecord { Name = name, Origin = basePt };
                var btrId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);

                // 克隆源实体的副本进块定义（世界坐标保留，Origin=基点，原位插入即复现）
                var mapping = new IdMapping();
                db.DeepCloneObjects(srcIds, btrId, mapping, false);

                string extra = "，源实体保留。用 insert_block 插入。";
                if (!keepSource)
                {
                    foreach (ObjectId sid in srcIds)
                        if (tr.GetObject(sid, OpenMode.ForWrite) is Entity e) e.Erase();

                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var brf = new BlockReference(basePt, btrId);
                    ms.AppendEntity(brf);
                    tr.AddNewlyCreatedDBObject(brf, true);
                    extra = $"，源实体已替换为原位块引用 handle={brf.Handle}。";
                }

                tr.Commit();
                return $"已定义图块 '{name}'（{handles.Count} 个实体）" + extra;
            }
        }

        public static string List()
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            var names = new List<string>();
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsAnonymous) continue;
                    names.Add(btr.Name);
                }
                tr.Commit();
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.Count == 0
                ? "当前图形没有可用图块定义。"
                : string.Join("\n", names);
        }

        public static string Insert(string name, double x, double y, double xscale, double yscale, double rotationDeg)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(name))
                {
                    var avail = bt.Cast<ObjectId>()
                        .Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
                        .Where(b => !b.IsLayout && !b.IsAnonymous)
                        .Select(b => b.Name);
                    throw new ArgumentException(
                        $"图块 '{name}' 不存在。可用图块：{string.Join(", ", avail)}");
                }

                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var br = new BlockReference(new Point3d(x, y, 0), bt[name])
                {
                    ScaleFactors = new Scale3d(xscale, yscale, 1.0),
                    Rotation = rotationDeg * Math.PI / 180.0,
                };
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);

                // 带属性定义的图块：补建属性引用
                var blockDef = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
                if (blockDef.HasAttributeDefinitions)
                {
                    foreach (ObjectId defId in blockDef)
                    {
                        if (tr.GetObject(defId, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant)
                        {
                            using var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            ar.TextString = ad.TextString;
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                    }
                }

                string handle = br.Handle.ToString();
                tr.Commit();
                return $"已插入图块 '{name}'，handle={handle}";
            }
        }
    }
}
