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
    const string Usage = "Usage: UvncFtpBridge --remote [drive letter | full redirected TEMP path]\n" +
                         "  e.g. UvncFtpBridge --remote D\n" +
                         "       UvncFtpBridge --remote \"\\\\tsclient\\C\\MyTemp\\UvncFtpBridge\\FileTunnel\"";

    enum MarkerState { Read, Gone, Unknown }

    public static int Run(string[] args)
    {
        string ft = Path.Combine(AppContext.BaseDirectory, "ft.exe");
        if (!File.Exists(ft))
        {
            Console.Error.WriteLine("File-Tunnel remote error: place ft.exe beside UvncFtpBridge.exe.");
            return 1;
        }
        if (args.Length > 1)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string target = args.Length == 1 ? args[0] : "C";
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
            string shared = WaitForLocalTunnel(explicitPath ? target : null, profiles);
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

    static string WaitForLocalTunnel(string? explicitPath, string? profiles)
    {
        string lastStatus = "";
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
                try
                {
                    foreach (string user in Directory.EnumerateDirectories(profiles!))
                    {
                        string candidate = Path.Combine(user, @"AppData\Local\Temp\UvncFtpBridge\FileTunnel");
                        try { if (File.Exists(Path.Combine(candidate, "active.marker"))) found.Add(candidate); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (found.Count == 1) return found[0];
                status = found.Count > 1
                    ? $"Multiple active local TEMP tunnels found under {profiles}. Close the extra ones, or restart with the complete redirected TEMP path."
                    : $"Waiting for local tunnel under {profiles}. Start the File-Tunnel launcher on the local PC.";
            }
            if (status != lastStatus)
            {
                Console.WriteLine(status);
                lastStatus = status;
            }
            Thread.Sleep(2000);
        }
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
