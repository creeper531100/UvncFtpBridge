using System.Text.Json;
using UvncFtpBridge.Rfb;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 「傳送檔案到桌面」（VNC）：經 UltraVNC 檔案傳輸把本機的檔案／資料夾放到機台登入使用者的桌面。
/// 由 mRemoteNG 呼叫 /api/desktop（POST：host、port、password、route、file 可多筆）。
///   • 桌面路徑用 UvncFileTransfer.ResolveDesktop 向伺服器問（跟 UltraVNC Viewer 的「Desktop」捷徑一樣）；
///   • 桌面上已經有同名的檔案或資料夾就不覆蓋，改存成「名稱 (2)」（機台是產線電腦，不能蓋掉上面的東西）；
///   • 資料夾整個連子資料夾一起傳。
/// </summary>
internal static class DesktopSend
{
    public static string Run(RfbConnection vnc, IReadOnlyList<string> localPaths)
    {
        var missing = localPaths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0) throw new VncException("找不到要傳的檔案：" + string.Join("、", missing));

        var ft = new UvncFileTransfer(vnc);
        ft.RequestAccess();
        var (desktop, entries) = ft.ResolveDesktop();
        var taken = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        Log.Info($"傳送檔案到桌面：機台 {vnc.Host} 的桌面是 {desktop}");

        var sent = new List<(string From, string To)>();
        long bytes = 0;
        foreach (var local in localPaths)
        {
            string name = FreeName(Path.GetFileName(local.TrimEnd('\\', '/')), Directory.Exists(local), taken);
            taken.Add(name);
            string remote = desktop + name;
            bytes += Directory.Exists(local) ? SendDirectory(ft, local, remote) : SendFile(ft, local, remote);
            sent.Add((local, remote));
            Log.Info($"傳送檔案到桌面：{local} → {remote}");
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("desktop", desktop);
            json.WriteNumber("bytes", bytes);
            json.WriteStartArray("sent");
            foreach (var (from, to) in sent)
            {
                json.WriteStartObject();
                json.WriteString("from", from);
                json.WriteString("to", to);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    static long SendFile(UvncFileTransfer ft, string local, string remote)
    {
        var info = new FileInfo(local);
        using var src = File.Open(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ft.Upload(remote, src, info.Length, info.LastWriteTimeUtc);
        return info.Length;
    }

    static long SendDirectory(UvncFileTransfer ft, string local, string remote)
    {
        ft.MakeDirectory(remote);
        long bytes = 0;
        foreach (var file in Directory.GetFiles(local))
            bytes += SendFile(ft, file, remote + "\\" + Path.GetFileName(file));
        foreach (var dir in Directory.GetDirectories(local))
            bytes += SendDirectory(ft, dir, remote + "\\" + Path.GetFileName(dir));
        return bytes;
    }

    /// <summary>名稱沒人用就原樣，否則「名稱 (2).副檔名」、「名稱 (3)」…（資料夾不拆副檔名）。</summary>
    internal static string FreeName(string name, bool isDirectory, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        string ext = isDirectory ? "" : Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
