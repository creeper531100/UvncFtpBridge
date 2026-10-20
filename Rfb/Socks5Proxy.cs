using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UvncFtpBridge.Rfb;

/// <summary>SOCKS5 代理（RFC 1928，只用免驗證 + TCP CONNECT）。目標主機名稱交給代理那端解析。</summary>
public sealed record Socks5Proxy(string Host, int Port)
{
    public static Socks5Proxy? FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("UVNC_SOCKS5_PROXY");
        return string.IsNullOrWhiteSpace(value) ? null : Parse(value);
    }

    public static Socks5Proxy Parse(string value)
    {
        string address = value.Contains("://", StringComparison.Ordinal) ? value : "socks5://" + value;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "socks5" || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("UVNC_SOCKS5_PROXY 須為 host:port，例如 127.0.0.1:1080。");
        return new Socks5Proxy(uri.DnsSafeHost, uri.Port);
    }

    /// <summary>連上代理並要求它轉接到目標；成功返回後 tcp 收送的就是目標的資料。</summary>
    public void Connect(TcpClient tcp, string targetHost, int targetPort, int timeoutMs)
    {
        RfbConnection.ConnectTcp(tcp, Host, Port, timeoutMs, $"連不上 SOCKS5 代理 {Host}:{Port}");

        tcp.ReceiveTimeout = timeoutMs;
        tcp.SendTimeout = timeoutMs;
        var stream = tcp.GetStream();
        stream.Write(new byte[] { 5, 1, 0 }); // SOCKS5、提供 1 種驗證方式：免驗證
        byte[] method = new byte[2];
        stream.ReadExactly(method);
        if (method[0] != 5 || method[1] != 0)
            throw new VncException("SOCKS5 代理不接受免驗證連線。");

        var request = new List<byte> { 5, 1, 0 }; // CONNECT
        if (IPAddress.TryParse(targetHost, out var ip))
        {
            request.Add(ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4);
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            string domain = new IdnMapping().GetAscii(targetHost);
            byte[] name = Encoding.ASCII.GetBytes(domain);
            if (name.Length is 0 or > 255) throw new VncException("SOCKS5 目標主機名稱長度無效。");
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }
        request.Add((byte)(targetPort >> 8));
        request.Add((byte)targetPort);
        stream.Write(request.ToArray());

        byte[] reply = new byte[4];
        stream.ReadExactly(reply);
        if (reply[0] != 5 || reply[2] != 0)
            throw new VncException("SOCKS5 代理回傳無效的連線回應。");
        if (reply[1] != 0)
            throw new VncException($"SOCKS5 代理無法連到 {targetHost}:{targetPort}（回應碼 {reply[1]}）。請確認跳板能連到這台機台的 VNC 埠。");

        int addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => stream.ReadByte(),
            _ => throw new VncException("SOCKS5 代理回傳未知的位址類型。"),
        };
        if (addressLength < 0) throw new EndOfStreamException("SOCKS5 代理提前關閉連線。");
        stream.ReadExactly(new byte[addressLength + 2]); // 讀掉代理綁定的位址與埠，後面才是目標的資料
        tcp.ReceiveTimeout = 0;
        tcp.SendTimeout = 0;
    }
}
