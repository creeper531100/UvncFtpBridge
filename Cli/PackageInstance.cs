using System.Security.Cryptography;
using System.Text;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 同一份可攜套件同時只有一個「擁有者」：直連的 WinSCP，或 File-Tunnel 通道主機。它們共用 INI、1081 埠與暫存資料夾。
/// 通道主機另外掛一個旗標，讓之後開的 WinSCP／VNC 入口知道可以直接沿用它的通道。
/// </summary>
internal sealed class PackageInstance : IDisposable
{
    readonly Mutex mutex;

    PackageInstance(Mutex mutex) => this.mutex = mutex;

    public static PackageInstance? TryAcquire(string baseDir)
    {
        var mutex = new Mutex(false, @"Local\UvncFtpBridge.Portable." + PackageId(baseDir));
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) return new PackageInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    /// <summary>通道主機活著的期間持有這個旗標；程序結束時系統自動收回。</summary>
    public static Mutex MarkTunnelHost(string baseDir) => new(false, TunnelHostName(baseDir));

    public static bool TunnelHostRunning(string baseDir)
    {
        if (!Mutex.TryOpenExisting(TunnelHostName(baseDir), out var handle)) return false;
        handle.Dispose();
        return true;
    }

    /// <summary>通道主機把 FTP 橋接的埠號寫在這裡，給之後開的 WinSCP 入口設定 INI。</summary>
    public static string FtpPortFile(string baseDir) => Path.Combine(baseDir, "Logs", "tunnel-host.port");

    static string TunnelHostName(string baseDir) => @"Local\UvncFtpBridge.TunnelHost." + PackageId(baseDir);

    static string PackageId(string baseDir) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir.ToUpperInvariant())));
}
