using UnityEngine;

// C/U12。色と矢印の表示だけを変える。残り時間は曲時計で、判定遅延やスクロールは含めない。
public static class ChameleonRules
{
    public const float MinReveal = .4f;
    public const float MaxReveal = 1.5f;
    // 30fpsで約2コマ。完了が1コマ遅れても .4 - .06 - 1/30 > .3 秒を残す。
    public const float TransitionSeconds = .06f;
    public static readonly Color Chalk = new Color(.94f, .93f, .86f);

    public static float Normalize(float value)
        => float.IsNaN(value) || float.IsInfinity(value) || value <= 0
            ? 0f : Mathf.Clamp(value, MinReveal, MaxReveal);

    public static float Amount(float reveal, double remaining)
    {
        reveal = Normalize(reveal);
        if (reveal == 0 || double.IsNaN(remaining) || double.IsInfinity(remaining)) return 1f;
        return Mathf.Clamp01((float)((reveal - remaining) / TransitionSeconds));
    }

    public static Color Tint(Color original, float amount)
    {
        Color color = Color.Lerp(Chalk, original, amount);
        color.a = original.a;
        return color;
    }

    public static float Flash(float amount)
        => amount <= 0f || amount >= 1f ? 0f : Mathf.Sin(Mathf.PI * amount);
}
