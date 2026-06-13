using UnityEngine;

// 入場演出：CanvasGroup の alpha と anchoredPosition をイージングで入れる。
// スキンが配置を終えた後の最初の Update で基準位置を取り、終わったら自分を無効化する。
[RequireComponent(typeof(CanvasGroup))]
public class UIFadeSlideIn : MonoBehaviour
{
    public float delay = 0f;
    public float duration = 0.5f;
    public Vector2 fromOffset = new Vector2(0f, -36f);

    CanvasGroup group;
    RectTransform rt;
    Vector2 basePos;
    float age;
    bool captured;

    void OnEnable()
    {
        group = GetComponent<CanvasGroup>();
        rt = GetComponent<RectTransform>();
        age = 0f;
        captured = false;
    }

    void Update()
    {
        if (!captured)
        {
            basePos = rt.anchoredPosition;
            group.alpha = 0f;
            rt.anchoredPosition = basePos + fromOffset;
            captured = true;
        }

        age += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01((age - delay) / Mathf.Max(0.0001f, duration));
        float ease = 1f - Mathf.Pow(1f - t, 3f); // ease-out cubic
        group.alpha = ease;
        rt.anchoredPosition = basePos + fromOffset * (1f - ease);

        if (t >= 1f)
        {
            rt.anchoredPosition = basePos;
            group.alpha = 1f;
            enabled = false;
        }
    }
}
