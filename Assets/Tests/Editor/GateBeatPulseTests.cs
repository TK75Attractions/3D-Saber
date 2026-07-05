using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class GateBeatPulseTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        var pulse = Object.FindFirstObjectByType<GateBeatPulse>();
        if (pulse != null) Object.DestroyImmediate(pulse.gameObject);
    }

    // ---- 純関数 Intensity01 ----

    [Test]
    public void Intensity_IsOne_AtBeatInstant()
    {
        // BPM120 → 拍間隔 0.5s。t=0(最初の拍)と t=0.5(次の拍)で強度 1
        Assert.AreEqual(1f, GateBeatPulse.Intensity01(0.0, 120f, 0.0, 0.12f), 0.001f);
        Assert.AreEqual(1f, GateBeatPulse.Intensity01(0.5, 120f, 0.0, 0.12f), 0.001f);
    }

    [Test]
    public void Intensity_DecaysToZero_AfterDecaySeconds()
    {
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(0.12, 120f, 0.0, 0.12f), 0.001f);
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(0.3, 120f, 0.0, 0.12f), 0.001f);
    }

    [Test]
    public void Intensity_LinearMidpoint()
    {
        Assert.AreEqual(0.5f, GateBeatPulse.Intensity01(0.06, 120f, 0.0, 0.12f), 0.001f);
    }

    [Test]
    public void Intensity_ZeroBeforeSongStart()
    {
        // オフセット前(曲開始前)は光らない
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(-0.1, 120f, 0.0, 0.12f), 0.001f);
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(0.3, 120f, 0.5, 0.12f), 0.001f);
    }

    [Test]
    public void Intensity_RespectsOffset()
    {
        // オフセット 0.5s → 拍は 0.5, 1.0, ... に来る
        Assert.AreEqual(1f, GateBeatPulse.Intensity01(0.5, 120f, 0.5, 0.12f), 0.001f);
        Assert.AreEqual(1f, GateBeatPulse.Intensity01(1.0, 120f, 0.5, 0.12f), 0.001f);
    }

    [Test]
    public void Intensity_InvalidInputs_ReturnZero()
    {
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(1.0, 0f, 0.0, 0.12f), 0.001f);
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(1.0, -10f, 0.0, 0.12f), 0.001f);
        Assert.AreEqual(0f, GateBeatPulse.Intensity01(1.0, 120f, 0.0, 0f), 0.001f);
    }

    // ---- Ensure ----

    private GameObject MakeGateViaStageSkin()
    {
        var guide = new GameObject("JudgeGuide");
        created.Add(guide);
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "JudgePanel";
        panel.transform.SetParent(guide.transform, false);
        panel.transform.localScale = new Vector3(7f, 4f, 0.1f);
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        panel.GetComponent<MeshRenderer>().sharedMaterial = new Material(sh);
        GameStageSkin.RestyleJudgeGuide(guide);
        return guide;
    }

    [Test]
    public void Ensure_AttachesToJudgeGate()
    {
        MakeGateViaStageSkin();
        var pulse = GateBeatPulse.Ensure(150f, 0.25, null);
        Assert.IsNotNull(pulse, "ゲートがあればパルスが付く");
        Assert.AreEqual(150f, pulse.bpm, 0.001f);
        Assert.AreEqual(0.25, pulse.offsetSeconds, 0.0001);
    }

    [Test]
    public void Ensure_IsIdempotent_AndUpdatesParams()
    {
        MakeGateViaStageSkin();
        var a = GateBeatPulse.Ensure(120f, 0.0, null);
        var b = GateBeatPulse.Ensure(90f, 0.1, null);
        Assert.AreSame(a, b, "二重生成しない");
        Assert.AreEqual(90f, b.bpm, 0.001f, "パラメータは更新される");
    }

    [Test]
    public void Ensure_ReturnsNull_WithoutGate()
    {
        Assert.IsNull(GateBeatPulse.Ensure(120f, 0.0, null), "ゲートが無ければ何もしない");
    }
}
