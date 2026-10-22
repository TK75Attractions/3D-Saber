// iPhoneの座標はOSのキーボード・マウスとは別経路なので、失焦中もUIへ通す。
// InputSystemの背景設定は変えず、マウスの代替操作には従来のフォーカス条件を残す。
public static class PhoneSaberUIFocus
{
    public static bool AllowsInput(bool phoneInput, bool focused, bool batchMode = false) =>
        phoneInput || focused || batchMode;
}
