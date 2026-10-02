using UnityEngine;
using UnityEngine.Rendering;

// 写真調の遠景と自然現象を一枚の描画にまとめる。曲時計だけで動き、判定空間には物体を置かない。
[ExecuteAlways]
public sealed class PhotographicStage : MonoBehaviour
{
    public StageTheme Theme { get; private set; }
    public double LastTickSeconds { get; private set; }
    private Mesh mesh;
    private Material material;
    private static readonly int MotionTime = Shader.PropertyToID("_MotionTime");
    private static readonly int Chorus = Shader.PropertyToID("_Chorus");
    private static readonly int Effects = Shader.PropertyToID("_Effects");

    public static PhotographicStage Create(FloorRenderer owner)
    {
        if (owner == null || !StageThemeCatalog.IsPhotographic(owner.ActiveTheme)) return null;
        var existing = owner.GetComponentInChildren<PhotographicStage>();
        if (existing != null) return existing;
        var go = new GameObject("PhotographicStage", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(owner.transform, false);
        var stage = go.AddComponent<PhotographicStage>();
        stage.Theme = owner.ActiveTheme;
        stage.Build();
        return stage;
    }

    private void Build()
    {
        var shader = Resources.Load<Shader>("Stage/PhotographicBackdrop");
        var texture = Resources.Load<Texture2D>("Stage/Photographic/" + Theme);
        if (shader == null || texture == null)
        {
            Debug.LogError("写真背景の素材がありません: " + Theme, this);
            return;
        }
        material = new Material(shader) { name = Theme + "/Backdrop" };
        material.SetTexture("_BackgroundTex", texture);
        material.SetFloat("_Style", (int)Theme - (int)StageTheme.AuroraLake);
        mesh = new Mesh { name = Theme + "/ScreenQuad" };
        mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        mesh.triangles = new[] { 0,2,1,0,3,2 };
        mesh.RecalculateNormals();
        // 頂点はシェーダーで画面全体へ投影。カメラ姿勢・解像度に依存した欠けを防ぐ。
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20000f);
        GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingOrder = -32000;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.allowOcclusionWhenDynamic = false;
        Tick(0);
    }

    public void Tick(double songSeconds, float chorus = 0)
    {
        if (!isActiveAndEnabled || material == null || double.IsNaN(songSeconds) || double.IsInfinity(songSeconds)) return;
        LastTickSeconds = System.Math.Max(0, songSeconds);
        material.SetFloat(MotionTime, (float)LastTickSeconds);
        material.SetFloat(Chorus, float.IsNaN(chorus) || float.IsInfinity(chorus) ? 0 : Mathf.Clamp01(chorus));
        material.SetFloat(Effects, DisplaySettings.AccentScale);
    }

    private void OnDestroy()
    {
        // テクスチャはResources所有。各ステージが生成した材質とメッシュだけを解放する。
        Release(mesh);
        Release(material);
    }

    private static void Release(Object asset)
    {
        if (asset == null) return;
        if (Application.isPlaying) Destroy(asset); else DestroyImmediate(asset);
    }
}
