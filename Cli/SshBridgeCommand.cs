using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FxSsh;
using FxSsh.Services;
using UvncFtpBridge.Client;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// VNC-SSH 閘道：當 ssh 的 ProxyCommand 後端，走 stdin/stdout、不開任何 TCP 埠。
/// 使用者指令真的用產線 IP：
///   ssh ultravnc@&lt;產線ip&gt; -o ProxyCommand="UvncFtpBridge.exe --ssh-stdio %h -p &lt;vncpass&gt;"
/// SSH 層免驗證；RFB 連線用啟動時帶的 VNC 密碼，可走 UVNC_SOCKS5_PROXY 代理。
/// 收到的指令交給 <see cref="VncShell"/>：包成 ps1 上傳、Win+R 貼上執行、從 TEMP 抓回輸出。
/// </summary>
public static class SshBridgeCommand
{
    const string Usage =
        "用法: UvncFtpBridge --ssh-stdio <產線ip[:vncport]> [-p 密碼] [選項]\n" +
        "  走 stdio，給 ssh 的 ProxyCommand 用，不開監聽埠。\n" +
        "  -p, --password <pw>   VNC 密碼（否則用環境變數 UVNC_PASSWORD）\n" +
        "  --vnc-port <n>        VNC 埠（預設 5900，也可寫在 ip:port）\n" +
        "  --temp <path>         機台暫存資料夾（預設 C:\\Windows\\Temp\\uvncssh）\n" +
        "  --type                強制逐字打字（剪貼簿同步被停用時用）\n" +
        "  --keep                跑完不刪暫存檔\n" +
        "  --timeout <秒>        等指令跑完的上限（預設 120）\n" +
        "  --winr-ms <ms>        Win+R 後等待（預設 800）\n" +
        "SSH 層接受任何帳密（等同免驗證）；真正的門檻是機台的 VNC 密碼。\n" +
        "連線範例（新版 OpenSSH 不需額外 host-key 旗標）:\n" +
        "  ssh -o ProxyCommand=\"UvncFtpBridge.exe --ssh-stdio 192.168.0.10 -p pass\" \\\n" +
        "      -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL \\\n" +
        "      ultravnc@192.168.0.10 \"hostname; Get-Date\"\n" +
        "若 client 沒有可用金鑰會要密碼：隨便輸入都可以，或 promptless 餵假密碼：\n" +
        "  set SSH_ASKPASS=C:\\path\\echo-any.cmd & set SSH_ASKPASS_REQUIRE=force\n" +
        "  （echo-any.cmd 內容：@echo x）再照上面連線，PreferredAuthentications=password。";

    public static int Run(string[] args)
    {
        string? target = null, password = null, tempDir = null;
        int vncPort = 5900, timeoutSec = 120, winrMs = 800;
        bool forceTyping = false, keep = false;

        string? Next(ref int i, string flag)
        {
            if (i + 1 >= args.Length) { Log.Error($"{flag} 後面要接一個值。"); return null; }
            return args[++i];
        }

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-p" or "--password": if ((password = Next(ref i, a)) == null) return 2; break;
                case "--vnc-port": { var v = Next(ref i, a); if (v == null || !int.TryParse(v, out vncPort)) { Log.Error("--vnc-port 要是數字。"); return 2; } break; }
                case "--temp": if ((tempDir = Next(ref i, a)) == null) return 2; break;
                case "--type": forceTyping = true; break;
                case "--keep": keep = true; break;
                case "--timeout": { var v = Next(ref i, a); if (v == null || !int.TryParse(v, out timeoutSec)) { Log.Error("--timeout 要是秒數。"); return 2; } break; }
                case "--winr-ms": { var v = Next(ref i, a); if (v == null || !int.TryParse(v, out winrMs)) { Log.Error("--winr-ms 要是毫秒。"); return 2; } break; }
                case "-h" or "--help": Console.Error.WriteLine(Usage); return 0;
                default:
                    if (a.StartsWith('-')) { Log.Error($"未知的選項：{a}\n{Usage}"); return 2; }
                    if (target != null) { Log.Error($"只能指定一台機台：{Usage}"); return 2; }
                    target = a;
                    break;
            }
        }

        if (target == null) { Console.Error.WriteLine(Usage); return 2; }
        password ??= Environment.GetEnvironmentVariable("UVNC_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Log.Error("需要 VNC 密碼：用 -p 或環境變數 UVNC_PASSWORD（ProxyCommand 底下無法互動輸入）。");
            return 2;
        }

        var opt = new ShellOptions
        {
            TempDir = tempDir ?? @"C:\Windows\Temp\uvncssh",
            ForceTyping = forceTyping,
            KeepFiles = keep,
            TimeoutMs = timeoutSec * 1000,
            Timing = VncInput.InputTiming.Default with { AfterWinR = winrMs },
        };

        // 先連上機台，VNC 密碼錯／連不上就在 SSH 交握前明確報錯（ssh 會顯示 proxy command failed）。
        var session = new RemoteSession(target, vncPort, password, Socks5Proxy.FromEnvironment());
        try { session.EnsureConnected(); }
        catch (Exception ex) when (ex is VncException or IOException) { Log.Error("連不上機台 VNC：" + ex.Message); return 1; }

        try { return ServeOverStdio(session, opt); }
        finally { session.Dispose(); }
    }

    /// <summary>
    /// FxSsh 的 SshServer 以 TcpListener 為基礎。做法：讓它綁在 loopback 的隨機埠，
    /// 我們自己接一條 TcpClient 當橋，把 stdin/stdout 與這條內部連線雙向搬資料。
    /// 使用者完全看不到這個內部 127.0.0.1。
    /// </summary>
    static int ServeOverStdio(RemoteSession session, ShellOptions opt)
    {
        int port = FreeLoopbackPort();
        using var server = new SshServer(new StartingInfo(IPAddress.Loopback, port, "SSH-2.0-UvncFtpBridge"));
        // FxSsh 吃 PKCS#8 PEM，且 host-key 演算法名是 rsa-sha2-256/512（不是 ssh-rsa）。
        // rsa-sha2-* 是 SHA-2 簽章，新版 OpenSSH 預設就收，所以 client 端不需要 +ssh-rsa 相容旗標。
        // 金鑰每次啟動臨時產生（client 用 StrictHostKeyChecking=no，不需固定）。
        using (var rsa = RSA.Create(2048))
        {
            string pem = rsa.ExportPkcs8PrivateKeyPem();
            server.AddHostKey("rsa-sha2-256", pem);
            server.AddHostKey("rsa-sha2-512", pem);
        }

        var finished = new ManualResetEventSlim(false);
        server.ConnectionAccepted += (_, ssh) =>
        {
            ssh.ServiceRegistered += (_, svc) =>
            {
                switch (svc)
                {
                    case UserauthService ua:
                        // 接受任何密碼或任何金鑰（等同免驗證）。FxSsh 不支援 "none" 方法，
                        // 所以 client 端至少要走 password 或 publickey；密碼內容不檢查、金鑰只驗簽不比對授權清單。
                        // promptless 作法：用 SSH_ASKPASS 餵任意字當密碼（見 --help）。真正的門檻是機台的 VNC 密碼。
                        ua.Userauth += (_, e) => e.Result = true;
                        break;
                    case ConnectionService cs:
                        cs.PtyReceived += (_, _) => { };   // 接受 pty-req（互動 shell 會先要 PTY），不做事
                        cs.CommandOpened += (_, e) => OnCommand(e, session, opt);
                        break;
                }
            };
            ssh.Disconnected += (_, _) => finished.Set();
        };
        server.Start();

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var net = client.GetStream();
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // 任一方向結束（ssh client 或內部 server 關閉）就收工。
        var up = Pump(stdin, net);     // 真 ssh client → 內部 ssh server
        var down = Pump(net, stdout);  // 內部 ssh server → 真 ssh client
        Task.WaitAny(up, down);
        try { net.Close(); } catch { }
        finished.Wait(500);
        return 0;
    }

    static void OnCommand(CommandRequestedArgs e, RemoteSession session, ShellOptions opt)
    {
        var channel = e.Channel;
        string? command = e.ShellType == "exec" ? e.CommandText : null;
        bool interactive = e.ShellType == "shell";

        // 在工作執行緒跑，別卡住 FxSsh 的收訊迴圈。
        new Thread(() =>
        {
            try
            {
                // root@機台 = 用系統管理員 driver（機台會跳 UAC，按好之前一直等）
                string user = e.AttachedUserauthArgs?.Username ?? "ultravnc";
                bool admin = user.Equals("root", StringComparison.OrdinalIgnoreCase);
                if (interactive)
                {
                    VncInteractiveShell.Run(channel, session, opt, user, admin);
                    return;
                }
                if (string.IsNullOrWhiteSpace(command)) { channel.SendClose(0); return; }

                // ssh client 被 Ctrl+C 中斷（連線斷掉）時，順手把機台上正在跑的指令也中止。
                // 注意：exec 的 stdin 很快就 EOF，不能拿 EOF 當取消，只認 channel 真的關閉。
                using var cts = new CancellationTokenSource();
                channel.CloseReceived += (_, _) => { try { cts.Cancel(); } catch { } };

                var r = VncShell.RunCommand(session, command, opt, admin,
                    status: msg => { try { channel.SendData(Encoding.UTF8.GetBytes(msg.Replace("\n", "\r\n"))); } catch { } },
                    onOutput: chunk => { try { channel.SendData(Encoding.UTF8.GetBytes(chunk)); } catch { } },  // 原樣送（Out-String 已含換行）
                    ct: cts.Token);
                if (r.Output.Length > 0) channel.SendData(Encoding.UTF8.GetBytes(r.Output));  // 有串流時 Output 為空，不會重複
                uint code = r.Canceled ? 130u : r.TimedOut ? 124u : r.ExitCode < 0 ? 255u : (uint)r.ExitCode;
                channel.SendClose(code);
            }
            catch (Exception ex)
            {
                try
                {
                    channel.SendData(Encoding.UTF8.GetBytes((ex is VncException ? "VNC 錯誤：" : "錯誤：") + ex.Message + "\r\n"));
                    channel.SendClose(1);
                }
                catch { }
            }
        })
        { IsBackground = true }.Start();
    }

    static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static Task Pump(Stream from, Stream to) => Task.Run(() =>
    {
        var buf = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                to.Write(buf, 0, n);
                to.Flush();
            }
        }
        catch { /* 連線關閉 */ }
    });
}
