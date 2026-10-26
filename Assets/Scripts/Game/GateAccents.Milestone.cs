using UnityEngine;

public sealed partial class GateAccents
{
    static Color MilestoneTint(int index) => index == 0 ? Color.white
        : index == 1 ? UISkinPalette.NoteGold : new Color(.3f, 1f, 1f);

    static Vector2 ShapePoint(int shape, float phase)
    {
        if (shape == 0)
        {
            float a = phase * Mathf.PI * 2;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        int corners = shape == 1 ? 4 : 10;
        float at = phase * corners;
        int index = Mathf.FloorToInt(at);

        Vector2 Vertex(int k)
        {
            float angle = Mathf.PI * .5f + k * Mathf.PI * 2 / corners;
            float radius = shape == 2 && (k & 1) != 0 ? .52f : 1;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }

        return Vector2.Lerp(Vertex(index), Vertex(index + 1), at - index);
    }

    void DrawMilestoneShape(int shape, float radius, float width, Color tint)
    {
        const int segments = 120;
        for (int i = 0; i < segments; i++)
        {
            Vector2 a = ShapePoint(shape, i / (float)segments) * radius;
            Vector2 b = ShapePoint(shape, (i + 1) / (float)segments) * radius;
            // 帯の全体が同じ辺の外にある区間だけ描く。角を横切る区間も落とす。
            Vector2 lo = Vector2.Min(a, b) - Vector2.one * width;
            Vector2 hi = Vector2.Max(a, b) + Vector2.one * width;
            float w = halfWidth + OutsideMargin, h = halfHeight + OutsideMargin;
            if (lo.x >= w || hi.x <= -w || lo.y >= h || hi.y <= -h)
                Band(a, b, width, tint, false);
        }
    }
}
