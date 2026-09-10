using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 把工作项编组到 AutoCAD 主线程执行。
    /// HTTP 回调线程调用 <see cref="Invoke{T}"/>，实际操作在 <c>Application.Idle</c> 时于主线程出队执行，
    /// 回调线程用 <see cref="ManualResetEventSlim"/> 阻塞等待结果。
    /// 入队后会 <see cref="WakeUpMainThread"/> 主动唤醒消息泵，否则后台状态下延迟不可预测。
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int _installed;

        /// <summary>
        /// AutoCAD 主窗口句柄，供 HTTP 线程唤醒消息泵使用。
        /// 只在主线程上下文（插件初始化或 Idle 回调）读取并缓存：
        /// <c>PostMessage</c> 本身线程安全，但 <c>AcApp.MainWindow</c> 属 AutoCAD .NET API，
        /// 不应在非主线程访问。
        /// </summary>
        private static IntPtr _mainWindow;

        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 0)
            {
                AcApp.Idle += OnIdle;
                // 首次安装发生在插件初始化（主线程），此刻取句柄是安全的。
                // 若主窗口尚未创建，OnIdle 首次运行时会补取。
                TryCacheMainWindow();
            }
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            TryCacheMainWindow();

            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* action 内部已捕获并回传，这里兜底防止 Idle 抛出 */ }
            }
        }

        /// <summary>在主线程上下文尝试缓存主窗口句柄；已缓存或暂不可用时直接返回。</summary>
        private static void TryCacheMainWindow()
        {
            if (_mainWindow != IntPtr.Zero) return;
            try { _mainWindow = AcApp.MainWindow?.Handle ?? IntPtr.Zero; }
            catch { /* 主窗口尚不可用，留待下次补取 */ }
        }

        public static T Invoke<T>(Func<T> func, int timeoutMs = 30000)
        {
            EnsureInstalled();

            T result = default!;
            Exception? error = null;

            using (var done = new ManualResetEventSlim(false))
            {
                Queue.Enqueue(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });

                WakeUpMainThread();

                if (!done.Wait(timeoutMs))
                    throw new TimeoutException(
                        $"AutoCAD 主线程在 {timeoutMs}ms 内未处理该操作（AutoCAD 可能正忙或存在模态对话框）。");
            }

            if (error != null)
                throw new AcadOpException(error.Message, error);

            return result;
        }

        public static void Invoke(Action action, int timeoutMs = 30000)
            => Invoke<object?>(() => { action(); return null; }, timeoutMs);

        /// <summary>
        /// 唤醒主线程的消息泵。
        ///
        /// <para><c>Application.Idle</c> 只在消息队列变空的那一刻触发一次，随后主线程阻塞在
        /// <c>WaitMessage()</c>。若只入队而不唤醒，工作项要等下一条消息自然到达
        /// （鼠标移动、定时器、重绘……）才会被处理：AutoCAD 在前台时消息流密集，延迟可忽略；
        /// <b>在后台且无输入时延迟不可预测，实测首次调用可达 4.2 秒</b>。</para>
        ///
        /// <para><c>WM_NULL</c> 是空消息，接收方不做任何处理，唯一作用是让 <c>WaitMessage</c> 返回，
        /// 使消息循环走完一轮后重新触发 <c>Idle</c>，从而立即出队。</para>
        ///
        /// <para>注意：这解决不了模态对话框场景——模态循环不触发 <c>Application.Idle</c>，
        /// 那种情况仍由 <see cref="Invoke{T}"/> 的超时兜底。</para>
        /// </summary>
        private static void WakeUpMainThread()
        {
            var hwnd = _mainWindow;
            if (hwnd != IntPtr.Zero)
                PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>空消息，仅用于打破消息循环的等待，不产生任何副作用。</summary>
        private const uint WM_NULL = 0x0000;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    /// <summary>主线程操作抛出的异常，Message 已是面向用户的中文说明。</summary>
    internal sealed class AcadOpException : Exception
    {
        public AcadOpException(string message, Exception inner) : base(message, inner) { }
    }
}
