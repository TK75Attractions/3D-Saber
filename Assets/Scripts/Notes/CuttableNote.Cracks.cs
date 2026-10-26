using UnityEngine;

// 31A: 新しいひびだけを着弾点から両端へ伸ばす。独自の Update は持たない。
public partial class CuttableNote
{
    public const float CrackGrowSeconds = .08f;
    public const float CrackHoldSeconds = .06f;
    public const float CrackFadeSeconds = .12f;
    public const float CrackRestStrength = .7f;
    static readonly int CrackBaseId = Shader.PropertyToID("_BaseColor");
    static readonly int CrackColorId = Shader.PropertyToID("_Color");
    static readonly int CrackEmissionId = Shader.PropertyToID("_EmissionColor");

    sealed class CrackVisual
    {
        public GameObject visual;
        public Material material;
        public Transform transform;
        public MeshRenderer renderer;
        public bool veiled;
        public Vector3 fullScale;
        public float age;
    }

    public static float CrackStrength(float age, bool reduced)
    {
        if (reduced) return CrackRestStrength;
        // 伸長0.08秒 → 濃さ維持0.06秒 → 0.12秒で70%へ。
        float fade = Mathf.Clamp01((age - CrackGrowSeconds - CrackHoldSeconds) / CrackFadeSeconds);
        return Mathf.Lerp(1f, CrackRestStrength, fade);
    }

    public static void ApplyCrack(Transform target, Material material, Vector3 fullScale, float age, float veil = 0f, float brightness = 1f)
    {
        fullScale.y *= Mathf.Clamp01(age / CrackGrowSeconds);
        target.localScale = fullScale;
        float strength = CrackStrength(age, DisplaySettings.ReducedEffects) * DarkRules.ClampBrightness(brightness);
        float visibility = NoteVeil.CueAlpha(veil);
        Color c = new Color(strength, strength, strength, visibility);
        if (material.HasProperty(CrackBaseId)) material.SetColor(CrackBaseId, c);
        else if (material.HasProperty(CrackColorId)) material.SetColor(CrackColorId, c);
        if (veil > 0f) strength *= visibility;
        if (material.HasProperty(CrackEmissionId))
            material.SetColor(CrackEmissionId, new Color(1.4f * strength, 1.4f * strength, 1.4f * strength, 1f));
    }

    public void TickCracks(float deltaTime)
    {
        if (!isActiveAndEnabled || IsCut || IsMissed || HidesCues || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
        float end = CrackGrowSeconds + CrackHoldSeconds + CrackFadeSeconds;
        for (int i = 0; i < cracksUsed; i++)
        {
            var crack = ownedCracks[i];
            // 終了値は通常・LOW共通。最後の反映を終えたひびは触らない。
            if (crack.age >= end) continue;
            crack.age = Mathf.Min(end, crack.age + Mathf.Max(0f, deltaTime));
            ApplyCrack(crack.transform, crack.material, crack.fullScale, crack.age, Veil, Brightness);
        }
    }
}
