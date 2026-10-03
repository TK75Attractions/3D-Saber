using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// 指まで骨格を持つ人体を専用カメラで描き、選曲UIへ合成する。
// 背面から見せることで、モデル本人の右手と画面上の右側を一致させる。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectGuideModel : MaskableGraphic
{
    const string AssetPath = "UI/SongSelectHuman/RightHandHuman";
    const int PreviewLayer = 30;
    readonly List<Material> ownedMaterials = new List<Material>();
    GameObject stage, human;
    Camera previewCamera;
    RenderTexture target;
    AnimationClip motion;
    SongSelectDiscGraphic reticle;
    bool loadFailed;
    public bool IsReady => human != null && motion != null && target != null && target.IsCreated();
    public override Texture mainTexture => target != null ? target : Texture2D.whiteTexture;

    // 案内が表示されるまで、モデルも描画用テクスチャも確保しない。
    public void SetPose(float seconds)
    {
        if (!isActiveAndEnabled || !EnsureModel()) return;
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) seconds = 0;
        motion.SampleAnimation(human, Mathf.Repeat(Mathf.Max(0, seconds), motion.length));
        if (reticle != null)
        {
            float cycle = Mathf.Repeat(Mathf.Max(0, seconds), motion.length);
            var tint = SongSelectSkin.Cyan;
            tint.a = Mathf.SmoothStep(.15f, .9f, (cycle - .5f) / .85f) * (1 - Mathf.SmoothStep(0, .85f, (cycle - 4.2f) / .9f));
            reticle.color = tint;
        }
        RenderPipeline.SubmitRenderRequest(previewCamera,
            new UniversalRenderPipeline.SingleCameraRequest { destination = target });
    }

    bool EnsureModel()
    {
        if (IsReady) return true;
        if (human != null && motion != null && target != null)
        {
            target.Create(); SetMaterialDirty();
            return target.IsCreated();
        }
        if (loadFailed) return false;
        var prefab = Resources.Load<GameObject>(AssetPath);
        foreach (var clip in Resources.LoadAll<AnimationClip>(AssetPath))
            if (!clip.name.StartsWith("__preview__")) { motion = clip; break; }
        var shader = Resources.Load<Shader>("UI/SongSelectHuman/GuideStudio");
        if (prefab == null || motion == null || shader == null)
        {
            loadFailed = true;
            Debug.LogError("右手の案内用モデル・アニメーション・シェーダーが見つかりません。", this);
            return false;
        }

        // メインカメラのレイヤーや照明には手を加えず、遠方の専用ステージに隔離する。
        stage = new GameObject("SongSelectHumanStage");
        stage.transform.position = new Vector3(2000, 2000, 2000);
        human = Instantiate(prefab, stage.transform, false);
        human.name = "RightHandHuman";
        foreach (var transform in human.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = PreviewLayer;
        foreach (var animator in human.GetComponentsInChildren<Animator>()) animator.enabled = false;
        foreach (var animation in human.GetComponentsInChildren<Animation>()) animation.enabled = false;
        var palette = new Dictionary<string, Material>();
        foreach (var renderer in human.GetComponentsInChildren<Renderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                string key = materials[i] != null ? materials[i].name : "Guide_Pearl";
                if (!palette.TryGetValue(key, out var material))
                {
                    var tint = new Color(.78f, .88f, .94f);
                    float emission = 0;
                    if (key.Contains("Graphite")) tint = new Color(.12f, .22f, .29f);
                    if (key.Contains("Coral")) tint = new Color(1, .22f, .34f);
                    if (key.Contains("Blade")) { tint = new Color(1, .12f, .25f); emission = .65f; }
                    if (key.Contains("Core")) { tint = new Color(1, .83f, .87f); emission = 1; }
                    material = new Material(shader) { name = key + " (Guide)" };
                    material.SetColor("_BaseColor", tint);
                    material.SetFloat("_Emission", emission);
                    palette.Add(key, material); ownedMaterials.Add(material);
                }
                materials[i] = material;
            }
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.updateWhenOffscreen = true;
                // 同じフレーム内の任意時刻描画でも、表面を最新の骨格に追従させる。
                skinned.forceMatrixRecalculationPerRender = true;
            }
        }

        var cameraObject = new GameObject("GuideCamera");
        cameraObject.transform.SetParent(stage.transform, false);
        previewCamera = cameraObject.AddComponent<Camera>();
        previewCamera.enabled = false;
        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = Color.clear;
        previewCamera.cullingMask = 1 << PreviewLayer;
        previewCamera.orthographic = true;
        previewCamera.orthographicSize = 1.325f;
        previewCamera.aspect = 420f / 440;
        previewCamera.nearClipPlane = .1f; previewCamera.farClipPlane = 12;
        previewCamera.allowHDR = false; previewCamera.allowMSAA = true;
        cameraObject.transform.localPosition = new Vector3(3, 2.7f, -5);
        cameraObject.transform.LookAt(stage.transform.position + new Vector3(.08f, 1.14f, 0));
        var cameraData = previewCamera.GetUniversalAdditionalCameraData();
        cameraData.renderPostProcessing = false;
        cameraData.renderShadows = false;
        cameraData.requiresColorOption = CameraOverrideOption.Off;
        cameraData.requiresDepthOption = CameraOverrideOption.Off;

        target = new RenderTexture(640, 672, 24, RenderTextureFormat.ARGB32)
        { name = "RightHandGuidePreview", antiAliasing = 2, filterMode = FilterMode.Bilinear };
        target.Create();
        previewCamera.targetTexture = target;
        reticle = SongSelectSkin.Graphic(transform, "DemonstrationAim", new Vector2(86, 174), new Vector2(46, 46), SongSelectDiscGraphic.Shape.Ring, SongSelectSkin.Cyan);
        reticle.Width = 2;
        SetMaterialDirty(); SetVerticesDirty();
        return true;
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        if (stage != null) stage.SetActive(true);
    }
    protected override void OnDisable()
    {
        if (stage != null) stage.SetActive(false);
        base.OnDisable();
    }
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (!IsReady) return;
        var rect = GetPixelAdjustedRect();
        vh.AddVert(new Vector3(rect.xMin, rect.yMin), color, new Vector2(0, 0));
        vh.AddVert(new Vector3(rect.xMin, rect.yMax), color, new Vector2(0, 1));
        vh.AddVert(new Vector3(rect.xMax, rect.yMax), color, new Vector2(1, 1));
        vh.AddVert(new Vector3(rect.xMax, rect.yMin), color, new Vector2(1, 0));
        vh.AddTriangle(0, 1, 2); vh.AddTriangle(2, 3, 0);
    }
    protected override void OnDestroy()
    {
        if (previewCamera != null) previewCamera.targetTexture = null;
        if (target != null) { target.Release(); Destroy(target); }
        if (stage != null) Destroy(stage);
        foreach (var material in ownedMaterials) if (material != null) Destroy(material);
        base.OnDestroy();
    }
}
