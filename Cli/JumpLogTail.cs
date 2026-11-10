using System.Text;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 把跳板端寫回本機的記錄（%TEMP%ﯞploy\jump-路由.log）併進 launcher.log，查問題只要看一個檔。
/// 每行前面的時間是跳板自己的時鐘（跳板時鐘可能跟本機差好幾分鐘），launcher.log 最前面的才是本機時間。
/// </summary>
internal sealed class JumpLogTail : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly string route;
    readonly string path;
    long position;

    public JumpLogTail(string route)
    {
        this.route = route;
        path = Path.Combine(Path.GetTempPath(), "ufbdeploy", $"jump-{route}.log");
        // 只看這次開始之後的；之前的還在那個檔裡
        try { position = File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { position = 0; }
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        byte[] pending = [];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                pending = ReadNew(pending);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { await Task.Delay(500, stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    byte[] ReadNew(byte[] pending)
    {
        if (!File.Exists(path)) return pending;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length < position)
        {
            position = 0; // 跳板端把太大的檔清掉重寫了
            pending = [];
        }
        if (file.Length == position) return pending;
        file.Seek(position, SeekOrigin.Begin);
        byte[] fresh = new byte[file.Length - position];
        file.ReadExactly(fresh);
        position += fresh.Length;
        byte[] all = [.. pending, .. fresh];
        int end = Array.LastIndexOf(all, (byte)'\n'); // 最後一行還沒寫完就留到下次（也避免切到半個中文字）
        if (end < 0) return all;
        foreach (string line in Encoding.UTF8.GetString(all, 0, end).Split('\n'))
        {
            string text = line.TrimEnd('\r');
            if (text.Length > 0) Log.Info($"跳板 [{route}] {text}");
        }
        return all[(end + 1)..];
    }

    public void Dispose()
    {
        stop.Cancel();
    }
}
