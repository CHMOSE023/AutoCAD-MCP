using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AcadMcp.Mcp
{
    /// <summary>
    /// MCP Streamable HTTP 传输的最小实现（tools-only，无 SSE、无会话）。
    /// 单端点 /mcp：POST 收 JSON-RPC，恒定返回 application/json；GET/DELETE 返回 405。
    /// </summary>
    internal sealed class HttpServer : IDisposable
    {
        private readonly string _prefix;      // http://127.0.0.1:7130/
        private readonly string _path;        // /mcp
        private readonly McpDispatcher _dispatcher;

        private HttpListener? _listener;
        private Thread? _thread;
        private volatile bool _running;

        public HttpServer(string host, int port, string path, McpDispatcher dispatcher)
        {
            _prefix = $"http://{host}:{port}/";
            _path = path.StartsWith("/") ? path : "/" + path;
            _dispatcher = dispatcher;
        }

        public string Endpoint => _prefix.TrimEnd('/') + _path;
        public bool IsRunning => _running;

        public void Start()
        {
            if (_running) return;

            var listener = new HttpListener();
            listener.Prefixes.Add(_prefix);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException(
                    $"无法在 {_prefix} 监听：{ex.Message}\n" +
                    $"若为权限问题，用管理员命令行执行一次：\n" +
                    $"  netsh http add urlacl url={_prefix} user=%USERNAME%", ex);
            }

            _listener = listener;
            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "AcadMcp-Http" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (_running && _listener != null)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (Exception ex)
                {
                    if (_running) Log.Error("http", "accept 循环退出：" + ex.Message);
                    break; // listener 已停止
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception ex) { Log.Error("http", "请求处理异常：" + ex.Message); }
                });
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.Headers["Cache-Control"] = "no-store";

            // DNS rebinding 防护：非本机 Origin 一律拒绝
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
            {
                Log.Error("http", $"拒绝非本机 Origin：{origin}（{req.RemoteEndPoint}）");
                Write(res, 403, "text/plain", "forbidden origin");
                return;
            }

            // 路径校验
            string reqPath = req.Url?.AbsolutePath ?? "/";
            if (!reqPath.Equals(_path, StringComparison.Ordinal)
                && !reqPath.TrimEnd('/').Equals(_path.TrimEnd('/'), StringComparison.Ordinal))
            {
                Write(res, 404, "text/plain", "not found");
                return;
            }

            // 协议版本头（可缺省；缺省按 2025-03-26）
            var pv = req.Headers["MCP-Protocol-Version"];
            if (pv != null && pv != "2025-06-18" && pv != "2025-03-26" && pv != "2024-11-05")
            {
                Write(res, 400, "text/plain", "unsupported MCP-Protocol-Version: " + pv);
                return;
            }

            // token 鉴权（设置了 ACADMCP_TOKEN / token 文件时才校验；OPTIONS 放行）
            if (req.HttpMethod != "OPTIONS")
            {
                var token = Safety.Token;
                if (token != null)
                {
                    var auth = req.Headers["Authorization"];
                    if (auth == null || !auth.Equals("Bearer " + token, StringComparison.Ordinal))
                    {
                        Log.Error("http", $"鉴权失败（{req.RemoteEndPoint}）");
                        res.AddHeader("WWW-Authenticate", "Bearer");
                        Write(res, 401, "text/plain", "unauthorized");
                        return;
                    }
                }
            }

            switch (req.HttpMethod)
            {
                case "POST":
                    HandlePost(req, res);
                    return;

                case "GET":
                    res.AddHeader("Allow", "POST");
                    Write(res, 405, "text/plain", "此端点不提供 SSE 流");
                    return;

                case "DELETE":
                    res.AddHeader("Allow", "POST");
                    Write(res, 405, "text/plain", "无会话可终止");
                    return;

                case "OPTIONS":
                    res.AddHeader("Allow", "POST, OPTIONS");
                    res.StatusCode = 204;
                    res.Close();
                    return;

                default:
                    res.AddHeader("Allow", "POST");
                    Write(res, 405, "text/plain", "method not allowed");
                    return;
            }
        }

        private void HandlePost(HttpListenerRequest req, HttpListenerResponse res)
        {
            // JSON-RPC 消息按 MCP 规范必须是 UTF-8。
            // 不能用 req.ContentEncoding：客户端不带 charset 时，.NET Framework 会退回系统 ANSI（如 GBK），导致中文乱码。
            string body;
            using (var sr = new StreamReader(req.InputStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
                body = sr.ReadToEnd();

            JObject msg;
            try
            {
                msg = JObject.Parse(body);
            }
            catch
            {
                Write(res, 200, "application/json",
                    JsonRpc.Error(null, JsonRpc.ParseError, "JSON 解析失败").ToString(Formatting.None));
                return;
            }

            JObject? response;
            try
            {
                response = _dispatcher.Handle(msg);
            }
            catch (Exception ex)
            {
                Log.Error("dispatch", $"{(string?)msg["method"]}：{ex.Message}");
                response = JsonRpc.Error(msg["id"], JsonRpc.InternalError, ex.Message);
            }

            if (response == null)
            {
                // 通知 / 响应：202 无 body
                res.StatusCode = 202;
                res.Close();
                return;
            }

            Write(res, 200, "application/json", response.ToString(Formatting.None));
        }

        private static bool IsLocalOrigin(string origin)
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var u))
            {
                string host = u.Host;
                return host == "127.0.0.1" || host == "localhost" || host == "::1" || host == "[::1]";
            }
            return false;
        }

        private static void Write(HttpListenerResponse res, int status, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            res.StatusCode = status;
            res.ContentType = contentType + "; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            try
            {
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                res.Close();
            }
        }
    }
}
