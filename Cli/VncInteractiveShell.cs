using System.Collections.Concurrent;
using System.Text;
using FxSsh.Services;
using UvncFtpBridge.Client;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 互動 shell（PTY 版）：--ssh 用 ssh -tt 連進來，控制字元（含 Ctrl+C）會原樣送到這裡。
/// 回顯、退格、上下鍵歷史與 Ctrl+C 中斷都由這邊自己處理；整行交給機台常駐 driver 執行，
/// 輸出邊跑邊串流回去。Ctrl+C 會送中止旗標，讓機台把正在跑的指令（例如 ping）砍掉、回到提示字元。
/// </summary>
public static class VncInteractiveShell
{
    public static void Run(SessionChannel channel, RemoteSession session, ShellOptions opt, string user, bool admin)
    {
        var input = new BlockingCollection<string>();
        var buf = new List<byte>();        // 目前這一行還沒送出的位元組
        var hist = new List<string>();     // 指令歷史（上下鍵）
        int hix = 0;                       // 歷史游標（== hist.Count 表示停在最新的空白行）
        int esc = 0;                       // ANSI 逸出序列狀態：0 無、1 剛收到 ESC、2 收到 ESC[
        var gate = new object();
        CancellationTokenSource? running = null;   // 指令執行中的取消來源（Ctrl+C 用）
        string promptText = "";

        void SendRaw(string s) { try { channel.SendData(Encoding.UTF8.GetBytes(s)); } catch { } }
        void Echo(byte b) { try { channel.SendData([b]); } catch { } }   // 原樣回顯一個位元組（多位元組字由終端機自己組）
        void Send(string s) { try { channel.SendData(Encoding.UTF8.GetBytes(Normalize(s))); } catch { } }

        // 清掉整行、重畫提示字元 + s，並把 buf 設成 s（上下鍵換歷史用）
        void Redraw(string s)
        {
            lock (gate) { buf.Clear(); buf.AddRange(Encoding.UTF8.GetBytes(s)); }
            SendRaw("\x1b[2K\r");       // 清整行、游標回行首
            SendRaw(promptText);
            SendRaw(s);
        }

        channel.DataReceived += (_, data) =>
        {
            foreach (byte b in data)
            {
                // 逸出序列：只認方向鍵 ESC [ A/B 當歷史，其餘吞掉，不讓亂碼跑進指令行
                if (esc == 1) { esc = (b == (byte)'[') ? 2 : 0; continue; }
                if (esc == 2)
                {
                    esc = 0;
                    lock (gate)
                    {
                        if (running == null && hist.Count > 0)
                        {
                            if (b == (byte)'A') { if (hix > 0) hix--; Redraw(hist[hix]); }
                            else if (b == (byte)'B') { if (hix < hist.Count - 1) { hix++; Redraw(hist[hix]); } else { hix = hist.Count; Redraw(""); } }
                        }
                    }
                    continue;
                }
                if (b == 0x1b) { esc = 1; continue; }

                if (b == 0x03)   // Ctrl+C
                {
                    CancellationTokenSource? r; lock (gate) { r = running; }
                    if (r != null) { try { r.Cancel(); } catch { } }   // 執行中：中止機台上的指令
                    else { lock (gate) buf.Clear(); SendRaw("^C\r\n"); SendRaw(promptText); }
                    continue;
                }

                bool busy; lock (gate) { busy = running != null; }
                if (busy) continue;   // 指令執行中，除了 Ctrl+C 其餘輸入忽略

                if (b == 0x04) { if (buf.Count == 0) { try { input.CompleteAdding(); } catch { } } continue; }  // Ctrl+D（空行）= 結束
                if (b == (byte)'\n') continue;   // 只用 CR 當 Enter，忽略 LF（避免 CRLF 變兩行）
                if (b == (byte)'\r')
                {
                    string line; lock (gate) { line = Encoding.UTF8.GetString(buf.ToArray()); buf.Clear(); }
                    SendRaw("\r\n");
                    try { input.Add(line); } catch (InvalidOperationException) { }
                    continue;
                }
                if (b == 0x7f || b == 0x08)   // 退格：連著把一個完整 UTF-8 字刪掉
                {
                    lock (gate)
                    {
                        if (buf.Count > 0)
                        {
                            int i = buf.Count - 1;
                            while (i > 0 && (buf[i] & 0xC0) == 0x80) i--;
                            buf.RemoveRange(i, buf.Count - i);
                            SendRaw("\b \b");
                        }
                    }
                    continue;
                }
                if (b < 0x20) continue;   // 其他控制字元忽略

                lock (gate) buf.Add(b);
                Echo(b);
            }
        };
        channel.EofReceived += (_, _) => { try { input.CompleteAdding(); } catch { } };
        channel.CloseReceived += (_, _) => { try { input.CompleteAdding(); } catch { } };

        Send(admin ? "以系統管理員身分連線（機台第一次會跳 UAC）…\n" : "首次連線會在機台開常駐的 PowerShell 視窗，請稍候…\n");
        string host = session.Host, cwd = "";
        try
        {
            // 初始化指令不串流（要抓主機名與工作目錄）
            var init = VncShell.RunCommand(session, "Set-Location $HOME; $env:COMPUTERNAME", opt, admin, Send);
            if (init.TimedOut) { Send(init.Output + "\n"); channel.SendClose(124); return; }
            host = init.Output.Trim() is { Length: > 0 } name ? name : host;
            cwd = init.Cwd ?? "";
        }
        catch (Exception ex) { Send("連線錯誤：" + ex.Message + "\n"); try { channel.SendClose(1); } catch { } return; }

        promptText = $"{user}@{host} {cwd}>";
        Send("\n(可用 Ctrl+C 中斷正在跑的指令，例如 ping)\n");
        Send("\n" + promptText);
        foreach (string line in input.GetConsumingEnumerable())
        {
            string cmd = line.Trim();
            if (cmd is "exit" or "logout" or "quit") break;
            if (cmd.Length > 0)
            {
                lock (gate) { if (hist.Count == 0 || hist[^1] != cmd) hist.Add(cmd); hix = hist.Count; }
                var cts = new CancellationTokenSource();
                lock (gate) running = cts;
                try
                {
                    var r = VncShell.RunCommand(session, cmd, opt, admin, status: Send, onOutput: Send, ct: cts.Token);
                    if (r.Cwd != null) cwd = r.Cwd;
                    if (r.Canceled) Send("^C 已中止\n");
                }
                catch (Exception ex) { Send("錯誤：" + ex.Message + "\n"); }
                finally { lock (gate) running = null; cts.Dispose(); }
                promptText = $"{user}@{host} {cwd}>";
            }
            Send("\n" + promptText);
        }
        try { channel.SendClose(0); } catch { }
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
