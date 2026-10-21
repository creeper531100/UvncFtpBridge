using System.Text;
using UvncFtpBridge.Client;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 只有 VNC 埠的機台上跑 PowerShell 指令：UvncFtpBridge --ssh [root@]&lt;機台&gt; -p 密碼 [指令...]
/// 不經過 ssh，直接在本程序裡把指令交給 <see cref="VncShell"/>（機台常駐 driver），輸出邊跑邊印。
/// 互動模式用本機主控台的 Console.ReadLine 讀指令，所以 ← → 游標、插入／刪除、↑ ↓ 與 F7 歷史、
/// 中文輸入法、折行都由 Windows 主控台自己處理，不用自己寫行編輯器。Ctrl+C 中止機台上正在跑的指令。
/// </summary>
public static class VncShellCommand
{
    const string Usage =
        "用法: UvncFtpBridge --ssh [root@]<機台ip[:vncport]> -p <VNC密碼> [選項] [指令...]\n" +
        "  沒給指令就進互動提示字元；給了就跑一次、印出輸出，以指令的 exit code 結束。\n" +
        "  --admin               系統管理員（同 root@機台；機台跳 UAC，按「是」之前會一直等）\n" +
        "  --vnc-port <n>        VNC 埠（預設 5900，也可寫在 ip:port）\n" +
        "  --temp <路徑>         機台暫存資料夾（預設 C:\\Windows\\Temp\\uvncssh）\n" +
        "  --type                冷啟動改用逐字打字（剪貼簿同步被停用時用）\n" +
        "  --timeout <秒>        單條指令的上限（給指令時預設 120；互動模式預設不限，用 Ctrl+C 中止）\n" +
        "  --winr-ms <ms>        Win+R 後等待（預設 800）\n" +
        "  --keep                跑完不刪機台上的暫存檔（除錯用）\n" +
        "  例: UvncFtpBridge --ssh 192.168.1.45 -p 密碼\n" +
        "      UvncFtpBridge --ssh 192.168.1.45 -p 密碼 \"Get-Content C:\\MES\\log.txt -Tail 50\"\n" +
        "      UvncFtpBridge --ssh root@192.168.1.45 -p 密碼";

    public static int Run(string[] args)
    {
        string? target = null, password = null, tempDir = null;
        int vncPort = 5900, winrMs = 800;
        int? timeoutSec = null;
        bool admin = false, forceTyping = false, keep = false;
        var command = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (a)
            {
                case "-h" or "--help": Console.Error.WriteLine(Usage); return 0;
                case "-p" or "--password" when hasValue: password = args[++i]; break;
                case "--vnc-port" when hasValue && int.TryParse(args[i + 1], out int vp): vncPort = vp; i++; break;
                case "--temp" when hasValue: tempDir = args[++i]; break;
                case "--timeout" when hasValue && int.TryParse(args[i + 1], out int ts): timeoutSec = ts; i++; break;
                case "--winr-ms" when hasValue && int.TryParse(args[i + 1], out int wr): winrMs = wr; i++; break;
                case "--admin": admin = true; break;
                case "--type": forceTyping = true; break;
                case "--keep": keep = true; break;
                case "-v" or "--verbose": Log.Verbose = true; break;
                default:
                    if (target == null && !a.StartsWith('-')) { target = a; break; }
                    if (target == null) { Console.Error.WriteLine($"不認得的選項：{a}\n{Usage}"); return 2; }
                    command.Add(a);   // 機台位址之後不認得的全部當指令
                    break;
            }
        }

        if (target == null) { Console.Error.WriteLine(Usage); return 2; }
        password ??= Environment.GetEnvironmentVariable("UVNC_PASSWORD");
        if (string.IsNullOrEmpty(password)) { Console.Error.WriteLine("需要 VNC 密碼：用 -p 或環境變數 UVNC_PASSWORD。"); return 2; }

        // 相容舊寫法 ultravnc@機台 / root@機台：只有 root 有意義（= --admin）
        int at = target.IndexOf('@');
        if (at >= 0)
        {
            if (target[..at].Equals("root", StringComparison.OrdinalIgnoreCase)) admin = true;
            target = target[(at + 1)..];
        }

        bool interactive = command.Count == 0;
        var opt = new ShellOptions
        {
            TempDir = tempDir ?? @"C:\Windows\Temp\uvncssh",
            ForceTyping = forceTyping,
            KeepFiles = keep,
            TimeoutMs = timeoutSec is int t ? t * 1000 : interactive ? int.MaxValue : 120_000,
            Timing = VncInput.InputTiming.Default with { AfterWinR = winrMs },
        };

        using var session = new RemoteSession(target, vncPort, password, Socks5Proxy.FromEnvironment());
        try { session.EnsureConnected(); }
        catch (Exception ex) when (ex is VncException or IOException) { Console.Error.WriteLine("連不上機台 VNC：" + ex.Message); return 1; }

        // Ctrl+C：有指令在跑就中止它，沒有就只是打斷這一行輸入；不結束本程式
        var gate = new object();
        CancellationTokenSource? running = null;
        var interrupted = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            lock (gate)
            {
                if (running != null) { try { running.Cancel(); } catch (ObjectDisposedException) { } }
                else interrupted.Set();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            // stream=false 時輸出不印，留在結果的 Output 裡（初始化要抓主機名）
            ShellResult Execute(string cmd, bool stream)
            {
                var cts = new CancellationTokenSource();
                lock (gate) running = cts;
                try
                {
                    var r = VncShell.RunCommand(session, cmd, opt, admin, status: msg => Console.Error.Write(msg),
                        onOutput: stream ? chunk => Console.Out.Write(chunk) : null, ct: cts.Token);
                    if (r.TimedOut) Console.Error.WriteLine(r.Output);   // 逾時提示
                    else if (stream && r.Output.Length > 0) Console.Out.Write(r.Output);
                    return r;
                }
                finally { lock (gate) running = null; cts.Dispose(); }
            }

            if (!interactive)
            {
                var r = Execute(string.Join(' ', command), stream: true);
                return r.Canceled ? 130 : r.TimedOut ? 124 : r.ExitCode < 0 ? 255 : r.ExitCode;
            }
            return Interactive(session, opt, admin, Execute, interrupted);
        }
        catch (Exception ex) when (ex is VncException or IOException)
        {
            Console.Error.WriteLine("VNC 錯誤：" + ex.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    static int Interactive(RemoteSession session, ShellOptions opt, bool admin,
        Func<string, bool, ShellResult> execute, ManualResetEventSlim interrupted)
    {
        // UTF-16 輸入讓 .NET 改用 ReadConsoleW：中文輸入法打的字才不會在 UTF-8 碼頁下讀成空白
        if (!Console.IsInputRedirected) try { Console.InputEncoding = Encoding.Unicode; } catch (IOException) { }
        void Status(string msg) => Console.Error.Write(msg);

        Status(admin ? "以系統管理員身分連線（機台第一次會跳 UAC）…\n" : "連線中；首次會在機台開常駐的 PowerShell 視窗，請稍候…\n");
        var init = execute("Set-Location $HOME; $env:COMPUTERNAME", false);
        if (init.TimedOut || init.Canceled) return init.Canceled ? 130 : 124;
        string host = init.Output.Trim() is { Length: > 0 } name ? name : session.Host;
        string cwd = init.Cwd ?? "";
        try { Console.Title = $"VNC Shell - {host}{(admin ? " (管理員)" : "")}"; } catch (IOException) { }
        Status("已連上 " + host + "。exit 離開、cls 清畫面、Ctrl+C 中止正在跑的指令；← → ↑ ↓ F7 照一般命令列使用。\n");

        while (true)
        {
            WritePrompt(host, cwd, admin);
            interrupted.Reset();
            string? line = Console.ReadLine();
            if (line == null)
            {
                // Ctrl+C 會讓 ReadLine 回 null；處理常式在另一條執行緒，稍等一下再判斷是不是真的 EOF（Ctrl+Z）
                if (interrupted.Wait(200)) { Console.WriteLine(); continue; }
                Console.WriteLine();
                return 0;
            }
            string cmd = line.Trim();
            if (cmd.Length == 0) continue;
            if (cmd is "exit" or "logout" or "quit") return 0;
            if (cmd is "cls" or "clear") { try { Console.Clear(); } catch (IOException) { } continue; }

            var r = execute(cmd, true);
            if (r.Cwd != null) cwd = r.Cwd;
            if (r.Canceled) Status("^C 已中止\n");
        }
    }

    static void WritePrompt(string host, string cwd, bool admin)
    {
        if (Console.IsOutputRedirected) { Console.Write($"{host} {cwd}> "); return; }
        // 指令輸出最後沒換行時，提示字元另起一行
        try { if (Console.CursorLeft > 0) Console.WriteLine(); } catch (IOException) { }
        var old = Console.ForegroundColor;
        if (admin) { Console.ForegroundColor = ConsoleColor.Red; Console.Write("[管理員] "); }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(host);
        Console.ForegroundColor = old;
        Console.Write($" {cwd}> ");
    }
}
