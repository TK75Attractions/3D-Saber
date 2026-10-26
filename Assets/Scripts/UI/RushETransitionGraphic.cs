using UnityEngine;
using UnityEngine.UI;

// 64×36の細かい四角が、切ったまん中の盤から波のように広がって覆い、また盤からほどける。点滅は使わない。
// 2026-10-26 ユーザー「裏譜面突入時のドット演出があらすぎる」で16×9(1マス120px)から1マス30pxへ細かくし、向きは「盤から広がる」に。
// 細かくすると決まった並びの濃淡は砂嵐に見えるので、面はなめらかな明暗にごく弱い粒、伸び縮みの途中の小さな四角ほど明るくする(光る波頭)。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class RushETransitionGraphic : MaskableGraphic
{
    public const int Columns = 64, Rows = 36;
    // 覆う: 盤からの遠さ×0.42秒と粒(0〜0.06秒)だけ遅れて0.26秒で伸びる。いちばん遠い四角も0.74秒で伸びきり、0.82秒の切り替えは全面の裏。
    // ほどく: 0.94秒から遠さ×0.45秒と粒だけ遅れて0.30秒で縮む。1.75秒で消えきり、1.85秒に幕を外す。
    const float SweepIn = .42f, GrowIn = .26f, HoldUntil = .94f, SweepOut = .45f, ShrinkOut = .30f, Grain = .06f;
    // 1920×1080 の選曲での1マスと、波の中心(中央の盤)。遠さは盤の中心から画面のいちばん遠い角までを1とする。
    const float Cell = 1920f / Columns;
    static readonly Vector2 Origin = SongSelectSkin.DiscSlotPosition(0);
    static readonly float Farthest = new Vector2(960 + Mathf.Abs(Origin.x), 540 + Mathf.Abs(Origin.y)).magnitude;
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
    // そのマスの中心の、盤の中心からの遠さ(0〜1)。
    public static float Reach(int x, int y) =>
        (new Vector2((x - (Columns - 1) * .5f) * Cell, (y - (Rows - 1) * .5f) * Cell) - Origin).magnitude / Farthest;
    public static float TileScale(float elapsed, int x, int y)
    {
        if (elapsed < 0) return 0;
        float reach = Reach(x, y);
        if (elapsed < HoldUntil) return Mathf.SmoothStep(0, 1, (elapsed - reach * SweepIn - Grain * Noise(x, y, 1)) / GrowIn);
        return 1 - Mathf.SmoothStep(0, 1, (elapsed - HoldUntil - reach * SweepOut - Grain * Noise(x, y, 2)) / ShrinkOut);
    }
    // マスごとに決まる0〜1の粒。毎フレーム同じで、並びの周期が見えない整数ハッシュ。
    static float Noise(int x, int y, uint salt)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + salt * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
    }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); if (Elapsed < 0 || Elapsed >= RushESecretState.TransitionSeconds) return;
        Rect r = rectTransform.rect;
        float size = Mathf.Max(r.width / Columns, r.height / Rows);
        // 波頭の色は明るい色をさらに35%明るく(1を超えない)。
        Color spark = new Color(Mathf.Min(1, LightColor.r * 1.35f), Mathf.Min(1, LightColor.g * 1.35f), Mathf.Min(1, LightColor.b * 1.35f), LightColor.a);
        for (int y = 0; y < Rows; y++) for (int x = 0; x < Columns; x++)
        {
            float scale = TileScale(Elapsed, x, y); if (scale <= 0) continue;
            Vector2 p = r.center + new Vector2((x - (Columns - 1) * .5f) * size, (y - (Rows - 1) * .5f) * size);
            float h = size * scale * .506f;
            // 面は盤の近くほど少し明るく、ごく弱い粒。伸び縮みの途中(小さい)ほど波頭の色に寄せる。
            Color face = Color.Lerp(DeepColor, LightColor, .18f + .22f * (1 - Reach(x, y)) + (Noise(x, y, 3) - .5f) * .12f);
            Color c = Color.Lerp(face, spark, 1 - scale);
            int at = vh.currentVertCount;
            vh.AddVert(new Vector3(p.x - h, p.y - h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x + h, p.y - h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x + h, p.y + h), c, Vector2.zero);
            vh.AddVert(new Vector3(p.x - h, p.y + h), c, Vector2.zero);
            vh.AddTriangle(at, at + 1, at + 2); vh.AddTriangle(at, at + 2, at + 3);
        }
    }
}
