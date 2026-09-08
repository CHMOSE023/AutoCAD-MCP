using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AcadMcp.Mcp;
using Newtonsoft.Json.Linq;

// 脱离 AutoCAD，验证内嵌 HTTP + MCP JSON-RPC 传输层是否符合 MCP Streamable HTTP 规范。
// 工具处理函数用假实现（回显参数），只测协议，不测 AutoCAD 操作。

int pass = 0, fail = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (detail != null ? "  -> " + detail : ""));
    if (ok) pass++; else fail++;
}

var tools = new List<Tool>
{
    new Tool("echo", "回显参数", Schema.Object(Schema.P("msg", "string", "内容", true)),
        a => "echo: " + (string?)a["msg"]),
    new Tool("add", "两数相加", Schema.Object(
            Schema.P("a", "number", "加数", true), Schema.P("b", "number", "加数", true)),
        a => "sum=" + (a["a"]!.Value<double>() + a["b"]!.Value<double>())),
    new Tool("snap", "返回一张图", Schema.Object(),
        _ => ToolResult.TextAndImage("here:", new byte[] { 1, 2, 3, 4 }, "image/png")),
    new Tool("boom", "总是抛错", Schema.Object(),
        (Func<JObject, string>)(_ => throw new Exception("kaboom"))),
    new Tool("gone", "抛 handle 未找到", Schema.Object(),
        (Func<JObject, string>)(_ => throw new Exception("未找到 handle 2A3"))),
};

var dispatcher = new McpDispatcher(tools);
var server = new HttpServer("127.0.0.1", 7139, "/mcp", dispatcher);
server.Start();
Console.WriteLine("test server: " + server.Endpoint);

using var http = new HttpClient();

async Task<(int status, string body, HttpResponseMessage resp)> Post(string json, bool withAccept = true)
{
    var req = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    if (withAccept)
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
    req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
    var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    return ((int)resp.StatusCode, body, resp);
}

try
{
    // 1. initialize
    var (st, body, _) = await Post(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");
    var jo = JObject.Parse(body);
    Check("initialize 200", st == 200, "status=" + st);
    Check("initialize protocolVersion", (string?)jo["result"]?["protocolVersion"] == "2025-06-18", body);
    Check("initialize serverInfo.name", (string?)jo["result"]?["serverInfo"]?["name"] == "autocad-mcp");
    Check("initialize capabilities.tools", jo["result"]?["capabilities"]?["tools"] != null);

    // 2. initialized 通知 -> 202 无 body
    var (st2, body2, _) = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    Check("notifications/initialized 202", st2 == 202, "status=" + st2);
    Check("notifications/initialized empty body", string.IsNullOrEmpty(body2));

    // 3. ping
    var (st3, body3, _) = await Post("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
    Check("ping result {}", st3 == 200 && JObject.Parse(body3)["result"]?.Type == JTokenType.Object, body3);

    // 4. tools/list
    var (_, body4, _) = await Post("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
    var list = JObject.Parse(body4)["result"]?["tools"] as JArray;
    Check("tools/list count", list?.Count == 5, "count=" + (list?.Count));
    Check("tools/list has inputSchema", list?[0]?["inputSchema"]?["type"]?.ToString() == "object");
    Check("tools/list name+description", (string?)list?[0]?["name"] == "echo" && list?[0]?["description"] != null);

    // 5. tools/call 正常
    var (st5, body5, _) = await Post(
        """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"add","arguments":{"a":2,"b":3}}}""");
    var content = JObject.Parse(body5)["result"]?["content"] as JArray;
    Check("tools/call content[0].type=text", (string?)content?[0]?["type"] == "text", body5);
    Check("tools/call result text", (string?)content?[0]?["text"] == "sum=5", body5);
    Check("tools/call no isError", JObject.Parse(body5)["result"]?["isError"] == null);

    // 5b. tools/call 返回图片 -> image content 块
    var (_, bodyImg, _) = await Post(
        """{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"snap","arguments":{}}}""");
    var imgContent = JObject.Parse(bodyImg)["result"]?["content"] as JArray;
    Check("tools/call image: text + image blocks", imgContent?.Count == 2, bodyImg);
    Check("tools/call image: type=image", (string?)imgContent?[1]?["type"] == "image");
    Check("tools/call image: base64 data", (string?)imgContent?[1]?["data"] == "AQIDBA==", bodyImg);
    Check("tools/call image: mimeType", (string?)imgContent?[1]?["mimeType"] == "image/png");

    // 5c. 错误 hint：handle 类错误应带"提示："
    var (_, bodyHint, _) = await Post(
        """{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"gone","arguments":{}}}""");
    var hintText = (string?)(JObject.Parse(bodyHint)["result"]?["content"]?[0]?["text"]);
    Check("tools/call error 带 hint", hintText != null && hintText.Contains("提示：") && hintText.Contains("query_entities"), bodyHint);

    // 6. tools/call 工具内异常 -> isError=true，仍是 200 + result
    var (st6, body6, _) = await Post(
        """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"add","arguments":{"a":"x"}}}""");
    Check("tools/call error status 200", st6 == 200);
    Check("tools/call isError=true", JObject.Parse(body6)["result"]?["isError"]?.Value<bool>() == true, body6);

    // 7. 未知工具 -> InvalidParams
    var (_, body7, _) = await Post(
        """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"nope","arguments":{}}}""");
    Check("unknown tool -> error -32602", JObject.Parse(body7)["error"]?["code"]?.Value<int>() == -32602, body7);

    // 8. 未知方法 -> MethodNotFound
    var (_, body8, _) = await Post("""{"jsonrpc":"2.0","id":7,"method":"foo/bar"}""");
    Check("unknown method -> -32601", JObject.Parse(body8)["error"]?["code"]?.Value<int>() == -32601, body8);

    // 9. 坏 JSON -> ParseError
    var (_, body9, _) = await Post("{ not json");
    Check("bad json -> -32700", JObject.Parse(body9)["error"]?["code"]?.Value<int>() == -32700, body9);

    // 10. GET -> 405
    var getResp = await http.GetAsync(server.Endpoint);
    Check("GET -> 405", (int)getResp.StatusCode == 405, "status=" + (int)getResp.StatusCode);

    // 11. 非法 Origin -> 403
    var badOrigin = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":9,"method":"ping"}""", Encoding.UTF8, "application/json"),
    };
    badOrigin.Headers.TryAddWithoutValidation("Origin", "https://evil.example.com");
    var badResp = await http.SendAsync(badOrigin);
    Check("evil Origin -> 403", (int)badResp.StatusCode == 403, "status=" + (int)badResp.StatusCode);

    // 12. 本机 Origin 放行
    var goodOrigin = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":10,"method":"ping"}""", Encoding.UTF8, "application/json"),
    };
    goodOrigin.Headers.TryAddWithoutValidation("Origin", "http://127.0.0.1:7139");
    var goodResp = await http.SendAsync(goodOrigin);
    Check("localhost Origin -> 200", (int)goodResp.StatusCode == 200, "status=" + (int)goodResp.StatusCode);

    // 13b. UTF-8 请求体、Content-Type 不带 charset -> 中文不能乱码
    {
        var raw = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"echo","arguments":{"msg":"客厅 α β 测试"}}}""");
        var rawContent = new ByteArrayContent(raw);
        rawContent.Headers.TryAddWithoutValidation("Content-Type", "application/json"); // 故意不带 charset
        var rawReq = new HttpRequestMessage(HttpMethod.Post, server.Endpoint) { Content = rawContent };
        rawReq.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        var rawResp = await http.SendAsync(rawReq);
        var text = (string?)JObject.Parse(await rawResp.Content.ReadAsStringAsync())["result"]?["content"]?[0]?["text"];
        Check("UTF-8 body without charset -> no mojibake", text == "echo: 客厅 α β 测试", text);
    }

    // 13c. 日志：成功 / 失败的 tools/call 都写入日志文件
    {
        var before = File.Exists(AcadMcp.Mcp.Log.CurrentFile)
            ? File.ReadAllText(AcadMcp.Mcp.Log.CurrentFile).Length : 0;
        await Post("""{"jsonrpc":"2.0","id":50,"method":"tools/call","params":{"name":"echo","arguments":{"msg":"日志测试"}}}""");
        await Post("""{"jsonrpc":"2.0","id":51,"method":"tools/call","params":{"name":"boom","arguments":{}}}""");
        System.Threading.Thread.Sleep(50);
        var log = File.ReadAllText(AcadMcp.Mcp.Log.CurrentFile);
        Check("log 记录了成功调用", log.IndexOf("echo", before) >= 0);
        Check("log 记录了失败调用 + ERR", log.Contains("boom") && log.Contains("kaboom"), "");
    }

    // 13d. 只读模式：写工具被拒
    {
        AcadMcp.Mcp.Safety.ToggleReadOnly();
        var (_, roBody, _) = await Post(
            """{"jsonrpc":"2.0","id":60,"method":"tools/call","params":{"name":"echo","arguments":{"msg":"x"}}}""");
        var jo2 = JObject.Parse(roBody);
        Check("只读模式拒绝写工具", jo2["result"]?["isError"]?.Value<bool>() == true
            && ((string?)jo2["result"]?["content"]?[0]?["text"])!.Contains("只读"), roBody);
        AcadMcp.Mcp.Safety.ToggleReadOnly();
        // 关掉后恢复
        var (_, okBody, _) = await Post(
            """{"jsonrpc":"2.0","id":61,"method":"tools/call","params":{"name":"echo","arguments":{"msg":"x"}}}""");
        Check("关闭只读后写工具恢复", (string?)JObject.Parse(okBody)["result"]?["content"]?[0]?["text"] == "echo: x");
    }

    // 13e. token 鉴权
    {
        Environment.SetEnvironmentVariable("ACADMCP_TOKEN", "testtok123");
        try
        {
            var noAuth = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            { Content = new StringContent("""{"jsonrpc":"2.0","id":70,"method":"ping"}""", Encoding.UTF8, "application/json") };
            var r1 = await http.SendAsync(noAuth);
            Check("有 token 时无 Authorization -> 401", (int)r1.StatusCode == 401, "status=" + (int)r1.StatusCode);

            var withAuth = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
            { Content = new StringContent("""{"jsonrpc":"2.0","id":71,"method":"ping"}""", Encoding.UTF8, "application/json") };
            withAuth.Headers.TryAddWithoutValidation("Authorization", "Bearer testtok123");
            var r2 = await http.SendAsync(withAuth);
            Check("正确 Bearer -> 200", (int)r2.StatusCode == 200, "status=" + (int)r2.StatusCode);
        }
        finally { Environment.SetEnvironmentVariable("ACADMCP_TOKEN", null); }
    }

    // 14. 不支持的协议版本头 -> 400
    var badPv = new HttpRequestMessage(HttpMethod.Post, server.Endpoint)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":11,"method":"ping"}""", Encoding.UTF8, "application/json"),
    };
    badPv.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "1999-01-01");
    var badPvResp = await http.SendAsync(badPv);
    Check("bad MCP-Protocol-Version -> 400", (int)badPvResp.StatusCode == 400, "status=" + (int)badPvResp.StatusCode);
}
finally
{
    server.Stop();
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
