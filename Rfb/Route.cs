using System.Net.Sockets;

namespace UvncFtpBridge.Rfb;

/// <summary>給一條「路由」（某個跳板的 File-Tunnel）回傳它的 SOCKS5 代理；沒開就先開。</summary>
public delegate Socks5Proxy RouteProxy(string routeId);

/// <summary>
/// 多跳板路由（mRemoteNG 主機模式用）：目標寫成「路由~目標」就走那條路由，沒有「~」就跟原本一樣。
/// WinSCP 用 USER ultravnc~路由@機台；noVNC 用 ws/路由~機台[:埠]；UltraVNC Repeater 用 路由~機台:埠。
/// 路由名稱就是跳板的主機名稱或 IP，只允許英數字與 . - _。
/// </summary>
public static class Route
{
    public const char Separator = '~';

    /// <summary>「路由~其他」拆開；沒有路由時 route = null、rest = 原字串。路由名稱不合法時回 false。</summary>
    public static bool TrySplit(string text, out string? route, out string rest)
    {
        int at = text.IndexOf(Separator);
        if (at < 0)
        {
            route = null;
            rest = text;
            return true;
        }
        route = text[..at];
        rest = text[(at + 1)..];
        return IsValidId(route);
    }

    public static bool IsValidId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>主機名稱／IP 轉成路由名稱：不合法的字元換成 _（IPv6 的 : 也是）。</summary>
    public static string Sanitize(string host)
    {
        var chars = host.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        string id = new string(chars).Trim('_');
        return id.Length > 64 ? id[..64] : id;
    }

    /// <summary>
    /// 經跳板通道連線時，每次嘗試等代理回覆多久。要比 ft.exe 自己等跳板撥號的時間（通道逾時 10 秒 × 2）長，
    /// 才拿得到它的判定（連不到機台的回應碼），不會在機台連得慢時自己先放棄。
    /// </summary>
    public const int AttemptTimeoutMs = 22000;

    /// <summary>
    /// 經代理連線；File-Tunnel 要等跳板端連上才收連線（通常十幾秒），代理連不上、或收了請求卻沒回覆
    /// （通道剛好重啟，請求丟了）時重試到 wait 為止。每次重試都用新的 TcpClient（失敗的會被處置）。
    /// </summary>
    public static TcpClient ConnectWithWait(Socks5Proxy proxy, string host, int port, TimeSpan wait)
    {
        DateTime deadline = DateTime.UtcNow + wait;
        bool announced = false;
        while (true)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                proxy.Connect(tcp, host, port, AttemptTimeoutMs);
                return tcp;
            }
            catch (VncException ex) when (Socks5Proxy.IsRetryable(ex) && DateTime.UtcNow < deadline)
            {
                tcp.Dispose();
                announced = AnnounceRetry(ex, wait, announced);
                Thread.Sleep(1000);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
    }

    /// <summary>重試前寫一行記錄：「還沒就緒」只寫一次，「請求沒有回覆」每次都寫（查問題要看得到）。回傳新的 announced。</summary>
    public static bool AnnounceRetry(VncException ex, TimeSpan wait, bool announced)
    {
        if (ex.Message.StartsWith(Socks5Proxy.NoReplyMessage, StringComparison.Ordinal))
        {
            Log.Warn($"{ex.Message}。跳板通道可能剛重新連線，重新送出連線請求…");
            return announced;
        }
        if (!announced) Log.Info($"跳板端通道還沒就緒，最多等 {wait.TotalSeconds:0} 秒…（請確認跳板上的 UvncFtpJump 視窗還開著）");
        return true;
    }
}
