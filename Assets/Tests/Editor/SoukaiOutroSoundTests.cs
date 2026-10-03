using System.Collections.Generic;
using NUnit.Framework;
using TMPro;
using UnityEngine;

// 爽快感カタログ 山4(終わりの音)と、演出用の合成音。
public class SoukaiOutroSoundTests
{
    readonly List<Object> created = new List<Object>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
    }

    static float Peak(float[] s) { float m = 0; foreach (var v in s) m = Mathf.Max(m, Mathf.Abs(v)); return m; }
    static bool AllFinite(float[] s) { foreach (var v in s) if (float.IsNaN(v) || float.IsInfinity(v)) return false; return true; }

    [Test]
    public void SynthesizedSoundsAreFiniteAndDoNotClip()
    {
        var sounds = new Dictionary<string, float[]>
        {
            { "Don", ProceduralSfx.Don() }, { "Rise", ProceduralSfx.Rise() }, { "Gold", ProceduralSfx.Chord(false) },
            { "Bright", ProceduralSfx.Chord(true) }, { "Tick", ProceduralSfx.Tick() }, { "Sparkle", ProceduralSfx.Sparkle() },
        };
        foreach (var pair in sounds)
        {
            Assert.IsTrue(AllFinite(pair.Value), pair.Key);
            Assert.LessOrEqual(Peak(pair.Value), .91f, pair.Key + " は割れない");
            Assert.Greater(Peak(pair.Value), .3f, pair.Key + " は無音ではない");
            Assert.Less(Mathf.Abs(pair.Value[pair.Value.Length - 1]), .02f, pair.Key + " は最後が無音へ収まる(プツッと切れない)");
        }
        Assert.AreNotEqual(ProceduralSfx.Chord(false).Length, 0);
        CollectionAssert.AreNotEqual(ProceduralSfx.Chord(false), ProceduralSfx.Chord(true), "FULL COMBO と ALL PERFECT は違う和音");
    }

    [Test]
    public void DonHasWeightButNoSubBass()
    {
        // 重さは倍音(100Hz台)で出し、60Hz 未満の超低音は入れない。基音は 150Hz から 72Hz へ下がって止まる。
        var don = ProceduralSfx.Don();
        int zeroCrossings = 0;
        int from = (int)(.15f * ProceduralSfx.Rate), to = (int)(.35f * ProceduralSfx.Rate);
        for (int i = from + 1; i < to; i++) if ((don[i - 1] < 0) != (don[i] < 0)) zeroCrossings++;
        float seconds = (to - from) / (float)ProceduralSfx.Rate;
        Assert.Greater(zeroCrossings / seconds / 2f, 60f, "基音が 60Hz 未満まで下がらない");
    }

    [Test]
    public void AmbientLoopIsSeamless()
    {
        var loop = ProceduralSfx.AmbientLoop();
        Assert.AreEqual(Mathf.RoundToInt(16f * ProceduralSfx.LoopRate), loop.Length);
        Assert.IsTrue(AllFinite(loop));
        Assert.LessOrEqual(Peak(loop), .71f);
        // 最後の1サンプルから最初へ戻る差は、ループ内の隣り合う差と同じくらい小さい。
        float maxStep = 0;
        for (int i = 1; i < loop.Length; i++) maxStep = Mathf.Max(maxStep, Mathf.Abs(loop[i] - loop[i - 1]));
        Assert.LessOrEqual(Mathf.Abs(loop[0] - loop[loop.Length - 1]), maxStep * 1.05f, "つなぎ目で音が飛ばない");
        // 和音の窓は常に足して1(どの瞬間も音量が一定)。
        for (float t = 0; t < 16f; t += .37f)
        {
            float sum = 0;
            for (int c = 0; c < 4; c++) sum += ProceduralSfx.ChordWindow(t, c * 4f, 4f, 16f);
            Assert.AreEqual(1f, sum, 1e-3f, "t=" + t);
        }
    }

    [TestCase(0f, 0)]
    [TestCase(.24f, 0)]
    [TestCase(1.74f, 48000)]
    [TestCase(9f, 48000)]
    public void CountUpStartsAtZeroAndLandsOnTheFinalScore(float t, int expected)
    {
        Assert.AreEqual(expected, ResultSoundtrack.CountedScore(t, 48000, .24f, 1.5f));
    }

    [Test]
    public void CountUpIsMonotonicAndFastFirst()
    {
        int previous = 0;
        for (float t = .24f; t <= 1.74f; t += .05f)
        {
            int v = ResultSoundtrack.CountedScore(t, 48000, .24f, 1.5f);
            Assert.GreaterOrEqual(v, previous);
            previous = v;
        }
        Assert.Greater(ResultSoundtrack.CountedScore(.24f + .75f, 48000, .24f, 1.5f), 48000 * .8f, "前半で大きく進む");
    }

    ResultSoundtrack Soundtrack(out ResultReveal reveal, out TextMeshProUGUI text)
    {
        var canvas = new GameObject("SoundtrackCanvas", typeof(RectTransform)); created.Add(canvas);
        reveal = canvas.AddComponent<ResultReveal>();
        var textGo = new GameObject("ScoreValue", typeof(RectTransform)); created.Add(textGo);
        text = textGo.AddComponent<TextMeshProUGUI>();
        var view = ResultSoundtrack.Build(canvas.transform, reveal, text, 48000, .24f, 1.98f, 3.2f);
        created.Add(view.gameObject);
        return view;
    }

    [Test]
    public void ResultCountsUpTicksSlamsAndThenStartsTheLoop()
    {
        var view = Soundtrack(out var reveal, out var text);
        Assert.AreEqual("0", text.text, "最初は 0 から");
        for (float t = 0; t <= 3.5f; t += 1f / 60f) reveal.Tick(t);
        Assert.AreEqual("48,000", text.text);
        Assert.AreEqual(48000, view.ShownScore);
        int expectedTicks = Mathf.FloorToInt(ResultSoundtrack.CountSeconds / ResultSoundtrack.TickInterval);
        Assert.That(view.TickCount, Is.InRange(expectedTicks - 2, expectedTicks + 1), "数字が動く間だけ刻む");
        Assert.IsTrue(view.DonPlayed, "ランクの衝撃波でドン");
        Assert.IsTrue(view.LoopStarted, "順位の演出の後にループ曲");
    }

    [Test]
    public void SkippingJumpsToTheFinalScoreWithoutReplayingSounds()
    {
        var view = Soundtrack(out var reveal, out var text);
        reveal.Tick(.1f);
        reveal.Tick(999f);
        Assert.AreEqual("48,000", text.text);
        Assert.AreEqual(0, view.TickCount, "スキップで過ぎた刻む音をまとめて鳴らさない");
        Assert.IsFalse(view.DonPlayed, "スキップで過ぎたドンも鳴らさない");
        Assert.IsTrue(view.LoopStarted, "ループ曲は始める");
    }

    [Test]
    public void TrackEndingCardPlaysTheRisingSound()
    {
        var feedback = GameplayFeedbackPresenter.Create(null, null); created.Add(feedback.gameObject);
        Assert.IsFalse(feedback.OutroSoundPlayed);
        feedback.BeginOutro();
        Assert.IsTrue(feedback.OutroSoundPlayed);
        Assert.AreEqual("TRACK CLEAR", feedback.EndingLabel);
        Assert.NotNull(feedback.OutroClip);
        Assert.Greater(feedback.OutroClip.length, .4f);
        feedback.BeginOutro();
        Assert.IsTrue(feedback.OutroSoundPlayed, "二度目の呼び出しでは何も変えない");
    }
}
