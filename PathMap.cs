namespace UvncFtpBridge;

/// <summary>
/// FTP 路徑（正斜線）和 Windows 路徑（反斜線）互轉。
/// FTP "/"          = 磁碟清單（根）
/// FTP "/C:"        = C 磁碟根目錄  → Windows "C:\"
/// FTP "/C:/MES"    = Windows "C:\MES"
/// FTP "/C:/a/b.txt"= Windows "C:\a\b.txt"
/// </summary>
public static class PathMap
{
    public static bool IsRoot(string ftpPath)
        => string.IsNullOrEmpty(ftpPath) || ftpPath.Trim('/') == "";

    /// <summary>把 FTP 絕對路徑轉成 Windows 路徑；根（磁碟清單）回傳 null。</summary>
    public static string? ToWindows(string ftpPath)
    {
        string trimmed = ftpPath.Trim('/');
        if (trimmed == "") return null;
        string win = trimmed.Replace('/', '\\');
        // 磁碟根目錄補上反斜線："C:" → "C:\"
        if (win.Length == 2 && win[1] == ':') win += "\\";
        return win;
    }

    /// <summary>判斷 FTP 路徑是否指向某個磁碟的根目錄（例如 "/C:"）。</summary>
    public static bool IsDriveRoot(string ftpPath)
    {
        string t = ftpPath.Trim('/');
        return t.Length == 2 && t[1] == ':';
    }

    /// <summary>把使用者輸入的（可能相對的）路徑，依目前目錄解析成正規化的 FTP 絕對路徑。</summary>
    public static string Resolve(string currentDir, string arg)
    {
        string combined;
        if (string.IsNullOrEmpty(arg))
            combined = currentDir;
        else if (arg.StartsWith('/'))
            combined = arg;
        else
            combined = currentDir.TrimEnd('/') + "/" + arg;

        var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(p);
        }
        return "/" + string.Join('/', stack);
    }

    /// <summary>取路徑最後一段（檔名）。</summary>
    public static string BaseName(string ftpPath)
    {
        var parts = ftpPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "/" : parts[^1];
    }
}
