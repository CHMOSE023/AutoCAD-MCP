using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Newtonsoft.Json.Linq;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using DbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 打印 / 出图。核心是 <see cref="ToPdf"/>：用 PlotEngine 把某个布局（或模型空间的一块区域）
    /// 打到 PDF 文件。页面设置（设备 / 纸张 / 方向）可单独用 <see cref="ApplyPageSetup"/> 固化到布局。
    /// </summary>
    internal static class Plot
    {
        public const string PdfDevice = "DWG To PDF.pc3";

        // ---------- 设备与纸张清单 ----------

        public static string ListDevices(string? device)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            {
                var psv = PlotSettingsValidator.Current;
                var devices = psv.GetPlotDeviceList().Cast<string>().ToList();

                var o = new JObject
                {
                    ["devices"] = new JArray(devices),
                    ["pdfDeviceAvailable"] = devices.Any(d => d.Equals(PdfDevice, StringComparison.OrdinalIgnoreCase)),
                };

                string probe = device ?? PdfDevice;
                if (devices.Any(d => d.Equals(probe, StringComparison.OrdinalIgnoreCase)))
                {
                    using (var ps = new PlotSettings(false))
                    {
                        psv.SetPlotConfigurationName(ps, probe, null);
                        psv.RefreshLists(ps);
                        var media = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
                        o["device"] = probe;
                        o["mediaCount"] = media.Count;
                        o["media"] = new JArray(media);
                        o["styleSheets"] = new JArray(psv.GetPlotStyleSheetList().Cast<string>());
                    }
                }
                else if (device != null)
                {
                    o["error"] = $"设备 '{device}' 不在可用列表里。";
                }

                o["note"] = "plot_pdf / create_layout 的 paperSize 可以传 media 里的完整名，也可以传 A4 / A3 这类简称做模糊匹配。";
                return o.ToString(Newtonsoft.Json.Formatting.Indented);
            }
        }

        // ---------- 页面设置 ----------

        /// <summary>把设备 / 纸张 / 方向写进布局本身的页面设置（持久化到 DWG）。</summary>
        public static string ApplyPageSetup(string layoutName, string? device, string? paperSize, bool landscape)
        {
            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var id = LayoutManager.Current.GetLayoutId(Layouts.ResolveName(db, layoutName));
                if (!id.IsValid) throw new ArgumentException($"布局 '{layoutName}' 不存在。");

                var layout = (Layout)tr.GetObject(id, OpenMode.ForWrite);
                var psv = PlotSettingsValidator.Current;

                using (var ps = new PlotSettings(layout.ModelType))
                {
                    ps.CopyFrom(layout);

                    string dev = device ?? (string.IsNullOrWhiteSpace(layout.PlotConfigurationName) ||
                                            layout.PlotConfigurationName == "None"
                        ? PdfDevice
                        : layout.PlotConfigurationName);

                    EnsureDevice(psv, dev);
                    psv.SetPlotConfigurationName(ps, dev, null);
                    psv.RefreshLists(ps);

                    string media;
                    bool byRotation = false;
                    if (paperSize != null)
                    {
                        var picked = MatchMedia(psv, ps, paperSize, landscape);
                        media = picked.Name;
                        // 用户给了完整纸张名就尊重它，方向只能靠旋转；简称则已经选中了对应方向的纸张
                        byRotation = picked.Exact && landscape;
                    }
                    else
                    {
                        media = ps.CanonicalMediaName;
                        byRotation = landscape;
                    }
                    psv.SetCanonicalMediaName(ps, media);

                    psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
                    psv.SetPlotRotation(ps, byRotation ? PlotRotation.Degrees090 : PlotRotation.Degrees000);

                    layout.CopyFrom(ps);
                    tr.Commit();

                    return $"页面设置：设备={dev}，纸张={media}，方向={(landscape ? "横向" : "纵向")}。";
                }
            }
        }

        // ---------- 打印到 PDF ----------

        /// <summary>一次打印请求。HTTP 线程填参数并等待，MCPPLOT 命令在文档上下文里执行并回填结果。</summary>
        private sealed class Request
        {
            public string? Layout, Output, PaperSize, Area;
            public bool? Landscape;
            public double[]? Window;
            public double? Scale;
            public bool Monochrome;

            public string? Result, Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static Request? _pending;
        private static readonly object Gate = new object();

        /// <summary>
        /// 打印到 PDF。**在 HTTP 线程调用**（不要包 MainThread.Invoke）：
        /// PlotEngine 必须在文档上下文运行，从 <c>Application.Idle</c>（应用上下文）调用会抛 eInvalidInput，
        /// 而 AutoCAD 2014 没有 <c>ExecuteInCommandContextAsync</c>。因此把实际打印交给 MCPPLOT 命令，
        /// 用 <c>SendStringToExecute</c> 送进命令队列，本方法阻塞等结果 —— 与 trim/fillet 的做法一致。
        /// </summary>
        public static string ToPdf(string? layoutName, string? output, string? paperSize, bool? landscape,
            string area, double[]? window, double? scale, bool monochrome, int timeoutMs = 180000)
        {
            area = (area ?? "layout").Trim().ToLowerInvariant();

            // window / display / limits 在 AutoCAD 2014 的 PlotEngine 上打不出内容 —— 见 UnsupportedArea。
            if (UnsupportedArea(area, out string why))
                throw new ArgumentException(why);

            var req = new Request
            {
                Layout = layoutName, Output = output, PaperSize = paperSize, Landscape = landscape,
                Area = area, Window = window, Scale = scale, Monochrome = monochrome,
            };

            lock (Gate)
            {
                if (_pending != null)
                    throw new InvalidOperationException("已有一个打印任务在进行中，等它结束再试。");
                _pending = req;
            }

            string previousLayout = "";
            bool switchedLayout = false;

            try
            {
                // MdiActiveDocument 在 HTTP 线程为 null，去主线程取引用；activate=false（后台线程用 true 会抛 eInvalidInput）
                var doc = MainThread.Invoke(() => AcadContext.ActiveDocument);

                // PlotEngine 只可靠地打印当前布局，所以要先切过去。
                // 切换必须在这里（应用上下文）做 —— 放进 MCPPLOT 命令里会抛 eDocumentSwitchDisabled：
                // 命令执行期间 AutoCAD 禁止切换布局 / 文档。
                string target = MainThread.Invoke(() => layoutName != null
                    ? Layouts.ResolveName(doc.Database, layoutName)
                    : LayoutManager.Current.CurrentLayout);
                req.Layout = target;   // 传规范化后的名字，命令里不用再解析

                previousLayout = MainThread.Invoke(() => LayoutManager.Current.CurrentLayout);
                if (!string.Equals(previousLayout, target, StringComparison.OrdinalIgnoreCase))
                {
                    MainThread.Invoke(() =>
                    {
                        using (doc.LockDocument()) LayoutManager.Current.CurrentLayout = target;
                    });
                    switchedLayout = true;
                }

                Mcp.Log.Info("plot", $"MCPPLOT layout={target} area={req.Area}");
                doc.SendStringToExecute("MCPPLOT\n", false, false, false);

                if (!req.Done.Wait(timeoutMs))
                    throw new TimeoutException(
                        $"打印命令在 {timeoutMs / 1000} 秒内没有完成。AutoCAD 可能正忙、有模态对话框，" +
                        "或打印机驱动弹了窗 —— 切到 AutoCAD 看一眼。");

                if (req.Error != null) throw new InvalidOperationException(req.Error);
                return req.Result ?? "打印已结束，但没有返回信息。";
            }
            finally
            {
                // 切回原布局，同样在应用上下文；失败只记日志，不掩盖打印本身的结果
                if (switchedLayout)
                {
                    try
                    {
                        MainThread.Invoke(() =>
                        {
                            var d = AcadContext.ActiveDocument;
                            using (d.LockDocument()) LayoutManager.Current.CurrentLayout = previousLayout;
                        });
                    }
                    catch (System.Exception ex)
                    {
                        Mcp.Log.Error("plot", $"切回布局 {previousLayout} 失败：{ex.Message}");
                    }
                }
                lock (Gate) _pending = null;
            }
        }

        /// <summary>由 MCPPLOT 命令调用（文档上下文）。没有待处理请求时什么都不做。</summary>
        internal static void RunPending()
        {
            var req = _pending;
            if (req == null) return;

            try { req.Result = Execute(req); }
            catch (System.Exception ex) { req.Error = ex.Message; }
            finally { req.Done.Set(); }
        }

        private static string Execute(Request req)
        {
            string? layoutName = req.Layout, output = req.Output, paperSize = req.PaperSize;
            bool? landscape = req.Landscape;
            string area = req.Area!;
            double[]? window = req.Window;
            double? scale = req.Scale;
            bool monochrome = req.Monochrome;

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException(
                    "AutoCAD 正在打印中（或上一次打印未结束），稍后重试。");

            var doc = AcadContext.ActiveDocument;
            var db = doc.Database;

            string outPath = ResolveOutput(doc, output, layoutName);

            // 后台打印会让 PlotEngine 立刻返回、文件还没写完，必须关掉
            short bgPlot = ToShort(AcApp.GetSystemVariable("BACKGROUNDPLOT"));

            var lm = LayoutManager.Current;
            // 目标布局已由 ToPdf 在应用上下文里切好并规范化过名字。
            // 这里绝不能再切：命令执行期间切布局 = eDocumentSwitchDisabled，而且失败后布局会卡住，
            // 导致之后每一次打印都挂。
            string target = layoutName ?? lm.CurrentLayout;

            using (doc.LockDocument())
            {
                if (bgPlot != 0) AcApp.SetSystemVariable("BACKGROUNDPLOT", (short)0);
                try
                {
                    string usedMedia, usedScale, layoutUsed, diagInfo = "";
                    var notes = new List<string>();

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var layoutId = lm.GetLayoutId(target);
                        if (!layoutId.IsValid) throw new ArgumentException($"布局 '{target}' 不存在。");

                        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                        layoutUsed = layout.LayoutName;
                        bool isModel = layout.ModelType;

                        if (isModel && area == "layout")
                            area = "extents";   // 模型空间没有"图纸范围"，退化为图形范围
                        if (area == "extents")
                            db.UpdateExt(false);   // EXTMIN/EXTMAX 可能是陈旧的

                        // 换打印设备后 ps.CanonicalMediaName 可能被清空，所以先从布局本身把纸张记下来
                        string layoutMedia = layout.CanonicalMediaName ?? "";

                        var psv = PlotSettingsValidator.Current;
                        using (var ps = new PlotSettings(isModel))
                        {
                        ps.CopyFrom(layout);

                        EnsureDevice(psv, PdfDevice);
                        Stage("设置打印设备", () =>
                        {
                            psv.SetPlotConfigurationName(ps, PdfDevice, null);
                            psv.RefreshLists(ps);
                        });

                        bool rotateForLandscape = false;
                        if (paperSize != null)
                        {
                            var picked = MatchMedia(psv, ps, paperSize, landscape);
                            usedMedia = picked.Name;
                            rotateForLandscape = picked.Exact && (landscape ?? false);
                        }
                        else
                        {
                            // 没指定纸张：沿用布局页面设置里的那张（打 A3 布局就该出 A3），
                            // 优先用从 layout 直接读到的名字 —— ps 上的那个换设备后可能已被清空。
                            string keep = !string.IsNullOrEmpty(layoutMedia) ? layoutMedia : ps.CanonicalMediaName;
                            usedMedia = !string.IsNullOrEmpty(keep) && MediaAvailable(psv, ps, keep)
                                ? keep
                                : MatchMedia(psv, ps, "A4", landscape).Name;
                            rotateForLandscape = landscape ?? false;
                        }
                        string media = usedMedia;
                        Stage("设置纸张", () => psv.SetCanonicalMediaName(ps, media));

                        string a2 = area;

                        // 图纸单位必须先定：之后再改单位会把已设好的窗口区域按 25.4 换算掉，
                        // 结果是"打印成功但纸上几乎空白"。
                        SoftStage("图纸单位(mm)", () => psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters), notes);

                        Stage("设置打印范围类型", () => psv.SetPlotType(ps, ParseArea(a2)));

                        // 可选项：个别范围类型 / 设备组合不接受，失败不该让整张图打不出来
                        if (a2 != "layout")   // 按布局打印是按图纸原点对齐的，居中无意义
                            SoftStage("居中", () => psv.SetPlotCentered(ps, true), notes);

                        if (scale.HasValue)
                        {
                            if (scale.Value <= 0) throw new ArgumentException("scale 必须大于 0（1:scale）。");
                            double sc = scale.Value;
                            Stage("设置打印比例", () =>
                            {
                                psv.SetUseStandardScale(ps, false);
                                psv.SetCustomPrintScale(ps, new CustomScale(1.0, sc));   // 1 图纸毫米 : sc 图形单位
                            });
                            usedScale = $"1:{sc:0.###}";
                        }
                        else
                        {
                            Stage("设置打印比例", () =>
                            {
                                psv.SetUseStandardScale(ps, true);
                                psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                            });
                            usedScale = "布满图纸";
                        }

                        // 方向优先靠纸张本身（见 MatchMedia）；只有用完整纸张名时才需要真的旋转
                        bool rot = rotateForLandscape;
                        SoftStage("纸张方向", () =>
                            psv.SetPlotRotation(ps, rot ? PlotRotation.Degrees090 : PlotRotation.Degrees000), notes);

                        if (monochrome)
                        {
                            var sheets = psv.GetPlotStyleSheetList().Cast<string>().ToList();
                            var mono = sheets.FirstOrDefault(s => s.StartsWith("monochrome", StringComparison.OrdinalIgnoreCase));
                            if (mono != null)
                            {
                                psv.SetCurrentStyleSheet(ps, mono);
                            }
                        }

                        string before = Snapshot(ps);

                        var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };

                        // MatchDisabled：纸张我们已经自己选定并校验过了，不需要验证器再去"匹配"一遍 ——
                        // MatchEnabled 会按它自己的规则重算，可能把窗口范围一并改掉。
                        Stage("校验打印配置", () =>
                            new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchDisabled }.Validate(pi));

                        string after = Snapshot(ps);
                        diagInfo = before + (after == before ? "（校验未改动）" : "\n校验后：" + after);

                        // PlotEngine 用完必须 Destroy()：只 Dispose 会把引擎实例留在"已占用"状态，
                        // 下一次打印就抛 eInvalidInput（第一次成功、之后全失败就是这个原因）。
                        var pe = PlotFactory.CreatePublishEngine();
                        try
                        {
                            Stage("BeginPlot", () => pe.BeginPlot(null, null));
                            Stage("BeginDocument", () => pe.BeginDocument(pi, doc.Name, null, 1, true, outPath));
                            Stage("BeginPage", () => pe.BeginPage(new PlotPageInfo(), pi, true, null));
                            Stage("生成图形", () => { pe.BeginGenerateGraphics(null); pe.EndGenerateGraphics(null); });
                            Stage("EndPage", () => pe.EndPage(null));
                            Stage("EndDocument", () => pe.EndDocument(null));
                            Stage("EndPlot", () => pe.EndPlot(null));
                        }
                        finally
                        {
                            try { pe.Destroy(); } catch { }
                            try { pe.Dispose(); } catch { }
                        }
                        }

                        tr.Commit();
                    }

                    var fi = new FileInfo(outPath);
                    if (!fi.Exists)
                        throw new InvalidOperationException(
                            $"打印结束但没有生成文件：{outPath}。检查目录是否可写，或该布局是否为空。");

                    return $"已打印到 PDF：{outPath}（{fi.Length / 1024.0:0.#} KB）\n" +
                           $"布局={layoutUsed}，纸张={usedMedia}，范围={area}，比例={usedScale}" +
                           (monochrome ? "，单色" : "") + "。" +
                           (notes.Count > 0
                               ? "\n（这些设置该组合不支持，已跳过：" + string.Join("、", notes) + "）"
                               : "") +
                           "\n生效设置：" + diagInfo;
                }
                finally
                {
                    // 布局与视图的切换 / 恢复都在 ToPdf 里做（应用上下文），这里只还原系统变量
                    if (bgPlot != 0)
                        try { AcApp.SetSystemVariable("BACKGROUNDPLOT", bgPlot); } catch { }
                }
            }
        }

        // ---------- 辅助 ----------

        /// <summary>
        /// 给打印的每一步加阶段名。AutoCAD 抛的 eInvalidInput 之类只有错误码、不说是哪一步，
        /// 打印又是十几步链式调用，没有阶段名基本没法排查。
        /// </summary>
        private static void Stage(string name, Action action)
        {
            try { action(); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new InvalidOperationException($"打印阶段【{name}】失败：{ex.ErrorStatus}");
            }
        }

        /// <summary>可选设置：某些"范围类型 × 设备"组合不接受，跳过也能出图，只在结果里说明一句。</summary>
        private static void SoftStage(string name, Action action, List<string> notes)
        {
            try { action(); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex) { notes.Add($"{name}({ex.ErrorStatus})"); }
        }

        private static DbPlotType ParseArea(string area) => area switch
        {
            "layout" => DbPlotType.Layout,
            "extents" => DbPlotType.Extents,
            "display" => DbPlotType.Display,
            "window" => DbPlotType.Window,
            "limits" => DbPlotType.Limits,
            _ => throw new ArgumentException($"area 只能是 layout / extents / display / window / limits，收到 '{area}'。"),
        };

        private static void EnsureDevice(PlotSettingsValidator psv, string device)
        {
            var devices = psv.GetPlotDeviceList().Cast<string>().ToList();
            if (!devices.Any(d => d.Equals(device, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"打印设备 '{device}' 不可用。可用设备：{string.Join(" / ", devices.Take(15))}" +
                    (devices.Count > 15 ? " …" : "") + "。用 list_plot_devices 查看完整列表。");
        }

        /// <summary>纸张匹配的结果：选中的名字，以及它是不是用户给的完整 canonical 名。</summary>
        private readonly struct Media
        {
            public readonly string Name;
            public readonly bool Exact;
            public Media(string name, bool exact) { Name = name; Exact = exact; }
        }

        /// <summary>
        /// 纸张匹配：完整 canonical 名优先；否则按 A4 / A3 这类简称模糊找，
        /// 优先非 full_bleed 的公制（MM）纸张。
        ///
        /// landscape 在这里就参与选择 —— 纸张名自带方向（ISO_A3_(420x297) 是横向，(297x420) 是纵向），
        /// 横向应当靠**选横向纸张**实现，而不是再叠一次 PlotRotation：两者叠加会转回纵向。
        /// </summary>
        private static Media MatchMedia(PlotSettingsValidator psv, PlotSettings ps, string wanted, bool? landscape)
        {
            var media = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
            if (media.Count == 0)
                throw new InvalidOperationException("当前打印设备没有可用纸张列表。");

            var exact = media.FirstOrDefault(m => m.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return new Media(exact, true);

            string key = wanted.Trim().ToUpperInvariant().Replace(" ", "_");

            bool Hit(string m)
            {
                var u = m.ToUpperInvariant();
                return u.Contains("_" + key + "_") || u.EndsWith("_" + key) || u.StartsWith(key + "_") || u.Contains(key);
            }

            var hits = media.Where(Hit).ToList();
            if (hits.Count == 0)
                throw new ArgumentException(
                    $"找不到匹配 '{wanted}' 的纸张。用 list_plot_devices 查看该设备支持的纸张名。");

            var ordered = hits.OrderBy(m => m.ToUpperInvariant().Contains("FULL_BLEED") ? 1 : 0)
                              .ThenBy(m => m.ToUpperInvariant().Contains("MM") ? 0 : 1)
                              .ThenBy(m => m.Length)
                              .ToList();

            if (landscape.HasValue)
            {
                var want = ordered.FirstOrDefault(m => IsLandscape(m) == landscape.Value);
                if (want != null) return new Media(want, false);
            }

            return new Media(ordered[0], false);
        }

        /// <summary>
        /// 这三种打印范围在 AutoCAD 2014 的 PlotEngine 上出不来内容，直接拒绝并给出可行的替代做法。
        ///
        /// 实测（每次都用解压 PDF 内容流数绘图指令来判定，不看返回值）：
        ///   Extents / Layout —— 正常，约 187KB 内容流、57~78 个绘图指令；
        ///   Window / Display / Limits —— 页面版式算得对（不同范围给出不同的打印原点），
        ///     但内容流只有约 520 字节、0 个文字指令，也就是白纸。
        /// 换过的写法：调换 SetPlotWindowArea 与 SetPlotType 的顺序、把图纸单位提前、
        ///   MatchEnabled→MatchDisabled、校验后重设窗口、改用 Display + 临时视图、
        ///   改用 Limits（LIMMIN/LIMMAX）、把设置写进布局而非 OverrideSettings —— 均无效。
        /// 而 AutoCAD 自带的 PLOT 对话框用同一套设置预览是正确的，所以这是 PlotEngine 的限制，
        /// 不是参数问题。Display 另有一个硬伤：打印区域按当前视口宽高比算，绘图区被命令行挤扁时
        /// 只剩 285x89mm —— 出图结果取决于用户窗口拉多大，本来也不该用。
        /// </summary>
        private static bool UnsupportedArea(string area, out string why)
        {
            switch (area)
            {
                case "window":
                case "display":
                case "limits":
                    why = $"area={area} 在 AutoCAD 2014 上打不出内容（PlotEngine 会正确排版但不渲染图形，已实测排除多种写法）。\n" +
                          "替代：整张图用 area=extents；只出局部用图纸空间 —— create_layout 建布局，" +
                          "add_viewport 用 viewCenterX/Y 对准位置、scale 定比例、width/height 定窗口大小，再 plot_pdf 打这个布局。" +
                          "后者也是 AutoCAD 里控制出图范围的正规做法，且已验证可用。";
                    return true;
                default:
                    why = "";
                    return false;
            }
        }

        /// <summary>把打印设置的关键字段拍个快照，用来对比某一步之后哪个值被改掉了。</summary>
        private static string Snapshot(PlotSettings ps)
        {
            try
            {
                var wa = ps.PlotWindowArea;
                return $"窗口=({wa.MinPoint.X:0.#},{wa.MinPoint.Y:0.#})-({wa.MaxPoint.X:0.#},{wa.MaxPoint.Y:0.#})" +
                       $"，纸张={ps.PlotPaperSize.X:0.#}x{ps.PlotPaperSize.Y:0.#}mm" +
                       $"，单位={ps.PlotPaperUnits}，旋转={ps.PlotRotation}，类型={ps.PlotType}" +
                       $"，原点=({ps.PlotOrigin.X:0.#},{ps.PlotOrigin.Y:0.#})" +
                       $"，标准比例={ps.UseStandardScale}/{ps.StdScaleType}" +
                       $"，居中={ps.PlotCentered}";
            }
            catch (System.Exception ex) { return "读取失败：" + ex.Message; }
        }

        /// <summary>该纸张名在当前设备的可用列表里吗。</summary>
        private static bool MediaAvailable(PlotSettingsValidator psv, PlotSettings ps, string name)
            => psv.GetCanonicalMediaNameList(ps).Cast<string>()
                  .Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>从纸张名里的 "(420.00_x_297.00_MM)" 判断方向；解析不出来当作纵向。</summary>
        private static bool IsLandscape(string mediaName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                mediaName, @"\((\d+(?:\.\d+)?)_x_(\d+(?:\.\d+)?)_");
            if (!m.Success) return false;
            return double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out double w)
                && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out double h)
                && w > h;
        }

        private static string ResolveOutput(Autodesk.AutoCAD.ApplicationServices.Document doc,
            string? output, string? layoutName)
        {
            string path;
            if (!string.IsNullOrWhiteSpace(output))
            {
                path = output!.Trim();
                if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
                if (!Path.IsPathRooted(path))
                    throw new ArgumentException("output 必须是绝对路径，例如 D:\\work\\plan.pdf");
            }
            else
            {
                string baseName = Path.IsPathRooted(doc.Name)
                    ? Path.Combine(Path.GetDirectoryName(doc.Name)!, Path.GetFileNameWithoutExtension(doc.Name))
                    : Path.Combine(Path.GetTempPath(), "acadmcp-plot");
                string suffix = string.IsNullOrWhiteSpace(layoutName) ? "" : "-" + Sanitize(layoutName!);
                path = $"{baseName}{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return path;
        }

        private static string Sanitize(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        private static short ToShort(object v) => v is short s ? s : Convert.ToInt16(v);
    }
}
