using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UvncFtpBridge.Client;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>OpenSSH over File-Tunnel. No SSH server or command interpreter lives in this program.</summary>
internal static class SshTunnelCommand
{
    public static int Run(string[] args)
    {
        string? target = null, password = Environment.GetEnvironmentVariable("UVNC_PASSWORD"), identity = null;
        int sshPort = 22;
        bool installedServer = false, admin = false;
        string? temporaryKey = null;
        var command = new List<string>();
        Process? localFt = null, relay = null;
        string? remote = null;
        RemoteSession? session = null;
        var children = new List<Process>();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-v": Log.Verbose = true; break;
                    case "-p" when i + 1 < args.Length: password = args[++i]; break;
                    case "--identity" when i + 1 < args.Length: identity = Path.GetFullPath(args[++i]); break;
                    case "--ssh-port" when i + 1 < args.Length: sshPort = int.Parse(args[++i]); installedServer = true; break;
                    case "--admin": admin = true; break;
                    default:
                        if (target == null) target = args[i];
                        else { command.AddRange(args.Skip(i)); i = args.Length; }
                        break;
                }
            }
            if (target == null || string.IsNullOrEmpty(password))
                throw new ArgumentException("用法：--ssh <Windows使用者@機台> -p <VNC密碼> [--admin] [--identity 私鑰] [指令]。自動部署可攜 sshd；--admin 跳 UAC 取得系統管理員權限；--ssh-port n 改用既有服務。");
            if (sshPort is < 1 or > 65535) throw new ArgumentException("SSH 埠必須介於 1 到 65535。");
            if (admin && installedServer) throw new ArgumentException("--admin 只用在可攜 sshd，不能和 --ssh-port 一起用。");
            int at = target.LastIndexOf('@');
            if (at <= 0 || at == target.Length - 1) throw new ArgumentException("請指定真正的 Windows 使用者@機台，例如 Ussr@192.168.1.45。");
            string user = target[..at], host = target[(at + 1)..];
            string baseDir = AppContext.BaseDirectory;
            string ssh = Path.Combine(baseDir, "OpenSSH", "ssh.exe"), ftExe = Path.Combine(baseDir, "ft.exe");
            if (!File.Exists(ssh) || !File.Exists(ftExe)) throw new FileNotFoundException("套件需要 OpenSSH\\ssh.exe 和 ft.exe；不會改用系統 ssh。");
            string id = Guid.NewGuid().ToString("N");
            // Not SshTunnel\<id>: when the target is this same PC that path is the remote folder.
            string local = Path.Combine(Path.GetTempPath(), "UvncFtpBridge", "SshClient", id);
            Directory.CreateDirectory(local);
            string? knownHosts = null;
            string? hostKeyAlias = null;
            if (!installedServer)
            {
                foreach (string name in PortableSshServer.Files)
                    if (!File.Exists(Path.Combine(baseDir, "OpenSSH", name))) throw new FileNotFoundException("缺少可攜 sshd 檔案：" + name);
                if (identity == null)
                {
                    identity = temporaryKey = Path.Combine(local, "client_key");
                    var keygen = new ProcessStartInfo(Path.Combine(baseDir, "OpenSSH", "ssh-keygen.exe")) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", identity }) keygen.ArgumentList.Add(arg);
                    using var generate = Process.Start(keygen)!;
                    generate.WaitForExit();
                    if (generate.ExitCode != 0) throw new IOException("無法產生臨時 SSH 金鑰。");
                }
                if (!File.Exists(identity + ".pub")) throw new FileNotFoundException("請提供私鑰旁的 .pub 公鑰檔，私鑰不會上傳。");
            }
            session = new RemoteSession(host, 5900, password, Socks5Proxy.FromEnvironment());
            var transfer = session.Ft;
            Console.Error.WriteLine("經 VNC 檔案傳輸找機台使用者的 %TEMP%…");
            string remoteTemp = FindRemoteTemp(transfer, user);
            Console.Error.WriteLine("機台 %TEMP%：" + remoteTemp);
            string remoteRoot = RemotePath.Combine(remoteTemp, "UvncFtpBridge");
            string remoteTunnel = RemotePath.Combine(remoteRoot, "SshTunnel");
            remote = RemotePath.Combine(remoteTunnel, id);
            // Creating a dedicated directory keeps concurrent sessions and the RDP File-Tunnel separate.
            try { transfer.MakeDirectory(remoteRoot); } catch (VncException) { }
            try { transfer.MakeDirectory(remoteTunnel); } catch (VncException) { }
            transfer.MakeDirectory(remote);
            void Upload(string name, byte[] bytes)
            {
                using var input = new MemoryStream(bytes, false);
                transfer.Upload(RemotePath.Combine(remote, name), input, bytes.Length, DateTime.UtcNow);
            }
            Console.Error.WriteLine("上傳 File-Tunnel 到機台；首次啟動需要可操作的桌面…");
            Upload("ft.exe", File.ReadAllBytes(ftExe));
            Upload("lease", new byte[] { 1 });
            if (!installedServer)
            {
                foreach (string name in PortableSshServer.Files) Upload(name, File.ReadAllBytes(Path.Combine(baseDir, "OpenSSH", name)));
                Upload("authorized_keys", File.ReadAllBytes(identity! + ".pub"));
            }
            string bootstrap = $$"""
                $ErrorActionPreference = 'Stop'
                $dir = $PSScriptRoot
                $p = Start-Process -FilePath "$dir\ft.exe" -ArgumentList ('--upload-download --tunnel-timeout 60000 --write "' + $dir + '\to-local.dat" --read "' + $dir + '\to-remote.dat"') -PassThru -NoNewWindow -WorkingDirectory $dir -RedirectStandardOutput "$dir\ft.log" -RedirectStandardError "$dir\ft.err"
                try {
                    while (-not $p.HasExited) {
                        Start-Sleep -Seconds 5
                        $lease = Get-Item -LiteralPath "$dir\lease" -ErrorAction SilentlyContinue
                        if (!$lease -or ((Get-Date).ToUniversalTime() - $lease.LastWriteTimeUtc).TotalSeconds -gt 120) { break }
                    }
                } finally { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
                """;
            if (!installedServer) bootstrap = PortableSshServer.Bootstrap(id);
            Upload("start.ps1", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(bootstrap)).ToArray());
            if (!installedServer) Upload("shell.ps1", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(PortableSshServer.ShellScript)).ToArray());
            bool elevate = admin;
            if (elevate)
            {
                string elevation = "New-Item -ItemType File -Path (Join-Path $PSScriptRoot 'launched') -Force | Out-Null\r\n" +
                                   "Start-Process powershell -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File \"' + (Join-Path $PSScriptRoot 'start.ps1') + '\"')";
                Upload("elevate.ps1", Encoding.UTF8.GetBytes(elevation));
            }
            // The exact folder that was uploaded; start.ps1 itself refuses to run if it is not the desktop user's %TEMP%.
            string launch = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{RemotePath.Combine(remote, elevate ? "elevate.ps1" : "start.ps1")}\"";
            Console.Error.WriteLine("在機台用 Win+R 開 cmd，逐字輸入啟動指令…");
            VncInput.RunViaCmd(session.Connection, launch);
            if (!installedServer && !WaitForRemoteFile(transfer, remote, "launched", TimeSpan.FromSeconds(25), cancellation.Token))
            {
                Console.Error.WriteLine("機台還沒執行到啟動指令，再試一次…");
                VncInput.RunViaCmd(session.Connection, launch);
                if (!WaitForRemoteFile(transfer, remote, "launched", TimeSpan.FromSeconds(35), cancellation.Token))
                    throw new TimeoutException("機台沒有執行啟動指令；請確認桌面未鎖定、可以用 Win+R 開 cmd，且輸入法不會干擾英數輸入。");
            }
            if (elevate) Console.Error.WriteLine("請到機台按 UAC 的「是」；按好之前會持續等待。");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); int localPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Process Start(string executable, IEnumerable<string> arguments, bool capture)
            {
                // Background helpers get their own stdin, or they compete with ssh.exe for console keystrokes.
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = capture,
                    RedirectStandardInput = capture, RedirectStandardOutput = capture, RedirectStandardError = capture, WorkingDirectory = local };
                foreach (string arg in arguments) start.ArgumentList.Add(arg);
                if (executable == Environment.ProcessPath) start.Environment["UVNC_PASSWORD"] = password;
                var process = new Process { StartInfo = start };
                if (capture)
                {
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Debug(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };
                }
                process.Start(); children.Add(process);
                if (capture) { process.StandardInput.Close(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); }
                return process;
            }
            relay = Start(Environment.ProcessPath!, new[] { "--vnc-file-relay", host, remote, local }, true);
            string ReadRemote(string name)
            {
                using var data = new MemoryStream(); transfer.Download(RemotePath.Combine(remote, name), data);
                return Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF').Trim();
            }
            if (!installedServer)
            {
                var startup = Stopwatch.StartNew();
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (relay.HasExited) throw new IOException("VNC 搬運中斷。");
                    var names = transfer.ListDirectory(remote).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (names.Contains("bootstrap.error")) throw new IOException(ReadRemote("bootstrap.error"));
                    if (names.Contains("server.ready")) break;
                    if (!elevate && startup.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("可攜 sshd 未啟動；請確認桌面、剪貼簿與 Win+R。");
                    Thread.Sleep(1000);
                }
                string[] readyLines = ReadRemote("server.ready").Replace("\r", "").Split('\n');
                sshPort = int.Parse(readyLines[0]);
                string actualUser = readyLines[1].Trim();
                if (!user.Equals(actualUser, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("可攜 sshd 只能登入目前桌面使用者：" + actualUser);
                user = actualUser;
                string[] hostKey = ReadRemote("host_key.pub").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (hostKey.Length < 2 || hostKey[0] != "ssh-ed25519") throw new IOException("機台主機公鑰格式錯誤。");
                Convert.FromBase64String(hostKey[1]);
                hostKeyAlias = "uvnc-session-" + id;
                knownHosts = Path.Combine(local, "known_hosts");
                File.WriteAllText(knownHosts, $"{hostKeyAlias} {hostKey[0]} {hostKey[1]}\n", Encoding.ASCII);
                Console.Error.WriteLine("可攜 sshd 已啟動，目前使用者：" + user);
            }
            localFt = Start(ftExe, new[] { "--upload-download", "--tunnel-timeout", "60000", "-L",
                $"127.0.0.1:{localPort}:127.0.0.1:{sshPort}", "--write", Path.Combine(local, "to-remote.dat"), "--read", Path.Combine(local, "to-local.dat") }, true);
            // ft initializes its listeners asynchronously; avoid an immediate connection-refused race.
            var ready = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(local, "to-local.dat")) ||
                   !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                       .GetActiveTcpListeners().Any(endpoint => endpoint.Port == localPort && IPAddress.IsLoopback(endpoint.Address)))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (relay.HasExited || localFt.HasExited) throw new IOException("File-Tunnel 啟動失敗。");
                if (ready.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("機台 File-Tunnel 未啟動；請確認桌面、剪貼簿與 Win+R。");
                Thread.Sleep(250);
            }
            Console.Error.WriteLine("正在經 VNC 建立 SSH 通道；SSH 帳密由機台的真正 sshd 驗證。");
            var sshArgs = new List<string> { "-F", "NUL", "-p", localPort.ToString(), "-o", "ConnectTimeout=120",
                "-o", $"HostKeyAlias={hostKeyAlias ?? host}", "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=6",
                "-o", "ProxyCommand=none", "-o", "ProxyJump=none" };
            if (identity != null) { sshArgs.Add("-i"); sshArgs.Add(identity); sshArgs.Add("-o"); sshArgs.Add("IdentitiesOnly=yes"); }
            if (knownHosts != null)
            {
                sshArgs.Add("-o"); sshArgs.Add("StrictHostKeyChecking=yes");
                sshArgs.Add("-o"); sshArgs.Add("UserKnownHostsFile=\"" + knownHosts.Replace('\\', '/') + "\"");
                sshArgs.Add("-o"); sshArgs.Add("BatchMode=yes");
            }
            else
            {
                sshArgs.Add("-o"); sshArgs.Add("UserKnownHostsFile=\"" + Path.Combine(local, "known_hosts").Replace('\\', '/') + "\"");
            }
            sshArgs.Add("-o"); sshArgs.Add("GlobalKnownHostsFile=NUL");
            sshArgs.Add("-o"); sshArgs.Add("UpdateHostKeys=no");
            sshArgs.Add(user + "@127.0.0.1"); sshArgs.AddRange(command);
            var client = Start(ssh, sshArgs, false);
            while (!client.WaitForExit(500))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (localFt.HasExited || relay.HasExited)
                    throw new IOException("File-Tunnel 或 VNC 搬運已結束，SSH 通道中斷。");
            }
            return client.ExitCode;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("連線已取消。"); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            foreach (var child in children.AsEnumerable().Reverse())
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(5000); } catch (InvalidOperationException) { }
                child.Dispose();
            }
            if (session != null && remote != null)
            {
                try { session.Ft.Delete(RemotePath.Combine(remote, "lease")); } catch { }
            }
            session?.Dispose();
            // Keys stay in the session folder; nothing is deleted unless the user does it.
            if (temporaryKey != null) Console.Error.WriteLine("本次金鑰保留在：" + Path.GetDirectoryName(temporaryKey));
        }
    }

    static bool WaitForRemoteFile(UvncFileTransfer ft, string dir, string name, TimeSpan timeout, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (ft.ListDirectory(dir).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    /// <summary>Finds &lt;drive&gt;:\Users\&lt;user&gt;\AppData\Local\Temp through VNC file transfer alone.</summary>
    static string FindRemoteTemp(UvncFileTransfer ft, string user)
    {
        foreach (var drive in ft.ListDrives())
        {
            string usersDir = RemotePath.Combine(drive.Name, "Users");
            List<RemoteEntry> profiles;
            try { profiles = ft.ListDirectory(usersDir); } catch (VncException) { continue; }
            // Domain or duplicate profiles look like "user.DOMAIN"; the exact name wins.
            var names = profiles.Where(e => e.IsDirectory)
                .Select(e => e.Name)
                .Where(n => n.Equals(user, StringComparison.OrdinalIgnoreCase) || n.StartsWith(user + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Length);
            foreach (string name in names)
            {
                string temp = RemotePath.Combine(RemotePath.Combine(usersDir, name), @"AppData\Local\Temp");
                try { ft.ListDirectory(temp); return temp; } catch (VncException) { }
            }
        }
        throw new IOException($"經 VNC 檔案傳輸找不到 {user} 的 AppData\\Local\\Temp；請確認帳號名稱。");
    }
}
