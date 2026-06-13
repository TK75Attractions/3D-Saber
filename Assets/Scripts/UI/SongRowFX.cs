using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

// 曲選択リスト1行ぶんのホバー / 選択アニメーション。
// - ホバー: わずかに拡大 + 行が明るくなる + ラベルが右へ数px スライド
// - 選択:   左のアクセントバーがバネで伸びる + 文字間隔が一瞬開いて収まる(パンチ) + スケールパンチ
// アニメ計算は Evaluate(dt) に切り出してあり、EditMode テストから直接駆動できる。
public class SongRowFX : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Targets")]
    public Image fill;
    public Image accentBar;
    public RectTransform accentBarRT;
    public TextMeshProUGUI labelTMP;     // ASCII 曲名用(無い行は legacy のみ)
    public Text labelLegacy;             // 日本語フォールバック用

    [Header("Colors")]
    public Color fillNormal = new Color(0.085f, 0.095f, 0.20f, 0.85f);
    public Color fillSelected = new Color(0.10f, 0.24f, 0.33f, 0.95f);
    public Color textNormal = new Color(0.62f, 0.66f, 0.80f);
    public Color textSelected = Color.white;
    public Color accent = new Color(0.27f, 1f, 0.97f);

    [Header("Tuning")]
    public float smoothing = 12f;          // 状態遷移の速さ
    public float hoverScale = 1.02f;
    public float barHeight = 46f;
    public float labelSlidePx = 10f;
    public float baseSpacing = 2f;         // TMP characterSpacing の基準
    public float selectedSpacingAdd = 4f;  // 選択中に常時加算
    public float punchSpacingAdd = 7f;     // 選択した瞬間に加算して減衰
    public float punchDuration = 0.28f;
    public float punchScale = 1.05f;

    public bool Selected { get; private set; }
    public float SelectAmount => selectAmount;
    public float HoverAmount => hoverAmount;

    private bool hovered;
    private float selectAmount;
    private float hoverAmount;
    private float punchAge = 999f;
    private Vector2 labelBasePos;
    private bool labelBaseCaptured;

    public void SetSelected(bool sel)
    {
        if (sel && !Selected) punchAge = 0f; // 選択した瞬間だけパンチ発火
        Selected = sel;
    }

    public void OnPointerEnter(PointerEventData e) { hovered = true; }
    public void OnPointerExit(PointerEventData e) { hovered = false; }

    void Update()
    {
        Evaluate(Time.unscaledDeltaTime);
    }

    // テストから直接呼べるアニメ本体。dt 秒ぶん状態を進めて見た目に反映する。
    public void Evaluate(float dt)
    {
        float k = 1f - Mathf.Exp(-smoothing * Mathf.Max(0f, dt));
        selectAmount = Mathf.Lerp(selectAmount, Selected ? 1f : 0f, k);
        hoverAmount = Mathf.Lerp(hoverAmount, hovered ? 1f : 0f, k);
        punchAge += dt;
        float punch01 = Mathf.Clamp01(1f - punchAge / Mathf.Max(0.0001f, punchDuration));

        // 行全体のスケール(ホバー + 選択パンチ)
        float scale = 1f
            + (hoverScale - 1f) * hoverAmount
            + (punchScale - 1f) * punch01;
        transform.localScale = new Vector3(scale, scale, 1f);

        // 塗り
        if (fill != null)
        {
            Color c = Color.Lerp(fillNormal, fillSelected, selectAmount);
            // ホバーでほんのり明るく
            c = Color.Lerp(c, new Color(accent.r, accent.g, accent.b, c.a), hoverAmount * 0.12f);
            fill.color = c;
        }

        // アクセントバー(バネで伸びる)
        if (accentBarRT != null)
        {
            float grow = EaseOutBack(selectAmount);
            Vector2 sz = accentBarRT.sizeDelta;
            accentBarRT.sizeDelta = new Vector2(sz.x, barHeight * Mathf.Max(0f, grow));
        }
        if (accentBar != null)
        {
            Color bc = accent;
            bc.a = selectAmount;
            accentBar.color = bc;
        }

        // ラベル(色 + 右スライド + 文字間隔)
        float emphasis = Mathf.Max(selectAmount, hoverAmount * 0.55f);
        Color textC = Color.Lerp(textNormal, textSelected, emphasis);
        float slide = labelSlidePx * selectAmount + 4f * hoverAmount;

        if (labelTMP != null)
        {
            if (!labelBaseCaptured)
            {
                labelBasePos = labelTMP.rectTransform.anchoredPosition;
                labelBaseCaptured = true;
            }
            labelTMP.color = textC;
            labelTMP.rectTransform.anchoredPosition = labelBasePos + new Vector2(slide, 0f);
            labelTMP.characterSpacing = baseSpacing
                + selectedSpacingAdd * selectAmount
                + punchSpacingAdd * punch01;
        }
        else if (labelLegacy != null)
        {
            if (!labelBaseCaptured)
            {
                labelBasePos = labelLegacy.rectTransform.anchoredPosition;
                labelBaseCaptured = true;
            }
            labelLegacy.color = textC;
            labelLegacy.rectTransform.anchoredPosition = labelBasePos + new Vector2(slide, 0f);
        }
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        t = Mathf.Clamp01(t);
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }
}
