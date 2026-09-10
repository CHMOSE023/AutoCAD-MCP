# AutoCAD MCP 插件

让 **Claude Code**（或任何 Agent 客户端）通过 MCP 协议操作 AutoCAD。 

一个 .NET AutoCAD 插件（NETLOAD 进 AutoCAD），插件内嵌一个 HTTP 服务，按 **MCP
Streamable HTTP** 规范对外暴露工具。Agent 以 `type: http` 直连
`http://127.0.0.1:7130/mcp`。 

```
Agent ──HTTP /mcp──▶ AcadMcp.Plugin.dll (NETLOAD 进 AutoCAD)
                             ├─ HttpListener 127.0.0.1:7130 + 手写 MCP JSON-RPC
                             ├─ 工具
                             ├─ 主线程调度 (Application.Idle 队列)
                             ├─ 命令队列桥 (SendStringToExecute + 结果文件轮询)
                             ├─ 安全网（只读模式 / 备份 / mark-rollback / token）
                             └─ AutoCAD.NET API / acedEvaluateLisp → DWG
``` 

---

## 环境要求

| 项 | 要求 |
|---|---|
| AutoCAD | 2014+  |
| .NET Framework | 4.72 |
| 构建 | .NET SDK 8+| 

---

## 1. 构建

```bash
dotnet build src/AcadMcp.Plugin/AcadMcp.Plugin.csproj -c Release
```

产物在 `src/AcadMcp.Plugin/bin/Release/`：

- `AcadMcp.Plugin.dll` — 插件本体
- `Newtonsoft.Json.dll` — 运行时依赖（需与插件放在同一目录）
 

---

## 2. 加载进 AutoCAD

1. 打开 AutoCAD 2020，新建或打开一个 DWG。
2. 命令行输入 `NETLOAD`，选择 `AcadMcp.Plugin.dll`。
3. 加载后自动启动，命令行显示：

   ```
   [AutoCAD MCP] AutoCAD MCP 已启动：http://127.0.0.1:7130/mcp
   [AutoCAD MCP] 接入 Claude Code：claude mcp add --transport http autocad http://127.0.0.1:7130/mcp
   ```

命令：

| 命令 | 作用 |
|---|---|
| `MCPSTART` | 启动服务（自动启动失败时用） |
| `MCPSTOP` | 停止服务 |
| `MCPSTATUS` | 查看运行状态、地址、eval_lisp / 只读 / 鉴权 / 备份 状态 |
| `MCPLISP` | 切换 `eval_lisp` 开关（危险，见工具清单） |
| `MCPREADONLY` | 切换只读模式（拒绝所有写工具） |
| `MCPTOKEN` / `MCPTOKENOFF` | 生成 / 查看 / 清除 HTTP token 鉴权 |

### 如果报「无法监听 / Access Denied」

非管理员进程用 `HttpListener` 有时需要一次性 URL 保留。以**管理员**打开命令行：

```bash
netsh http add urlacl url=http://127.0.0.1:7130/ user=%USERNAME%
```

然后在 AutoCAD 里执行 `MCPSTART`。

### 让插件随 AutoCAD 自动加载（可选）

把 `AcadMcp.Plugin.dll` 和 `Newtonsoft.Json.dll` 放到一个固定目录，
在 AutoCAD 命令行 `APPLOAD` → 「启动组」里添加该 DLL；或做成 `.bundle` 放到
`%APPDATA%\Autodesk\ApplicationPlugins\`。

---

## 3. 接入 Claude Code

插件启动后，在项目目录执行：

```bash
claude mcp add --transport http autocad http://127.0.0.1:7130/mcp
```

或写进项目级 `.mcp.json`（见 [.mcp.json.example](.mcp.json.example)）：

```json
{
  "mcpServers": {
    "autocad": { "type": "http", "url": "http://127.0.0.1:7130/mcp" }
  }
}
```

然后在 Claude Code 里直接说需求，例如：

> 在当前图纸新建一个红色的 WALL 图层，并在上面画一个 5000×3000 的矩形，然后缩放到范围。

---

## 4. 工具清单 

**基础绘图闭环**

| 工具 | 类别 | 说明 |
|---|---|---|
| `get_status` | 只读 | 图形路径、单位、当前图层、图层数、图形范围 |
| `list_layers` | 只读 | 所有图层及颜色 / 开关 / 锁定 / 冻结 / 当前 |
| `query_entities` | 只读 | 遍历模型空间，按类型 / 图层过滤，返回 handle + 包围盒 |
| `create_layer` | 图层 | 建图层（可设颜色索引 1-255） |
| `set_current_layer` | 图层 | 设为当前图层 |
| `draw_line` `draw_circle` `draw_polyline` `draw_text` | 绘制 | 基本图元，返回 handle |
| `erase_entity` | 修改 | 按 handle 删除（单个或数组） |
| `zoom_extents` | 视图 | 缩放到图形范围 |
| `save` | 文档 | 保存当前图形（QSAVE，新图形需先另存） |
| `run_command` | 逃生舱 | 【危险】向命令行发送任意命令串（异步） |

**视觉反馈 + 修改 + 图块**

| 工具 | 类别 | 说明 |
|---|---|---|
| `capture_view` | 视觉 | 截取 AutoCAD 主窗口为 PNG，作为 image 块返回（模型自检用） |
| `get_entity` | 只读 | 单个实体的完整属性（含线 / 圆 / 多段线 / 文字 / 块的专有几何） |
| `move` | 修改 | 按 (dx,dy) 移动（handle 或 handles） |
| `copy` | 修改 | 复制到偏移处，可指定份数 |
| `rotate` | 修改 | 绕基点旋转 |
| `scale` | 修改 | 绕基点缩放 |
| `offset` | 修改 | 曲线偏移（Line / Polyline / Circle / Arc） |
| `list_blocks` | 只读 | 列出图形中已定义的图块名 |
| `insert_block` | 图块 | 在指定点插入已定义图块 |
| `save_as` | 文档 | 另存为指定绝对路径 .dwg，并把当前文档切换到该文件（文件同步写出，切换走命令队列异步完成） |
| `undo` | 修改 | 撤销最近操作（等价命令行 U），可指定步数 |
| `get_log` | 只读 | 返回今天日志最后 N 行（自查刚才发生了什么） |
| `eval_lisp` | 逃生舱 | 【危险 · 任意代码执行】同步执行 AutoLISP 表达式并返回其值。**默认关闭**，见下 |

**工具广度补全**

| 工具 | 类别 | 说明 |
|---|---|---|
| `draw_arc` `draw_ellipse` `draw_point` `draw_xline` `draw_ray` | 绘制 | 圆弧 / 椭圆 / 点 / 构造线 / 射线 |
| `draw_mtext` | 绘制 | 多行文字（`\P` 换行） |
| `mirror` | 修改 | 沿轴镜像（可保留源） |
| `explode` | 修改 | 分解多段线 / 块，返回碎片 handle |
| `trim` / `extend` | 修改 | 按剪切边修剪 / 按边界延伸 |
| `fillet` / `chamfer` | 修改 | 倒圆角 / 倒角 |
| `break_entity` / `join` | 修改 | 两点打断 / 合并共线实体 |
| `dim_linear` `dim_aligned` `dim_angular` `dim_radius` `dim_diameter` | 标注 | 线性 / 对齐 / 角度 / 半径 / 直径 |
| `leader` | 标注 | 引线 + 文字 |
| `hatch` | 填充 | 用闭合实体作边界的关联图案填充 |
| `select` | 只读 | 按类型 / 图层 / 颜色 / 块名 / 窗口筛选，设为**当前选择集** |
| `measure_distance` / `measure_area` | 测量 | 两点距离 / 闭合实体面积周长 |
| `define_block` | 图块 | 用一组实体定义新图块（可原位替换为块引用） |

**布局出图 / 单位 / 外部参照 / 多文档**

| 工具 | 类别 | 说明 |
|---|---|---|
| `get_sysvars` / `set_sysvar` | 只读 / 设置 | 读写任意系统变量（GETVAR / SETVAR）；不传 names 时给一份常用变量快照 |
| `get_units` / `set_units` | 只读 / 设置 | INSUNITS（1 图形单位代表的现实长度）、长度 / 角度格式与精度 |
| `convert_length` | 只读 | 单位换算（mm / cm / m / km / in / ft / yd / mi）；`from` 省略时用图形 INSUNITS |
| `list_layouts` / `set_layout` | 布局 | 列出布局（设备 / 纸张 / 方向 / 视口数）、切换当前布局（`Model` 回模型空间） |
| `create_layout` / `delete_layout` | 布局 | 新建布局（可同时定设备 / 纸张 / 方向）、删除布局 |
| `list_viewports` / `add_viewport` / `set_viewport` | 视口 | 浮动视口的增删改：图纸位置尺寸（毫米）、比例（`scale=100` 即 1:100）、对准的模型点、开关与锁定 |
| `list_plot_devices` | 只读 | 可用打印设备 + 某设备支持的纸张名与打印样式表 |
| `set_page_setup` | 打印 | 把设备 / 纸张 / 方向固化进布局的页面设置 |
| `plot_pdf` | 打印 | 打印到 PDF（`DWG To PDF.pc3`）。范围 `layout`（图纸，默认）/ `extents`（图形范围）；`scale` 省略＝布满图纸；可单色 |
| `list_xrefs` | 只读 | 外部参照：路径、附着方式、解析状态、插入 handle |
| `attach_xref` | 参照 | 附着 / 覆盖一个 DWG 并插入模型空间 |
| `manage_xrefs` | 参照 | `reload` 重载 / `unload` 卸载 / `detach` 拆离（names 省略＝全部） |
| `bind_xref` | 参照 | 绑定为本地图块（脱离源文件） |
| `list_documents` / `activate_document` | 多文档 | 列出打开的图形、切换活动文档 |
| `open_document` / `new_document` / `close_document` | 多文档 | 打开 / 按样板新建 / 关闭（丢弃修改需 `force=true`） |

**多文档模型**：所有工具都作用于**当前活动文档**。同时开多张图时，先 `activate_document` 切过去
再操作 —— 隔离靠"显式切换 + 每次调用都记日志"，而不是并发多目标。
每张图的**首个写操作**前各自触发一次自动备份。

**布局出图的典型流程**：`create_layout`（A3 横向）→ `add_viewport`（图纸毫米定位 + `scale`
定比例 + `viewCenterX/Y` 对准模型区域）→ `set_viewport locked:true` 锁死 → `plot_pdf`。

**只出图形的局部**：别指望按矩形裁剪（`area=window` 在 AutoCAD 2014 上打不出内容，见「已知限制」），
就走上面这条布局 + 视口的路 —— `viewCenterX/Y` 对准要出的位置、`scale` 定比例、`width/height` 定视口大小。
这本来就是 AutoCAD 里控制出图范围的正规做法。

**P3.5 · 阵列 / 线型线宽 / 空间校验**

| 工具 | 类别 | 说明 |
|---|---|---|
| `array_rect` | 阵列 | 矩形阵列：`rows`×`cols` 含原件，行距沿 Y、列距沿 X（可为负），`angleDeg` 让整个阵列倾斜（斜列式车位）。柱网、车位、座席全靠它 —— 91 根柱 / 400 座席都是一次调用 |
| `array_polar` | 阵列 | 环形阵列：绕中心均布，`fillAngleDeg` 默认整圈，`rotateItems` 控制每份跟转（辐条）还是保持朝向（树、路灯） |
| `list_linetypes` | 只读 | 列出已加载线型；未列出的名字也能直接用，会自动从 `acadiso.lin` / `acad.lin` 加载 |
| `set_entity_layer` | 修改 | 把已有实体改到指定图层（自动建层），可选带颜色 / 线宽。批量插块后重新分层用它 |
| `check_overlap` | 校验 | 一组实体两两查重叠（共边不算）。查功能分区有没有画重 |
| `check_inside` | 校验 | 查实体是否都在某边界内，越界的给出各方向超出多少 |
| `check_adjacency` | 校验 | 查两个实体是否相邻，分 PASS / FAIL / **OVERLAP**（压在一起不算相邻）。**用来核任务书的功能关系图** |

**图层三件套**：`create_layer` 增加了 `linetype`（轴线用 CENTER）与 `lineWeight`（毫米，就近取 AutoCAD 标准档）。
单线表达的建筑图靠线宽分层次：承重墙柱 0.7 / 隔墙 0.35 / 家具填充 0.18 / 轴线标注 0.13。
线宽默认不在屏幕显示，`set_sysvar LWDISPLAY=1` 才看得见（**打印时始终生效**）。
`list_layers` 会一并列出线宽与线型，配完能直接确认。

**为什么要有 check_ 这组校验**：面积和数量对不代表画对了。实测里翻过车 —— 块插反方向撞上行道树、
房间位置排错，而面积校核全是满分。这类错只有看图才发现，看图又没法自动化。
这三个检查把「位置对不对」也变成可判定的，和 `measure_area` 对照面积表一起，
形成「面积 + 位置 + 关系」的自动校验闭环。

**`insert_block` 增加 `layer` 参数**：不指定的话块引用会落在**当前图层**上。
实测有 102 个块跑到了 `DIM` 和 `0` 层 —— 后果不只是按图层查越界查不到，
**按图层配的线宽对它们也完全失效**。批量插块务必显式指定图层。

**`list_blocks` 增加 `bboxFromBase`**：块相对基点的包围盒。
插入点给的是基点位置，块往哪个方向长得看它 —— 基点在底边的车位块最容易插反。

**P3.6 · 样条曲线**

| 工具 | 类别 | 说明 |
|---|---|---|
| `draw_spline` | 绘制 | 样条曲线（NURBS）。默认 `method:"fit"` 拟合点——曲线严格穿过给的每个点，等高线 / 河道 / 管线走向 / 自由曲线用它；`method:"cv"` 是控制点方式，曲线不经过控制点、形状更平滑（点数至少 `degree+1`，`closed=true` 时 `degree` 个即可）。`closed` 闭合、`degree` 阶次（默认 3）、`fitTolerance` 拟合公差、起终点切向（仅拟合点方式，四个分量成对给） |

**拟合点还是控制点**：给的是「必须经过的位置」（实测点、道路中线控制点）就用 `fit`；
给的是「大致把曲线拉成这个形状」就用 `cv`。闭合和切向不能同时用 —— 闭合样条的接缝处切向由 AutoCAD 自己接。

**P2.5 · 安全网**

| 工具 | 类别 | 说明 |
|---|---|---|
| `mark` | 安全 | 打一个命名 undo 标记（风险操作前用） |
| `rollback` | 安全 | 撤销回最近一个 mark（没打过 mark 时拒绝，避免误撤整张图） |

- **只读模式**：`MCPREADONLY` 切换，开启后所有写工具返回"只读模式已开启"
- **会话前自动备份**：本次加载后**首个写操作**前，自动把已保存的 dwg 复制一份
  `<dwg>.mcpbak-<时间>.dwg`（未保存的新图跳过）
- **HTTP token 鉴权**：`MCPTOKEN` 生成 token（或设环境变量 `ACADMCP_TOKEN`），之后所有请求
  必须带 `Authorization: Bearer <token>`；`claude mcp add ... --header "Authorization: Bearer <token>"`
- **批量删除保护**：`erase_entity` 一次删 >30 个需 `force=true`
- `get_status` 返回 `readOnly` / `authRequired` / `backups`（已备份的图形）/ `marks` / `layout` / `openDocuments`

修改类工具（`move` `copy` `rotate` `scale` `mirror` `explode` `erase_entity` `hatch`）都支持
`useSelection: true` —— 用 `select` 的结果代替显式 handle。

`trim` / `extend` / `fillet` / `chamfer` 内部走 **AutoCAD 命令队列**（`SendStringToExecute` +
轮询结果文件），因为这些交互命令需要文档上下文；比其它工具慢几百毫秒。不受 `eval_lisp` 开关影响。
`break_entity` / `join` 是 .NET 原生。

坐标与尺寸单位＝当前图形单位；要按现实尺寸下料先用 `convert_length` 换算，`get_units` 看当前 INSUNITS。
写操作立即修改图纸。
`capture_view` 用 `PrintWindow` 抓窗口，`region:"drawing"` 只截绘图区（去掉功能区 / 命令行，更省 token），
`zoomExtents:true` 可在截图前先缩放到图形范围；返回文本里带实际像素与窗口 DPI。

### `eval_lisp` —— 任意代码执行，默认关闭

`eval_lisp` 让模型直接写并执行 AutoLISP（能读返回值、`(defun ...)` 定义函数后复用、`vlax-*`
操作对象），把工具集从固定的那些变成可自扩展。但 AutoLISP 能删文件 / 起进程 / 调 COM，
等于**在你机器上任意代码执行**，风险高于 `run_command`。

- 默认**不可用**，调用会返回"未启用"提示
- 开启：AutoCAD 命令行执行 `MCPLISP`（立即生效，再执行一次关闭）；或设环境变量
  `ACADMCP_ALLOW_LISP=1` 后重启 AutoCAD
- 每次调用的**完整代码**写入日志（`eval_lisp  CODE: ...`）
- `MCPSTATUS` 显示当前开关状态
- 仅在信任对面模型 / 会话时开启

---

## 5. 日志

插件把服务启停、**每次 `tools/call`**（工具名 / ok·ERR / 耗时 / 参数摘要 / 危险标记）、HTTP 错误
写入按天滚动的文件：

```
%LOCALAPPDATA%\AcadMcp\logs\mcp-yyyyMMdd.log
```

NETLOAD 后 AutoCAD 命令行会打印当前日志路径。示例：

```
2026-09-08 14:32:07.123  tools/call  draw_line        ok      38ms  {"x1":0,"y1":0,"x2":5000,"y2":3000}
2026-09-08 14:32:09.887  tools/call  run_command      ok       5ms  {"command":"_.PURGE ..."}  DANGER
2026-09-08 14:32:11.005  tools/call  save_as          ERR     21ms  {"path":"x"}  -> eInvalidDwgVersion
2026-09-08 14:32:20.114  http        ERR 拒绝非本机 Origin：https://evil.example.com（1.2.3.4:5678）
```

也可以让模型调 `get_log` 工具自查。

## 6. 验证

### 6.1 脱离 AutoCAD 的协议测试

```bash
dotnet run --project test/AcadMcp.ProtocolTest -c Release
```

用假工具验证内嵌 HTTP + MCP JSON-RPC 传输符合 MCP Streamable HTTP 规范
（initialize / tools.list / tools.call / 图片块 / 错误码 + hint / UTF-8 / Origin 校验 / 协议版本头 / 日志）。
应输出 `37 passed, 0 failed`。

### 6.2 连着 AutoCAD 的端到端测试

NETLOAD 插件后，另开一个终端：

```bash
pwsh scripts/test-mcp.ps1
```

脚本按阶段跑一遍：P0 绘制 / 查询 / 删除 → P1 截图 + `save_as` → `eval_lisp`（若已开）→
P2 绘制补全 / 修改 / 标注 / 填充 / 选择集 / 测量 → P2.5 `mark`+`rollback` →
**P3** 系统变量与单位 → 布局 + 视口 → `plot_pdf` → 外部参照 附着/重载/拆离 →
多文档 新建/关闭 → 只截绘图区的截图 → 清理（删掉测试布局）。

产物：`scripts/capture.png`、`capture-p2.png`、`capture-p3.png`，`%TEMP%` 下的测试 dwg 与 pdf。
在 AutoCAD 中肉眼确认图元出现 / 消失、布局与视口正确。

> 脚本必须保持**纯 ASCII**：Windows PowerShell 5.1 按系统 ANSI 读 .ps1，中文会让它解析崩溃。

---

## 已知限制

- 一次只对接**当前活动文档**（`activate_document` 显式切换）；多 AutoCAD 实例仍未隔离（一个端口一个实例）
- 只读模式 / 会话前备份 / `mark`+`rollback` / token 鉴权已有（P2.5），但**没有交互式审批 UI**——
  危险操作靠"默认关 + 命令开关 + 日志"，不是逐次弹窗确认
- `run_command` / `save` / `undo` / `rollback` 及 trim/fillet 等走 AutoCAD 命令队列，是**异步**的：
  命令只在 AutoCAD 处理消息时才执行，**窗口在后台且无人操作时可能拖几秒甚至更久**
  （现象：鼠标移到 AutoCAD 上，积压的命令立刻开始跑）。要确认结果就轮询 `query_entities` / `get_status`，
  别用固定 sleep
- `capture_view` 默认抓整个主窗口，`region:"drawing"` 只抓绘图区；被完全遮挡时个别显卡驱动可能截到黑图。
  截图分辨率受 AutoCAD 自身 DPI 感知能力限制（老版本在高 DPI 屏上由系统拉伸位图，返回文本会提示）
- `Application.Idle` 在 AutoCAD 有模态对话框或长时间无响应时不触发，工具会在 30s 后超时
- `insert_block` 只能插入**当前图形中已定义**的图块，不从外部 dwg 导入
- 坐标 / 尺寸参数始终是裸图形单位；`convert_length` 只做数值换算，不会替你改坐标
- `plot_pdf` 依赖 `DWG To PDF.pc3` 设备；打印期间会临时把 `BACKGROUNDPLOT` 置 0（结束后恢复）
- **`plot_pdf` 只支持 `area=layout` / `extents`**。`window` / `display` / `limits` 在 AutoCAD 2014 的
  PlotEngine 上会正确排版却不渲染图形（出白纸），已实测排除多种写法（调换窗口与范围类型的设置顺序、
  图纸单位提前、`MatchEnabled`→`MatchDisabled`、改走 `Display`+临时视图、改走 `Limits`、
  把设置写进布局而非 `OverrideSettings`），因此直接禁用并在错误信息里给出替代方案。
  AutoCAD 自带 PLOT 对话框用同一套设置预览是正确的，所以这是引擎限制而非参数问题。
  另：`display` 的打印区域按当前视口宽高比算（绘图区被命令行挤扁时只剩 285x89mm），本来也不可靠
- 首次 `plot_pdf` 要等打印引擎冷启动（实测 15~90 秒），之后每次约 0.2 秒
- `add_viewport` 创建视口时会先切到目标布局（视口的"打开"状态只在其所属布局为当前布局时可靠生效）
 
---

## 目录结构

```
AutoCADMCP
├─ ARCHITECTURE.md                 总体架构
├─ README.md                       本文件
├─ .mcp.json.example               Claude Code 配置样例
├─ AutoCadMcp.slnx
├─ scripts/test-mcp.ps1            端到端冒烟脚本
├─ src/AcadMcp.Plugin/             插件（net48, x64）
│  ├─ PluginEntry.cs               入口 + 命令 MCPSTART/MCPSTOP/MCPSTATUS
│  ├─ Mcp/                         HTTP + JSON-RPC + 工具注册 + 日志 + 安全网（Safety.cs）
│  ├─ Acad/                        AutoCAD 操作：绘制/修改/编辑/标注/填充/测量/查询/图层/图块/截图/视图/LISP
│  │                               Layouts / Plot / Xrefs / Docs / Sysvars / Units
│  └─ Tools/ToolCatalog.cs         工具的定义
└─ test/AcadMcp.ProtocolTest/      协议一致性测试（链接 Mcp/*.cs，不依赖 AutoCAD）
```
