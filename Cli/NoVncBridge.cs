using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// noVNC 橋：開一個本機 HTTP 服務，原樣送出官方 noVNC（程式旁的 noVNC\ 資料夾，可攜套件建置時下載），
/// 並把網頁的 WebSocket（ws://127.0.0.1:6080/ws/機台IP[:埠]）轉成 TCP，經代理（直連／SOCKS5／File-Tunnel）接到機台 UltraVNC。
/// 原版 vnc.html 用 path 參數決定 WebSocket 位址，所以 mRemoteNG 的 HTTP/HTTPS 連線填
/// http://127.0.0.1:6080/vnc.html?path=ws/&lt;機台IP&gt; 就能嵌進分頁、直達那台。
/// 不走 Repeater 的握手——目標由橋這端直接連。--open 會一起開（見 TunnelHost），也可以用 --novnc 單獨開。
/// </summary>
internal sealed class NoVncBridge(string baseDir, int port, Socks5Proxy? proxy)
{
    public const int DefaultPort = 6080;
    const int DefaultVncPort = 5900;
    const int BufferSize = 64 * 1024;
    const string WebSocketPrefix = "/ws/";
    const string Usage = "用法：UvncFtpBridge --novnc [--file-tunnel] [--port <HTTP 埠，預設 6080>]";

    readonly HttpListener http = new();
    readonly string webRoot = Path.Combine(baseDir, "noVNC");

    public int Port => port;

    public static string UrlHint(int port) => $"http://127.0.0.1:{port}/vnc.html?path=ws/<機台IP>";

    /// <summary>開始聽 127.0.0.1:port。缺 noVNC 檔案或埠被佔用時丟 IOException。</summary>
    public void Start()
    {
        if (!File.Exists(Path.Combine(webRoot, "vnc.html")) || !File.Exists(Path.Combine(webRoot, "core", "rfb.js")))
            throw new FileNotFoundException($"找不到 noVNC（{webRoot}\\vnc.html）。請用 Build-Portable.ps1 建置的可攜套件。");
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { http.Start(); }
        catch (HttpListenerException ex)
        {
            throw new IOException($"本機 {port} 埠無法開啟 noVNC 網頁服務：{ex.Message}（埠可能被佔用）。");
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var onStop = ct.Register(() => { try { http.Close(); } catch (ObjectDisposedException) { } });
        var sessions = new List<Task>();
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }
            sessions.RemoveAll(t => t.IsCompleted);
            sessions.Add(Task.Run(() => HandleAsync(ctx, ct)));
        }
        await Task.WhenAll(sessions);
    }

    // ================================================================ 單獨開（--novnc）
    public static int RunCli(string[] args)
    {
        int httpPort = DefaultPort;
        bool fileTunnel = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--file-tunnel" && !fileTunnel) fileTunnel = true;
            else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int p) && p is > 0 and <= 65535) httpPort = p;
            else
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }
        Socks5Proxy? proxy;
        try { proxy = fileTunnel ? new Socks5Proxy("127.0.0.1", FileTunnelSession.SocksPort) : Socks5Proxy.FromEnvironment(); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        string baseDir = AppContext.BaseDirectory;
        Directory.CreateDirectory(Path.Combine(baseDir, "Logs"));
        Log.FilePath = Path.Combine(baseDir, "Logs", "launcher.log");
        using var stop = new CancellationTokenSource();
        FileTunnelSession? tunnel = null;
        try
        {
            var bridge = new NoVncBridge(baseDir, httpPort, proxy);
            bridge.Start();
            tunnel = fileTunnel ? FileTunnelSession.Start(baseDir) : null;
            var serve = Task.Run(() => bridge.RunAsync(stop.Token));

            string mode = fileTunnel ? "經 File-Tunnel 跳板" : proxy != null ? $"經 SOCKS5 代理 {proxy.Host}:{proxy.Port}" : "直連";
            Log.Info($"noVNC 服務就緒（{mode}）：http://127.0.0.1:{httpPort}/");
            Console.WriteLine();
            Console.WriteLine($"noVNC 已就緒（{mode}）。在 mRemoteNG 新增「HTTP/HTTPS」連線，網址填：");
            Console.WriteLine("    " + UrlHint(httpPort));
            if (fileTunnel) Console.WriteLine("  （File-Tunnel 模式：請先用 mstsc 連跳板並執行 Start-FileTunnel-Remote.cmd。）");
            Console.WriteLine("此視窗開著服務就一直可用；按 Q 或關閉此視窗即結束。");
            Console.WriteLine();

            WaitForQuit(stop.Token);
            stop.Cancel();
            serve.Wait(2000);
            return 0;
        }
        catch (IOException ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
        finally
        {
            stop.Cancel();
            tunnel?.Dispose();
        }
    }

    static void WaitForQuit(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            ct.WaitHandle.WaitOne();
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.Q or ConsoleKey.Escape) return;
        }
    }

    // ================================================================ 請求處理
    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            string path = ctx.Request.Url!.AbsolutePath;
            if (path.StartsWith(WebSocketPrefix, StringComparison.Ordinal))
            {
                await HandleWebSocketAsync(ctx, Uri.UnescapeDataString(path[WebSocketPrefix.Length..]), ct);
                return;
            }
            if (path == "/")
            {
                // 沒指定頁面就開原版 vnc.html，網址參數原樣帶過去
                ctx.Response.Redirect("/vnc.html" + ctx.Request.Url.Query);
                ctx.Response.Close();
                return;
            }

            string? file = SafePath(webRoot, path);
            if (file == null || !File.Exists(file))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
            byte[] body = await File.ReadAllBytesAsync(file, ct);
            ctx.Response.ContentType = ContentType(file);
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    async Task HandleWebSocketAsync(HttpListenerContext ctx, string target, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }
        var destination = ParseTarget(target);
        if (destination == null)
        {
            Log.Warn($"noVNC：無法辨識機台位址「{target}」。網址請用 vnc.html?path=ws/10.1.2.3 或 ws/10.1.2.3:5901。");
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }
        var (host, targetPort) = destination.Value;

        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(subProtocol: null)).WebSocket; }
        catch { try { ctx.Response.Abort(); } catch { } return; }

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            Log.Info($"noVNC 連線 → {host}:{targetPort}");
            await Task.Run(() =>
            {
                if (proxy == null) RfbConnection.ConnectTcp(tcp, host, targetPort, 15000, $"連不上 {host}:{targetPort}");
                else proxy.Connect(tcp, host, targetPort, 15000);
            }, ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"noVNC 連線失敗 {host}:{targetPort}：{ex.Message}");
            tcp.Dispose();
            try { await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "connect failed", CancellationToken.None); } catch { }
            ws.Dispose();
            return;
        }

        using (tcp)
        using (ws)
        {
            var stream = tcp.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task up = WebSocketToTcp(ws, stream, linked);
            Task down = TcpToWebSocket(stream, ws, linked);
            await Task.WhenAny(up, down);
            linked.Cancel();
            await Task.WhenAll(up, down);
            Log.Info($"noVNC 連線結束：{host}:{targetPort}");
        }
    }

    /// <summary>WebSocket（瀏覽器 noVNC）→ 機台 TCP。不理會 WebSocket 訊息邊界，收到多少就往 TCP 寫多少。</summary>
    static async Task WebSocketToTcp(WebSocket ws, NetworkStream tcp, CancellationTokenSource cts)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0) await tcp.WriteAsync(buffer.AsMemory(0, result.Count), cts.Token);
            }
        }
        catch { }
        finally { cts.Cancel(); }
    }

    /// <summary>機台 TCP → WebSocket（二進位訊框）。</summary>
    static async Task TcpToWebSocket(NetworkStream tcp, WebSocket ws, CancellationTokenSource cts)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                int n = await tcp.ReadAsync(buffer, cts.Token);
                if (n <= 0) break;
                await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
            }
        }
        catch { }
        finally { cts.Cancel(); }
    }

    /// <summary>
    /// WebSocket 路徑 /ws/ 後面的機台位址：IP 或主機名稱，可加 :埠（IPv6 寫 [位址]:埠），沒給埠用 5900。
    /// 小於 100 的埠當 display 編號（跟 UltraVNC 一樣 +5900）。
    /// </summary>
    internal static (string Host, int Port)? ParseTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string host = target.Trim().TrimEnd('/');
        string? portText = null;
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end < 0) return null;
            string rest = host[(end + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') return null;
                portText = rest[1..];
            }
            host = host[1..end];
        }
        else if (host.IndexOf(':') is int colon and >= 0 && host.IndexOf(':', colon + 1) < 0)
        {
            portText = host[(colon + 1)..];
            host = host[..colon];
        }
        int port = DefaultVncPort;
        if (portText != null && !int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)) return null;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        if (port is >= 0 and < 100) port = 5900 + port;
        return port is >= 1 and <= 65535 ? (host, port) : null;
    }

    /// <summary>網址對到 root 底下的檔案；跑出 root 外（..）回 null。</summary>
    static string? SafePath(string root, string urlPath)
    {
        string rel = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, rel));
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".ico" or ".cur" => "image/x-icon",
        ".oga" => "audio/ogg",
        ".mp3" => "audio/mpeg",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
