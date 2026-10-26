using UnityEngine;

public partial class NoteVisuals
{
    public float RevealAmount { get; private set; } = 1f;
    float revealFlash;
    // 暗さ・透明度を掛ける前の表示色。判定用の baseColor は書き換えない。
    public Color DisplayColor => ChameleonRules.Tint(baseColor, RevealAmount);

    public void SetReveal(float amount, float flash)
    {
        Initialize();
        amount = Mathf.Clamp01(amount);
        flash = Mathf.Clamp01(flash);
        if (RevealAmount == amount && revealFlash == flash) return;
        RememberDisplayColors();
        RevealAmount = amount;
        revealFlash = flash;
        ApplyDisplayColors();
    }

    Color RevealedMaterialColor(Material material, Color original)
    {
        // 黒い外縁は白くしない。内側のレール・コアだけ、一度短く白く光る。
        if (material == contrastOutlineMaterial) return original;
        Color color = ChameleonRules.Tint(original, RevealAmount);
        if (material != runtimeBodyMat && revealFlash > 0f)
        {
            float alpha = color.a;
            color = Color.Lerp(color, Color.white, .35f * revealFlash * NoteVeil.CueAlpha(Veil));
            color.a = alpha;
        }
        return color;
    }
}
