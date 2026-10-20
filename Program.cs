using System.Net;
using System.Text;
using UvncFtpBridge.Cli;
using UvncFtpBridge.Ftp;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge;

static class Program
{
    static int Main(string[] args)
    {
        // 讓 CP950（Big5）可用，給不支援 UTF-8 的舊版 UltraVNC 伺服器用
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?" or "help")
        {
            CliCommands.PrintHelp();
            return 0;
        }

        if (args[0] is "--selftest") return SelfTest();
        if (args[0] is "winscp") return WinScpCommand.Run(args.Skip(1).ToArray());
        if (args.Contains("--ftp")) return RunFtp(args);

        return CliCommands.Run(args);
    }

    // ---------------- FTP 橋接（要加 --ftp 才開） ----------------

    static int RunFtp(string[] args)
    {
        int ftpPort = 2221, vncPort = 5900;
        string bind = "127.0.0.1";
        string? target = null;
        bool readyStdout = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--ftp": break;
                    case "--port" when i + 1 < args.Length: ftpPort = int.Parse(args[++i]); break;
                    case "--vnc-port" when i + 1 < args.Length: vncPort = int.Parse(args[++i]); break;
                    case "--bind" when i + 1 < args.Length: bind = args[++i]; break;
                    case "--target" when i + 1 < args.Length: target = args[++i]; break;
                    case "--ready-stdout": readyStdout = true; break;
                    case "--log" when i + 1 < args.Length: Log.FilePath = args[++i]; break;
                    case "--verbose" or "-v": Log.Verbose = true; break;
                    default:
                        Console.Error.WriteLine($"FTP 模式不認得的參數：{args[i]}");
                        return 2;
                }
            }

            if (ftpPort is < 0 or > 65535 || vncPort is < 1 or > 65535)
                throw new ArgumentException("FTP 埠須為 0–65535，VNC 埠須為 1–65535（FTP 0 = 自動分配）。");
            if (target != null && (string.IsNullOrWhiteSpace(target) || Uri.CheckHostName(target) == UriHostNameType.Unknown))
                throw new ArgumentException("--target 須為機台 IP 或主機名稱，埠請用 --vnc-port 指定。");
            if (!IPAddress.TryParse(bind, out var bindAddress) || bindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !IPAddress.IsLoopback(bindAddress))
                throw new ArgumentException("FTP 橋接只允許綁定 IPv4 loopback 位址。");

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var proxy = Socks5Proxy.FromEnvironment();
            var server = new FtpServer(bindAddress, ftpPort, vncPort, target, proxy);
            server.Start();
            // 給 WinSCP 子程序的唯一 stdout 訊息；記錄統一走 stderr。
            if (readyStdout)
            {
                Console.WriteLine($"UVNCFTP/1 {server.Port}");
                Console.Out.Flush();
            }
            server.Run(cts.Token);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("用法錯誤：" + ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error("啟動失敗：" + ex.Message);
            if (ex is System.Net.Sockets.SocketException)
                Log.Error($"（通常是 {ftpPort} 埠已經被其他程式佔用，可用 --port 換一個埠。）");
            return 1;
        }
    }

    // ---------------- 自我測試 ----------------

    static int SelfTest()
    {
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {what}"); if (!ok) fail++; }

        // DES 經典測試向量（FIPS）：金鑰 133457799BBCDFF1、明文 0123456789ABCDEF → 85E813540F0AB405
        string got = Convert.ToHexString(Rfb.VncDes.EncryptBlock(
            Convert.FromHexString("133457799BBCDFF1"), Convert.FromHexString("0123456789ABCDEF")));
        Check(got == "85E813540F0AB405", $"DES 測試向量：{got}");

        // FTP 路徑轉換
        Check(PathMap.ToWindows("/") == null, "FTP / → 磁碟清單");
        Check(PathMap.ToWindows("/C:") == "C:\\", "FTP /C: → C:\\");
        Check(PathMap.ToWindows("/C:/MES/log.txt") == "C:\\MES\\log.txt", "FTP /C:/MES/log.txt → C:\\MES\\log.txt");
        Check(PathMap.Resolve("/C:/MES/sub", "../other/./x.txt") == "/C:/MES/other/x.txt", "FTP 相對路徑解析");

        // 命令列用的遠端路徑
        Check(Client.RemotePath.Normalize("c:/MES//Log/") == "C:\\MES\\Log", "正規化 c:/MES//Log/");
        Check(Client.RemotePath.Normalize("/C:/a.txt") == "C:\\a.txt", "正規化 /C:/a.txt");
        Check(Client.RemotePath.Normalize("D:") == "D:\\" && Client.RemotePath.Normalize("d") == "D:\\", "正規化磁碟 D: / d");
        Check(Client.RemotePath.Parent("C:\\MES\\Log") == "C:\\MES", "上一層 C:\\MES\\Log");
        Check(Client.RemotePath.Parent("C:\\MES") == "C:\\", "上一層 C:\\MES");
        Check(Client.RemotePath.Parent("C:\\") == "", "上一層 C:\\ → 磁碟清單");
        Check(Client.RemotePath.Combine("", "C:") == "C:\\", "Combine(磁碟清單, C:)");
        Check(Client.RemotePath.Combine("C:\\", "MES") == "C:\\MES", "Combine(C:\\, MES)");
        Check(Client.RemotePath.Name("C:\\MES\\a.csv") == "a.csv", "取檔名");
        Check(Client.RemotePath.Glob("*.csv").IsMatch("A_2026.CSV") && !Client.RemotePath.Glob("*.csv").IsMatch("a.txt"), "萬用字元 *.csv");
        Check(Client.RemoteSession.ParseHost("10.0.0.5:5901", 5900) == ("10.0.0.5", 5901), "解析 IP:埠");
        Check(Client.RemoteSession.ParseHost("10.0.0.5", 5900) == ("10.0.0.5", 5900), "解析 IP（預設埠）");

        // SOCKS5：用本機假代理檢查交握位元組
        Check(Socks5Proxy.Parse("127.0.0.1:1081") == new Socks5Proxy("127.0.0.1", 1081), "SOCKS5 解析 127.0.0.1:1081");
        Check(Throws<ArgumentException>(() => Socks5Proxy.Parse("127.0.0.1")), "SOCKS5 沒給埠要報錯");
        byte[] ipv4Request = [5, 1, 0, 1, 10, 1, 2, 3, 0x17, 0x0C];
        Check(Socks5Exchange("10.1.2.3", 5900, ipv4Request, [5, 0, 0, 3, 4, (byte)'j', (byte)'u', (byte)'m', (byte)'p', 0, 0], out string data) == null && data == "RFB",
            "SOCKS5 CONNECT IPv4，讀完網域格式的綁定位址後接著是目標資料");
        byte[] domainRequest = [5, 1, 0, 3, 3, (byte)'v', (byte)'n', (byte)'c', 0x17, 0x0C];
        Check(Socks5Exchange("vnc", 5900, domainRequest, [5, 0, 0, 1, 0, 0, 0, 0, 0, 0], out data) == null && data == "RFB",
            "SOCKS5 CONNECT 網域名稱交給代理解析");
        string? refused = Socks5Exchange("10.1.2.3", 5900, ipv4Request, [5, 5, 0, 1, 0, 0, 0, 0, 0, 0], out _);
        Check(refused?.Contains("回應碼 5") == true, "SOCKS5 代理拒絕時回報回應碼");
        string? noProxy = Socks5ConnectError(new Socks5Proxy("127.0.0.1", UnusedPort()));
        Check(noProxy?.StartsWith("連不上 SOCKS5 代理") == true, "SOCKS5 代理沒開時報錯");

        Console.WriteLine(fail == 0 ? "\n全部通過。" : $"\n有 {fail} 項失敗。");
        return fail == 0 ? 0 : 1;
    }

    static bool Throws<T>(Action act) where T : Exception
    {
        try { act(); return false; }
        catch (T) { return true; }
    }

    static int UnusedPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string? Socks5ConnectError(Socks5Proxy proxy)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        try { proxy.Connect(tcp, "10.1.2.3", 5900, 3000); return null; }
        catch (VncException ex) { return ex.Message; }
    }

    /// <summary>假 SOCKS5 代理：檢查客戶端送來的 CONNECT 請求，回 reply，成功時再送 "RFB" 當作目標資料。</summary>
    static string? Socks5Exchange(string host, int port, byte[] expectedRequest, byte[] reply, out string data)
    {
        data = "";
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        bool requestOk = false;
        var server = Task.Run(() =>
        {
            using var peer = listener.AcceptTcpClient();
            var s = peer.GetStream();
            byte[] greeting = new byte[3];
            s.ReadExactly(greeting);
            s.Write(new byte[] { 5, 0 });
            byte[] request = new byte[expectedRequest.Length];
            s.ReadExactly(request);
            requestOk = greeting.AsSpan().SequenceEqual(new byte[] { 5, 1, 0 }) && request.AsSpan().SequenceEqual(expectedRequest);
            s.Write(reply);
            if (reply[1] == 0) s.Write("RFB"u8);
        });
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            try { new Socks5Proxy("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port).Connect(tcp, host, port, 3000); }
            catch (VncException ex) { return ex.Message; }
            byte[] buf = new byte[3];
            tcp.GetStream().ReadExactly(buf);
            data = Encoding.ASCII.GetString(buf);
            server.Wait(3000);
            return requestOk ? null : "請求位元組不符";
        }
        finally { listener.Stop(); }
    }
}
