using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using AcadMcp.Acad;
using AcadMcp.Mcp;
using static AcadMcp.Mcp.Schema;

namespace AcadMcp.Tools
{
    /// <summary>P0 工具集（13 个）。每个工具把参数解析后交给 MainThread.Invoke 在主线程执行。</summary>
    internal static class ToolCatalog
    {
        public static List<Tool> Build()
        {
            var tools = new List<Tool>();

            // ---------- 只读 ----------
            tools.Add(new Tool(
                "get_status",
                "返回 AutoCAD 当前状态：图形路径、是否已保存、单位、模型/图纸空间、当前图层、图层数、图形范围。",
                Object(),
                _ => MainThread.Invoke(() => Query.Status())));

            tools.Add(new Tool(
                "list_layers",
                "列出当前图形所有图层及其颜色索引、开/关、锁定、冻结状态，并标出当前图层。",
                Object(),
                _ => MainThread.Invoke(() => Layers.List())));

            tools.Add(new Tool(
                "query_entities",
                "遍历模型空间实体，可按类型（DXF 名如 LINE/CIRCLE/LWPOLYLINE 或 .NET 类名如 Line/Circle/Polyline）与图层过滤；返回 handle、类型、图层、包围盒。",
                Object(
                    P("type", "string", "实体类型过滤，可选"),
                    P("layer", "string", "图层名过滤，可选"),
                    P("limit", "integer", "最多返回条数，默认 500，最大 5000")),
                a => MainThread.Invoke(() => Query.Entities(
                    Args.StrOrNull(a, "type"),
                    Args.StrOrNull(a, "layer"),
                    Args.IntOr(a, "limit", 500)))));

            // ---------- 图层 ----------
            tools.Add(new Tool(
                "create_layer",
                "创建图层；若已存在则仅按需更新颜色。colorIndex 为 AutoCAD 颜色索引 1-255。",
                Object(
                    P("name", "string", "图层名", required: true),
                    P("colorIndex", "integer", "颜色索引 1-255，可选")),
                a => MainThread.Invoke(() => Layers.Create(
                    Args.Str(a, "name"),
                    Args.IntOrNull(a, "colorIndex")))));

            tools.Add(new Tool(
                "set_current_layer",
                "把指定图层设为当前图层；之后未显式指定 layer 的绘制都落在该层。",
                Object(P("name", "string", "图层名", required: true)),
                a => MainThread.Invoke(() => Layers.SetCurrent(Args.Str(a, "name")))));

            // ---------- 绘制 ----------
            tools.Add(new Tool(
                "draw_line",
                "在模型空间画一条直线，返回实体 handle。坐标为当前图形单位。",
                Object(
                    P("x1", "number", "起点 X", required: true),
                    P("y1", "number", "起点 Y", required: true),
                    P("x2", "number", "终点 X", required: true),
                    P("y2", "number", "终点 Y", required: true),
                    P("layer", "string", "目标图层，可选（不存在会自动创建）")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddLine(
                    Args.Num(a, "x1"), Args.Num(a, "y1"),
                    Args.Num(a, "x2"), Args.Num(a, "y2"),
                    Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool(
                "draw_circle",
                "在模型空间画圆，返回 handle。",
                Object(
                    P("cx", "number", "圆心 X", required: true),
                    P("cy", "number", "圆心 Y", required: true),
                    P("r", "number", "半径，必须 > 0", required: true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddCircle(
                    Args.Num(a, "cx"), Args.Num(a, "cy"), Args.Num(a, "r"),
                    Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool(
                "draw_polyline",
                "在模型空间画多段线（closed=true 可得矩形/闭合多边形），返回 handle。",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["points"] = new JObject
                        {
                            ["type"] = "array",
                            ["description"] = "顶点数组，每项为 [x, y]，至少 2 个",
                            ["items"] = new JObject
                            {
                                ["type"] = "array",
                                ["items"] = new JObject { ["type"] = "number" },
                                ["minItems"] = 2,
                                ["maxItems"] = 2,
                            },
                            ["minItems"] = 2,
                        },
                        ["closed"] = new JObject { ["type"] = "boolean", ["description"] = "是否闭合，默认 false" },
                        ["layer"] = new JObject { ["type"] = "string", ["description"] = "目标图层，可选" },
                    },
                    ["required"] = new JArray { "points" },
                    ["additionalProperties"] = false,
                },
                a =>
                {
                    if (!(a["points"] is JArray pa) || pa.Count < 2)
                        throw new McpParamException("points 至少需要 2 个 [x, y]");

                    var pts = new List<(double, double)>();
                    foreach (var p in pa)
                    {
                        if (!(p is JArray xy) || xy.Count < 2)
                            throw new McpParamException("每个点必须是 [x, y] 形式");
                        pts.Add((xy[0]!.Value<double>(), xy[1]!.Value<double>()));
                    }

                    return MainThread.Invoke(() => "handle=" + Draw.AddPolyline(
                        pts, Args.BoolOr(a, "closed", false), Args.StrOrNull(a, "layer")));
                }));

            tools.Add(new Tool(
                "draw_text",
                "在模型空间放置单行文字（DBText），返回 handle。",
                Object(
                    P("x", "number", "插入点 X", required: true),
                    P("y", "number", "插入点 Y", required: true),
                    P("height", "number", "字高，必须 > 0", required: true),
                    P("content", "string", "文字内容", required: true),
                    P("rotation", "number", "旋转角度（度），默认 0"),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddText(
                    Args.Num(a, "x"), Args.Num(a, "y"), Args.Num(a, "height"),
                    Args.Str(a, "content"),
                    Args.StrOrNull(a, "layer"),
                    Args.NumOr(a, "rotation", 0)))));

            // ---------- 修改 / 视图 / 文档 ----------
            tools.Add(new Tool(
                "erase_entity",
                "删除实体。传 handle（单个）/ handles（数组）/ useSelection（当前选择集）。一次删除超过 30 个需 force=true。",
                With(Selectable(Object()), "force", Bool("批量删除（>30）确认")),
                a =>
                {
                    var list = Handles(a);
                    if (list.Count > 30 && !Args.BoolOr(a, "force", false))
                        throw new McpParamException($"将删除 {list.Count} 个实体，数量较大。确认请加 force=true。");
                    return MainThread.Invoke(() => Draw.Erase(list));
                }));

            tools.Add(new Tool(
                "zoom_extents",
                "把当前视图缩放到图形范围（出图或检查前用）。",
                Object(),
                _ => MainThread.Invoke(() => ViewDoc.ZoomExtents())));

            tools.Add(new Tool(
                "save",
                "保存当前图形（QSAVE）。未保存过的新图形会失败，需先在 AutoCAD 中另存为。",
                Object(),
                _ => MainThread.Invoke(() => ViewDoc.Save())));

            tools.Add(new Tool(
                "run_command",
                "【危险 · 逃生舱】向 AutoCAD 命令行发送任意命令串（异步执行）。仅当其它工具无法表达需求时使用。",
                Object(P("command", "string",
                    "完整命令行字符串，例如 \"_.LINE 0,0 100,100 \"（注意结尾空格代表回车）", required: true)),
                a => MainThread.Invoke(() => ViewDoc.RunCommand(Args.Str(a, "command"))),
                dangerous: true));

            // ================= P1 =================

            tools.Add(new Tool(
                "capture_view",
                "截取 AutoCAD 主窗口为 PNG 图片返回（用于查看当前绘图效果、自检）。可传 maxWidth 限制宽度。",
                Object(P("maxWidth", "integer", "输出图片最大宽度像素，默认按窗口原始尺寸；建议 1200 左右")),
                a =>
                {
                    int? mw = Args.IntOrNull(a, "maxWidth");
                    var png = MainThread.Invoke(() => Capture.MainWindowPng(mw));
                    return ToolResult.TextAndImage("AutoCAD 当前视图：", png, "image/png");
                }));

            tools.Add(new Tool(
                "get_entity",
                "按 handle 返回单个实体的详细属性（类型、图层、颜色、包围盒，以及线/圆/多段线/文字/块的专有几何）。",
                Object(P("handle", "string", "实体 handle（十六进制）", required: true)),
                a => MainThread.Invoke(() => Modify.GetEntity(Args.Str(a, "handle")))));

            tools.Add(new Tool(
                "move",
                "按位移量 (dx, dy) 移动实体。传 handle / handles / useSelection。",
                Selectable(Object(
                    P("dx", "number", "X 方向位移", required: true),
                    P("dy", "number", "Y 方向位移", required: true))),
                a => MainThread.Invoke(() => Modify.Move(Handles(a), Args.Num(a, "dx"), Args.Num(a, "dy")))));

            tools.Add(new Tool(
                "copy",
                "复制一个实体到 (dx, dy) 偏移处，可指定份数（阵列），返回新 handle。",
                Object(
                    P("handle", "string", "源实体 handle", required: true),
                    P("dx", "number", "每份 X 偏移", required: true),
                    P("dy", "number", "每份 Y 偏移", required: true),
                    P("count", "integer", "份数，默认 1")),
                a => MainThread.Invoke(() => Modify.Copy(
                    Args.Str(a, "handle"), Args.Num(a, "dx"), Args.Num(a, "dy"), Args.IntOr(a, "count", 1)))));

            tools.Add(new Tool(
                "rotate",
                "绕基点 (baseX, baseY) 旋转实体，angleDeg 逆时针为正。传 handle / handles / useSelection。",
                Selectable(Object(
                    P("baseX", "number", "基点 X", required: true),
                    P("baseY", "number", "基点 Y", required: true),
                    P("angleDeg", "number", "旋转角度（度）", required: true))),
                a => MainThread.Invoke(() => Modify.Rotate(
                    Handles(a), Args.Num(a, "baseX"), Args.Num(a, "baseY"), Args.Num(a, "angleDeg")))));

            tools.Add(new Tool(
                "scale",
                "绕基点 (baseX, baseY) 缩放实体，factor > 0。传 handle / handles / useSelection。",
                Selectable(Object(
                    P("baseX", "number", "基点 X", required: true),
                    P("baseY", "number", "基点 Y", required: true),
                    P("factor", "number", "缩放比例，必须 > 0", required: true))),
                a => MainThread.Invoke(() => Modify.Scale(
                    Handles(a), Args.Num(a, "baseX"), Args.Num(a, "baseY"), Args.Num(a, "factor")))));

            tools.Add(new Tool(
                "offset",
                "偏移一条曲线（Line/Polyline/Circle/Arc），distance 为偏移距离；可用 sideX/sideY 指定偏移到哪一侧。",
                Object(
                    P("handle", "string", "曲线实体 handle", required: true),
                    P("distance", "number", "偏移距离（>0）", required: true),
                    P("sideX", "number", "参考侧点 X，可选"),
                    P("sideY", "number", "参考侧点 Y，可选")),
                a => MainThread.Invoke(() => Modify.Offset(
                    Args.Str(a, "handle"), Args.Num(a, "distance"),
                    Args.NumOrNull(a, "sideX"), Args.NumOrNull(a, "sideY")))));

            tools.Add(new Tool(
                "list_blocks",
                "列出当前图形中已定义的图块名（供 insert_block 使用）。",
                Object(),
                _ => MainThread.Invoke(() => Blocks.List())));

            tools.Add(new Tool(
                "insert_block",
                "在 (x, y) 处插入一个已定义的图块引用，返回 handle。图块必须已存在于当前图形（用 list_blocks 查看）。",
                Object(
                    P("name", "string", "图块名", required: true),
                    P("x", "number", "插入点 X", required: true),
                    P("y", "number", "插入点 Y", required: true),
                    P("xscale", "number", "X 比例，默认 1"),
                    P("yscale", "number", "Y 比例，默认 1"),
                    P("rotation", "number", "旋转角度（度），默认 0")),
                a => MainThread.Invoke(() => Blocks.Insert(
                    Args.Str(a, "name"), Args.Num(a, "x"), Args.Num(a, "y"),
                    Args.NumOr(a, "xscale", 1), Args.NumOr(a, "yscale", 1), Args.NumOr(a, "rotation", 0)))));

            tools.Add(new Tool(
                "save_as",
                "把当前图形另存为指定绝对路径的 .dwg（可保存未命名的新图形）。",
                Object(P("path", "string", "目标绝对路径，例如 D:\\work\\plan.dwg", required: true)),
                a => MainThread.Invoke(() => ViewDoc.SaveAs(Args.Str(a, "path")))));

            tools.Add(new Tool(
                "undo",
                "撤销最近的操作（等价于命令行 U），steps 为撤销步数，默认 1。异步执行。",
                Object(P("steps", "integer", "撤销步数，默认 1，最多 50")),
                a => MainThread.Invoke(() => ViewDoc.Undo(Args.IntOr(a, "steps", 1)))));

            tools.Add(new Tool(
                "get_log",
                "返回本插件今天日志的最后 N 行（服务启停、每次工具调用及其结果 / 耗时、错误）。用于自查刚才发生了什么。",
                Object(P("lines", "integer", "返回行数，默认 50，最多 2000")),
                a => Log.Tail(Args.IntOr(a, "lines", 50))));

            tools.Add(new Tool(
                "eval_lisp",
                "【危险 · 任意代码执行】同步执行一段 AutoLISP 表达式并返回其值（比 run_command 强：能读返回值、定义函数、用 vlax-* 操作对象）。" +
                "默认关闭，需在 AutoCAD 里执行 MCPLISP 开启。示例：(getvar \"DWGNAME\") / (defun c:foo () ...) 定义后可再调。",
                Object(P("code", "string", "AutoLISP 表达式，例如 (+ 1 2) 或 (cdr (assoc 40 (entget (car (entsel)))))", required: true)),
                a => MainThread.Invoke(() => Lisp.Eval(Args.Str(a, "code")), timeoutMs: 60000),
                dangerous: true));

            // ================= P2：绘制 =================

            tools.Add(new Tool("draw_arc",
                "画圆弧（逆时针从 startAngleDeg 到 endAngleDeg）。",
                Object(P("cx", "number", "圆心 X", true), P("cy", "number", "圆心 Y", true),
                    P("r", "number", "半径 >0", true),
                    P("startAngleDeg", "number", "起始角（度）", true), P("endAngleDeg", "number", "终止角（度）", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddArc(
                    Args.Num(a, "cx"), Args.Num(a, "cy"), Args.Num(a, "r"),
                    Args.Num(a, "startAngleDeg"), Args.Num(a, "endAngleDeg"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("draw_ellipse",
                "画椭圆。majorX/majorY 是从圆心指向长轴端点的向量，ratio 是短轴/长轴 (0,1]。",
                Object(P("cx", "number", "圆心 X", true), P("cy", "number", "圆心 Y", true),
                    P("majorX", "number", "长轴向量 X", true), P("majorY", "number", "长轴向量 Y", true),
                    P("ratio", "number", "短轴/长轴，(0,1]", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddEllipse(
                    Args.Num(a, "cx"), Args.Num(a, "cy"), Args.Num(a, "majorX"), Args.Num(a, "majorY"),
                    Args.Num(a, "ratio"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("draw_point",
                "画一个点（DBPoint）。",
                Object(P("x", "number", "X", true), P("y", "number", "Y", true), P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddPoint(Args.Num(a, "x"), Args.Num(a, "y"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("draw_mtext",
                "多行文字（MText）。width=0 表示不自动换行；content 里可用 \\P 换行。",
                Object(P("x", "number", "插入点 X（左上）", true), P("y", "number", "插入点 Y", true),
                    P("width", "number", "文本框宽度，0=不换行", true),
                    P("content", "string", "文字内容", true),
                    P("height", "number", "字高，默认 2.5"),
                    P("rotationDeg", "number", "旋转角度（度），默认 0"),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddMText(
                    Args.Num(a, "x"), Args.Num(a, "y"), Args.Num(a, "width"), Args.Str(a, "content"),
                    Args.NumOr(a, "height", 2.5), Args.StrOrNull(a, "layer"), Args.NumOr(a, "rotationDeg", 0)))));

            tools.Add(new Tool("draw_xline",
                "画构造线（无限长）。(x,y) 上一点，(dirX,dirY) 方向。",
                Object(P("x", "number", "过点 X", true), P("y", "number", "过点 Y", true),
                    P("dirX", "number", "方向 X", true), P("dirY", "number", "方向 Y", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddXline(
                    Args.Num(a, "x"), Args.Num(a, "y"), Args.Num(a, "dirX"), Args.Num(a, "dirY"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("draw_ray",
                "画射线（单向无限长）。(x,y) 起点，(dirX,dirY) 方向。",
                Object(P("x", "number", "起点 X", true), P("y", "number", "起点 Y", true),
                    P("dirX", "number", "方向 X", true), P("dirY", "number", "方向 Y", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => "handle=" + Draw.AddRay(
                    Args.Num(a, "x"), Args.Num(a, "y"), Args.Num(a, "dirX"), Args.Num(a, "dirY"), Args.StrOrNull(a, "layer")))));

            // ================= P2：修改 =================

            tools.Add(new Tool("mirror",
                "沿轴 (x1,y1)-(x2,y2) 镜像实体。keepSource=true 保留源并返回新 handle。传 handle / handles / useSelection。",
                Selectable(Object(
                    P("x1", "number", "镜像轴端点1 X", true), P("y1", "number", "镜像轴端点1 Y", true),
                    P("x2", "number", "镜像轴端点2 X", true), P("y2", "number", "镜像轴端点2 Y", true),
                    P("keepSource", "boolean", "保留源，默认 false"))),
                a => MainThread.Invoke(() => Modify.Mirror(Handles(a),
                    Args.Num(a, "x1"), Args.Num(a, "y1"), Args.Num(a, "x2"), Args.Num(a, "y2"),
                    Args.BoolOr(a, "keepSource", false)))));

            tools.Add(new Tool("explode",
                "分解实体（多段线→线段、块→组成实体等），返回新 handle。传 handle / handles / useSelection。",
                Selectable(Object()),
                a => MainThread.Invoke(() => Edit.Explode(Handles(a)))));

            // trim/extend/fillet/chamfer 走命令队列：不能包 MainThread.Invoke
            // （那会在 Idle 里跑，轮询会阻塞主线程导致命令无法执行）。
            tools.Add(new Tool("trim",
                "以 cutting 为剪切边修剪 targets（都是 handle 数组）。",
                With(With(Object(), "cutting", StrArray("剪切边 handle 数组")), "targets", StrArray("被修剪目标 handle 数组")),
                a => Edit.Trim(StrList(a, "cutting"), StrList(a, "targets"))));

            tools.Add(new Tool("extend",
                "把 targets 延伸到 boundary 边界（都是 handle 数组）。",
                With(With(Object(), "boundary", StrArray("边界 handle 数组")), "targets", StrArray("被延伸目标 handle 数组")),
                a => Edit.Extend(StrList(a, "boundary"), StrList(a, "targets"))));

            tools.Add(new Tool("fillet",
                "在两个实体间倒圆角。radius=0 得尖角（延伸/修剪相交）。",
                Object(P("handle1", "string", "实体1 handle", true), P("handle2", "string", "实体2 handle", true),
                    P("radius", "number", "圆角半径 >=0", true)),
                a => Edit.Fillet(Args.Str(a, "handle1"), Args.Str(a, "handle2"), Args.Num(a, "radius"))));

            tools.Add(new Tool("chamfer",
                "在两个实体间倒角。dist2 缺省等于 dist1。",
                Object(P("handle1", "string", "实体1 handle", true), P("handle2", "string", "实体2 handle", true),
                    P("dist1", "number", "第一距离 >=0", true), P("dist2", "number", "第二距离，默认等于 dist1")),
                a => Edit.Chamfer(Args.Str(a, "handle1"), Args.Str(a, "handle2"),
                    Args.Num(a, "dist1"), Args.NumOr(a, "dist2", Args.Num(a, "dist1")))));

            tools.Add(new Tool("break_entity",
                "在 (x1,y1) 和 (x2,y2) 之间打断实体（两点相同则只打断不删除）。",
                Object(P("handle", "string", "实体 handle", true),
                    P("x1", "number", "第一断点 X", true), P("y1", "number", "第一断点 Y", true),
                    P("x2", "number", "第二断点 X", true), P("y2", "number", "第二断点 Y", true)),
                a => MainThread.Invoke(() => Edit.BreakAt(Args.Str(a, "handle"),
                    Args.Num(a, "x1"), Args.Num(a, "y1"), Args.Num(a, "x2"), Args.Num(a, "y2")))));

            tools.Add(new Tool("join",
                "把多个共线/连续的实体合并成一个（handle 数组，至少 2 个）。",
                With(Object(), "handles", StrArray("要合并的 handle 数组")),
                a => MainThread.Invoke(() => Edit.Join(StrList(a, "handles")))));

            // ================= P2：标注 =================

            tools.Add(new Tool("dim_linear",
                "线性标注：测量 (x1,y1)-(x2,y2)，标注线过 (dimX,dimY)。rotationDeg=0 量水平距离，90 量竖直。",
                Object(P("x1", "number", "点1 X", true), P("y1", "number", "点1 Y", true),
                    P("x2", "number", "点2 X", true), P("y2", "number", "点2 Y", true),
                    P("dimX", "number", "标注线上一点 X", true), P("dimY", "number", "标注线上一点 Y", true),
                    P("rotationDeg", "number", "标注方向角，默认 0"),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => Dim.Linear(
                    Args.Num(a, "x1"), Args.Num(a, "y1"), Args.Num(a, "x2"), Args.Num(a, "y2"),
                    Args.Num(a, "dimX"), Args.Num(a, "dimY"), Args.NumOr(a, "rotationDeg", 0), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("dim_aligned",
                "对齐标注：沿 (x1,y1)-(x2,y2) 连线方向测量真实距离，标注线过 (dimX,dimY)。",
                Object(P("x1", "number", "点1 X", true), P("y1", "number", "点1 Y", true),
                    P("x2", "number", "点2 X", true), P("y2", "number", "点2 Y", true),
                    P("dimX", "number", "标注线上一点 X", true), P("dimY", "number", "标注线上一点 Y", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => Dim.Aligned(
                    Args.Num(a, "x1"), Args.Num(a, "y1"), Args.Num(a, "x2"), Args.Num(a, "y2"),
                    Args.Num(a, "dimX"), Args.Num(a, "dimY"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("dim_angular",
                "角度标注：顶点 (vx,vy)，两边分别过 (x1,y1) 和 (x2,y2)，标注弧过 (arcX,arcY)。",
                Object(P("vx", "number", "顶点 X", true), P("vy", "number", "顶点 Y", true),
                    P("x1", "number", "边1 上一点 X", true), P("y1", "number", "边1 上一点 Y", true),
                    P("x2", "number", "边2 上一点 X", true), P("y2", "number", "边2 上一点 Y", true),
                    P("arcX", "number", "标注弧位置 X", true), P("arcY", "number", "标注弧位置 Y", true),
                    P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => Dim.Angular(
                    Args.Num(a, "vx"), Args.Num(a, "vy"), Args.Num(a, "x1"), Args.Num(a, "y1"),
                    Args.Num(a, "x2"), Args.Num(a, "y2"), Args.Num(a, "arcX"), Args.Num(a, "arcY"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("dim_radius",
                "半径标注（对圆或圆弧 handle）。",
                Object(P("handle", "string", "圆/圆弧 handle", true), P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => Dim.Radius(Args.Str(a, "handle"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("dim_diameter",
                "直径标注（对圆 handle）。",
                Object(P("handle", "string", "圆 handle", true), P("layer", "string", "目标图层，可选")),
                a => MainThread.Invoke(() => Dim.Diameter(Args.Str(a, "handle"), Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool("leader",
                "引线 + 文字。points 是折线顶点 [[x,y],...]（箭头在第一点，文字在最后一点）。",
                With(Object(P("text", "string", "引线文字", true),
                        P("height", "number", "字高，默认 2.5"),
                        P("layer", "string", "目标图层，可选")),
                    "points", PointArray("引线折线顶点 [[x,y],...]，至少 2 个")),
                a => MainThread.Invoke(() => Dim.Leader(PtList(a, "points"), Args.Str(a, "text"),
                    Args.NumOr(a, "height", 2.5), Args.StrOrNull(a, "layer")))));

            // ================= P2：图案填充 =================

            tools.Add(new Tool("hatch",
                "用一组已有闭合实体作边界创建关联图案填充。pattern 如 ANSI31 / SOLID / NET / DOTS。传 boundaryHandles 或 useSelection。",
                With(Object(
                        P("pattern", "string", "图案名，默认 ANSI31"),
                        P("scale", "number", "图案比例，默认 1"),
                        P("angleDeg", "number", "图案角度（度），默认 0"),
                        P("layer", "string", "目标图层，可选"),
                        P("useSelection", "boolean", "用当前选择集作边界")),
                    "boundaryHandles", StrArray("边界实体 handle 数组")),
                a =>
                {
                    var bnd = Args.BoolOr(a, "useSelection", false)
                        ? new List<string>(SelectionStore.Get())
                        : StrList(a, "boundaryHandles");
                    return MainThread.Invoke(() => HatchOps.Create(bnd,
                        Args.StrOrNull(a, "pattern") ?? "ANSI31",
                        Args.NumOr(a, "scale", 1), Args.NumOr(a, "angleDeg", 0), Args.StrOrNull(a, "layer")));
                }));

            // ================= P2：选择集 + 测量 + 块 =================

            tools.Add(new Tool("select",
                "按条件选择模型空间实体并设为当前选择集（供 move/copy/rotate/scale/mirror/erase_entity/hatch 用 useSelection 引用）。" +
                "window：完全在框内；crossing=true：与框相交。",
                new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["type"] = new JObject { ["type"] = "string", ["description"] = "类型过滤（DXF 名或 .NET 类名）" },
                        ["layer"] = new JObject { ["type"] = "string", ["description"] = "图层过滤" },
                        ["colorIndex"] = new JObject { ["type"] = "integer", ["description"] = "颜色索引过滤 1-255" },
                        ["blockName"] = new JObject { ["type"] = "string", ["description"] = "块名过滤（只选该块的引用）" },
                        ["window"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject { ["type"] = "number" },
                            ["minItems"] = 4, ["maxItems"] = 4,
                            ["description"] = "[x1,y1,x2,y2] 窗口范围",
                        },
                        ["crossing"] = new JObject { ["type"] = "boolean", ["description"] = "true=交叉窗口，默认 false=窗口" },
                    },
                    ["additionalProperties"] = false,
                },
                a =>
                {
                    double[]? win = null;
                    if (a["window"] is JArray w && w.Count == 4)
                        win = new[] { w[0]!.Value<double>(), w[1]!.Value<double>(), w[2]!.Value<double>(), w[3]!.Value<double>() };
                    return MainThread.Invoke(() => Query.Select(
                        Args.StrOrNull(a, "type"), Args.StrOrNull(a, "layer"),
                        Args.IntOrNull(a, "colorIndex"), Args.StrOrNull(a, "blockName"),
                        win, Args.BoolOr(a, "crossing", false)));
                }));

            tools.Add(new Tool("measure_distance",
                "两点间距离、Δx、Δy、角度。",
                Object(P("x1", "number", "点1 X", true), P("y1", "number", "点1 Y", true),
                    P("x2", "number", "点2 X", true), P("y2", "number", "点2 Y", true)),
                a => MainThread.Invoke(() => Measure.Distance(
                    Args.Num(a, "x1"), Args.Num(a, "y1"), Args.Num(a, "x2"), Args.Num(a, "y2")))));

            tools.Add(new Tool("measure_area",
                "闭合实体（圆/多段线/面域/填充/椭圆）的面积和周长。",
                Object(P("handle", "string", "实体 handle", true)),
                a => MainThread.Invoke(() => Measure.Area(Args.Str(a, "handle")))));

            // ---- P2.5 安全网：命名 undo 标记 ----

            tools.Add(new Tool("mark",
                "打一个命名 undo 标记（做风险操作前用），对应 AutoCAD 的 UNDO Mark。之后 rollback 精确回到这里。",
                Object(P("label", "string", "标记名，可选")),
                a => MainThread.Invoke(() =>
                {
                    ViewDoc.RunCommand("_.UNDO _Mark");
                    int id = Mcp.Safety.AddMark(Args.StrOrNull(a, "label"));
                    return $"已打标记 #{id}。标记栈：{Mcp.Safety.DescribeMarks()}";
                })));

            tools.Add(new Tool("rollback",
                "撤销回到最近一个 mark（AutoCAD 的 UNDO Back，弹出一层标记）。没打过 mark 时拒绝，避免误撤整张图。",
                Object(),
                _ =>
                {
                    if (!Mcp.Safety.HasMark)
                        throw new McpParamException("本次会话没有 mark，拒绝 rollback。先调 mark，或用 undo {steps}。");
                    return MainThread.Invoke(() =>
                    {
                        ViewDoc.RunCommand("_.UNDO _Back");
                        var label = Mcp.Safety.PopMark();
                        return $"已回滚到标记 {label}。剩余标记：{Mcp.Safety.DescribeMarks()}";
                    });
                }));

            tools.Add(new Tool("define_block",
                "用一组已有实体定义一个新图块。keepSource=false 时把源实体替换为原位块引用。",
                With(Object(P("name", "string", "新图块名", true),
                        P("baseX", "number", "基点 X", true), P("baseY", "number", "基点 Y", true),
                        P("keepSource", "boolean", "保留源实体，默认 true")),
                    "entityHandles", StrArray("要打包的实体 handle 数组")),
                a => MainThread.Invoke(() => Blocks.Define(Args.Str(a, "name"), StrList(a, "entityHandles"),
                    Args.Num(a, "baseX"), Args.Num(a, "baseY"), Args.BoolOr(a, "keepSource", true)))));

            return tools;
        }

        /// <summary>从参数里取 handle（单个）、handles（数组）或 useSelection（当前选择集）。</summary>
        private static List<string> Handles(JObject a)
        {
            if (Args.BoolOr(a, "useSelection", false))
            {
                if (!SelectionStore.HasSelection)
                    throw new McpParamException("useSelection=true 但当前选择集为空，先调用 select。");
                return new List<string>(SelectionStore.Get());
            }
            var list = StrList(a, "handles");
            if (Args.StrOrNull(a, "handle") is string h) list.Insert(0, h);
            if (list.Count == 0)
                throw new McpParamException("需要 handle / handles / useSelection");
            return list;
        }

        /// <summary>取字符串数组参数（缺失返回空 list）。</summary>
        private static List<string> StrList(JObject a, string key)
        {
            var list = new List<string>();
            if (a[key] is JArray arr)
                foreach (var x in arr)
                {
                    var s = (string?)x;
                    if (!string.IsNullOrEmpty(s)) list.Add(s!);
                }
            return list;
        }

        /// <summary>取 [[x,y],...] 点数组参数。</summary>
        private static List<(double, double)> PtList(JObject a, string key)
        {
            var pts = new List<(double, double)>();
            if (!(a[key] is JArray arr))
                throw new McpParamException($"缺少点数组参数 {key}（形如 [[x,y],...]）");
            foreach (var p in arr)
            {
                if (!(p is JArray xy) || xy.Count < 2)
                    throw new McpParamException($"{key} 的每一项必须是 [x, y]");
                pts.Add((xy[0]!.Value<double>(), xy[1]!.Value<double>()));
            }
            return pts;
        }
    }
}
