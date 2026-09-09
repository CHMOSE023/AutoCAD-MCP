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
                "列出当前图形所有图层：颜色索引、线宽、线型、开/关、锁定、冻结，并标出当前图层。",
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
                "创建图层；若已存在则仅按需更新颜色 / 线型。colorIndex 为 AutoCAD 颜色索引 1-255。" +
                "linetype 可传 CENTER（中心线）/ DASHED（虚线）/ HIDDEN / PHANTOM 等，图形里没有会自动从线型库加载。",
                Object(
                    P("name", "string", "图层名", required: true),
                    P("colorIndex", "integer", "颜色索引 1-255，可选"),
                    P("linetype", "string", "线型名，可选；轴线 / 道路中心线用 CENTER"),
                    P("lineWeight", "number",
                        "线宽（毫米），可选。单线表达的建筑图靠它分层次：承重墙柱 0.7 / 隔墙 0.35 / 家具填充 0.18 / 轴线标注 0.13。" +
                        "会就近取 AutoCAD 的标准档；屏幕上要看见需 set_sysvar LWDISPLAY=1，打印始终生效")),
                a => MainThread.Invoke(() => Layers.Create(
                    Args.Str(a, "name"),
                    Args.IntOrNull(a, "colorIndex"),
                    Args.StrOrNull(a, "linetype"),
                    Args.NumOrNull(a, "lineWeight")))));

            tools.Add(new Tool(
                "list_linetypes",
                "列出图形中已加载的线型。未列出的名字也可直接用，会自动从 acadiso.lin / acad.lin 加载。",
                Object(),
                _ => MainThread.Invoke(() => Layers.ListLinetypes())));

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
                "截取 AutoCAD 为 PNG 图片返回（查看当前绘图效果、自检）。region=drawing 只截绘图区（更干净、更省 token）；" +
                "zoomExtents=true 会先缩放到图形范围再截。",
                Object(P("maxWidth", "integer", "输出图片最大宽度像素，默认按窗口原始尺寸；建议 1200 左右"),
                    P("region", "string", "window（整窗，默认）或 drawing（只要绘图区）"),
                    P("zoomExtents", "boolean", "截图前先缩放到图形范围，默认 false")),
                a =>
                {
                    int? mw = Args.IntOrNull(a, "maxWidth");
                    string? region = Args.StrOrNull(a, "region");
                    bool zoom = Args.BoolOr(a, "zoomExtents", false);
                    var shot = MainThread.Invoke(() =>
                    {
                        if (zoom) ViewDoc.ZoomExtents();
                        return Capture.Take(mw, region);
                    });
                    return ToolResult.TextAndImage(shot.Info, shot.Png, "image/png");
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

            tools.Add(new Tool("set_entity_layer",
                "把已有实体改到指定图层（图层不存在会自动创建），可选同时改颜色 / 线宽。" +
                "批量插块后忘了指定图层、或事后要重新分层时用它 —— 图层是按图层配线宽和跑 check_* 校验的前提。" +
                "传 handle / handles / useSelection。",
                Selectable(Object(
                    P("layer", "string", "目标图层名", true),
                    P("colorIndex", "integer", "实体颜色索引 1-255，可选（一般不用，让它跟图层走）"),
                    P("lineWeight", "number", "实体线宽（毫米），可选（一般不用，让它跟图层走）"),
                    P("byLayerColor", "boolean", "把颜色重置为 ByLayer，默认 false"))),
                a => MainThread.Invoke(() => Modify.SetLayer(Handles(a),
                    Args.Str(a, "layer"), Args.IntOrNull(a, "colorIndex"),
                    Args.NumOrNull(a, "lineWeight"), Args.BoolOr(a, "byLayerColor", false)), 60000)));

            tools.Add(new Tool("array_rect",
                "矩形阵列：把实体按行列复制（柱网、车位、座椅）。rows×cols 含原件本身，" +
                "行距 rowSpacing 沿 Y、列距 colSpacing 沿 X，可为负；angleDeg 让整个阵列倾斜（斜列式车位）。" +
                "源实体保留。比 copy 强的地方是两个方向同时铺。",
                Selectable(Object(
                    P("rows", "integer", "行数（含原件），沿 Y", true),
                    P("cols", "integer", "列数（含原件），沿 X", true),
                    P("rowSpacing", "number", "行距，可为负（向下铺）"),
                    P("colSpacing", "number", "列距，可为负（向左铺）"),
                    P("angleDeg", "number", "整个阵列的倾斜角，默认 0"))),
                a => MainThread.Invoke(() => Arrange.Rectangular(Handles(a),
                    Args.IntOr(a, "rows", 1), Args.IntOr(a, "cols", 1),
                    Args.NumOr(a, "rowSpacing", 0), Args.NumOr(a, "colSpacing", 0),
                    Args.NumOr(a, "angleDeg", 0)), 60000)));

            tools.Add(new Tool("array_polar",
                "环形阵列：绕 (centerX, centerY) 均布 count 份（含原件）。fillAngleDeg 默认 360 整圈。" +
                "rotateItems=true 每份跟着转（辐条、螺栓），false 保持原朝向（树、路灯、家具）。源实体保留。",
                Selectable(Object(
                    P("centerX", "number", "阵列中心 X", true),
                    P("centerY", "number", "阵列中心 Y", true),
                    P("count", "integer", "总份数（含原件），>=2", true),
                    P("fillAngleDeg", "number", "张角，默认 360（整圈均布）"),
                    P("rotateItems", "boolean", "每份是否跟着旋转，默认 true"))),
                a => MainThread.Invoke(() => Arrange.Polar(Handles(a),
                    Args.Num(a, "centerX"), Args.Num(a, "centerY"),
                    Args.IntOr(a, "count", 2), Args.NumOr(a, "fillAngleDeg", 360),
                    Args.BoolOr(a, "rotateItems", true)), 60000)));

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
                "列出当前图形中已定义的图块：实体数、基点、**相对基点的包围盒**（bboxFromBase）、占位尺寸、已插入次数。" +
                "插入前先看 bboxFromBase —— 它决定块相对插入点往哪个方向长，基点在底边的块最容易插反。",
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
                    P("rotation", "number", "旋转角度（度），默认 0"),
                    P("layer", "string", "目标图层，可选（不存在会自动创建）。不传则落在当前图层 —— 批量插块时建议显式指定，否则事后按图层查越界 / 配线宽会对不上")),
                a => MainThread.Invoke(() => Blocks.Insert(
                    Args.Str(a, "name"), Args.Num(a, "x"), Args.Num(a, "y"),
                    Args.NumOr(a, "xscale", 1), Args.NumOr(a, "yscale", 1), Args.NumOr(a, "rotation", 0),
                    Args.StrOrNull(a, "layer")))));

            tools.Add(new Tool(
                "save_as",
                "把当前图形另存为指定绝对路径的 .dwg（可保存未命名的新图形），并把当前文档切换到该文件。" +
                "文件是同步写出的（立即可用），文档切换走命令队列异步完成，之后 get_status 会显示新路径。",
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

            tools.Add(new Tool("draw_spline",
                "画样条曲线（NURBS）。默认 method=\"fit\" 拟合点方式——曲线严格穿过给的每个点，" +
                "画等高线 / 河道 / 管线走向 / 自由曲线用这个；method=\"cv\" 是控制点方式，曲线被控制点拉扯、" +
                "一般不经过它们，形状更平滑可控，点数至少 degree+1 个（closed=true 时 degree 个即可）。可选起终点切向（仅拟合点方式，成对给）。",
                With(Object(
                        P("method", "string", "fit=拟合点（默认，穿过每个点）；cv=控制点"),
                        P("closed", "boolean", "是否闭合成环，默认 false"),
                        P("degree", "integer", "阶次，默认 3（三次样条），范围 1-11"),
                        P("fitTolerance", "number", "拟合公差，默认 0（严格穿过每个点）；仅拟合点方式有效"),
                        P("startTangentX", "number", "起点切向 X，可选（与 startTangentY / 终点切向成对给）"),
                        P("startTangentY", "number", "起点切向 Y，可选"),
                        P("endTangentX", "number", "终点切向 X，可选"),
                        P("endTangentY", "number", "终点切向 Y，可选"),
                        P("layer", "string", "目标图层，可选")),
                    "points", PointArray("型值点数组 [[x,y],...]；拟合点方式至少 2 个，控制点方式至少 degree+1 个（closed=true 时 degree 个即可）")),
                a =>
                {
                    var method = (Args.StrOrNull(a, "method") ?? "fit").Trim().ToLowerInvariant();
                    bool cv = method == "cv" || method == "control" || method == "controlpoints";
                    if (!cv && method != "fit")
                        throw new McpParamException("method 只能是 fit（拟合点）或 cv（控制点）");

                    var st = TangentOrNull(a, "startTangentX", "startTangentY");
                    var et = TangentOrNull(a, "endTangentX", "endTangentY");
                    var pts = PtList(a, "points");

                    return MainThread.Invoke(() => "handle=" + Draw.AddSpline(
                        pts, cv, Args.BoolOr(a, "closed", false), Args.IntOr(a, "degree", 3),
                        Args.NumOr(a, "fitTolerance", 0), st, et, Args.StrOrNull(a, "layer")));
                }));

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
                        // UNDO 走命令队列，是异步的：AutoCAD 在后台没有消息可处理时会拖上几秒才执行
                        return $"已发送回滚到标记 {label} 的命令（异步执行，通常 1 秒内完成；" +
                               $"AutoCAD 在后台时可能更久）。用 query_entities 确认结果。剩余标记：{Mcp.Safety.DescribeMarks()}";
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

            // ================= P3：系统变量与单位 =================

            tools.Add(new Tool("get_sysvars",
                "读系统变量（GETVAR）。不传 names 时返回一份常用变量快照（图层/单位/标注/捕捉/显示等）。",
                With(Object(), "names", StrArray("要读的变量名数组，如 [\"DIMTXT\",\"OSMODE\"]；省略则读常用清单")),
                a => MainThread.Invoke(() => Sysvars.Get(StrList(a, "names")))));

            tools.Add(new Tool("set_sysvar",
                "设一个系统变量（SETVAR）。值的类型按变量当前类型自动转换；只读变量会报错。",
                With(Object(P("name", "string", "变量名，如 OSMODE / DIMTXT / LTSCALE", true)),
                    "value", Any("新值：整数 / 实数 / 字符串 / 点 [x,y]，按变量类型给")),
                a =>
                {
                    var v = a["value"] ?? throw new McpParamException("缺少必填参数：value");
                    return MainThread.Invoke(() => Sysvars.Set(Args.Str(a, "name"), v));
                }));

            tools.Add(new Tool("get_units",
                "查看图形单位设置：INSUNITS（1 图形单位代表的现实长度）、长度/角度显示格式与精度、LTSCALE、DIMSCALE。",
                Object(),
                _ => MainThread.Invoke(() => Units.Get())));

            tools.Add(new Tool("set_units",
                "改图形单位设置。只影响插入缩放与数值显示格式，不会缩放已有几何。",
                Object(P("insunits", "string", "插入单位：mm / cm / m / km / in / ft / yd / mi 或 INSUNITS 数值 0-24"),
                    P("lunits", "integer", "长度格式 1=科学 2=小数 3=工程 4=建筑 5=分数"),
                    P("luprec", "integer", "长度精度（小数位）0-8"),
                    P("aunits", "integer", "角度格式 0=十进制度 1=度分秒 2=百分度 3=弧度 4=勘测"),
                    P("auprec", "integer", "角度精度 0-8")),
                a => MainThread.Invoke(() => Units.Set(Args.StrOrNull(a, "insunits"),
                    Args.IntOrNull(a, "lunits"), Args.IntOrNull(a, "luprec"),
                    Args.IntOrNull(a, "aunits"), Args.IntOrNull(a, "auprec")))));

            tools.Add(new Tool("convert_length",
                "长度单位换算（纯计算）。from 省略时用图形当前 INSUNITS。用于把现实尺寸换算成图形单位再画图。",
                Object(P("value", "number", "数值", true),
                    P("to", "string", "目标单位：mm / cm / m / km / in / ft / yd / mi", true),
                    P("from", "string", "源单位，省略则用图形 INSUNITS")),
                a => Units.Convert(Args.Num(a, "value"), Args.StrOrNull(a, "from"), Args.Str(a, "to"))));

            // ================= P3：布局 / 图纸空间 / 视口 =================

            tools.Add(new Tool("list_layouts",
                "列出所有布局（图纸）及其打印设备、纸张、方向、浮动视口数，并标出当前布局。",
                Object(P("includeViewports", "boolean", "同时列出每个布局的视口明细，默认 false")),
                a => MainThread.Invoke(() => Layouts.List(Args.BoolOr(a, "includeViewports", false)))));

            tools.Add(new Tool("set_layout",
                "切换当前布局。传 Model 回到模型空间（TILEMODE=1），传布局名进入图纸空间。",
                Object(P("name", "string", "布局名，或 Model 表示模型空间", true)),
                a => MainThread.Invoke(() => Layouts.SetCurrent(Args.Str(a, "name")))));

            tools.Add(new Tool("create_layout",
                "新建布局（图纸）。可同时指定打印设备、纸张（A4/A3 或完整纸张名）与方向。",
                Object(P("name", "string", "布局名", true),
                    P("plotDevice", "string", "打印设备，默认沿用；出 PDF 用 DWG To PDF.pc3"),
                    P("paperSize", "string", "纸张，如 A3 / A4 或完整 canonical 名"),
                    P("landscape", "boolean", "横向，默认 false（纵向）"),
                    P("setCurrent", "boolean", "创建后切为当前布局，默认 true")),
                a => MainThread.Invoke(() => Layouts.Create(Args.Str(a, "name"),
                    Args.StrOrNull(a, "plotDevice"), Args.StrOrNull(a, "paperSize"),
                    Args.BoolOr(a, "landscape", false), Args.BoolOr(a, "setCurrent", true)))));

            tools.Add(new Tool("delete_layout",
                "删除一个布局（连同其上的视口与图纸空间实体）。不能删模型空间，也不会删到最后一个布局。",
                Object(P("name", "string", "布局名", true)),
                a => MainThread.Invoke(() => Layouts.Delete(Args.Str(a, "name")))));

            tools.Add(new Tool("list_viewports",
                "列出某个布局上的浮动视口：handle、图纸位置尺寸、对准的模型点、比例、开关与锁定状态。",
                Object(P("layout", "string", "布局名，省略则用当前布局")),
                a => MainThread.Invoke(() => Layouts.ListViewports(Args.StrOrNull(a, "layout")))));

            tools.Add(new Tool("add_viewport",
                "在布局上开一个浮动视口。centerX/centerY/width/height 是图纸坐标（毫米）；" +
                "scale=100 表示 1:100；viewCenterX/Y 指定视口对准的模型空间点。",
                Object(P("centerX", "number", "视口中心 X（图纸毫米）", true),
                    P("centerY", "number", "视口中心 Y（图纸毫米）", true),
                    P("width", "number", "视口宽（图纸毫米）", true),
                    P("height", "number", "视口高（图纸毫米）", true),
                    P("layout", "string", "布局名，省略则用当前布局"),
                    P("scale", "number", "出图比例分母：100 表示 1:100"),
                    P("viewCenterX", "number", "对准的模型空间点 X"),
                    P("viewCenterY", "number", "对准的模型空间点 Y"),
                    P("locked", "boolean", "创建后锁定视口（防止误缩放），默认 false")),
                a => MainThread.Invoke(() => Layouts.AddViewport(Args.StrOrNull(a, "layout"),
                    Args.Num(a, "centerX"), Args.Num(a, "centerY"),
                    Args.Num(a, "width"), Args.Num(a, "height"),
                    Args.NumOrNull(a, "scale"), Args.NumOrNull(a, "viewCenterX"), Args.NumOrNull(a, "viewCenterY"),
                    Args.BoolOr(a, "locked", false)), 60000)));

            tools.Add(new Tool("set_viewport",
                "改浮动视口：比例、对准的模型点、图纸位置与尺寸、开关、锁定。锁定的视口会自动临时解锁再改。",
                Object(P("handle", "string", "视口 handle（list_viewports 获取）", true),
                    P("scale", "number", "出图比例分母：100 表示 1:100"),
                    P("viewCenterX", "number", "对准的模型空间点 X"),
                    P("viewCenterY", "number", "对准的模型空间点 Y"),
                    P("centerX", "number", "视口中心 X（图纸毫米）"),
                    P("centerY", "number", "视口中心 Y（图纸毫米）"),
                    P("width", "number", "视口宽（图纸毫米）"),
                    P("height", "number", "视口高（图纸毫米）"),
                    P("on", "boolean", "打开/关闭视口显示"),
                    P("locked", "boolean", "锁定/解锁")),
                a => MainThread.Invoke(() => Layouts.SetViewport(Args.Str(a, "handle"),
                    Args.NumOrNull(a, "scale"), Args.NumOrNull(a, "viewCenterX"), Args.NumOrNull(a, "viewCenterY"),
                    Args.NumOrNull(a, "centerX"), Args.NumOrNull(a, "centerY"),
                    Args.NumOrNull(a, "width"), Args.NumOrNull(a, "height"),
                    BoolOrNull(a, "on"), BoolOrNull(a, "locked")))));

            // ================= P3：打印 =================

            tools.Add(new Tool("list_plot_devices",
                "列出可用打印设备；并给出某个设备（默认 DWG To PDF.pc3）支持的纸张名与打印样式表，供 plot_pdf 选参数。",
                Object(P("device", "string", "要查纸张的设备名，默认 DWG To PDF.pc3")),
                a => MainThread.Invoke(() => Plot.ListDevices(Args.StrOrNull(a, "device")))));

            tools.Add(new Tool("set_page_setup",
                "把打印设备 / 纸张 / 方向写进布局的页面设置（存进 DWG，之后打印默认就用它）。",
                Object(P("layout", "string", "布局名", true),
                    P("device", "string", "打印设备，默认 DWG To PDF.pc3"),
                    P("paperSize", "string", "纸张，如 A3 / A4 或完整 canonical 名"),
                    P("landscape", "boolean", "横向，默认 false")),
                a => MainThread.Invoke(() => Plot.ApplyPageSetup(Args.Str(a, "layout"),
                    Args.StrOrNull(a, "device"), Args.StrOrNull(a, "paperSize"),
                    Args.BoolOr(a, "landscape", false)), 60000)));

            tools.Add(new Tool("plot_pdf",
                "打印到 PDF（DWG To PDF.pc3）。默认打当前布局的图纸范围；打模型空间时默认按图形范围。" +
                "scale 省略=布满图纸，scale=100 表示 1:100。output 省略则输出到 dwg 同目录并加时间戳。" +
                "只出图形的**局部**：不要指望按矩形裁剪（AutoCAD 2014 的打印引擎打不出内容），" +
                "改用 create_layout + add_viewport（viewCenterX/Y 对准位置、scale 定比例、width/height 定视口大小）再打这个布局。",
                Object(P("layout", "string", "布局名或 Model，省略则用当前布局"),
                    P("output", "string", "输出 PDF 绝对路径，如 D:\\\\work\\\\plan.pdf"),
                    P("paperSize", "string", "纸张，如 A3 / A4，省略则用布局页面设置"),
                    P("landscape", "boolean", "横向；省略则沿用布局设置"),
                    P("area", "string", "打印范围：layout（图纸，默认）或 extents（图形范围）。window / display / limits 在 AutoCAD 2014 上出不来内容，已禁用"),
                    P("scale", "number", "比例分母：100 表示 1:100；省略=布满图纸"),
                    P("monochrome", "boolean", "用 monochrome.ctb 单色打印，默认 false")),
                a =>
                    // 不包 MainThread.Invoke：内部走命令队列（PlotEngine 需要文档上下文）
                    Plot.ToPdf(
                        Args.StrOrNull(a, "layout"), Args.StrOrNull(a, "output"), Args.StrOrNull(a, "paperSize"),
                        BoolOrNull(a, "landscape"), Args.StrOrNull(a, "area") ?? "layout", null,
                        Args.NumOrNull(a, "scale"), Args.BoolOr(a, "monochrome", false))));

            // ================= P3：外部参照 =================

            tools.Add(new Tool("list_xrefs",
                "列出图形里的外部参照：名称、路径、附着方式、解析状态、插入次数与块引用 handle。",
                Object(),
                _ => MainThread.Invoke(() => Xrefs.List())));

            tools.Add(new Tool("attach_xref",
                "把一个 DWG 附着为外部参照并在模型空间插入。overlay=true 用覆盖方式（不随宿主再被参照时嵌套）。",
                Object(P("path", "string", "DWG 路径（绝对路径，或相对当前图形目录）", true),
                    P("x", "number", "插入点 X，默认 0"),
                    P("y", "number", "插入点 Y，默认 0"),
                    P("scale", "number", "缩放，默认 1"),
                    P("rotation", "number", "旋转角度（度），默认 0"),
                    P("overlay", "boolean", "覆盖方式，默认 false=附着"),
                    P("name", "string", "参照名，默认取文件名")),
                a => MainThread.Invoke(() => Xrefs.Attach(Args.Str(a, "path"),
                    Args.NumOr(a, "x", 0), Args.NumOr(a, "y", 0), Args.NumOr(a, "scale", 1),
                    Args.NumOr(a, "rotation", 0), Args.BoolOr(a, "overlay", false),
                    Args.StrOrNull(a, "name")), 120000)));

            tools.Add(new Tool("manage_xrefs",
                "对外部参照执行 reload（重载，取源文件最新内容）/ unload（卸载，保留定义不显示）/ detach（拆离，彻底移除）。names 省略=全部。",
                With(Object(P("op", "string", "reload / unload / detach", true)),
                    "names", StrArray("要操作的参照名数组，省略则全部")),
                a => MainThread.Invoke(() => Xrefs.Operate(Args.Str(a, "op"), StrList(a, "names")), 120000)));

            tools.Add(new Tool("bind_xref",
                "把外部参照绑定成宿主图自己的图块（脱离源文件）。insertBind=true 时符号名不加 $0$ 前缀。",
                With(Object(P("insertBind", "boolean", "Insert 方式绑定（不加前缀），默认 false")),
                    "names", StrArray("要绑定的参照名数组，省略则全部")),
                a => MainThread.Invoke(() => Xrefs.Bind(StrList(a, "names"),
                    Args.BoolOr(a, "insertBind", false)), 120000)));

            // ================= P3：多文档 =================

            // ================= 空间校验：把"位置对不对"也变成可自动判定的 =================

            tools.Add(new Tool("check_overlap",
                "检查一组实体两两之间有没有重叠（按包围盒判定，共边不算）。" +
                "典型用法：房间画在同一图层，查功能分区有没有画重叠。传 layer 查整层，或用 handles 指定。",
                With(Object(P("layer", "string", "要检查的图层名（查整层）"),
                        P("minOverlapArea", "number", "小于该面积的重叠忽略，默认 0")),
                    "handles", StrArray("要检查的实体 handle 数组（与 layer 二选一）")),
                a => MainThread.Invoke(() => Check.Overlap(
                    StrList(a, "handles"), Args.StrOrNull(a, "layer"),
                    Args.NumOr(a, "minOverlapArea", 0)), 60000)));

            tools.Add(new Tool("check_inside",
                "检查一组实体是否都落在某个边界实体内（按包围盒判定）。" +
                "典型用法：房间是否都在建筑轮廓内、建筑是否在用地红线内。越界的会给出各方向超出多少。",
                With(Object(P("boundary", "string", "边界实体的 handle，如建筑轮廓 / 用地红线", true),
                        P("layer", "string", "要检查的图层名（查整层）")),
                    "handles", StrArray("要检查的实体 handle 数组（与 layer 二选一）")),
                a => MainThread.Invoke(() => Check.Inside(
                    StrList(a, "handles"), Args.StrOrNull(a, "layer"), Args.Str(a, "boundary")), 60000)));

            tools.Add(new Tool("check_adjacency",
                "检查两个实体是否相邻（包围盒搭接或间距在 gap 之内）。" +
                "用来核任务书的**功能关系图** —— 候车大厅要挨着检票、检票要挨着发车站台，可逐条验证。" +
                "结果分 PASS（相邻）/ FAIL（不相邻，给出实际间距）/ OVERLAP（压在一起，不是相邻）。",
                Object(P("handleA", "string", "实体 A 的 handle", true),
                    P("handleB", "string", "实体 B 的 handle", true),
                    P("gap", "number", "允许的缝隙（墙厚 / 走廊宽），默认 0")),
                a => MainThread.Invoke(() => Check.Adjacency(
                    Args.Str(a, "handleA"), Args.Str(a, "handleB"), Args.NumOr(a, "gap", 0)), 60000)));

            tools.Add(new Tool("list_documents",
                "列出 AutoCAD 中打开的所有图形及其索引、路径、只读状态，并标出当前活动文档（所有工具都作用于它）。",
                Object(),
                _ => MainThread.Invoke(() => Docs.List())));

            tools.Add(new Tool("activate_document",
                "切换活动文档。之后所有工具都作用于新的活动文档。传 index 或文件名片段。",
                Object(P("name", "string", "文件名片段（大小写不敏感）"),
                    P("index", "integer", "list_documents 里的 index")),
                a => MainThread.Invoke(() => Docs.Activate(Args.StrOrNull(a, "name"), Args.IntOrNull(a, "index")), 60000)));

            tools.Add(new Tool("open_document",
                "打开一个 DWG 并切为活动文档。已打开的直接激活。",
                Object(P("path", "string", "DWG 绝对路径", true),
                    P("readOnly", "boolean", "以只读方式打开，默认 false")),
                a => MainThread.Invoke(() => Docs.Open(Args.Str(a, "path"), Args.BoolOr(a, "readOnly", false)), 180000)));

            tools.Add(new Tool("new_document",
                "按样板新建一个图形并切为活动文档（默认 acadiso.dwt 公制样板）。新图尚未保存，用 save_as 存盘。",
                Object(P("template", "string", "样板文件名或绝对路径，默认 acadiso.dwt")),
                a => MainThread.Invoke(() => Docs.New(Args.StrOrNull(a, "template")), 120000)));

            tools.Add(new Tool("close_document",
                "关闭一个图形。save=true 存盘后关；save=false 丢弃未保存修改（需同时 force=true）。不会关掉最后一个图形。",
                Object(P("name", "string", "文件名片段，省略则关当前活动文档"),
                    P("index", "integer", "list_documents 里的 index"),
                    P("save", "boolean", "关闭前保存，默认 true"),
                    P("force", "boolean", "save=false 时必须显式传 true 才丢弃修改")),
                a => MainThread.Invoke(() => Docs.Close(Args.StrOrNull(a, "name"), Args.IntOrNull(a, "index"),
                    Args.BoolOr(a, "save", true), Args.BoolOr(a, "force", false)), 120000),
                dangerous: true));

            return tools;
        }

        /// <summary>可选布尔参数：缺失返回 null（用于"不传就不改"的语义）。</summary>
        private static bool? BoolOrNull(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.Value<bool>(); }
            catch { throw new McpParamException($"参数 {key} 必须是布尔值"); }
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

        /// <summary>取一对切向分量：两个都没给返回 null，只给一个报错。</summary>
        private static (double, double)? TangentOrNull(JObject a, string keyX, string keyY)
        {
            var x = Args.NumOrNull(a, keyX);
            var y = Args.NumOrNull(a, keyY);
            if (x == null && y == null) return null;
            if (x == null || y == null)
                throw new McpParamException($"{keyX} 与 {keyY} 必须同时给出");
            return (x.Value, y.Value);
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
