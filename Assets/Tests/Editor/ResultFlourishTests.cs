using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// リザルトのランク別の決めと、スコアの上昇・終止音(賞2・賞5)。
public class ResultFlourishTests
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const float CountStart = .24f;
    const float CountEnd = CountStart + ResultSoundtrack.CountSeconds;

    GameObject root;
    ResultReveal reveal;
    float time;
    int savedScore;
    bool savedLow;

    [SetUp]
    public void Setup()
    {
        savedScore = GameSession.FinalScore;
        savedLow = DisplaySettings.ReducedEffects;
        DisplaySettings.SetReducedEffectsForTest(false);
        root = new GameObject("ResultFlourishTest", typeof(RectTransform), typeof(Canvas));
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(1920f, 1080f);
        reveal = root.AddComponent<ResultReveal>();
        time = 0f;
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null)
        {
            // EditMode では Awake を通っていないため、所有クリップの片付けを明示する。
            foreach (var track in root.GetComponentsInChildren<ResultSoundtrack>(true))
                typeof(ResultSoundtrack).GetMethod("OnDestroy", PrivateInstance).Invoke(track, null);
            Object.DestroyImmediate(root);
        }
        GameSession.FinalScore = savedScore;
        DisplaySettings.SetReducedEffectsForTest(savedLow);
    }

    ResultSoundtrack Soundtrack(int score = 48000)
    {
        return ResultSoundtrack.Build(root.transform, reveal, null, score,
            CountStart, ResultSkin.RankLandingTime, 3.2f);
    }

    Transform Center(int score)
    {
        GameSession.FinalScore = score;
        var skin = root.AddComponent<ResultSkin>();
        typeof(ResultSkin).GetField("reveal", PrivateInstance).SetValue(skin, reveal);
        PlayRank rank = PlayRankHelper.FromScore(score);
        typeof(ResultSkin).GetMethod("BuildCenterColumn", PrivateInstance).Invoke(skin, new object[]
        {
            root.transform, rank, ResultSkin.RankAccentColor(PlayRankHelper.Label(rank)), .9f
        });
        return root.transform.Find("RankBadge");
    }

    void At(float t)
    {
        time = t;
        reveal.Tick(t);
    }

    void AdvanceTo(float target)
    {
        while (time + .05f < target) At(time + .05f);
        At(target);
    }

    static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);

    static UIVertex[] Vertices(ResultRankFlourishGraphic graphic)
    {
        using (var mesh = new VertexHelper())
        {
            typeof(ResultRankFlourishGraphic).GetMethod("OnPopulateMesh", PrivateInstance,
                null, new[] { typeof(VertexHelper) }, null).Invoke(graphic, new object[] { mesh });
            Assert.AreEqual(mesh.currentVertCount / 4 * 6, mesh.currentIndexCount);
            var vertices = new UIVertex[mesh.currentVertCount];
            for (int i = 0; i < vertices.Length; i++)
            {
                mesh.PopulateUIVertex(ref vertices[i], i);
                Assert.IsFalse(float.IsNaN(vertices[i].position.sqrMagnitude));
                Assert.IsFalse(float.IsInfinity(vertices[i].position.sqrMagnitude));
            }
            return vertices;
        }
    }

    [Test]
    public void TickVolumeIsAudible()
    {
        Assert.AreEqual(.3f, ResultSoundtrack.TickVolume, 1e-6f);
    }

    [Test]
    public void CountSwellStartsWithTheCountAndStopsWithOneChime()
    {
        var track = Soundtrack();
        At(CountStart - .001f);
        Assert.IsFalse(track.SwellStarted);
        At(CountStart);
        Assert.IsTrue(track.SwellStarted);
        Assert.IsTrue(track.SwellActive);

        AdvanceTo(CountEnd - .001f);
        Assert.Zero(track.CountStopCount);
        Assert.IsTrue(track.SwellActive);
        At(CountEnd);
        Assert.AreEqual(48000, track.ShownScore);
        Assert.AreEqual(1, track.CountStopCount);
        Assert.IsFalse(track.SwellActive);

        At(CountEnd);
        At(CountEnd + .1f);
        At(999f);
        At(CountEnd);
        Assert.AreEqual(1, track.CountStopCount, "同時刻・後続フレーム・巻き戻しで鳴り直さない");
    }

    [TestCase(.1f)]
    [TestCase(.6f)]
    [TestCase(1.7f)]
    public void SkippingBeforeCountEndStopsTheSwellWithoutAChime(float skipAt)
    {
        var track = Soundtrack();
        AdvanceTo(skipAt);
        int ticks = track.TickCount;
        Assert.AreEqual(skipAt >= CountStart, track.SwellActive);

        At(999f);
        At(999f);
        At(CountEnd);
        Assert.IsFalse(track.SwellActive);
        Assert.Zero(track.CountStopCount);
        Assert.AreEqual(ticks, track.TickCount);
        Assert.IsFalse(track.FinishPlayed);
        Assert.IsTrue(track.LoopStarted);
        Assert.AreEqual(48000, track.ShownScore);
    }

    [Test]
    public void FirstFrameSkipDoesNotStartAnyCountOrFinishSound()
    {
        var track = Soundtrack();
        At(999f);
        Assert.IsFalse(track.SwellStarted);
        Assert.IsFalse(track.SwellActive);
        Assert.Zero(track.TickCount);
        Assert.Zero(track.CountStopCount);
        Assert.IsFalse(track.FinishPlayed);
        Assert.IsTrue(track.LoopStarted);
    }

    [Test]
    public void AJumpInsideTheCountSuppressesTheChimeButNotTheLaterLanding()
    {
        var track = Soundtrack();
        AdvanceTo(.4f);
        Assert.IsTrue(track.SwellActive);
        int ticks = track.TickCount;
        At(1.1f);
        Assert.IsFalse(track.SwellActive);
        AdvanceTo(ResultSkin.RankLandingTime);
        Assert.Zero(track.CountStopCount, "カウントを飛ばした後に終点を普通に越えても鳴らさない");
        Assert.AreEqual(ticks, track.TickCount);
        Assert.IsTrue(track.FinishPlayed, "まだ飛ばしていない着地の決めは鳴らす");
    }

    [Test]
    public void ZeroScoreHasNoCountSounds()
    {
        var track = Soundtrack(0);
        AdvanceTo(CountEnd + .1f);
        Assert.Zero(track.ShownScore);
        Assert.Zero(track.TickCount);
        Assert.Zero(track.CountStopCount);
        Assert.IsFalse(track.SwellStarted);
        Assert.IsFalse(track.SwellActive);
    }

    [TestCase(0, 0)]
    [TestCase(48000, 1)]
    [TestCase(65000, 2)]
    [TestCase(85000, 3)]
    [TestCase(95000, 4)]
    public void FinishUsesTheLevelForTheFinalScore(int score, int level)
    {
        var track = Soundtrack(score);
        Assert.AreEqual(level, track.FinishLevel);
        var clip = Field<AudioClip>(track, "finishClip");
        Assert.AreEqual("ResultRankFinish" + level, clip.name);
        Assert.AreEqual(44100, clip.frequency);
        Assert.AreEqual(1, clip.channels);
        Assert.Greater(clip.samples, 0);
        Assert.Zero(track.GetComponents<AudioSource>().Length, "EditMode では音を再生しない");
    }

    [Test]
    public void SoundRingAndRaysWaitForTheBadgeLanding()
    {
        var track = Soundtrack(95000);
        var badge = Center(95000);
        var ring = root.transform.Find("ShockRing").GetComponent<CanvasGroup>();
        var flourish = root.GetComponentInChildren<ResultRankFlourishGraphic>();
        Assert.AreEqual(1.82f + .65f * ResultReveal.SlamLandPortion, ResultSkin.RankLandingTime, 1e-6f);
        Assert.AreEqual(ResultSkin.RankLandingTime, track.FinishTime);
        Assert.AreEqual(3.2f, track.LoopStart, "ループ曲の開始予定は維持する");
        Assert.AreEqual(new Vector2(0f, 74f), flourish.rectTransform.anchoredPosition);
        Assert.Less(flourish.transform.GetSiblingIndex(), badge.GetSiblingIndex());
        Assert.Zero(Vertices(flourish).Length);

        AdvanceTo(1.98f);
        Assert.IsFalse(track.FinishPlayed, "以前のリング開始時刻では早すぎる");
        Assert.Zero(ring.alpha);
        Assert.Zero(Vertices(flourish).Length);
        AdvanceTo(ResultSkin.RankLandingTime - .001f);
        Assert.IsFalse(track.FinishPlayed);
        Assert.Zero(ring.alpha);
        Assert.Zero(Vertices(flourish).Length);

        At(ResultSkin.RankLandingTime);
        Assert.IsTrue(track.FinishPlayed);
        Assert.AreEqual(1f, badge.localScale.x, 1e-4f, "決めの瞬間にバッジが等倍へ着地する");
        At(ResultSkin.RankLandingTime + .01f);
        Assert.Greater(ring.alpha, 0f);
        Assert.Greater(Vertices(flourish).Length, 0);
        Assert.IsFalse(track.LoopStarted);
    }

    [TestCase(PlayRank.SPlus, 18, 60)]
    [TestCase(PlayRank.S, 10, 0)]
    [TestCase(PlayRank.A, 0, 0)]
    [TestCase(PlayRank.B, 0, 0)]
    [TestCase(PlayRank.C, 0, 0)]
    public void RankDeterminesTheRayAndConfettiCounts(PlayRank rank, int rays, int confetti)
    {
        var graphic = ResultRankFlourishGraphic.Build(root.transform, reveal, rank);
        int objects = root.transform.childCount;
        AdvanceTo(ResultSkin.RankLandingTime + .3f);
        Assert.AreEqual((rays + confetti) * 4, Vertices(graphic).Length);
        Assert.IsFalse(graphic.raycastTarget);
        Assert.Zero(graphic.transform.childCount, "粒ごとのオブジェクトは作らない");
        Assert.AreEqual(objects, root.transform.childCount);
    }

    [TestCase(PlayRank.SPlus, 18)]
    [TestCase(PlayRank.S, 10)]
    public void LowModeKeepsOnlyDimStationaryRays(PlayRank rank, int rays)
    {
        var graphic = ResultRankFlourishGraphic.Build(root.transform, reveal, rank);
        AdvanceTo(ResultSkin.RankLandingTime + .3f);
        var early = Vertices(graphic);
        At(ResultSkin.RankLandingTime + .6f);
        var full = Vertices(graphic);
        Assert.AreNotEqual(early[2].position, full[2].position, "通常は光がゆっくり回る");

        DisplaySettings.SetReducedEffectsForTest(true);
        At(time);
        var low = Vertices(graphic);
        At(ResultSkin.RankLandingTime + .9f);
        var later = Vertices(graphic);
        Assert.AreEqual(rays * 4, low.Length, "LOW は S+ でも紙吹雪を描かない");
        Assert.AreEqual(low.Length, later.Length);
        Assert.Less(low[0].color.a, full[0].color.a);
        for (int i = 0; i < low.Length; i++)
        {
            Assert.AreEqual(low[i].position, later[i].position, "LOW は成長・回転・落下を止める");
            Assert.LessOrEqual(low[i].color.a, 15);
        }
    }

    [TestCase(PlayRank.SPlus, 3f)]
    [TestCase(PlayRank.S, 2.05f)]
    public void FlourishLeavesNoGeometryAfterItsLifetime(PlayRank rank, float seconds)
    {
        var graphic = ResultRankFlourishGraphic.Build(root.transform, reveal, rank);
        AdvanceTo(ResultSkin.RankLandingTime + seconds - .02f);
        Assert.Greater(Vertices(graphic).Length, 0);
        At(ResultSkin.RankLandingTime + seconds + .01f);
        Assert.Zero(Vertices(graphic).Length);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SkipBeforeLandingNeverReplaysTheFlourish(bool low)
    {
        DisplaySettings.SetReducedEffectsForTest(low);
        var graphic = ResultRankFlourishGraphic.Build(root.transform, reveal, PlayRank.SPlus);
        At(.1f);
        At(999f);
        Assert.Zero(Vertices(graphic).Length);
        At(ResultSkin.RankLandingTime + .3f);
        Assert.Zero(Vertices(graphic).Length, "スキップ後の手動巻き戻しでも再開しない");
    }

    [TestCase(0, 60000)]
    [TestCase(39999, 20001)]
    [TestCase(48000, 12000)]
    [TestCase(58766, 1234)]
    [TestCase(59999, 1)]
    [TestCase(60000, 0)]
    [TestCase(80000, 0)]
    [TestCase(90000, 0)]
    public void PointsToAIsHonestAndFitsInsideTheBadge(int score, int remaining)
    {
        string expected = remaining > 0 ? "あと " + remaining.ToString("N0") + " 点で A" : "";
        Assert.AreEqual(expected, ResultSkin.FormatPointsToA(score));
        var badge = Center(score);
        var hintTransform = badge.Find("PointsToA");
        if (remaining == 0)
        {
            Assert.IsNull(hintTransform, "A/S/S+ には点差を出さない");
            return;
        }

        Assert.IsNotNull(hintTransform);
        Assert.AreSame(badge, hintTransform.parent, "バッジと一緒に Slam する");
        Assert.IsNotNull(badge.GetComponent<CanvasGroup>());
        var hint = hintTransform.GetComponent<TextMeshProUGUI>();
        var letter = badge.Find("RankLetter").GetComponent<TextMeshProUGUI>();
        Assert.AreEqual(expected, hint.text);
        Assert.AreSame(UISkinKit.JapaneseFallbackFontAsset(), hint.font);
        foreach (char c in "あと点で")
            Assert.IsTrue(hint.font.HasCharacter(c, searchFallbacks: true, tryAddCharacter: true));
        Assert.That(hint.fontSize, Is.InRange(26f, 28f));
        Assert.AreEqual(250f, letter.fontSize);
        Assert.IsFalse(hint.raycastTarget);
        Assert.AreEqual(new Vector2(0f, -125f), hint.rectTransform.anchoredPosition);
        Assert.LessOrEqual(hint.GetPreferredValues().x, hint.rectTransform.rect.width);

        float hintTop = hint.rectTransform.anchoredPosition.y + hint.rectTransform.rect.yMax;
        float letterBottom = letter.rectTransform.anchoredPosition.y + letter.rectTransform.rect.yMin;
        Assert.Less(hintTop, letterBottom, "ランク文字と点差の領域を重ねない");
        var accuracy = root.transform.Find("AccuracyRow").GetComponent<RectTransform>();
        var accuracyValue = accuracy.Find("Value").GetComponent<TextMeshProUGUI>();
        float hintBottom = ((RectTransform)badge).anchoredPosition.y
            + hint.rectTransform.anchoredPosition.y + hint.rectTransform.rect.yMin;
        Assert.Greater(hintBottom, accuracy.anchoredPosition.y + accuracyValue.rectTransform.rect.yMax);

        // 内側六角形(378×416)の下端へ向かう斜辺にも、実際の文字が収まる。
        hint.ForceMeshUpdate(true, true);
        Bounds bounds = hint.textBounds;
        float lowest = hint.rectTransform.anchoredPosition.y + bounds.min.y;
        float halfWidth = 189f * Mathf.Clamp01((208f - Mathf.Abs(lowest)) / 104f);
        Assert.LessOrEqual(Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)), halfWidth);
    }

    [Test]
    public void OwnedClipsAreReleasedOnDestroy()
    {
        var track = Soundtrack();
        var clips = new[]
        {
            Field<AudioClip>(track, "tickClip"),
            Field<AudioClip>(track, "countSwellClip"),
            Field<AudioClip>(track, "countStopClip"),
            Field<AudioClip>(track, "finishClip"),
            Field<AudioClip>(track, "loopClip")
        };
        foreach (var clip in clips) Assert.IsNotNull(clip);
        typeof(ResultSoundtrack).GetMethod("OnDestroy", PrivateInstance).Invoke(track, null);
        foreach (var clip in clips) Assert.IsTrue(clip == null);
        At(CountStart);
        Assert.IsFalse(track.SwellStarted, "破棄後は時計の購読も外れる");
    }
}
