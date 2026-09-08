using System;
using System.Collections.Concurrent;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Acad
{
    /// <summary>
    /// 把工作项编组到 AutoCAD 主线程执行。
    /// HTTP 回调线程调用 <see cref="Invoke{T}"/>，实际操作在 <c>Application.Idle</c> 时于主线程出队执行，
    /// 回调线程用 <see cref="ManualResetEventSlim"/> 阻塞等待结果。
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static int _installed;

        public static void EnsureInstalled()
        {
            if (Interlocked.Exchange(ref _installed, 1) == 0)
                AcApp.Idle += OnIdle;
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            while (Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch { /* action 内部已捕获并回传，这里兜底防止 Idle 抛出 */ }
            }
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
    }

    /// <summary>主线程操作抛出的异常，Message 已是面向用户的中文说明。</summary>
    internal sealed class AcadOpException : Exception
    {
        public AcadOpException(string message, Exception inner) : base(message, inner) { }
    }
}
