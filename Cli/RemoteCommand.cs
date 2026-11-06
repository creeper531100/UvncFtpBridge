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
                         "       UvncFtpBridge --remote --route 10.6.0.11   (mRemoteNG multi jump host tunnels)\n" +
                         "  --exit-on-disconnect   exit when the local side disconnects (start.bat from mRemoteNG \"deploy jump tunnel\")";

    /// <summary>斷開後等這麼久：RDP 自動重連、網路瞬斷時標記檔會回來，不要誤殺。</summary>
    const int DisconnectGraceMs = 15000;

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
        // --exit-on-disconnect：mRemoteNG「部署跳板通道」的 start.bat 帶這個，本機斷開就結束（start.bat 接著自刪）
        bool exitOnDisconnect = false;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--route" && routeArg == null && i + 1 < args.Length) routeArg = args[++i];
            else if (args[i] == "--exit-on-disconnect") exitOnDisconnect = true;
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
                // 連子程序一起殺（本機端 FileTunnelSession 也是這樣）：只殺 ft.exe 本身時，實測它底下的程序會留著，
                // 繼續佔住檔案（start.bat 刪不掉資料夾）、掛著視窗，RDP 重連後還會自己接回去
                try { if (current is { HasExited: false }) current.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not stop ft.exe: {ex.Message}"); }
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
        if (exitOnDisconnect) Console.WriteLine("This window closes by itself when the local side disconnects.");
        // Each pass serves one local launcher session; when it ends, go back to waiting for the next one.
        while (true)
        {
            string? shared = WaitForLocalTunnel(explicitPath ? target : null, profiles, routeArg, exitOnDisconnect);
            if (shared == null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] RDP drive redirection is gone (disconnected). Exiting.");
                return 0;
            }
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
                // 實測：斷線後 UvncFtpBridge 結束了，ft.exe 卻還活著、RDP 重連後自己接回去。
                // 放進「擁有者結束就全部結束」的工作物件：UvncFtpBridge 不管怎麼結束（正常、當掉、被殺），系統都會收掉 ft.exe。
                KillOnExitJob.Add(process);
                lock (gate) current = process;
                int unknown = 0;
                long goneSince = -1;
                while (!process.HasExited)
                {
                    // The marker disappears when the local launcher closes (or RDP drops); different
                    // content means the local side already restarted with fresh tunnel files.
                    var (state, content) = ReadMarker(marker);
                    if (state == MarkerState.Read && content != session) break;
                    if (state == MarkerState.Read) goneSince = -1;
                    if (state != MarkerState.Read && exitOnDisconnect)
                    {
                        // 結束前先等一下：RDP 自動重連、網路瞬斷時同一個標記檔會回來
                        if (goneSince < 0) goneSince = Environment.TickCount64;
                        if (Environment.TickCount64 - goneSince >= DisconnectGraceMs) break;
                        Thread.Sleep(500);
                        continue;
                    }
                    if (state == MarkerState.Gone) break;
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
            if (exitOnDisconnect)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Local side disconnected. Exiting.");
                return 0;
            }
            Console.WriteLine("Waiting for the local File-Tunnel launcher again.");
            Thread.Sleep(2000);
        }
    }

    /// <summary>
    /// 等本機的通道。exitOnDisconnect 時，曾經看得到 \\tsclient 之後又消失超過 DisconnectGraceMs（RDP 斷了）就回 null。
    /// </summary>
    static string? WaitForLocalTunnel(string? explicitPath, string? profiles, string? routeArg, bool exitOnDisconnect)
    {
        string lastStatus = "";
        bool sawRedirection = false;
        long missingSince = -1;
        var identities = routeArg == null ? OwnIdentities() : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { routeArg };
        if (routeArg == null && explicitPath == null)
            Console.WriteLine("mRemoteNG jump host tunnels for this machine: " + string.Join(", ", identities.Order()));
        while (true)
        {
            string status;
            if (explicitPath != null)
            {
                if (TimedFileExists(Path.Combine(explicitPath, "active.marker"))) return explicitPath;
                status = $"Waiting for local tunnel at {explicitPath}";
            }
            else if (!TimedDirectoryExists(profiles) || Timed<(List<string>, SortedSet<string>)?>(() => ScanTunnels(profiles!, routeArg, identities), null) is not { } scan)
            {
                status = $"Waiting for {profiles}. Enable drive redirection in mstsc and reconnect.";
                if (exitOnDisconnect && sawRedirection)
                {
                    if (missingSince < 0) missingSince = Environment.TickCount64;
                    if (Environment.TickCount64 - missingSince >= DisconnectGraceMs) return null;
                }
            }
            else
            {
                sawRedirection = true;
                missingSince = -1;
                var (found, otherRoutes) = scan;
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

    /// <summary>掃描 \\tsclient\磁碟\Users 底下的本機通道資料夾（在 Timed 裡跑：RDP 斷線時可能卡住）。</summary>
    static (List<string> Found, SortedSet<string> OtherRoutes) ScanTunnels(string profiles, string? routeArg, HashSet<string> identities)
    {
        var found = new List<string>();
        var otherRoutes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string user in Directory.EnumerateDirectories(profiles))
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
        return (found, otherRoutes);
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

    /// <summary>
    /// Windows 工作物件（JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE）：handle 只在這個程序裡，程序一結束 handle 被系統關掉，
    /// 工作物件裡的程序（ft.exe）就會被系統強制結束。Windows 7 起，已在別的工作物件裡的程序也能再加入（巢狀）。
    /// </summary>
    static class KillOnExitJob
    {
        static readonly IntPtr Handle = Create();

        static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, buffer, (uint)size)) return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return job;
        }

        public static void Add(Process process)
        {
            if (Handle == IntPtr.Zero || !AssignProcessToJobObject(Handle, process.Handle))
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Note: could not tie ft.exe to this window (error {Marshal.GetLastWin32Error()}); it is still stopped on exit.");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    // RDP 斷線時 \\tsclient 消失，讀它可能卡住很久（不會馬上報錯），主迴圈就跑不到寬限期計時、永遠不結束。
    // 所以讀標記檔、檢查資料夾都丟到背景、限時 3 秒；卡住就當作「讀不到」。同一時間只留一個卡住的讀取，不越積越多。
    const int TsclientTimeoutMs = 3000;
    static Task? pendingIo;

    static T Timed<T>(Func<T> io, T onTimeout)
    {
        if (pendingIo is { IsCompleted: false }) return onTimeout;
        var task = Task.Run(io);
        pendingIo = task;
        return task.Wait(TsclientTimeoutMs) ? task.Result : onTimeout;
    }

    static bool TimedDirectoryExists(string? path) => Timed(() => Directory.Exists(path), false);

    static bool TimedFileExists(string path) => Timed(() => File.Exists(path), false);

    /// <summary>Reads without locking: the local launcher rewrites and deletes this file while we poll it.</summary>
    static (MarkerState State, string? Content) ReadMarker(string path) =>
        Timed(() => ReadMarkerNow(path), (MarkerState.Unknown, (string?)null));

    static (MarkerState State, string? Content) ReadMarkerNow(string path)
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
