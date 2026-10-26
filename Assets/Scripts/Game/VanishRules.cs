using System;
using UnityEngine;

// 消える裏譜面(Zero の「V」、2026-10-26 ユーザー「zeroはU11の消える案にして」)のノーツの見え方。
// 届くまでの残り時間(秒、実際の時刻)から、本体と合図(矢印・着地の枠・床の印・ロングの数字)の見えにくさ(0〜1)を決める。
// fade: 本体だけ消える(合図は残る) / sudden: 直前まで見えず急に出る / blink: 途中で消えて、直前にまた出る / hide: 合図ごと消える。
// 判定は変えない。見逃したノーツは締め切りのあと(RevealAfterSeconds)に姿を戻し、通り過ぎたことが分かるようにする。
public enum VanishMode { None, Fade, Sudden, Blink, Hide }

public static class VanishRules
{
    // 消え始め・消えきる残り時間(秒)。投影で灰色に沈まないよう、消すときは 0 まで落とす。
    public const float FadeFrom = .5f, FadeTo = .3f;
    public const float SuddenAt = .5f, SuddenTo = .42f;
    public const float BlinkOutFrom = .8f, BlinkOutTo = .65f, BlinkInFrom = .4f, BlinkInTo = .32f;
    public const float HideFrom = .5f, HideTo = .32f;
    public const float RevealAfterSeconds = .25f;

    public static VanishMode Parse(string value)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "fade": return VanishMode.Fade;
            case "sudden": return VanishMode.Sudden;
            case "blink": return VanishMode.Blink;
            case "hide": return VanishMode.Hide;
            default: return VanishMode.None;
        }
    }

    public static string Name(VanishMode mode) => mode == VanishMode.None ? "" : mode.ToString().ToLowerInvariant();

    // 本体の見えにくさ(0 で見える、1 で見えない)。
    public static float BodyVeil(VanishMode mode, double remaining)
    {
        if (mode == VanishMode.None || double.IsNaN(remaining) || remaining <= -RevealAfterSeconds) return 0f;
        double r = Math.Max(0, remaining);
        switch (mode)
        {
            case VanishMode.Fade: return Away(r, FadeFrom, FadeTo);
            case VanishMode.Hide: return Away(r, HideFrom, HideTo);
            case VanishMode.Sudden: return 1f - Away(r, SuddenAt, SuddenTo);
            case VanishMode.Blink: return r > BlinkInFrom ? Away(r, BlinkOutFrom, BlinkOutTo) : 1f - Away(r, BlinkInFrom, BlinkInTo);
            default: return 0f;
        }
    }

    // 合図の見えにくさ。fade だけは合図を残す(本体が消えても、矢印と着地の枠で切れる)。
    public static float CueVeil(VanishMode mode, double remaining) => mode == VanishMode.Fade ? 0f : BodyVeil(mode, remaining);

    // 残り from 秒より前は 0、to 秒で 1 になる、なめらかな変わり方。
    static float Away(double remaining, float from, float to)
    {
        float t = Mathf.Clamp01((float)((from - remaining) / (from - to)));
        return t * t * (3f - 2f * t);
    }
}
