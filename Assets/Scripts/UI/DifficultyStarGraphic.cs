using UnityEngine;
using UnityEngine.UI;

// 難易度バッジ用の五芒星。画像に焼き込まず、独立したベクターUIとして描画する。
public class DifficultyStarGraphic : MaskableGraphic
{
    [Range(0.2f, 0.8f)] public float innerRadius = 0.48f;

    public static Vector2 NormalizedPoint(int index, float innerRatio = 0.48f)
    {
        int wrapped = ((index % 10) + 10) % 10;
        float radius = wrapped % 2 == 0 ? 1f : Mathf.Clamp(innerRatio, 0.2f, 0.8f);
        float angle = (90f - wrapped * 36f) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = rectTransform.rect;
        Vector2 center = r.center;
        float radius = Mathf.Min(r.width, r.height) * 0.5f;

        vh.AddVert(center, color, new Vector2(0.5f, 0.5f));
        for (int i = 0; i < 10; i++)
        {
            Vector2 p = NormalizedPoint(i, innerRadius);
            vh.AddVert(center + p * radius, color, (p + Vector2.one) * 0.5f);
        }
        for (int i = 0; i < 10; i++)
        {
            int a = i + 1;
            int b = ((i + 1) % 10) + 1;
            vh.AddTriangle(0, a, b);
        }
    }
}
