using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 切断が確定した場所に、判定段階に応じた短い演出を出す(Perfect > Great > Good。Bad と Miss は出さない)。
// 2026-09-23: ユーザー依頼で段階化。A案「リング+火花+光片」、Perfect は虹色の二重リングと白い芯、切断片の縁も一瞬発光。
// 画面全体のフラッシュやカメラ揺れ、物理判定は追加しない。ノーツ付近に文字は出さない。
public sealed class GameplayCutFeedback : MonoBehaviour
{
    public const int MaxBursts = 12;
    public const float BurstLifetime = .24f;
    const float SlashLifetime = .075f;
    const float FlashLifetime = .07f;
    public const int SparkCount = 12;      // Perfect の火花本数(Great 8 / Good 4 / LOW 2)
    public const int ShardCount = 6;       // Perfect だけの光片
    public const int RingSegments = 20;
    public const int RingCount = 2;        // Perfect は二重、Great/Good は一重
    // 1バーストの最大四角形数 = 斬撃2 + 火花 + 光片 + リング + 中心フラッシュ2
    public const int VerticesPerBurst = (2 + SparkCount + ShardCount + RingSegments * RingCount + 2) * 4;
    public const float SliceFlashSeconds = .30f;

    struct Burst
    {
        public bool active;
        public JudgmentTier tier;
        public Vector3 position;
        public Vector2 direction;
        public Color color;
        public float age, scale, gain, hue;
    }

    // 判定段階ごとの量。LOW(演出控えめ)は火花2本・光片なし・一重リング・中心フラッシュなし。
    struct TierStyle { public int sparks, shards, rings; public float size, gain; public bool rainbow, flash; }

    static TierStyle StyleFor(JudgmentTier tier, bool reduced)
    {
        TierStyle style;
        switch (tier)
        {
            case JudgmentTier.Perfect:
                style = new TierStyle { sparks = SparkCount, shards = ShardCount, rings = RingCount, size = 1.25f, gain = 1f, rainbow = true, flash = true };
                break;
            case JudgmentTier.Great:
                style = new TierStyle { sparks = 8, shards = 0, rings = 1, size = .85f, gain = .8f };
                break;
            default:
                style = new TierStyle { sparks = 4, shards = 0, rings = 1, size = .62f, gain = .6f };
                break;
        }
        if (reduced) { style.sparks = 2; style.shards = 0; style.rings = 1; style.flash = false; }
        return style;
    }

    // 演出を出す判定。Bad は切断片と画面下の文字だけ、Miss は何も出さない。
    public static bool Draws(JudgmentTier tier)
    {
        return tier == JudgmentTier.Perfect || tier == JudgmentTier.Great || tier == JudgmentTier.Good;
    }

    readonly Burst[] bursts = new Burst[MaxBursts];
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly List<Vector3> vertices = new List<Vector3>(MaxBursts * VerticesPerBurst);
    readonly List<Color> colors = new List<Color>(MaxBursts * VerticesPerBurst);
    readonly List<Vector2> uvs = new List<Vector2>(MaxBursts * VerticesPerBurst);
    readonly List<int> triangles = new List<int>(MaxBursts * VerticesPerBurst * 3 / 2);
    NoteSpawner owner;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    public int ActiveCount { get; private set; }

    public static GameplayCutFeedback Create(NoteSpawner spawner)
    {
        var go = new GameObject("GameplayCutFeedback");
        go.transform.SetParent(spawner.transform, false);
        var feedback = go.AddComponent<GameplayCutFeedback>();
        feedback.owner = spawner;
        feedback.BuildRenderer();
        return feedback;
    }

    void BuildRenderer()
    {
        // Resourcesから参照し、ビルドでも頂点色を使う専用シェーダーが残るようにする。
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null) return;
        mesh = new Mesh { name = "GameplayCutFeedback", hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        material = new Material(shader) { name = "GameplayCutFeedback", hideFlags = HideFlags.DontSave };
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        meshRenderer.enabled = false;
    }

    public void Track(CuttableNote note)
    {
        if (note == null || !tracked.Add(note)) return;
        note.OnRetired += Untrack;
        note.OnJudged += HandleCut;
        note.OnMiss += HandleMiss;
    }

    void Untrack(CuttableNote note)
    {
        if (note == null) return;
        note.OnRetired -= Untrack;
        note.OnJudged -= HandleCut;
        note.OnMiss -= HandleMiss;
        tracked.Remove(note);
    }

    void HandleMiss(CuttableNote note) { Untrack(note); }

    void HandleCut(CuttableNote note, JudgmentTier tier, Vector3 hitPoint, Vector3 velocity)
    {
        Untrack(note);
        // 部分達成ロングのタイムアウトもOnCutを通知するため、実際に切れた時だけ描く。
        if (!Draws(tier) || note == null || !note.IsCut || note.IsMissed || !isActiveAndEnabled
            || owner == null || !owner.isActiveAndEnabled || mesh == null) return;
        if (!Finite(hitPoint) || !Finite(velocity)) return;

        Vector3 center = note.transform.position;
        Vector3 size = note.transform.lossyScale;
        float scale = Mathf.Clamp(Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y)), .6f, 1.5f);
        var renderer = note.GetComponent<MeshRenderer>();
        // ノーツ破棄後はTransformを参照しない。正面へ少しだけ出して二分割片とZ競合を避ける。
        Vector3 position = new Vector3(
            Mathf.Clamp(hitPoint.x, center.x - scale * .5f, center.x + scale * .5f),
            Mathf.Clamp(hitPoint.y, center.y - scale * .5f, center.y + scale * .5f),
            renderer != null ? renderer.bounds.min.z - .035f : center.z - .55f);
        Vector2 direction = new Vector2(velocity.x, velocity.y);
        direction = direction.sqrMagnitude > .0001f ? direction.normalized : Vector2.right;
        var visuals = note.GetComponent<NoteVisuals>();
        Color accent = visuals != null ? visuals.baseColor : UISkinPalette.NoteFlick;
        if (note.IsGold) accent = UISkinPalette.NoteGold;
        accent.a = 1f;

        // Perfect は切断片の縁も短く発光させる(ノーツ色を白へ寄せた色)。
        if (tier == JudgmentTier.Perfect) note.FlashSlices(Color.Lerp(accent, Color.white, .55f), SliceFlashSeconds);

        int slot = -1;
        int oldest = 0;
        for (int i = 0; i < bursts.Length; i++)
        {
            if (!bursts[i].active && slot < 0) slot = i;
            if (bursts[i].age > bursts[oldest].age) oldest = i;
            // 同じ場所の同時切りを白く積み重ねず、短い一つのアクセントへまとめる。
            if (bursts[i].active && bursts[i].age < .035f
                && (bursts[i].position - position).sqrMagnitude < .09f)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) slot = oldest;
        // 同時切りをまとめるときは良い方の判定で描く。
        JudgmentTier drawn = bursts[slot].active ? (JudgmentTier)Mathf.Min((int)tier, (int)bursts[slot].tier) : tier;
        if (!bursts[slot].active) ActiveCount++;
        bursts[slot] = new Burst {
            active = true, tier = drawn, position = position, direction = direction, color = accent,
            scale = scale, age = 0f, gain = 1f / Mathf.Sqrt(1f + .12f * (ActiveCount - 1)),
            hue = Mathf.Repeat(position.x * .37f + position.y * .61f + .13f, 1f)
        };
        RebuildMesh();
    }

    // NoteSpawner.Tickからだけ呼ぶ。既存の破片と同じTime.deltaTimeで寿命を進める。
    public void Tick(float deltaTime)
    {
        if (!isActiveAndEnabled || ActiveCount == 0) return;
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return;
        for (int i = 0; i < bursts.Length; i++)
        {
            if (!bursts[i].active) continue;
            bursts[i].age += Mathf.Max(0f, deltaTime);
            if (bursts[i].age >= BurstLifetime)
            {
                bursts[i].active = false;
                ActiveCount--;
            }
        }
        RebuildMesh();
    }

    void RebuildMesh()
    {
        if (mesh == null) return;
        vertices.Clear(); colors.Clear(); uvs.Clear(); triangles.Clear();
        bool reduced = DisplaySettings.ReducedEffects;
        float accentScale = DisplaySettings.AccentScale;
        foreach (var burst in bursts)
        {
            if (!burst.active) continue;
            TierStyle style = StyleFor(burst.tier, reduced);
            float t = Mathf.Clamp01(burst.age / BurstLifetime);
            float life = 1f - t;
            float eased = 1f - life * life;          // 立ち上がりを速く、終わりをゆっくり
            float size = burst.scale * style.size;
            float gain = burst.gain * style.gain * accentScale;
            Vector3 direction = new Vector3(burst.direction.x, burst.direction.y, 0f);
            Vector3 normal = new Vector3(-direction.y, direction.x, 0f);

            // 斬撃の光(振った方向へ抜ける短い帯)
            float slash = Mathf.Pow(Mathf.Clamp01(1f - burst.age / SlashLifetime), 2f) * gain;
            if (slash > .001f)
            {
                Vector3 center = burst.position + direction * (burst.age * 1.5f * size);
                float length = (1.16f + burst.age * 2f) * size;
                Streak(center, direction, length, .11f * size, WithAlpha(burst.color, slash * .4f));
                Streak(center, direction, length * .93f, .032f * size,
                    WithAlpha(Color.Lerp(burst.color, Color.white, .9f), slash * .96f));
            }

            // 衝撃波リング。Perfect は虹色(回転)の二重で2本目が少し遅れて広がる、Great/Good はノーツ色を白へ寄せた一重。
            for (int r = 0; r < style.rings; r++)
            {
                float lag = r * .06f;
                if (burst.age < lag) continue;
                float tr = Mathf.Clamp01((burst.age - lag) / (BurstLifetime - lag));
                float easedR = 1f - (1f - tr) * (1f - tr);
                float radius = size * (.14f + easedR * 1.15f);
                float width = size * (.16f * (1f - tr * .6f) + .02f) * (r == 0 ? 1f : .7f);
                float alpha = Mathf.Pow(1f - tr, .9f) * gain * (r == 0 ? 1f : .7f);
                if (alpha < .002f) continue;
                for (int s = 0; s < RingSegments; s++)
                {
                    float a0 = s / (float)RingSegments, a1 = (s + 1) / (float)RingSegments;
                    Color tint = style.rainbow
                        ? Rainbow(a0 + burst.hue + r * .5f, burst.age)
                        : Color.Lerp(burst.color, Color.white, .3f + r * .4f);
                    RingSegment(burst.position, a0 * Mathf.PI * 2f, a1 * Mathf.PI * 2f, radius, width, WithAlpha(tint, alpha));
                }
            }

            // 光片(Perfect のみ): 回転しながら放射状に離れ、先端が細くなる。
            for (int i = 0; i < style.shards; i++)
            {
                float angle = i * (Mathf.PI * 2f / ShardCount) + burst.age * 1.6f + burst.hue * Mathf.PI * 2f;
                Vector3 outward = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                float distance = size * (.10f + eased * .9f);
                float length = size * (.30f + t * .7f);
                Vector3 center = burst.position + outward * (distance + length * .5f);
                Color tint = style.rainbow ? Rainbow((float)i / ShardCount + burst.hue + .5f, burst.age) : burst.color;
                Taper(center, outward, length, size * .12f * (1f - t * .4f), size * .015f,
                    WithAlpha(Color.Lerp(tint, Color.white, .25f), Mathf.Pow(life, 1.2f) * gain * .95f));
            }

            // 中心の白いフラッシュ(Perfect のみ): 十字が一瞬広がって消える。
            float flashT = Mathf.Clamp01(burst.age / FlashLifetime);
            if (style.flash && flashT < 1f)
            {
                float flashLength = size * (.9f + flashT * .5f), flashWidth = size * .3f * (1f - flashT * .5f);
                Color white = WithAlpha(Color.white, (1f - flashT) * (1f - flashT) * gain * .85f);
                Streak(burst.position, direction, flashLength, flashWidth, white);
                Streak(burst.position, normal, flashLength * .8f, flashWidth, white);
            }

            // 火花: 主に振った方向へ飛ばし、少数だけ後方へ散らす。細い白い芯は短く減衰する。
            int forward = Mathf.Max(1, style.sparks * 3 / 4);
            for (int i = 0; i < style.sparks; i++)
            {
                float side = (i & 1) == 0 ? -1f : 1f;
                float spread = .18f + (i % 3) * .3f;
                Vector3 outward = (direction * (i < forward ? 1f : -.6f) + normal * (side * spread)).normalized;
                float speed = 2.6f + (i % 4) * .65f;
                float travel = (.08f + burst.age * speed) * size;
                Vector3 position = burst.position + outward * travel + Vector3.down * (burst.age * burst.age * 3f);
                Streak(position, (outward + Vector3.down * burst.age).normalized,
                    (.10f + life * (.22f + i % 3 * .06f)) * size, (.016f + (i % 2) * .006f) * size,
                    WithAlpha(Color.Lerp(burst.color, Color.white, .5f + life * .35f), life * life * gain * .9f));
            }
        }
        mesh.Clear();
        mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0, true);
        meshRenderer.enabled = vertices.Count > 0;
    }

    // 虹色: 位置と時間で色相を回す。投影で沈まないよう彩度は少し抑え、明度は最大。
    static Color Rainbow(float t, float age)
    {
        return Color.HSVToRGB(Mathf.Repeat(t + age * 1.6f, 1f), .8f, 1f);
    }

    void Streak(Vector3 center, Vector3 direction, float length, float width, Color tint)
    {
        Taper(center, direction, length, width, width, tint);
    }

    // 先端へ細くなる帯。widthNear == widthFar なら従来の Streak と同じ矩形。
    void Taper(Vector3 center, Vector3 direction, float length, float widthNear, float widthFar, Color tint)
    {
        Vector3 half = direction * (length * .5f);
        Vector3 side = new Vector3(-direction.y, direction.x, 0f);
        int first = vertices.Count;
        // ワールド座標をこのメッシュのローカルへ戻し、Spawnerの位置/回転/拡大に依存させない。
        vertices.Add(transform.InverseTransformPoint(center - half - side * (widthNear * .5f)));
        vertices.Add(transform.InverseTransformPoint(center + half - side * (widthFar * .5f)));
        vertices.Add(transform.InverseTransformPoint(center + half + side * (widthFar * .5f)));
        vertices.Add(transform.InverseTransformPoint(center - half + side * (widthNear * .5f)));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
        uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
        AddQuad(first);
    }

    // リングの一区画。uv.x を 0.5 に固定して区画の継ぎ目を薄くせず(数珠状に見えない)、uv.y で内外の縁だけ柔らかくする。
    void RingSegment(Vector3 center, float a0, float a1, float radius, float width, Color tint)
    {
        Vector3 d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f), d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f);
        float inner = Mathf.Max(0f, radius - width * .5f), outer = radius + width * .5f;
        int first = vertices.Count;
        vertices.Add(transform.InverseTransformPoint(center + d0 * inner));
        vertices.Add(transform.InverseTransformPoint(center + d1 * inner));
        vertices.Add(transform.InverseTransformPoint(center + d1 * outer));
        vertices.Add(transform.InverseTransformPoint(center + d0 * outer));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(new Vector2(.5f, 0)); uvs.Add(new Vector2(.5f, 0));
        uvs.Add(new Vector2(.5f, 1)); uvs.Add(new Vector2(.5f, 1));
        AddQuad(first);
    }

    void AddQuad(int first)
    {
        triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
        triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
    }

    public void ClearEffects()
    {
        for (int i = 0; i < bursts.Length; i++) bursts[i].active = false;
        ActiveCount = 0;
        if (mesh != null) mesh.Clear();
        if (meshRenderer != null) meshRenderer.enabled = false;
    }

    public void ResetState()
    {
        foreach (var note in tracked)
        {
            if (note == null) continue;
            note.OnRetired -= Untrack;
            note.OnJudged -= HandleCut;
            note.OnMiss -= HandleMiss;
        }
        tracked.Clear();
        ClearEffects();
    }

    void OnDisable() { ClearEffects(); }
    void OnDestroy()
    {
        ResetState();
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }

    static Color WithAlpha(Color tint, float alpha) { tint.a = alpha; return tint; }
    static bool Finite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
