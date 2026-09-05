using NUnit.Framework;
using UnityEngine;

public class GatePerfectPulseTests
{
    private GameObject guide;
    private ScoreManager score;
    private Material panelMaterial;
    private GateBeatPulse pulse;

    [SetUp]
    public void SetUp()
    {
        guide = new GameObject("JudgeGuide");
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "JudgePanel";
        panel.transform.SetParent(guide.transform, false);
        panel.transform.localScale = new Vector3(7, 4, .1f);
        panelMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        panel.GetComponent<MeshRenderer>().sharedMaterial = panelMaterial;
        GameStageSkin.RestyleJudgeGuide(guide);
        score = new GameObject("ScoreForGate").AddComponent<ScoreManager>();
        pulse = GateBeatPulse.Ensure(120, 0, null, score);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(guide);
        Object.DestroyImmediate(score.gameObject);
        Object.DestroyImmediate(panelMaterial);
    }

    [Test]
    public void ClockAloneNeverFlashes()
    {
        foreach (double t in new[] { 0d, .5, 1, 2, 100 })
        {
            pulse.Tick(t);
            Assert.AreEqual(0f, pulse.CurrentIntensity);
        }
    }

    [TestCase(JudgmentTier.Great)]
    [TestCase(JudgmentTier.Good)]
    [TestCase(JudgmentTier.Bad)]
    [TestCase(JudgmentTier.Miss)]
    public void OnlyPerfectTriggers(JudgmentTier tier)
    {
        score.RegisterHit(tier);
        Assert.AreEqual(0f, pulse.CurrentIntensity);
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(1f, pulse.CurrentIntensity);
        var material = pulse.transform.Find("GateTop").GetComponent<MeshRenderer>().sharedMaterial;
        Assert.Greater(material.GetColor("_EmissionColor").maxColorComponent, GameStageSkin.GateEmission);
    }

    [Test]
    public void PulseDecaysAndSimultaneousPerfectsDoNotMultiplyBrightness()
    {
        for (int i = 0; i < 3; i++) score.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(1f, pulse.CurrentIntensity);
        pulse.Tick(Time.unscaledTimeAsDouble + 1);
        Assert.AreEqual(0f, pulse.CurrentIntensity);
        Assert.AreEqual(.5f, GateBeatPulse.PerfectIntensity01(10.08, 10, .16f), .001f);
        Assert.AreEqual(0f, GateBeatPulse.PerfectIntensity01(11, 10, .16f));
    }

    [Test]
    public void DisabledOrReboundPulseDoesNotKeepOldSubscription()
    {
        pulse.enabled = false;
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(0f, pulse.CurrentIntensity);
        pulse.enabled = true;
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(1f, pulse.CurrentIntensity);
        pulse.Bind(null);
        pulse.Tick(Time.unscaledTimeAsDouble + 1);
        score.RegisterHit(JudgmentTier.Perfect);
        Assert.AreEqual(0f, pulse.CurrentIntensity);
    }

    [Test]
    public void LongOnlyFlashesAfterThePerfectIsConfirmed()
    {
        var go = new GameObject("RepeatedCuts");
        try
        {
            var note = go.AddComponent<CuttableNote>();
            note.shatterDebrisCount = 0;
            note.RequiredCutCount = note.RemainingCuts = 3;
            typeof(ScoreManager).GetMethod("HandleSpawned", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(score, new object[] { note });
            note.Cut(Vector3.zero, Vector3.up * 3);
            Assert.AreEqual(0f, pulse.CurrentIntensity);
            note.Cut(Vector3.zero, Vector3.down * 3);
            Assert.AreEqual(0f, pulse.CurrentIntensity);
            note.Cut(Vector3.zero, Vector3.up * 3);
            Assert.AreEqual(1f, pulse.CurrentIntensity);
        }
        finally
        {
            if (go != null)
            {
                foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.sharedMaterial != null) Object.DestroyImmediate(renderer.sharedMaterial);
                Object.DestroyImmediate(go);
            }
        }
    }
}
