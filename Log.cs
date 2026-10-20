namespace UvncFtpBridge;

public static class Log
{
    static readonly object _lock = new();
    public static string? FilePath { get; set; }
    public static bool Verbose { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);
    public static void Debug(string msg) { if (Verbose) Write("DBG ", msg); }

    static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}";
        lock (_lock)
        {
            Console.Error.WriteLine(line);
            if (FilePath != null)
            {
                try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            }
        }
    }
}
