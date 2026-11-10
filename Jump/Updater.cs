using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UvncFtpJump
{
    /// <summary>
    /// 跟來源（start.bat 帶的 --source，\\tsclient 上本機 mRemoteNG 準備好的那份）比對自己的資料夾：
    /// 檔案大小或修改時間不一樣（容許 2 秒，跟 robocopy 一樣）、少了檔案、或多了來源沒有的檔案（例如舊版留下的 UvncFtpBridge.exe），
    /// 就算「要更新」。不比版本新舊：只要不一樣就換成來源那份。
    /// </summary>
    internal sealed class Updater
    {
        public const string OldSuffix = ".old";

        readonly string source;
        readonly string own;

        public List<string> Changed { get; private set; }
        public List<string> Extra { get; private set; }

        Updater(string source, string own)
        {
            this.source = source;
            this.own = own;
        }

        public bool NeedsUpdate => Changed.Count + Extra.Count > 0;

        /// <summary>
        /// 比對。來源讀不到（RDP 還沒好、路徑錯）就回 null，照舊執行不更新；error 給畫面顯示原因。
        /// 經 \\tsclient 讀可能卡住，最多等 15 秒。
        /// </summary>
        public static Updater Check(string source, string own, out string error)
        {
            error = null;
            var updater = new Updater(source, own);
            var task = Task.Factory.StartNew(() => updater.Compare());
            try
            {
                if (!task.Wait(15000))
                {
                    error = "讀取來源逾時：" + source;
                    return null;
                }
                if (task.Result != null)
                {
                    error = task.Result;
                    return null;
                }
                return updater;
            }
            catch (AggregateException ex)
            {
                error = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return null;
            }
        }

        string Compare()
        {
            if (!Directory.Exists(source)) return "找不到來源：" + source;
            var sourceFiles = Directory.GetFiles(source).Select(f => new FileInfo(f)).ToList();
            if (!sourceFiles.Any(f => f.Name.Equals("UvncFtpJump.exe", StringComparison.OrdinalIgnoreCase)))
                return "來源裡沒有 UvncFtpJump.exe：" + source;
            Changed = new List<string>();
            foreach (var from in sourceFiles)
            {
                var to = new FileInfo(Path.Combine(own, from.Name));
                if (!to.Exists || to.Length != from.Length ||
                    Math.Abs((to.LastWriteTimeUtc - from.LastWriteTimeUtc).TotalSeconds) > 2)
                    Changed.Add(from.Name);
            }
            var names = new HashSet<string>(sourceFiles.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            Extra = Directory.GetFiles(own)
                .Select(Path.GetFileName)
                .Where(n => !names.Contains(n) && !n.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return null;
        }

        /// <summary>
        /// 換新版（呼叫前舊的 UvncFtpJump 和它的 ft.exe 已經結束）：
        /// 執行中的 exe 不能覆蓋但可以改名，先把自己改名成 .old，再從來源複製有變的檔案、刪掉多的檔案。
        /// 複製失敗就把自己改回來，讓這一份照舊執行。
        /// </summary>
        public void Apply(JumpState state, string selfPath)
        {
            string selfName = Path.GetFileName(selfPath);
            string parked = null;
            if (Changed.Any(n => n.Equals(selfName, StringComparison.OrdinalIgnoreCase)))
            {
                parked = selfPath + OldSuffix;
                TryDelete(parked);
                File.Move(selfPath, parked);
            }
            try
            {
                int done = 0;
                foreach (string name in Changed)
                {
                    done++;
                    state.SetPhase(Phase.Updating, "複製 " + name + "（" + done + "/" + Changed.Count + "），完成後自動重新開啟");
                    state.Log("複製 " + name);
                    CopyWithRetry(Path.Combine(source, name), Path.Combine(own, name));
                }
            }
            catch
            {
                if (parked != null && !File.Exists(selfPath))
                {
                    try { File.Move(parked, selfPath); }
                    catch (IOException) { }
                }
                throw;
            }
            foreach (string name in Extra)
            {
                try
                {
                    File.Delete(Path.Combine(own, name));
                    state.Log("刪除舊檔 " + name);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    state.Log("刪不掉 " + name + "：" + ex.Message);
                }
            }
        }

        /// <summary>ft.exe 剛被結束時，檔案可能還被鎖住一下。File.Copy 會保留修改時間，下次比對才會一樣。</summary>
        static void CopyWithRetry(string from, string to)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(from, to, true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 10)
                {
                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>上次更新留下的 .old（那時它還在執行刪不掉）；新版啟動時在背景多試幾次。</summary>
        public static void CleanupOld(string selfPath)
        {
            string parked = selfPath + OldSuffix;
            if (!File.Exists(parked)) return;
            var thread = new Thread(() =>
            {
                for (int attempt = 0; attempt < 20 && File.Exists(parked); attempt++)
                {
                    if (TryDelete(parked)) return;
                    Thread.Sleep(500);
                }
            }) { IsBackground = true };
            thread.Start();
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
