using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Client;

/// <summary>
/// 一台機台的連線。記住連線資訊，連線斷掉時下一次操作會自動重連。
/// 給命令列操作使用。
/// </summary>
public sealed class RemoteSession : IDisposable
{
    readonly string _password;
    readonly Socks5Proxy? _proxy;
    RfbConnection? _conn;
    UvncFileTransfer? _ft;

    public string Host { get; }
    public int Port { get; }
    public string DesktopName => _conn?.DesktopName ?? "";
    public bool IsConnected => _conn?.IsAlive == true;

    public RemoteSession(string hostSpec, int defaultPort, string password, Socks5Proxy? proxy = null)
    {
        (Host, Port) = ParseHost(hostSpec, defaultPort);
        _password = password;
        _proxy = proxy;
    }

    /// <summary>"192.168.0.10" 或 "192.168.0.10:5901"。</summary>
    public static (string host, int port) ParseHost(string spec, int defaultPort)
    {
        spec = spec.Trim();
        int colon = spec.LastIndexOf(':');
        if (colon > 0 && int.TryParse(spec[(colon + 1)..], out int p) && p is > 0 and < 65536)
            return (spec[..colon], p);
        return (spec, defaultPort);
    }

    /// <summary>取得可用的檔案傳輸物件；還沒連或已斷線就（重新）連線。</summary>
    public UvncFileTransfer Ft
    {
        get { EnsureConnected(); return _ft!; }
    }

    public void EnsureConnected()
    {
        if (_conn is { IsAlive: true }) return;
        Drop();
        Log.Debug($"連線到 {Host}:{Port}");
        var conn = RfbConnection.Connect(Host, Port, _password, proxy: _proxy);
        try
        {
            var ft = new UvncFileTransfer(conn);
            ft.RequestAccess();
            _conn = conn;
            _ft = ft;
        }
        catch { conn.Dispose(); throw; }
    }

    /// <summary>把目前的連線丟掉（傳輸被取消或出錯後，連線狀態不可信時用）。</summary>
    public void Drop()
    {
        try { _conn?.Dispose(); } catch { }
        _conn = null;
        _ft = null;
    }

    public void Dispose() => Drop();
}
