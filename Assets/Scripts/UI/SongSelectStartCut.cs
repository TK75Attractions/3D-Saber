using UnityEngine;
using UnityEngine.UI;

// スタートの瞬間にジャケットを斬る演出(派手さカタログ 盤3、2026-10-20)。中央の盤の子に2枚置く。
//   0.00秒 青(左上→右下)の斬撃がジャケットの上を走り、0.04秒遅れて赤(右上→左下)が走る(それぞれ0.08秒)
//   0.12秒 ジャケットが X の対角線で4片(右・上・左・下)に割れ、0.35秒で外へ飛ぶ
//   金の輪が 540→900px へ0.4秒で広がる。盤の拡大(1.0→1.25倍)は SongSelectSkin が盤ごと行う。
// 割れた片はジャケットの絵(Shards)、斬撃と輪は白い面(Lights)で描く。片ごとの物体は作らない。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectStartCut : MaskableGraphic
{
    public enum Layer { Shards, Lights }
    public const float SlashSeconds = .08f, RedDelay = .04f, SplitAt = .12f, FlySeconds = .35f, RingSeconds = .4f;
    public const float RingFrom = 270, RingTo = 450;
    const int ArcSegments = 24;

    public Layer Part;
    public Sprite Artwork;
    public float Radius = 227;
    public float Age { get; private set; } = -1;

    public override Texture mainTexture => Part == Layer.Shards && Artwork != null ? Artwork.texture : s_WhiteTexture;
    public static bool IsSplit(float age) => age >= SplitAt;

    public static SongSelectStartCut Create(RectTransform parent, Layer part, Sprite artwork, Color tint, float radius)
    {
        var rect = SongSelectVisuals.Rect(parent, part == Layer.Shards ? "StartCutShards" : "StartCutLights", Vector2.zero, new Vector2(radius * 2, radius * 2));
        var cut = rect.gameObject.AddComponent<SongSelectStartCut>();
        cut.Part = part; cut.Artwork = artwork; cut.color = tint; cut.Radius = radius; cut.raycastTarget = false;
        return cut;
    }

    public void SetAge(float age)
    {
        if (age == Age) return;
        Age = age;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (Age < 0) return;
        if (Part == Layer.Shards) DrawShards(vh); else DrawLights(vh);
    }

    void DrawShards(VertexHelper vh)
    {
        if (!IsSplit(Age)) return;
        float p = Mathf.Clamp01((Age - SplitAt) / FlySeconds);
        if (p >= 1) return;
        float ease = 1 - (1 - p) * (1 - p) * (1 - p);
        Rect uv = SongSelectDiscGraphic.CoverUvRect(Artwork);
        Color tint = color; tint.a *= 1 - p * p;
        for (int k = 0; k < 4; k++)
        {
            float bisector = k * Mathf.PI * .5f;
            Vector2 outward = new Vector2(Mathf.Cos(bisector), Mathf.Sin(bisector));
            // 片の重心のまわりに回しながら、外へ飛ばして少し落とす。
            Vector2 pivot = outward * Radius * .6f;
            Vector2 offset = outward * (Radius * .95f * ease) + Vector2.down * (120 * p * p);
            float spin = (k % 2 == 0 ? 1 : -1) * 22 * ease * Mathf.Deg2Rad;
            int start = vh.currentVertCount;
            vh.AddVert(Place(Vector2.zero, pivot, offset, spin), tint, uv.center);
            for (int s = 0; s <= ArcSegments; s++)
            {
                float a = bisector - Mathf.PI * .25f + s * (Mathf.PI * .5f) / ArcSegments;
                Vector2 direction = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                vh.AddVert(Place(direction * Radius, pivot, offset, spin), tint, uv.center + Vector2.Scale(direction, uv.size) * .5f);
                if (s > 0) vh.AddTriangle(start, start + s, start + s + 1);
            }
        }
    }

    static Vector2 Place(Vector2 point, Vector2 pivot, Vector2 offset, float spin)
    {
        Vector2 d = point - pivot;
        float c = Mathf.Cos(spin), s = Mathf.Sin(spin);
        return pivot + new Vector2(d.x * c - d.y * s, d.x * s + d.y * c) + offset;
    }

    void DrawLights(VertexHelper vh)
    {
        Slash(vh, new Vector2(-1, 1), new Vector2(1, -1), Age, UISkinPalette.LogoBlue);
        Slash(vh, new Vector2(1, 1), new Vector2(-1, -1), Age - RedDelay, UISkinPalette.LogoRed);
        float p = Mathf.Clamp01(Age / RingSeconds);
        if (p >= 1) return;
        float ease = 1 - (1 - p) * (1 - p) * (1 - p);
        Color gold = SongSelectSkin.Gold; gold.a = Mathf.Pow(1 - p, 1.5f);
        SongSelectDiscGraphic.Ring(vh, Mathf.Lerp(RingFrom, RingTo, ease), Mathf.Lerp(16, 4, p), 1, gold);
    }

    // 斬撃の帯。先端が0.08秒で端まで走り、その後0.25秒で消える。外側の淡い光と白に寄せた芯の2層。
    void Slash(VertexHelper vh, Vector2 from, Vector2 to, float age, Color tint)
    {
        if (age < 0) return;
        float fade = 1 - Mathf.Clamp01((age - SlashSeconds) / .25f);
        if (fade <= 0) return;
        float reach = Radius * 1.25f;
        Vector2 a = from.normalized * reach, b = to.normalized * reach;
        Vector2 head = Vector2.Lerp(a, b, Mathf.Clamp01(age / SlashSeconds));
        Color glow = tint; glow.a = .38f * fade;
        Color core = Color.Lerp(tint, Color.white, .6f); core.a = fade;
        Band(vh, a, head, 38, glow);
        Band(vh, a, head, 10, core);
    }

    // a→b の帯。両端は細くして刃の跡に見せる。
    static void Band(VertexHelper vh, Vector2 a, Vector2 b, float width, Color tint)
    {
        Vector2 along = b - a;
        if (along.sqrMagnitude < 1) return;
        Vector2 n = new Vector2(-along.y, along.x).normalized * width * .5f;
        Vector2 mid = (a + b) * .5f;
        int start = vh.currentVertCount;
        vh.AddVert(a, tint, Vector2.zero);
        vh.AddVert(mid + n, tint, Vector2.zero);
        vh.AddVert(b, tint, Vector2.zero);
        vh.AddVert(mid - n, tint, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
