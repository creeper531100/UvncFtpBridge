using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UvncFtpJump
{
    /// <summary>畫面上的大狀態（決定狀態列顏色與標題）。</summary>
    internal enum Phase
    {
        /// <summary>還沒找到本機通道（本機沒開、或還沒開過這台跳板的機台）。</summary>
        WaitingLocal,
        /// <summary>ft.exe 已啟動，等本機那邊回應。</summary>
        Connecting,
        /// <summary>本機已回應，通道正常。</summary>
        Connected,
        /// <summary>曾經連上，現在看不到 \\tsclient（RDP 斷線）；程式不結束，等連回來。</summary>
        Disconnected,
        /// <summary>正在換新版。</summary>
        Updating,
        /// <summary>出錯（例如 ft.exe 異常結束、參數不對）。</summary>
        Error,
    }

    /// <summary>畫面上要顯示的全部內容；背景執行緒寫、畫面計時器讀（Snapshot）。</summary>
    internal sealed class JumpState
    {
        const int MaxLog = 15;

        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<string> log = new List<string>();

        Phase phase = Phase.WaitingLocal;
        string detail = "";
        long phaseSince;
        string tunnel = "—";
        string ft = "未啟動";
        string version = "";
        int revision;

        public string Route { get; set; }
        public string Name { get; set; }

        public void SetPhase(Phase newPhase, string newDetail)
        {
            lock (gate)
            {
                if (newPhase != phase) phaseSince = clock.ElapsedMilliseconds;
                phase = newPhase;
                detail = newDetail ?? "";
                revision++;
            }
        }

        public void SetTunnel(string text)
        {
            lock (gate)
            {
                tunnel = text;
                revision++;
            }
        }

        public void SetFt(string text)
        {
            lock (gate)
            {
                ft = text;
                revision++;
            }
        }

        public void SetVersion(string text)
        {
            lock (gate)
            {
                version = text;
                revision++;
            }
        }

        public void Log(string message)
        {
            lock (gate)
            {
                log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
                if (log.Count > MaxLog) log.RemoveAt(0);
                revision++;
            }
        }

        public Snapshot Take()
        {
            lock (gate)
            {
                return new Snapshot
                {
                    Phase = phase,
                    Detail = detail,
                    PhaseElapsed = TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds - phaseSince),
                    Tunnel = tunnel,
                    Ft = ft,
                    Version = version,
                    Log = log.ToArray(),
                    Revision = revision,
                };
            }
        }

        internal sealed class Snapshot
        {
            public Phase Phase;
            public string Detail;
            public TimeSpan PhaseElapsed;
            public string Tunnel;
            public string Ft;
            public string Version;
            public string[] Log;
            public int Revision;
        }
    }
}
