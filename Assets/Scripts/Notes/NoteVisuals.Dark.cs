using System.Collections.Generic;
using UnityEngine;

public partial class NoteVisuals
{
    public float Brightness { get; private set; } = 1f;
    Dictionary<Material, Color> darkOriginalColors;

    // V/Mの透明度・描画可否と独立したRGB倍率。元のRGBを持ち、往復で色を劣化させない。
    public void SetBrightness(float brightness)
    {
        Initialize();
        brightness = DarkRules.ClampBrightness(brightness);
        if (Brightness == brightness) return;
        darkOriginalColors ??= new Dictionary<Material, Color>();
        RememberDarkColor(runtimeBodyMat);
        foreach (var material in ownedSubMaterials) RememberDarkColor(material);
        RememberDarkColor(contrastOutlineMaterial);
        Brightness = brightness;
        foreach (var pair in darkOriginalColors)
        {
            var material = pair.Key;
            if (material == null) continue;
            float factor = brightness * (material == runtimeBodyMat && bodyDimmed ? .4f : 1f);
            Color color = DarkRules.Rgb(pair.Value, factor);
            color.a = ReadBaseColor(material).a;
            SetBaseColor(material, color);
        }
        lastAppliedStrength = -1f;
        ApplyCurrentEmission();
        if (bodyDimmed) ApplyEmissionStrength(runtimeBodyMat, .2f);
    }

    void RememberDarkColor(Material material)
    {
        if (material == null || darkOriginalColors.ContainsKey(material)) return;
        Color color = ReadBaseColor(material);
        // 明るい区間でMissになったノーツを、初めて暗くする場合も基準色は通常時のものにする。
        if (material == runtimeBodyMat && bodyDimmed) color = DarkRules.Rgb(color, 1f / .4f);
        darkOriginalColors.Add(material, color);
    }
}
