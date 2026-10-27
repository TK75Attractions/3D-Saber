using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 判定ゲートのまわりの手応え(派手さカタログ 2026-10-20)。ゲートの子の一枚のメッシュに、ゲートの外とゲートの辺だけ描く。
//   斬1 Perfect と Great の斬撃は、振った向きの延長に光の筋を飛ばし、ゲートの外に出た所だけ見せる(0.34秒)。
//   斬2 金ノーツを Perfect・Great で切ったら、切った所に金の星形の光(光片5つ、0.35秒)。
//   斬3 左右同時の X 斬り: ゲートの左の辺を青、右の辺を赤、上下を白で0.4秒光らせ(カウントインの「1」と同じ部品)、
//       2つの切った位置の真ん中に短い X(0.2秒)。
//   斬5 ロングを切るたびにゲートの左右の辺が下から満ちていき、最後の一振りで枠がはじける(外へ広がる枠)。
//   波1 コンボの節目で、ゲートの外を輪が広がる(0.7秒、100 と 300 は二重)。ゲートの内側には描かない。
// 独自の Update は持たず、GamePlayManager が Tick する。ノーツの判定・点数・音は変えない。成功ごとの物体は作らない。
// 演出控えめ(LOW)では光の筋と X を出さず、ほかは弱める。投影モードでは明るさを抑える。
public sealed partial class GateAccents : MonoBehaviour
{
    public const float StreakSeconds = .34f, StreakSpeed = 24f, StreakLength = 2.8f;
    public const float StarSeconds = .35f, CrossSeconds = .4f, CrossMarkSeconds = .2f;
    public const float BurstSeconds = .45f, MilestoneSeconds = .7f, FillFallSeconds = .2f;
    // 輪の半径(ゲートの面のワールド単位)。画面でおよそ 300→1000px。
    public const float RingFrom = 2.2f, RingTo = 7.5f;
    // ゲートの筐体の分だけ、外側も「内側」として扱う(ゲートの線に重ねない)。
    public const float OutsideMargin = .3f;
    // 演出控えめ(LOW)で残す光の強さ。
    public const float LowGain = .35f;
    const int MaxStreaks = 12, MaxStars = 6, MaxRings = 4, RingSegments = 96, VertexBudget = 8000;
    const float Z = -.12f;

    struct Streak { public bool active; public Vector2 origin, direction; public float age; public Color color; }
    struct Star { public bool active; public Vector2 center; public float age; }
    struct Ring { public bool active, twin; public float age; public MilestoneChoice choice; }
    double lastRingTime = double.NaN;

    readonly Streak[] streaks = new Streak[MaxStreaks];
    readonly Star[] stars = new Star[MaxStars];
    readonly Ring[] rings = new Ring[MaxRings];
    readonly Dictionary<CuttableNote, (float progress, Color color)> longs = new Dictionary<CuttableNote, (float, Color)>();
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly List<CuttableNote> untrack = new List<CuttableNote>();
    readonly List<Vector3> vertices = new List<Vector3>(VertexBudget);
    readonly List<Color> colors = new List<Color>(VertexBudget);
    readonly List<Vector3> uvs = new List<Vector3>(VertexBudget);
    readonly List<int> indices = new List<int>(VertexBudget * 3 / 2);

    NoteSpawner spawner;
    ScoreManager score;
    GameplayFeedbackPresenter feedback;
    Transform gate;
    float halfWidth, halfHeight;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    CountInGateCue edgeCue;
    float fillShown;
    Color fillColor = Color.white;
    float burstAge = -1, crossAge = -1, crossMarkAge = -1;
    Color burstColor = Color.white;
    Vector2 crossCenter;
    bool drewLastFrame;

    public int StreakCount { get; private set; }
    public int StarCount { get; private set; }
    public int CrossCount { get; private set; }
    public int RingCount { get; private set; }
    public int BurstCount { get; private set; }
    public float FillLevel => fillShown;
    public int ActiveStreaks { get { int n = 0; foreach (var s in streaks) if (s.active) n++; return n; } }
    public int ActiveRings { get { int n = 0; foreach (var r in rings) if (r.active) n++; return n; } }
    public bool CrossLit => crossAge >= 0;
    public Mesh Mesh => mesh;

    public static GateAccents Create(NoteSpawner spawner, ScoreManager score, GameplayFeedbackPresenter feedback)
    {
        var gateObject = GameObject.Find("JudgeGate");
        var frame = gateObject != null ? gateObject.GetComponent<JudgeGateFrame>() : null;
        if (frame == null || frame.HalfWidth <= 0 || frame.HalfHeight <= 0) return null;
        var go = new GameObject("GateAccents");
        go.transform.SetParent(frame.transform, false);
        var accents = go.AddComponent<GateAccents>();
        accents.Setup(frame, spawner, score, feedback);
        return accents;
    }

    void Setup(JudgeGateFrame frame, NoteSpawner owner, ScoreManager scoring, GameplayFeedbackPresenter presenter)
    {
        gate = frame.transform; halfWidth = frame.HalfWidth; halfHeight = frame.HalfHeight;
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader != null)
        {
            mesh = new Mesh { name = "GateAccents", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            material = new Material(shader) { name = "GateAccents", hideFlags = HideFlags.DontSave };
            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off; meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off; meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            meshRenderer.enabled = false;
        }
        edgeCue = new CountInGateCue(gate, null);
        spawner = owner; score = scoring; feedback = presenter;
        if (spawner != null) { spawner.OnNoteSpawned += Track; spawner.OnChartReset += ResetState; }
        if (score != null) score.OnCrossCut += CrossCut;
        if (feedback != null) feedback.OnMilestone += Milestone;
    }

    // ---- 入力 ----

    void Track(CuttableNote note)
    {
        // Q の見えないノーツは見張らない(斬撃の筋・金の星・ロングの満ち引きを出さない。試験用紙が薄れる間や終わった直後に漏らさない)。
        // 採点と音は ScoreManager・効果音の側がそのまま扱う。
        if (note == null || note.IsHidden || !tracked.Add(note)) return;
        note.OnJudged += Judged;
        note.OnPartialCut += PartialCut;
        note.OnMiss += Missed;
        note.OnRetired += Missed;
    }

    void Untrack(CuttableNote note)
    {
        if (ReferenceEquals(note, null)) return;
        note.OnJudged -= Judged;
        note.OnPartialCut -= PartialCut;
        note.OnMiss -= Missed;
        note.OnRetired -= Missed;
        tracked.Remove(note);
        longs.Remove(note);
    }

    void Missed(CuttableNote note) => Untrack(note);

    static Color NoteColor(CuttableNote note)
    {
        if (note.IsGold) return UISkinPalette.NoteGold;
        var hand = note.LastCutterHand != SaberHand.Any ? note.LastCutterHand : note.RequiredHand;
        return hand == SaberHand.Left ? UISkinPalette.LogoBlue : hand == SaberHand.Right ? UISkinPalette.LogoRed : Color.white;
    }

    void PartialCut(CuttableNote note, int cutIndex, int total)
    {
        if (note == null || total <= 1 || cutIndex >= total - 1) return;
        longs[note] = (Mathf.Clamp01((cutIndex + 1) / (float)total), NoteColor(note));
    }

    void Judged(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        if (note == null) return;
        // 見えないノーツの判定では光を出さない(見張りの外し忘れの保険)。
        if (note.IsHidden) { Untrack(note); return; }
        bool cut = note.IsCut && !note.IsMissed;
        if (cut && note.RequiredCutCount > 1 && JudgmentTierHelper.StageReactionGain(tier) > 0)
        {
            burstAge = 0; burstColor = NoteColor(note); BurstCount++;
        }
        if (cut && GoldNoteSfx.IsCritical(tier))
        {
            Vector3 local = gate.InverseTransformPoint(point);
            Vector3 direction = gate.InverseTransformDirection(new Vector3(velocity.x, velocity.y, 0));
            Vector2 d = new Vector2(direction.x, direction.y);
            AddStreak(local, d.sqrMagnitude > .0001f ? d.normalized : Vector2.right, Color.Lerp(NoteColor(note), Color.white, .35f));
            if (note.IsGold) AddStar(local);
        }
        Untrack(note);
    }

    void AddStreak(Vector2 origin, Vector2 direction, Color color)
    {
        int slot = Slot(streaks, s => s.active, s => s.age);
        streaks[slot] = new Streak { active = true, origin = origin, direction = direction, color = color };
        StreakCount++;
    }

    void AddStar(Vector2 center)
    {
        int slot = Slot(stars, s => s.active, s => s.age);
        stars[slot] = new Star { active = true, center = center };
        StarCount++;
    }

    static int Slot<T>(T[] items, System.Func<T, bool> active, System.Func<T, float> age)
    {
        int oldest = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (!active(items[i])) return i;
            if (age(items[i]) > age(items[oldest])) oldest = i;
        }
        return oldest;
    }

    void CrossCut(Vector3 a, Vector3 b)
    {
        crossAge = 0; crossMarkAge = 0; CrossCount++;
        Vector3 local = gate.InverseTransformPoint((a + b) * .5f);
        crossCenter = new Vector2(local.x, local.y);
    }

    public void Milestone(int combo)
    {
        int slot = Slot(rings, r => r.active, r => r.age);
        var choice = feedback != null ? feedback.LastMilestoneChoice : default;
        rings[slot] = new Ring { active = true, twin = GameplayFeedbackPresenter.IsBigMilestone(combo) || choice.Jackpot, choice = choice };
        RingCount++;
    }

    // ---- 時計 ----

    public void Tick(float dt)
    {
        if (!isActiveAndEnabled) return;
        dt = Mathf.Clamp(float.IsNaN(dt) ? 0 : dt, 0, .1f);
        feedback?.SyncMilestonePause();
        float ringDt = score != null && score.songPlayer != null && Application.isPlaying
            ? DopamineFlourish.SongDelta(score.songPlayer, ref lastRingTime) : dt;
        for (int i = 0; i < streaks.Length; i++)
            if (streaks[i].active && (streaks[i].age += dt) >= StreakSeconds) streaks[i].active = false;
        for (int i = 0; i < stars.Length; i++)
            if (stars[i].active && (stars[i].age += dt) >= StarSeconds) stars[i].active = false;
        for (int i = 0; i < rings.Length; i++)
            if (rings[i].active && ((rings[i].choice.Enabled && DisplaySettings.ReducedEffects) ||
                (rings[i].age += ringDt) >= MilestoneSeconds + (rings[i].twin ? .08f : 0))) rings[i].active = false;
        if (burstAge >= 0 && (burstAge += dt) >= BurstSeconds) burstAge = -1;
        if (crossMarkAge >= 0 && (crossMarkAge += dt) >= CrossMarkSeconds) crossMarkAge = -1;
        TickEdges(dt);
        TickFill(dt);
        Draw();
    }

    // X 斬りのゲートの辺。0.2 秒で光らせたまま、残りの 0.2 秒で元の材質へ戻す。
    void TickEdges(float dt)
    {
        if (crossAge < 0 || edgeCue == null || !edgeCue.HasGate) return;
        crossAge += dt;
        if (crossAge >= CrossSeconds) { edgeCue.Restore(); crossAge = -1; return; }
        double restore = crossAge < CrossSeconds * .5f ? -1 : CountInGateCue.RestoreFrom + (crossAge - CrossSeconds * .5f) / (CrossSeconds * .5f) * CountInGateCue.RestoreSeconds;
        // 演出控えめでは、ほかの光と同じだけ弱める(辺だけ通常の強さで光っていた。Codex の点検で 2026-10-22 に追加)。
        edgeCue.Apply(2, crossAge, 0, restore, Color.white, DisplaySettings.ReducedEffects ? LowGain : 1f);
    }

    // ロングの満ち方。上がるときはすぐ、下がるとき(取りこぼし・最後の一振り)は短くフェードする。
    void TickFill(float dt)
    {
        float target = 0;
        untrack.Clear();
        foreach (var item in longs)
        {
            if (item.Key == null || !item.Key.isActiveAndEnabled || item.Key.IsFinalized) { untrack.Add(item.Key); continue; }
            if (item.Value.progress > target) { target = item.Value.progress; fillColor = item.Value.color; }
        }
        foreach (var note in untrack) longs.Remove(note);
        fillShown = target >= fillShown ? Mathf.MoveTowards(fillShown, target, dt * 6f) : Mathf.MoveTowards(fillShown, target, dt / FillFallSeconds);
    }

    // ---- 描画 ----

    // 始点から向きに沿って、ゲート(と筐体)の外へ出るまでの距離。外から始まるときは 0。純関数。
    public static float ExitDistance(Vector2 origin, Vector2 direction, float halfWidth, float halfHeight)
    {
        if (Mathf.Abs(origin.x) >= halfWidth || Mathf.Abs(origin.y) >= halfHeight) return 0;
        float t = float.PositiveInfinity;
        if (direction.x > 1e-4f) t = Mathf.Min(t, (halfWidth - origin.x) / direction.x);
        if (direction.x < -1e-4f) t = Mathf.Min(t, (-halfWidth - origin.x) / direction.x);
        if (direction.y > 1e-4f) t = Mathf.Min(t, (halfHeight - origin.y) / direction.y);
        if (direction.y < -1e-4f) t = Mathf.Min(t, (-halfHeight - origin.y) / direction.y);
        return float.IsInfinity(t) ? 0 : Mathf.Max(0, t);
    }

    public bool IsInsideGate(Vector2 local) => Mathf.Abs(local.x) < halfWidth + OutsideMargin && Mathf.Abs(local.y) < halfHeight + OutsideMargin;

    void Draw()
    {
        if (mesh == null) return;
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        bool low = DisplaySettings.ReducedEffects;
        float gain = (DisplaySettings.ProjectorMode ? .68f : 1f) * (low ? LowGain : 1f);
        float outerW = halfWidth + OutsideMargin, outerH = halfHeight + OutsideMargin;

        if (!low)
            foreach (var s in streaks)
            {
                if (!s.active) continue;
                float p = s.age / StreakSeconds;
                float exit = ExitDistance(s.origin, s.direction, outerW, outerH);
                float head = exit + StreakSpeed * s.age, tail = Mathf.Max(exit, head - StreakLength * Mathf.Lerp(.35f, 1f, Mathf.Clamp01(s.age / .08f)));
                if (head - tail < .05f) continue;
                float alpha = Mathf.Pow(1 - p, 1.4f) * gain;
                Band(s.origin + s.direction * tail, s.origin + s.direction * head, .42f, WithAlpha(s.color, .3f * alpha), true);
                Band(s.origin + s.direction * tail, s.origin + s.direction * head, .11f, WithAlpha(Color.Lerp(s.color, Color.white, .6f), alpha), true);
            }

        foreach (var s in stars)
        {
            if (!s.active) continue;
            float p = s.age / StarSeconds, ease = 1 - (1 - p) * (1 - p);
            float alpha = Mathf.Pow(1 - p, 1.2f) * gain;
            for (int k = 0; k < 5; k++)
            {
                float angle = Mathf.PI * .5f + k * Mathf.PI * 2 / 5 + ease * .35f;
                Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Band(s.center + dir * (.18f + ease * .35f), s.center + dir * (.55f + ease * 1.1f), .16f, WithAlpha(UISkinPalette.NoteGold, alpha), true);
            }
            DrawRing(s.center, .3f + ease * 1.2f, .07f, WithAlpha(Color.Lerp(UISkinPalette.NoteGold, Color.white, .4f), alpha * .7f), false);
        }

        foreach (var r in rings)
        {
            if (!r.active || r.choice.Enabled && (low || DopamineFlourish.Veiled(spawner))) continue;
            for (int twin = 0; twin < (r.twin ? 2 : 1); twin++)
            {
                float age = r.age - twin * .08f;
                if (age < 0) continue;
                float p = Mathf.Clamp01(age / MilestoneSeconds), ease = 1 - (1 - p) * (1 - p) * (1 - p);
                float radius = Mathf.Lerp(RingFrom, RingTo, ease) * (twin == 0 ? 1 : .86f);
                Color tint = r.choice.Enabled ? MilestoneTint(r.choice.Color) : Color.Lerp(SongSelectSkin.Gold, Color.white, .35f);
                float alpha = Mathf.Pow(1 - p, 1.2f) * .85f * (r.choice.Enabled ? 1f : gain);
                if (r.choice.Enabled) DrawMilestoneShape(r.choice.Shape, radius, .16f * (1 - p * .5f), WithAlpha(tint, alpha));
                else DrawRing(Vector2.zero, radius, .16f * (1 - p * .5f), WithAlpha(tint, alpha), true);
            }
        }

        if (fillShown > .002f)
        {
            // 左右の辺を下から満たす。満ちるほど白く明るく。
            float top = -halfHeight + 2 * halfHeight * fillShown;
            Color tint = WithAlpha(Color.Lerp(fillColor, Color.white, .25f + .45f * fillShown), (.45f + .5f * fillShown) * gain);
            // 帯の内側の縁をゲートの辺にそろえ、ノーツの通り道へ光をはみ出させない。
            for (int side = -1; side <= 1; side += 2)
            {
                float glowX = side * (halfWidth + .16f), coreX = side * (halfWidth + .035f);
                Band(new Vector2(glowX, -halfHeight), new Vector2(glowX, top), .32f, WithAlpha(tint, tint.a * .35f), false);
                Band(new Vector2(coreX, -halfHeight), new Vector2(coreX, top), .07f, tint, false);
            }
        }

        if (burstAge >= 0)
        {
            float p = burstAge / BurstSeconds, ease = 1 - (1 - p) * (1 - p) * (1 - p);
            float grow = 1 + .45f * ease, w = halfWidth * grow + OutsideMargin, h = halfHeight * grow + OutsideMargin;
            Color tint = WithAlpha(Color.Lerp(Color.white, burstColor, p), Mathf.Pow(1 - p, 2) * .9f * gain);
            float thick = .09f * (1 + p);
            Band(new Vector2(-w, h), new Vector2(w, h), thick, tint, false);
            Band(new Vector2(-w, -h), new Vector2(w, -h), thick, tint, false);
            Band(new Vector2(-w, -h), new Vector2(-w, h), thick, tint, false);
            Band(new Vector2(w, -h), new Vector2(w, h), thick, tint, false);
        }

        if (crossMarkAge >= 0 && !low)
        {
            float p = crossMarkAge / CrossMarkSeconds, size = 1.1f * Mathf.Lerp(.6f, 1f, Mathf.Clamp01(p * 3));
            float alpha = (1 - p * p) * gain;
            Vector2 a = new Vector2(-1, 1).normalized * size, b = new Vector2(1, 1).normalized * size;
            Band(crossCenter + a, crossCenter - a, .2f, WithAlpha(UISkinPalette.LogoBlue, alpha), true);
            Band(crossCenter + b, crossCenter - b, .2f, WithAlpha(UISkinPalette.LogoRed, alpha), true);
        }

        bool draw = vertices.Count > 0;
        if (draw || drewLastFrame)
        {
            mesh.Clear();
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0, true);
        }
        drewLastFrame = draw;
        meshRenderer.enabled = draw;
    }

    // 中心 center・半径 radius の輪。outsideOnly のときはゲートの内側に入る区画を描かない。
    void DrawRing(Vector2 center, float radius, float width, Color tint, bool outsideOnly)
    {
        if (tint.a < .002f) return;
        for (int s = 0; s < RingSegments; s++)
        {
            float a0 = s * Mathf.PI * 2 / RingSegments, a1 = (s + 1) * Mathf.PI * 2 / RingSegments;
            Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
            if (outsideOnly && IsInsideGate(center + (d0 + d1) * .5f * radius)) continue;
            Quad(center + d0 * (radius - width * .5f), center + d1 * (radius - width * .5f),
                center + d1 * (radius + width * .5f), center + d0 * (radius + width * .5f), tint, .5f, .5f);
        }
    }

    // a→b の帯。taper のときは両端を柔らかくする(uv.x)。側面は常に柔らかい(uv.y)。
    void Band(Vector2 a, Vector2 b, float width, Color tint, bool taper)
    {
        if (tint.a < .002f) return;
        Vector2 along = b - a;
        if (along.sqrMagnitude < 1e-6f) return;
        Vector2 n = new Vector2(-along.y, along.x).normalized * width * .5f;
        float u0 = taper ? 0 : .5f, u1 = taper ? 1 : .5f;
        int start = vertices.Count;
        if (start + 4 > VertexBudget) return;
        vertices.Add(new Vector3(a.x - n.x, a.y - n.y, Z)); vertices.Add(new Vector3(b.x - n.x, b.y - n.y, Z));
        vertices.Add(new Vector3(b.x + n.x, b.y + n.y, Z)); vertices.Add(new Vector3(a.x + n.x, a.y + n.y, Z));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(new Vector3(u0, 0, 0)); uvs.Add(new Vector3(u1, 0, 0)); uvs.Add(new Vector3(u1, 1, 0)); uvs.Add(new Vector3(u0, 1, 0));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    // 輪の一区画(内側→外側で uv.y が 0→1)。
    void Quad(Vector2 innerA, Vector2 innerB, Vector2 outerB, Vector2 outerA, Color tint, float u0, float u1)
    {
        int start = vertices.Count;
        if (start + 4 > VertexBudget) return;
        vertices.Add(new Vector3(innerA.x, innerA.y, Z)); vertices.Add(new Vector3(innerB.x, innerB.y, Z));
        vertices.Add(new Vector3(outerB.x, outerB.y, Z)); vertices.Add(new Vector3(outerA.x, outerA.y, Z));
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(new Vector3(u0, 0, 0)); uvs.Add(new Vector3(u1, 0, 0)); uvs.Add(new Vector3(u1, 1, 0)); uvs.Add(new Vector3(u0, 1, 0));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    static Color WithAlpha(Color c, float a) { c.a = Mathf.Clamp01(a); return c; }

    // 曲の停止・譜面の差し替え・離脱で、描いている物と辺の色を元に戻す。
    public void ResetState()
    {
        foreach (var note in new List<CuttableNote>(tracked)) Untrack(note);
        tracked.Clear(); longs.Clear();
        System.Array.Clear(streaks, 0, streaks.Length);
        System.Array.Clear(stars, 0, stars.Length);
        System.Array.Clear(rings, 0, rings.Length);
        burstAge = crossMarkAge = -1; fillShown = 0;
        if (crossAge >= 0 && edgeCue != null) edgeCue.Restore();
        crossAge = -1;
        if (mesh != null) mesh.Clear();
        if (meshRenderer != null) meshRenderer.enabled = false;
        drewLastFrame = false;
    }

    void OnDisable() { ResetState(); }

    void OnDestroy()
    {
        ResetState();
        if (spawner != null) { spawner.OnNoteSpawned -= Track; spawner.OnChartReset -= ResetState; }
        if (score != null) score.OnCrossCut -= CrossCut;
        if (feedback != null) feedback.OnMilestone -= Milestone;
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }
}
