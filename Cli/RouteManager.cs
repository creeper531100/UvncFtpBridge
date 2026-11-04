using System.Net;
using System.Net.Sockets;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 多跳板路由（mRemoteNG 主機模式）：每個跳板一條 File-Tunnel，各用自己的資料夾
/// （%TEMP%\UvncFtpBridge\Routes\&lt;路由&gt;）與 SOCKS5 埠（從 1082 起找空的，不跟原本的 1081 衝突）。
/// 第一次用到才開；ft.exe 意外結束時下次用到會重開。
/// </summary>
internal sealed class RouteManager(string baseDir) : IDisposable
{
    const int FirstSocksPort = 1082;

    sealed record Entry(FileTunnelSession Session, int SocksPort);

    readonly Dictionary<string, Entry> routes = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    bool disposed;

    /// <summary>取得路由的 SOCKS5 代理，沒開就開。路由名稱不合法丟 ArgumentException。</summary>
    public Socks5Proxy Get(string route)
    {
        if (!Route.IsValidId(route)) throw new ArgumentException($"路由名稱不合法：{route}");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (routes.TryGetValue(route, out var entry))
            {
                if (!entry.Session.HasExited) return new Socks5Proxy("127.0.0.1", entry.SocksPort);
                Log.Warn($"File-Tunnel [{route}] 已結束，重新開啟。");
                entry.Session.Dispose();
                routes.Remove(route);
            }
            int port = FreePort(routes.Values.Select(e => e.SocksPort).ToHashSet());
            var session = FileTunnelSession.Start(baseDir, FileTunnelSession.RouteFolder(route), port, route);
            routes[route] = new Entry(session, port);
            return new Socks5Proxy("127.0.0.1", port);
        }
    }

    public IReadOnlyList<(string Route, int SocksPort, bool Online)> Snapshot()
    {
        lock (gate) return routes.Select(r => (r.Key, r.Value.SocksPort, r.Value.Session.IsOnline)).ToList();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in routes.Values) entry.Session.Dispose();
            routes.Clear();
        }
    }

    static int FreePort(HashSet<int> taken)
    {
        for (int port = FirstSocksPort; port < FirstSocksPort + 200; port++)
        {
            if (taken.Contains(port)) continue;
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new IOException($"找不到可用的本機埠給 File-Tunnel（{FirstSocksPort} 起 200 個都被佔用）。");
    }
}
