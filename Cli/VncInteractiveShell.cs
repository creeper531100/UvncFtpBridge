using System.Collections.Concurrent;
using System.Text;
using FxSsh.Services;
using UvncFtpBridge.Client;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 互動 shell：一行一指令。--ssh 用 ssh -T（不要 pty）連進來，回顯、退格、上下鍵歷史都由本機主控台處理，
/// 這邊只收整行、交給機台的常駐 driver 執行、把輸出與下一個提示字元送回去。
/// </summary>
public static class VncInteractiveShell
{
    public static void Run(SessionChannel channel, RemoteSession session, ShellOptions opt, string user, bool admin)
    {
        var lines = new BlockingCollection<string>();
        var pending = new List<byte>();
        channel.DataReceived += (_, data) =>
        {
            lock (pending)
            {
                foreach (byte b in data)
                {
                    if (b != (byte)'\n') { if (b != (byte)'\r') pending.Add(b); continue; }
                    try { lines.Add(Encoding.UTF8.GetString(pending.ToArray())); } catch (InvalidOperationException) { }
                    pending.Clear();
                }
            }
        };
        channel.EofReceived += (_, _) => lines.CompleteAdding();
        channel.CloseReceived += (_, _) => lines.CompleteAdding();

        void Send(string s) { try { channel.SendData(Encoding.UTF8.GetBytes(Normalize(s))); } catch { } }

        Send(admin ? "以系統管理員身分連線（機台第一次會跳 UAC）…\n" : "首次連線會在機台開常駐的 PowerShell 視窗，請稍候…\n");
        string host = session.Host, cwd = "";
        try
        {
            var init = VncShell.RunCommand(session, "Set-Location $HOME; $env:COMPUTERNAME", opt, admin, Send);
            if (init.TimedOut) { Send(init.Output + "\n"); channel.SendClose(124); return; }
            host = init.Output.Trim() is { Length: > 0 } name ? name : host;
            cwd = init.Cwd ?? "";
        }
        catch (Exception ex) { Send("連線錯誤：" + ex.Message + "\n"); try { channel.SendClose(1); } catch { } return; }

        Send($"\n{user}@{host} {cwd}>");
        foreach (string line in lines.GetConsumingEnumerable())
        {
            string cmd = line.Trim();
            if (cmd is "exit" or "logout" or "quit") break;
            if (cmd.Length > 0)
            {
                try
                {
                    var r = VncShell.RunCommand(session, cmd, opt, admin, Send);
                    if (r.Output.Length > 0) Send(r.Output.EndsWith('\n') ? r.Output : r.Output + "\n");
                    if (r.Cwd != null) cwd = r.Cwd;
                }
                catch (Exception ex) { Send("錯誤：" + ex.Message + "\n"); }
            }
            Send($"\n{user}@{host} {cwd}>");
        }
        try { channel.SendClose(0); } catch { }
    }

    static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
