using System.Text;

namespace UvncFtpBridge.Cli;

/// <summary>
/// 互動模式的多行輸入：把「貼上的一整段腳本」或「語法還沒結束的跨行輸入」收成單一腳本送出，
/// 而不是一行一行各自執行（各自執行會讓 foreach {} / if {} / here-string 這類跨行語法壞掉）。
/// 貼上靠主控台輸入緩衝區是否還有資料偵測；跨行靠 <see cref="NeedsMore"/> 做 PowerShell 風格的平衡判斷。
/// </summary>
internal static class InputCollector
{
    /// <summary>
    /// 目前累積的文字在語法上是否還沒結束（還需要再讀下一行）。
    /// 判斷：大括號／小括號／中括號未閉合、引號或 here-string 未結束、區塊註解未結束，或最後一個有效字元是續行字元（` | ,）。
    /// 會略過單引號、雙引號、行註解與區塊註解裡的內容，避免把字串或註解裡的括號誤判。
    /// </summary>
    public static bool NeedsMore(string text)
    {
        int depth = 0;
        // 0 一般、1 單引號、2 雙引號、3 區塊註解 <# #>、4 here-string @'、5 here-string @"
        int state = 0;
        bool atLineStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char n = i + 1 < text.Length ? text[i + 1] : '\0';
            switch (state)
            {
                case 1: // 單引號字串：'' 代表一個引號，單一 ' 結束
                    if (c == '\'') { if (n == '\'') i++; else state = 0; }
                    break;
                case 2: // 雙引號字串：反引號跳脫下一個字，單一 " 結束
                    if (c == '`') i++;
                    else if (c == '"') state = 0;
                    break;
                case 3: // 區塊註解
                    if (c == '#' && n == '>') { i++; state = 0; }
                    break;
                case 4: // here-string @'...'@，終止符 '@ 必須在行首
                case 5: // here-string @"..."@，終止符 "@ 必須在行首
                    if (atLineStart && c == (state == 4 ? '\'' : '"') && n == '@') { i++; state = 0; }
                    break;
                default: // 一般
                    if (c == '#') { while (i < text.Length && text[i] != '\n') i++; atLineStart = true; continue; }
                    if (c == '<' && n == '#') { i++; state = 3; break; }
                    if (c == '@' && (n == '\'' || n == '"')) { state = n == '\'' ? 4 : 5; i++; break; }
                    if (c == '\'') { state = 1; break; }
                    if (c == '"') { state = 2; break; }
                    if (c == '`') { i++; break; }   // 反引號跳脫（行尾時會吃掉換行＝續行）
                    if (c is '{' or '(' or '[') depth++;
                    else if (c is '}' or ')' or ']') { if (depth > 0) depth--; }
                    break;
            }
            atLineStart = c == '\n';   // 只有換行後的第一個字元算行首（含縮排就不算）
        }
        if (state != 0 || depth > 0) return true;
        string trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '`' or '|' or ',';
    }

    /// <summary>
    /// 讀使用者輸入的一段腳本（可多行）。null＝EOF（Ctrl+Z／串流結束）。
    /// 主控台行為以參數注入，方便測試：<paramref name="readLine"/> 讀一行、
    /// <paramref name="morePasted"/> 回報輸入緩衝區是否還有資料（偵測貼上）、<paramref name="continuationPrompt"/> 畫續行提示。
    /// </summary>
    public static string? ReadScript(Func<string?> readLine, Func<bool> morePasted, Action continuationPrompt)
    {
        string? first = readLine();
        if (first == null) return null;
        var sb = new StringBuilder(first);
        while (true)
        {
            bool pasted = morePasted();
            if (!pasted && !NeedsMore(sb.ToString())) break;
            if (!pasted) continuationPrompt();
            string? next = readLine();
            if (next == null) break;                     // EOF：就把目前收到的送出
            if (!pasted && next.Length == 0) break;      // 續行時空行＝強制送出
            sb.Append('\n').Append(next);
        }
        return sb.ToString();
    }
}
