using System.Net;
using System.Net.Sockets;

namespace UvncFtpBridge.Ftp;

/// <summary>最精簡的 FTP 伺服器，只監聽在 127.0.0.1。每條連線各開一條執行緒。</summary>
public sealed class FtpServer(IPAddress bindAddress, int port, int defaultVncPort, string? targetHost = null)
{
    readonly TcpListener _listener = new(bindAddress, port);
    public int DefaultVncPort { get; } = defaultVncPort;
    public string? TargetHost { get; } = targetHost;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    bool _started;

    /// <summary>開始監聽；port=0 時由系統分配，Port 可讀取實際埠。</summary>
    public void Start()
    {
        if (_started) return;
        _listener.Start();
        _started = true;
        Log.Info($"FTP 橋接已啟動，監聽 {bindAddress}:{Port}");
        if (TargetHost != null) Log.Info($"機台目標 {TargetHost}:{DefaultVncPort}（由 WinSCP 工作階段指定）。");
    }

    public void Run(CancellationToken ct)
    {
        Start();
        using var registration = ct.Register(() => { try { _listener.Stop(); } catch { } });

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error($"接受連線失敗：{ex.Message}"); break; }

            var session = new FtpSession(client, this);
            new Thread(session.Run) { IsBackground = true, Name = "ftp-session" }.Start();
        }
        Log.Info("FTP 橋接已停止。");
    }
}
