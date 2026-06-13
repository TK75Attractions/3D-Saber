using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// ボタンのホバー / 押下のマイクロインタラクション。
// スケールのスプリング補間と、任意のグロー Image のフェードを行う。
// Selectable 標準のカラーティントより「生きている」感じを出すための小物。
public class UIHoverEffect : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public float hoverScale = 1.04f;
    public float pressScale = 0.97f;
    public float lerpSpeed = 14f;
    public Image glow;              // 任意。ホバー時にフェードイン
    public float glowHoverAlpha = 0.55f;

    bool hovered;
    bool pressed;
    float currentScale = 1f;
    float glowAlpha;

    public void OnPointerEnter(PointerEventData e) { hovered = true; }
    public void OnPointerExit(PointerEventData e) { hovered = false; pressed = false; }
    public void OnPointerDown(PointerEventData e) { pressed = true; }
    public void OnPointerUp(PointerEventData e) { pressed = false; }

    void OnDisable()
    {
        hovered = false;
        pressed = false;
        currentScale = 1f;
        transform.localScale = Vector3.one;
    }

    void Update()
    {
        float target = pressed ? pressScale : (hovered ? hoverScale : 1f);
        float k = 1f - Mathf.Exp(-lerpSpeed * Time.unscaledDeltaTime);
        currentScale = Mathf.Lerp(currentScale, target, k);
        transform.localScale = new Vector3(currentScale, currentScale, 1f);

        if (glow != null)
        {
            float targetAlpha = hovered && !pressed ? glowHoverAlpha : (pressed ? glowHoverAlpha * 0.7f : 0f);
            glowAlpha = Mathf.Lerp(glowAlpha, targetAlpha, k);
            Color c = glow.color;
            c.a = glowAlpha;
            glow.color = c;
        }
    }
}
