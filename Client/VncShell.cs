using System.Text;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Client;

/// <summary>執行遠端指令的設定。</summary>
public sealed record ShellOptions
{
    /// <summary>機台上放 driver、指令佇列與輸出的資料夾。</summary>
    public string TempDir { get; init; } = @"C:\Windows\Temp\uvncssh";
    /// <summary>冷啟動 driver 時強制逐字打字（剪貼簿同步被停用時用），否則以剪貼簿貼上為主。</summary>
    public bool ForceTyping { get; init; }
    /// <summary>跑完不刪暫存輸出檔（除錯用）。</summary>
    public bool KeepFiles { get; init; }
    /// <summary>等單一指令跑完的上限（毫秒），從 driver 接手指令開始算。</summary>
    public int TimeoutMs { get; init; } = 120_000;
    public VncInput.InputTiming Timing { get; init; } = VncInput.InputTiming.Default;
}

/// <summary>一條指令的結果。<see cref="TimedOut"/> 時 <see cref="ExitCode"/> 為 -1。<see cref="Cwd"/> 是跑完後的工作目錄（互動 shell 畫提示字元用）。</summary>
public sealed record ShellResult(int ExitCode, string Output, bool TimedOut, string? Cwd = null);

/// <summary>
/// VNC-SSH 的核心引擎（常駐 driver 模型）。
///
/// 冷啟動：用 Win+R 在機台開一個常駐的 PowerShell driver 迴圈，它輪詢 TEMP 裡的指令佇列。
/// 之後每條指令只是上傳一個指令檔＋觸發檔，driver 在「同一個 session」（同一份變數、工作目錄與權限）
/// 跑完、把輸出與 exit code 寫回檔案，閘道再抓回來。暖路徑完全不用 Win+R／focus／貼上。
///
/// 一般與系統管理員各有自己的 driver（觸發檔 go_user_* / go_admin_*），可以同時開著、互不搶指令。
/// 系統管理員 driver 冷啟動會跳 UAC，在有人按「是」之前一直等，不算逾時。
/// </summary>
public static class VncShell
{
    const string DriverFile = "driver.ps1";
    const int ColdStartGraceMs = 3000; // 觸發檔沒被接手多久就判定沒有 driver、自動冷啟動

    /// <param name="admin">用系統管理員 driver 執行（ssh 用 root@ 登入）。</param>
    /// <param name="status">等待中要告訴使用者的訊息（例如請去按 UAC）。</param>
    public static ShellResult RunCommand(RemoteSession session, string command, ShellOptions opt, bool admin = false,
        Action<string>? status = null, CancellationToken ct = default)
    {
        var ft = session.Ft;              // 確保已連線
        var conn = session.Connection;    // 同一條連線，冷啟動時送 Win+R／剪貼簿
        string temp = RemotePath.Normalize(opt.TempDir);
        try { ft.MakeDirectory(temp); } catch (VncException) { }
        return Submit(ft, conn, temp, command, opt, admin ? "admin" : "user", status, ct);
    }

    static ShellResult Submit(UvncFileTransfer ft, RfbConnection conn, string temp, string command, ShellOptions opt,
        string mode, Action<string>? status, CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        string cmdPath = RemotePath.Combine(temp, $"cmd_{id}.ps1");
        string goName = $"go_{mode}_{id}.flag", doneName = $"done_{id}.txt";
        string goPath = RemotePath.Combine(temp, goName);
        string outPath = RemotePath.Combine(temp, $"out_{id}.txt");
        string donePath = RemotePath.Combine(temp, doneName);
        bool admin = mode == "admin";

        // 先上傳完整指令檔，再上傳觸發檔（driver 看到觸發檔才會去讀指令檔，避免讀到還沒傳完的內容）。
        UploadText(ft, cmdPath, command);
        UploadText(ft, goPath, "");

        long graceDeadline = Environment.TickCount64 + ColdStartGraceMs;
        long? deadline = null;      // driver 接手（觸發檔被拿走）後才開始算指令時限
        bool coldStarted = false;

        while (!ct.IsCancellationRequested)
        {
            if (deadline is long d && Environment.TickCount64 > d) break;
            // 一般 driver 冷啟動也只等這麼久；系統管理員要等人按 UAC，沒有上限
            if (deadline == null && !admin && coldStarted && Environment.TickCount64 > graceDeadline + opt.TimeoutMs) break;

            List<RemoteEntry> entries;
            try { entries = ft.ListDirectory(temp); }
            catch (VncException) { Thread.Sleep(500); continue; }

            if (entries.Any(e => !e.IsDirectory && e.Name.Equals(doneName, StringComparison.OrdinalIgnoreCase)))
            {
                var (exit, cwd) = ReadDone(ft, donePath);
                string output = ReadText(ft, outPath);
                if (!opt.KeepFiles) TryDelete(ft, outPath, donePath);
                return new ShellResult(exit, output, TimedOut: false, Cwd: cwd);
            }

            bool goPresent = entries.Any(e => e.Name.Equals(goName, StringComparison.OrdinalIgnoreCase));
            if (!goPresent && deadline == null) deadline = Environment.TickCount64 + opt.TimeoutMs;

            // 觸發檔還在、沒有 driver 來接手：等過 grace 就冷啟動一個。
            if (goPresent && !coldStarted && Environment.TickCount64 > graceDeadline)
            {
                UploadText(ft, RemotePath.Combine(temp, DriverFile), BuildDriverScript(temp));
                Inject(conn, admin ? AdminLauncher(temp) : UserLauncher(temp), opt);
                coldStarted = true;
                if (admin) status?.Invoke("已在機台跳出 UAC 視窗，請到機台按「是」。按好之前會一直等（Ctrl+C 取消）…\n");
            }
            Thread.Sleep(500);
        }

        if (!opt.KeepFiles) TryDelete(ft, goPath, cmdPath);
        return new ShellResult(-1, BuildTimeoutHint(opt), TimedOut: true);
    }

    /// <summary>常駐 driver 迴圈。輪詢自己模式的 go_*.flag，用「同一個 session」dot-source 執行指令檔，寫回輸出與 exit code。</summary>
    internal static string BuildDriverScript(string temp)
    {
        string d = temp.Replace("'", "''");
        return
            "param([string]$Mode='user')\r\n" +
            "$ErrorActionPreference='Continue'\r\n" +
            $"$dir = '{d}'\r\n" +
            // 系統管理員 driver 寫的檔案預設只有 Administrators 能讀，非提升權限的 winvnc 抓不回來；
            // 對這個資料夾補上「同一個使用者」可繼承的修改權限
            "try { icacls $dir /grant ('*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value + ':(OI)(CI)M') | Out-Null } catch { }\r\n" +
            "$host.UI.RawUI.WindowTitle = \"UvncFtpBridge VNC-SSH ($Mode)\"\r\n" +
            "$inst = \"$dir\\instance_$Mode.txt\"\r\n" +
            "$tok = [guid]::NewGuid().ToString('N')\r\n" +
            "Set-Content -LiteralPath $inst -Value $tok -Encoding ascii\r\n" +
            "while ($true) {\r\n" +
            "  try {\r\n" +
            "    $cur = Get-Content -LiteralPath $inst -ErrorAction SilentlyContinue\r\n" +
            "    if ($cur -ne $tok) { break }\r\n" +   // 同模式有新的 driver 接手，舊的自己退出
            "    $go = Get-ChildItem -LiteralPath $dir -Filter \"go_$($Mode)_*.flag\" -ErrorAction SilentlyContinue | Select-Object -First 1\r\n" +
            "    if ($go) {\r\n" +
            "      $id = $go.BaseName.Substring(\"go_$($Mode)_\".Length)\r\n" +
            "      $cmd = Join-Path $dir (\"cmd_\" + $id + \".ps1\")\r\n" +
            "      $out = Join-Path $dir (\"out_\" + $id + \".txt\")\r\n" +
            "      $done = Join-Path $dir (\"done_\" + $id + \".txt\")\r\n" +
            "      Remove-Item -LiteralPath $go.FullName -ErrorAction SilentlyContinue\r\n" +   // 先拿走觸發檔，表示已接手
            // 視窗上也印出指令與輸出，機台現場的人看得到在做什麼
            "      Write-Host ('PS ' + (Get-Location).Path + '> ' + (Get-Content -LiteralPath $cmd -Raw).Trim()) -ForegroundColor Yellow\r\n" +
            "      try { $__uvncOut = . $cmd *>&1 | Out-String }\r\n" +  // dot-source：變數／工作目錄持續
            "      catch { $__uvncOut = $_ | Out-String }\r\n" +
            "      $__uvncOut | Out-File -LiteralPath $out -Encoding utf8 -NoNewline\r\n" +
            "      Write-Host $__uvncOut.TrimEnd()\r\n" +
            "      if ($null -ne $LASTEXITCODE) { $code = $LASTEXITCODE } else { $code = 0 }\r\n" +
            "      $loc = (Get-Location).Path\r\n" +   // 回傳工作目錄，互動 shell 畫提示字元用
            "      Set-Content -LiteralPath $done -Value (\"$code`n$loc\") -Encoding utf8\r\n" +
            "      Remove-Item -LiteralPath $cmd -ErrorAction SilentlyContinue\r\n" +
            "    } else { Start-Sleep -Milliseconds 200 }\r\n" +
            "  } catch { Start-Sleep -Milliseconds 200 }\r\n" +
            "}\r\n";
    }

    /// <summary>非管理員冷啟動：直接開 driver 視窗。</summary>
    internal static string UserLauncher(string temp)
        => $"powershell -ExecutionPolicy Bypass -NoProfile -File \"{RemotePath.Combine(temp, DriverFile)}\" user";

    /// <summary>管理員冷啟動：用 Start-Process -Verb RunAs 觸發 UAC，再以管理員跑 driver。</summary>
    internal static string AdminLauncher(string temp)
    {
        string driver = RemotePath.Combine(temp, DriverFile);
        return "powershell -NoProfile -Command \"Start-Process powershell -Verb RunAs -ArgumentList " +
               $"'-ExecutionPolicy','Bypass','-NoProfile','-File','{driver}','admin'\"";
    }

    static void Inject(RfbConnection conn, string launcher, ShellOptions opt)
    {
        VncInput.PressEscape(conn); // 先關掉任何殘留對話框
        if (opt.ForceTyping) VncInput.RunViaTyping(conn, launcher, opt.Timing);
        else VncInput.RunViaClipboard(conn, launcher, opt.Timing);
    }

    static void UploadText(UvncFileTransfer ft, string remotePath, string text)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text);
        using var ms = new MemoryStream(bytes, writable: false);
        ft.Upload(remotePath, ms, bytes.Length, DateTime.UtcNow);
    }

    /// <summary>done 檔：第一行是 exit code，第二行是工作目錄。</summary>
    static (int exit, string? cwd) ReadDone(UvncFileTransfer ft, string file)
    {
        try
        {
            var lines = ReadText(ft, file).Replace("\r", "").Split('\n');
            int exit = lines.Length > 0 && int.TryParse(lines[0].Trim(), out int v) ? v : 0;
            string? cwd = lines.Length > 1 && lines[1].Trim().Length > 0 ? lines[1].Trim() : null;
            return (exit, cwd);
        }
        catch (VncException) { return (0, null); }
    }

    static string ReadText(UvncFileTransfer ft, string file)
    {
        using var ms = new MemoryStream();
        ft.Download(file, ms);
        return Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF');
    }

    static void TryDelete(UvncFileTransfer ft, params string[] paths)
    {
        foreach (var p in paths)
            try { ft.Delete(p); } catch (VncException) { }
    }

    static string BuildTimeoutHint(ShellOptions opt) =>
        "指令在時限內沒有回應。可能原因：常駐視窗沒能啟動（機台螢幕鎖定、遠端桌面視窗最小化或沒有互動桌面，Win+R 無效）、" +
        (opt.ForceTyping ? "打字被輸入法干擾" : "剪貼簿同步被停用，冷啟動貼不進去（可改用 --type 逐字打字）") +
        "、或指令本身跑太久（可調 --timeout）。";
}
