using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace UvncFtpBridge.Rfb;

/// <summary>UltraVNC 伺服器回傳的錯誤（驗證失敗、權限不足等），訊息直接給使用者看。</summary>
public class VncException(string message) : Exception(message);

/// <summary>一則收到的 rfbFileTransfer 訊息。</summary>
public sealed record FtMessage(byte ContentType, ushort Param, uint Size, byte[] Data, uint SizeH);

/// <summary>
/// 與 UltraVNC Server 的一條標準 RFB 連線（跟一般 VNC Viewer 的連線方式相同）：
/// 協商版本、用使用者提供的密碼回應挑戰碼、宣告支援檔案傳輸、讀 ServerInit，
/// 接著背景收訊息（只把檔案傳輸訊息放進佇列，其餘訊息讀掉丟棄）。
/// 不送 FramebufferUpdateRequest，所以不會收到任何畫面影像，純做檔案存取。
/// </summary>
public sealed class RfbConnection : IDisposable
{
    // 協定常數（對照 rfb/rfbproto.h）
    public const byte MsgFileTransfer = 7;
    const byte MsgKeepAlive = 13;

    public const byte FtDirContentRequest = 1, FtDirPacket = 2, FtFileTransferRequest = 3, FtFileHeader = 4,
        FtFilePacket = 5, FtEndOfFile = 6, FtAbortFileTransfer = 7, FtFileTransferOffer = 8, FtFileAcceptHeader = 9,
        FtCommand = 10, FtCommandReturn = 11, FtFileChecksums = 12, FtFileTransferAccess = 14,
        FtSessionStart = 15, FtSessionEnd = 16, FtProtocolVersion = 17;

    const uint EncFTProtocolVersion = 0xFFFF8002;

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly object _sendLock = new();
    readonly BlockingCollection<FtMessage> _ftQueue = new();
    readonly Thread _reader;
    readonly Timer _keepAlive;
    volatile bool _closed;
    Exception? _readerError;

    public string Host { get; }
    public int Port { get; }
    public string DesktopName { get; private set; } = "";

    /// <summary>伺服器宣告的檔案傳輸協定版本（0 = 沒宣告，代表較舊的伺服器）。</summary>
    public int ServerFtVersion { get; private set; }
    /// <summary>伺服器宣告的資料塊大小（0 = 用預設值）。</summary>
    public int ServerBlockSize { get; private set; }
    /// <summary>RC18 以前的舊協定（檔案標頭後面不帶 sizeH 欄位）。</summary>
    public volatile bool OldFtProtocol;
    /// <summary>伺服器是否支援 UTF-8（Unicode）檔名。</summary>
    public bool SupportsUnicode => ServerFtVersion >= 4;

    public bool IsAlive => !_closed && _readerError == null;

    RfbConnection(string host, int port, TcpClient tcp)
    {
        Host = host; Port = port; _tcp = tcp; _stream = tcp.GetStream();
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = $"rfb-reader {host}" };
        _keepAlive = new Timer(_ => SendKeepAlive(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static RfbConnection Connect(string host, int port, string password, int timeoutMs = 15000)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
                throw new VncException($"連不上 {host}:{port}（逾時）");
        }
        catch (AggregateException ex)
        {
            tcp.Dispose();
            throw new VncException($"連不上 {host}:{port}：{ex.InnerException?.Message}");
        }
        catch (VncException) { tcp.Dispose(); throw; }
        catch (Exception ex) { tcp.Dispose(); throw new VncException($"連不上 {host}:{port}：{ex.Message}"); }

        var c = new RfbConnection(host, port, tcp);
        try
        {
            // 握手階段若伺服器設成「連線時要現場按同意」，可能要等人操作，逾時設長一點
            tcp.ReceiveTimeout = 90000;
            tcp.SendTimeout = 30000;
            c.Handshake(password);
            tcp.ReceiveTimeout = 0; // 之後改由背景執行緒阻塞讀取
            c._reader.Start();
            c._keepAlive.Change(10000, 10000);
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    // ---------------- 握手（標準 VNC 連線流程） ----------------

    void Handshake(string password)
    {
        string verStr = Encoding.ASCII.GetString(ReadExact(12));
        if (!verStr.StartsWith("RFB "))
            throw new VncException($"對方不是 VNC 伺服器（收到 \"{verStr.Trim()}\"）");
        int major = int.Parse(verStr.Substring(4, 3));
        int minor = int.Parse(verStr.Substring(8, 3));
        Log.Debug($"[{Host}] 伺服器協定版本 {major}.{minor}");
        if (major != 3) throw new VncException($"不支援的 RFB 版本 {verStr.Trim()}");

        // UltraVNC 以 RFB 3.7 以上走多重驗證型別清單，3.3 走單一型別
        int replyMinor = minor >= 8 ? 8 : (minor >= 7 ? 7 : 3);
        replyMinorWas8 = replyMinor >= 8;
        WriteRaw(Encoding.ASCII.GetBytes($"RFB 003.{replyMinor:D3}\n"));

        if (replyMinor >= 7) SecurityHandshakeModern(password);
        else SecurityHandshakeLegacy(password);

        // ClientInit：shared = 1，表示不要把現場已連上的人踢掉
        WriteRaw(new byte[] { 1 });

        // ServerInit：16 bytes 固定欄位 + 名稱長度(4) + 名稱
        byte[] si = ReadExact(24);
        uint nameLen = BinaryPrimitives.ReadUInt32BigEndian(si.AsSpan(20, 4));
        if (nameLen > 0 && nameLen < 4096)
            DesktopName = Encoding.UTF8.GetString(ReadExact((int)nameLen));
        Log.Debug($"[{Host}] 桌面名稱：{DesktopName}");

        // UltraVNC 就算沒要求也會主動推畫面更新，改成 8 位元色彩讓這些資料小一點（讀到就丟）
        SendSetPixelFormat8();
        // Raw 是我們唯一能略過的畫面編碼；再宣告「檔案傳輸協定版本」pseudo-encoding，
        // 伺服器就會回一則 rfbFileTransferProtocolVersion 告訴我們版本與塊大小。
        SendSetEncodings(EncRaw, EncFTProtocolVersion);
    }

    const uint EncRaw = 0, EncLastRect = 0xFFFFFF20, EncNewFBSize = 0xFFFFFF21;
    int _bytesPerPixel = 4;

    void SendSetPixelFormat8()
    {
        var m = new byte[20];
        m[0] = 0;                      // SetPixelFormat
        m[4] = 8; m[5] = 8;            // bits-per-pixel、depth
        m[6] = 0; m[7] = 1;            // big-endian=否、true-colour=是
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8, 2), 7);   // red max
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(10, 2), 7);  // green max
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(12, 2), 3);  // blue max
        m[14] = 0; m[15] = 3; m[16] = 6;                            // shifts
        WriteRaw(m);
        _bytesPerPixel = 1;
    }

    void SecurityHandshakeModern(string password)
    {
        int count = ReadExact(1)[0];
        if (count == 0)
        {
            // 伺服器拒絕：後面接一段原因字串
            uint reasonLen = ReadU32();
            string reason = Encoding.UTF8.GetString(ReadExact((int)Math.Min(reasonLen, 4096)));
            throw new VncException($"伺服器拒絕連線：{reason}");
        }

        byte[] types = ReadExact(count);
        Log.Debug($"[{Host}] 伺服器提供的驗證型別：{string.Join(",", types)}");

        // 我們只處理：2 = VNC 密碼、1 = 不需驗證。
        // 17 (rfbUltraVNC) 是 UltraVNC 的識別前綴，若出現要略過，挑後面真正能用的型別。
        byte chosen = 0;
        if (Array.IndexOf(types, (byte)2) >= 0) chosen = 2;
        else if (Array.IndexOf(types, (byte)1) >= 0) chosen = 1;

        if (chosen == 0)
            throw new VncException("這台機台的 UltraVNC 不是用單純的 VNC 密碼登入（可能設了加密插件或 Windows 帳號登入），目前的版本只支援 VNC 密碼。");

        WriteRaw(new byte[] { chosen });

        if (chosen == 2) VncPasswordResponse(password);
        // chosen == 1：不需驗證，不用做事

        // SecurityResult（3.8 一定有；3.7 在成功時不送，所以只在有資料時讀）
        ReadSecurityResult(require: replyMinorWas8);
    }

    bool replyMinorWas8;

    void SecurityHandshakeLegacy(string password)
    {
        uint type = ReadU32(); // 3.3：伺服器直接指定單一驗證型別
        Log.Debug($"[{Host}] 舊式驗證型別：{type}");
        if (type == 0)
        {
            uint reasonLen = ReadU32();
            string reason = Encoding.UTF8.GetString(ReadExact((int)Math.Min(reasonLen, 4096)));
            throw new VncException($"伺服器拒絕連線：{reason}");
        }
        if (type == 1) return;               // 不需驗證
        if (type == 2) { VncPasswordResponse(password); ReadSecurityResult(require: true); return; }
        throw new VncException($"不支援的驗證型別 {type}（目前只支援 VNC 密碼）。");
    }

    /// <summary>標準 VNC 密碼驗證：收 16 bytes 挑戰碼，用密碼加密後送回。</summary>
    void VncPasswordResponse(string password)
    {
        byte[] challenge = ReadExact(16);
        byte[] response = VncDes.VncResponse(password ?? "", challenge);
        WriteRaw(response);
    }

    void ReadSecurityResult(bool require)
    {
        // RFB 3.8：成功/失敗都會回 4 bytes 結果；3.7 成功時不回。
        if (!require && _tcp.Available == 0) return;
        uint result = ReadU32();
        if (result == 0) return; // 0 = OK
        // 失敗：3.8 後面通常接原因字串
        string reason = "密碼錯誤或驗證失敗";
        try
        {
            uint reasonLen = ReadU32();
            if (reasonLen is > 0 and < 4096)
                reason = Encoding.UTF8.GetString(ReadExact((int)reasonLen));
        }
        catch { }
        throw new VncException($"登入失敗：{reason}");
    }

    void SendSetEncodings(params uint[] encodings)
    {
        var buf = new byte[4 + encodings.Length * 4];
        buf[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4 + i * 4, 4), encodings[i]);
        WriteRaw(buf);
    }

    // ---------------- 背景收訊息 ----------------

    void ReaderLoop()
    {
        try
        {
            while (!_closed)
            {
                int type = _stream.ReadByte();
                if (type < 0) break;
                switch (type)
                {
                    case MsgFileTransfer: ReadFileTransferMsg(); break;
                    case MsgKeepAlive: break;                       // 1 byte，讀掉即可
                    case 0: DrainFramebufferUpdate(); break;        // 理論上不會來（我們沒要求更新）
                    case 1: SkipSetColourMap(); break;
                    case 2: break;                                   // Bell
                    case 3: SkipServerCutText(); break;              // 剪貼簿
                    case 0xAD: ReadExact(11); break;                 // rfbServerState（12 bytes，已吃掉 1）
                    case 11: SkipTextChat(); break;                  // TextChat
                    case 4: ReadExact(5); break;                     // ResizeFrameBuffer（6 bytes）
                    default:
                        throw new VncException($"收到未預期的訊息型別 0x{type:X2}");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_closed) { _readerError = ex; Log.Debug($"[{Host}] 收訊息執行緒結束：{ex.Message}"); }
        }
        finally
        {
            _ftQueue.CompleteAdding();
        }
    }

    void ReadFileTransferMsg()
    {
        // 已讀掉 type，剩 11 bytes：contentType(1) contentParam(2) size(4) length(4)
        byte[] h = ReadExact(11);
        byte contentType = h[0];
        // 注意：UltraVNC 的 contentParam 沒有做網路位元組序轉換，線上是 x86 的小端序
        ushort param = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1, 2));
        uint size = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(3, 4));
        uint length = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(7, 4));

        // 伺服器端一宣告協定版本就記起來（不進佇列，因為它是連線層級的設定）
        if (contentType == FtProtocolVersion)
        {
            ServerFtVersion = param;
            ServerBlockSize = (int)size;
            Log.Debug($"[{Host}] 伺服器檔案傳輸協定版本 v{param}，塊大小 {size}");
            return;
        }

        byte[] data = length is > 0 and < 64 * 1024 * 1024 ? ReadExact((int)length) : Array.Empty<byte>();

        // 只有下載用的檔案標頭 rfbFileHeader 在非舊協定時，資料後面還跟著一個 sizeH(4)。
        // 上傳用的 rfbFileAcceptHeader 後面沒有 sizeH。
        uint sizeH = 0;
        if (!OldFtProtocol && contentType == FtFileHeader)
            sizeH = ReadU32();

        if (contentType != FtFilePacket)
            Log.Debug($"[{Host}] 收到 FT type={contentType} param=0x{param:X4} size=0x{size:X8} len={length}");
        _ftQueue.Add(new FtMessage(contentType, param, size, data, sizeH));
    }

    void DrainFramebufferUpdate()
    {
        // 伺服器主動推的畫面更新：讀完丟掉。我們只宣告了 Raw，所以每個矩形就是 w*h*bpp 的原始像素。
        byte[] h = ReadExact(3); // pad(1) + 矩形數(2)
        int rects = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(1, 2));
        for (int i = 0; i < rects; i++)
        {
            byte[] r = ReadExact(12);
            int w = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4, 2));
            int hgt = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(6, 2));
            uint enc = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(8, 4));
            if (enc == EncLastRect) break;
            if (enc == EncNewFBSize) continue;
            if (enc != EncRaw) throw new VncException($"收到不支援的畫面編碼 0x{enc:X8}");
            Skip((long)w * hgt * _bytesPerPixel);
        }
    }

    void Skip(long n)
    {
        var buf = new byte[64 * 1024];
        while (n > 0)
        {
            int r = _stream.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (r <= 0) throw new EndOfStreamException("連線被對方關閉");
            n -= r;
        }
    }

    void SkipSetColourMap()
    {
        byte[] h = ReadExact(5); // pad(1)+first(2)+count(2)，已吃掉 type
        ushort n = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(3, 2));
        ReadExact(n * 6);
    }

    void SkipServerCutText()
    {
        byte[] h = ReadExact(7); // pad(3)+length(4)
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(3, 4));
        if (len > 0) ReadExact(len);
    }

    void SkipTextChat()
    {
        byte[] h = ReadExact(7); // pad(1)+pad(2)+length(4)
        uint len = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(3, 4));
        if (len is > 0 and < 0x10000000) ReadExact((int)len);
    }

    // ---------------- 送檔案傳輸訊息 ----------------

    /// <summary>送一則 rfbFileTransfer，標頭後可接資料。</summary>
    public void SendFt(byte contentType, ushort param, uint size, byte[]? data = null, byte[]? extraTail = null)
    {
        data ??= Array.Empty<byte>();
        var header = new byte[12];
        header[0] = MsgFileTransfer;
        header[1] = contentType;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), param); // contentParam 是小端序（見上）
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), size);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)data.Length);
        lock (_sendLock)
        {
            _stream.Write(header);
            if (data.Length > 0) _stream.Write(data);
            if (extraTail is { Length: > 0 }) _stream.Write(extraTail);
            _stream.Flush();
        }
    }

    void SendKeepAlive()
    {
        if (_closed) return;
        try { lock (_sendLock) { _stream.WriteByte(MsgKeepAlive); _stream.Flush(); } }
        catch { }
    }

    /// <summary>從收訊息佇列取下一則，逾時或連線斷掉就丟例外。</summary>
    public FtMessage TakeFt(int timeoutMs = 60000)
    {
        if (_readerError != null) throw new VncException($"連線已中斷：{_readerError.Message}");
        if (_ftQueue.TryTake(out var msg, timeoutMs)) return msg;
        if (_readerError != null) throw new VncException($"連線已中斷：{_readerError.Message}");
        if (_ftQueue.IsAddingCompleted) throw new VncException("連線已被對方關閉");
        throw new VncException("等待伺服器回應逾時");
    }

    // ---------------- 低階 IO ----------------

    byte[] ReadExact(int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = _stream.Read(buf, off, n - off);
            if (r <= 0) throw new EndOfStreamException("連線被對方關閉");
            off += r;
        }
        return buf;
    }

    uint ReadU32() => BinaryPrimitives.ReadUInt32BigEndian(ReadExact(4));

    void WriteRaw(byte[] data)
    {
        lock (_sendLock) { _stream.Write(data); _stream.Flush(); }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _keepAlive.Dispose(); } catch { }
        try { _ftQueue.CompleteAdding(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }
}
