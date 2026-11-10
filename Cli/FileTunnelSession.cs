using System.Diagnostics;

namespace UvncFtpBridge.Cli;

/// <summary>Owns the local File-Tunnel SOCKS process for one WinSCP session.</summary>
internal sealed class FileTunnelSession : IDisposable
{
    public const int SocksPort = 1081;
    public const string SharedFolderName = "UvncFtpBridge";

    /// <summary>多跳板路由（mRemoteNG 主機模式）的資料夾：%TEMP%\UvncFtpBridge\Routes\&lt;路由&gt;。</summary>
    public const string RoutesFolderName = "Routes";

    readonly Process process;
    readonly string shared;
    readonly CounterpartStatus status;
    readonly ChildProcessJob? job;

    FileTunnelSession(Process process, string shared, CounterpartStatus status, ChildProcessJob? job)
    {
        this.process = process;
        this.shared = shared;
        this.status = status;
        this.job = job;
    }

    /// <summary>
    /// 清掉上一個通道主機沒收乾淨的 ft.exe（同一個工作階段、旁邊有 UvncFtpBridge.exe 的才算）。
    /// 只能在確定本機沒有其他通道主機時呼叫（Repeater 埠綁得上才算）。
    /// </summary>
    public static void KillOrphans()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var ft in Process.GetProcessesByName("ft"))
        {
            using (ft)
            {
                try
                {
                    if (ft.SessionId != session) continue;
                    string? path = ft.MainModule?.FileName;
                    string? dir = path == null ? null : Path.GetDirectoryName(path);
                    if (dir == null || !File.Exists(Path.Combine(dir, "UvncFtpBridge.exe"))) continue;
                    ft.Kill(entireProcessTree: true);
                    ft.WaitForExit(3000);
                    Log.Warn($"清掉殘留的 ft.exe（PID {ft.Id}，{path}）：上一個通道主機沒有正常結束，它會搶共用檔和 SOCKS 埠。");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 已經結束，或是別的帳號的程序：跳過
                }
            }
        }
    }

    /// <summary>跳板端已連上（ft.exe 回報 Counterpart: Online）。</summary>
    public bool IsOnline => status.IsOnline;

    public bool HasExited
    {
        get
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public static FileTunnelSession Start(string baseDir) =>
        Start(baseDir, Path.Combine(Path.GetTempPath(), SharedFolderName, "FileTunnel"), SocksPort, null);

    /// <summary>多跳板路由用：每條路由自己的資料夾與 SOCKS5 埠。</summary>
    public static string RouteFolder(string route) => Path.Combine(Path.GetTempPath(), SharedFolderName, RoutesFolderName, route);

    public static FileTunnelSession Start(string baseDir, string shared, int socksPort, string? route)
    {
        string executable = Path.Combine(baseDir, "ft.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 File-Tunnel：" + executable);
        string label = route == null ? "File-Tunnel" : $"File-Tunnel [{route}]";
        Directory.CreateDirectory(shared);
        string marker = Path.Combine(shared, "active.marker");
        WriteMarker(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = baseDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-D");
        start.ArgumentList.Add($"127.0.0.1:{socksPort}");
        start.ArgumentList.Add("--write");
        start.ArgumentList.Add(Path.Combine(shared, "to-remote.dat"));
        start.ArgumentList.Add("--read");
        start.ArgumentList.Add(Path.Combine(shared, "to-local.dat"));
        foreach (string option in ExtraOptions(baseDir)) start.ArgumentList.Add(option);
        // ft.exe has no console of its own here, so it writes in the system code page (e.g. Big5).
        start.StandardOutputEncoding = start.StandardErrorEncoding =
            System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        var process = new Process { StartInfo = start };
        var status = new CounterpartStatus(label);
        process.OutputDataReceived += (_, e) => { if (e.Data != null && !status.Swallow(e.Data)) Log.Info(label + ": " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null && !status.Swallow(e.Data)) Log.Warn(label + ": " + e.Data); };
        try
        {
            if (!process.Start()) throw new IOException("無法啟動 File-Tunnel。");
            var job = ChildProcessJob.Attach(process);
            if (job == null) Log.Warn($"{label}：無法把 ft.exe 綁到通道主機，主機異常結束時它可能殘留（下次啟動會清掉）。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.WaitForExit(500))
                throw new IOException($"{label} 啟動失敗（結束碼 {process.ExitCode}）。請查看 Logs\\launcher.log；確認本機 {socksPort} 埠未被佔用。");
            Log.Info($"{label} 已啟動；本機 SOCKS5 127.0.0.1:{socksPort}，共用資料夾 {shared}。等待你連接 RDP 並啟動跳板端。");
            return new FileTunnelSession(process, shared, status, job);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            Cleanup(shared);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        finally
        {
            job?.Dispose();
            process.Dispose();
            Cleanup(shared);
        }
    }

    /// <summary>
    /// Optional ft-options.txt beside the exe (local PC and jump host alike): extra ft.exe arguments for tuning,
    /// e.g. "--read-duration 200" or "--max-size 104857600". Whitespace-separated; lines starting with # are ignored.
    /// </summary>
    internal static IReadOnlyList<string> ExtraOptions(string baseDir)
    {
        string path = Path.Combine(baseDir, "ft-options.txt");
        if (!File.Exists(path)) return [];
        var options = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (options.Count > 0) Log.Info("ft.exe 額外參數（ft-options.txt）：" + string.Join(' ', options));
        return options;
    }

    /// <summary>ft.exe prints "Counterpart: Online/Offline" every second; only log when that state changes.</summary>
    sealed class CounterpartStatus(string label)
    {
        string? state;

        public bool IsOnline
        {
            get { lock (this) return state == "Online"; }
        }

        public bool Swallow(string line)
        {
            int at = line.IndexOf("Counterpart:", StringComparison.Ordinal);
            if (at < 0) return false;
            string now = line[(at + "Counterpart:".Length)..].TrimStart().Split(' ', 2)[0];
            lock (this)
            {
                if (now == state) return true;
                state = now;
            }
            if (now == "Online") Log.Info($"{label}：跳板端已連上。");
            else Log.Info($"{label}：跳板端未連線，等待跳板執行 Start-FileTunnel-Remote.cmd…");
            return true;
        }
    }

    // The jump-side script reads the marker every 500 ms over \\tsclient, which briefly locks it.
    static void WriteMarker(string marker, string content)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(marker, content);
                return;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
        }
    }

    static void Cleanup(string shared)
    {
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            try { File.Delete(Path.Combine(shared, "active.marker")); break; }
            catch (IOException) { Thread.Sleep(250); }
        }
        // 跳板端（UvncFtpJump）常駐、不看標記檔，RDP 還連著時它的 ft.exe 會一直開著這兩個檔：刪不掉就留著，
        // 下次開通道直接沿用，不要為了等它放手拖慢關閉。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                File.Delete(Path.Combine(shared, "to-remote.dat"));
                File.Delete(Path.Combine(shared, "to-local.dat"));
                Directory.Delete(shared);
                try { Directory.Delete(Path.GetDirectoryName(shared)!); }
                catch (IOException) { }
                return;
            }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
