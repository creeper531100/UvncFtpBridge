using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Ftp;

/// <summary>一條 FTP 控制連線。登入後對應一條到機台的 VNC 連線。</summary>
public sealed class FtpSession(TcpClient control, FtpServer server)
{
    readonly TcpClient _control = control;
    readonly FtpServer _server = server;
    Stream _ctrl = null!;

    string _user = "";
    bool _authed;
    string _cwd = "/";
    string _renameFrom = "";

    RfbConnection? _vnc;
    UvncFileTransfer? _ft;

    TcpListener? _pasvListener;

    public void Run()
    {
        try
        {
            _control.NoDelay = true;
            _ctrl = _control.GetStream();
            Send(220, "UltraVNC FTP bridge. Password = VNC password.");
            string? line;
            while ((line = ReadLine()) != null)
            {
                if (!Handle(line)) break;
            }
        }
        catch (Exception ex) { Log.Debug($"連線結束：{ex.Message}"); }
        finally { Cleanup(); }
    }

    bool Handle(string line)
    {
        int sp = line.IndexOf(' ');
        string cmd = (sp < 0 ? line : line[..sp]).Trim().ToUpperInvariant();
        string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();

        if (cmd is not ("USER" or "PASS")) Log.Debug($"<- {cmd} {arg}");
        else Log.Debug($"<- {cmd} ***");

        switch (cmd)
        {
            case "USER":
                _vnc?.Dispose(); _vnc = null; _ft = null;
                _user = arg; _authed = false; _renameFrom = "";
                Send(331, "需要密碼（填 VNC 密碼）。"); return true;
            case "PASS": DoLogin(arg); return true;
            case "QUIT": Send(221, "再見。"); return false;
            case "NOOP": Send(200, "OK"); return true;
            case "SYST": Send(215, "UNIX Type: L8"); return true;
            case "FEAT": SendFeat(); return true;
            case "OPTS": Send(arg.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? 200 : 501,
                               arg.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? "UTF8 已開啟" : "不支援的選項"); return true;
            case "TYPE": Send(200, $"型別設為 {arg}"); return true;
            case "PWD": case "XPWD": Send(257, $"\"{_cwd}\" 是目前目錄"); return true;
        }

        if (!_authed) { Send(530, "請先登入。"); return true; }

        try
        {
            switch (cmd)
            {
                case "CWD": case "XCWD": DoCwd(arg); break;
                case "CDUP": case "XCUP": DoCwd(".."); break;
                case "PASV": DoPasv(); break;
                case "EPSV": DoEpsv(); break;
                case "LIST": DoList(arg, longFormat: true); break;
                case "MLSD": DoMlsd(arg); break;
                case "NLST": DoList(arg, longFormat: false); break;
                case "RETR": DoRetr(arg); break;
                case "STOR": DoStor(arg); break;
                case "DELE": DoDelete(arg, "檔案"); break;
                case "RMD": case "XRMD": DoDelete(arg, "資料夾"); break;
                case "MKD": case "XMKD": DoMkd(arg); break;
                case "RNFR": DoRnfr(arg); break;
                case "RNTO": DoRnto(arg); break;
                case "SIZE": DoSize(arg); break;
                case "MDTM": DoMdtm(arg); break;
                case "MLST": DoMlst(arg); break;
                case "ABOR": Send(226, "沒有進行中的傳輸。"); break;
                default: Send(502, $"不支援的指令 {cmd}"); break;
            }
        }
        catch (VncException ex) { Send(550, ex.Message); }
        catch (Exception ex) { Log.Error($"{cmd} 發生錯誤：{ex.Message}"); Send(550, "操作失敗：" + ex.Message); }
        return true;
    }

    // ---------------- 登入 ----------------

    void DoLogin(string password)
    {
        if (string.IsNullOrWhiteSpace(_user)) { Send(503, "請先送 USER。"); return; }

        // WinSCP 模式：機台由原生登入視窗的 HostName/PortNumber 傳入，USER 不再承載機台 IP。
        // Standard WinSCP FTP proxy login: USER username@machine[:vncPort].
        string hostSpec = _user[(_user.LastIndexOf('@') + 1)..];
        string host = _server.TargetHost ?? hostSpec;
        int vncPort = _server.DefaultVncPort;
        int colon = hostSpec.LastIndexOf(':');
        if (_server.TargetHost == null && colon > 0 && int.TryParse(hostSpec[(colon + 1)..], out int p))
        {
            host = hostSpec[..colon];
            vncPort = p;
        }

        try
        {
            _authed = false;
            _vnc?.Dispose(); _vnc = null; _ft = null;
            Log.Info($"連線到機台 {host}:{vncPort} …");
            _vnc = RfbConnection.Connect(host, vncPort, password, proxy: _server.Proxy);
            _ft = new UvncFileTransfer(_vnc);
            _ft.RequestAccess();
            _authed = true;
            _cwd = "/";
            Log.Info($"機台 {host} 登入成功（桌面：{_vnc.DesktopName}）。");
            Send(230, $"已連上機台 {host}。");
        }
        catch (VncException ex)
        {
            Log.Warn($"機台 {host} 登入失敗：{ex.Message}");
            _vnc?.Dispose(); _vnc = null; _ft = null;
            Send(530, ex.Message);
        }
    }

    // ---------------- 目錄操作 ----------------

    void DoCwd(string arg)
    {
        string target = PathMap.Resolve(_cwd, arg);
        if (PathMap.IsRoot(target) || PathMap.IsDriveRoot(target))
        {
            _cwd = target == "/" ? "/" : target;
            Send(250, $"目錄切換到 {_cwd}");
            return;
        }
        // 用列出父目錄來確認目標是存在的資料夾
        var entry = Stat(target);
        if (entry is { IsDirectory: true })
        {
            _cwd = target;
            Send(250, $"目錄切換到 {_cwd}");
        }
        else Send(550, "找不到該資料夾。");
    }

    void DoList(string arg, bool longFormat)
    {
        string path = ResolveListTarget(arg);
        var entries = ListPath(path);
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (longFormat) sb.Append(FormatUnixLine(e));
            else sb.Append(e.Name).Append("\r\n");
        }
        SendDataText(sb.ToString(), "目錄清單");
    }

    void DoMlsd(string arg)
    {
        string path = ResolveListTarget(arg);
        var entries = ListPath(path);
        var sb = new StringBuilder();
        foreach (var e in entries)
            sb.Append(FormatMlsx(e)).Append("\r\n");
        SendDataText(sb.ToString(), "目錄清單");
    }

    void DoMlst(string arg)
    {
        string target = PathMap.Resolve(_cwd, arg);
        var e = Stat(target) ?? throw new VncException("找不到該項目。");
        SendRaw($"250-列出項目:\r\n {FormatMlsx(e)}\r\n250 結束\r\n");
    }

    string ResolveListTarget(string arg)
    {
        // 去掉像 "-la" 這種選項參數
        string a = arg;
        if (a.StartsWith('-'))
        {
            int sp = a.IndexOf(' ');
            a = sp < 0 ? "" : a[(sp + 1)..].Trim();
        }
        return PathMap.Resolve(_cwd, a);
    }

    List<RemoteEntry> ListPath(string ftpPath)
    {
        if (PathMap.IsRoot(ftpPath)) return _ft!.ListDrives();
        string win = PathMap.ToWindows(ftpPath)!;
        return _ft!.ListDirectory(win);
    }

    RemoteEntry? Stat(string ftpPath)
    {
        if (PathMap.IsRoot(ftpPath))
            return new RemoteEntry { Name = "/", IsDirectory = true, ModifiedUtc = DateTime.UtcNow };
        if (PathMap.IsDriveRoot(ftpPath))
            return new RemoteEntry { Name = PathMap.BaseName(ftpPath), IsDirectory = true, ModifiedUtc = DateTime.UtcNow };

        string parent = PathMap.Resolve(ftpPath, "..");
        string name = PathMap.BaseName(ftpPath);
        var siblings = ListPath(parent);
        return siblings.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 檔案傳輸 ----------------

    void DoRetr(string arg)
    {
        string win = PathMap.ToWindows(PathMap.Resolve(_cwd, arg))
                     ?? throw new VncException("不能下載磁碟根。");
        using var data = OpenDataConnection();
        if (data == null) return;
        Send(150, "開始下載。");
        try
        {
            using var ns = data.GetStream();
            _ft!.Download(win, ns);
            ns.Flush();
        }
        finally { data.Close(); }
        Send(226, "下載完成。");
    }

    void DoStor(string arg)
    {
        string ftpPath = PathMap.Resolve(_cwd, arg);
        string win = PathMap.ToWindows(ftpPath) ?? throw new VncException("不能寫入磁碟根。");
        using var data = OpenDataConnection();
        if (data == null) return;
        Send(150, "開始上傳。");
        try
        {
            using var ns = data.GetStream();
            // 先把資料收到暫存檔，才知道大小（VNC 協定上傳時要先宣告檔案大小）
            string tmp = Path.Combine(Path.GetTempPath(), "uvncftp_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                long len;
                using (var fs = File.Create(tmp))
                {
                    ns.CopyTo(fs);
                    len = fs.Length;
                }
                using (var fs = File.OpenRead(tmp))
                    _ft!.Upload(win, fs, len, DateTime.UtcNow);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        finally { data.Close(); }
        Send(226, "上傳完成。");
    }

    // ---------------- 其他檔案指令 ----------------

    void DoDelete(string arg, string kind)
    {
        string win = PathMap.ToWindows(PathMap.Resolve(_cwd, arg))
                     ?? throw new VncException($"不能刪除磁碟根。");
        _ft!.Delete(win);
        Send(250, $"{kind}已刪除。");
    }

    void DoMkd(string arg)
    {
        string ftpPath = PathMap.Resolve(_cwd, arg);
        string win = PathMap.ToWindows(ftpPath) ?? throw new VncException("不能在此建立資料夾。");
        _ft!.MakeDirectory(win);
        Send(257, $"\"{ftpPath}\" 已建立。");
    }

    void DoRnfr(string arg)
    {
        string win = PathMap.ToWindows(PathMap.Resolve(_cwd, arg))
                     ?? throw new VncException("不能改名磁碟根。");
        _renameFrom = win;
        Send(350, "收到舊名稱，請送 RNTO。");
    }

    void DoRnto(string arg)
    {
        if (_renameFrom == "") { Send(503, "請先送 RNFR。"); return; }
        string win = PathMap.ToWindows(PathMap.Resolve(_cwd, arg))
                     ?? throw new VncException("目標名稱無效。");
        _ft!.Rename(_renameFrom, win);
        _renameFrom = "";
        Send(250, "改名完成。");
    }

    void DoSize(string arg)
    {
        var e = Stat(PathMap.Resolve(_cwd, arg));
        if (e is { IsDirectory: false }) Send(213, e.Size.ToString());
        else Send(550, "找不到檔案。");
    }

    void DoMdtm(string arg)
    {
        var e = Stat(PathMap.Resolve(_cwd, arg));
        if (e != null) Send(213, e.ModifiedUtc.ToString("yyyyMMddHHmmss"));
        else Send(550, "找不到項目。");
    }

    // ---------------- 被動資料連線 ----------------

    void DoPasv()
    {
        var port = StartPasv();
        var bytes = IPAddress.Loopback.GetAddressBytes();
        Send(227, $"Entering Passive Mode ({bytes[0]},{bytes[1]},{bytes[2]},{bytes[3]},{port / 256},{port % 256})");
    }

    void DoEpsv()
    {
        var port = StartPasv();
        Send(229, $"Entering Extended Passive Mode (|||{port}|)");
    }

    int StartPasv()
    {
        try { _pasvListener?.Stop(); } catch { }
        _pasvListener = new TcpListener(IPAddress.Loopback, 0);
        _pasvListener.Start();
        return ((IPEndPoint)_pasvListener.LocalEndpoint).Port;
    }

    TcpClient? OpenDataConnection()
    {
        if (_pasvListener == null) { Send(425, "請先送 PASV。"); return null; }
        try
        {
            _pasvListener.Server.ReceiveTimeout = 15000;
            var task = _pasvListener.AcceptTcpClientAsync();
            if (!task.Wait(15000)) { Send(425, "等不到資料連線。"); return null; }
            return task.Result;
        }
        catch (Exception ex) { Send(425, "資料連線失敗：" + ex.Message); return null; }
        finally { try { _pasvListener.Stop(); } catch { } _pasvListener = null; }
    }

    void SendDataText(string text, string what)
    {
        using var data = OpenDataConnection();
        if (data == null) return;
        Send(150, $"開始傳送{what}。");
        try
        {
            byte[] buf = Encoding.UTF8.GetBytes(text);
            using var ns = data.GetStream();
            ns.Write(buf);
            ns.Flush();
        }
        finally { data.Close(); }
        Send(226, $"{what}傳送完成。");
    }

    // ---------------- 清單格式 ----------------

    static string FormatUnixLine(RemoteEntry e)
    {
        string perm = e.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        var t = e.ModifiedUtc.ToLocalTime();
        string date = (DateTime.Now - t).TotalDays > 180
            ? t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture)
            : t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{perm} 1 owner group {e.Size,15} {date} {e.Name}\r\n";
    }

    static string FormatMlsx(RemoteEntry e)
    {
        string type = e.IsDirectory ? "dir" : "file";
        string modify = e.ModifiedUtc.ToString("yyyyMMddHHmmss");
        return e.IsDirectory
            ? $"type={type};modify={modify}; {e.Name}"
            : $"type={type};size={e.Size};modify={modify}; {e.Name}";
    }

    void SendFeat()
        => SendRaw("211-支援的功能:\r\n UTF8\r\n MLSD\r\n MLST type*;size*;modify*;\r\n SIZE\r\n MDTM\r\n211 結束\r\n");

    // ---------------- 低階 IO ----------------

    string? ReadLine()
    {
        var sb = new List<byte>(128);
        int b;
        while ((b = _ctrl.ReadByte()) != -1)
        {
            if (b == '\n') break;
            if (b != '\r') sb.Add((byte)b);
        }
        if (b == -1 && sb.Count == 0) return null;
        // 濾掉某些客戶端在開頭送的 UTF-8 BOM
        return Encoding.UTF8.GetString(sb.ToArray()).TrimStart('﻿');
    }

    void Send(int code, string msg)
    {
        _ctrl.Write(Encoding.UTF8.GetBytes($"{code} {msg}\r\n"));
        _ctrl.Flush();
        if (code >= 400) Log.Debug($"-> {code} {msg}");
    }

    void SendRaw(string text)
    {
        _ctrl.Write(Encoding.UTF8.GetBytes(text));
        _ctrl.Flush();
    }

    void Cleanup()
    {
        try { _pasvListener?.Stop(); } catch { }
        try { _vnc?.Dispose(); } catch { }
        try { _control.Close(); } catch { }
    }
}
