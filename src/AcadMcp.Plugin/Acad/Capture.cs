using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 截取 AutoCAD 窗口为 PNG。用 PrintWindow(PW_RENDERFULLCONTENT)：
    /// 即使窗口被遮挡也能让它自行绘制，比 CopyFromScreen 稳。
    ///
    /// region=drawing 时只截绘图区子窗口（去掉功能区 / 命令行 / 状态栏），图更干净、token 更省。
    /// 截图分辨率受 AutoCAD 自身的 DPI 感知能力限制：老版本（如 2014）在高 DPI 屏上由系统做位图拉伸，
    /// 此时 Info 里会给出提示，可用 maxWidth 控制输出尺寸。
    /// </summary>
    internal static class Capture
    {
        public readonly struct Shot
        {
            public readonly byte[] Png;
            public readonly string Info;
            public Shot(byte[] png, string info) { Png = png; Info = info; }
        }

        public static Shot Take(int? maxWidth, string? region)
        {
            IntPtr main = AcApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (main == IntPtr.Zero || !IsWindow(main))
                throw new InvalidOperationException("拿不到 AutoCAD 主窗口句柄。");

            if (IsIconic(main))
                throw new InvalidOperationException("AutoCAD 窗口已最小化，无法截图。先还原窗口。");

            bool wantDrawing = string.Equals((region ?? "window").Trim(), "drawing", StringComparison.OrdinalIgnoreCase);

            IntPtr hwnd = main;
            string regionLabel = "整个窗口";
            if (wantDrawing)
            {
                var view = FindDrawingArea(main);
                if (view != IntPtr.Zero) { hwnd = view; regionLabel = "绘图区"; }
                else regionLabel = "整个窗口（未找到绘图区子窗口，已回退）";
            }

            if (!GetClientRect(hwnd, out RECT rc) || rc.Width <= 0 || rc.Height <= 0)
                throw new InvalidOperationException("窗口尺寸无效（可能已最小化或正在布局）。");

            int w = rc.Width, h = rc.Height;
            var dpi = WindowDpi(hwnd);

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // 2 = PW_RENDERFULLCONTENT（Win8.1+，能抓 DX/GL 绘制的视口）
                        if (!PrintWindow(hwnd, hdc, 2)) PrintWindow(hwnd, hdc, 0);
                    }
                    finally { g.ReleaseHdc(hdc); }
                }

                byte[] png;
                int outW = w, outH = h;

                if (maxWidth is int mw && mw > 0 && w > mw)
                {
                    outW = mw;
                    outH = (int)Math.Round(h * (mw / (double)w));
                    using (var scaled = new Bitmap(outW, outH, PixelFormat.Format32bppArgb))
                    {
                        using (var g2 = Graphics.FromImage(scaled))
                        {
                            // 线图缩小时默认插值会糊掉细线，用高质量双三次
                            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g2.SmoothingMode = SmoothingMode.HighQuality;
                            g2.DrawImage(bmp, new Rectangle(0, 0, outW, outH));
                        }
                        png = ToPng(scaled);
                    }
                }
                else
                {
                    png = ToPng(bmp);
                }

                var info = new StringBuilder();
                info.Append($"截图：{regionLabel}，源 {w}x{h}px");
                if (outW != w) info.Append($"，已缩放到 {outW}x{outH}px");
                info.Append($"，{png.Length / 1024.0:0.#} KB");
                if (dpi > 0)
                {
                    info.Append($"，窗口 DPI {dpi}（{dpi / 96.0 * 100:0}% 缩放）");
                    if (dpi > 96 && !ProcessIsDpiAware())
                        info.Append("；AutoCAD 未声明 DPI 感知，系统会拉伸位图，细节受限");
                }
                return new Shot(png, info.ToString());
            }
        }

        private static byte[] ToPng(Image img)
        {
            using (var ms = new MemoryStream())
            {
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 找绘图区窗口。图形窗口的类名各版本不同（2014 是动态的 <c>Afx:&lt;基址&gt;:28:…</c>，
        /// 新版是 <c>AfxFrameOrView*</c>），所以不按类名匹配，而是取 MDIClient 里面积最大的可见窗口 ——
        /// 那就是当前图形的 MDI 子窗口。
        /// </summary>
        private static IntPtr FindDrawingArea(IntPtr parent)
        {
            var buf = new StringBuilder(256);

            IntPtr mdi = IntPtr.Zero;
            EnumWindowsProc findMdi = (child, _) =>
            {
                buf.Length = 0;
                GetClassName(child, buf, buf.Capacity);
                if (buf.ToString().Equals("MDIClient", StringComparison.OrdinalIgnoreCase))
                {
                    mdi = child;
                    return false;   // 找到就停
                }
                return true;
            };
            EnumChildWindows(parent, findMdi, IntPtr.Zero);
            GC.KeepAlive(findMdi);

            if (mdi == IntPtr.Zero) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindowsProc pick = (child, _) =>
            {
                if (IsWindowVisible(child)
                    && GetClientRect(child, out RECT r) && r.Width > 200 && r.Height > 200)
                {
                    long area = (long)r.Width * r.Height;
                    if (area > bestArea) { bestArea = area; best = child; }
                }
                return true;
            };
            EnumChildWindows(mdi, pick, IntPtr.Zero);
            GC.KeepAlive(pick);

            // MDI 子窗口顶部还带着一条工具栏；真正的图形视图是它里面那个几乎同样大的子窗口，
            // 再下探一层把工具栏去掉（找不到就用外层，无非多一条工具栏）。
            if (best != IntPtr.Zero && GetClientRect(best, out RECT outer))
            {
                long outerArea = (long)outer.Width * outer.Height;
                IntPtr inner = IntPtr.Zero;
                long innerArea = 0;
                EnumWindowsProc dig = (child, _) =>
                {
                    if (IsWindowVisible(child)
                        && GetClientRect(child, out RECT r) && r.Width > 200 && r.Height > 200)
                    {
                        long area = (long)r.Width * r.Height;
                        if (area > innerArea && area >= outerArea * 0.7) { innerArea = area; inner = child; }
                    }
                    return true;
                };
                EnumChildWindows(best, dig, IntPtr.Zero);
                GC.KeepAlive(dig);
                if (inner != IntPtr.Zero) best = inner;
            }

            return best;
        }

        private static int WindowDpi(IntPtr hwnd)
        {
            try { return (int)GetDpiForWindow(hwnd); }
            catch (EntryPointNotFoundException) { return 0; }   // Win10 1607 之前没有这个 API
            catch (DllNotFoundException) { return 0; }
        }

        private static bool ProcessIsDpiAware()
        {
            try { return IsProcessDPIAware(); }
            catch { return true; }
        }

        // ---------- P/Invoke ----------

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
    }
}
