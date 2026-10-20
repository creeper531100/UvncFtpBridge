using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using UvncFtpBridge.Ftp;

namespace UvncFtpBridge.Cli;

/// <summary>CLI launcher for an unmodified portable WinSCP. No custom graphical interface.</summary>
public static class WinScpCommand
{
    public static int Run(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string executable = Path.Combine(baseDir, "WinSCP", "WinSCP.exe");
        string iniPath = Path.ChangeExtension(executable, ".ini");
        try
        {
            string? script = null;
            if (args.Length == 2 && args[0] == "--script") script = Path.GetFullPath(args[1]);
            else if (args.Length != 0) throw new ArgumentException("用法：UvncFtpBridge winscp [--script 檔案]");
            if (!File.Exists(executable)) throw new FileNotFoundException("找不到可攜版 WinSCP：" + executable);
            if (!File.Exists(iniPath)) throw new FileNotFoundException("找不到可攜版預設設定：" + iniPath);
            if (script != null && !File.Exists(script)) throw new FileNotFoundException("找不到指令稿：" + script);

            // A single package owns its INI and bridge until its WinSCP process exits.
            string packageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir.ToUpperInvariant())));
            using var instance = new Mutex(false, @"Local\UvncFtpBridge.Portable." + packageId);
            bool acquired;
            try { acquired = instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { Console.Error.WriteLine("這份可攜 WinSCP 已經開啟。"); return 0; }
            try
            {
                Directory.CreateDirectory(Path.Combine(baseDir, "Logs"));
                Log.FilePath = Path.Combine(baseDir, "Logs", "launcher.log");
                using var stop = new CancellationTokenSource();
                var server = new FtpServer(IPAddress.Loopback, 0, 5900);
                server.Start();
                Task serve = Task.Run(() => server.Run(stop.Token));
                try
                {
                    ConfigureIni(iniPath, server.Port);
                    var start = new ProcessStartInfo(executable)
                    {
                        WorkingDirectory = Path.GetDirectoryName(executable)!,
                        UseShellExecute = false,
                    };
                    start.ArgumentList.Add("/newinstance");
                    start.ArgumentList.Add("/ini=" + iniPath);
                    if (script != null)
                    {
                        start.ArgumentList.Add("/console");
                        start.ArgumentList.Add("/nointeractiveinput");
                        start.ArgumentList.Add("/script=" + script);
                    }
                    using var client = Process.Start(start) ?? throw new IOException("無法啟動 WinSCP。");
                    Log.Info("已啟動可攜 WinSCP；機台 IP 與 VNC 密碼請在 WinSCP 輸入。");
                    client.WaitForExit();
                    return client.ExitCode;
                }
                finally { stop.Cancel(); serve.GetAwaiter().GetResult(); }
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return ex is ArgumentException ? 2 : 1;
        }
    }

    static void ConfigureIni(string path, int port)
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
