using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 判定ロジックから独立した金属のゲート。コライダーも中央の面も生成しない。
// 光る細線と、実際の厚みを持つ外側の筐体を分ける。拍パルスは細線だけを駆動する。
[ExecuteAlways]
public sealed class JudgeGateFrame : MonoBehaviour
{
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<Material> materials = new List<Material>();
    private MeshRenderer panelRenderer;
    private Material originalPanelMaterial;
    private Material panelCopy;
    private bool built;

    public void OwnPanelMaterial(MeshRenderer renderer, Material original, Material replacement)
    {
        panelRenderer = renderer; originalPanelMaterial = original; panelCopy = replacement;
        if (replacement != null) materials.Add(replacement);
    }

    public void Build(Vector3 panelSize)
    {
        if (built) return;
        built = true;
        float hw = Mathf.Abs(panelSize.x) * .5f, hh = Mathf.Abs(panelSize.y) * .5f;
        float cut = Mathf.Min(.31f, Mathf.Min(hw, hh) * .18f);
        float t = GameStageSkin.GateBarThickness;
        var metal = MakeSurface("GraphiteHousing", new Color(.062f, .092f, .118f), .05f);
        var edge = MakeSurface("SatinBevel", new Color(.13f, .18f, .21f), .12f);
        var light = MakeSurface("BeatInlay", GameStageSkin.GateColor, GameStageSkin.GateEmission);
        var corner = MakeSurface("CornerInlay", GameStageSkin.GateCornerColor, GameStageSkin.GateCornerEmission);
        var body = new StageGeometry(); var bevel = new StageGeometry();
        // 判定領域の外側へ筐体を張り出し、ノーツの入射域は狭めない。
        Octagon(body, hw + .155f, hh + .155f, cut + .10f, .18f, .20f, .055f);
        Octagon(bevel, hw + .255f, hh + .255f, cut + .15f, .035f, .06f, -.04f);
        Emit("GateHousing", body, metal); Emit("GateOuterBevel", bevel, edge);

        // 中央を少し切り欠いた細いガイド線。GateTop等の名前と共有材質は拍同期との契約。
        foreach (int sign in new[] { -1, 1 })
        {
            var horizontal = new StageGeometry(); var vertical = new StageGeometry();
            foreach (int segment in new[] { -1, 1 })
            {
                float inner = Mathf.Min(.30f, hw * .1f), outer = hw - cut;
                horizontal.Box(new Vector3(segment * (inner + outer) * .5f, sign * hh, -.065f), new Vector3(outer - inner, t, .025f));
                inner = Mathf.Min(.18f, hh * .1f); outer = hh - cut;
                vertical.Box(new Vector3(sign * hw, segment * (inner + outer) * .5f, -.065f), new Vector3(t, outer - inner, .025f));
            }
            Emit(sign > 0 ? "GateTop" : "GateBottom", horizontal, light);
            Emit(sign > 0 ? "GateRight" : "GateLeft", vertical, light);
        }

        var joints = new StageGeometry(); var marks = new StageGeometry();
        float cl = Mathf.Min(GameStageSkin.GateCornerLength, Mathf.Min(hw, hh) * .28f);
        foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
            {
                string tag = (sy > 0 ? "T" : "B") + (sx > 0 ? "R" : "L");
                Vector3 a = new Vector3(sx * (hw - cut), sy * hh, -.07f);
                Vector3 b = new Vector3(sx * hw, sy * (hh - cut), -.07f);
                var chamfer = new StageGeometry(); chamfer.Beam(a, b, .032f, .028f);
                // 四隅の白ブラケットは短く細く。大きな白い塊を作らない。
                chamfer.Beam(a, a + Vector3.left * sx * cl, .032f, .028f);
                Emit("GateCorner" + tag + "_h", chamfer, corner);
                var upright = new StageGeometry(); upright.Beam(b, b + Vector3.down * sy * cl, .032f, .028f);
                Emit("GateCorner" + tag + "_v", upright, corner);
                joints.Panel(new Vector3(sx * (hw + .17f), sy * (hh - cut - .15f), -.035f),
                    new Vector3(.26f, .65f, .16f), Quaternion.identity, .10f);
                for (int tick = 0; tick < 3; tick++)
                    marks.Box(new Vector3(sx * (hw + .17f), sy * (hh - cut - .05f - tick * .085f), -.124f),
                        new Vector3(.095f, .015f, .008f));
            }
        Emit("GateJointArmor", joints, metal); Emit("GateRegistrationTicks", marks, light);
    }

    private static void Octagon(StageGeometry geo, float hw, float hh, float cut, float width, float depth, float z)
    {
        var p = new[] { new Vector3(-hw + cut, hh, z), new Vector3(hw - cut, hh, z),
            new Vector3(hw, hh - cut, z), new Vector3(hw, -hh + cut, z), new Vector3(hw - cut, -hh, z),
            new Vector3(-hw + cut, -hh, z), new Vector3(-hw, -hh + cut, z), new Vector3(-hw, hh - cut, z) };
        for (int i = 0; i < p.Length; i++) geo.Beam(p[i], p[(i + 1) % p.Length], width, depth);
    }

    private Material MakeSurface(string name, Color color, float emission)
    {
        var shader = Resources.Load<Shader>("Stage/ObsidianMetal")
            ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { name = "JudgeGate/" + name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * emission);
        }
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .45f);
        materials.Add(material); return material;
    }

    private void Emit(string name, StageGeometry geometry, Material material)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(transform, false);
        var mesh = geometry.CreateMesh(name); meshes.Add(mesh);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private void OnDestroy()
    {
        if (panelRenderer != null && panelRenderer.sharedMaterial == panelCopy) panelRenderer.sharedMaterial = originalPanelMaterial;
        foreach (var mesh in meshes) Release(mesh);
        foreach (var material in materials) Release(material);
        meshes.Clear(); materials.Clear();
    }

    private static void Release(Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }
}
