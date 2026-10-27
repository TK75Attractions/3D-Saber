using System;
using UnityEngine;
using UnityEngine.Rendering;

// 2:23 AM の Y(闇と灯り)の暗幕(2026-10-27)。カメラの前を黒い板で覆い、剣のまわりだけ穴を開ける。
// 線形の色空間・ノーツの強い発光・投影向けの露出の上げ幅・あとから足した演出に左右されず、暗い区間を黒くする。
// 並び順は M のノイズと同じ段。剣(MemoryVeilLayers.AboveSortingOrder)とノーツ(AboveNoteSortingOrder)は DarkMode が上へ上げる。
// HUD など Overlay のキャンバスは覆わない(DarkMode が透明度で暗くする)。
public sealed class DarkVeil : IDisposable
{
    static readonly int DarknessId = Shader.PropertyToID("_Darkness");
    static readonly int RedId = Shader.PropertyToID("_RedSegment");
    static readonly int BlueId = Shader.PropertyToID("_BlueSegment");
    static readonly int MaskId = Shader.PropertyToID("_BladeMask");
    static readonly int RadiiId = Shader.PropertyToID("_Radii");
    static readonly int JudgeZId = Shader.PropertyToID("_JudgeZ");
    static bool warned;

    readonly Camera view;
    readonly GameObject root;
    readonly Mesh mesh;
    readonly Material material;
    readonly MeshRenderer renderer;
    readonly Vector3[] corners = new Vector3[4];
    float fittedNear = float.NaN, fittedFov = float.NaN, fittedAspect = float.NaN;

    public float Darkness { get; private set; }
    public bool Visible => renderer != null && renderer.enabled;
    public Renderer Renderer => renderer;

    public static DarkVeil Create(Camera view, float judgeZ)
    {
        if (view == null) return null;
        var shader = Resources.Load<Shader>("Effects/DarkVeil");
        if (shader == null || !shader.isSupported)
        {
            if (!warned)
            {
                warned = true;
                Debug.LogWarning("[DarkVeil] 暗幕のshaderが無いか未対応です。暗幕なしで続行します。");
            }
            return null;
        }
        return new DarkVeil(view, shader, judgeZ);
    }

    DarkVeil(Camera view, Shader shader, float judgeZ)
    {
        this.view = view;
        material = new Material(shader)
        {
            name = "DarkVeil",
            hideFlags = HideFlags.DontSave,
            renderQueue = MemoryVeilLayers.NoiseRenderQueue
        };
        material.SetFloat(JudgeZId, judgeZ);
        material.SetVector(RadiiId, new Vector4(DarkRules.FullRadius, DarkRules.FadeRadius, 0f, 0f));
        material.SetFloat(DarknessId, 0f);
        mesh = new Mesh
        {
            name = "DarkVeil/Screen",
            hideFlags = HideFlags.DontSave,
            vertices = new Vector3[4],
            triangles = new[] { 0, 1, 2, 0, 2, 3 }
        };
        mesh.MarkDynamic();
        root = new GameObject("DarkVeil", typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.DontSave };
        root.transform.SetParent(view.transform, false);
        root.GetComponent<MeshFilter>().sharedMesh = mesh;
        renderer = root.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = 0;
        renderer.sortingOrder = MemoryVeilLayers.NoiseSortingOrder;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
        Fit();
    }

    // darkness は穴の外の透明度(DarkRules.VeilDarkness)。使えない剣の側は穴を開けない。
    public void Show(float darkness, BladeSample red, BladeSample blue)
    {
        if (renderer == null) return;
        Darkness = DarkRules.Level(darkness);
        renderer.enabled = Darkness > .001f;
        if (!renderer.enabled) return;
        Fit();
        material.SetFloat(DarknessId, Darkness);
        bool r = DarkRules.Available(red), b = DarkRules.Available(blue);
        material.SetVector(MaskId, new Vector4(r ? 1f : 0f, b ? 1f : 0f, 0f, 0f));
        if (r) material.SetVector(RedId, new Vector4(red.A.x, red.A.y, red.B.x, red.B.y));
        if (b) material.SetVector(BlueId, new Vector4(blue.A.x, blue.A.y, blue.B.x, blue.B.y));
    }

    public void Hide()
    {
        Darkness = 0f;
        if (renderer != null) renderer.enabled = false;
    }

    // 近くの切り口の面に、画角の2倍の板を置く(撮影で縦横比が変わっても端が欠けない)。カメラが変わったときだけ作り直す。
    void Fit()
    {
        if (view == null || mesh == null) return;
        if (view.nearClipPlane == fittedNear && view.fieldOfView == fittedFov && view.aspect == fittedAspect) return;
        fittedNear = view.nearClipPlane; fittedFov = view.fieldOfView; fittedAspect = view.aspect;
        float depth = Mathf.Min(view.nearClipPlane + .05f, (view.nearClipPlane + view.farClipPlane) * .5f);
        view.CalculateFrustumCorners(new Rect(0, 0, 1, 1), depth, Camera.MonoOrStereoscopicEye.Mono, corners);
        Vector3 center = (corners[0] + corners[1] + corners[2] + corners[3]) * .25f;
        for (int i = 0; i < corners.Length; i++)
            corners[i] = center + (corners[i] - center) * 2f;
        mesh.vertices = corners;
        mesh.RecalculateBounds();
    }

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
