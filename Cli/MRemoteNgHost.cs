using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UvncFtpBridge.Ftp;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// mRemoteNG 主機模式（--mremoteng）：給自編的 mRemoteNG 自動開、在背景一直跑。
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

                string? Api(string name, System.Collections.Specialized.NameValueCollection query) => name switch
                {
                    "status" => Status(ftp.Port, repeater.Port, routes),
                    "route" => RouteJson(routes, query["id"]),
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
                Console.WriteLine("此視窗可以縮到最小；按 Q 或關閉此視窗即結束所有通道。");
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

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
