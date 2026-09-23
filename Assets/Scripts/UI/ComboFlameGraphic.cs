using UnityEngine;
using UnityEngine.UI;

// コンボ数の後ろで燃える炎(AP中=虹色、FC中=金色)。1枚のUIメッシュへ毎フレーム描き直す。
// 形と色は ComboFlameLogic の純関数で決め、テストから頂点数を検証できる。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class ComboFlameGraphic : MaskableGraphic
{
    ComboFlameMode mode;
    float intensity, level, time, width = 220f;

    public ComboFlameMode Mode => mode;
    public float Level => level;

    public void SetState(ComboFlameMode shown, float intensity01, float level01, float seconds, float flameWidth)
    {
        mode = shown; intensity = intensity01; level = level01; time = seconds; width = flameWidth;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        ComboFlameLogic.Fill(vh, mode, intensity, level, time, width, DisplaySettings.ReducedEffects);
    }
}
