using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace UvncFtpJump
{
    /// <summary>
    /// 跳板端記錄檔：一律寫到 exe 旁的 Logs\jump.log；有 --source 時另外複製一份到本機
    /// （%TEMP%ﯞploy\jump-路由.log），本機 UvncFtpBridge 會把它併進 launcher.log。
    /// 寫本機這份走 \\tsclient，RDP 斷線時可能卡住，所以放在自己的執行緒，寫不出去就留著等連回來再補。
    /// </summary>
    internal static class JumpLog
    {
        const long MaxLocalBytes = 5 * 1024 * 1024;
        const long MaxMirrorBytes = 2 * 1024 * 1024;
        const int MaxPending = 20000;

        static readonly object gate = new object();
        static readonly Queue<string> pending = new Queue<string>();
        static readonly Dictionary<string, DateTime> throttled = new Dictionary<string, DateTime>();
        static string localFile;
        static string mirrorFile;
        static bool mirrorBusy;

        public static string LocalFile => localFile;

        public static void Configure(string baseDir, string mirror)
        {
            try
            {
                string dir = Path.Combine(baseDir, "Logs");
                Directory.CreateDirectory(dir);
                localFile = Path.Combine(dir, "jump.log");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            mirrorFile = mirror;
            if (mirror != null) new Thread(MirrorLoop) { IsBackground = true, Name = "JumpLogMirror" }.Start();
        }

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + "\r\n";
            lock (gate)
            {
                AppendLocal(line);
                if (mirrorFile == null) return;
                if (pending.Count >= MaxPending) pending.Dequeue();
                pending.Enqueue(line);
            }
        }

        /// <summary>每秒重複的訊息：同一個 key 在 intervalSeconds 內只寫一次。</summary>
        public static void Throttled(string key, string message, int intervalSeconds)
        {
            lock (gate)
            {
                DateTime last;
                if (throttled.TryGetValue(key, out last) && (DateTime.UtcNow - last).TotalSeconds < intervalSeconds) return;
                throttled[key] = DateTime.UtcNow;
            }
            Write(message);
        }

        /// <summary>程式結束前等本機那份寫完（最多 timeoutMs，RDP 斷線時就算了）。</summary>
        public static void Flush(int timeoutMs)
        {
            if (mirrorFile == null) return;
            int waited = 0;
            while (waited < timeoutMs)
            {
                lock (gate)
                {
                    if (pending.Count == 0 && !mirrorBusy) return;
                }
                Thread.Sleep(50);
                waited += 50;
            }
        }

        static void AppendLocal(string line)
        {
            if (localFile == null) return;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var info = new FileInfo(localFile);
                    if (info.Exists && info.Length > MaxLocalBytes)
                    {
                        string old = localFile + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(localFile, old);
                    }
                    File.AppendAllText(localFile, line, new UTF8Encoding(false));
                    return;
                }
                catch (IOException) { Thread.Sleep(20); } // 同時有另一份 UvncFtpJump（start.bat 再跑一次）在寫
                catch (UnauthorizedAccessException) { return; }
            }
        }

        static void MirrorLoop()
        {
            while (true)
            {
                string batch;
                lock (gate)
                {
                    if (pending.Count == 0) batch = null;
                    else
                    {
                        batch = string.Concat(pending.ToArray());
                        mirrorBusy = true;
                    }
                }
                if (batch == null)
                {
                    Thread.Sleep(200);
                    continue;
                }
                int sent = batch.Length;
                bool ok = false;
                try
                {
                    var info = new FileInfo(mirrorFile);
                    if (info.Exists && info.Length > MaxMirrorBytes) File.Delete(mirrorFile);
                    File.AppendAllText(mirrorFile, batch, new UTF8Encoding(false));
                    ok = true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                lock (gate)
                {
                    if (ok)
                    {
                        // 寫的時候可能又有新的進來：只拿掉寫出去的那幾行
                        while (sent > 0 && pending.Count > 0) sent -= pending.Dequeue().Length;
                    }
                    mirrorBusy = false;
                }
                Thread.Sleep(ok ? 200 : 2000);
            }
        }
    }
}
