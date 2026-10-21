using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UvncFtpBridge.Rfb;

/// <summary>
/// UltraVNC Repeater Mode I：Viewer 勾「Use Repeater」後先連到這裡，告訴我們要連哪台機台，
/// 我們再經 SOCKS5 代理接過去，之後雙向原樣轉送。協定同 UltraVNC repeater/mode12_listener.cpp。
/// </summary>
public sealed class VncRepeater(IPAddress bindAddress, int port, Socks5Proxy? proxy)
{
    public const int DefaultPort = 5999;
    const int HostBlockLength = 250; // UltraVNC 的 MAX_HOST_NAME_LEN
    static readonly byte[] RepeaterVersion = "RFB 000.000\n"u8.ToArray(); // 0.0 = 叫 Viewer 送目標位址

    readonly TcpListener listener = new(bindAddress, port);

    /// <summary>代理連不上時最多等多久（給 File-Tunnel 等跳板端連上）。</summary>
    public TimeSpan ProxyWait { get; init; } = TimeSpan.FromSeconds(30);

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public void Start()
    {
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new IOException($"本機 {port} 埠已被佔用，無法啟動 VNC Repeater。請關閉舊的啟動檔或其他 Repeater。");
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var open = new ConcurrentDictionary<TcpClient, byte>();
        var sessions = new List<Task>();
        try
        {
            while (true)
            {
                TcpClient viewer;
                try { viewer = await listener.AcceptTcpClientAsync(ct); }
                catch (OperationCanceledException) { break; }
                sessions.RemoveAll(t => t.IsCompleted);
                sessions.Add(Task.Run(() => Serve(viewer, open, ct)));
            }
        }
        finally
        {
            listener.Stop();
            foreach (var tcp in open.Keys) tcp.Dispose();
            await Task.WhenAll(sessions);
        }
    }

    async Task Serve(TcpClient viewer, ConcurrentDictionary<TcpClient, byte> open, CancellationToken ct)
    {
        TcpClient? target = null;
        open[viewer] = 0;
        string where = "（未知機台）";
        try
        {
            viewer.NoDelay = true;
            var viewerStream = viewer.GetStream();
            await viewerStream.WriteAsync(RepeaterVersion, ct);
            byte[] block = new byte[HostBlockLength];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(10000);
                await viewerStream.ReadExactlyAsync(block, timeout.Token);
            }
            var destination = ParseTarget(block);
            if (destination == null)
            {
                Log.Warn($"VNC Repeater 收到無法辨識的機台位址「{Describe(block)}」。請在 Viewer 填機台 IP，例如 10.1.2.3 或 10.1.2.3::5901。");
                return;
            }
            var (host, targetPort) = destination.Value;
            where = $"{host}:{targetPort}";
            Log.Info($"VNC 連線 → {where}");
            target = await ConnectThroughProxy(host, targetPort, open, ct);

            var targetStream = target.GetStream();
            Task up = Pump(viewerStream, targetStream, ct);
            Task down = Pump(targetStream, viewerStream, ct);
            await Task.WhenAny(up, down);
            // 任一方向斷了就關兩端，讓另一個方向也結束
            viewer.Close();
            target.Close();
            await Task.WhenAll(up, down);
            Log.Info($"VNC 連線結束：{where}");
        }
        catch (VncException ex) { Log.Warn($"VNC 連線失敗：{ex.Message}"); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn($"VNC 連線 {where} 中斷：{ex.Message}。請確認跳板端 Start-FileTunnel-Remote 仍在執行。");
        }
        finally
        {
            open.TryRemove(viewer, out _);
            viewer.Dispose();
            if (target != null)
            {
                open.TryRemove(target, out _);
                target.Dispose();
            }
        }
    }

    /// <summary>
    /// File-Tunnel 要等跳板端連上才開始收 SOCKS5 連線（log 會出現 Started SOCKS proxy），通常要十幾秒。
    /// 這段期間代理會拒絕連線，所以先等它就緒，Viewer 那邊就不用重按 Connect。
    /// </summary>
    async Task<TcpClient> ConnectThroughProxy(string host, int targetPort, ConcurrentDictionary<TcpClient, byte> open, CancellationToken ct)
    {
        if (proxy == null) // 直連模式：Viewer 填的機台本機就連得到
        {
            var direct = new TcpClient { NoDelay = true };
            open[direct] = 0;
            try
            {
                await Task.Run(() => RfbConnection.ConnectTcp(direct, host, targetPort, 15000, $"連不上 {host}:{targetPort}"), ct);
                return direct;
            }
            catch
            {
                open.TryRemove(direct, out _);
                direct.Dispose();
                throw;
            }
        }
        DateTime deadline = DateTime.UtcNow + ProxyWait;
        bool announced = false;
        while (true)
        {
            var tcp = new TcpClient { NoDelay = true };
            open[tcp] = 0;
            try
            {
                await Task.Run(() => proxy.Connect(tcp, host, targetPort, 15000), ct);
                return tcp;
            }
            catch (VncException ex) when (ex.Message.StartsWith(Socks5Proxy.UnreachableMessage, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                open.TryRemove(tcp, out _);
                tcp.Dispose();
                if (!announced)
                {
                    Log.Info($"跳板端通道還沒就緒，最多等 {ProxyWait.TotalSeconds:0} 秒…（請確認跳板已執行 Start-FileTunnel-Remote.cmd）");
                    announced = true;
                }
                await Task.Delay(1000, ct);
            }
            catch
            {
                open.TryRemove(tcp, out _);
                tcp.Dispose();
                throw;
            }
        }
    }

    static async Task Pump(Stream from, Stream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 81920, ct); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    /// <summary>解析 Viewer 送來的 250 bytes「主機:埠」（UTF-8，NUL 補滿）。不支援 Repeater 的 ID 模式。</summary>
    internal static (string Host, int Port)? ParseTarget(ReadOnlySpan<byte> block)
    {
        int end = block.IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(end < 0 ? block : block[..end]).Trim();
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out int value)) return null;
        string host = text[..colon].Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Equals("ID", StringComparison.OrdinalIgnoreCase) || host.StartsWith("ID+", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        int targetPort = value is >= 0 and < 100 ? 5900 + value : value; // 跟 UltraVNC 一樣：小於 100 視為 display 編號
        return targetPort is >= 1 and <= 65535 ? (host, targetPort) : null;
    }

    static string Describe(ReadOnlySpan<byte> block)
    {
        int end = block.IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(end < 0 ? block : block[..end]).Trim();
        return text.Length > 64 ? text[..64] + "…" : text;
    }
}
