using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 全背景共通の舞台反応。Perfectの光をノーツの横位置に対応する床列へ送る。
// 独自Updateは持たず、GamePlayManagerの曲時計でのみ進む。
[ExecuteAlways]
public sealed class StageReactiveEffects : MonoBehaviour
{
    public const int MaxWaves = 10;
    public const int VertexBudget = 14000;
    public const float CorridorHalfWidth = 5.8f;
    enum WaveKind { Cut, Pair, Release }
    struct Wave
    {
        public bool active;
        public WaveKind kind;
        public int hand, laneMask;
        public double chartTime;
        public float age, gain;
        public Color color;
    }
    readonly Wave[] waves = new Wave[MaxWaves];
    // 同時ノーツを一群として保持し、通知順ではなく譜面上の隣接で連打を判定する。
    sealed class WeaveGroup
    {
        public WeaveGroup previous;
        public double time;
        public int lane, pending, count, epoch;
        public bool perfect = true, single = true;
    }
    struct Stitch { public bool active; public int lane; public float age; public Color color; }
    public const int StitchesPerLane = 8;
    public const float StitchLifetime = .5f;
    readonly Stitch[] stitches = new Stitch[4 * StitchesPerLane];
    readonly WeaveGroup[] lastGroups = new WeaveGroup[4];
    readonly Dictionary<CuttableNote, WeaveGroup> weaveNotes = new Dictionary<CuttableNote, WeaveGroup>();
    readonly double[] latestResolved = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
    int weaveEpoch;
    StageThemeResponse themeResponse;
    ScenicStageWorld meteorWorld;
    readonly HashSet<CuttableNote> tracked = new HashSet<CuttableNote>();
    readonly List<CuttableNote> expired = new List<CuttableNote>();
    readonly List<Vector3> vertices = new List<Vector3>(VertexBudget);
    readonly List<Color> colors = new List<Color>(VertexBudget);
    readonly List<Vector2> uvs = new List<Vector2>(VertexBudget);
    readonly List<int> indices = new List<int>(VertexBudget * 3 / 2);
    NoteSpawner owner;
    StagePerformanceTimeline timeline;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    float floor;
    bool hasTime;
    StageTheme theme;
    public double LastTickSeconds { get; private set; }
    public StagePerformanceTimeline.Presentation Presentation { get; private set; }
    public int ActiveWaveCount { get; private set; }
    public int PairCount { get; private set; }
    public int ReleaseCount { get; private set; }
    public int ActiveFloorLaneMask { get; private set; }
    public int ActiveStitchCount { get; private set; }
    public int ActiveWeaveLaneMask { get; private set; }

    // 譜面エディターの横8列（-2.5～2.5）を左から2列ずつまとめる。
    // 色や担当ハンドではなく、coordScale適用後の実際の配置で決める。
    public static int FloorLaneForX(float worldX)
    {
        if (!Finite(worldX)) return -1;
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(-2.5f, 2.5f, worldX) * 7), 0, 7) / 2;
    }
    public static float FloorLaneCenter(int lane) => -4.5f + Mathf.Clamp(lane, 0, 3) * 3f;

    public static StageReactiveEffects Create(FloorRenderer stage, NoteSpawner spawner, StagePerformanceTimeline song)
    {
        var go = new GameObject("StageReactiveEffects");
        go.transform.SetParent(stage.transform, false);
        var effect = go.AddComponent<StageReactiveEffects>();
        effect.floor = stage.floorY;
        effect.theme = stage.ActiveTheme;
        effect.Build();
        effect.themeResponse = StageThemeResponse.Create(effect.transform, effect.theme, effect.floor);
        if (effect.theme == StageTheme.AstralOrbit)
            effect.meteorWorld = stage.GetComponentInChildren<ScenicStageWorld>();
        effect.Bind(spawner, song);
        return effect;
    }

    void Build()
    {
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null) return;
        mesh = new Mesh { name = "StageReactionMesh", hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        material = new Material(shader) { name = "StageReactionLight", hideFlags = HideFlags.DontSave };
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        meshRenderer.enabled = false;
    }

    public void Bind(NoteSpawner spawner, StagePerformanceTimeline song)
    {
        if (owner != null)
        {
            owner.OnNoteSpawned -= Track;
            owner.OnChartReset -= ResetState;
        }
        ResetState();
        owner = spawner;
        timeline = song ?? new StagePerformanceTimeline();
        if (owner != null)
        {
            owner.OnNoteSpawned += Track;
            owner.OnChartReset += ResetState;
        }
    }

    void Track(CuttableNote note)
    {
        if (note == null || !tracked.Add(note)) return;
        note.OnRetired += Retired;
        note.OnJudged += Cut;
        note.OnMiss += Miss;
        int lane = FloorLaneForX(note.transform.position.x);
        if (lane < 0 || !Finite(note.HitTime)) return;
        var group = lastGroups[lane];
        if (group == null || Math.Abs(group.time - note.HitTime) > NoteSpawner.SimultaneousEpsilonSeconds)
        {
            group = new WeaveGroup { previous = group, time = note.HitTime, lane = lane, epoch = weaveEpoch };
            lastGroups[lane] = group;
        }
        group.count++; group.pending++;
        group.single &= note.RequiredCutCount == 1;
        weaveNotes.Add(note, group);
    }

    void Retired(CuttableNote note) { ResolveWeave(note, false); Untrack(note); }

    void Untrack(CuttableNote note)
    {
        if (!ReferenceEquals(note, null))
        {
            note.OnRetired -= Retired;
            note.OnJudged -= Cut;
            note.OnMiss -= Miss;
        }
        tracked.Remove(note);
    }

    bool CanReact => isActiveAndEnabled && owner != null && owner.isActiveAndEnabled;
    void Miss(CuttableNote note) { ResolveWeave(note, false); Untrack(note); }

    void Cut(CuttableNote note, JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        // 降格後の確定判定だけを使う。ロング途中・時間切れは祝福しない。
        bool perfect = CanReact && tier == JudgmentTier.Perfect && note != null && note.IsCut && !note.IsMissed &&
            Finite(note.HitTime) && FloorLaneForX(note.transform.position.x) >= 0;
        ResolveWeave(note, perfect);
        Untrack(note);
        if (!perfect) return;
        if (themeResponse != null) themeResponse.OnPerfect(FloorLaneForX(note.transform.position.x));
        if (meteorWorld != null) meteorWorld.OnMeteorPerfect(FloorLaneForX(note.transform.position.x));
        if (note.RequiredCutCount > 1)
        {
            AddWave(note, WaveKind.Release, 1);
            ReleaseCount++;
            return;
        }
        int hand = Hand(note);
        for (int i = 0; i < waves.Length; i++)
        {
            // 譜面上同時で、別の手が短い時間内に切った組だけを一つの左右反応へ変える。
            if (!waves[i].active || waves[i].kind != WaveKind.Cut || waves[i].gain < .9f ||
                waves[i].hand == 0 || hand == 0 || waves[i].hand == hand || waves[i].age > .16f ||
                Math.Abs(waves[i].chartTime - note.HitTime) > NoteSpawner.SimultaneousEpsilonSeconds) continue;
            waves[i].kind = WaveKind.Pair;
            waves[i].age = 0;
            waves[i].laneMask |= 1 << FloorLaneForX(note.transform.position.x);
            waves[i].color = new Color(.68f, .88f, 1.35f);
            PairCount++;
            CountWaves();
            return;
        }
        AddWave(note, WaveKind.Cut, 1);
    }

    void ResolveWeave(CuttableNote note, bool perfect)
    {
        if (ReferenceEquals(note, null) || !weaveNotes.TryGetValue(note, out var group)) return;
        weaveNotes.Remove(note);
        group.perfect &= perfect;
        if (--group.pending != 0) return;
        var previous = group.previous;
        double gap = previous == null ? double.PositiveInfinity : group.time - previous.time;
        if (group.epoch == weaveEpoch && group.perfect && group.single && group.count == 1 &&
            previous != null && previous.epoch == weaveEpoch && previous.pending == 0 &&
            previous.perfect && previous.single && previous.count == 1 &&
            group.time >= latestResolved[group.lane] && gap > NoteSpawner.SimultaneousEpsilonSeconds && gap <= .220001)
            AddStitch(group.lane, note.IsGold ? UISkinPalette.NoteGold : Hand(note) < 0 ? UISkinPalette.LogoBlue : UISkinPalette.LogoRed);
        if (group.epoch == weaveEpoch) latestResolved[group.lane] = Math.Max(latestResolved[group.lane], group.time);
        // 完了した群から前へたどらない。曲全体の履歴を保持し続けない。
        group.previous = null;
    }

    void AddStitch(int lane, Color color)
    {
        int start = lane * StitchesPerLane, slot = start;
        for (int i = start; i < start + StitchesPerLane; i++)
        {
            if (!stitches[i].active) { slot = i; break; }
            if (stitches[i].age > stitches[slot].age) slot = i;
        }
        stitches[slot] = new Stitch { active = true, lane = lane, color = color };
        CountStitches();
    }

    void CountStitches()
    {
        ActiveStitchCount = ActiveWeaveLaneMask = 0;
        foreach (var stitch in stitches) if (stitch.active)
        { ActiveStitchCount++; ActiveWeaveLaneMask |= 1 << stitch.lane; }
    }

    static int Hand(CuttableNote note)
    {
        var hand = note.LastCutterHand != SaberHand.Any ? note.LastCutterHand : note.RequiredHand;
        return hand == SaberHand.Left ? -1 : hand == SaberHand.Right ? 1 : 0;
    }

    void AddWave(CuttableNote note, WaveKind kind, float gain)
    {
        if (note == null || !Finite(note.HitTime)) return;
        int lane = FloorLaneForX(note.transform.position.x);
        if (lane < 0) return;
        int hand = Hand(note), slot = -1, oldest = 0;
        for (int i = 0; i < waves.Length; i++)
        {
            if (!waves[i].active && slot < 0) slot = i;
            if (waves[i].age > waves[oldest].age) oldest = i;
        }
        if (slot < 0) slot = oldest;
        Color tint = note.IsGold ? UISkinPalette.NoteGold : hand < 0 ? UISkinPalette.LogoBlue : UISkinPalette.LogoRed;
        waves[slot] = new Wave { active = true, kind = kind, hand = hand, laneMask = 1 << lane, chartTime = note.HitTime,
            color = tint, gain = gain, age = 0 };
        CountWaves();
    }

    void PruneNotes()
    {
        expired.Clear();
        foreach (var note in tracked)
        {
            if (note == null || note.IsFinalized || !note.gameObject.activeInHierarchy)
            { expired.Add(note); continue; }
        }
        foreach (var note in expired) { ResolveWeave(note, false); Untrack(note); }
    }

    public void Tick(double songSeconds)
    {
        if (!Finite(songSeconds) || !isActiveAndEnabled) return;
        if (owner == null || !owner.isActiveAndEnabled) { ClearVisuals(); return; }
        float delta = hasTime ? (float)(songSeconds - LastTickSeconds) : 0;
        // シーク・再実行に前区間の手応えを持ち越さない。
        if (delta < -.001f)
        {
            ClearVisuals();
            delta = 0;
        }
        hasTime = true;
        LastTickSeconds = songSeconds;
        Presentation = timeline.EvaluatePresentation(songSeconds);
        for (int i = 0; i < waves.Length; i++)
        {
            if (!waves[i].active) continue;
            waves[i].age += Mathf.Max(0, delta);
            float duration = waves[i].kind == WaveKind.Cut ? .62f : waves[i].kind == WaveKind.Pair ? .85f : 1.05f;
            if (waves[i].age >= duration) waves[i].active = false;
        }
        CountWaves();
        for (int i = 0; i < stitches.Length; i++) if (stitches[i].active)
        {
            stitches[i].age += Mathf.Max(0, delta);
            if (stitches[i].age >= StitchLifetime) stitches[i].active = false;
        }
        CountStitches();
        if (themeResponse != null) themeResponse.Tick(songSeconds);
        PruneNotes();
        Draw();
    }

    void CountWaves()
    {
        ActiveWaveCount = 0;
        ActiveFloorLaneMask = 0;
        foreach (var wave in waves) if (wave.active)
        {
            ActiveWaveCount++;
            ActiveFloorLaneMask |= wave.laneMask;
        }
    }

    Color ThemeColor()
    {
        switch (theme)
        {
            case StageTheme.AmberFoundry: case StageTheme.DesertSanctum: return new Color(1.2f, .66f, .2f);
            case StageTheme.MoonlitGarden: return new Color(.96f, .48f, 1.1f);
            case StageTheme.AstralOrbit: case StageTheme.VioletVault: return new Color(.57f, .42f, 1.3f);
            default: return new Color(.24f, .82f, 1.25f);
        }
    }

    void Draw()
    {
        if (mesh == null) return;
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        Color themeColor = ThemeColor();
        float projector = DisplaySettings.ProjectorMode ? .68f : 1;
        var state = Presentation;
        float open = state.opening;
        float prepare = state.anticipation;
        // サビの演出だけは判定・難易度に依存させず、準備から入口まで曲に同期する。
        for (int side = -1; side <= 1; side += 2)
        {
            for (int bank = 0; bank < 7; bank++)
            {
                float z = 4 + bank * 5;
                float alpha = (open * .2f + prepare * .14f) * (1 - state.hush * .85f) * projector;
                Vector3 root = new Vector3(side * 6.05f, floor + .12f, z);
                Vector3 tip = new Vector3(side * (7 + open * 6 + (1 - prepare) * 1.3f), 4.6f + open * 1.6f, z + 4 + prepare * 5);
                Beam(root, tip, .035f + open * .07f, themeColor, alpha);
                if (prepare > 0)
                {
                    float moving = Mathf.Repeat((float)LastTickSeconds * 2.5f - bank * .22f, 1);
                    float z0 = z + moving * 4;
                    Beam(new Vector3(side * 4.85f, floor + .1f, z0), new Vector3(side * 4.85f, floor + .1f, z0 + 1.2f), .16f, themeColor, prepare * .65f * (1 - state.hush) * projector, Vector3.right);
                }
            }
            if (state.impact > .001f)
            {
                float travel = (1 - Mathf.Sqrt(state.impact)) * 25;
                FloorFan(side, travel, new Color(1.2f, .82f, .38f), state.impact * projector, 1.4f);
            }
        }
        float crowded = 1 / Mathf.Sqrt(Mathf.Max(1, ActiveWaveCount * .45f));
        foreach (var wave in waves)
        {
            if (!wave.active) continue;
            float duration = wave.kind == WaveKind.Cut ? .62f : wave.kind == WaveKind.Pair ? .85f : 1.05f;
            float life = Mathf.Clamp01(1 - wave.age / duration);
            float gain = life * wave.gain * projector * crowded;
            float travel = wave.age * (wave.kind == WaveKind.Cut ? 42 : 34);
            Color tint = wave.kind == WaveKind.Release ? Color.Lerp(wave.color, new Color(1.3f, .92f, .45f), .65f) : wave.color;
            for (int lane = 0; lane < 4; lane++)
                if ((wave.laneMask & (1 << lane)) != 0)
                    FloorLaneWave(lane, travel, tint, gain, wave.kind == WaveKind.Cut ? .7f : 1.35f);
            for (int side = -1; side <= 1; side += 2)
            {
                if ((themeResponse != null && themeResponse.ReplacesSideResponse) ||
                    (meteorWorld != null && meteorWorld.MeteorResponsesReady)) continue;
                // 同時斬りでも、実際に切った床列の側だけへ展開する。
                if ((wave.laneMask & (side < 0 ? 3 : 12)) == 0) continue;
                for (int bank = 0; bank < 7; bank++)
                {
                    float local = wave.age - bank * .055f;
                    if (local < 0 || local > .28f) continue;
                    float flash = (1 - local / .28f) * gain;
                    float z = 4 + bank * 5;
                    Beam(new Vector3(side * 6.05f, floor + .18f, z), new Vector3(side * (7.1f + local * 3), 3.8f, z + 1), .1f, tint, flash);
                }
                if (wave.kind != WaveKind.Cut)
                {
                    // 弧は通路の外側だけ。ロング完走は二重の弧がほどける。
                    int arcs = wave.kind == WaveKind.Release ? 2 : 1;
                    for (int arc = 0; arc < arcs; arc++)
                        for (int segment = 0; segment < 12; segment++)
                        {
                            float a = segment / 12f, b = (segment + 1) / 12f;
                            Vector3 p = ArcPoint(side, a, wave.age, arc);
                            Vector3 q = ArcPoint(side, b, wave.age, arc);
                            Beam(p, q, .08f, tint, gain * .85f);
                        }
                }
            }
        }
        DrawWeave(projector);
        mesh.Clear(); mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs);
        mesh.SetTriangles(indices, 0, true);
        meshRenderer.enabled = vertices.Count > 0;
    }

    void DrawWeave(float projector)
    {
        float y = floor + (theme == StageTheme.ObsidianRelay ? .47f : theme == StageTheme.VioletVault ? .57f : .14f);
        foreach (var stitch in stitches)
        {
            if (!stitch.active) continue;
            // ノーツと切断片の真下から少し外へ寄せ、床列の中で縫い目を見せる。
            float x = FloorLaneCenter(stitch.lane) + (stitch.lane < 2 ? -.55f : .55f);
            float z = 1.2f + stitch.age * 18;
            float alpha = (1 - stitch.age / StitchLifetime) * projector * .85f;
            // 帯と斜めの縫い目を同じ床列に収める。後続の成功で古い節の寿命を延ばさない。
            Beam(new Vector3(x, y, z - 1.7f), new Vector3(x, y, z + .6f), .20f, stitch.color, alpha * .36f, Vector3.right);
            Beam(new Vector3(x - .48f, y, z - .3f), new Vector3(x + .48f, y, z + .3f), .10f, stitch.color, alpha, Vector3.forward);
        }
    }

    Vector3 ArcPoint(int side, float fraction, float age, int layer)
    {
        return new Vector3(side * (6 + Mathf.Sin(fraction * Mathf.PI) * (2.4f + age * 4) + layer * .35f),
            floor + .25f + fraction * 8, 7 + age * 12 + layer * 3);
    }

    void FloorFan(int side, float z, Color tint, float alpha, float width)
    {
        // 動く床板の最大上昇分より上に薄い波を置き、板の中に埋めない。
        float y = floor + (theme == StageTheme.ObsidianRelay ? .46f : theme == StageTheme.VioletVault ? .56f : .13f);
        Vector3 a = new Vector3(side * 3.35f, y, z - 1.2f);
        Vector3 b = new Vector3(side * 5.1f, y, z);
        Vector3 c = new Vector3(side * 7.7f, y, z + 1.1f);
        Beam(a, b, width * .12f, tint, alpha, Vector3.forward);
        Beam(b, c, width * .12f, tint, alpha, Vector3.forward);
        Beam(new Vector3(side * 4.75f, y, z - 3), new Vector3(side * 4.75f, y, z + 1), width * .24f, tint, alpha * .42f, Vector3.right);
    }

    void FloorLaneWave(int lane, float z, Color tint, float alpha, float width)
    {
        float y = floor + (theme == StageTheme.ObsidianRelay ? .46f : theme == StageTheme.VioletVault ? .56f : .13f);
        float x = FloorLaneCenter(lane);
        // 床4列の枠（-6,-3,0,3,6）を越えない短い山形と残光。
        Vector3 tip = new Vector3(x, y, z + .7f);
        Beam(new Vector3(x - 1.18f, y, z), tip, width * .10f, tint, alpha, Vector3.forward);
        Beam(tip, new Vector3(x + 1.18f, y, z), width * .10f, tint, alpha, Vector3.forward);
        Beam(new Vector3(x, y, z - 2.1f), tip, width * .17f, tint, alpha * .38f, Vector3.right);
    }

    void Beam(Vector3 a, Vector3 b, float width, Color tint, float alpha, Vector3 edge = default)
    {
        alpha *= DisplaySettings.AccentScale;
        if (alpha < .002f) return;
        if (edge == Vector3.zero) edge = new Vector3(-(b - a).y, (b - a).x, 0).normalized;
        Quad(a, b, width * 4, tint, alpha * .16f, edge);
        Quad(a, b, width, Color.Lerp(tint, Color.white, .18f), Mathf.Min(.92f, alpha), edge);
    }

    void Quad(Vector3 a, Vector3 b, float width, Color tint, float alpha, Vector3 edge)
    {
        if (vertices.Count + 4 > VertexBudget) return;
        int start = vertices.Count;
        Vector3 half = edge * width * .5f;
        vertices.Add(a - half); vertices.Add(b - half); vertices.Add(b + half); vertices.Add(a + half);
        tint.a = alpha;
        for (int i = 0; i < 4; i++) colors.Add(tint);
        uvs.Add(Vector2.zero); uvs.Add(Vector2.right); uvs.Add(Vector2.one); uvs.Add(Vector2.up);
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    void ClearVisuals()
    {
        Array.Clear(waves, 0, waves.Length);
        Array.Clear(stitches, 0, stitches.Length);
        ActiveStitchCount = ActiveWeaveLaneMask = 0;
        weaveEpoch++;
        // 既に先読みした未確定ノーツ同士は、再開後の新しい連打を作れる。
        // 完了済みの前群は旧世代のままなので、停止前の成功へはつながらない。
        foreach (var group in weaveNotes.Values) group.epoch = weaveEpoch;
        for (int i = 0; i < latestResolved.Length; i++) latestResolved[i] = double.NegativeInfinity;
        if (themeResponse != null) themeResponse.Clear();
        if (meteorWorld != null) meteorWorld.ClearMeteorResponses();
        ActiveWaveCount = 0;
        ActiveFloorLaneMask = 0;
        Presentation = default;
        if (mesh != null) mesh.Clear();
        if (meshRenderer != null) meshRenderer.enabled = false;
    }

    public void ResetState()
    {
        foreach (var note in tracked)
            if (!ReferenceEquals(note, null))
            {
                note.OnRetired -= Retired;
                note.OnJudged -= Cut; note.OnMiss -= Miss;
            }
        tracked.Clear();
        weaveNotes.Clear();
        Array.Clear(lastGroups, 0, lastGroups.Length);
        ClearVisuals();
        hasTime = false;
        PairCount = ReleaseCount = 0;
    }

    void OnDisable() { ClearVisuals(); hasTime = false; }
    void OnDestroy()
    {
        Bind(null, null);
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }
    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
