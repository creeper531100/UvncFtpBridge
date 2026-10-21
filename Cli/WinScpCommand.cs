using System.Diagnostics;
using System.Net;
using System.Text;
using UvncFtpBridge.Ftp;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>CLI launcher for an unmodified portable WinSCP. No custom graphical interface.</summary>
public static class WinScpCommand
{
    public static int Run(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        try
        {
            string? script = null;
            bool fileTunnel = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--file-tunnel" && !fileTunnel) fileTunnel = true;
                else if (args[i] == "--script" && script == null && i + 1 < args.Length)
                    script = Path.GetFullPath(args[++i]);
                else throw new ArgumentException("用法：UvncFtpBridge winscp [--file-tunnel] [--script 檔案]");
            }
            // Interactive File-Tunnel use goes through the combined WinSCP + UltraVNC launcher.
            if (fileTunnel && script == null)
                return TunnelHost.Run(true, new Socks5Proxy("127.0.0.1", FileTunnelSession.SocksPort), null);

            EnsureInstalled(baseDir);
            if (script != null && !File.Exists(script)) throw new FileNotFoundException("找不到指令稿：" + script);
            Directory.CreateDirectory(Path.Combine(baseDir, "Logs"));
            Log.FilePath = Path.Combine(baseDir, "Logs", "launcher.log");

            // A single package owns its INI and bridge until its WinSCP process exits.
            using var instance = PackageInstance.TryAcquire(baseDir);
            if (instance == null)
            {
                if (!fileTunnel || !PackageInstance.TunnelHostRunning(baseDir))
                {
                    Console.Error.WriteLine("這份可攜套件已經開啟（WinSCP 或通道主機）。");
                    return 0;
                }
                // A script run can borrow the running tunnel host's bridge.
                ConfigureIni(baseDir, TunnelHost.WaitForFtpPort(baseDir));
                using var borrowed = StartWinScp(baseDir, script);
                borrowed.WaitForExit();
                return borrowed.ExitCode;
            }
            using var stop = new CancellationTokenSource();
            using var tunnel = fileTunnel ? FileTunnelSession.Start(baseDir) : null;
            var proxy = fileTunnel ? new Socks5Proxy("127.0.0.1", FileTunnelSession.SocksPort) : Socks5Proxy.FromEnvironment();
            var server = new FtpServer(IPAddress.Loopback, 0, 5900, proxy: proxy);
            server.Start();
            Task serve = Task.Run(() => server.Run(stop.Token));
            try
            {
                ConfigureIni(baseDir, server.Port);
                using var client = StartWinScp(baseDir, script);
                Log.Info("已啟動可攜 WinSCP；機台 IP 與 VNC 密碼請在 WinSCP 輸入。");
                client.WaitForExit();
                return client.ExitCode;
            }
            finally { stop.Cancel(); serve.GetAwaiter().GetResult(); }
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return ex is ArgumentException ? 2 : 1;
        }
    }

    static string Executable(string baseDir) => Path.Combine(baseDir, "WinSCP", "WinSCP.exe");

    static string IniPath(string baseDir) => Path.ChangeExtension(Executable(baseDir), ".ini");

    internal static void EnsureInstalled(string baseDir)
    {
        if (!File.Exists(Executable(baseDir))) throw new FileNotFoundException("找不到可攜版 WinSCP：" + Executable(baseDir));
        if (!File.Exists(IniPath(baseDir))) throw new FileNotFoundException("找不到可攜版預設設定：" + IniPath(baseDir));
    }

    internal static Process StartWinScp(string baseDir, string? script)
    {
        var start = new ProcessStartInfo(Executable(baseDir))
        {
            WorkingDirectory = Path.GetDirectoryName(Executable(baseDir))!,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("/newinstance");
        start.ArgumentList.Add("/ini=" + IniPath(baseDir));
        if (script != null)
        {
            start.ArgumentList.Add("/console");
            start.ArgumentList.Add("/nointeractiveinput");
            start.ArgumentList.Add("/script=" + script);
        }
        return Process.Start(start) ?? throw new IOException("無法啟動 WinSCP。");
    }

    internal static void ConfigureIni(string baseDir, int port) => WriteIni(IniPath(baseDir), port);

    static void WriteIni(string path, int port)
    {
        // Preserve saved sites and user preferences. Only the internal proxy endpoint changes.
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string section = line[1..^1];
                if (!sections.TryGetValue(section, out current))
                    sections[section] = current = new(StringComparer.OrdinalIgnoreCase);
            }
            else if (current != null && line.Length > 0 && line[0] is not (';' or '#'))
            {
                int equals = line.IndexOf('=');
                if (equals >= 0) current[line[..equals].Trim()] = line[(equals + 1)..];
            }
        }

        const string defaultSection = @"Sessions\Default%20Settings";
        if (!sections.TryGetValue(defaultSection, out var defaults))
            sections[defaultSection] = defaults = new(StringComparer.OrdinalIgnoreCase);
        defaults["FSProtocol"] = "5";
        defaults["PortNumber"] = "5900";
        defaults["UserName"] = "ultravnc";
        defaults["Ftps"] = "0";
        defaults["FtpPasvMode"] = "1";
        defaults["FtpProxyLogonType"] = "5";
        defaults["ProxyHost"] = "127.0.0.1";
        defaults["ProxyPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var (name, values) in sections)
        {
            if (!name.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase)) continue;
            if (values.GetValueOrDefault("FSProtocol", "5") == "5" &&
                values.GetValueOrDefault("FtpProxyLogonType", "5") == "5" &&
                values.GetValueOrDefault("ProxyHost", "127.0.0.1") == "127.0.0.1")
                values["ProxyPort"] = defaults["ProxyPort"];
        }

        var text = new StringBuilder();
        foreach (var (name, values) in sections)
        {
            text.Append('[').Append(name).AppendLine("]");
            foreach (var (key, value) in values) text.Append(key).Append('=').AppendLine(value);
            text.AppendLine();
        }
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(true));
        File.Move(temporary, path, true);
    }
}
