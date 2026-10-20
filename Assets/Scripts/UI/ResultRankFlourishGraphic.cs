using UnityEngine;
using UnityEngine.UI;

// ランク着地の金の放射光と紙吹雪(賞2)。一枚のUIメッシュを既存の時計で動かす。
// LOW は動かない薄い光だけ。スキップ・寿命終了後は空のメッシュにする。
public sealed class ResultRankFlourishGraphic : MaskableGraphic
{
    const float GrowSeconds = .25f;
    const float ConfettiSeconds = 3f;
    static readonly Color Gold = new Color(1f, .78f, .25f);
    static readonly Color[] ConfettiColors =
    {
        Gold, UISkinPalette.Cyan, new Color(1f, .32f, .76f), Color.white
    };

    ResultReveal reveal;
    PlayRank rank;
    float lastT, age;
    bool visible, reduced, suppressed;

    public static ResultRankFlourishGraphic Build(Transform parent, ResultReveal timeline, PlayRank rank)
    {
        var go = new GameObject("RankFlourish", typeof(RectTransform), typeof(CanvasRenderer));
        go.transform.SetParent(parent, false);
        var view = go.AddComponent<ResultRankFlourishGraphic>();
        view.rectTransform.anchoredPosition = new Vector2(0f, 74f);
        view.rectTransform.sizeDelta = new Vector2(1920f, 2160f);
        view.raycastTarget = false;
        view.rank = rank;
        view.reveal = timeline;
        if (timeline != null) timeline.TimeChanged += view.Tick;
        view.Tick(0f);
        return view;
    }

    static float RaySeconds(PlayRank rank)
        => rank == PlayRank.SPlus ? GrowSeconds + 2.6f : rank == PlayRank.S ? GrowSeconds + 1.8f : 0f;

    public void Tick(float t)
    {
        if (t < lastT) return;
        bool skipped = t - lastT > ResultSoundtrack.SkipJump;
        if (skipped && t >= ResultSkin.RankLandingTime) suppressed = true;
        lastT = t;

        bool wasVisible = visible;
        age = t - ResultSkin.RankLandingTime;
        reduced = DisplaySettings.ReducedEffects;
        float lifetime = rank == PlayRank.SPlus ? ConfettiSeconds : RaySeconds(rank);
        visible = !suppressed && age >= 0f && age < lifetime;
        if (visible || wasVisible) SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (!visible) return;

        int rays = rank == PlayRank.SPlus ? 18 : rank == PlayRank.S ? 10 : 0;
        float raySeconds = RaySeconds(rank);
        if (rays > 0 && age < raySeconds)
        {
            float p = Mathf.Clamp01(age / GrowSeconds);
            float grow = reduced ? 1f : 1f - (1f - p) * (1f - p) * (1f - p);
            float fade = 1f - Mathf.Clamp01((age - GrowSeconds) / (raySeconds - GrowSeconds));
            Color bright = Gold;
            bright.a = (reduced ? .055f : .22f) * grow * fade;
            Color clear = bright; clear.a = 0f;
            for (int i = 0; i < rays; i++)
            {
                float angle = i * Mathf.PI * 2f / rays + (reduced ? 0f : age * .07f);
                float width = .027f + .006f * (i % 3);
                float outer = Mathf.Lerp(160f, 680f + i % 3 * 55f, grow);
                Quad(mesh, Direction(angle - width) * 160f, Direction(angle + width) * 160f,
                    Direction(angle + width) * outer, Direction(angle - width) * outer, bright, clear);
            }
        }

        if (reduced || rank != PlayRank.SPlus || age >= ConfettiSeconds) return;
        // 粒の初期値は番号から決める。乱数状態・粒オブジェクト・毎フレームの配列を持たない。
        float alpha = .9f * Mathf.Clamp01((ConfettiSeconds - age) / .85f);
        for (int i = 0; i < 60; i++)
        {
            float angle = i * 2.399963f;
            float speed = 220f + i * 37 % 180;
            Vector2 p = Direction(angle) * (135f + speed * age)
                + new Vector2(0f, 210f * age - 180f * age * age);
            float flip = .12f + .88f * Mathf.Abs(Mathf.Cos(age * (7f + i % 4) + i));
            float spin = i * .71f + age * (i % 2 == 0 ? 1.8f : -1.5f);
            Vector2 right = Direction(spin) * ((5f + i % 4) * flip);
            Vector2 up = Direction(spin + Mathf.PI * .5f) * (3f + i % 3);
            Color tint = ConfettiColors[i % ConfettiColors.Length]; tint.a = alpha;
            Quad(mesh, p - right - up, p - right + up, p + right + up, p + right - up, tint, tint);
        }
    }

    protected override void OnDestroy()
    {
        if (reveal != null) reveal.TimeChanged -= Tick;
        base.OnDestroy();
    }

    static Vector2 Direction(float angle) => new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

    static void Quad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color inner, Color outer)
    {
        int start = mesh.currentVertCount;
        mesh.AddVert(a, inner, Vector2.zero); mesh.AddVert(b, inner, Vector2.zero);
        mesh.AddVert(c, outer, Vector2.zero); mesh.AddVert(d, outer, Vector2.zero);
        mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
    }
}
