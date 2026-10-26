using UnityEngine;

public partial class CuttableNote
{
    public float RevealSeconds { get; private set; }
    public bool HidesRevealArrow { get; private set; }
    bool revealArrow;
    bool revealCountStyled, revealCountGradient;
    Color revealCountOriginal;

    internal void ConfigureReveal(NoteData data, double remaining)
    {
        RevealSeconds = ChameleonRules.Normalize(data.reveal);
        revealArrow = RevealSeconds > 0f && data.revealArrow;
        TickReveal(remaining);
    }

    internal void TickReveal(double remaining)
    {
        if (RevealSeconds <= 0f) return;
        float amount = ChameleonRules.Amount(RevealSeconds, remaining);
        HidesRevealArrow = revealArrow && !double.IsNaN(remaining)
            && !double.IsInfinity(remaining) && remaining > RevealSeconds + 1e-7;
        if (Visuals != null) Visuals.SetReveal(amount, ChameleonRules.Flash(amount));
        if (Arrow != null) Arrow.RefreshVeil();
        RefreshRevealCountColor();
    }

    internal void RefreshRevealCountColor()
    {
        if (RevealSeconds <= 0f || countLabel == null || Visuals == null) return;
        if (!revealCountStyled)
        {
            revealCountStyled = true;
            revealCountGradient = countLabel.enableVertexGradient;
            revealCountOriginal = countLabel.color;
        }
        // 既存の残回数だけを使う。中立色と発色を見せるため、Cの間だけ氷色のグラデーションを外す。
        countLabel.enableVertexGradient = false;
        Color color = DarkRules.Rgb(Visuals.DisplayColor, Brightness);
        color.a = countLabel.color.a;
        countLabel.color = color;
    }

    internal void ResetReveal()
    {
        RevealSeconds = 0f;
        revealArrow = HidesRevealArrow = false;
        if (Visuals != null) Visuals.SetReveal(1f, 0f);
        if (revealCountStyled && countLabel != null)
        {
            countLabel.enableVertexGradient = revealCountGradient;
            Color color = revealCountOriginal;
            color.a = countLabel.color.a;
            countLabel.color = color;
        }
        revealCountStyled = false;
        if (Arrow != null) Arrow.RefreshVeil();
    }
}
