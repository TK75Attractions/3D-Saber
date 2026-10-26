using System.Collections.Generic;
using UnityEngine;

public partial class FloorRenderer
{
    readonly DarkStageMaterials darkMaterials = new DarkStageMaterials();
    public float DarkBrightness { get; private set; } = 1f;

    // 空・水面・霧・サビの演出を含む最終RGBへ掛ける。曲時計や演出の強さ自体は変えない。
    public void SetDarkBrightness(float brightness)
    {
        DarkBrightness = DarkRules.Finite(brightness) ? Mathf.Clamp01(brightness) : 1f;
        darkMaterials.Apply(transform, DarkBrightness);
    }
}

// 本編で生成した背景の材質だけに使う。ノーツや剣の共有材質には触れない。
// 動的に増える演出も拾うが、一覧の入れ物は再利用し、毎コマ配列を確保しない。
internal sealed class DarkStageMaterials
{
    static readonly int BrightnessId = Shader.PropertyToID("_DarkBrightness");
    readonly List<Renderer> renderers = new List<Renderer>();
    readonly List<Material> materials = new List<Material>();

    public void Apply(Transform root, float brightness)
    {
        if (root == null) return;
        renderers.Clear();
        root.GetComponentsInChildren(true, renderers);
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            materials.Clear();
            renderer.GetSharedMaterials(materials);
            foreach (var material in materials)
                if (material != null && material.HasProperty(BrightnessId))
                    material.SetFloat(BrightnessId, brightness);
        }
    }
}
