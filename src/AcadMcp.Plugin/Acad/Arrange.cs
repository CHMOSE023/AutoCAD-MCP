using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 阵列：矩形（行列）与环形（绕中心）。
    ///
    /// 和 <see cref="Modify.Copy"/> 的区别：copy 只能沿一个方向等距复制，
    /// 柱网、车位、座椅这类要的是行列两个方向；环形阵列则用于绕点均布。
    /// 源实体保留，返回新建实体的 handle。
    /// </summary>
    internal static class Arrange
    {
        /// <summary>一次阵列最多生成的实体数，防止手滑写出几万个图元把图纸撑爆。</summary>
        private const int MaxItems = 2000;

        /// <summary>
        /// 矩形阵列。rows×cols 含源实体本身那一份，所以 rows=1,cols=1 什么都不做。
        /// angleDeg 让整个阵列倾斜（斜列式车位、斜向柱网）。
        /// </summary>
        public static string Rectangular(IReadOnlyList<string> handles, int rows, int cols,
            double rowSpacing, double colSpacing, double angleDeg)
        {
            if (rows < 1 || cols < 1) throw new ArgumentException("rows / cols 必须 >= 1。");
            long total = (long)rows * cols * handles.Count;
            if (total > MaxItems)
                throw new ArgumentException(
                    $"这次阵列会生成 {total} 个实体，超过上限 {MaxItems}。请减少行列数，或分批做。");
            if (rows == 1 && cols == 1)
                return "rows 和 cols 都是 1，没有要生成的副本。";

            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            var newHandles = new List<string>();
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id))
                        throw new ArgumentException($"未找到 handle {h}");
                    var src = (Entity)tr.GetObject(id, OpenMode.ForRead);

                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            if (r == 0 && c == 0) continue;   // 原件不动

                            // 先按行列算位移，再整体转 angleDeg
                            double dx = c * colSpacing, dy = r * rowSpacing;
                            var v = new Vector3d(dx * cos - dy * sin, dx * sin + dy * cos, 0);

                            var clone = (Entity)src.Clone();
                            clone.TransformBy(Matrix3d.Displacement(v));
                            ms.AppendEntity(clone);
                            tr.AddNewlyCreatedDBObject(clone, true);
                            newHandles.Add(clone.Handle.ToString());
                        }
                }
                tr.Commit();
            }

            return $"矩形阵列完成：{rows} 行 × {cols} 列，行距 {rowSpacing:0.###}、列距 {colSpacing:0.###}" +
                   (Math.Abs(angleDeg) > 1e-9 ? $"，整体倾斜 {angleDeg:0.##}°" : "") +
                   $"，新增 {newHandles.Count} 个实体。\n" + Brief(newHandles);
        }

        /// <summary>
        /// 环形阵列。count 含源实体本身；fillAngleDeg 默认 360 均布。
        /// rotateItems=true 时每份跟着转（辐条、螺栓孔），false 则保持原朝向（树、路灯）。
        /// </summary>
        public static string Polar(IReadOnlyList<string> handles, double centerX, double centerY,
            int count, double fillAngleDeg, bool rotateItems)
        {
            if (count < 2) throw new ArgumentException("count 必须 >= 2（含源实体本身）。");
            long total = (long)count * handles.Count;
            if (total > MaxItems)
                throw new ArgumentException(
                    $"这次阵列会生成 {total} 个实体，超过上限 {MaxItems}。请减少 count。");

            var center = new Point3d(centerX, centerY, 0);
            bool full = Math.Abs(Math.Abs(fillAngleDeg) - 360.0) < 1e-9;
            // 整圈时最后一份会和第一份重合，所以除以 count；不足整圈则首尾都要占位，除以 count-1
            double step = (fillAngleDeg * Math.PI / 180.0) / (full ? count : count - 1);

            var newHandles = new List<string>();
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id))
                        throw new ArgumentException($"未找到 handle {h}");
                    var src = (Entity)tr.GetObject(id, OpenMode.ForRead);

                    for (int i = 1; i < count; i++)
                    {
                        double ang = step * i;
                        var clone = (Entity)src.Clone();

                        if (rotateItems)
                        {
                            clone.TransformBy(Matrix3d.Rotation(ang, Vector3d.ZAxis, center));
                        }
                        else
                        {
                            // 只把位置绕中心转，实体本身不转：先算旋转后的位置，再纯位移过去
                            var ext = SafeCenter(src);
                            var moved = ext.TransformBy(Matrix3d.Rotation(ang, Vector3d.ZAxis, center));
                            clone.TransformBy(Matrix3d.Displacement(moved - ext));
                        }

                        ms.AppendEntity(clone);
                        tr.AddNewlyCreatedDBObject(clone, true);
                        newHandles.Add(clone.Handle.ToString());
                    }
                }
                tr.Commit();
            }

            return $"环形阵列完成：绕 ({centerX:0.###},{centerY:0.###}) 共 {count} 份" +
                   (full ? "（整圈均布）" : $"（张角 {fillAngleDeg:0.##}°）") +
                   (rotateItems ? "，每份跟着旋转" : "，每份保持原朝向") +
                   $"，新增 {newHandles.Count} 个实体。\n" + Brief(newHandles);
        }

        /// <summary>取实体的定位参考点：优先包围盒中心，取不到就退回原点。</summary>
        private static Point3d SafeCenter(Entity e)
        {
            try
            {
                var ext = e.GeometricExtents;
                return new Point3d(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0, 0);
            }
            catch { return Point3d.Origin; }
        }

        /// <summary>handle 太多时只回前若干个，免得刷屏。</summary>
        private static string Brief(List<string> handles)
        {
            const int show = 20;
            if (handles.Count <= show) return "handle：" + string.Join(", ", handles);
            return "handle（前 " + show + " 个）：" + string.Join(", ", handles.GetRange(0, show)) +
                   $" … 共 {handles.Count} 个";
        }
    }
}
