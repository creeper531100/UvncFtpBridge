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
        File.WriteAllText(marker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Info("File-Tunnel: " + e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Warn("File-Tunnel: " + e.Data); };
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

    static void Cleanup(string shared)
    {
        try { File.Delete(Path.Combine(shared, "active.marker")); }
        catch (IOException) { }
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
