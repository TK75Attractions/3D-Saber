using UnityEngine;
using UnityEngine.UI;

// ロゴの表面とHARD選択中だけの信号ノイズ。入力と画面の位置は変えない。
[DisallowMultipleComponent]
[RequireComponent(typeof(Graphic))]
public sealed class MenuSignalNoise : MonoBehaviour, ITitlePresentationLayer
{
    static readonly int StrengthId = Shader.PropertyToID("_NoiseStrength");
    static readonly int TimeId = Shader.PropertyToID("_NoiseTime");
    static readonly int OverlayId = Shader.PropertyToID("_Overlay");
    Graphic graphic;
    Material effectMaterial, originalMaterial;
    bool overlay, selected, presentationDriven;
    float age, strength;

    public bool IsScreenOverlay => overlay;
    public float Strength => strength;

    public static void AttachToLogo(Graphic word)
    {
        var effect = word.GetComponent<MenuSignalNoise>() ?? word.gameObject.AddComponent<MenuSignalNoise>();
        effect.Configure(false);
        effect.selected = true;
    }

    public static MenuSignalNoise BuildScreenOverlay(Canvas parent)
    {
        var go = new GameObject("HardSignalNoise", typeof(RectTransform), typeof(Canvas), typeof(Image));
        go.transform.SetParent(parent.transform, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        // ポインターを後から生成しても全画面を覆い、画面遷移の幕より後ろに留める。
        var canvas = go.GetComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingLayerID = parent.sortingLayerID;
        canvas.sortingOrder = parent.sortingOrder + 1;
        var effect = go.AddComponent<MenuSignalNoise>();
        effect.Configure(true);
        return effect;
    }

    void Configure(bool isOverlay)
    {
        overlay = isOverlay;
        graphic = GetComponent<Graphic>();
        graphic.raycastTarget = false;
        if (effectMaterial == null)
        {
            var shader = Resources.Load<Shader>("UI/MenuSignalNoise");
            if (shader == null || !shader.isSupported)
            {
                // シェーダーが利用できない環境で白い全画面Imageを出さない。
                if (overlay) graphic.enabled = false;
                enabled = false;
                return;
            }
            originalMaterial = graphic.material;
            effectMaterial = new Material(shader) { name = "Menu signal noise (runtime)", hideFlags = HideFlags.DontSave };
        }
        effectMaterial.SetFloat(OverlayId, overlay ? 1f : 0f);
        graphic.material = effectMaterial;
        if (overlay) graphic.enabled = false;
    }

    public void SetHardSelected(bool hard)
    {
        if (selected == hard) return;
        selected = hard;
        if (hard) age = 0f;
        else
        {
            strength = 0f;
            Apply();
        }
    }

    void Update()
    {
        if (presentationDriven && !overlay) return;
        age += Time.unscaledDeltaTime;
        SetFrame(age, 0f);
    }

    public void SetPresentationTime(float elapsed, float departure)
    {
        if (overlay) return;
        presentationDriven = true;
        SetFrame(elapsed, departure);
    }

    void SetFrame(float elapsed, float departure)
    {
        age = Mathf.Max(0f, elapsed);
        // LOWでは粒・横線・色ずれをすべて止める。切替時に設定を保存し直さない。
        float reveal = Mathf.SmoothStep(0f, 1f, age / (overlay ? .3f : 1.4f));
        strength = selected && !DisplaySettings.ReducedEffects
            ? reveal * (1f - Mathf.SmoothStep(0f, 1f, departure / .45f)) : 0f;
        Apply();
    }

    void Apply()
    {
        if (effectMaterial == null) return;
        effectMaterial.SetFloat(StrengthId, strength);
        effectMaterial.SetFloat(TimeId, age);
        if (overlay) graphic.enabled = strength > .0001f;
    }

    void OnDisable()
    {
        if (effectMaterial != null) effectMaterial.SetFloat(StrengthId, 0f);
        if (overlay && graphic != null) graphic.enabled = false;
    }

    void OnDestroy()
    {
        if (graphic != null && graphic.material == effectMaterial) graphic.material = originalMaterial;
        if (effectMaterial != null) Destroy(effectMaterial);
    }
}
