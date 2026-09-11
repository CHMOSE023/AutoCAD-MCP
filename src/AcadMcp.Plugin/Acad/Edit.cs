using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 修改类工具。
    /// explode/break/join：.NET 原生。
    /// trim/extend/fillet/chamfer：AutoCAD 交互命令（(command) 需要文档上下文），
    /// 通过 <see cref="Lisp.RunViaCommandQueue"/> 送进命令队列执行。
    /// </summary>
    internal static class Edit
    {
        // ---------------- explode（原生）----------------

        public static string Explode(IReadOnlyList<string> handles)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;
            int exploded = 0;
            var newHandles = new List<string>();
            var skipped = new List<string>();

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = Space.Current(tr, db, OpenMode.ForWrite);

                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id)) { skipped.Add(h + "(未找到)"); continue; }
                    if (!(tr.GetObject(id, OpenMode.ForWrite) is Entity ent)) { skipped.Add(h); continue; }

                    var frags = new DBObjectCollection();
                    try { ent.Explode(frags); }
                    catch (AcRx.Exception) { skipped.Add(h + "(不可分解)"); continue; }

                    foreach (DBObject o in frags)
                        if (o is Entity fe)
                        {
                            ms.AppendEntity(fe);
                            tr.AddNewlyCreatedDBObject(fe, true);
                            newHandles.Add(fe.Handle.ToString());
                        }
                    ent.Erase();
                    exploded++;
                }
                tr.Commit();
            }

            var msg = $"已分解 {exploded} 个实体，产生 {newHandles.Count} 个新实体。";
            if (newHandles.Count > 0 && newHandles.Count <= 40)
                msg += " handle：" + string.Join(", ", newHandles);
            if (skipped.Count > 0) msg += $" 跳过：{string.Join(", ", skipped)}";
            return msg;
        }

        // ---------------- break（原生 GetSplitCurves）----------------

        public static string BreakAt(string handle, double x1, double y1, double x2, double y2)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!Draw.TryGetObjectId(db, handle, out var id))
                    throw new ArgumentException($"未找到 handle {handle}");
                if (!(tr.GetObject(id, OpenMode.ForWrite) is Curve curve))
                    throw new ArgumentException("break 只支持曲线实体（Line/Polyline/Arc/…）。");

                var ms = Space.Current(tr, db, OpenMode.ForWrite);

                var p1 = curve.GetClosestPointTo(new Point3d(x1, y1, 0), false);
                var p2 = curve.GetClosestPointTo(new Point3d(x2, y2, 0), false);
                bool onePoint = p1.DistanceTo(p2) < 1e-9;

                var pts = new Point3dCollection { p1 };
                if (!onePoint) pts.Add(p2);

                DBObjectCollection pieces;
                try { pieces = curve.GetSplitCurves(pts); }
                catch (AcRx.Exception)
                { throw new InvalidOperationException("打断失败：点不在实体上，或几何不允许。"); }

                if (pieces.Count == 0)
                    throw new InvalidOperationException("打断未产生结果。");

                var newHandles = new List<string>();
                for (int i = 0; i < pieces.Count; i++)
                {
                    // 两点打断：丢中间段
                    if (!onePoint && i == 1) { (pieces[i] as IDisposable)?.Dispose(); continue; }
                    if (pieces[i] is Entity e)
                    {
                        e.SetPropertiesFrom(curve);
                        ms.AppendEntity(e);
                        tr.AddNewlyCreatedDBObject(e, true);
                        newHandles.Add(e.Handle.ToString());
                    }
                }
                curve.Erase();
                tr.Commit();
                return $"已打断 {handle} → {newHandles.Count} 段：{string.Join(", ", newHandles)}";
            }
        }

        // ---------------- join（原生 JoinEntities）----------------

        public static string Join(IReadOnlyList<string> handles)
        {
            if (handles.Count < 2) throw new ArgumentException("join 至少需要 2 个实体。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ids = new List<ObjectId>();
                foreach (var h in handles)
                {
                    if (!Draw.TryGetObjectId(db, h, out var id))
                        throw new ArgumentException($"未找到 handle {h}");
                    ids.Add(id);
                }

                var first = (Entity)tr.GetObject(ids[0], OpenMode.ForWrite);
                var rest = ids.Skip(1).Select(id => (Entity)tr.GetObject(id, OpenMode.ForWrite)).ToArray();

                int joined;
                try { joined = first.JoinEntities(rest).Count; }
                catch (AcRx.Exception ex)
                { throw new InvalidOperationException($"合并失败（实体类型或几何不兼容）：{ex.Message}"); }

                foreach (var e in rest)
                    if (!e.IsErased && e.ObjectId != first.ObjectId)
                        try { e.UpgradeOpen(); e.Erase(); } catch { }

                tr.Commit();
                return joined > 0
                    ? $"已把 {joined} 个实体合并进 {handles[0]}。"
                    : "未合并任何实体（可能不共线/不连续）。";
            }
        }

        // ---------------- trim / extend / fillet / chamfer（命令队列）----------------

        public static string Trim(IReadOnlyList<string> cutting, IReadOnlyList<string> targets)
        {
            if (cutting.Count == 0 || targets.Count == 0)
                throw new ArgumentException("trim 需要 cutting 和 targets 都非空。");
            var cut = string.Join(" ", cutting.Select(Ent));
            var tgt = string.Join(" ", targets.Select(Ent));
            Cmd($"(vl-cmdf \"._trim\" {cut} \"\" {tgt} \"\")", "修剪");
            return $"已用 {cutting.Count} 条边修剪 {targets.Count} 个目标。";
        }

        public static string Extend(IReadOnlyList<string> boundary, IReadOnlyList<string> targets)
        {
            if (boundary.Count == 0 || targets.Count == 0)
                throw new ArgumentException("extend 需要 boundary 和 targets 都非空。");
            var bnd = string.Join(" ", boundary.Select(Ent));
            var tgt = string.Join(" ", targets.Select(Ent));
            Cmd($"(vl-cmdf \"._extend\" {bnd} \"\" {tgt} \"\")", "延伸");
            return $"已把 {targets.Count} 个目标延伸到 {boundary.Count} 条边界。";
        }

        public static string Fillet(string h1, string h2, double radius)
        {
            if (radius < 0) throw new ArgumentException("圆角半径不能为负。");
            // 拾取点取实体中点（避免落在两实体的公共角点上导致歧义）
            Cmd($"(progn (setvar \"FILLETRAD\" {F(radius)}) (setvar \"TRIMMODE\" 1) " +
                $"(vl-cmdf \"._fillet\" {MidPt(h1)} {MidPt(h2)}))", "倒圆角");
            return $"已在 {h1} 与 {h2} 之间倒圆角 R={radius}。";
        }

        public static string Chamfer(string h1, string h2, double d1, double d2)
        {
            if (d1 < 0 || d2 < 0) throw new ArgumentException("倒角距离不能为负。");
            Cmd($"(progn (setvar \"CHAMMODE\" 0) (setvar \"CHAMFERA\" {F(d1)}) (setvar \"CHAMFERB\" {F(d2)}) " +
                $"(setvar \"TRIMMODE\" 1) (vl-cmdf \"._chamfer\" {MidPt(h1)} {MidPt(h2)}))", "倒角");
            return $"已在 {h1} 与 {h2} 之间倒角 D1={d1} D2={d2}。";
        }

        private static void Cmd(string lisp, string verb)
        {
            var r = Lisp.RunViaCommandQueue(lisp);
            if (!r.Ok) throw new InvalidOperationException($"{verb}失败：{r.Value}");
            if (r.Value.Trim().Equals("nil", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{verb}未生效（命令被拒绝，检查 handle 是否有效、几何是否满足条件）。");
        }

        private static string Ent(string handle)
        {
            if (string.IsNullOrWhiteSpace(handle) || handle.IndexOf('"') >= 0)
                throw new ArgumentException("非法 handle：" + handle);
            return $"(handent \"{handle}\")";
        }

        /// <summary>(ename 中点) 对：fillet/chamfer 的对象选择要"对象+拾取点"，用中点避开公共角。</summary>
        private static string MidPt(string handle)
        {
            var e = Ent(handle);
            return $"(list {e} (vlax-curve-getPointAtParam {e} " +
                   $"(/ (+ (vlax-curve-getStartParam {e}) (vlax-curve-getEndParam {e})) 2.0)))";
        }

        private static string F(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);
    }
}
