using UnityEngine;
using UnityEngine.Rendering;

// 見えにくさの共通規則。描画だけに使い、ノーツの判定や時計には介入しない。
internal static class NoteVeil
{
    internal static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    internal static readonly int ColorId = Shader.PropertyToID("_Color");
    internal static readonly int SurfaceId = Shader.PropertyToID("_Surface");
    internal static readonly int ModeId = Shader.PropertyToID("_Mode");
    internal static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
    internal static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
    internal static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

    internal static float Clamp(float value)
        => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);

    internal static float CueAlpha(float veil)
        => veil >= MemoryVeilLayers.HideCuesAtVeil ? 0f : 1f - veil;

    internal static bool IsAlphaBlended(Material material)
        => material != null && material.HasProperty(SrcBlendId)
            && material.GetFloat(SrcBlendId) == (float)BlendMode.SrcAlpha;

    internal static void SetAlpha(Material material, float alpha)
    {
        if (material == null) return;
        int property;
        if (material.HasProperty(BaseColorId)) property = BaseColorId;
        else if (material.HasProperty(ColorId)) property = ColorId;
        else return;
        Color color = material.GetColor(property);
        color.a = alpha;
        material.SetColor(property, color);
    }

    // MakeCrackMaterial が作る不透明材質専用。通常ノーツへ戻すときは生成時の設定へ戻す。
    internal static void SetCrackTransparency(Material material, bool transparent)
    {
        SetFloat(material, SurfaceId, transparent ? 1f : 0f);
        SetFloat(material, ModeId, transparent ? 2f : 0f);
        SetFloat(material, SrcBlendId, (float)(transparent ? BlendMode.SrcAlpha : BlendMode.One));
        SetFloat(material, DstBlendId, (float)(transparent ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
        SetFloat(material, ZWriteId, transparent ? 0f : 1f);
        if (transparent)
        {
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
        }
        else
        {
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHABLEND_ON");
        }
        material.renderQueue = transparent ? 3000 : -1;
    }

    static void SetFloat(Material material, int property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }
}
