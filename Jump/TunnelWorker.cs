using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UvncFtpJump
{
    /// <summary>
    /// 跳板端通道（原本的 UvncFtpBridge --remote）：經 RDP 磁碟重新導向找到本機的通道資料夾，用旁邊的 ft.exe 接上。
    /// 常駐：RDP 斷線不結束、ft.exe 也不主動停（連回來就接著用，不用重連）；本機重開（標記檔內容變了）就換新的 ft.exe；
    /// ft.exe 自己結束就等一下再開。只有關視窗才結束。
    /// </summary>
    internal sealed class TunnelWorker
    {
        /// <summary>多跳板通道的資料夾：本機 %TEMP%\UvncFtpBridge\Routes\&lt;路由&gt;（跟 UvncFtpBridge 的 FileTunnelSession 一樣）。</summary>
        const string RoutesFolderName = "Routes";
        /// <summary>讀不到標記檔超過這麼久，畫面改顯示「RDP 斷線」（ft.exe 照跑）。</summary>
        const int ShowDisconnectedAfterMs = 5000;

        enum MarkerState { Read, Gone, Unknown }

        readonly JumpState state;
        readonly string baseDir;
        readonly string explicitPath;
        readonly string profiles;
        readonly string routeArg;
        readonly HashSet<string> identities;
        readonly object gate = new object();
        volatile bool stopping;
        ChildJob currentJob;
        Thread thread;
        /// <summary>曾經看到過 \\tsclient：之後再看不到就是 RDP 斷線（不是一開始就沒開磁碟重新導向）。</summary>
        bool sawRedirection;
        long missingSince = -1;
        readonly Stopwatch clock = Stopwatch.StartNew();

        public TunnelWorker(JumpState state, string baseDir, string explicitPath, string profiles, string routeArg)
        {
            this.state = state;
            this.baseDir = baseDir;
            this.explicitPath = explicitPath;
            this.profiles = profiles;
            this.routeArg = routeArg;
            identities = routeArg == null
                ? OwnIdentities()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { routeArg };
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Tunnel" };
            thread.Start();
        }

        /// <summary>關視窗時呼叫：收掉 ft.exe。</summary>
        public void Stop()
        {
            stopping = true;
            lock (gate)
            {
                if (currentJob != null) currentJob.Dispose();
                currentJob = null;
            }
        }

        void Run()
        {
            string ft = Path.Combine(baseDir, "ft.exe");
            if (!File.Exists(ft))
            {
                state.SetPhase(Phase.Error, "找不到 ft.exe，請把它放在 UvncFtpJump.exe 旁邊後重新開啟。");
                state.Log("找不到 " + ft);
                return;
            }
            var extraOptions = ExtraOptions();
            if (extraOptions.Count > 0) state.Log("ft.exe 額外參數（ft-options.txt）：" + string.Join(" ", extraOptions.ToArray()));
            if (routeArg == null && explicitPath == null)
                state.Log("這台跳板的通道名稱：" + string.Join("、", identities.OrderBy(x => x).ToArray()));

            while (!stopping)
            {
                string shared = WaitForLocalTunnel();
                if (shared == null) return;
                string marker = Path.Combine(shared, "active.marker");
                string session = null;
                for (int attempt = 0; session == null && attempt < 10 && !stopping; attempt++)
                {
                    string content;
                    if (ReadMarker(marker, out content) == MarkerState.Read) session = content;
                    else Thread.Sleep(200);
                }
                if (session == null) continue;
                RunFt(ft, shared, marker, session, extraOptions);
            }
        }

        /// <summary>一個本機通道 session：開 ft.exe，盯著標記檔，直到本機重開／關閉或 ft.exe 結束。</summary>
        void RunFt(string ft, string shared, string marker, string session, List<string> extraOptions)
        {
            state.Log("找到本機通道：" + shared);
            state.SetTunnel("已找到（" + session + "）");
            var args = new StringBuilder();
            args.Append("--read ").Append(Quote(Path.Combine(shared, "to-remote.dat")));
            args.Append(" --write ").Append(Quote(Path.Combine(shared, "to-local.dat")));
            foreach (string option in extraOptions) args.Append(' ').Append(Quote(option));
            var start = new ProcessStartInfo(ft, args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = baseDir,
                // 沒有自己的主控台時 ft.exe 用系統字碼頁（例如 Big5）輸出
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
            };
            var process = new Process { StartInfo = start };
            string counterpart = null;
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null) return;
                int at = e.Data.IndexOf("Counterpart:", StringComparison.Ordinal);
                if (at < 0)
                {
                    if (e.Data.Trim().Length > 0) state.Log("ft.exe：" + e.Data.Trim());
                    return;
                }
                string now = e.Data.Substring(at + "Counterpart:".Length).Trim().Split(' ')[0];
                if (now == counterpart) return;
                counterpart = now;
                if (now == "Online")
                {
                    state.Log("本機已回應，通道接上");
                    state.SetPhase(Phase.Connected, "通道正常，可以使用 VNC／WinSCP");
                }
                else
                {
                    state.Log("本機沒有回應，等待中");
                    state.SetPhase(Phase.Connecting, "已找到本機通道，等待本機回應…");
                }
            };
            process.OutputDataReceived += onLine;
            process.ErrorDataReceived += onLine;
            try
            {
                if (!process.Start()) throw new IOException("ft.exe 沒有啟動。");
            }
            catch (Exception ex)
            {
                state.SetPhase(Phase.Error, "無法啟動 ft.exe：" + ex.Message);
                state.Log("無法啟動 ft.exe：" + ex.Message);
                process.Dispose();
                Thread.Sleep(5000);
                return;
            }
            var job = new ChildJob(process);
            lock (gate)
            {
                if (stopping)
                {
                    job.Dispose();
                    process.Dispose();
                    return;
                }
                currentJob = job;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            state.SetFt("執行中（PID " + process.Id + "）");
            state.SetPhase(Phase.Connecting, "已找到本機通道，等待本機回應…");
            state.Log("ft.exe 啟動");

            long unreadSince = -1;
            int gone = 0;
            string reason = null;
            while (!stopping && !process.HasExited)
            {
                string content;
                var markerState = ReadMarker(marker, out content);
                if (markerState == MarkerState.Read)
                {
                    if (content != session)
                    {
                        reason = "本機通道重新開啟過，換新的連線";
                        break;
                    }
                    gone = 0;
                    if (unreadSince >= 0)
                    {
                        unreadSince = -1;
                        state.Log("RDP 已重新連線，通道繼續使用");
                        state.SetTunnel("已找到（" + session + "）");
                        state.SetPhase(counterpart == "Online" ? Phase.Connected : Phase.Connecting,
                            counterpart == "Online" ? "通道正常，可以使用 VNC／WinSCP" : "已找到本機通道，等待本機回應…");
                    }
                }
                else
                {
                    // 標記檔不見、而 \\tsclient 本身還看得到：本機關掉了通道（mRemoteNG 關了）。
                    // \\tsclient 也看不到（或讀取卡住）：RDP 斷線，ft.exe 照跑，連回來就接著用。
                    if (markerState == MarkerState.Gone && TimedDirectoryExists(Path.GetPathRoot(shared)))
                    {
                        if (++gone >= 2)
                        {
                            reason = "本機已關閉通道";
                            break;
                        }
                    }
                    else gone = 0;
                    if (unreadSince < 0) unreadSince = clock.ElapsedMilliseconds;
                    else if (clock.ElapsedMilliseconds - unreadSince >= ShowDisconnectedAfterMs && state.Take().Phase != Phase.Disconnected)
                    {
                        state.Log("看不到 \\\\tsclient（RDP 斷線），等待重新連線");
                        state.SetTunnel("看不到 \\\\tsclient");
                        state.SetPhase(Phase.Disconnected, "程式不會結束，連回來後自動接上");
                    }
                }
                Thread.Sleep(500);
            }

            bool exited = process.HasExited;
            lock (gate)
            {
                job.Dispose();
                if (currentJob == job) currentJob = null;
            }
            if (stopping) return;
            if (exited)
            {
                string code;
                try { code = process.ExitCode.ToString(); }
                catch (InvalidOperationException) { code = "?"; }
                state.Log("ft.exe 結束（代碼 " + code + "），2 秒後重新啟動");
                state.SetFt("已結束（代碼 " + code + "）");
                state.SetPhase(Phase.Error, "ft.exe 結束（代碼 " + code + "），2 秒後重新啟動");
            }
            else
            {
                state.Log(reason);
                state.SetFt("已停止");
            }
            process.Dispose();
            Thread.Sleep(2000);
        }

        /// <summary>等本機的通道出現，回傳資料夾；關視窗時回 null。</summary>
        string WaitForLocalTunnel()
        {
            string lastStatus = null;
            while (!stopping)
            {
                string status;
                Phase phase = Phase.WaitingLocal;
                if (explicitPath != null)
                {
                    if (TimedFileExists(Path.Combine(explicitPath, "active.marker"))) return Found(explicitPath);
                    if (TimedDirectoryExists(Path.GetPathRoot(explicitPath))) status = "等待本機通道：" + explicitPath;
                    else
                    {
                        status = Missing(out phase);
                    }
                }
                else
                {
                    Scan scan = TimedDirectoryExists(profiles) ? Timed(() => ScanTunnels(), null) : null;
                    if (scan == null) status = Missing(out phase);
                    else
                    {
                        sawRedirection = true;
                        missingSince = -1;
                        if (scan.Found.Count == 1) return Found(scan.Found[0]);
                        if (scan.Found.Count > 1)
                            status = "找到多個本機通道，請關掉多餘的，或用 --route 指定";
                        else if (scan.OtherRoutes.Count > 0)
                            status = "只找到其他跳板的通道：" + string.Join("、", scan.OtherRoutes.ToArray()) +
                                     "。如果其中一個就是這台，請用 --route 指定";
                        else
                            status = "請在本機 mRemoteNG 開啟經這台跳板的機台";
                    }
                }
                if (status != lastStatus)
                {
                    if (phase == Phase.Disconnected) state.Log("看不到 \\\\tsclient（RDP 斷線），等待重新連線");
                    else state.Log(status);
                    state.SetTunnel(phase == Phase.Disconnected ? "看不到 \\\\tsclient" : "未找到");
                    state.SetPhase(phase, phase == Phase.Disconnected ? "程式不會結束，連回來後自動接上" : status);
                    lastStatus = status;
                }
                Thread.Sleep(2000);
            }
            return null;
        }

        string Found(string folder)
        {
            sawRedirection = true;
            missingSince = -1;
            return folder;
        }

        /// <summary>\\tsclient 看不到：連上過就是 RDP 斷線，不然是沒開磁碟重新導向。</summary>
        string Missing(out Phase phase)
        {
            if (sawRedirection)
            {
                if (missingSince < 0) missingSince = clock.ElapsedMilliseconds;
                phase = Phase.Disconnected;
                return "RDP 斷線";
            }
            phase = Phase.WaitingLocal;
            return "看不到 " + (profiles ?? explicitPath) + "：請在 RDP 連線設定開啟「磁碟機」重新導向後重新連線";
        }

        sealed class Scan
        {
            public readonly List<string> Found = new List<string>();
            public readonly SortedSet<string> OtherRoutes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>掃描 \\tsclient\磁碟\Users 底下的本機通道資料夾（在 Timed 裡跑：RDP 斷線時可能卡住）。</summary>
        Scan ScanTunnels()
        {
            var scan = new Scan();
            try
            {
                foreach (string user in Directory.GetDirectories(profiles))
                {
                    string bridgeTemp = Path.Combine(user, @"AppData\Local\Temp\UvncFtpBridge");
                    // 單一通道（Start-FileTunnel.cmd）；有指定 --route 時不看
                    if (routeArg == null)
                    {
                        string candidate = Path.Combine(bridgeTemp, "FileTunnel");
                        try { if (File.Exists(Path.Combine(candidate, "active.marker"))) scan.Found.Add(candidate); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    // mRemoteNG 主機模式：每台跳板一個資料夾，用跳板的名稱或 IP 命名
                    string routesDir = Path.Combine(bridgeTemp, RoutesFolderName);
                    try
                    {
                        if (!Directory.Exists(routesDir)) continue;
                        foreach (string routeDir in Directory.GetDirectories(routesDir))
                        {
                            if (!File.Exists(Path.Combine(routeDir, "active.marker"))) continue;
                            string name = Path.GetFileName(routeDir);
                            if (identities.Contains(name)) scan.Found.Add(routeDir);
                            else scan.OtherRoutes.Add(name);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return scan;
        }

        /// <summary>
        /// 這台跳板的通道名稱：電腦名稱、DNS 名稱與每個 IP。mRemoteNG 用跳板連線的主機名稱／IP 命名資料夾，
        /// start.bat 都會帶 --route，這裡只給手動執行時自動比對用。
        /// </summary>
        static HashSet<string> OwnIdentities()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = value =>
            {
                if (string.IsNullOrEmpty(value) || value.Trim().Length == 0) return;
                string id = RouteName.Sanitize(value);
                if (RouteName.IsValid(id)) names.Add(id);
            };
            add(Environment.MachineName);
            try
            {
                string host = System.Net.Dns.GetHostName();
                add(host);
                add(System.Net.Dns.GetHostEntry(host).HostName);
            }
            catch (System.Net.Sockets.SocketException) { }
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (System.Net.IPAddress.IsLoopback(address.Address)) continue;
                        add(address.Address.ToString());
                    }
                }
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { }
            return names;
        }

        /// <summary>exe 旁的 ft-options.txt：額外的 ft.exe 參數，空白分隔，# 開頭的行不算。</summary>
        List<string> ExtraOptions()
        {
            string path = Path.Combine(baseDir, "ft-options.txt");
            if (!File.Exists(path)) return new List<string>();
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#"))
                .SelectMany(line => line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .ToList();
        }

        internal static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            // CommandLineToArgvW 規則：引號前的反斜線要加倍
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        // RDP 斷線時 \\tsclient 消失，讀它可能卡住很久（不會馬上報錯）。所以讀標記檔、檢查資料夾都丟到背景、限時 3 秒；
        // 卡住就當作「讀不到」。同一時間只留一個卡住的讀取，不越積越多。
        const int TsclientTimeoutMs = 3000;
        Task pendingIo;

        T Timed<T>(Func<T> io, T onTimeout)
        {
            if (pendingIo != null && !pendingIo.IsCompleted) return onTimeout;
            var task = Task.Factory.StartNew(io);
            pendingIo = task;
            try
            {
                return task.Wait(TsclientTimeoutMs) ? task.Result : onTimeout;
            }
            catch (AggregateException)
            {
                return onTimeout;
            }
        }

        bool TimedDirectoryExists(string path) => path != null && Timed(() => Directory.Exists(path), false);

        bool TimedFileExists(string path) => Timed(() => File.Exists(path), false);

        sealed class MarkerRead
        {
            public MarkerState State;
            public string Content;
        }

        /// <summary>不鎖檔讀標記檔：本機端會一邊改寫、刪除它。</summary>
        MarkerState ReadMarker(string path, out string content)
        {
            var result = Timed(() => ReadMarkerNow(path), new MarkerRead { State = MarkerState.Unknown });
            content = result.Content;
            return result.State;
        }

        static MarkerRead ReadMarkerNow(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    return new MarkerRead { State = MarkerState.Read, Content = reader.ReadToEnd().Trim() };
                }
            }
            catch (FileNotFoundException) { return new MarkerRead { State = MarkerState.Gone }; }
            catch (DirectoryNotFoundException) { return new MarkerRead { State = MarkerState.Gone }; }
            catch (IOException) { return new MarkerRead { State = MarkerState.Unknown }; }
            catch (UnauthorizedAccessException) { return new MarkerRead { State = MarkerState.Unknown }; }
        }
    }

    /// <summary>跟 UvncFtpBridge 的 Route 一樣：通道名稱只允許英數字與 . - _。</summary>
    internal static class RouteName
    {
        public static bool IsValid(string id) =>
            id.Length > 0 && id.Length <= 64 && id.All(Allowed);

        public static string Sanitize(string host)
        {
            var chars = host.Trim().Select(c => Allowed(c) ? c : '_').ToArray();
            string id = new string(chars).Trim('_');
            return id.Length > 64 ? id.Substring(0, 64) : id;
        }

        static bool Allowed(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
    }
}
