using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace UvncFtpJump
{
    /// <summary>
    /// UvncFtpJump：RDP 跳板上的通道程式（取代 UvncFtpBridge --remote）。
    ///   UvncFtpJump [磁碟代號 | 完整重新導向路徑] [--route 跳板名稱] [--name 顯示名稱] [--source 來源資料夾]
    /// 同一個工作階段只跑一份：
    ///   • 已經有一份在跑、來源沒有不一樣 → 什麼都不做直接結束（不打擾正在用的通道）；
    ///   • 來源不一樣（有更新）→ 叫舊的結束 → 複製新檔 → 開新版；
    ///   • 沒有在跑 → 有更新就先更新，然後開始常駐。
    /// </summary>
    internal static class Program
    {
        const string MutexName = @"Local\UvncFtpJump";
        const string ExitEventName = @"Local\UvncFtpJump.Exit";
        /// <summary>剛更新完重開的那一份帶這個：不再比對（避免時間誤差造成一直重開）。</summary>
        const string UpdatedFlag = "--updated";

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string self = Application.ExecutablePath;
            string own = Path.GetDirectoryName(self);
            Updater.CleanupOld(self);

            string error;
            var options = Options.Parse(args, out error);
            var state = new JumpState { Route = options.Route, Name = options.Name };
            if (error != null)
            {
                state.SetPhase(Phase.Error, error);
                state.Log(error);
                state.SetVersion(VersionText(self, ""));
                Application.Run(new StatusForm(state));
                return 2;
            }

            // 有沒有更新：只有 start.bat 帶 --source、而且來源不是自己這個資料夾時才比
            Updater updater = null;
            string versionNote;
            if (options.Updated) versionNote = "（剛更新）";
            else if (options.Source == null) versionNote = "（沒有指定來源，不檢查更新）";
            else if (SamePath(options.Source, own)) versionNote = "（從桌面直接開啟，不檢查更新）";
            else
            {
                string checkError;
                updater = Updater.Check(options.Source, own, out checkError);
                versionNote = updater == null ? "（無法比對：" + checkError + "）"
                    : updater.NeedsUpdate ? "（與本機不同，更新中）" : "（與本機相同）";
            }
            bool needUpdate = updater != null && updater.NeedsUpdate;

            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned = TryAcquire(mutex, 0);
                if (!owned)
                {
                    // 已經有一份在跑：沒有更新就完全不動它
                    if (!needUpdate)
                    {
                        Report(options, own, "same");
                        return 0;
                    }
                    Report(options, own, "updating");
                    owned = StopOthers(mutex, own);
                    if (!owned)
                    {
                        Report(options, own, "failed");
                        state.SetPhase(Phase.Error, "舊的 UvncFtpJump 沒有結束，無法更新。請關掉它後再執行一次 start.bat。");
                        state.SetVersion(VersionText(self, versionNote));
                        Application.Run(new StatusForm(state));
                        return 1;
                    }
                }

                state.SetVersion(VersionText(self, versionNote));
                var form = new StatusForm(state);
                TunnelWorker worker = null;
                bool released = false;

                if (!needUpdate) Report(options, own, options.Updated ? "updated" : "started");
                else if (owned) Report(options, own, "updating");
                if (needUpdate)
                {
                    state.SetPhase(Phase.Updating, "複製新版檔案，完成後自動重新開啟");
                    state.Log("發現新版：" + string.Join("、", updater.Changed.Concat(updater.Extra).ToArray()));
                    form.Shown += (s, e) => new Thread(() =>
                    {
                        try
                        {
                            updater.Apply(state, self);
                            state.Log("更新完成，重新開啟");
                            form.BeginInvoke(new Action(() =>
                            {
                                // Mutex 要由擁有它的主執行緒放掉，新版才拿得到
                                mutex.ReleaseMutex();
                                released = true;
                                Process.Start(new ProcessStartInfo(self, Restart(args)) { WorkingDirectory = own, UseShellExecute = false });
                                form.Close();
                            }));
                        }
                        catch (Exception ex)
                        {
                            state.Log("更新失敗：" + ex.Message);
                            Report(options, own, "failed");
                            state.SetVersion(VersionText(self, "（更新失敗，用目前這份繼續）"));
                            form.BeginInvoke(new Action(() =>
                            {
                                worker = StartWorker(state, own, options);
                                WatchExit(form);
                            }));
                        }
                    }) { IsBackground = true }.Start();
                }
                else
                {
                    form.Shown += (s, e) =>
                    {
                        worker = StartWorker(state, own, options);
                        WatchExit(form);
                    };
                }

                Application.Run(form);
                if (worker != null) worker.Stop();
                if (!released)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
            }
            return 0;
        }

        static TunnelWorker StartWorker(JumpState state, string own, Options options)
        {
            var worker = new TunnelWorker(state, own, options.ExplicitPath, options.Profiles, options.Route);
            worker.Start();
            return worker;
        }

        /// <summary>有新版的那一份叫這份結束（ExitEventName）：關視窗＝收掉 ft.exe、放掉 Mutex。</summary>
        static void WatchExit(Form form)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            new Thread(() =>
            {
                signal.WaitOne();
                try { form.BeginInvoke(new Action(form.Close)); }
                catch (InvalidOperationException) { }
            }) { IsBackground = true, Name = "ExitSignal" }.Start();
        }

        /// <summary>
        /// 叫正在跑的那份結束並等它放掉 Mutex；10 秒沒反應就直接結束同一個工作階段裡的 UvncFtpJump，
        /// 以及這個資料夾的 ft.exe（Win7 上工作物件可能沒綁到，ft.exe 會留著鎖住檔案）。
        /// </summary>
        static bool StopOthers(Mutex mutex, string own)
        {
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName)) signal.Set();
            if (TryAcquire(mutex, 10000)) return true;
            var me = Process.GetCurrentProcess();
            foreach (var other in Process.GetProcessesByName("UvncFtpJump"))
            {
                try
                {
                    if (other.Id != me.Id && other.SessionId == me.SessionId) other.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            }
            bool owned = TryAcquire(mutex, 5000);
            string ownFt = Path.Combine(own, "ft.exe");
            foreach (var ft in Process.GetProcessesByName("ft"))
            {
                try
                {
                    if (ft.SessionId == me.SessionId && SamePath(ft.MainModule.FileName, ownFt)) ft.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            }
            return owned;
        }

        /// <summary>
        /// 告訴本機 mRemoteNG 這次 start.bat 的結果（它在等通道時讀，才知道要不要等更新完成）：
        /// 寫在來源資料夾的上一層（本機 %TEMP%ﯞploy\jump-&lt;路由&gt;.txt）。
        /// same＝已在跑、沒有更新；started＝剛啟動；updating＝更新中；updated＝更新完成、新版已啟動。
        /// 只有 start.bat 帶 --source 時才寫；寫不進去（RDP 斷了）就算了，最多等 3 秒。
        /// </summary>
        static void Report(Options options, string own, string result)
        {
            if (options.Source == null || SamePath(options.Source, own)) return;
            string folder = Path.GetDirectoryName(options.Source.TrimEnd('\\'));
            if (string.IsNullOrEmpty(folder)) return;
            string file = Path.Combine(folder, "jump-" + (options.Route ?? "default") + ".txt");
            var task = System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                try { File.WriteAllText(file, result); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            try { task.Wait(3000); }
            catch (AggregateException) { }
        }

        static bool TryAcquire(Mutex mutex, int timeoutMs)
        {
            try { return mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { return true; } // 上一份被強制結束：Mutex 一樣歸我們
        }

        static string VersionText(string self, string note)
        {
            return File.GetLastWriteTime(self).ToString("yyyy-MM-dd HH:mm") + note;
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// <summary>新版用同樣的參數重開，多帶 --updated。</summary>
        static string Restart(string[] args)
        {
            var parts = args.Where(a => a != UpdatedFlag).Select(TunnelWorker.Quote).ToList();
            parts.Add(UpdatedFlag);
            return string.Join(" ", parts.ToArray());
        }

        sealed class Options
        {
            public string Route;
            public string Name;
            public string Source;
            public bool Updated;
            /// <summary>給了完整重新導向路徑（本機 TEMP 不在預設位置時）。</summary>
            public string ExplicitPath;
            /// <summary>\\tsclient\磁碟\Users（預設 C）。</summary>
            public string Profiles;

            public static Options Parse(string[] args, out string error)
            {
                error = null;
                var options = new Options();
                var positional = new List<string>();
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--route" && i + 1 < args.Length) options.Route = args[++i];
                    else if (arg == "--name" && i + 1 < args.Length) options.Name = args[++i];
                    else if (arg == "--source" && i + 1 < args.Length) options.Source = args[++i].TrimEnd('\\');
                    else if (arg == UpdatedFlag) options.Updated = true;
                    else if (arg.StartsWith("--"))
                    {
                        error = "不認得的參數：" + arg + "。" + Usage;
                        return options;
                    }
                    else positional.Add(arg);
                }
                if (options.Route != null && !RouteName.IsValid(options.Route))
                {
                    error = "通道名稱不合法：" + options.Route + "（只能有英數字與 . - _）";
                    return options;
                }
                if (positional.Count > 1)
                {
                    error = "參數太多。" + Usage;
                    return options;
                }
                string target = positional.Count == 1 ? positional[0] : "C";
                if (Path.IsPathRooted(target) && target.Length > 2)
                {
                    options.ExplicitPath = target;
                    return options;
                }
                string drive = target.TrimEnd(':');
                if (drive.Length != 1 || !char.IsLetter(drive[0]) || drive[0] > 'z')
                {
                    error = "請給磁碟代號（例如 C）或完整的重新導向路徑。" + Usage;
                    return options;
                }
                options.Profiles = @"\\tsclient\" + char.ToUpperInvariant(drive[0]) + @"\Users";
                return options;
            }

            const string Usage = "用法：UvncFtpJump [磁碟代號 | 完整重新導向路徑] [--route 跳板名稱] [--name 顯示名稱] [--source 來源資料夾]";
        }
    }
}
