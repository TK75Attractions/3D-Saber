using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// GamePlayManager の時計でだけ進める。判定・入力・ノーツの位置は変更しない。
public sealed class DarkMode : MonoBehaviour
{
    NoteSpawner spawner;
    FloorRenderer floor;
    BarLineSpawner bars;
    GameHUDSkin hud;
    Camera view;
    Transform gate;
    Color originalBackground, originalFog;
    DarkTimeline timeline;
    BladeSample red, blue;
    readonly SaberInputBridge[] bridges = new SaberInputBridge[2];
    readonly DarkLamp[] lamps = new DarkLamp[2];
    readonly DarkStageMaterials gateMaterials = new DarkStageMaterials();
    Func<CuttableNote, float> brightnessFor;
    bool ended;
    // 暗幕(2026-10-27)。剣とノーツは暗幕の上に描き、ノーツは自分の明るさ(BrightnessFor)で隠れる。
    DarkVeil veil;
    Action<CuttableNote> onNoteSpawned;
    readonly List<(SaberInputBridge bridge, bool above)> saberLayers = new List<(SaberInputBridge, bool)>();
    readonly List<(MouseSaberInput mouse, bool above)> mouseLayers = new List<(MouseSaberInput, bool)>();
    readonly Dictionary<Renderer, int> originalNoteOrders = new Dictionary<Renderer, int>();
    readonly List<Renderer> noteRenderers = new List<Renderer>();
    readonly List<Renderer> partRenderers = new List<Renderer>();
    // 判定の文字・コンボの炎(GameplayFeedbackPresenter の Overlay)も HUD と同じだけ暗くする。
    CanvasGroup feedbackGroup;
    float feedbackOriginalAlpha = 1f;

    public DarkVeil Veil => veil;

    public float Level { get; private set; }
    public Func<SaberHand, BladeSample?> BladeOverride { get; set; }

    public static DarkMode Create(Transform parent, ChartData chart, NoteSpawner spawner,
        SaberCutJudge first, SaberCutJudge second, FloorRenderer floor, BarLineSpawner bars)
    {
        var map = DarkTimeline.Build(chart?.dark);
        if (map == null || spawner == null) return null;
        var go = new GameObject("DarkMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<DarkMode>();
        mode.timeline = map;
        mode.spawner = spawner;
        mode.floor = floor;
        mode.bars = bars;
        mode.hud = UnityEngine.Object.FindFirstObjectByType<GameHUDSkin>();
        mode.view = Camera.main;
        mode.originalBackground = mode.view != null ? mode.view.backgroundColor : Color.black;
        mode.originalFog = RenderSettings.fogColor;
        var guide = GameObject.Find("JudgeGuide");
        mode.gate = guide != null ? guide.transform : null;
        foreach (var judge in new[] { first, second })
            if (judge != null)
                mode.bridges[judge.hand == SaberHand.Left ? 1 : 0] = judge.bladeProvider;
        mode.brightnessFor = mode.BrightnessFor;
        spawner.BrightnessFor = mode.brightnessFor;
        spawner.OnChartReset += mode.EndPlay;
        mode.lamps[0] = new DarkLamp(go.transform, "RedLamp");
        mode.lamps[1] = new DarkLamp(go.transform, "BlueLamp");
        mode.veil = DarkVeil.Create(mode.view, spawner.judgeZ);
        mode.LiftSabers(go.scene);
        mode.onNoteSpawned = mode.LiftNote;
        spawner.OnNoteSpawned += mode.onNoteSpawned;
        foreach (var note in spawner.LiveNotes) mode.LiftNote(note);
        var feedback = UnityEngine.Object.FindFirstObjectByType<GameplayFeedbackPresenter>();
        if (feedback != null)
        {
            mode.feedbackGroup = feedback.GetComponent<CanvasGroup>();
            if (mode.feedbackGroup == null) mode.feedbackGroup = feedback.gameObject.AddComponent<CanvasGroup>();
            mode.feedbackOriginalAlpha = mode.feedbackGroup.alpha;
        }
        return mode;
    }

    // 剣(刃・縁・残光)は暗幕の上。M のノイズと同じ仕組み(SaberMemoryVeilOrder)を使い、終わりに元へ戻す。
    void LiftSabers(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (var bridge in UnityEngine.Object.FindObjectsByType<SaberInputBridge>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (bridge.gameObject.scene != scene) continue;
            saberLayers.Add((bridge, bridge.DrawAboveMemoryVeil));
            bridge.DrawAboveMemoryVeil = true;
        }
        foreach (var mouse in UnityEngine.Object.FindObjectsByType<MouseSaberInput>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mouse.gameObject.scene != scene) continue;
            mouseLayers.Add((mouse, mouse.DrawAboveMemoryVeil));
            mouse.DrawAboveMemoryVeil = true;
        }
    }

    // ノーツ(本体・矢印・着地の枠・残り回数)は暗幕と剣のあいだ。遠いノーツは自分の明るさで黒くなる。
    void LiftNote(CuttableNote note)
    {
        if (note == null || ended) return;
        noteRenderers.Clear();
        note.GetComponentsInChildren(true, noteRenderers);
        if (note.TimingCue != null)
        {
            AddRenderers(note.TimingCue.GhostRoot);
            AddRenderers(note.TimingCue.ApproachRoot);
        }
        if (note.countLabel != null) AddRenderers(note.countLabel.gameObject);
        foreach (var renderer in noteRenderers)
        {
            if (renderer == null) continue;
            if (!originalNoteOrders.ContainsKey(renderer)) originalNoteOrders[renderer] = renderer.sortingOrder;
            renderer.sortingOrder = MemoryVeilLayers.AboveNoteSortingOrder;
        }
    }

    void AddRenderers(GameObject part)
    {
        if (part == null) return;
        partRenderers.Clear();
        part.GetComponentsInChildren(true, partRenderers);
        noteRenderers.AddRange(partRenderers);
    }

    BladeSample SampleFor(SaberHand hand)
    {
        var forced = BladeOverride?.Invoke(hand);
        if (forced.HasValue) return DarkRules.Available(forced.Value) ? forced.Value : default;
        var bridge = bridges[hand == SaberHand.Left ? 1 : 0];
        if (bridge == null || !bridge.isActiveAndEnabled || !bridge.HasBlade) return default;
        bool fresh = bridge.UsingMouseFallback;
        var input = InputPoint.Instance;
        if (!fresh && input != null)
            fresh = bridge.stickIndex == 2
                ? input.IsRecentlyActive2(DarkRules.FreshSeconds)
                : input.IsRecentlyActive(DarkRules.FreshSeconds);
        if (!fresh) return default;
        var sample = new BladeSample(bridge.WorldEndA, bridge.WorldEndB);
        return DarkRules.Available(sample) ? sample : default;
    }

    public float BrightnessFor(CuttableNote note)
    {
        if (ended || note == null) return 1f;
        return DarkRules.Brightness(Level, note.RequiredHand,
            (Vector2)note.VisualTransform.position, red, blue);
    }

    public void Tick(double songTime, bool refreshNotes = true)
    {
        if (ended || spawner == null || !DarkRules.Finite(songTime)) return;
        Level = timeline.Evaluate(songTime - spawner.TotalOffsetSeconds);
        red = SampleFor(SaberHand.Right);
        blue = SampleFor(SaberHand.Left);
        float stage = DarkRules.StageBrightness(Level);
        if (floor != null) floor.SetDarkBrightness(stage);
        gateMaterials.Apply(gate, stage);
        GameStageSkin.SetDarkBrightness(view, originalBackground, originalFog, stage);
        if (bars != null) bars.DarkBrightness = stage;
        float hudBrightness = DarkRules.HudBrightness(Level);
        if (hud != null) hud.SetDarkBrightness(hudBrightness);
        if (feedbackGroup != null) feedbackGroup.alpha = feedbackOriginalAlpha * hudBrightness;
        if (spawner.FloorGuide != null) spawner.FloorGuide.DarkBrightness = stage;
        lamps[0]?.Show(red, Level, spawner.judgeZ + .12f);
        lamps[1]?.Show(blue, Level, spawner.judgeZ + .12f);
        veil?.Show(DarkRules.VeilDarkness(Level), red, blue);

        // 再生中は位置更新後の NoteSpawner.Tick に任せ、停止中だけここで補う。
        if (refreshNotes)
        {
            var notes = spawner.LiveNotes;
            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (note != null && !note.IsCut) note.SetDarkBrightness(BrightnessFor(note));
            }
            if (spawner.FloorGuide != null) spawner.FloorGuide.Tick(spawner, songTime);
            spawner.RefreshFallPetalBrightness();
            if (bars != null) bars.RefreshDarkBrightness();
        }
    }

    public void EndPlay()
    {
        if (ended) return;
        ended = true;
        Level = 0;
        BladeOverride = null;
        veil?.Dispose();
        veil = null;
        foreach (var item in saberLayers)
            if (item.bridge != null) item.bridge.DrawAboveMemoryVeil = item.above;
        foreach (var item in mouseLayers)
            if (item.mouse != null) item.mouse.DrawAboveMemoryVeil = item.above;
        saberLayers.Clear();
        mouseLayers.Clear();
        foreach (var pair in originalNoteOrders)
            if (pair.Key != null) pair.Key.sortingOrder = pair.Value;
        originalNoteOrders.Clear();
        if (feedbackGroup != null) feedbackGroup.alpha = feedbackOriginalAlpha;
        if (spawner != null)
        {
            spawner.OnChartReset -= EndPlay;
            if (onNoteSpawned != null) spawner.OnNoteSpawned -= onNoteSpawned;
            if (spawner.BrightnessFor == brightnessFor) spawner.BrightnessFor = null;
            var notes = spawner.LiveNotes;
            for (int i = 0; i < notes.Count; i++)
                if (notes[i] != null) notes[i].SetDarkBrightness(1f);
            if (spawner.FloorGuide != null) spawner.FloorGuide.RestoreDarkBrightness();
            spawner.RefreshFallPetalBrightness();
        }
        if (floor != null) floor.SetDarkBrightness(1f);
        gateMaterials.Apply(gate, 1f);
        if (view != null) view.backgroundColor = originalBackground;
        RenderSettings.fogColor = originalFog;
        if (bars != null)
        {
            bars.DarkBrightness = 1f;
            bars.RefreshDarkBrightness();
        }
        if (hud != null) hud.SetDarkBrightness(1f);
        foreach (var lamp in lamps) lamp?.Hide();
    }

    void OnDisable() => EndPlay();
    void OnDestroy()
    {
        EndPlay();
        foreach (var lamp in lamps) lamp?.Dispose();
    }
}

// 二つの半円と直線で刃全体を包む柔らかな光。画像・Collider・Lightは作らない。
// 頂点のalphaを中心→内周→外周へ落とし、回転や長さが変わっても線分に沿わせる。
sealed class DarkLamp : IDisposable
{
    const int Steps = 24;
    const int Contour = (Steps + 1) * 2;
    readonly GameObject root;
    readonly Mesh mesh;
    readonly Material material;
    readonly List<Vector3> vertices = new List<Vector3>(1 + Contour * 2);

    public DarkLamp(Transform parent, string name)
    {
        root = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        root.transform.SetParent(parent, false);
        mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        material = new Material(Resources.Load<Shader>("Effects/NoteGuide")) {
            name = name, hideFlags = HideFlags.DontSave, renderQueue = 2440
        };
        root.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = root.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        var colors = new List<Color>(1 + Contour * 2);
        var indices = new List<int>(Contour * 9);
        vertices.Add(Vector3.zero);
        colors.Add(new Color(1, 1, 1, .12f));
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < Contour; i++)
            {
                vertices.Add(Vector3.zero);
                colors.Add(new Color(1, 1, 1, ring == 0 ? .045f : 0f));
            }
        for (int i = 0; i < Contour; i++)
        {
            int next = (i + 1) % Contour;
            int a = 1 + i, b = 1 + next, c = a + Contour, d = b + Contour;
            indices.Add(0); indices.Add(a); indices.Add(b);
            indices.Add(a); indices.Add(c); indices.Add(d);
            indices.Add(a); indices.Add(d); indices.Add(b);
        }
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(indices, 0);
        root.SetActive(false);
    }

    public void Show(BladeSample blade, float level, float z)
    {
        if (root == null) return;
        if (!DarkRules.Available(blade) || level <= 0f) { Hide(); return; }
        root.SetActive(true);
        Color tint = RushESecretState.Lamp;
        tint.a = level;
        material.SetColor("_BaseColor", tint);
        vertices[0] = Local(blade.Middle, z);
        float angle = Mathf.Atan2(blade.B.y - blade.A.y, blade.B.x - blade.A.x);
        for (int ring = 0; ring < 2; ring++)
        {
            float radius = ring == 0 ? DarkRules.FullRadius : DarkRules.FadeRadius;
            for (int cap = 0; cap < 2; cap++)
                for (int step = 0; step <= Steps; step++)
                {
                    float theta = angle + (cap == 0 ? Mathf.PI / 2 : -Mathf.PI / 2)
                        + Mathf.PI * step / Steps;
                    Vector2 center = cap == 0 ? blade.A : blade.B;
                    Vector2 point = center + new Vector2(Mathf.Cos(theta), Mathf.Sin(theta)) * radius;
                    vertices[1 + ring * Contour + cap * (Steps + 1) + step] = Local(point, z);
                }
        }
        mesh.SetVertices(vertices);
        mesh.RecalculateBounds();
    }

    Vector3 Local(Vector2 point, float z)
        => root.transform.InverseTransformPoint(new Vector3(point.x, point.y, z));

    public void Hide() { if (root != null) root.SetActive(false); }

    public void Dispose()
    {
        Discard(root);
        Discard(mesh);
        Discard(material);
    }

    static void Discard(UnityEngine.Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(item);
        else UnityEngine.Object.DestroyImmediate(item);
    }
}
