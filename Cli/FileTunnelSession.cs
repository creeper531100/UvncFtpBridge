using System.Diagnostics;

namespace UvncFtpBridge.Cli;

/// <summary>Owns the local File-Tunnel SOCKS process for one WinSCP session.</summary>
internal sealed class FileTunnelSession : IDisposable
{
    public const int SocksPort = 1081;
    public const string SharedFolderName = "UvncFtpBridge";

    readonly Process process;
    readonly string shared;

    FileTunnelSession(Process process, string shared)
    {
        this.process = process;
        this.shared = shared;
    }

    public static FileTunnelSession Start(string baseDir)
    {
        string executable = Path.Combine(baseDir, "ft.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到 File-Tunnel：" + executable);
        string shared = Path.Combine(Path.GetTempPath(), SharedFolderName, "FileTunnel");
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
        start.ArgumentList.Add($"127.0.0.1:{SocksPort}");
        start.ArgumentList.Add("--write");
        start.ArgumentList.Add(Path.Combine(shared, "to-remote.dat"));
        start.ArgumentList.Add("--read");
        start.ArgumentList.Add(Path.Combine(shared, "to-local.dat"));
        // ft.exe has no console of its own here, so it writes in the system code page (e.g. Big5).
        start.StandardOutputEncoding = start.StandardErrorEncoding =
            System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        var process = new Process { StartInfo = start };
        var status = new CounterpartStatus();
        process.OutputDataReceived += (_, e) => { if (e.Data != null && !status.Swallow(e.Data)) Log.Info("File-Tunnel: " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null && !status.Swallow(e.Data)) Log.Warn("File-Tunnel: " + e.Data); };
        try
        {
            if (!process.Start()) throw new IOException("無法啟動 File-Tunnel。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.WaitForExit(500))
                throw new IOException($"File-Tunnel 啟動失敗（結束碼 {process.ExitCode}）。請查看 Logs\\launcher.log；確認本機 1081 埠未被佔用。");
            Log.Info($"File-Tunnel 已啟動；本機 SOCKS5 127.0.0.1:{SocksPort}，共用資料夾 {shared}。等待你連接 RDP 並啟動跳板端。");
            return new FileTunnelSession(process, shared);
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
            process.Dispose();
            Cleanup(shared);
        }
    }

    /// <summary>ft.exe prints "Counterpart: Online/Offline" every second; only log when that state changes.</summary>
    sealed class CounterpartStatus
    {
        string? state;

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
            if (now == "Online") Log.Info("File-Tunnel：跳板端已連上。");
            else Log.Info("File-Tunnel：跳板端未連線，等待跳板執行 Start-FileTunnel-Remote.cmd…");
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
        // The jump-side process notices the marker disappear and releases its file handles.
        for (int attempt = 0; attempt < 8; attempt++)
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
