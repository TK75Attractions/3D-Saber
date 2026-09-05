using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Obsidian Relay: 段差のあるデッキと、奥へ重なる側壁で距離を表す。
// 既存+追加3種の静的環境。ノーツ・HUDには触れず、中央は空け、光は床端と側壁に置く。
// 環境は静的な結合メッシュ。パーツ数に比例した描画呼び出しや Update を増やさない。
// EditMode の確認で生成した一時資源も OnDestroy で解放する。自動生成・毎フレーム処理はしない。
[ExecuteAlways]
public partial class FloorRenderer : MonoBehaviour
{
    [Header("Stage variation")]
    public StageTheme theme = StageTheme.ObsidianRelay;
    public bool randomizeOnPlay = true;
    public StageTheme ActiveTheme { get; private set; }
    [Header("Floor / Ceiling extents")]
    public float floorY = -2.5f;
    public float ceilingY = 3f;
    public float minX = -8f;
    public float maxX = 8f;
    public float minZ = -3f;
    public float maxZ = 22f;
    [Header("Colors")]
    public Color baseColor = new Color(.023f, .035f, .049f, 1f);
    public Color lineColor = new Color(.065f, .30f, .37f, 1f);
    public Color brightLineColor = new Color(.19f, .53f, .61f, 1f);
    [Header("Lane lines (vertical strips along Z)")]
    public float[] laneXPositions = { -3f, 0f, 3f };
    public float laneLineThickness = .024f;
    public float laneLineEmission = .30f;
    [Header("Depth marks (outside the central lane)")]
    public float depthLineSpacing = 4f;
    public float depthLineThickness = .025f;
    public float depthLineEmission = .22f;
    public float judgeDepthLineEmission = .45f;
    [Header("Ceiling (optional; off by default)")]
    public bool addCeiling = false;
    public float ceilingDimming = .4f;
    [Header("Floor base")]
    public bool addFloorBase = true;
    [Header("Side architecture")]
    public bool addSideArchitecture = true;
    public float wallBaySpacing = 5f;
    public float distantExtension = 16f;

    public const float ClearCorridorHalfWidth = 5.8f;
    // 背景だけの明度補正。ノーツ・HUD・全画面露出には触れない。
    public const float SurfaceBrightness = 1.75f;
    public const float InlayBrightness = 1.18f;
    private bool built;
    private readonly List<Material> materials = new List<Material>();
    private readonly List<Mesh> meshes = new List<Mesh>();

    public static FloorRenderer Ensure(Transform parent = null)
    {
        var existing = Object.FindFirstObjectByType<FloorRenderer>();
        if (existing != null) { existing.Build(); return existing; }
        var go = new GameObject("FloorRenderer");
        if (parent != null) go.transform.SetParent(parent, false);
        var renderer = go.AddComponent<FloorRenderer>();
        renderer.Build();
        return renderer;
    }

    // 二重適用しても環境・マテリアルを増やさない。
    public void Build()
    {
        if (built) return;
        Build(Application.isPlaying && randomizeOnPlay ? StageThemeCatalog.NextForPlay() : theme);
    }

    // 明示指定はプレビュー/Inspector用。通常プレイでは上の入口で開始時に一度だけ抽選する。
    public void Build(StageTheme selectedTheme)
    {
        if (built) return;
        ActiveTheme = (int)selectedTheme >= 0 && (int)selectedTheme < StageThemeCatalog.Count
            ? selectedTheme : StageTheme.ObsidianRelay;
        built = true;
        bool original = ActiveTheme == StageTheme.ObsidianRelay;
        float farZ = maxZ + Mathf.Max(0f, distantExtension);
        float width = Mathf.Max(1f, maxX - minX);
        float centerX = (minX + maxX) * .5f;
        float centerZ = (minZ + farZ) * .5f;
        float depth = Mathf.Max(1f, farZ - minZ);
        var baseMat = Surface("Foundation", baseColor, .18f, .02f);
        Color tint = StageThemeCatalog.MetalTint(ActiveTheme);
        Color accent = original ? brightLineColor : StageThemeCatalog.Accent(ActiveTheme);
        var deckMat = Surface("GraphiteDeck", tint, .38f, .04f);
        var edgeMat = Surface("MachinedEdge", original ? new Color(.100f, .139f, .172f) : tint * 1.64f, .45f, .04f);
        var recessMat = Surface("Recess", new Color(.009f, .017f, .023f), .12f, .015f);
        var wallMat = Surface("WallPanels", original ? new Color(.042f, .064f, .086f) : tint * .8f, .30f, .055f);
        var ribMat = Surface("StructuralRibs", original ? new Color(.076f, .102f, .125f) : tint * 1.32f, .38f, .06f);
        var guideMat = Surface("MutedLane", original ? lineColor : accent * .48f, .25f, laneLineEmission);
        var lightMat = Surface("IceInlay", accent, .30f, .52f);
        var amberMat = Surface("AmberServiceTabs", new Color(.38f, .215f, .073f), .20f, .25f);

        if (addFloorBase)
        {
            var foundation = new StageGeometry();
            foundation.Box(new Vector3(centerX, floorY - .16f, centerZ), new Vector3(width, .28f, depth));
            Emit("FloorBase", foundation, baseMat);
        }
        var panels = new StageGeometry();
        var sideDecks = new StageGeometry();
        var recesses = new StageGeometry();
        var deckInlays = new StageGeometry();
        var serviceTabs = new StageGeometry();
        var variantDetails = new StageGeometry();
        Quaternion horizontal = Quaternion.Euler(90f, 0f, 0f);
        float slabSpacing = Mathf.Max(2f, depthLineSpacing);
        for (float z = minZ; z < farZ - .05f; z += slabSpacing)
        {
            float length = Mathf.Min(slabSpacing, farZ - z);
            float mid = z + length * .5f;
            // 全面を発光する格子ではなく、大きな面の継ぎ目と面取りで床を見せる。
            if (original)
                foreach (float x in new[] { -4.47f, -1.49f, 1.49f, 4.47f })
                    panels.Panel(new Vector3(x, floorY - .018f, mid),
                        new Vector3(2.90f, Mathf.Max(.1f, length - .11f), .10f), horizontal, .17f);
            else BuildVariantFloorBay(panels, variantDetails, z, length);
            foreach (int side in new[] { -1, 1 })
            {
                sideDecks.Panel(new Vector3(side * 6.94f, floorY + .045f, mid),
                    new Vector3(1.77f, Mathf.Max(.1f, length - .10f), .28f), horizontal, .22f);
                recesses.Box(new Vector3(side * 6.09f, floorY + .12f, mid), new Vector3(.17f, .06f, length - .13f));
                deckInlays.Box(new Vector3(side * 6.09f, floorY + .154f, mid), new Vector3(.038f, .018f, length * .72f));
                // 通路端の排熱溝。短い影の反復で縮尺を示す。
                for (int slit = 0; slit < 5; slit++)
                    recesses.Box(new Vector3(side * 6.85f, floorY + .194f, mid - .38f + slit * .19f),
                        new Vector3(.60f, .014f, .042f));
                if (Mathf.RoundToInt((z - minZ) / slabSpacing) % 3 == 0)
                    serviceTabs.Box(new Vector3(side * 7.42f, floorY + .194f, mid), new Vector3(.10f, .016f, .42f));
            }
        }
        Emit("FloorPanels", panels, deckMat);
        Emit("RaisedSideDecks", sideDecks, edgeMat);
        Emit("FloorRecesses", recesses, recessMat);
        Emit("FloorEdgeInlays", deckInlays, lightMat);
        Emit("FloorServiceTabs", serviceTabs, amberMat);
        Emit("FloorVariantInlays", variantDetails, guideMat);
        BuildLaneLines(floorY + .037f, minZ, farZ, guideMat, "FloorLane_");
        BuildDepthMarks(floorY + .04f, minZ, farZ, guideMat, "FloorDepth_");
        if (addCeiling)
        {
            var ceilingMat = Surface("OptionalCeiling", lineColor * ceilingDimming, .2f, depthLineEmission);
            BuildLaneLines(ceilingY, minZ, farZ, ceilingMat, "CeilLane_");
            BuildDepthMarks(ceilingY, minZ, farZ, ceilingMat, "CeilDepth_");
        }
        if (addSideArchitecture)
        {
            if (original) BuildWalls(farZ, wallMat, ribMat, recessMat, lightMat, amberMat);
            else BuildVariantWalls(farZ, wallMat, ribMat, recessMat, lightMat, amberMat);
        }
    }

    private void BuildLaneLines(float y, float near, float far, Material material, string prefix)
    {
        if (laneXPositions == null) return;
        foreach (float x in laneXPositions)
        {
            var geometry = new StageGeometry();
            // 中央は一段細くし、ノーツの真下に強い光を作らない。
            float thickness = laneLineThickness * (Mathf.Abs(x) < .01f ? .55f : 1f);
            geometry.Box(new Vector3(x, y, (near + far) * .5f), new Vector3(thickness, .008f, far - near));
            Emit($"{prefix}x{x:F1}", geometry, material);
        }
    }

    private void BuildDepthMarks(float y, float near, float far, Material material, string prefix)
    {
        for (float z = near; z <= far; z += Mathf.Max(2f, depthLineSpacing))
        {
            var geometry = new StageGeometry();
            // 横線は左右の肩だけ。中央を横切る輝線は既存の小節線に任せる。
            foreach (int side in new[] { -1, 1 })
                geometry.Box(new Vector3(side * 4.93f, y, z + .04f), new Vector3(1.72f, .008f, depthLineThickness));
            Emit($"{prefix}z{z:F1}", geometry, material);
        }
    }

    private void BuildWalls(float farZ, Material panelMat, Material ribMat, Material darkMat, Material lightMat, Material amberMat)
    {
        var panels = new StageGeometry(); var insets = new StageGeometry(); var ribs = new StageGeometry();
        var trims = new StageGeometry(); var tabs = new StageGeometry();
        float spacing = Mathf.Max(3.5f, wallBaySpacing);
        for (float z = minZ; z < farZ - .1f; z += spacing)
        {
            float length = Mathf.Min(spacing, farZ - z);
            if (length < 1.2f) continue;
            float mid = z + length * .5f;
            int bay = Mathf.RoundToInt((z - minZ) / spacing);
            foreach (int side in new[] { -1, 1 })
            {
                Quaternion inward = Quaternion.Euler(0f, side * 90f, 0f);
                panels.Panel(new Vector3(side * 8.03f, floorY + 2.95f, mid),
                    new Vector3(length - .18f, 5.35f, .34f), inward, .36f);
                // 背板・くぼみ・縁の三層を実際に離す。視点に応じた重なりが生まれる。
                insets.Panel(new Vector3(side * 7.83f, floorY + 2.8f, mid),
                    new Vector3(length - .65f, 2.26f, .12f), inward, .28f);
                panels.Panel(new Vector3(side * 7.72f, floorY + 2.72f, mid),
                    new Vector3(length - .96f, 1.82f, .14f), inward, .24f);
                Vector3 foot = new Vector3(side * 7.25f, floorY + .18f, z + .14f);
                Vector3 elbow = new Vector3(side * 7.70f, floorY + 3.88f, z + .14f);
                Vector3 crown = new Vector3(side * 6.77f, floorY + 6.08f, z + .14f);
                ribs.Beam(foot, elbow, .28f, .43f);
                ribs.Beam(elbow, crown, .25f, .43f);
                ribs.Box(new Vector3(side * 7.29f, floorY + .28f, z + .14f), new Vector3(.78f, .48f, .73f));
                // 支柱全体ではなく、下半分と上の肩に短い埋め込み灯。
                trims.Beam(foot + new Vector3(-side * .16f, .44f, -.23f),
                    Vector3.Lerp(foot, elbow, .58f) + new Vector3(-side * .16f, 0f, -.23f), .025f, .018f);
                trims.Beam(Vector3.Lerp(elbow, crown, .44f) + new Vector3(-side * .14f, 0f, -.23f),
                    Vector3.Lerp(elbow, crown, .75f) + new Vector3(-side * .14f, 0f, -.23f), .023f, .018f);
                trims.Box(new Vector3(side * 7.61f, floorY + 1.66f, mid), new Vector3(.018f, .027f, length * .47f));
                ribs.Box(new Vector3(side * 7.76f, floorY + .77f, mid), new Vector3(.42f, .23f, length - .10f));
                if (bay % 2 == 0)
                    for (int tick = 0; tick < 3; tick++)
                        tabs.Box(new Vector3(side * 7.615f, floorY + 3.74f, mid - .2f + tick * .20f), new Vector3(.018f, .08f, .09f));
            }
        }
        Emit("WallPanels", panels, panelMat);
        Emit("WallRecesses", insets, darkMat);
        Emit("WallStructuralRibs", ribs, ribMat);
        Emit("WallLightInlays", trims, lightMat);
        Emit("WallServiceTabs", tabs, amberMat);
    }

    private Material Surface(string name, Color color, float smoothness, float emission)
    {
        // Resources 参照でビルド時のシェーダー除外を防ぐ。既存の2D Rendererも維持する。
        var shader = Resources.Load<Shader>("Stage/ObsidianMetal")
            ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { name = StageThemeCatalog.DisplayName(ActiveTheme) + "/" + name };
        Color surface = color * SurfaceBrightness;
        surface.a = 1f;
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", surface);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", surface);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", .32f);
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * emission * InlayBrightness);
        }
        materials.Add(material);
        return material;
    }

    private void Emit(string name, StageGeometry geometry, Material material)
    {
        if (geometry.VertexCount == 0) return;
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        Mesh mesh = geometry.CreateMesh(name);
        meshes.Add(mesh);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        // 最初から Collider を作らず、セーバーの衝突・判定へ干渉させない。
    }

    private void OnDestroy()
    {
        foreach (var mesh in meshes) Release(mesh);
        foreach (var material in materials) Release(material);
        meshes.Clear(); materials.Clear();
    }

    private static void Release(Object item)
    {
        if (item == null) return;
        if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
    }

}
