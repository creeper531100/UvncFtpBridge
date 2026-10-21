using System.Diagnostics;
using System.Text.Json;
using UvncFtpBridge.Client;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 命令列模式。輸出規則（方便腳本 / AI 使用）：
///   成功結果 → stdout；進度與錯誤 → stderr；結束碼 0=成功、1=失敗、2=用法錯誤。
/// </summary>
public static class CliCommands
{
    sealed class Options
    {
        public List<string> Pos { get; } = [];
        public string? Password;
        public int VncPort = 5900;
        public bool Recursive, Json, Quiet;
    }

    public static int Run(string[] args)
    {
        string cmd = args[0].ToLowerInvariant();
        Options o;
        try { o = Parse(args.Skip(1).ToArray()); }
        catch (ArgumentException ex) { Console.Error.WriteLine("用法錯誤：" + ex.Message); return 2; }

        Socks5Proxy? proxy;
        try { proxy = Socks5Proxy.FromEnvironment(); }
        catch (ArgumentException ex) { Console.Error.WriteLine("用法錯誤：" + ex.Message); return 2; }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            return cmd switch
            {
                "ls" or "dir" => Need(o, 1, 2) ?? Ls(o, proxy),
                "get" => Need(o, 2, 3) ?? Get(o, cts.Token, proxy),
                "put" => Need(o, 3, 3) ?? Put(o, cts.Token, proxy),
                "rm" or "del" => Need(o, 2, 2) ?? Simple(o, proxy, (ft, a) => { ft.Delete(a[0]); return $"已刪除 {a[0]}"; }),
                "mkdir" => Need(o, 2, 2) ?? Simple(o, proxy, (ft, a) => { ft.MakeDirectory(a[0]); return $"已建立 {a[0]}"; }),
                "mv" or "ren" => Need(o, 3, 3) ?? Simple(o, proxy, (ft, a) => { ft.Rename(a[0], a[1]); return $"已改名 {a[0]} -> {a[1]}"; }),
                _ => Usage($"不認得的指令「{args[0]}」"),
            };
        }
        catch (OperationCanceledException) { ClearProgress(); Console.Error.WriteLine("已取消。"); return 1; }
        catch (VncException ex) { ClearProgress(); Console.Error.WriteLine("錯誤：" + ex.Message); return 1; }
        catch (IOException ex) { ClearProgress(); Console.Error.WriteLine("錯誤：" + ex.Message); return 1; }
        catch (UnauthorizedAccessException ex) { ClearProgress(); Console.Error.WriteLine("錯誤：" + ex.Message); return 1; }
    }

    static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "-p" or "--password":
                    if (++i >= a.Length) throw new ArgumentException("-p 後面要接密碼");
                    o.Password = a[i]; break;
                case "--vnc-port":
                    if (++i >= a.Length || !int.TryParse(a[i], out o.VncPort)) throw new ArgumentException("--vnc-port 後面要接數字");
                    break;
                case "-r" or "--recursive": o.Recursive = true; break;
                case "--json": o.Json = true; break;
                case "-q" or "--quiet": o.Quiet = true; break;
                case "-v" or "--verbose": Log.Verbose = true; break;
                case "--log":
                    if (++i >= a.Length) throw new ArgumentException("--log 後面要接檔名");
                    Log.FilePath = a[i]; break;
                default:
                    if (a[i].StartsWith('-') && a[i].Length > 1) throw new ArgumentException($"不認得的選項 {a[i]}");
                    o.Pos.Add(a[i]); break;
            }
        }
        return o;
    }

    static int? Need(Options o, int min, int max)
        => o.Pos.Count < min || o.Pos.Count > max ? Usage("參數數量不對") : null;

    static int Usage(string why)
    {
        Console.Error.WriteLine("用法錯誤：" + why);
        Console.Error.WriteLine("執行 UvncFtpBridge --help 看完整說明。");
        return 2;
    }

    // ---------------- 連線 ----------------

    static RemoteSession Open(Options o, Socks5Proxy? proxy)
    {
        string pw = o.Password ?? Environment.GetEnvironmentVariable("UVNC_PASSWORD") ?? PromptPassword();
        var s = new RemoteSession(o.Pos[0], o.VncPort, pw, proxy);
        s.EnsureConnected();
        return s;
    }

    static string PromptPassword()
    {
        if (Console.IsInputRedirected)
            throw new VncException("沒有提供密碼。請用 -p <密碼> 或設定環境變數 UVNC_PASSWORD。");
        Console.Error.Write("VNC 密碼：");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    // ---------------- ls ----------------

    static int Ls(Options o, Socks5Proxy? proxy)
    {
        using var s = Open(o, proxy);
        string path = RemotePath.Normalize(o.Pos.Count > 1 ? o.Pos[1] : "");
        string? pattern = null;
        if (RemotePath.HasWildcard(RemotePath.Name(path)))
        {
            pattern = RemotePath.Name(path);
            path = RemotePath.Parent(path);
        }

        var list = path == "" ? s.Ft.ListDrives() : s.Ft.ListDirectory(path);
        if (pattern != null)
        {
            var re = RemotePath.Glob(pattern);
            list = list.Where(e => re.IsMatch(e.Name)).ToList();
        }
        list = list.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (o.Json)
        {
            var rows = list.Select(e => new
            {
                name = e.Name,
                path = RemotePath.Combine(path, e.Name),
                type = path == "" ? "drive" : e.IsDirectory ? "dir" : "file",
                size = e.IsDirectory ? (long?)null : e.Size,
                modified = path == "" ? null : e.ModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        else
        {
            foreach (var e in list)
            {
                string type = e.IsDirectory ? "d" : "-";
                string size = e.IsDirectory ? "-" : e.Size.ToString();
                string time = path == "" ? "                " : e.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                Console.WriteLine($"{type} {size,14} {time}  {e.Name}");
            }
        }
        return 0;
    }

    // ---------------- get ----------------

    static int Get(Options o, CancellationToken ct, Socks5Proxy? proxy)
    {
        using var s = Open(o, proxy);
        string remoteRaw = o.Pos[1];
        string remote = RemotePath.Normalize(remoteRaw);
        string? localArg = o.Pos.Count > 2 ? o.Pos[2] : null;
        if (remote == "") throw new VncException("請指定要下載的遠端路徑。");

        // 1) 萬用字元：C:\MES\*.csv
        string leaf = RemotePath.Name(remote);
        if (RemotePath.HasWildcard(leaf))
        {
            string dir = RemotePath.Parent(remote);
            string localDir = localArg ?? ".";
            var re = RemotePath.Glob(leaf);
            var matches = s.Ft.ListDirectory(dir).Where(e => re.IsMatch(e.Name)).ToList();
            if (matches.Count == 0) throw new VncException($"「{remote}」沒有符合的檔案。");
            int n = 0;
            foreach (var e in matches)
            {
                string r = RemotePath.Combine(dir, e.Name);
                string l = Path.Combine(localDir, RemotePath.SafeLocalName(e.Name));
                if (e.IsDirectory)
                {
                    if (!o.Recursive) { Console.Error.WriteLine($"略過資料夾 {r}（要一起抓請加 -r）"); continue; }
                    n += Transfer.DownloadTree(s.Ft, r, l, Progress(o), f => { }, ct);
                }
                else
                {
                    Transfer.DownloadFile(s.Ft, r, l, e.Size, e.ModifiedUtc, Progress(o), ct);
                    Done($"已下載 {r} -> {Path.GetFullPath(l)} ({Transfer.FormatSize(e.Size)})");
                    n++;
                }
            }
            Console.WriteLine($"共 {n} 個檔案。");
            return 0;
        }

        // 2) 資料夾（-r）
        if (o.Recursive)
        {
            var st = Transfer.Stat(s.Ft, remote) ?? throw new VncException($"找不到「{remote}」。");
            if (st.IsDirectory)
            {
                string localDir = localArg ?? RemotePath.SafeLocalName(RemotePath.Name(remote));
                int n = Transfer.DownloadTree(s.Ft, remote, localDir, Progress(o),
                    f => { }, ct);
                ClearProgress();
                Console.WriteLine($"已下載資料夾 {remote} -> {Path.GetFullPath(localDir)}（共 {n} 個檔案）");
                return 0;
            }
        }

        // 3) 單一檔案：知道路徑就直接抓，不用先列目錄
        string localFile = ResolveLocalTarget(localArg, RemotePath.Name(remote));
        long size = -1;
        try
        {
            Transfer.DownloadFile(s.Ft, remote, localFile, size, null, Progress(o), ct);
        }
        catch (VncException) when (!ct.IsCancellationRequested)
        {
            // 給更清楚的錯誤：是不是其實是資料夾？
            s.Drop();
            var st = SafeStat(s, remote);
            if (st is { IsDirectory: true })
                throw new VncException($"「{remote}」是資料夾，要整個下載請加 -r。");
            throw;
        }
        long len = new FileInfo(localFile).Length;
        Done($"已下載 {remote} -> {localFile} ({Transfer.FormatSize(len)})");
        return 0;
    }

    static RemoteEntry? SafeStat(RemoteSession s, string remote)
    {
        try { return Transfer.Stat(s.Ft, remote); } catch { return null; }
    }

    /// <summary>本機目標：沒給 → 目前資料夾＋原檔名；給的是資料夾 → 裡面＋原檔名；否則就是那個檔名。</summary>
    static string ResolveLocalTarget(string? localArg, string remoteName)
    {
        string name = RemotePath.SafeLocalName(remoteName);
        if (string.IsNullOrEmpty(localArg)) return Path.GetFullPath(name);
        if (Directory.Exists(localArg) || localArg.EndsWith('\\') || localArg.EndsWith('/'))
            return Path.GetFullPath(Path.Combine(localArg, name));
        return Path.GetFullPath(localArg);
    }

    // ---------------- put ----------------

    static int Put(Options o, CancellationToken ct, Socks5Proxy? proxy)
    {
        using var s = Open(o, proxy);
        string localArg = o.Pos[1];
        string remoteRaw = o.Pos[2];
        string remote = RemotePath.Normalize(remoteRaw);
        if (remote == "") throw new VncException("遠端路徑不能是磁碟清單，請指定例如 C:\\temp\\");

        // 1) 本機萬用字元：D:\data\*.csv → 遠端當資料夾
        if (RemotePath.HasWildcard(Path.GetFileName(localArg)))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(localArg))!;
            var files = Directory.GetFiles(dir, Path.GetFileName(localArg));
            if (files.Length == 0) throw new VncException($"本機「{localArg}」沒有符合的檔案。");
            foreach (var f in files)
            {
                string r = RemotePath.Combine(remote, Path.GetFileName(f));
                Transfer.UploadFile(s.Ft, f, r, Progress(o), ct);
                Done($"已上傳 {f} -> {r}");
            }
            Console.WriteLine($"共 {files.Length} 個檔案。");
            return 0;
        }

        // 2) 本機資料夾
        if (Directory.Exists(localArg))
        {
            if (!o.Recursive) throw new VncException($"「{localArg}」是資料夾，要整個上傳請加 -r。");
            string target = RemotePath.LooksLikeDirectory(remoteRaw)
                ? RemotePath.Combine(remote, Path.GetFileName(Path.GetFullPath(localArg).TrimEnd('\\')))
                : remote;
            int n = Transfer.UploadTree(s.Ft, localArg, target, Progress(o), f => { }, ct);
            ClearProgress();
            Console.WriteLine($"已上傳資料夾 {Path.GetFullPath(localArg)} -> {target}（共 {n} 個檔案）");
            return 0;
        }

        // 3) 單一檔案
        if (!File.Exists(localArg)) throw new VncException($"找不到本機檔案「{localArg}」。");
        string dest = RemotePath.LooksLikeDirectory(remoteRaw) || RemotePath.IsDriveRoot(remote)
            ? RemotePath.Combine(remote, Path.GetFileName(localArg))
            : remote;
        Transfer.UploadFile(s.Ft, localArg, dest, Progress(o), ct);
        Done($"已上傳 {Path.GetFullPath(localArg)} -> {dest} ({Transfer.FormatSize(new FileInfo(localArg).Length)})");
        return 0;
    }

    // ---------------- rm / mkdir / mv ----------------

    static int Simple(Options o, Socks5Proxy? proxy, Func<UvncFileTransfer, string[], string> act)
    {
        using var s = Open(o, proxy);
        var a = o.Pos.Skip(1).Select(RemotePath.Normalize).ToArray();
        if (a.Any(p => p == "" || RemotePath.IsDriveRoot(p)))
            throw new VncException("不能對磁碟根目錄做這個操作。");
        Console.WriteLine(act(s.Ft, a));
        return 0;
    }

    // ---------------- 進度顯示 ----------------

    static readonly Stopwatch _tick = Stopwatch.StartNew();
    static long _lastTick = -1000;
    static bool _progressShown;

    static TransferProgress? Progress(Options o)
    {
        if (o.Quiet || Console.IsErrorRedirected) return null;
        return (path, done, total) =>
        {
            long now = _tick.ElapsedMilliseconds;
            if (now - _lastTick < 200 && (total < 0 || done < total)) return;
            _lastTick = now;
            string name = RemotePath.Name(path);
            if (name.Length > 40) name = "…" + name[^39..];
            string pct = total > 0 ? $" ({done * 100 / total}%)" : "";
            string tot = total > 0 ? " / " + Transfer.FormatSize(total) : "";
            string line = $"\r{name}  {Transfer.FormatSize(done)}{tot}{pct}";
            Console.Error.Write(line.PadRight(Math.Min(Console.BufferWidth - 1, 79)));
            _progressShown = true;
        };
    }

    static void ClearProgress()
    {
        if (!_progressShown) return;
        try { Console.Error.Write("\r" + new string(' ', Math.Min(Console.BufferWidth - 1, 79)) + "\r"); } catch { }
        _progressShown = false;
    }

    static void Done(string msg) { ClearProgress(); Console.WriteLine(msg); }

    // ---------------- 說明 ----------------

    public static void PrintHelp()
    {
        Console.WriteLine("""
        UltraVNC 檔案傳輸工具

        【命令列】（<機台> = IP 或 IP:埠，例如 192.168.0.10 或 192.168.0.10:5901）
          UvncFtpBridge ls    <機台> [遠端資料夾]            列目錄（不給路徑 = 列磁碟）
          UvncFtpBridge get   <機台> <遠端路徑> [本機路徑]    下載
          UvncFtpBridge put   <機台> <本機路徑> <遠端路徑>    上傳
          UvncFtpBridge rm    <機台> <遠端路徑>              刪除檔案或資料夾
          UvncFtpBridge mkdir <機台> <遠端資料夾>            建立資料夾
          UvncFtpBridge mv    <機台> <舊路徑> <新路徑>        改名／搬移

          共用選項：
            -p, --password <密碼>  VNC 密碼（也可以用環境變數 UVNC_PASSWORD；都沒有就會問）
            --vnc-port <埠>        預設的 VNC 埠（預設 5900）
            UVNC_SOCKS5_PROXY       設為 127.0.0.1:1080 時，VNC 連線經 SOCKS5 代理
            -r, --recursive        get/put 整個資料夾
            --json                 ls 用 JSON 輸出（給程式／AI 解析）
            -q, --quiet            不顯示進度
            -v, --verbose          顯示除錯訊息

          遠端路徑可寫 C:\MES\log.txt 或 C:/MES/log.txt，檔名可用萬用字元 * ?

          範例：
            UvncFtpBridge ls  192.168.0.10 C:\MES\Log
            UvncFtpBridge get 192.168.0.10 C:\MES\Log\20261020.csv D:\收集\
            UvncFtpBridge get 192.168.0.10 "C:\MES\Log\*.csv" D:\收集
            UvncFtpBridge get 192.168.0.10 C:\MES\Config D:\備份\Config -r
            UvncFtpBridge put 192.168.0.10 D:\新設定.ini C:\MES\Config\

          結束碼：0 = 成功、1 = 失敗、2 = 參數錯誤。結果印在 stdout，進度與錯誤印在 stderr。

        【FTP 橋接】（給 WinSCP 用，預設不開）
          UvncFtpBridge --ftp [--port 2221] [--bind 127.0.0.1] [--vnc-port 5900] [--log 檔案] [-v]
          --target <機台IP>        固定目標（搭配 --vnc-port），USER 不再指定目標。
          --port 0                 由作業系統分配本機 FTP 埠。
          --ready-stdout           啟動後 stdout 輸出 UVNCFTP/1 <實際埠>。
          UVNC_SOCKS5_PROXY       設為 127.0.0.1:1080 時，所有機台連線經 SOCKS5 代理。
          支援原版 WinSCP 的 USER %user@%host FTP 代理；目標由 WinSCP 主機欄指定。

        【可攜套件：WinSCP ＋ UltraVNC 一起開】
          UvncFtpBridge --open                 直連（Start-Direct.cmd）
          UvncFtpBridge --open --file-tunnel   經 RDP 跳板（Start-FileTunnel.cmd）
          開 FTP 橋接（給 WinSCP）與 UltraVNC Repeater（127.0.0.1:5999），再開好 WinSCP 與 UltraVNC Viewer。
          命令視窗開著就一直可用，關閉 WinSCP 或 Viewer 不影響；按 W／V 再開，按 Q 結束。
          直連時若設了 UVNC_SOCKS5_PROXY，則經該 SOCKS5 代理。--viewer 可指定其他 vncviewer.exe。

        【跳板端（RDP 跳板上執行）】
          UvncFtpBridge --remote [磁碟代號 | 完整重新導向路徑]
          從 \\tsclient\C 找本機啟動檔的共用資料夾，用旁邊的 ft.exe 接上；本機重開時自動重接。
          本機系統磁碟不是 C 時給磁碟代號，例如 --remote D。

        【VNC 遠端 PowerShell】（只有 VNC 埠的機台，跑指令、抓 log）
          UvncFtpBridge --rc [root@]<機台> -p <VNC密碼> [--admin] [--type] [--timeout 秒] [指令...]
            例: UvncFtpBridge --rc 192.168.1.45 -p 密碼                 互動提示字元
                UvncFtpBridge --rc 192.168.1.45 -p 密碼 "Get-Content C:\MES\log.txt -Tail 50"
                UvncFtpBridge --rc root@192.168.1.45 -p 密碼            系統管理員（機台跳 UAC，按「是」之前會一直等）
          互動模式就是一般命令列：← → 游標、Home/End、插入／刪除、↑ ↓ 與 F7 歷史、中文輸入都照常；
          Ctrl+C 中止正在跑的指令（例如 ping -t），exit 離開、cls 清畫面。
          貼上多行腳本（例如 AI 給的打包 log 指令）會整段一起執行，不會被拆成一行一行；
          跨行語法（foreach {}、here-string 等）會顯示 >> 續行，輸入空行送出。也可用 --script 檔.ps1 送整個檔。
          原理：機台上開一個常駐的 PowerShell 工作視窗（一般與系統管理員各一個），之後每條指令都在同一個視窗、
          同一份權限與工作目錄執行；指令與輸出都經檔案傳輸走 TEMP。第一條指令會冷啟動那個視窗（Win+R），
          之後都不用再動鍵盤。可走 UVNC_SOCKS5_PROXY 代理。機台須未鎖屏、有互動桌面，冷啟動才有效；剪貼簿被停用時加 --type。

        【其他】
          UvncFtpBridge --selftest   內建自我測試
          UvncFtpBridge --help       顯示這個說明
        """);
    }
}
