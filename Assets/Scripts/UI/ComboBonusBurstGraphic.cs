using UnityEngine;
using UnityEngine.UI;

// 放射光・衝撃波・メダルを一枚のUIメッシュで描く。粒ごとのオブジェクトは生成しない。
public class ComboBonusBurstGraphic : MaskableGraphic
{
    int maximum;
    float age;
    bool visible, reduced;

    public void SetFrame(int combo, float elapsed, bool show, bool low)
    {
        maximum = combo; age = elapsed; visible = show; reduced = low;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (!visible || maximum <= 0) return;
        int tier = ComboBonusPresentation.Tier(maximum);
        Color accent = ComboBonusPresentation.Accent(maximum);
        int rays = 14 + tier * 6;
        for (int i = 0; i < rays; i++)
        {
            float angle = i * Mathf.PI * 2 / rays + (reduced ? 0 : age * .07f);
            float width = .017f + .006f * (i % 3);
            Color bright = accent; bright.a = reduced ? .055f : .16f + tier * .014f;
            Color clear = bright; clear.a = 0;
            Quad(mesh, Direction(angle - width) * 170, Direction(angle + width) * 170,
                Direction(angle + width) * 1300, Direction(angle - width) * 1300, bright, clear);
        }
        if (reduced) return;
        float ring = Mathf.Clamp01(age / .85f);
        float radius = Mathf.Lerp(160, 1050, 1 - Mathf.Pow(1 - ring, 3));
        Color ringColor = accent; ringColor.a = (1 - ring) * .65f;
        for (int i = 0; i < 96; i++)
        {
            var a = Direction(i * Mathf.PI * 2 / 96);
            var b = Direction((i + 1) * Mathf.PI * 2 / 96);
            Quad(mesh, a * radius, b * radius, b * (radius + 8), a * (radius + 8), ringColor, ringColor);
        }
        int coins = 8 + tier * 7;
        for (int i = 0; i < coins; i++)
        {
            float angle = i * 2.399963f;
            float speed = 210 + (i % 7) * 38;
            Vector2 p = Direction(angle) * (290 + speed * age) + new Vector2(0, 160 * age - 110 * age * age);
            float r = 10 + i % 5 * 3;
            float sx = .25f + .75f * Mathf.Abs(Mathf.Cos(age * 4 + i));
            Color gold = tier == 4 && i % 3 == 0 ? Color.HSVToRGB((i * .137f) % 1, .55f, 1) : new Color(1, .76f, .18f);
            gold.a = Mathf.Clamp01(1 - age / 3.2f);
            Color edge = new Color(.48f, .23f, .025f, gold.a);
            Disc(mesh, p + new Vector2(3, -3), r + 2, sx, edge);
            Disc(mesh, p, r, sx, gold);
            Disc(mesh, p + new Vector2(-2 * sx, 2), r * .63f, sx, new Color(1, .94f, .62f, gold.a));
            if (tier >= 2 && i % 2 == 0)
            {
                Vector2 star = p + new Vector2(28, 24);
                float length = (8 + tier * 2) * (.65f + .35f * Mathf.Sin(age * 6 + i));
                Color white = new Color(1, 1, .86f, gold.a);
                Quad(mesh, star + Vector2.left * length, star + Vector2.up * 2,
                    star + Vector2.right * length, star + Vector2.down * 2, white, white);
                Quad(mesh, star + Vector2.down * length, star + Vector2.left * 2,
                    star + Vector2.up * length, star + Vector2.right * 2, white, white);
            }
        }
    }

    static Vector2 Direction(float a) => new Vector2(Mathf.Cos(a), Mathf.Sin(a));
    static void Disc(VertexHelper mesh, Vector2 p, float radius, float squash, Color tint)
    {
        int start = mesh.currentVertCount;
        mesh.AddVert(p, tint, Vector2.zero);
        for (int i = 0; i <= 12; i++)
        {
            var v = Direction(i * Mathf.PI * 2 / 12) * radius;
            v.x *= squash;
            mesh.AddVert(p + v, tint, Vector2.zero);
            if (i > 0) mesh.AddTriangle(start, start + i, start + i + 1);
        }
    }
    static void Quad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color inner, Color outer)
    {
        int start = mesh.currentVertCount;
        mesh.AddVert(a, inner, Vector2.zero); mesh.AddVert(b, inner, Vector2.zero);
        mesh.AddVert(c, outer, Vector2.zero); mesh.AddVert(d, outer, Vector2.zero);
        mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
    }
}
