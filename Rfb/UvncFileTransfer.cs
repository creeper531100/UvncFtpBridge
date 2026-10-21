using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace UvncFtpBridge.Rfb;

/// <summary>遠端目錄中的一筆項目。</summary>
public sealed class RemoteEntry
{
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public bool Inaccessible { get; init; }
}

/// <summary>
/// 把 RfbConnection 包成好用的檔案操作：列磁碟、列目錄、下載、上傳、建資料夾、刪除、改名。
/// 所有操作用同一把鎖序列化，確保同一條連線一次只跑一個操作。
/// </summary>
public sealed class UvncFileTransfer(RfbConnection conn)
{
    const uint FileSizeError = 0xFFFFFFFF;
    const ushort UnicodeFlag = 0x8000;

    // 命令 contentParam（rfbCommand）
    const ushort CDirCreate = 1, CFileDelete = 4, CFileRename = 5;
    // 列目錄請求 contentParam
    const ushort RDirContent = 1, RDrivesList = 2;
    // 回應 contentParam
    const ushort ADirectory = 1, AFile = 2, ADrivesList = 3, ADirInaccessible = 18;

    const uint FdInaccessible = 0xFFFFFFFE; // dwReserved0 的哨兵值

    readonly RfbConnection _c = conn;
    readonly object _op = new();

    Encoding PathEncoding => _c.SupportsUnicode ? Encoding.UTF8 : Encoding.GetEncoding(950);

    int ChunkSize
    {
        get
        {
            int n = _c.ServerBlockSize;
            return n is >= 4096 and <= 32768 ? n : (_c.SupportsUnicode ? 32768 : 8192);
        }
    }

    byte[] Enc(string s) => PathEncoding.GetBytes(s);

    /// <summary>向伺服器要求檔案傳輸權限。失敗代表機台沒開啟檔案傳輸或權限不足。</summary>
    public void RequestAccess()
    {
        lock (_op)
        {
            // 通知伺服器「檔案傳輸視窗已開啟」（v3 以上才有意義，送了也無害）
            _c.SendFt(RfbConnection.FtSessionStart, 0, 0);

            // 用相容的舊方法詢問權限：送 AbortFileTransfer、contentParam = 檔案傳輸版本(3)。
            // 伺服器會回 FtFileTransferAccess，size=1 允許、-1 拒絕。
            _c.SendFt(RfbConnection.FtAbortFileTransfer, 3, 0);
            var reply = ReadUntil(RfbConnection.FtFileTransferAccess);
            if (reply.Size != 1)
                throw new VncException("這台機台沒有開啟檔案傳輸功能（或目前不允許）。請到機台的 UltraVNC Server 設定裡勾選「Enable File Transfer」。");
        }
    }

    public List<RemoteEntry> ListDrives()
    {
        lock (_op)
        {
            _c.SendFt(RfbConnection.FtDirContentRequest, RDrivesList, 0);
            var msg = ReadUntil(RfbConnection.FtDirPacket);
            if ((msg.Param & ~UnicodeFlag) != ADrivesList)
                throw new VncException("列磁碟失敗：伺服器回應異常");

            // 資料格式："C:l\0D:c\0..."，每段第 3 個字元是磁碟型別
            var list = new List<RemoteEntry>();
            string raw = Encoding.ASCII.GetString(msg.Data);
            foreach (string tok in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.Length < 2 || tok[1] != ':') continue;
                list.Add(new RemoteEntry { Name = $"{tok[0]}:", IsDirectory = true, ModifiedUtc = DateTime.UtcNow });
            }
            return list;
        }
    }

    /// <summary>列出遠端目錄。dirPath 例如 "C:\\MES\\"，結尾要有反斜線。</summary>
    public List<RemoteEntry> ListDirectory(string dirPath)
    {
        if (!dirPath.EndsWith('\\')) dirPath += "\\";
        lock (_op)
        {
            ushort param = (ushort)(RDirContent | (_c.SupportsUnicode ? UnicodeFlag : 0));
            _c.SendFt(RfbConnection.FtDirContentRequest, param, 0, Enc(dirPath));

            // 第一則一定是「目錄路徑回音」或「無法讀取」
            var first = ReadUntil(RfbConnection.FtDirPacket);
            ushort firstParam = (ushort)(first.Param & ~UnicodeFlag);
            if (firstParam == ADirInaccessible)
                throw new VncException($"無法讀取資料夾「{dirPath}」（可能沒有權限或磁碟未就緒）。");
            // 舊版伺服器（1.4.x，協定 v3）打不開資料夾時，送一則「空的」路徑回音（長度 0）後就不再送任何東西
            if (firstParam != ADirectory || first.Data.Length == 0)
                throw new VncException($"找不到資料夾「{dirPath}」，或沒有權限讀取。");
            // first 是路徑回音，略過。

            var list = new List<RemoteEntry>();
            while (true)
            {
                var msg = ReadUntil(RfbConnection.FtDirPacket);
                ushort baseParam = (ushort)(msg.Param & ~UnicodeFlag);
                bool unicode = (msg.Param & UnicodeFlag) != 0;

                if (baseParam != ADirectory && baseParam != AFile)
                    break; // contentParam == 0 的結束標記

                var entry = ParseFindData(msg.Data, unicode);
                if (entry == null) continue;             // "." 或 ".."
                list.Add(entry);
            }
            return list;
        }
    }

    static RemoteEntry? ParseFindData(byte[] d, bool unicode)
    {
        if (d.Length < 44) return null;
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0, 4));
        long writeTime = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(20, 8));
        uint sizeHigh = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(28, 4));
        uint sizeLow = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(32, 4));
        uint reserved0 = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(36, 4));

        ReadOnlySpan<byte> nameBytes = d.AsSpan(44);
        string name = unicode
            ? Encoding.Unicode.GetString(nameBytes).TrimEnd('\0')
            : Encoding.GetEncoding(950).GetString(nameBytes).TrimEnd('\0');
        name = name.Split('\0')[0];
        if (name is "." or ".." or "") return null;

        bool isDir = (attrs & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY
        DateTime mtime;
        try { mtime = DateTime.FromFileTimeUtc(writeTime); }
        catch { mtime = DateTime.UtcNow; }

        return new RemoteEntry
        {
            Name = name,
            IsDirectory = isDir,
            Size = isDir ? 0 : (((long)sizeHigh << 32) | sizeLow),
            ModifiedUtc = mtime,
            Inaccessible = reserved0 == FdInaccessible,
        };
    }

    /// <summary>下載遠端檔案到 dest。filePath 例如 "C:\\MES\\log.txt"。</summary>
    public void Download(string filePath, Stream dest)
    {
        lock (_op)
        {
            // size=1 要求伺服器每塊先 zlib 壓縮（壓了反而變大的塊會自動改送原始資料）。
            // CSV、log 這類文字檔可以小好幾倍，經跳板時特別有感。
            _c.SendFt(RfbConnection.FtFileTransferRequest, 0, 1, Enc(filePath));

            var header = ReadUntil(RfbConnection.FtFileHeader);
            if (header.Size == FileSizeError && (_c.OldFtProtocol || header.SizeH == FileSizeError))
                throw new VncException($"無法開啟遠端檔案「{filePath}」（可能不存在、被鎖定或沒有權限）。");

            // 回覆伺服器「可以開始傳了」（size 填回檔案大小低位，不是 -1 即可）
            _c.SendFt(RfbConnection.FtFileHeader, 0, header.Size);

            while (true)
            {
                var pkt = _c.TakeFt();
                switch (pkt.ContentType)
                {
                    case RfbConnection.FtFilePacket:
                        if (pkt.Size == 1)      // zlib 壓縮
                            Decompress(pkt.Data, dest);
                        else if (pkt.Size == 2) // 空封包（差異傳輸），正常下載不會出現
                            { }
                        else                     // 原始資料
                            dest.Write(pkt.Data);
                        break;
                    case RfbConnection.FtEndOfFile:
                        return;
                    case RfbConnection.FtAbortFileTransfer:
                        throw new VncException($"下載「{filePath}」被伺服器中止。");
                    default:
                        throw new VncException($"下載時收到非預期的訊息（contentType={pkt.ContentType}）。");
                }
            }
        }
    }

    /// <summary>zlib 格式（跟伺服器的 uncompress() 相容）。</summary>
    internal static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    static void Decompress(byte[] zlibData, Stream dest)
    {
        using var ms = new MemoryStream(zlibData);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        z.CopyTo(dest);
    }

    /// <summary>上傳檔案到遠端。remotePath 例如 "C:\\MES\\new.txt"。</summary>
    public void Upload(string remotePath, Stream src, long length, DateTime modifiedUtc)
    {
        lock (_op)
        {
            // 伺服器把這個時間當 UTC（SystemTimeToFileTime）設成檔案時間
            string stamp = modifiedUtc.ToUniversalTime().ToString("MM/dd/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            byte[] nameBytes = Enc(remotePath + "," + stamp);

            uint sizeLow = (uint)(length & 0xFFFFFFFF);
            uint sizeHigh = (uint)((length >> 32) & 0xFFFFFFFF);
            byte[] tail = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tail, sizeHigh); // sizeH 在線上也是大端序

            _c.SendFt(RfbConnection.FtFileTransferOffer, 0, sizeLow, nameBytes,
                      _c.OldFtProtocol ? null : tail);

            // 可能先收到檔案校驗碼（差異傳輸），略過；接著是 AcceptHeader
            FtMessage accept;
            while (true)
            {
                var m = _c.TakeFt();
                if (m.ContentType == RfbConnection.FtFileChecksums) continue;
                if (m.ContentType == RfbConnection.FtFileAcceptHeader) { accept = m; break; }
                if (m.ContentType == RfbConnection.FtAbortFileTransfer)
                    throw new VncException($"上傳「{remotePath}」被伺服器拒絕。");
                throw new VncException($"上傳時收到非預期的訊息（contentType={m.ContentType}）。");
            }
            if (accept.Size == FileSizeError)
                throw new VncException($"伺服器無法建立檔案「{remotePath}」（可能沒有權限、磁碟空間不足或路徑不存在）。");

            // 一塊一塊送；壓縮後比較小就送 zlib（size=1，伺服器會解開），否則送原始資料（size=0）
            byte[] buf = new byte[ChunkSize];
            int read;
            while ((read = ReadFull(src, buf)) > 0)
            {
                byte[] compressed = Compress(buf.AsSpan(0, read));
                if (compressed.Length < read)
                    _c.SendFt(RfbConnection.FtFilePacket, 0, 1, compressed);
                else
                    _c.SendFt(RfbConnection.FtFilePacket, 0, 0, read == buf.Length ? buf : buf.AsSpan(0, read).ToArray());
            }
            _c.SendFt(RfbConnection.FtEndOfFile, 0, 0);
        }
    }

    static int ReadFull(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = s.Read(buf, off, buf.Length - off);
            if (r <= 0) break;
            off += r;
        }
        return off;
    }

    public void MakeDirectory(string path)
    {
        lock (_op)
        {
            _c.SendFt(RfbConnection.FtCommand, CDirCreate, 0, Enc(path));
            var r = ReadUntil(RfbConnection.FtCommandReturn);
            if (r.Size == FileSizeError) throw new VncException($"建立資料夾「{path}」失敗。");
        }
    }

    /// <summary>刪除檔案或資料夾（伺服器會自己判斷）。</summary>
    public void Delete(string path)
    {
        lock (_op)
        {
            _c.SendFt(RfbConnection.FtCommand, CFileDelete, 0, Enc(path));
            var r = ReadUntil(RfbConnection.FtCommandReturn);
            if (r.Size == FileSizeError) throw new VncException($"刪除「{path}」失敗（可能不是空資料夾或沒有權限）。");
        }
    }

    /// <summary>改名／搬移。兩個都是完整路徑。</summary>
    public void Rename(string oldPath, string newPath)
    {
        lock (_op)
        {
            // 伺服器以 '*' 分隔新舊名稱
            _c.SendFt(RfbConnection.FtCommand, CFileRename, 0, Enc(oldPath + "*" + newPath));
            var r = ReadUntil(RfbConnection.FtCommandReturn);
            if (r.Size == FileSizeError) throw new VncException($"把「{oldPath}」改名為「{newPath}」失敗。");
        }
    }

    /// <summary>從佇列讀到指定型別的訊息（過程中略過不相干的）。</summary>
    FtMessage ReadUntil(byte contentType, int timeoutMs = 60000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int remain = (int)Math.Max(1000, deadline - Environment.TickCount64);
            var msg = _c.TakeFt(remain);
            if (msg.ContentType == contentType) return msg;
            if (msg.ContentType == RfbConnection.FtAbortFileTransfer)
                throw new VncException("操作被伺服器中止。");
            // 其他訊息略過，繼續等
            if (Environment.TickCount64 > deadline)
                throw new VncException("等待伺服器回應逾時。");
        }
    }
}
