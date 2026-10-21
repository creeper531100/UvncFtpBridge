namespace UvncFtpBridge.Rfb;

/// <summary>
/// 把 RFB KeyEvent／ClientCutText 包成高階輸入動作：打開「執行」對話框、貼上、逐字打字。
/// 看不到遠端畫面，所以每個步驟之間只能固定等待；延遲集中在 <see cref="InputTiming"/> 方便調整。
/// </summary>
public static class VncInput
{
    // X11 keysym（對照 keysymdef.h）
    const uint Super_L = 0xFFEB, Return = 0xFF0D, Control_L = 0xFFE3, Escape = 0xFF1B;
    const uint KeyR = (uint)'r', KeyV = (uint)'v';

    /// <summary>各步驟之間的等待（毫秒）。看不到畫面，只能估。</summary>
    public sealed record InputTiming(int AfterWinR = 800, int AfterPaste = 250, int AfterEnter = 150, int PerKey = 12)
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
    }

    /// <summary>首選：用剪貼簿貼上啟動字串。Win+R → 設遠端剪貼簿 → Ctrl+V → Enter。避開輸入法。</summary>
    public static void RunViaClipboard(RfbConnection c, string launcher, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        OpenRunDialog(c, t);
        c.SendClientCutText(launcher);
        Thread.Sleep(80); // 讓伺服器端套用剪貼簿
        PressChord(c, Control_L, KeyV);
        Thread.Sleep(t.AfterPaste);
        c.SendKeyEvent(Return, true);
        c.SendKeyEvent(Return, false);
        Thread.Sleep(t.AfterEnter);
    }

    /// <summary>備援：逐字打字。剪貼簿同步被停用時用。</summary>
    public static void RunViaTyping(RfbConnection c, string launcher, InputTiming? timing = null)
    {
        var t = timing ?? InputTiming.Default;
        OpenRunDialog(c, t);
        TypeText(c, launcher, t);
        Thread.Sleep(t.AfterPaste);
        c.SendKeyEvent(Return, true);
        c.SendKeyEvent(Return, false);
        Thread.Sleep(t.AfterEnter);
    }

    /// <summary>保險起見先按 Esc 關掉任何殘留的對話框。</summary>
    public static void PressEscape(RfbConnection c)
    {
        c.SendKeyEvent(Escape, true);
        c.SendKeyEvent(Escape, false);
    }
}
