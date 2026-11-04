using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UvncFtpBridge.Cli;

/// <summary>
/// Jump-host side of the File-Tunnel: finds the local launcher's shared folder through RDP drive
/// redirection and runs ft.exe against it, restarting whenever the local side restarts.
/// Replaces Start-FileTunnel-Remote.ps1, which needs a newer PowerShell than Windows 7 ships with.
/// Messages stay in English: older consoles may not render CJK text.
/// </summary>
public static class RemoteCommand
{
    const string Usage = "Usage: UvncFtpBridge --remote [drive letter | full redirected TEMP path] [--route <jump host name>]\n" +
                         "  e.g. UvncFtpBridge --remote D\n" +
                         "       UvncFtpBridge --remote \"\\\\tsclient\\C\\MyTemp\\UvncFtpBridge\\FileTunnel\"\n" +
                         "       UvncFtpBridge --remote --route 10.6.0.11   (mRemoteNG multi jump host tunnels)";

    enum MarkerState { Read, Gone, Unknown }

    public static int Run(string[] args)
    {
        string ft = Path.Combine(AppContext.BaseDirectory, "ft.exe");
        if (!File.Exists(ft))
        {
            Console.Error.WriteLine("File-Tunnel remote error: place ft.exe beside UvncFtpBridge.exe.");
            return 1;
        }
        // --route <name>：mRemoteNG 主機模式的多跳板通道，指定這台跳板對應的路由（預設依本機名稱／IP 自動比對）
        string? routeArg = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--route" && routeArg == null && i + 1 < args.Length) routeArg = args[++i];
            else positional.Add(args[i]);
        }
        if (positional.Count > 1 || (routeArg != null && !Rfb.Route.IsValidId(routeArg)))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string target = positional.Count == 1 ? positional[0] : "C";
        bool explicitPath = Path.IsPathRooted(target) && target.Length > 2;
        string? profiles = null;
        if (!explicitPath)
        {
            string drive = target.TrimEnd(':');
            if (drive.Length != 1 || !char.IsAsciiLetter(drive[0]))
            {
                Console.Error.WriteLine("Pass a drive letter such as C, or the complete redirected TEMP path.\n" + Usage);
                return 2;
            }
            profiles = $@"\\tsclient\{drive}\Users";
        }

        // ft.exe shares this console, but kill it explicitly so a closed window never leaves it running.
        Process? current = null;
        var gate = new object();
        void StopFt()
        {
            lock (gate)
            {
                try { if (current is { HasExited: false }) current.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
        using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => StopFt());
        using var onCtrlC = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => StopFt());
        using var onBreak = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, _ => StopFt());
        using var onLogoff = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => StopFt());

        Log.Silent = true; // ft-options.txt is reported in English below; Log messages are for the local side
        var extraOptions = FileTunnelSession.ExtraOptions(AppContext.BaseDirectory);
        Log.Silent = false;
        if (extraOptions.Count > 0) Console.WriteLine("Extra ft.exe options (ft-options.txt): " + string.Join(' ', extraOptions));
        Console.WriteLine("Waiting for the local File-Tunnel launcher. Close this window or press Ctrl+C to stop.");
        // Each pass serves one local launcher session; when it ends, go back to waiting for the next one.
        while (true)
        {
            string shared = WaitForLocalTunnel(explicitPath ? target : null, profiles, routeArg);
            string marker = Path.Combine(shared, "active.marker");
            string? session = null;
            for (int attempt = 0; session == null && attempt < 10; attempt++)
            {
                var (state, content) = ReadMarker(marker);
                if (state == MarkerState.Read) session = content;
                else Thread.Sleep(200);
            }
            if (session == null) continue;

            Console.WriteLine();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Connected to local tunnel: {shared}");
            Console.WriteLine("Keep this window open while using WinSCP or UltraVNC.");
            var start = new ProcessStartInfo(ft) { UseShellExecute = false };
            start.ArgumentList.Add("--read");
            start.ArgumentList.Add(Path.Combine(shared, "to-remote.dat"));
            start.ArgumentList.Add("--write");
            start.ArgumentList.Add(Path.Combine(shared, "to-local.dat"));
            foreach (string option in extraOptions) start.ArgumentList.Add(option);
            using (var process = Process.Start(start) ?? throw new IOException("Could not start ft.exe."))
            {
                lock (gate) current = process;
                int unknown = 0;
                while (!process.HasExited)
                {
                    // The marker disappears when the local launcher closes (or RDP drops); different
                    // content means the local side already restarted with fresh tunnel files.
                    var (state, content) = ReadMarker(marker);
                    if (state == MarkerState.Gone || (state == MarkerState.Read && content != session)) break;
                    unknown = state == MarkerState.Unknown ? unknown + 1 : 0;
                    if (unknown >= 10) break;
                    Thread.Sleep(500);
                }
                if (process.HasExited)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ft.exe exited (code {process.ExitCode}).");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Local tunnel closed.");
                    StopFt();
                    process.WaitForExit(5000);
                }
                lock (gate) current = null;
            }
            Console.WriteLine("Waiting for the local File-Tunnel launcher again.");
            Thread.Sleep(2000);
        }
    }

    static string WaitForLocalTunnel(string? explicitPath, string? profiles, string? routeArg)
    {
        string lastStatus = "";
        var identities = routeArg == null ? OwnIdentities() : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { routeArg };
        if (routeArg == null && explicitPath == null)
            Console.WriteLine("mRemoteNG jump host tunnels for this machine: " + string.Join(", ", identities.Order()));
        while (true)
        {
            string status;
            if (explicitPath != null)
            {
                if (File.Exists(Path.Combine(explicitPath, "active.marker"))) return explicitPath;
                status = $"Waiting for local tunnel at {explicitPath}";
            }
            else if (!Directory.Exists(profiles))
            {
                status = $"Waiting for {profiles}. Enable drive redirection in mstsc and reconnect.";
            }
            else
            {
                var found = new List<string>();
                var otherRoutes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string user in Directory.EnumerateDirectories(profiles!))
                    {
                        string bridgeTemp = Path.Combine(user, @"AppData\Local\Temp\UvncFtpBridge");
                        // The single File-Tunnel of Start-FileTunnel.cmd (unchanged); skipped when --route picks one.
                        if (routeArg == null)
                        {
                            string candidate = Path.Combine(bridgeTemp, "FileTunnel");
                            try { if (File.Exists(Path.Combine(candidate, "active.marker"))) found.Add(candidate); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                        // mRemoteNG host mode: one folder per jump host, named after the jump host's name or IP.
                        string routesDir = Path.Combine(bridgeTemp, FileTunnelSession.RoutesFolderName);
                        try
                        {
                            if (!Directory.Exists(routesDir)) continue;
                            foreach (string routeDir in Directory.EnumerateDirectories(routesDir))
                            {
                                if (!File.Exists(Path.Combine(routeDir, "active.marker"))) continue;
                                string name = Path.GetFileName(routeDir);
                                if (identities.Contains(name)) found.Add(routeDir);
                                else otherRoutes.Add(name);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (found.Count == 1) return found[0];
                if (found.Count > 1)
                    status = $"Multiple active local TEMP tunnels found under {profiles}. Close the extra ones, or restart with the complete redirected TEMP path or --route.";
                else if (otherRoutes.Count > 0)
                    status = $"Waiting for this jump host's tunnel under {profiles}. Found tunnels for other jump hosts: {string.Join(", ", otherRoutes)}.\n" +
                             "If one of them is this machine (e.g. mRemoteNG connects to it by another name or a NAT address), restart with --route <name>.";
                else
                    status = $"Waiting for local tunnel under {profiles}. Start the File-Tunnel launcher on the local PC.";
            }
            if (status != lastStatus)
            {
                Console.WriteLine(status);
                lastStatus = status;
            }
            Thread.Sleep(2000);
        }
    }

    /// <summary>
    /// Route names this jump host answers to: its computer name, DNS host name / FQDN and every local IP,
    /// so the folder mRemoteNG names after the jump connection's host (name or IP) is found automatically.
    /// </summary>
    static HashSet<string> OwnIdentities()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string id = Rfb.Route.Sanitize(value);
            if (Rfb.Route.IsValidId(id)) names.Add(id);
        }
        Add(Environment.MachineName);
        try
        {
            string host = System.Net.Dns.GetHostName();
            Add(host);
            Add(System.Net.Dns.GetHostEntry(host).HostName);
        }
        catch (System.Net.Sockets.SocketException) { }
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (System.Net.IPAddress.IsLoopback(address.Address)) continue;
                    Add(address.Address.ToString());
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return names;
    }

    /// <summary>Reads without locking: the local launcher rewrites and deletes this file while we poll it.</summary>
    static (MarkerState State, string? Content) ReadMarker(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return (MarkerState.Read, reader.ReadToEnd().Trim());
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return (MarkerState.Gone, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (MarkerState.Unknown, null); }
    }
}
