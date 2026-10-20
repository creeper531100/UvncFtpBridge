using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Client;

/// <summary>進度回報：(遠端路徑, 已傳 bytes, 總 bytes；不知道總量時為 -1)。</summary>
public delegate void TransferProgress(string remotePath, long done, long total);

/// <summary>命令列下載／上傳邏輯（單檔、萬用字元、整個資料夾）。</summary>
public static class Transfer
{
    /// <summary>下載一個檔案。先寫成 .part，完成才改名，失敗不會留下半個檔案。</summary>
    public static void DownloadFile(UvncFileTransfer ft, string remote, string localFile, long size,
                                    DateTime? modifiedUtc, TransferProgress? progress, CancellationToken ct)
    {
        localFile = Path.GetFullPath(localFile);
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        string part = localFile + ".part";
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var ps = new ProgressStream(fs, n => progress?.Invoke(remote, n, size), ct);
                ft.Download(remote, ps);
            }
            File.Move(part, localFile, overwrite: true);
            if (modifiedUtc is { Year: > 1980 } t)
                File.SetLastWriteTimeUtc(localFile, t);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>整個資料夾往下抓。回傳下載的檔案數。</summary>
    public static int DownloadTree(UvncFileTransfer ft, string remoteDir, string localDir,
                                   TransferProgress? progress, Action<string>? onFile, CancellationToken ct)
    {
        Directory.CreateDirectory(localDir);
        int count = 0;
        foreach (var e in ft.ListDirectory(remoteDir))
        {
            ct.ThrowIfCancellationRequested();
            string r = RemotePath.Combine(remoteDir, e.Name);
            string l = Path.Combine(localDir, RemotePath.SafeLocalName(e.Name));
            if (e.IsDirectory)
            {
                if (e.Inaccessible) { Log.Warn($"略過無法讀取的資料夾 {r}"); continue; }
                count += DownloadTree(ft, r, l, progress, onFile, ct);
            }
            else
            {
                onFile?.Invoke(r);
                DownloadFile(ft, r, l, e.Size, e.ModifiedUtc, progress, ct);
                count++;
            }
        }
        return count;
    }

    public static void UploadFile(UvncFileTransfer ft, string localFile, string remote,
                                  TransferProgress? progress, CancellationToken ct)
    {
        var fi = new FileInfo(localFile);
        using var fs = fi.OpenRead();
        var ps = new ProgressStream(fs, n => progress?.Invoke(remote, n, fi.Length), ct);
        ft.Upload(remote, ps, fi.Length, fi.LastWriteTimeUtc);
    }

    /// <summary>整個本機資料夾往上傳（遠端資料夾不存在會建立）。回傳上傳的檔案數。</summary>
    public static int UploadTree(UvncFileTransfer ft, string localDir, string remoteDir,
                                 TransferProgress? progress, Action<string>? onFile, CancellationToken ct)
    {
        try { ft.MakeDirectory(remoteDir); } catch (VncException) { /* 已存在 */ }
        int count = 0;
        foreach (var f in Directory.GetFiles(localDir))
        {
            ct.ThrowIfCancellationRequested();
            string r = RemotePath.Combine(remoteDir, Path.GetFileName(f));
            onFile?.Invoke(r);
            UploadFile(ft, f, r, progress, ct);
            count++;
        }
        foreach (var d in Directory.GetDirectories(localDir))
            count += UploadTree(ft, d, RemotePath.Combine(remoteDir, Path.GetFileName(d)), progress, onFile, ct);
        return count;
    }

    /// <summary>找遠端某個路徑的資訊（列出上一層再比對名稱）。磁碟與最上層一律當資料夾。</summary>
    public static RemoteEntry? Stat(UvncFileTransfer ft, string remote)
    {
        remote = RemotePath.Normalize(remote);
        if (remote == "" || RemotePath.IsDriveRoot(remote))
            return new RemoteEntry { Name = RemotePath.Name(remote), IsDirectory = true, ModifiedUtc = DateTime.UtcNow };
        string parent = RemotePath.Parent(remote);
        string name = RemotePath.Name(remote);
        var list = parent == "" ? ft.ListDrives() : ft.ListDirectory(parent);
        return list.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {u[i]}";
    }
}

/// <summary>包住檔案串流，讀寫時回報進度，並在取消時丟出 OperationCanceledException。</summary>
public sealed class ProgressStream(Stream inner, Action<long> onProgress, CancellationToken ct) : Stream
{
    long _done;

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        int n = inner.Read(buffer, offset, count);
        _done += n; onProgress(_done);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
        _done += count; onProgress(_done);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer);
        _done += buffer.Length; onProgress(_done);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
