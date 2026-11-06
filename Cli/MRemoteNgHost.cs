using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UvncFtpBridge.Ftp;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// mRemoteNG 主機模式（--mremoteng）：給自編的 mRemoteNG 自動開，mRemoteNG 結束時跟著結束（/api/owner、/api/quit）。
/// 跟 --open 一樣開 FTP 橋接（WinSCP）、UltraVNC Repeater（5999）與 noVNC（6080），但：
///   • 不自動開 WinSCP 與 Viewer，由 mRemoteNG 嵌進分頁；
///   • 同時支援直連與多個跳板：目標帶「路由~」就走那個跳板自己的 File-Tunnel（見 Route、RouteManager）；
///   • noVNC 服務另外提供 /api/status、/api/route 給 mRemoteNG 查埠號、先開跳板通道。
/// 同一份套件已經在跑就直接結束（mRemoteNG 每次連線前都會呼叫，重複呼叫沒關係）。
/// </summary>
internal static class MRemoteNgHost
{
    const string Usage = "用法：UvncFtpBridge --mremoteng（由 mRemoteNG 自動啟動，平常不用手動執行）";

    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string baseDir = AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(baseDir, "Logs"));
            Log.FilePath = Path.Combine(baseDir, "Logs", "launcher.log");
            // 查問題用：設環境變數 UVNCFTPBRIDGE_VERBOSE=1 再讓 mRemoteNG 開通道，記錄檔會多 VNC 協定細節
            if (Environment.GetEnvironmentVariable("UVNCFTPBRIDGE_VERBOSE") == "1") Log.Verbose = true;

            using var instance = PackageInstance.TryAcquire(baseDir);
            if (instance == null)
            {
                if (PackageInstance.MRemoteNgHostRunning(baseDir))
                {
                    Console.WriteLine("mRemoteNG 通道主機已經在執行。");
                    return 0;
                }
                Log.Error("這份可攜套件已經用其他入口開著（Start-Direct／Start-FileTunnel 或 WinSCP 入口），請先關閉它再讓 mRemoteNG 開通道。");
                return 1;
            }
            try { Console.Title = "UvncFtpBridge - mRemoteNG 通道主機"; }
            catch (IOException) { }
            using var hostFlag = PackageInstance.MarkTunnelHost(baseDir);
            using var mremotengFlag = PackageInstance.MarkMRemoteNgHost(baseDir);
            string portFile = PackageInstance.FtpPortFile(baseDir);
            TryDelete(portFile);

            Socks5Proxy? direct = Socks5Proxy.FromEnvironment();
            using var routes = new RouteManager(baseDir);
            RouteProxy routeProxy = routes.Get;
            using var stop = new CancellationTokenSource();
            var services = new List<Task>();
            int shutdown = 0;
            void Shutdown()
            {
                if (Interlocked.Exchange(ref shutdown, 1) != 0) return;
                stop.Cancel();
                routes.Dispose();
                try { Task.WaitAll(services.ToArray(), 2000); }
                catch (AggregateException) { }
                TryDelete(portFile);
            }
            using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Shutdown());
            using var onCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => Shutdown());
            using var onBreak = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, _ => Shutdown());
            using var onLogoff = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Shutdown());
            try
            {
                var repeater = new VncRepeater(IPAddress.Loopback, VncRepeater.DefaultPort, direct, routeProxy);
                repeater.Start();
                services.Add(Task.Run(() => repeater.RunAsync(stop.Token)));
                var ftp = new FtpServer(IPAddress.Loopback, 0, 5900, proxy: direct, routes: routeProxy);
                ftp.Start();
                services.Add(Task.Run(() => ftp.Run(stop.Token)));
                File.WriteAllText(portFile, ftp.Port.ToString(CultureInfo.InvariantCulture));

                // mRemoteNG 登記自己的 PID：它結束（含當掉）時通道主機跟著結束；正常關閉時它會再呼叫 quit 立刻結束。
                int ownerPid = 0;
                string Owner(string? pidText)
                {
                    if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                        throw new ArgumentException("缺少 pid。");
                    Process owner;
                    try { owner = Process.GetProcessById(pid); }
                    catch (ArgumentException) { throw new ArgumentException($"找不到程序 {pid}。"); }
                    if (Interlocked.Exchange(ref ownerPid, pid) != pid)
                    {
                        Log.Info($"由 mRemoteNG（PID {pid}）管理：mRemoteNG 結束時通道主機跟著結束。");
                        _ = WatchOwner(owner, pid);
                    }
                    else owner.Dispose();
                    return "{\"owner\":" + pid.ToString(CultureInfo.InvariantCulture) + "}";
                }
                async Task WatchOwner(Process owner, int pid)
                {
                    using (owner)
                    {
                        try { await owner.WaitForExitAsync(stop.Token); }
                        catch (OperationCanceledException) { return; }
                    }
                    if (Volatile.Read(ref ownerPid) != pid) return; // 已換成新的 mRemoteNG
                    Log.Info("mRemoteNG 已結束，關閉通道主機。");
                    stop.Cancel();
                }
                string Quit()
                {
                    Log.Info("mRemoteNG 關閉，結束通道主機。");
                    // 先讓這個回應送出去再停
                    _ = Task.Delay(200).ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
                    return "{\"quit\":true}";
                }

                // 傳送檔案到桌面（VNC）：POST host、port、password、route（可省略＝直連）、file（可多筆）
                string Desktop(System.Collections.Specialized.NameValueCollection query)
                {
                    string host = (query["host"] ?? "").Trim();
                    if (host.Length == 0) throw new ArgumentException("缺少 host（機台 IP）。");
                    int port = int.TryParse(query["port"], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 5900;
                    string password = query["password"] ?? "";
                    string? route = string.IsNullOrWhiteSpace(query["route"]) ? null : query["route"]!.Trim();
                    if (route != null && !Route.IsValidId(route)) throw new ArgumentException($"路由名稱不合法：{route}");
                    var files = query.GetValues("file") ?? Array.Empty<string>();
                    if (files.Length == 0) throw new ArgumentException("沒有要傳的檔案。");
                    Log.Info(route == null ? $"傳送檔案到桌面：連線到機台 {host}:{port} …" : $"傳送檔案到桌面：連線到機台 {host}:{port}（經跳板 {route}）…");
                    using var vnc = route == null
                        ? RfbConnection.Connect(host, port, password, proxy: direct)
                        : Ftp.FtpSession.ConnectThroughRoute(() => routes.Get(route), host, port, password);
                    return DesktopSend.Run(vnc, files);
                }

                string? Api(string name, System.Collections.Specialized.NameValueCollection query) => name switch
                {
                    "status" => Status(ftp.Port, repeater.Port, routes),
                    "route" => RouteJson(routes, query["id"]),
                    "owner" => Owner(query["pid"]),
                    "quit" => Quit(),
                    "desktop" => Desktop(query),
                    _ => null,
                };
                var novnc = new NoVncBridge(baseDir, NoVncBridge.DefaultPort, direct, routeProxy, Api);
                novnc.Start();
                services.Add(Task.Run(() => novnc.RunAsync(stop.Token)));

                string mode = direct != null ? $"直連經 SOCKS5 代理 {direct.Host}:{direct.Port}" : "直連";
                Log.Info($"mRemoteNG 通道主機已就緒（{mode}，跳板依連線設定）：UltraVNC Repeater 127.0.0.1:{repeater.Port}，" +
                         $"WinSCP 橋接 127.0.0.1:{ftp.Port}，noVNC http://127.0.0.1:{novnc.Port}/。");
                Console.WriteLine();
                Console.WriteLine("mRemoteNG 通道主機已就緒，WinSCP、UltraVNC、noVNC 分頁都經由這裡連到機台。");
                Console.WriteLine("  有設「跳板通道」的連線會自動開那個跳板自己的 File-Tunnel；");
                Console.WriteLine("  請在跳板（RDP 分頁）執行 Start-FileTunnel-Remote.cmd（已在執行就不用再開）。");
                Console.WriteLine("關閉這個「通道主機」分頁、關閉 mRemoteNG 或按 Q，即結束所有通道。");
                Console.WriteLine();
                WaitForQuit(stop.Token);
                return 0;
            }
            finally { Shutdown(); }
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
    }

    static string Status(int ftpPort, int repeaterPort, RouteManager routes)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteNumber("ftpPort", ftpPort);
            json.WriteNumber("repeaterPort", repeaterPort);
            json.WriteNumber("noVncPort", NoVncBridge.DefaultPort);
            // 主控台視窗 handle：mRemoteNG 用它把這個視窗嵌進「通道主機」分頁
            json.WriteNumber("consoleWindow", GetConsoleWindow().ToInt64());
            json.WriteNumber("pid", Environment.ProcessId);
            json.WriteStartArray("routes");
            foreach (var (route, socksPort, online) in routes.Snapshot()) WriteRoute(json, route, socksPort, online);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>先把跳板通道開起來（mRemoteNG 開連線前呼叫），讓跳板端早點連上。</summary>
    static string RouteJson(RouteManager routes, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("缺少 id（路由名稱）。");
        var proxy = routes.Get(id);
        bool online = routes.Snapshot().FirstOrDefault(r => string.Equals(r.Route, id, StringComparison.OrdinalIgnoreCase)).Online;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) WriteRoute(json, id, proxy.Port, online);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteRoute(Utf8JsonWriter json, string route, int socksPort, bool online)
    {
        json.WriteStartObject();
        json.WriteString("route", route);
        json.WriteNumber("socksPort", socksPort);
        json.WriteBoolean("online", online);
        json.WriteString("folder", FileTunnelSession.RouteFolder(route));
        json.WriteEndObject();
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    static void WaitForQuit(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            ct.WaitHandle.WaitOne();
            return;
        }
        // 不能卡在 ReadKey：mRemoteNG 結束（quit／owner 監看）時要能馬上離開
        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                ct.WaitHandle.WaitOne(100);
                continue;
            }
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.Q or ConsoleKey.Escape) return;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
