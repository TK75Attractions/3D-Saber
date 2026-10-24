using UnityEngine;
using UnityEngine.UI;

// 16×9の正方形が少しずつ覆い、別の波でほどける。点滅は使わない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class RushETransitionGraphic : MaskableGraphic
{
    public float Elapsed { get; private set; } = -1;
    // 四角の暗い色と明るい色(隠し譜面ごと。既定は RUSH E の紫)。
    public Color DeepColor { get; private set; } = RushESecretState.RushE.TileDeep;
    public Color LightColor { get; private set; } = RushESecretState.RushE.TileLight;
    public void SetPalette(SecretChart secret)
    {
        if (secret == null) return;
        DeepColor = secret.TileDeep; LightColor = secret.TileLight; SetVerticesDirty();
    }
    public void Show(float elapsed) { Elapsed = elapsed; SetVerticesDirty(); }
    public static float TileScale(float elapsed, int x, int y)
    {
        float delay = (x + y) * .011f + ((x * 7 + y * 11) % 5) * .012f;
        if (elapsed < 0) return 0;
        if (elapsed < .90f) return Mathf.SmoothStep(0, 1, (elapsed - delay) / .39f);
        return 1 - Mathf.SmoothStep(0, 1, (elapsed - .94f - (15 - x + y) * .010f) / .47f);
    }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); if (Elapsed < 0 || Elapsed >= RushESecretState.TransitionSeconds) return;
        Rect r = rectTransform.rect;
        float size = Mathf.Max(r.width / 16, r.height / 9);
        for (int y = 0; y < 9; y++) for (int x = 0; x < 16; x++)
        {
            float scale = TileScale(Elapsed, x, y); if (scale <= 0) continue;
            Vector2 p = r.center + new Vector2((x - 7.5f) * size, (y - 4) * size);
            float h = size * scale * .506f;
            Color c = Color.Lerp(DeepColor, LightColor, ((x * 3 + y * 5) % 9) / 8f);
            int at = vh.currentVertCount;
            vh.AddVert(new Vector3(p.x - h, p.y - h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x + h, p.y - h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x + h, p.y + h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x - h, p.y + h), c, Vector2.zero);
            vh.AddTriangle(at, at + 1, at + 2); vh.AddTriangle(at, at + 2, at + 3);
        }
    }
}
