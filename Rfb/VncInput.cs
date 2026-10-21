namespace UvncFtpBridge.Rfb;

/// <summary>
/// 把 RFB KeyEvent 包成高階輸入動作：Win+R 開 cmd，再逐字把指令打進 cmd 視窗執行。
/// 看不到遠端畫面，所以每個步驟之間只能固定等待；延遲集中在 <see cref="InputTiming"/> 方便調整。
/// </summary>
public static class VncInput
{
    // X11 keysym（對照 keysymdef.h）
    const uint Super_L = 0xFFEB, Return = 0xFF0D, Control_L = 0xFFE3, Escape = 0xFF1B, BackSpace = 0xFF08;
    const uint KeyR = (uint)'r', KeyA = (uint)'a';

    /// <summary>各步驟之間的等待（毫秒）。看不到畫面，只能估。</summary>
    public sealed record InputTiming(int AfterWinR = 800, int AfterCmdOpen = 1500, int AfterEnter = 200, int PerKey = 15)
    {
        public static readonly InputTiming Default = new();
    }

    /// <summary>依序按下、再反序放開一組鍵（組合鍵用）。</summary>
    public static void PressChord(RfbConnection c, params uint[] keysyms)
    {
        for (int i = 0; i < keysyms.Length; i++) c.SendKeyEvent(keysyms[i], true);
        for (int i = keysyms.Length - 1; i >= 0; i--) c.SendKeyEvent(keysyms[i], false);
    }

    /// <summary>逐字打字（備援用）。送字元本身的 keysym，大小寫與符號的 Shift 交給伺服器的 VkKeyScan 處理。</summary>
    public static void TypeText(RfbConnection c, string text, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        foreach (char ch in text)
        {
            uint keysym = ch == '\n' ? Return : ch == '\t' ? 0xFF09 : (uint)ch;
            if (ch is < ' ' or > '\x00ff' && ch is not ('\n' or '\t')) continue; // 只處理可輸入的 Latin-1
            c.SendKeyEvent(keysym, true);
            c.SendKeyEvent(keysym, false);
            if (t.PerKey > 0) Thread.Sleep(t.PerKey);
        }
    }

    /// <summary>打開「執行」對話框（Win+R）。</summary>
    public static void OpenRunDialog(RfbConnection c, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        PressChord(c, Super_L, KeyR);
        Thread.Sleep(t.AfterWinR);
        // 「執行」會預填上一次的指令；先清空，貼上失敗時 Enter 才不會重跑舊指令。
        PressChord(c, Control_L, KeyA);
        c.SendKeyEvent(BackSpace, true);
        c.SendKeyEvent(BackSpace, false);
    }

    /// <summary>送一次 Enter（按下＋放開）。</summary>
    static void PressEnter(RfbConnection c)
    {
        c.SendKeyEvent(Return, true);
        c.SendKeyEvent(Return, false);
    }

    /// <summary>
    /// Win+R 打 cmd 開一個命令提示字元視窗，再把 <paramref name="command"/> 逐字敲進去執行。
    /// </summary>
    public static void RunViaCmd(RfbConnection c, string command, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        // 先保險按 Esc 關掉殘留對話框，再 Win+R → 清空 → 打 cmd → Enter 開視窗。
        PressEscape(c);
        OpenRunDialog(c, t);
        TypeText(c, "cmd", t);
        PressEnter(c);
        Thread.Sleep(t.AfterCmdOpen); // 等 cmd 視窗開好並取得焦點
        // 在 cmd 視窗裡逐字打指令再 Enter。cmd 會回顯，機台桌面看得到打了什麼。
        TypeText(c, command, t);
        Thread.Sleep(t.AfterEnter);
        PressEnter(c);
        Thread.Sleep(t.AfterEnter);
    }

    /// <summary>保險起見先按 Esc 關掉任何殘留的對話框。</summary>
    public static void PressEscape(RfbConnection c)
    {
        c.SendKeyEvent(Escape, true);
        c.SendKeyEvent(Escape, false);
    }
}
