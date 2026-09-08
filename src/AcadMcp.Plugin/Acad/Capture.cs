using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 截取 AutoCAD 主窗口为 PNG。用 PrintWindow(PW_RENDERFULLCONTENT)：
    /// 即使窗口被遮挡也能让它自行绘制，比 CopyFromScreen 稳。
    /// </summary>
    internal static class Capture
    {
        public static byte[] MainWindowPng(int? maxWidth)
        {
            IntPtr hwnd = AcApp.MainWindow?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                throw new InvalidOperationException("拿不到 AutoCAD 主窗口句柄。");

            if (!GetClientRect(hwnd, out RECT rc) || rc.Width <= 0 || rc.Height <= 0)
                throw new InvalidOperationException("AutoCAD 窗口尺寸无效（可能已最小化）。");

            int w = rc.Width, h = rc.Height;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        // 2 = PW_RENDERFULLCONTENT（Win8.1+，能抓 DX/GL 绘制的视口）
                        bool ok = PrintWindow(hwnd, hdc, 2);
                        if (!ok) PrintWindow(hwnd, hdc, 0);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                Image outImg = bmp;
                Bitmap? scaled = null;
                if (maxWidth is int mw && mw > 0 && w > mw)
                {
                    int nh = (int)Math.Round(h * (mw / (double)w));
                    scaled = new Bitmap(bmp, new Size(mw, nh));
                    outImg = scaled;
                }

                try
                {
                    using (var ms = new MemoryStream())
                    {
                        outImg.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
                finally
                {
                    scaled?.Dispose();
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
    }
}
