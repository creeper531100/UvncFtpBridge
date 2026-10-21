using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>UltraVNC Viewer 的輔助：找 Viewer、預選 Repeater、開啟。啟動流程在 TunnelHost（WinSCP 與 UltraVNC 一起開）。</summary>
public static class VncCommand
{
    /// <summary>
    /// 套件裡的 Viewer 會把設定存在同資料夾的 options.vnc（可攜模式），連線視窗從這裡帶出 Repeater。
    /// 1.4.3.6 的 -proxy 參數不帶機台時會直接報錯，所以改用設定檔預選 Repeater。只改這三個值，其餘保留。
    /// </summary>
    internal static void ConfigureViewer(string viewer, int repeaterPort)
    {
        string options = Path.Combine(Path.GetDirectoryName(viewer)!, "options.vnc");
        if (!WriteRepeater(options, repeaterPort))
            Log.Warn($"無法寫入 {options}（錯誤碼 {Marshal.GetLastWin32Error()}）；Viewer 不會自動選好 Repeater。");

        // Viewer 每連一台就存一份 %APPDATA%\UltraVNC\<主機>-<埠>.vnc，從歷史清單選到那台時會改讀它，
        // 裡面若是直連設定，Repeater 欄位就被清空。把這份 Viewer 歷史清單裡的主機都改回這個 Repeater。
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (char key in ReadProfile("connection", "index", options))
        {
            string entry = ReadProfile("connection", key.ToString(), options);
            int colon = entry.IndexOf(':');
            if (entry.Length > 0) hosts.Add(colon < 0 ? entry : entry[..colon]);
        }
        string perHostDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraVNC");
        if (hosts.Count == 0 || !Directory.Exists(perHostDir)) return;
        foreach (string file in Directory.EnumerateFiles(perHostDir, "*.vnc"))
        {
            if (Path.GetFileName(file).Equals("options.vnc", StringComparison.OrdinalIgnoreCase)) continue;
            if (hosts.Contains(ReadProfile("connection", "host", file)) && !WriteRepeater(file, repeaterPort))
                Log.Warn($"無法寫入 {file}；從歷史清單選這台時可能要手動選 Repeater。");
        }
    }

    static bool WriteRepeater(string file, int repeaterPort) =>
        WritePrivateProfileString("options", "UseProxy", "1", file)
        && WritePrivateProfileString("connection", "proxyhost", "127.0.0.1", file)
        && WritePrivateProfileString("connection", "proxyport", repeaterPort.ToString(System.Globalization.CultureInfo.InvariantCulture), file);

    static string ReadProfile(string section, string key, string file)
    {
        var buffer = new char[512];
        int length = GetPrivateProfileString(section, key, "", buffer, buffer.Length, file);
        return new string(buffer, 0, length).Trim();
    }

    [DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WritePrivateProfileString(string section, string key, string value, string fileName);

    [DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", CharSet = CharSet.Unicode)]
    static extern int GetPrivateProfileString(string section, string key, string defaultValue, [Out] char[] buffer, int size, string fileName);

    internal static void OpenViewer(string viewer)
    {
        var start = new ProcessStartInfo(viewer)
        {
            WorkingDirectory = Path.GetDirectoryName(viewer)!,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("無法啟動 UltraVNC Viewer。");
            Log.Info("已開啟 UltraVNC Viewer；請在連線視窗填機台 IP 並按 Connect。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Log.Warn($"無法啟動 UltraVNC Viewer（{viewer}）：{ex.Message}");
        }
    }

    /// <summary>套件內或 --viewer 指定的 Viewer 由我們設定 Repeater；電腦已安裝的版本不動它的設定，只提示手動選。</summary>
    internal static (string? Path, bool Own) FindViewer(string baseDir, string? explicitPath)
    {
        if (explicitPath != null)
            return File.Exists(explicitPath) ? (Path.GetFullPath(explicitPath), true) : throw new FileNotFoundException("找不到 UltraVNC Viewer：" + explicitPath);
        string bundled = Path.Combine(baseDir, "UltraVNC", "vncviewer.exe");
        if (File.Exists(bundled)) return (bundled, true);
        string[] installed =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "uvnc bvba", "UltraVNC", "vncviewer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "uvnc bvba", "UltraVNC", "vncviewer.exe"),
        ];
        return (installed.FirstOrDefault(File.Exists), false);
    }
}
