using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 当前空间 —— 实体到底建在哪、查询到底扫哪。
    ///
    /// 这个概念原先散在十六处，每一处都写死了模型空间：
    ///
    /// <code>
    /// var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
    /// var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
    /// </code>
    ///
    /// 后果是：Agent 先 set_layout 切到布局 A1，再 draw_polyline 画图框——图元落进了模型空间，
    /// 布局上什么都没有。工具返回的是 <c>handle=28A</c>，一个字都没提它画在哪，
    /// 于是 Agent 以为画成功了，直到自己写 LISP 去读 DXF 组码 67 才发现不对，
    /// 之后九个回合都在绕这个坑。**错得毫无声响，这是最贵的一种错。**
    ///
    /// 正确做法就是 AutoCAD 自己的做法：跟随 TILEMODE。
    /// <c>Database.CurrentSpaceId</c> 在 TILEMODE=1 时是模型空间，=0 时是**当前布局的图纸空间**。
    /// 命令行敲 LINE 落在哪，这些工具就该落在哪。
    ///
    /// 一个已知边界：在布局里双击进浮动视口（MSPACE，CVPORT≠1）时，AutoCAD 的命令会画进模型空间，
    /// 而 <c>CurrentSpaceId</c> 仍返回图纸空间。本插件没有进出 MSPACE 的工具，正常路径下不会走到那个状态；
    /// 真要支持，得同时看 CVPORT，而不是把这里改回写死模型空间。
    /// </summary>
    internal static class Space
    {
        /// <summary>
        /// 当前空间的块表记录。所有"把实体放进图里"和"遍历图里的实体"都必须经此获取。
        /// </summary>
        public static BlockTableRecord Current(Transaction tr, Database db, OpenMode mode)
            => (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, mode);

        /// <summary>
        /// 当前空间的展示名，进工具返回文本。
        ///
        /// **必须跟着结果一起报**。只返回 handle 的话，Agent 无从知道东西落在哪一个空间，
        /// 而这恰恰是它下一步判断"画对了没有"的唯一依据。
        /// </summary>
        public static string CurrentName(Database db)
        {
            if (db.TileMode) return "模型空间";
            string tab = Autodesk.AutoCAD.DatabaseServices.LayoutManager.Current.CurrentLayout;
            return $"布局「{tab}」的图纸空间";
        }

        /// <summary>拼在结果末尾的空间说明，形如 <c>（模型空间）</c>。</summary>
        public static string Suffix(Database db) => $"（{CurrentName(db)}）";
    }
}
