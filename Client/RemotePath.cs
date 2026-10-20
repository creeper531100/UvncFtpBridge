using System.Text.RegularExpressions;

namespace UvncFtpBridge.Client;

/// <summary>
/// 機台端的 Windows 路徑處理。空字串 "" 代表「磁碟清單」（最上層）。
/// 接受 "C:\MES\a.txt"、"C:/MES/a.txt"、"/C:/MES/a.txt" 這幾種寫法。
/// </summary>
public static class RemotePath
{
    /// <summary>統一成反斜線、去掉多餘斜線；磁碟根保持 "C:\"，其他去掉結尾反斜線。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Replace('/', '\\');
        p = p.TrimStart('\\');
        while (p.Contains("\\\\")) p = p.Replace("\\\\", "\\");
        if (p == "") return "";
        if (p.Length == 1 && char.IsLetter(p[0])) p += ":";   // "C" → "C:"
        if (p.Length == 2 && p[1] == ':') return char.ToUpperInvariant(p[0]) + ":\\";
        p = p.TrimEnd('\\');
        if (p.Length >= 2 && p[1] == ':') p = char.ToUpperInvariant(p[0]) + p[1..];
        return p;
    }

    public static bool IsRoot(string path) => Normalize(path) == "";
    public static bool IsDriveRoot(string path) { var n = Normalize(path); return n.Length == 3 && n.EndsWith(":\\"); }

    /// <summary>使用者寫的路徑是不是以斜線結尾（表示「這是資料夾」）。</summary>
    public static bool LooksLikeDirectory(string raw) => raw.EndsWith('\\') || raw.EndsWith('/');

    public static string Combine(string dir, string name)
    {
        dir = Normalize(dir);
        if (dir == "") return Normalize(name);          // 最上層底下只有磁碟
        return dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
    }

    /// <summary>上一層。"C:\MES\a" → "C:\MES"；"C:\MES" → "C:\"；"C:\" → ""。</summary>
    public static string Parent(string path)
    {
        string p = Normalize(path);
        if (p == "" || IsDriveRoot(p)) return "";
        int i = p.LastIndexOf('\\');
        if (i <= 2) return Normalize(p[..2]);           // 只剩磁碟
        return p[..i];
    }

    public static string Name(string path)
    {
        string p = Normalize(path);
        if (p == "") return "";
        if (IsDriveRoot(p)) return p[..2];
        int i = p.LastIndexOf('\\');
        return i < 0 ? p : p[(i + 1)..];
    }

    public static bool HasWildcard(string s) => s.IndexOfAny(['*', '?']) >= 0;

    /// <summary>"*.csv"、"log_2026??.txt" 這類萬用字元轉成正規表示式（不分大小寫）。</summary>
    public static Regex Glob(string pattern)
        => new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>機台檔名放到本機時，去掉 Windows 不允許的字元（例如磁碟名稱的冒號）。</summary>
    public static string SafeLocalName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
