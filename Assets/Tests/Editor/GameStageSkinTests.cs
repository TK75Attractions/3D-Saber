using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class GameStageSkinTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    private bool savedFog;
    private FogMode savedFogMode;
    private float savedFogDensity;
    private Color savedFogColor;

    [SetUp]
    public void SaveRenderSettings()
    {
        savedFog = RenderSettings.fog;
        savedFogMode = RenderSettings.fogMode;
        savedFogDensity = RenderSettings.fogDensity;
        savedFogColor = RenderSettings.fogColor;
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        RenderSettings.fog = savedFog;
        RenderSettings.fogMode = savedFogMode;
        RenderSettings.fogDensity = savedFogDensity;
        RenderSettings.fogColor = savedFogColor;
    }

    // 実シーンの JudgeGuide を模した合成ガイドを作る。
    private (GameObject guide, Transform panel) MakeSyntheticGuide()
    {
        var guide = new GameObject("JudgeGuide");
        created.Add(guide);

        // 旧テーマの格子・枠(剥がされるべき子)
        foreach (var name in new[] { "GridV1", "GridH2", "BorderTop", "CornerTL_v", "CrossH" })
        {
            var deco = new GameObject(name);
            deco.transform.SetParent(guide.transform, false);
        }

        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "JudgePanel";
        panel.transform.SetParent(guide.transform, false);
        panel.transform.localScale = new Vector3(7f, 4f, 0.1f);
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        panel.GetComponent<MeshRenderer>().sharedMaterial = new Material(sh);

        return (guide, panel.transform);
    }

    // ---- 判定ゲート ----

    [Test]
    public void RestyleJudgeGuide_StripsLegacyChildren()
    {
        var (guide, _) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        foreach (Transform child in guide.transform)
        {
            foreach (var prefix in new[] { "GridV", "GridH", "Border", "Corner", "Cross" })
            {
                Assert.IsFalse(child.name.StartsWith(prefix), $"旧装飾が残っている: {child.name}");
            }
        }
    }

    [Test]
    public void RestyleJudgeGuide_BuildsGateWithFourBars()
    {
        var (guide, _) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        var gate = guide.transform.Find("JudgeGate");
        Assert.IsNotNull(gate, "JudgeGate が生成される");
        Assert.IsNotNull(gate.Find("GateTop"));
        Assert.IsNotNull(gate.Find("GateBottom"));
        Assert.IsNotNull(gate.Find("GateLeft"));
        Assert.IsNotNull(gate.Find("GateRight"));
    }

    [Test]
    public void RestyleJudgeGuide_BuildsEightCornerBrackets()
    {
        var (guide, _) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        var gate = guide.transform.Find("JudgeGate");
        Assert.IsNotNull(gate);
        int corners = 0;
        foreach (Transform child in gate)
        {
            if (child.name.StartsWith("GateCorner")) corners++;
        }
        Assert.AreEqual(8, corners, "四隅 x (縦+横) = 8 本のブラケット");
    }

    [Test]
    public void RestyleJudgeGuide_IsIdempotent()
    {
        var (guide, _) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        GameStageSkin.RestyleJudgeGuide(guide);
        int gates = 0;
        foreach (Transform child in guide.transform)
        {
            if (child.name == "JudgeGate") gates++;
        }
        Assert.AreEqual(1, gates, "二重適用してもゲートは1つ");
    }

    [Test]
    public void RestyleJudgeGuide_StripsCollidersFromGateParts()
    {
        var (guide, _) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        var gate = guide.transform.Find("JudgeGate");
        foreach (var col in gate.GetComponentsInChildren<Collider>(true))
        {
            Assert.Fail($"ゲート部品に Collider が残っている: {col.name}");
        }
    }

    [Test]
    public void RestyleJudgeGuide_RemovesOldGlowAndBeatLine()
    {
        var (guide, _) = MakeSyntheticGuide();
        // 旧テーマの残骸を再現
        var glow = new GameObject("JudgePanelGlow");
        glow.transform.SetParent(guide.transform, false);
        var beat = new GameObject("BeatReferenceLine");
        beat.transform.SetParent(guide.transform, false);

        GameStageSkin.RestyleJudgeGuide(guide);

        Assert.IsNull(guide.transform.Find("JudgePanelGlow"), "旧グローは除去される");
        Assert.IsNull(guide.transform.Find("BeatReferenceLine"), "旧ビート線は除去される");
    }

    [Test]
    public void RestyleJudgeGuide_DimsPanelFill()
    {
        var (guide, panel) = MakeSyntheticGuide();
        GameStageSkin.RestyleJudgeGuide(guide);
        var m = panel.GetComponent<MeshRenderer>().sharedMaterial;
        float alpha = 1f;
        if (m.HasProperty("_BaseColor")) alpha = m.GetColor("_BaseColor").a;
        else if (m.HasProperty("_Color")) alpha = m.color.a;
        Assert.AreEqual(GameStageSkin.PanelFillAlpha, alpha, 0.001f, "パネルの面はほぼ透明化される");
    }

    [Test]
    public void RestyleJudgeGuide_NullGuide_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => GameStageSkin.RestyleJudgeGuide(null));
    }

    [Test]
    public void RestyleJudgeGuide_GuideWithoutPanel_DoesNotThrow()
    {
        var guide = new GameObject("JudgeGuide");
        created.Add(guide);
        Assert.DoesNotThrow(() => GameStageSkin.RestyleJudgeGuide(guide));
        Assert.IsNull(guide.transform.Find("JudgeGate"), "パネルが無ければゲートは建てない");
    }

    // ---- カメラ+フォグ ----

    [Test]
    public void ApplyCameraAndFog_EnablesExponentialFog()
    {
        GameStageSkin.ApplyCameraAndFog(null); // カメラ無しでもフォグは設定される
        Assert.IsTrue(RenderSettings.fog);
        Assert.AreEqual(FogMode.Exponential, RenderSettings.fogMode);
        Assert.AreEqual(GameStageSkin.FogDensity, RenderSettings.fogDensity, 0.0001f);
        Assert.AreEqual(GameStageSkin.BackgroundColor.r, RenderSettings.fogColor.r, 0.001f);
    }

    [Test]
    public void ApplyCameraAndFog_SetsCameraBackground()
    {
        var camGo = new GameObject("cam");
        created.Add(camGo);
        var cam = camGo.AddComponent<Camera>();
        GameStageSkin.ApplyCameraAndFog(cam);
        Assert.AreEqual(CameraClearFlags.SolidColor, cam.clearFlags);
        Assert.AreEqual(GameStageSkin.BackgroundColor.r, cam.backgroundColor.r, 0.001f);
        Assert.AreEqual(GameStageSkin.BackgroundColor.b, cam.backgroundColor.b, 0.001f);
    }

    [Test]
    public void BarLine_IsDarkerThanGate()
    {
        // 視覚ヒエラルキー:小節線はゲートより暗い(=アルファが小さい)こと
        Assert.Less(GameStageSkin.BarLineAlpha, 0.5f);
    }
}
