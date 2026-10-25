using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

// 覚える裏譜面(M)の描画と配線。進行はGamePlayManagerのTickだけで行う。
// ノーツ・判定・得点・曲の音には手を加えない。
public sealed class MemoryVeilMode : MonoBehaviour
{
    static readonly int StrengthId = Shader.PropertyToID("_Strength");
    static readonly int FrameId = Shader.PropertyToID("_NoiseFrame");
    static readonly int SecondsId = Shader.PropertyToID("_NoiseSeconds");
    static readonly int ReducedId = Shader.PropertyToID("_Reduced");
    static readonly int AmberId = Shader.PropertyToID("_Amber");
    static bool warnedShader;

    readonly List<(SaberInputBridge bridge, bool above)> bridges =
        new List<(SaberInputBridge bridge, bool above)>();
    readonly List<(MouseSaberInput mouse, bool above)> mice =
        new List<(MouseSaberInput mouse, bool above)>();
    readonly Vector3[] corners = new Vector3[4];

    ChartData chart;
    NoteSpawner spawner;
    bool previousSpawnerMode;
    bool ended, shaderUnavailable, ownsCaption;
    Camera noiseCamera;
    Mesh mesh;
    Material material;
    MeshRenderer noise;
    List<MemoryVeilRules.Interval> intervals = new List<MemoryVeilRules.Interval>();
    double offset = double.NaN, approach = double.NaN;
    double previousTime = double.NegativeInfinity;
    float nextOrderRefresh;
    int nextCaption;

    public Renderer NoiseRenderer => noise;
    public float Strength { get; private set; }
    public IReadOnlyList<MemoryVeilRules.Interval> Intervals => intervals;

    public static MemoryVeilMode Create(Transform parent, ChartData chart, NoteSpawner spawner)
    {
        var go = new GameObject("MemoryVeilMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<MemoryVeilMode>();
        mode.Initialize(chart, spawner);
        return mode;
    }

    void Initialize(ChartData source, NoteSpawner noteSpawner)
    {
        chart = source;
        spawner = noteSpawner;
        if (spawner != null)
        {
            previousSpawnerMode = spawner.MemoryVeilMode;
            spawner.MemoryVeilMode = true;
        }

        // BridgeとMouseが同じ物体に付いていても、変更前の値を両方とも先に記録する。
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (bridge.gameObject.scene != gameObject.scene) continue;
            bridges.Add((bridge, bridge.DrawAboveMemoryVeil));
        }
        foreach (var mouse in Object.FindObjectsByType<MouseSaberInput>(
            FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mouse.gameObject.scene != gameObject.scene) continue;
            mice.Add((mouse, mouse.DrawAboveMemoryVeil));
        }
        foreach (var item in bridges) item.bridge.DrawAboveMemoryVeil = true;
        foreach (var item in mice) item.mouse.DrawAboveMemoryVeil = true;

        RefreshTiming();
        EnsureNoise();
    }

    void RefreshTiming()
    {
        if (spawner == null) return;
        double nextOffset = spawner.TotalOffsetSeconds;
        double nextApproach = spawner.approachTime;
        if (offset == nextOffset && approach == nextApproach) return;
        offset = nextOffset;
        approach = nextApproach;
        intervals = MemoryVeilRules.BuildIntervals(chart, offset, approach);
        ResetCaptions();
    }

    public void Tick(double songTime)
    {
        if (ended || double.IsNaN(songTime) || double.IsInfinity(songTime)) return;
        RefreshTiming();

        // 剣の部品は作った所でも当て直すので、ここは取りこぼしの保険(0.5秒ごと。毎コマ材質を写さない)。
        if (Time.unscaledTime >= nextOrderRefresh)
        {
            nextOrderRefresh = Time.unscaledTime + .5f;
            foreach (var item in bridges)
                if (item.bridge != null) SaberMemoryVeilOrder.RefreshFor(item.bridge);
            foreach (var item in mice)
                if (item.mouse != null) SaberMemoryVeilOrder.RefreshFor(item.mouse);
        }

        Strength = MemoryVeilRules.StrengthAt(intervals, songTime);
        EnsureNoise();
        if (noise != null)
        {
            FitNoise();
            bool reduced = DisplaySettings.ReducedEffects;
            double fps = reduced ? 4 : 20;
            double frame = Math.Floor(songTime * fps);
            material.SetFloat(StrengthId, Strength);
            material.SetFloat(FrameId, (float)frame);
            material.SetFloat(SecondsId, (float)(frame / fps));
            material.SetFloat(ReducedId, reduced ? 1f : 0f);
            noise.enabled = Strength > 0f;
        }

        TickCaptions(songTime);
    }

    void EnsureNoise()
    {
        if (noise != null || shaderUnavailable) return;
        noiseCamera = Camera.main;
        if (noiseCamera == null) return;

        if (material == null)
        {
            var shader = Resources.Load<Shader>("Effects/MemoryVeilNoise");
            if (shader == null || !shader.isSupported)
            {
                shaderUnavailable = true;
                if (!warnedShader)
                {
                    warnedShader = true;
                    Debug.LogWarning("[MemoryVeil] ノイズ用のshaderが無いか未対応です。ノイズなしで続行します。");
                }
                return;
            }
            material = new Material(shader)
            {
                name = "MemoryVeil/Noise",
                hideFlags = HideFlags.DontSave,
                renderQueue = MemoryVeilLayers.NoiseRenderQueue
            };
            material.SetColor(AmberId, RushESecretState.Amber);
            material.SetFloat(StrengthId, 0f);
        }

        if (mesh == null)
        {
            mesh = new Mesh
            {
                name = "MemoryVeil/Screen",
                hideFlags = HideFlags.DontSave,
                vertices = new Vector3[4],
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            mesh.MarkDynamic();
        }

        var go = new GameObject("MemoryVeilNoise", typeof(MeshFilter), typeof(MeshRenderer));
        go.hideFlags = HideFlags.DontSave;
        go.transform.SetParent(noiseCamera.transform, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        noise = go.GetComponent<MeshRenderer>();
        noise.sharedMaterial = material;
        noise.sortingLayerID = 0;
        noise.sortingOrder = MemoryVeilLayers.NoiseSortingOrder;
        noise.shadowCastingMode = ShadowCastingMode.Off;
        noise.receiveShadows = false;
        noise.lightProbeUsage = LightProbeUsage.Off;
        noise.reflectionProbeUsage = ReflectionProbeUsage.Off;
        noise.enabled = false;
        FitNoise();
    }

    void FitNoise()
    {
        if (noiseCamera == null || mesh == null) return;
        float depth = Mathf.Min(noiseCamera.nearClipPlane + .05f,
            (noiseCamera.nearClipPlane + noiseCamera.farClipPlane) * .5f);
        noiseCamera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), depth,
            Camera.MonoOrStereoscopicEye.Mono, corners);
        Vector3 center = (corners[0] + corners[1] + corners[2] + corners[3]) * .25f;
        for (int i = 0; i < corners.Length; i++)
            corners[i] = center + (corners[i] - center) * 1.04f;
        mesh.vertices = corners;
        mesh.RecalculateBounds();
    }

    void TickCaptions(double songTime)
    {
        if (songTime < previousTime) ResetCaptions();
        previousTime = songTime;
        var captions = chart?.memory?.captions;
        if (captions == null) return;

        MemoryCaptionData latest = null;
        while (nextCaption < captions.Count)
        {
            var caption = captions[nextCaption];
            if (caption == null) { nextCaption++; continue; }
            if (caption.time / 1000.0 + offset > songTime) break;
            latest = caption;
            nextCaption++;
        }
        if (latest == null) return;

        double begin = latest.time / 1000.0 + offset;
        double end = latest.endTime / 1000.0 + offset;
        if (end <= songTime || string.IsNullOrWhiteSpace(latest.text))
        {
            if (ownsCaption) FirstNoteHandHintView.Hide();
            ownsCaption = false;
            return;
        }

        FirstNoteHandHintView.Show(latest.text, RushESecretState.Amber, begin, end);
        ownsCaption = true;
    }

    void ResetCaptions()
    {
        if (ownsCaption) FirstNoteHandHintView.Hide();
        ownsCaption = false;
        nextCaption = 0;
        previousTime = double.NegativeInfinity;
    }

    public void EndPlay()
    {
        if (ended) return;
        ended = true;
        Strength = 0;
        if (noise != null) noise.enabled = false;
        if (material != null) material.SetFloat(StrengthId, 0f);
        FirstNoteHandHintView.Hide();
        ownsCaption = false;

        if (spawner != null) spawner.MemoryVeilMode = previousSpawnerMode;
        foreach (var item in bridges)
            if (item.bridge != null) item.bridge.DrawAboveMemoryVeil = item.above;
        foreach (var item in mice)
            if (item.mouse != null) item.mouse.DrawAboveMemoryVeil = item.above;
        bridges.Clear();
        mice.Clear();
    }

    void OnDestroy()
    {
        EndPlay();
        if (noise != null) UISkinKit.SafeDestroy(noise.gameObject);
        UISkinKit.SafeDestroy(mesh);
        UISkinKit.SafeDestroy(material);
    }
}
