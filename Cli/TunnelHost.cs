using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using UvncFtpBridge.Ftp;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 一個入口同時開 WinSCP 與 UltraVNC：本機起 FTP 橋接（給 WinSCP）與 UltraVNC Repeater（給 Viewer），
/// 加 --file-tunnel 時兩者都經同一條 File-Tunnel 跳板，否則直連（或走 UVNC_SOCKS5_PROXY）。
/// 命令視窗開著服務就在，關掉 WinSCP 或 Viewer 都不影響，方便把它們釘在工作列隨時開；按 Q 或關窗才結束。
/// 同一份套件再開一次時不另起服務，只再開一次 WinSCP 與 Viewer。
/// </summary>
internal static class TunnelHost
{
    const string Usage = "用法：UvncFtpBridge --open [--file-tunnel] [--viewer vncviewer.exe 路徑]";

    public static int RunCli(string[] args)
    {
        bool fileTunnel = false;
        string? viewerArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--file-tunnel" && !fileTunnel) fileTunnel = true;
            else if (args[i] == "--viewer" && viewerArg == null && i + 1 < args.Length) viewerArg = args[++i];
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
        return Run(fileTunnel, proxy, viewerArg);
    }

    public static int Run(bool fileTunnel, Socks5Proxy? proxy, string? viewerArg)
    {
        string baseDir = AppContext.BaseDirectory;
        try
        {
            var (viewer, ownViewer) = VncCommand.FindViewer(baseDir, viewerArg);
            Directory.CreateDirectory(Path.Combine(baseDir, "Logs"));
            Log.FilePath = Path.Combine(baseDir, "Logs", "launcher.log");

            using var instance = PackageInstance.TryAcquire(baseDir);
            if (instance == null)
            {
                if (!PackageInstance.TunnelHostRunning(baseDir))
                {
                    Console.Error.WriteLine("這份可攜套件已用舊的 WinSCP 入口開著，請先關閉它。");
                    return 1;
                }
                // 服務已經在跑（可能是另一種模式）：沿用它，只再開一次 WinSCP 與 Viewer。
                Console.WriteLine("這份套件的通道已經在執行，沿用它開啟 WinSCP 與 UltraVNC Viewer。");
                OpenBoth(baseDir, WaitForFtpPort(baseDir), viewer, ownViewer);
                return 0;
            }
            using var hostFlag = PackageInstance.MarkTunnelHost(baseDir);
            string portFile = PackageInstance.FtpPortFile(baseDir);
            TryDelete(portFile);

            using var stop = new CancellationTokenSource();
            var tunnel = fileTunnel ? FileTunnelSession.Start(baseDir) : null;
            var services = new List<Task>();
            int shutdown = 0;
            void Shutdown()
            {
                if (Interlocked.Exchange(ref shutdown, 1) != 0) return;
                stop.Cancel();
                tunnel?.Dispose();
                try { Task.WaitAll(services.ToArray(), 2000); }
                catch (AggregateException) { }
                TryDelete(portFile);
            }
            // 直接按 X 關視窗、Ctrl+C 或登出時也要收掉 ft.exe，不然它會一直佔著 1081 埠
            using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Shutdown());
            using var onCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => Shutdown());
            using var onBreak = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, _ => Shutdown());
            using var onLogoff = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Shutdown());
            try
            {
                var repeater = new VncRepeater(IPAddress.Loopback, VncRepeater.DefaultPort, proxy);
                repeater.Start();
                services.Add(Task.Run(() => repeater.RunAsync(stop.Token)));
                var ftp = new FtpServer(IPAddress.Loopback, 0, 5900, proxy: proxy);
                ftp.Start();
                services.Add(Task.Run(() => ftp.Run(stop.Token)));
                File.WriteAllText(portFile, ftp.Port.ToString(CultureInfo.InvariantCulture));
                string mode = fileTunnel ? "經 File-Tunnel 跳板" : proxy != null ? $"經 SOCKS5 代理 {proxy.Host}:{proxy.Port}" : "直連";
                Log.Info($"已就緒（{mode}）：UltraVNC Repeater 127.0.0.1:{repeater.Port}，WinSCP 橋接 127.0.0.1:{ftp.Port}。");

                Console.WriteLine();
                Console.WriteLine($"已就緒（{mode}），WinSCP 與 UltraVNC 可同時使用。");
                if (fileTunnel) Console.WriteLine("  1. 用 mstsc 連跳板，在跳板執行 Start-FileTunnel-Remote.cmd（已在執行就不用再開）。");
                Console.WriteLine("  2. WinSCP：填機台 IP、VNC 埠與密碼後登入。UltraVNC：在 Viewer 填機台 IP，按 Connect。");
                if (viewer != null && !ownViewer) Console.WriteLine("     " + ManualRepeaterHint);
                Console.WriteLine("此視窗開著就一直可用；關閉 WinSCP 或 Viewer 不影響，可從工作列再開。");
                Console.WriteLine("按 W 再開 WinSCP，按 V（或 Enter）再開 Viewer；按 Q 或關閉此視窗即結束。");
                Console.WriteLine();
                OpenBoth(baseDir, ftp.Port, viewer, ownViewer);
                WaitForQuit(baseDir, ftp.Port, viewer, ownViewer, stop.Token);
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

    static string ManualRepeaterHint => $"請在 Viewer 連線視窗選「Repeater」，旁邊填 127.0.0.1:{VncRepeater.DefaultPort}。";

    static void WaitForQuit(string baseDir, int ftpPort, string? viewer, bool ownViewer, CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            ct.WaitHandle.WaitOne();
            return;
        }
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape: return;
                case ConsoleKey.V or ConsoleKey.Enter: OpenViewer(viewer, ownViewer); break;
                case ConsoleKey.W: OpenWinScp(baseDir, ftpPort); break;
            }
        }
    }

    static void OpenBoth(string baseDir, int ftpPort, string? viewer, bool ownViewer)
    {
        OpenWinScp(baseDir, ftpPort);
        OpenViewer(viewer, ownViewer);
    }

    static void OpenViewer(string? viewer, bool ownViewer)
    {
        if (viewer == null) { Log.Warn("找不到 UltraVNC Viewer，請自行開啟。" + ManualRepeaterHint); return; }
        if (ownViewer) VncCommand.ConfigureViewer(viewer, VncRepeater.DefaultPort);
        VncCommand.OpenViewer(viewer);
    }

    static void OpenWinScp(string baseDir, int ftpPort)
    {
        try
        {
            WinScpCommand.EnsureInstalled(baseDir);
            WinScpCommand.ConfigureIni(baseDir, ftpPort);
            using var client = WinScpCommand.StartWinScp(baseDir, null);
            Log.Info("已開啟 WinSCP；機台 IP 與 VNC 密碼請在 WinSCP 輸入。");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            Log.Warn("無法開啟 WinSCP：" + ex.Message);
        }
    }

    /// <summary>服務剛啟動時可能還沒寫好埠號檔，稍等一下。</summary>
    internal static int WaitForFtpPort(string baseDir)
    {
        string portFile = PackageInstance.FtpPortFile(baseDir);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                if (int.TryParse(File.ReadAllText(portFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535)
                    return port;
            }
            catch (IOException) { }
            Thread.Sleep(250);
        }
        throw new IOException("通道還沒準備好 WinSCP 橋接；請稍後再開一次。");
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
