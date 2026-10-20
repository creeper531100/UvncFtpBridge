using System.Net;
using System.Text;
using UvncFtpBridge.Cli;
using UvncFtpBridge.Ftp;

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
            var server = new FtpServer(bindAddress, ftpPort, vncPort, target);
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

        Console.WriteLine(fail == 0 ? "\n全部通過。" : $"\n有 {fail} 項失敗。");
        return fail == 0 ? 0 : 1;
    }
}
