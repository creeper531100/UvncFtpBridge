using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UvncFtpJump
{
    /// <summary>
    /// 只給人看現在的情況，沒有任何按鈕：最上面一條大狀態列（顏色＝狀態），下面路由、本機通道、ft.exe、版本，
    /// 再下面最近的事件。關視窗＝結束（通道跟著斷）。
    /// </summary>
    internal sealed class StatusForm : Form
    {
        readonly JumpState state;
        readonly Panel band = new Panel();
        readonly Label big = new Label();
        readonly Label sub = new Label();
        readonly Label route = new Label();
        readonly Label tunnel = new Label();
        readonly Label ft = new Label();
        readonly Label version = new Label();
        readonly TextBox log = new TextBox();
        readonly Timer timer = new Timer { Interval = 500 };
        int shownRevision = -1;
        string shownElapsed;

        public StatusForm(JumpState state)
        {
            this.state = state;
            Text = "UvncFtpJump";
            Font = PickFont(9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 420);
            MinimumSize = new Size(420, 360);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            ShowIcon = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            band.Dock = DockStyle.Top;
            band.Height = 78;
            band.Padding = new Padding(16, 12, 16, 10);
            big.Font = PickFont(15f, FontStyle.Bold);
            big.AutoSize = false;
            big.Dock = DockStyle.Top;
            big.Height = 32;
            sub.AutoSize = false;
            sub.Dock = DockStyle.Fill;
            band.Controls.Add(sub);
            band.Controls.Add(big);

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                Padding = new Padding(12, 8, 12, 4),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(table, 0, "路由", route);
            AddRow(table, 1, "本機通道", tunnel);
            AddRow(table, 2, "ft.exe", ft);
            AddRow(table, 3, "版本", version);

            var logBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 4) };
            log.Multiline = true;
            log.ReadOnly = true;
            log.BackColor = Color.White;
            log.ForeColor = Color.FromArgb(0x44, 0x44, 0x41);
            log.ScrollBars = ScrollBars.Vertical;
            log.Dock = DockStyle.Fill;
            log.Font = PickFont(9f);
            logBox.Controls.Add(log);

            var warning = new Label
            {
                Text = "⚠ 關閉這個視窗，VNC／WinSCP 就會中斷",
                Dock = DockStyle.Bottom,
                Height = 28,
                Padding = new Padding(16, 0, 16, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(0x85, 0x4F, 0x0B),
            };

            Controls.Add(logBox);
            Controls.Add(warning);
            Controls.Add(table);
            Controls.Add(band);

            route.Text = string.IsNullOrEmpty(state.Route)
                ? "（自動比對這台跳板的名稱／IP）"
                : state.Route + (string.IsNullOrEmpty(state.Name) ? "" : "（" + state.Name + "）");

            timer.Tick += (s, e) => UpdateView(false);
            timer.Start();
            UpdateView(true);
        }

        static void AddRow(TableLayoutPanel table, int row, string caption, Label value)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                ForeColor = Color.FromArgb(0x5F, 0x5E, 0x5A),
                Margin = new Padding(4, 4, 4, 4),
            }, 0, row);
            value.AutoSize = true;
            value.Margin = new Padding(4, 4, 4, 4);
            table.Controls.Add(value, 1, row);
        }

        void UpdateView(bool force)
        {
            var snap = state.Take();
            string elapsed = Elapsed(snap);
            if (!force && snap.Revision == shownRevision && elapsed == shownElapsed) return;
            shownRevision = snap.Revision;
            shownElapsed = elapsed;

            Color back, fore;
            string title;
            switch (snap.Phase)
            {
                case Phase.Connected:
                    back = Color.FromArgb(0xEA, 0xF3, 0xDE); fore = Color.FromArgb(0x27, 0x50, 0x0A); title = "已連線"; break;
                case Phase.Connecting:
                    back = Color.FromArgb(0xE6, 0xF1, 0xFB); fore = Color.FromArgb(0x0C, 0x44, 0x7C); title = "連線中"; break;
                case Phase.Disconnected:
                    back = Color.FromArgb(0xFA, 0xEE, 0xDA); fore = Color.FromArgb(0x63, 0x38, 0x06); title = "RDP 斷線，等待重新連線"; break;
                case Phase.Updating:
                    back = Color.FromArgb(0xE6, 0xF1, 0xFB); fore = Color.FromArgb(0x0C, 0x44, 0x7C); title = "更新中"; break;
                case Phase.Error:
                    back = Color.FromArgb(0xFC, 0xEB, 0xEB); fore = Color.FromArgb(0x79, 0x1F, 0x1F); title = "錯誤"; break;
                default:
                    back = Color.FromArgb(0xF1, 0xEF, 0xE8); fore = Color.FromArgb(0x44, 0x44, 0x41); title = "等待本機通道"; break;
            }
            band.BackColor = back;
            big.ForeColor = sub.ForeColor = fore;
            big.Text = title;
            sub.Text = snap.Detail + elapsed;
            Text = "UvncFtpJump － " + title;
            tunnel.Text = snap.Tunnel;
            ft.Text = snap.Ft;
            version.Text = snap.Version;
            string text = string.Join("\r\n", snap.Log.Reverse().ToArray());
            if (log.Text != text) log.Text = text;
        }

        /// <summary>已連線多久／已等多久（狀態列第二行後面）。</summary>
        static string Elapsed(JumpState.Snapshot snap)
        {
            var t = snap.PhaseElapsed;
            string span = t.TotalHours >= 1
                ? ((int)t.TotalHours) + " 小時 " + t.Minutes + " 分"
                : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            switch (snap.Phase)
            {
                case Phase.Connected: return "（已連線 " + span + "）";
                case Phase.Disconnected: return "（已等 " + span + "）";
                case Phase.Connecting:
                case Phase.WaitingLocal: return "（" + span + "）";
                default: return "";
            }
        }

        /// <summary>微軟正黑體（Win7 也有）；沒有就用系統預設。</summary>
        static Font PickFont(float size, FontStyle style = FontStyle.Regular)
        {
            foreach (string name in new[] { "Microsoft JhengHei UI", "Microsoft JhengHei" })
            {
                using (var probe = new Font(name, size, style))
                {
                    if (probe.Name == name) return new Font(name, size, style);
                }
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
