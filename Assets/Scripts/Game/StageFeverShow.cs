using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// サビを「フィーバー」に(派手さカタログ 祭2)と、遠景のライトショー(祭4)。2026-10-20。
// 一枚のメッシュで、通路の外(|x|≥5.8)だけに描く。ゲートの内側(ノーツの通り道)には何も足さない。
//   祭4 譜面のノーツの時刻で、遠景の左右に5本ずつ立つ光の柱を、奥から手前へ順に光らせる。押したかどうかに関係なく光る。
//       点滅は1秒2.2回まで(それより詰まった合図はまとめる)。強さは区間の強度で決める(区間外・A メロ 0.25 → サビ 1.0)。
//       RUSH E の背景(PulseArray)は自前の照明が同じ合図で光るので、柱は足さない。
//   祭2 サビ(強度 .65 以上の区間)の間は、左右の床から斜め上へレーザー6本(拍で明るく、強拍で太く、0.5Hz でゆっくり首を振る)、
//       通路の外の柱を手前へ流して速さを出し、背景の色を背景ごとの「ブースト」の色へ0.3秒で寄せる。
//       終わりはレーザーを0.5秒で細くして消し、下がる「シュー」を鳴らして色を戻す。拍の明滅も1秒2.2回まで(速い曲は2拍ごと)。
//   斬2 金ノーツの会心では、背景を2拍だけ金色へ寄せる。
// 独自の Update は持たず、GamePlayManager の曲時計から Tick する。演出控えめ(LOW)ではレーザー・流れる柱・色の変化を出さず、
// 遠景の柱は3割。投影モードでは明るさを抑える。
public sealed class StageFeverShow : MonoBehaviour
{
    public const float FeverInSeconds = .3f, FeverOutSeconds = .5f;
    public const float MaxFlashesPerSecond = 2.2f;
    public const float CueSpacing = 1f / MaxFlashesPerSecond;
    public const float CorridorHalfWidth = StageReactiveEffects.CorridorHalfWidth;
    public const int PillarsPerSide = 5, LasersPerSide = 3, PostsPerSide = 6;
    const int VertexBudget = 4000;

    struct Beat { public double time; public bool downbeat; }

    readonly List<Vector3> vertices = new List<Vector3>(VertexBudget);
    readonly List<Color> colors = new List<Color>(VertexBudget);
    readonly List<Vector2> uvs = new List<Vector2>(VertexBudget);
    readonly List<int> indices = new List<int>(VertexBudget * 3 / 2);
    readonly List<(double start, double end)> strong = new List<(double, double)>();
    StagePerformanceTimeline timeline = new StagePerformanceTimeline();
    double[] cues = new double[0];
    // サビの終わりの音を鳴らす時刻(EndSoundTimes)。
    double[] endTimes = new double[0];
    // 明滅させる拍。前に光らせた拍から CueSpacing 以上あいたものだけ(テンポが途中で速くなっても1秒2.2回まで)。
    Beat[] beats = new Beat[0];
    float beatSeconds = .5f;
    StageTheme theme;
    float floor;
    bool pillars = true;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    AudioSource endSource;
    AudioClip endClip;
    Camera stageCamera;
    Color baseBackground;
    bool atmosphereChanged;
    double lastTime = double.NaN;
    float goldLeft, goldSeconds = 1;

    public float Fever { get; private set; }
    public float LightLevel { get; private set; }
    public float PillarPulse { get; private set; }
    public float BeatPulse { get; private set; }
    public float Boost { get; private set; }
    public float GoldTint { get; private set; }
    public int EndSoundCount { get; private set; }
    public int CueCount => cues.Length;
    public bool PillarsEnabled => pillars;
    // 背景の色を寄せるカメラ(既定は Camera.main)。テストでは明示する。
    public Camera AtmosphereCamera { get => stageCamera; set => stageCamera = value; }
    public Mesh Mesh => mesh;

    public static StageFeverShow Create(FloorRenderer stage, StagePerformanceTimeline song, ChartData chart)
    {
        var go = new GameObject("StageFeverShow");
        go.transform.SetParent(stage.transform, false);
        var show = go.AddComponent<StageFeverShow>();
        show.Setup(stage.ActiveTheme, stage.floorY, song, chart);
        return show;
    }

    // テストからも呼ぶ。背景・床の高さ・区間・譜面を渡す。
    public void Setup(StageTheme stageTheme, float floorY, StagePerformanceTimeline song, ChartData chart)
    {
        theme = stageTheme; floor = floorY;
        pillars = theme != StageTheme.PulseArray;
        timeline = song ?? new StagePerformanceTimeline();
        strong.Clear();
        if (timeline.sections != null)
            foreach (var s in timeline.sections)
                if (s != null && Finite(s.startSeconds) && Finite(s.endSeconds) && Finite(s.intensity)
                    && s.startSeconds >= 0 && s.endSeconds > s.startSeconds && s.intensity >= StagePerformanceTimeline.StrongIntensity)
                    strong.Add((s.startSeconds, s.endSeconds));
        strong.Sort((a, b) => a.start.CompareTo(b.start));
        endTimes = EndSoundTimes(strong);
        cues = BuildCues(chart);
        BuildBeats(chart);
        if (mesh == null) Build();
        stageCamera = Camera.main;
        baseBackground = GameStageSkin.BackgroundColor;
        lastTime = double.NaN;
    }

    void Build()
    {
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null) return;
        mesh = new Mesh { name = "StageFeverShow", hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        material = new Material(shader) { name = "StageFeverShow", hideFlags = HideFlags.DontSave };
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off; meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = LightProbeUsage.Off; meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        meshRenderer.enabled = false;
        if (Application.isPlaying)
        {
            endSource = gameObject.AddComponent<AudioSource>();
            endSource.playOnAwake = false; endSource.spatialBlend = 0;
            endClip = ProceduralSfx.Clip("ChorusEnd", FlourishSfx.Cached("ChorusEnd", FlourishSfx.ChorusEnd));
        }
    }

    // サビの終わりの「シュー」を鳴らす時刻。フィーバーが本当に終わる時刻だけを、1ms 以内の重なりは1つにして返す(2026-10-22)。
    // 校歌は強い区間が2つとも 145.401 秒で終わり、区間ごとに鳴らすと同じ音が同時に2回(約2倍の大きさ)鳴っていた。
    // ほかの強い区間がまだ続いている時刻も鳴らさない(フィーバーが続いているのに終わりの音が出ないように)。純関数。
    public static double[] EndSoundTimes(IReadOnlyList<(double start, double end)> sections)
    {
        var picked = new List<double>();
        if (sections == null) return picked.ToArray();
        var ends = new List<double>();
        foreach (var (_, end) in sections) if (Finite(end)) ends.Add(end);
        ends.Sort();
        foreach (double end in ends)
        {
            if (picked.Count > 0 && end - picked[picked.Count - 1] <= .001) continue;
            bool continues = false;
            foreach (var (start, other) in sections)
                if (start <= end && other > end + .001) { continues = true; break; }
            if (!continues) picked.Add(end);
        }
        return picked.ToArray();
    }

    // 照明の合図。ノーツの時刻(と長いノーツの終わり)を、点滅が1秒2.2回を超えないように間引く。判定調整値は含めない。純関数。
    public static double[] BuildCues(ChartData chart)
    {
        var all = new List<double>();
        double offset = chart != null && Finite(chart.offsetMs) ? chart.offsetMs / 1000.0 : 0;
        if (chart?.notes != null)
            foreach (var note in chart.notes)
                if (note != null && Finite(note.time) && note.TimeSeconds + offset >= 0)
                {
                    all.Add(note.TimeSeconds + offset);
                    if (note.count > 1 && note.lengthMs > 0 && Finite(note.lengthMs)) all.Add(note.TimeSeconds + offset + note.lengthMs / 1000.0);
                }
        all.Sort();
        var picked = new List<double>();
        foreach (double time in all)
            if (picked.Count == 0 || time - picked[picked.Count - 1] >= CueSpacing - 1e-6) picked.Add(time);
        return picked.ToArray();
    }

    void BuildBeats(ChartData chart)
    {
        beats = new Beat[0]; beatSeconds = .5f;
        if (chart == null) return;
        double end = 0;
        double offset = Finite(chart.offsetMs) ? chart.offsetMs / 1000.0 : 0;
        if (chart.notes != null) foreach (var note in chart.notes) if (note != null && Finite(note.time)) end = System.Math.Max(end, note.TimeSeconds + offset);
        foreach (var s in strong) end = System.Math.Max(end, s.end);
        var events = new SongSelectBeatTimeline(new[] { chart }).Events(0, end + 2);
        var picked = new List<Beat>(events.Count);
        double sum = 0; int n = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (i > 0 && n < 64) { sum += events[i].Time - events[i - 1].Time; n++; }
            if (picked.Count == 0 || events[i].Time - picked[picked.Count - 1].time >= CueSpacing - 1e-6)
                picked.Add(new Beat { time = events[i].Time, downbeat = events[i].Downbeat });
        }
        beats = picked.ToArray();
        if (n > 0) beatSeconds = Mathf.Clamp((float)(sum / n), .2f, 1.5f);
    }

    // 明滅させる拍の時刻(テスト用)。
    public double[] FlashBeatTimes()
    {
        var times = new double[beats.Length];
        for (int i = 0; i < beats.Length; i++) times[i] = beats[i].time;
        return times;
    }

    // 強い区間の中は入口から0.3秒で1へ、終わってから0.5秒で0へ。純関数(区間は開始順)。
    public static float FeverAt(double time, IReadOnlyList<(double start, double end)> sections)
    {
        float level = 0;
        foreach (var (start, end) in sections)
        {
            if (time < start) continue;
            if (time < end) level = Mathf.Max(level, Mathf.Clamp01((float)((time - start) / FeverInSeconds)));
            else if (time < end + FeverOutSeconds) level = Mathf.Max(level, 1 - (float)((time - end) / FeverOutSeconds));
        }
        return level;
    }

    // 区間の強さ → 遠景の灯りの強さ(強度 .35 未満は 0.25、サビ .65 以上は 1)。純関数。
    public static float LightLevelFor(float sectionIntensity)
        => Mathf.Lerp(.25f, 1f, Mathf.InverseLerp(.35f, StagePerformanceTimeline.StrongIntensity, sectionIntensity));

    static float CuePulse(double[] times, double now)
    {
        if (times.Length == 0 || !Finite(now)) return 0;
        int index = System.Array.BinarySearch(times, now);
        if (index < 0) index = ~index - 1;
        if (index < 0) return 0;
        double age = now - times[index];
        return age >= .5 ? 0 : Mathf.Exp((float)-age * 9f) * (1 - (float)age * 2);
    }

    // 金ノーツの会心(斬2)。背景を2拍だけ金色へ寄せる。
    public void GoldFlash(float beatSeconds)
    {
        goldSeconds = Mathf.Clamp(beatSeconds, .25f, .75f) * 2;
        goldLeft = goldSeconds;
    }

    public void Tick(double songTime, float deltaTime)
    {
        if (!isActiveAndEnabled || !Finite(songTime)) return;
        bool jumped = Finite(lastTime) && (songTime < lastTime - .05 || songTime - lastTime > .5);
        // 区間の終わりを通り過ぎたら、下がる「シュー」(シーク・巻き戻しでは鳴らさない)。
        if (Finite(lastTime) && !jumped && !DisplaySettings.ReducedEffects)
            foreach (double end in endTimes)
                if (lastTime < end && songTime >= end) PlayEnd();
        lastTime = songTime;
        bool low = DisplaySettings.ReducedEffects;
        Fever = low ? 0 : FeverAt(songTime, strong);
        LightLevel = LightLevelFor(timeline.Evaluate(songTime));
        PillarPulse = pillars ? CuePulse(cues, songTime) : 0;
        BeatPulse = BeatPulseAt(songTime, out bool downbeat);
        goldLeft = Mathf.Max(0, goldLeft - Mathf.Max(0, deltaTime));
        GoldTint = low ? 0 : Mathf.Clamp01(goldLeft / Mathf.Max(.01f, goldSeconds));
        Boost = Fever;
        ApplyAtmosphere();
        Draw(songTime, downbeat, low);
    }

    float BeatPulseAt(double now, out bool downbeat)
    {
        downbeat = false;
        if (beats.Length == 0) return 0;
        int lo = 0, hi = beats.Length - 1, found = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (beats[mid].time <= now) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        if (found < 0) return 0;
        downbeat = beats[found].downbeat;
        float age = (float)(now - beats[found].time);
        return age >= .45f ? 0 : Mathf.Exp(-age * 7f);
    }

    void PlayEnd()
    {
        EndSoundCount++;
        if (endSource != null && endClip != null) endSource.PlayOneShot(endClip, 1f);
    }

    // 背景(カメラの背景色と霧の色)をブーストの色と金へ寄せる。どちらも 0 なら元の色へきっちり戻す。
    void ApplyAtmosphere()
    {
        if (stageCamera == null) stageCamera = Camera.main;
        if (Boost <= 0 && GoldTint <= 0)
        {
            if (!atmosphereChanged) return;
            atmosphereChanged = false;
            SetAtmosphere(baseBackground);
            return;
        }
        atmosphereChanged = true;
        Color target = Color.Lerp(baseBackground, BoostBackground(theme), Boost);
        SetAtmosphere(Color.Lerp(target, new Color(.075f, .052f, .012f), GoldTint * .8f));
    }

    void SetAtmosphere(Color color)
    {
        if (stageCamera != null) stageCamera.backgroundColor = color;
        if (RenderSettings.fog) RenderSettings.fogColor = color;
    }

    // 背景ごとの灯りの色と、サビのブーストの色の組(暗い背景色と、レーザーの2色)。
    public static Color LightColor(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.AmberFoundry: case StageTheme.DesertSanctum: case StageTheme.ClockworkLibrary: return new Color(1.2f, .66f, .2f);
            case StageTheme.MoonlitGarden: return new Color(.96f, .48f, 1.1f);
            case StageTheme.AstralOrbit: case StageTheme.VioletVault: return new Color(.57f, .42f, 1.3f);
            case StageTheme.VerdantAtrium: return new Color(.45f, 1.1f, .55f);
            default: return new Color(.24f, .82f, 1.25f);
        }
    }

    public static Color BoostBackground(StageTheme theme)
    {
        switch (theme)
        {
            case StageTheme.AmberFoundry: case StageTheme.DesertSanctum: case StageTheme.ClockworkLibrary: return new Color(.012f, .045f, .07f);
            case StageTheme.MoonlitGarden: case StageTheme.VerdantAtrium: return new Color(.012f, .05f, .06f);
            case StageTheme.AstralOrbit: case StageTheme.VioletVault: return new Color(.07f, .045f, .012f);
            default: return new Color(.06f, .014f, .06f);
        }
    }

    public static void BoostLasers(StageTheme theme, out Color a, out Color b)
    {
        switch (theme)
        {
            case StageTheme.AmberFoundry: case StageTheme.DesertSanctum: case StageTheme.ClockworkLibrary:
                a = new Color(.2f, .85f, 1.3f); b = new Color(.85f, .95f, 1.3f); return;
            case StageTheme.MoonlitGarden: case StageTheme.VerdantAtrium:
                a = new Color(.25f, 1.2f, 1.1f); b = new Color(1.25f, .95f, .35f); return;
            case StageTheme.AstralOrbit: case StageTheme.VioletVault:
                a = new Color(1.3f, .85f, .25f); b = new Color(.3f, 1f, 1.25f); return;
            default:
                a = new Color(1.3f, .3f, .95f); b = new Color(1.3f, .82f, .3f); return;
        }
    }

    void Draw(double songTime, bool downbeat, bool low)
    {
        if (mesh == null) return;
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        float time = (float)songTime;
        float projector = DisplaySettings.ProjectorMode ? .7f : 1f;
        BoostLasers(theme, out Color boostA, out Color boostB);
        Color baseLight = LightColor(theme);

        // 祭4: 遠景の光の柱。奥から手前へ 0.04 秒ずつずらして光らせる(波のように)。
        if (pillars)
            for (int k = 0; k < PillarsPerSide; k++)
            {
                float z = 12 + k * 8;
                float pulse = CuePulse(cues, songTime - (PillarsPerSide - 1 - k) * .04);
                float alpha = (.05f + .6f * pulse * LightLevel) * projector * (low ? .3f : 1f);
                for (int side = -1; side <= 1; side += 2)
                {
                    Color tint = Fever > 0 ? Color.Lerp(baseLight, (k + (side > 0 ? 1 : 0)) % 2 == 0 ? boostA : boostB, Fever) : baseLight;
                    float x = side * (7.6f + k * .45f), height = 5.2f + k * .6f;
                    Beam(new Vector3(x, floor + .1f, z), new Vector3(x, floor + height, z), .09f + .05f * pulse, tint, alpha, Vector3.right);
                }
            }

        if (!low && Fever > .001f)
        {
            // 祭2: 左右の床から斜め上へ伸びるレーザー。拍で明るく(0.25→0.7)、強拍で太く、ゆっくり首を振る。
            float beat = Mathf.Lerp(.25f, .7f, BeatPulse);
            float thick = 1f + (downbeat ? .9f : .35f) * BeatPulse;
            for (int k = 0; k < LasersPerSide; k++)
                for (int side = -1; side <= 1; side += 2)
                {
                    float sway = Mathf.Sin(time * Mathf.PI * 2 * .5f + k * 1.3f + (side > 0 ? 0 : Mathf.PI)) * .2f;
                    Vector3 origin = new Vector3(side * 6.5f, floor + .05f, 6 + k * 8);
                    Vector3 direction = new Vector3(side * (.45f + sway), 1f, .55f + .1f * k).normalized;
                    Color tint = (k + (side > 0 ? 1 : 0)) % 2 == 0 ? boostA : boostB;
                    Beam(origin, origin + direction * 18f, .06f * thick * Fever, tint, beat * Fever * projector, Vector3.zero);
                }

            // 通路の外の柱を手前へ流して速さを出す(拍ごとに1本ぶん進む)。
            float speed = 44f / PostsPerSide / beatSeconds;
            for (int k = 0; k < PostsPerSide; k++)
            {
                float z = 48 - Mathf.Repeat(time * speed + k * (44f / PostsPerSide), 44f);
                float fade = Mathf.Clamp01((48 - z) / 6f) * Mathf.Clamp01((z - 3) / 6f);
                for (int side = -1; side <= 1; side += 2)
                    Beam(new Vector3(side * 6.3f, floor + .05f, z), new Vector3(side * 6.3f, floor + 2.4f, z), .07f,
                        Color.Lerp(boostA, Color.white, .3f), .5f * fade * Fever * projector, Vector3.right);
            }
        }

        bool draw = vertices.Count > 0;
        mesh.Clear();
        if (draw) { mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0, true); }
        meshRenderer.enabled = draw;
    }

    // 外側の淡い光と芯の2層の帯。edge が 0 のときはカメラから見た幅の向きを使う。
    void Beam(Vector3 a, Vector3 b, float width, Color tint, float alpha, Vector3 edge)
    {
        alpha *= DisplaySettings.AccentScale;
        if (alpha < .002f) return;
        if (edge == Vector3.zero) edge = Vector3.Cross((b - a).normalized, Vector3.forward).normalized;
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

    // 曲の停止・終了・離脱で、背景の色と光を元に戻す。
    public void ResetState()
    {
        Fever = Boost = GoldTint = BeatPulse = PillarPulse = 0;
        goldLeft = 0;
        lastTime = double.NaN;
        if (atmosphereChanged) { atmosphereChanged = false; SetAtmosphere(baseBackground); }
        if (endSource != null) endSource.Stop();
        if (mesh != null) mesh.Clear();
        if (meshRenderer != null) meshRenderer.enabled = false;
    }

    void OnDisable() { ResetState(); }

    void OnDestroy()
    {
        ResetState();
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
        UISkinKit.SafeDestroy(endClip);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
