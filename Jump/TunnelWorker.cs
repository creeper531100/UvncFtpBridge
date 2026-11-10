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
    /// 跳板端通道（原本的 UvncFtpBridge --remote）：經 RDP 磁碟重新導向用旁邊的 ft.exe 接上本機的通道資料夾。
    /// 常駐、不看標記檔：ft.exe 開了就一直跑，RDP 斷線、本機關閉通道、本機重開通道都交給 ft.exe 自己處理
    /// （它會自己重新開檔、偵測對方換了 session）。我們不主動關它，才不會在通道剛接上時又斷一次。
    /// ft.exe 自己結束才重開。只有關視窗才結束。
    /// 資料夾位置：start.bat 帶的 --source 推得出來就直接用（不用等資料夾出現）；否則掃 \\tsclient 找。
    /// </summary>
    internal sealed class TunnelWorker
    {
        /// <summary>多跳板通道的資料夾：本機 %TEMP%\UvncFtpBridge\Routes\&lt;路由&gt;（跟 UvncFtpBridge 的 FileTunnelSession 一樣）。</summary>
        const string RoutesFolderName = "Routes";
        const string BridgeFolderName = "UvncFtpBridge";
        /// <summary>看不到 \\tsclient 超過這麼久，畫面改顯示「RDP 斷線」（ft.exe 照跑）。</summary>
        const int ShowDisconnectedAfterMs = 5000;
        /// <summary>每隔多久把共用檔的大小與時間寫進記錄檔。</summary>
        const int FileReportMs = 15000;

        readonly JumpState state;
        readonly string baseDir;
        readonly string fixedFolder;
        readonly string profiles;
        readonly string routeArg;
        readonly HashSet<string> identities;
        readonly object gate = new object();
        volatile bool stopping;
        ChildJob currentJob;
        Thread thread;
        /// <summary>曾經看到過 \\tsclient：之後再看不到就是 RDP 斷線（不是一開始就沒開磁碟重新導向）。</summary>
        bool sawRedirection;
        readonly Stopwatch clock = Stopwatch.StartNew();

        /// <param name="fixedFolder">已經知道的通道資料夾（完整路徑），不知道就給 null 用掃的。</param>
        public TunnelWorker(JumpState state, string baseDir, string fixedFolder, string profiles, string routeArg)
        {
            this.state = state;
            this.baseDir = baseDir;
            this.fixedFolder = fixedFolder;
            this.profiles = profiles;
            this.routeArg = routeArg;
            identities = routeArg == null
                ? OwnIdentities()
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { routeArg };
        }

        /// <summary>
        /// start.bat 的 --source 是本機 TEMP 底下的 ufbdeploy/UvncFtpBridge；通道資料夾在同一個 TEMP 底下：
        /// %TEMP%\UvncFtpBridge\Routes\&lt;路由&gt;。推不出來回 null。
        /// </summary>
        public static string FolderFromSource(string source, string route)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(route)) return null;
            try
            {
                string deploy = Path.GetDirectoryName(source.TrimEnd('\\'));
                string temp = string.IsNullOrEmpty(deploy) ? null : Path.GetDirectoryName(deploy);
                if (string.IsNullOrEmpty(temp)) return null;
                return Path.Combine(Path.Combine(Path.Combine(temp, BridgeFolderName), RoutesFolderName), route);
            }
            catch (ArgumentException) { return null; }
        }

        /// <summary>正在跑的那份把它用的通道資料夾記在這裡，start.bat 再跑一次時用來判斷是不是換了一台本機。</summary>
        public static string CurrentFolderFile
        {
            get { return Path.Combine(Path.GetTempPath(), "UvncFtpJump.tunnel"); }
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
            if (fixedFolder == null && routeArg == null)
                state.Log("這台跳板的通道名稱：" + string.Join("、", identities.OrderBy(x => x).ToArray()));

            while (!stopping)
            {
                string shared = fixedFolder ?? WaitForLocalTunnel();
                if (shared == null) return;
                Remember(shared);
                RunFt(ft, shared, extraOptions);
            }
        }

        static void Remember(string shared)
        {
            try { File.WriteAllText(CurrentFolderFile, shared); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>開 ft.exe 並一直留著，直到它自己結束或關視窗。這裡只負責更新畫面上的狀態。</summary>
        void RunFt(string ft, string shared, List<string> extraOptions)
        {
            state.Log("通道資料夾：" + shared);
            state.SetTunnel(shared);
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
            bool rdpDown = false;
            var noise = new HashSet<string>();
            var statusGate = new object();
            Action show = () =>
            {
                // rdpDown、counterpart 兩個來源（ft.exe 輸出、下面的迴圈）都會改，一起決定畫面
                if (rdpDown) state.SetPhase(Phase.Disconnected, "程式不會結束，連回來後自動接上");
                else if (counterpart == "Online") state.SetPhase(Phase.Connected, "通道正常，可以使用 VNC／WinSCP");
                else state.SetPhase(Phase.WaitingLocal, "ft.exe 已就緒，等待本機 mRemoteNG 開啟經這台跳板的機台");
            };
            DataReceivedEventHandler onLine = (s, e) =>
            {
                if (e.Data == null) return;
                string line = e.Data.Trim();
                int at = line.IndexOf("Counterpart:", StringComparison.Ordinal);
                lock (statusGate)
                {
                    if (at < 0)
                    {
                        if (line.Length == 0) return;
                        // 本機沒開、RDP 斷線時 ft.exe 每秒重複這兩句：同一段期間只記一次
                        string kind = line.IndexOf("Could not create file", StringComparison.Ordinal) >= 0 ? "create"
                            : line.IndexOf("Could not enqueue", StringComparison.Ordinal) >= 0 ? "enqueue" : null;
                        if (kind != null && !noise.Add(kind))
                        {
                            JumpLog.Throttled("ft-" + kind, "ft.exe（重複中）：" + line, 30); // 視窗不洗版，記錄檔每 30 秒留一筆
                            return;
                        }
                        state.Log("ft.exe：" + line);
                        return;
                    }
                    JumpLog.Throttled("ft-counterpart", "ft.exe：" + line, 30);
                    string now = line.Substring(at + "Counterpart:".Length).Trim().Split(' ')[0];
                    if (now == counterpart) return;
                    counterpart = now;
                    if (now == "Online")
                    {
                        noise.Clear();
                        state.Log("本機已回應，通道接上");
                    }
                    else state.Log("本機沒有回應，等待中");
                    show();
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
            state.Log("ft.exe 啟動");
            JumpLog.Write("ft.exe PID " + process.Id + " 參數：" + args);
            lock (statusGate) show();

            // 只看 \\tsclient 還在不在，給畫面顯示用；不會因此去動 ft.exe
            string root = Path.GetPathRoot(shared);
            long missingSince = -1;
            long lastFileReport = -FileReportMs;
            while (!stopping && !process.HasExited)
            {
                bool visible = TimedDirectoryExists(root);
                if (visible && clock.ElapsedMilliseconds - lastFileReport >= FileReportMs)
                {
                    lastFileReport = clock.ElapsedMilliseconds;
                    JumpLog.Write("共用檔：" + Timed(() => Describe(shared, "to-remote.dat") + "；" + Describe(shared, "to-local.dat"), "讀取逾時"));
                }
                lock (statusGate)
                {
                    if (visible)
                    {
                        sawRedirection = true;
                        missingSince = -1;
                        if (rdpDown)
                        {
                            rdpDown = false;
                            state.Log("RDP 已重新連線，通道繼續使用");
                            show();
                        }
                    }
                    else if (missingSince < 0) missingSince = clock.ElapsedMilliseconds;
                    else if (!rdpDown && clock.ElapsedMilliseconds - missingSince >= ShowDisconnectedAfterMs)
                    {
                        rdpDown = true;
                        state.Log(sawRedirection
                            ? "看不到 " + root + "（RDP 斷線），等待重新連線"
                            : "看不到 " + root + "：請在 RDP 連線設定開啟「磁碟機」重新導向後重新連線");
                        show();
                    }
                }
                Thread.Sleep(1000);
            }

            lock (gate)
            {
                job.Dispose();
                if (currentJob == job) currentJob = null;
            }
            if (stopping) return;
            string code;
            try { code = process.ExitCode.ToString(); }
            catch (InvalidOperationException) { code = "?"; }
            state.Log("ft.exe 結束（代碼 " + code + "），2 秒後重新啟動");
            state.SetFt("已結束（代碼 " + code + "）");
            state.SetPhase(Phase.Error, "ft.exe 結束（代碼 " + code + "），2 秒後重新啟動");
            process.Dispose();
            Thread.Sleep(2000);
        }

        /// <summary>共用檔的大小與修改時間（記錄用：看得出兩邊有沒有真的在寫）。</summary>
        static string Describe(string folder, string name)
        {
            var info = new FileInfo(Path.Combine(folder, name));
            return info.Exists ? name + " " + info.Length + " bytes，" + info.LastWriteTime.ToString("HH:mm:ss") : name + " 不存在";
        }

        /// <summary>沒有指定資料夾時：掃 \\tsclient 找本機的通道資料夾，回傳資料夾；關視窗時回 null。</summary>
        string WaitForLocalTunnel()
        {
            string lastStatus = null;
            while (!stopping)
            {
                string status;
                Phase phase = Phase.WaitingLocal;
                Scan scan = TimedDirectoryExists(profiles) ? Timed(() => ScanTunnels(), null) : null;
                if (scan == null)
                {
                    if (sawRedirection)
                    {
                        phase = Phase.Disconnected;
                        status = "RDP 斷線";
                    }
                    else status = "看不到 " + profiles + "：請在 RDP 連線設定開啟「磁碟機」重新導向後重新連線";
                }
                else
                {
                    sawRedirection = true;
                    if (scan.Found.Count == 1) return scan.Found[0];
                    if (scan.Found.Count > 1)
                        status = "找到多個本機通道資料夾，請從 mRemoteNG 重新部署跳板通道";
                    else if (scan.OtherRoutes.Count > 0)
                        status = "只找到其他跳板的通道：" + string.Join("、", scan.OtherRoutes.ToArray()) +
                                 "。如果其中一個就是這台，請用 --route 指定";
                    else
                        status = "請在本機 mRemoteNG 開啟經這台跳板的機台";
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
                    string bridgeTemp = Path.Combine(user, @"AppData\Local\Temp\" + BridgeFolderName);
                    try
                    {
                        // 單一通道（Start-FileTunnel.cmd）；有指定 --route 時不看
                        if (routeArg == null)
                        {
                            string candidate = Path.Combine(bridgeTemp, "FileTunnel");
                            if (Directory.Exists(candidate)) scan.Found.Add(candidate);
                        }
                        // mRemoteNG 主機模式：每台跳板一個資料夾，用跳板的名稱或 IP 命名
                        string routesDir = Path.Combine(bridgeTemp, RoutesFolderName);
                        if (!Directory.Exists(routesDir)) continue;
                        foreach (string routeDir in Directory.GetDirectories(routesDir))
                        {
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

        // RDP 斷線時 \\tsclient 消失，讀它可能卡住很久（不會馬上報錯）。所以檢查資料夾都丟到背景、限時 3 秒；
        // 卡住就當作「看不到」。同一時間只留一個卡住的讀取，不越積越多。
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
