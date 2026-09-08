# AutoCAD MCP 插件

让 **Claude Code**（或任何 MCP 客户端）通过 MCP 协议操作 AutoCAD。**53 个工具**，AutoCAD 2014 实测通过。

一个 .NET AutoCAD 插件（NETLOAD 进 AutoCAD），插件内嵌一个 HTTP 服务，按 **MCP
Streamable HTTP** 规范对外暴露工具。Claude Code 以 `type: http` 直连
`http://127.0.0.1:7130/mcp`。没有独立进程、没有 socket 桥接。

```
Claude Code ──HTTP /mcp──▶ AcadMcp.Plugin.dll (NETLOAD 进 AutoCAD)
                             ├─ HttpListener 127.0.0.1:7130 + 手写 MCP JSON-RPC
                             ├─ 53 个工具
                             ├─ 主线程调度 (Application.Idle 队列)
                             ├─ 命令队列桥 (SendStringToExecute + 结果文件轮询)
                             ├─ 安全网（只读模式 / 备份 / mark-rollback / token）
                             └─ ObjectARX .NET API / acedEvaluateLisp → DWG
```

详见 [ARCHITECTURE.md](ARCHITECTURE.md)（总体架构与后续演进）。

---

## 环境要求

| 项 | 要求 |
|---|---|
| AutoCAD | 2020（其它版本改 `AcadDir` 后重编，见下） |
| .NET Framework | 4.8（Windows 11 自带） |
| 构建 | .NET SDK 8+（本仓库用 10.0.400 验证），**无需 Visual Studio** |
| Claude Code | 支持 `--transport http` 的版本 |

---

## 1. 构建

```bash
dotnet build src/AcadMcp.Plugin/AcadMcp.Plugin.csproj -c Release
```

产物在 `src/AcadMcp.Plugin/bin/Release/`：

- `AcadMcp.Plugin.dll` — 插件本体
- `Newtonsoft.Json.dll` — 唯一运行时依赖（需与插件放在同一目录）

**换 AutoCAD 版本**：编译时传参，例如 2018：

```bash
dotnet build src/AcadMcp.Plugin/AcadMcp.Plugin.csproj -c Release -p:AcadDir="C:\Program Files\Autodesk\AutoCAD 2018\"
```

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

## 4. 工具清单（53 个）

**P0 · 基础绘图闭环**

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

**P1 · 视觉反馈 + 修改 + 图块**

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
| `save_as` | 文档 | 另存为指定绝对路径 .dwg（可存未命名新图） |
| `undo` | 修改 | 撤销最近操作（等价命令行 U），可指定步数 |
| `get_log` | 只读 | 返回今天日志最后 N 行（自查刚才发生了什么） |
| `eval_lisp` | 逃生舱 | 【危险 · 任意代码执行】同步执行 AutoLISP 表达式并返回其值。**默认关闭**，见下 |

**P2 · 工具广度补全**

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
- `get_status` 返回 `readOnly` / `authRequired` / `sessionBackedUp` / `marks`

修改类工具（`move` `copy` `rotate` `scale` `mirror` `explode` `erase_entity` `hatch`）都支持
`useSelection: true` —— 用 `select` 的结果代替显式 handle。

`trim` / `extend` / `fillet` / `chamfer` 内部走 **AutoCAD 命令队列**（`SendStringToExecute` +
轮询结果文件），因为这些交互命令需要文档上下文；比其它工具慢几百毫秒。不受 `eval_lisp` 开关影响。
`break_entity` / `join` 是 .NET 原生。

坐标与尺寸单位＝当前图形单位（不做单位换算）。写操作立即修改图纸。
`capture_view` 用 `PrintWindow` 抓主窗口（含菜单栏），P2 可换成仅绘图区的离屏渲染。

### `eval_lisp` —— 任意代码执行，默认关闭

`eval_lisp` 让模型直接写并执行 AutoLISP（能读返回值、`(defun ...)` 定义函数后复用、`vlax-*`
操作对象），把工具集从固定 26 个变成可自扩展。但 AutoLISP 能删文件 / 起进程 / 调 COM，
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
应输出 `34 passed, 0 failed`。

### 6.2 连着 AutoCAD 的端到端测试

NETLOAD 插件后，另开一个终端：

```bash
pwsh scripts/test-mcp.ps1
```

依次调用 `initialize → tools/list → get_status → create_layer → draw_line →
query_entities → zoom_extents → erase_entity`，并在 AutoCAD 中肉眼确认图元出现 / 消失。

---

## 已知限制

- 只对接**当前活动文档**；多文档 / 多 AutoCAD 实例未隔离（P3）
- 只读模式 / 会话前备份 / `mark`+`rollback` / token 鉴权已有（P2.5），但**没有交互式审批 UI**——
  危险操作靠"默认关 + 命令开关 + 日志"，不是逐次弹窗确认
- `run_command` / `save` / `undo` 及 trim/fillet 等走 AutoCAD 命令队列，是**异步 / 有延迟**的
- `capture_view` 抓整个主窗口（含工具栏）；被完全遮挡时个别显卡驱动可能截到黑图
- `Application.Idle` 在 AutoCAD 有模态对话框或长时间无响应时不触发，工具会在 30s 后超时
- `insert_block` 只能插入**当前图形中已定义**的图块，不从外部 dwg 导入
- 无单位换算：坐标 / 尺寸都是裸图形单位（P3）

后续路线见 [ARCHITECTURE.md](ARCHITECTURE.md) 第十二节 / 附录 C。

---

## 目录结构

```
D:\AutoCADMCP\
├─ ARCHITECTURE.md                 总体架构
├─ README.md                       本文件
├─ .mcp.json.example               Claude Code 配置样例
├─ AutoCadMcp.slnx
├─ scripts/test-mcp.ps1            端到端冒烟脚本
├─ src/AcadMcp.Plugin/             插件（net48, x64）
│  ├─ PluginEntry.cs               入口 + 命令 MCPSTART/MCPSTOP/MCPSTATUS
│  ├─ Mcp/                         HTTP + JSON-RPC + 工具注册 + 日志 + 安全网（Safety.cs）
│  ├─ Acad/                        AutoCAD 操作（绘制/修改/编辑/标注/填充/测量/查询/图层/图块/截图/视图/LISP）
│  └─ Tools/ToolCatalog.cs         53 个工具的定义
└─ test/AcadMcp.ProtocolTest/      协议一致性测试（链接 Mcp/*.cs，不依赖 AutoCAD）
```
