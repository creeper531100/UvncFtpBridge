using System.Diagnostics;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 方便包裝：UvncFtpBridge --ssh [ultravnc@]&lt;機台ip[:vncport]&gt; [-p 密碼] [指令...]
/// 自動呼叫系統的 ssh，並把 ProxyCommand 接到自己的 --ssh-stdio，省得每次打一長串。
/// 例：UvncFtpBridge --ssh ultravnc@192.168.1.45 "Get-Content C:\MES\log.txt"
///     UvncFtpBridge --ssh root@192.168.1.45      （系統管理員）
/// promptless：把 SSH_ASKPASS 指到自己（--askpass 會印假密碼），SSH 層本來就接受任何密碼。
/// </summary>
public static class SshLauncherCommand
{
    const string Usage =
        "用法: UvncFtpBridge --ssh [ultravnc@]<機台ip[:vncport]> [-p 密碼] [--vnc-port n] [--temp 路徑] [--type] [--timeout 秒] [指令...]\n" +
        "  自動呼叫 ssh 並接好 ProxyCommand。沒給指令就進互動提示字元。\n" +
        "  例: UvncFtpBridge --ssh ultravnc@192.168.1.45 \"hostname; Get-Date\"\n" +
        "      UvncFtpBridge --ssh root@192.168.1.45                （系統管理員，機台跳 UAC，按好之前會一直等）";

    public static int Run(string[] args)
    {
        string? userHost = null, password = null, tempDir = null;
        int? vncPort = null;
        int? timeoutSec = null;
        bool forceTyping = false;
        var remoteCmd = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (userHost == null && !a.StartsWith('-')) { userHost = a; continue; }
            if (userHost == null)
            {
                switch (a)
                {
                    case "-h" or "--help": Console.Error.WriteLine(Usage); return 0;
                    default: Console.Error.WriteLine($"請先給機台位址。\n{Usage}"); return 2;
                }
            }
            // 位址之後：先吃我們認得的選項，其餘全部當遠端指令往後傳
            switch (a)
            {
                case "-p" or "--password" when i + 1 < args.Length: password = args[++i]; break;
                case "--vnc-port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int vp): vncPort = vp; i++; break;
                case "--temp" when i + 1 < args.Length: tempDir = args[++i]; break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out int ts): timeoutSec = ts; i++; break;
                case "--type": forceTyping = true; break;
                default: remoteCmd.Add(a); break;
            }
        }

        if (userHost == null) { Console.Error.WriteLine(Usage); return 2; }
        password ??= Environment.GetEnvironmentVariable("UVNC_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("需要 VNC 密碼：用 -p 或環境變數 UVNC_PASSWORD。");
            return 2;
        }

        // 拆出 user 與 host；user 預設 ultravnc，只拿 host 給 --ssh-stdio 當目標。
        string user = "ultravnc", host = userHost;
        int at = userHost.IndexOf('@');
        if (at > 0) { user = userHost[..at]; host = userHost[(at + 1)..]; }

        string self = Environment.ProcessPath ?? "UvncFtpBridge.exe";
        string ssh = FindSsh();

        // ProxyCommand 的值：自己的 exe 路徑要用雙引號包（ssh 會再經 cmd 解析）。
        var proxy = new System.Text.StringBuilder();
        proxy.Append('"').Append(self).Append("\" --ssh-stdio ").Append(host).Append(" -p ").Append(password);
        if (vncPort is int p) proxy.Append(" --vnc-port ").Append(p);
        if (tempDir != null) proxy.Append(" --temp \"").Append(tempDir).Append('"');
        if (timeoutSec is int t) proxy.Append(" --timeout ").Append(t);
        if (forceTyping) proxy.Append(" --type");

        var psi = new ProcessStartInfo(ssh) { UseShellExecute = false };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ProxyCommand=" + proxy);
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("StrictHostKeyChecking=no");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("UserKnownHostsFile=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"));
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("PreferredAuthentications=password");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("PubkeyAuthentication=no");
        // FxSsh 30 秒收不到資料就斷線，指令跑久一點也要撐住；臨時主機金鑰的警告不用印
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=10");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("LogLevel=ERROR");
        // 不讀使用者的 ~/.ssh/config，免得那邊對同一個 IP 的設定（ProxyJump、User…）干擾
        psi.ArgumentList.Add("-F"); psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
        // 互動模式不要 pty：回顯、退格、上下鍵歷史交給本機主控台，伺服器端一行收一行
        if (remoteCmd.Count == 0) psi.ArgumentList.Add("-T");
        psi.ArgumentList.Add($"{user}@{host}");
        foreach (var c in remoteCmd) psi.ArgumentList.Add(c);

        // promptless：ssh 會呼叫 SSH_ASKPASS 取密碼；指到自己，帶 UVNC_ASKPASS=1 讓它印假密碼。
        // SSH 層本來就接受任何密碼，真正的門檻是機台 VNC 密碼（已放在 ProxyCommand 裡）。
        psi.Environment["SSH_ASKPASS"] = self;
        psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        psi.Environment["UVNC_ASKPASS"] = "1";

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { Console.Error.WriteLine("無法啟動 ssh。"); return 1; }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"啟動 ssh 失敗：{ex.Message}（找得到 ssh 嗎？{ssh}）");
            return 1;
        }
    }

    static string FindSsh()
    {
        // 先用可攜套件內附的 ssh（OpenSSH\ssh.exe 或 exe 旁邊的 ssh.exe），沒有才用系統的
        string baseDir = AppContext.BaseDirectory;
        foreach (string portable in new[] { Path.Combine(baseDir, "OpenSSH", "ssh.exe"), Path.Combine(baseDir, "ssh.exe") })
            if (File.Exists(portable)) return portable;
        if (OperatingSystem.IsWindows())
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string openssh = Path.Combine(sys, "OpenSSH", "ssh.exe");
            if (File.Exists(openssh)) return openssh;
        }
        return "ssh"; // 交給 PATH
    }
}
