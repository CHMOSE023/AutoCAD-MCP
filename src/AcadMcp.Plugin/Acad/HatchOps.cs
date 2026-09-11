using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadMcp.Acad
{
    internal static class HatchOps
    {
        /// <summary>用一组已有闭合实体作边界创建关联图案填充。</summary>
        public static string Create(IReadOnlyList<string> boundaryHandles, string pattern, double scale, double angleDeg, string? layer)
        {
            if (boundaryHandles.Count == 0)
                throw new ArgumentException("需要至少一个边界实体 handle。");

            pattern = string.IsNullOrWhiteSpace(pattern) ? "ANSI31" : pattern.Trim().ToUpperInvariant();
            if (scale <= 0) scale = 1.0;

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = Space.Current(tr, db, OpenMode.ForWrite);

                var ids = new ObjectIdCollection();
                foreach (var h in boundaryHandles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id))
                        throw new ArgumentException($"边界 handle 未找到：{h}");
                    ids.Add(id);
                }

                var hatch = new Hatch();
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    Layers.EnsureLayer(tr, db, layer!, null);
                    hatch.Layer = layer!;
                }

                ms.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                hatch.SetDatabaseDefaults();
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.PatternAngle = angleDeg * Math.PI / 180.0;
                // scale / angle 需再次 SetHatchPattern 才生效
                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.Associative = true;

                try
                {
                    hatch.AppendLoop(HatchLoopTypes.Default, ids);
                    hatch.EvaluateHatch(true);
                }
                catch (AcRx.Exception ex)
                {
                    throw new InvalidOperationException(
                        $"填充失败（边界可能不闭合或不首尾相连）：{ex.Message}");
                }

                string handle = hatch.Handle.ToString();
                tr.Commit();
                return $"已填充 pattern={pattern} scale={scale}，handle={handle}";
            }
        }
    }
}
