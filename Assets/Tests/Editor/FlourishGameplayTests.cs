using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 派手さカタログ(2026-10-20)のプレイ中の演出: 祭2・祭4・斬1・斬2・斬3・斬5・波1・波3。
public class FlourishGameplayTests
{
    readonly List<GameObject> objects = new List<GameObject>();
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    NoteSpawner spawner;
    ScoreManager score;
    SongPlayer clock;

    [SetUp]
    public void Setup()
    {
        var root = new GameObject("FlourishTest"); objects.Add(root);
        var prefab = new GameObject("FlourishNotePrefab"); objects.Add(prefab);
        prefab.AddComponent<CuttableNote>();
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.buildTimingCues = false;
        spawner.approachTime = 5f;
        spawner.OnNoteSpawned += n => { notes.Add(n); objects.Add(n.gameObject); };
        clock = root.AddComponent<SongPlayer>();
        score = root.AddComponent<ScoreManager>(); score.songPlayer = clock; score.Bind(spawner);
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var item in objects) if (item != null) Object.DestroyImmediate(item);
        objects.Clear(); notes.Clear();
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    void Spawn(params NoteData[] entries)
    {
        var chart = new ChartData(); chart.notes.AddRange(entries);
        spawner.SetChart(chart); spawner.Tick(1);
    }
    static NoteData N(float ms = 1000, string color = "blue", int count = 1) =>
        new NoteData { time = ms, type = count > 1 ? "long" : "tap", color = color, count = count, x = color == "blue" ? -1 : 1 };
    void Cut(int index, double error = 0, SaberHand? hand = null)
    {
        typeof(SongPlayer).GetField("scheduled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(clock, AudioSettings.dspTime - notes[index].HitTime - error);
        notes[index].Cut(notes[index].transform.position, Vector3.up * 8, CutDirection.None, hand ?? notes[index].RequiredHand);
    }
    static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, null);

    // ---- 斬3 X 斬り ----

    [Test]
    public void SimultaneousCutsWithBothHandsBecomeACrossCut()
    {
        var pairs = new List<(Vector3, Vector3)>();
        score.OnCrossCut += (a, b) => pairs.Add((a, b));
        Spawn(N(1000, "blue"), N(1000, "red"));
        Cut(0); Assert.IsFalse(score.LastCutWasCross, "1つ目はまだ X ではない");
        Cut(1);
        Assert.IsTrue(score.LastCutWasCross);
        Assert.AreEqual(1, score.CrossCutCount);
        Assert.AreEqual(1, pairs.Count);
    }

    [Test]
    public void CrossCutNeedsGreatOrBetterAndATightPair()
    {
        Spawn(N(1000, "blue"), N(1000, "red"));
        Cut(0); Cut(1, .1);
        Assert.IsFalse(score.LastCutWasCross, "両方 Great 以上でも、切った時刻が 50ms より離れていれば出さない");
        notes.Clear(); score.Reset();
        Spawn(N(1000, "blue"), N(1000, "red"));
        Cut(0, .14); Cut(1, .14);
        Assert.AreEqual(0, score.CrossCutCount, "Good 同士では出さない");
        notes.Clear(); score.Reset();
        Spawn(N(1000, "blue"), N(1200, "red"));
        Cut(0); Cut(1);
        Assert.AreEqual(0, score.CrossCutCount, "譜面上同時でなければ出さない");
        notes.Clear(); score.Reset();
        Spawn(N(1000, "gold"), N(1000, "red"));
        Cut(0, 0, SaberHand.Left); Cut(1);
        Assert.AreEqual(0, score.CrossCutCount, "金は自分の会心の演出を使う");
    }

    [Test]
    public void JudgmentSfxReplacesTheSecondCutSoundWithTheCrossSound()
    {
        var go = new GameObject("CrossSfx", typeof(AudioSource)); objects.Add(go);
        var sfx = go.AddComponent<JudgmentSfx>(); sfx.scoreManager = score;
        Invoke(sfx, "Awake"); Invoke(sfx, "OnEnable");
        Spawn(N(1000, "blue"), N(1000, "red"));
        Cut(0); Assert.AreEqual(0, sfx.CrossCutPlayCount);
        Cut(1); Assert.AreEqual(1, sfx.CrossCutPlayCount);
        Invoke(sfx, "OnDisable"); Invoke(sfx, "OnDestroy");
    }

    // ---- 斬2 金ノーツ ----

    [Test]
    public void GoldGoodAndBadUseTheNormalCutSoundsAndCriticalsClimb()
    {
        var sfxGo = new GameObject("GoldRouting", typeof(AudioSource)); objects.Add(sfxGo);
        var judgment = sfxGo.AddComponent<JudgmentSfx>(); judgment.scoreManager = score;
        var goldGo = new GameObject("GoldCritical", typeof(AudioSource)); objects.Add(goldGo);
        var gold = goldGo.AddComponent<GoldNoteSfx>();
        gold.Bind(spawner); gold.BindScore(score);
        var points = new List<int>();
        gold.OnCritical += (p, step) => points.Add(step);
        Spawn(N(1000, "gold"), N(1500, "gold"), N(2000, "gold"), N(2500, "red"), N(3000, "gold"), N(3500, "gold"));
        Cut(0, 0, SaberHand.Left);
        Assert.IsNull(judgment.ClipForCurrentJudgment(score.LastTier), "Perfect の金には普通の音を重ねない");
        Cut(1, 0, SaberHand.Left); Cut(2, 0, SaberHand.Right);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, points, "続けて切ると一段ずつ上がる(3段まで)");
        Cut(3);
        Assert.AreEqual(0, gold.CriticalStep, "間に普通のノーツが入ったら戻す");
        Cut(4, .14, SaberHand.Left);
        Assert.AreEqual(JudgmentTier.Good, score.LastTier, "ずれた金");
        Assert.IsNotNull(judgment.ClipForCurrentJudgment(score.LastTier), "Good・Bad の金は判定ごとの普通の切断音");
        Assert.AreEqual(3, gold.CriticalCount, "Good・Bad では会心の音を鳴らさない");
        Cut(5, 0, SaberHand.Left);
        Assert.AreEqual(0, points[points.Count - 1], "外したあとは最初の段から");
        gold.Bind(null); gold.BindScore(null);
    }

    [Test]
    public void GoldOmenRunsOnceAroundTheLandingFrame()
    {
        Assert.AreEqual(new Vector3(-.5f, .5f, -.02f), NoteTimingCue.OmenPoint(0));
        Assert.AreEqual(new Vector3(.5f, .5f, -.02f), NoteTimingCue.OmenPoint(.25f));
        Assert.AreEqual(new Vector3(.5f, -.5f, -.02f), NoteTimingCue.OmenPoint(.5f));
        Assert.AreEqual(new Vector3(-.5f, -.5f, -.02f), NoteTimingCue.OmenPoint(.75f));
        Assert.Greater(NoteTimingCue.OmenLeadSeconds, 0);
    }

    // ---- 斬5 ロング ----

    [Test]
    public void LongNotesGlowMoreAsTheyAreCut()
    {
        Spawn(N(1000, "blue", 4));
        var note = notes[0];
        Assert.AreEqual(1f, NoteTimingCue.LongGlow(note));
        Cut(0); Cut(0);
        Assert.AreEqual(1 + NoteTimingCue.LongGlowGain * .5f, NoteTimingCue.LongGlow(note), 1e-4f, "半分切ったら半分だけ強く");
        Assert.AreEqual(1f, NoteTimingCue.LongGlow(null));
    }

    [Test]
    public void LongReleaseDebrisNearlyStopsThenFlies()
    {
        var go = new GameObject("HoldPiece"); objects.Add(go);
        var piece = go.AddComponent<SlicePieceDecay>();
        piece.Launch(null, Vector3.right, Vector3.zero, false, 1, .5f, 0);
        piece.Hold(CuttableNote.LongReleaseHoldSeconds, CuttableNote.LongReleaseHoldSpeed);
        Assert.IsTrue(piece.IsHolding);
        piece.Step(.1f);
        Assert.Less(go.transform.position.x, .1f * 1.9f * .1f, "止めている間はほぼ動かない");
        piece.Step(.1f);
        Assert.IsFalse(piece.IsHolding);
        Assert.Greater(go.transform.position.x, .1f, "そのあとは元の速さ(1.9倍)で飛ぶ");
    }

    // ---- ゲートまわり(斬1・斬3・斬5・波1) ----

    GateAccents BuildGate(GameplayFeedbackPresenter feedback = null)
    {
        var gate = new GameObject("JudgeGate"); objects.Add(gate);
        gate.AddComponent<JudgeGateFrame>().Build(new Vector3(11, 6, 1));
        var accents = GateAccents.Create(spawner, score, feedback);
        Assert.IsNotNull(accents);
        return accents;
    }

    [Test]
    public void StreaksOnlyShowOutsideTheGate()
    {
        Assert.AreEqual(2.5f, GateAccents.ExitDistance(new Vector2(3, 0), Vector2.right, 5.5f, 3f), 1e-4f);
        Assert.AreEqual(0f, GateAccents.ExitDistance(new Vector2(6, 0), Vector2.right, 5.5f, 3f), "外で切ったら最初から見せる");
        var accents = BuildGate();
        Spawn(N(1000, "blue"));
        Cut(0);
        Assert.AreEqual(1, accents.StreakCount, "Perfect の斬撃は光の筋を飛ばす");
        accents.Tick(.1f);
        var vertices = accents.Mesh.vertices;
        Assert.Greater(vertices.Length, 0);
        foreach (var v in vertices)
            Assert.IsFalse(Mathf.Abs(v.x) < 5.5f + GateAccents.OutsideMargin - .5f && Mathf.Abs(v.y) < 3f + GateAccents.OutsideMargin - .5f,
                "ゲートの内側に光の筋を描かない: " + v);
        for (int i = 0; i < 5; i++) accents.Tick(.1f);
        Assert.AreEqual(0, accents.ActiveStreaks, "0.34 秒で消える");
    }

    [Test]
    public void MilestoneRingsStayOutsideAndLongsFillTheEdges()
    {
        var accents = BuildGate();
        accents.Milestone(100);
        accents.Tick(.3f);
        foreach (var v in accents.Mesh.vertices)
            Assert.IsFalse(Mathf.Abs(v.x) < 5.5f && Mathf.Abs(v.y) < 3f, "節目の輪はゲートの内側に描かない: " + v);
        accents.Tick(1);
        Spawn(N(1000, "red", 4));
        Cut(0); Cut(0);
        accents.Tick(.5f);
        Assert.AreEqual(.5f, accents.FillLevel, .01f, "切るたびに下から満ちる");
        Cut(0); Cut(0);
        Assert.AreEqual(1, accents.BurstCount, "最後の一振りで枠がはじける");
        accents.Tick(1);
        Assert.AreEqual(0, accents.FillLevel, .01f);
    }

    [Test]
    public void CrossCutLightsTheGateEdgesAndRestoresThem()
    {
        var accents = BuildGate();
        Spawn(N(1000, "blue"), N(1000, "red"));
        Cut(0); Cut(1);
        Assert.AreEqual(1, accents.CrossCount);
        accents.Tick(.05f);
        Assert.IsTrue(accents.CrossLit);
        var left = GameObject.Find("JudgeGate").transform.Find("GateLeft").GetComponent<MeshRenderer>();
        Assert.IsTrue(left.HasPropertyBlock(), "左の辺を青く上書きする");
        for (int i = 0; i < 5; i++) accents.Tick(.1f);
        Assert.IsFalse(accents.CrossLit);
        Assert.IsFalse(left.HasPropertyBlock(), "0.4 秒で元の材質へ戻す");
    }

    // ---- 祭2・祭4 ----

    [Test]
    public void LightCuesNeverFlashMoreThanTwoPointTwoTimesASecond()
    {
        var chart = new ChartData();
        for (int i = 0; i < 40; i++) chart.notes.Add(new NoteData { time = 1000 + i * 100, type = "tap", color = "red" });
        var cues = StageFeverShow.BuildCues(chart);
        for (int i = 1; i < cues.Length; i++) Assert.GreaterOrEqual(cues[i] - cues[i - 1], StageFeverShow.CueSpacing - 1e-6);
        Assert.AreEqual(.25f, StageFeverShow.LightLevelFor(0), 1e-4f, "区間の外・A メロは 0.25");
        Assert.AreEqual(1f, StageFeverShow.LightLevelFor(1), 1e-4f, "サビは 1");
    }

    [Test]
    public void BeatFlashesStaySpacedEvenWhenTheTempoSpeedsUp()
    {
        var root = new GameObject("FeverTempo"); objects.Add(root);
        var show = root.AddComponent<StageFeverShow>();
        // 前半は 120BPM、後半は 360BPM(1拍 0.167 秒、2拍ごとでも 1 秒3回になる速さ)。
        var chart = new ChartData { bpm = 120 };
        double t = 0; int beat = 0;
        for (; beat < 16; beat++, t += .5) chart.notes.Add(new NoteData { time = (float)(t * 1000), beat = beat, type = "tap", color = "red" });
        for (; beat < 64; beat++, t += 1 / 6.0) chart.notes.Add(new NoteData { time = (float)(t * 1000), beat = beat, type = "tap", color = "red" });
        show.Setup(StageTheme.ObsidianRelay, -2.5f, new StagePerformanceTimeline(), chart);
        var times = show.FlashBeatTimes();
        Assert.Greater(times.Length, 10);
        for (int i = 1; i < times.Length; i++)
            Assert.GreaterOrEqual(times[i] - times[i - 1], StageFeverShow.CueSpacing - 1e-6, "1秒2.2回を超えて光らせない: " + times[i]);
    }

    [Test]
    public void FeverRisesAtTheChorusAndThinsOutAfterIt()
    {
        var sections = new List<(double, double)> { (10, 20) };
        Assert.AreEqual(0f, StageFeverShow.FeverAt(9.9, sections));
        Assert.AreEqual(.5f, StageFeverShow.FeverAt(10.15, sections), 1e-3f, "0.3 秒で入る");
        Assert.AreEqual(1f, StageFeverShow.FeverAt(15, sections));
        Assert.AreEqual(.5f, StageFeverShow.FeverAt(20.25, sections), 1e-3f, "終わってから 0.5 秒で細くして消す");
        Assert.AreEqual(0f, StageFeverShow.FeverAt(21, sections));
    }

    [Test]
    public void FeverShowStaysOutsideTheReadingCorridorAndRestoresTheBackground()
    {
        var root = new GameObject("FeverStage"); objects.Add(root);
        var cameraGo = new GameObject("FeverCamera", typeof(Camera)); objects.Add(cameraGo);
        cameraGo.tag = "MainCamera";
        var cam = cameraGo.GetComponent<Camera>(); cam.backgroundColor = GameStageSkin.BackgroundColor;
        var show = root.AddComponent<StageFeverShow>();
        var chart = new ChartData { bpm = 120 };
        for (int i = 0; i < 60; i++) chart.notes.Add(new NoteData { time = i * 500, beat = i, type = "tap", color = "red" });
        var timeline = new StagePerformanceTimeline { sections = new[] { new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20, intensity = 1 } } };
        show.Setup(StageTheme.ObsidianRelay, -2.5f, timeline, chart);
        show.AtmosphereCamera = cam;
        for (double t = 14; t <= 15.01; t += .05) show.Tick(t, .05f);
        Assert.AreEqual(1f, show.Fever, 1e-4f);
        Assert.Greater(show.Mesh.vertexCount, 0);
        foreach (var v in show.Mesh.vertices)
            Assert.IsTrue(Mathf.Abs(v.x) >= StageFeverShow.CorridorHalfWidth || v.y <= -2.5f + .6f, "通路の外だけに描く: " + v);
        Assert.AreNotEqual(GameStageSkin.BackgroundColor, cam.backgroundColor, "サビの間はブーストの色へ寄せる");
        show.ResetState();
        Assert.AreEqual(GameStageSkin.BackgroundColor, cam.backgroundColor, "止めたら元の背景色へ戻す");
        DisplaySettings.SetReducedEffectsForTest(true);
        show.Tick(15, .05f);
        Assert.AreEqual(0f, show.Fever, "演出控えめでは出さない");
        show.Setup(StageTheme.PulseArray, -2.5f, timeline, chart);
        Assert.IsFalse(show.PillarsEnabled, "RUSH E の背景は自前の照明が同じ合図で光る");
    }

    // ---- 波1・波3 ----

    [TestCase(30, true)] [TestCase(40, false)] [TestCase(50, true)] [TestCase(75, false)] [TestCase(100, true)] [TestCase(150, true)] [TestCase(175, false)]
    public void MilestonesStartAtThirty(int combo, bool expected) => Assert.AreEqual(expected, GameplayFeedbackPresenter.IsMilestone(combo));

    [Test]
    public void MilestoneSoundsClimbOneShapeAtATime()
    {
        Assert.AreEqual(0, GameplayFeedbackPresenter.MilestoneStep(30));
        Assert.AreEqual(1, GameplayFeedbackPresenter.MilestoneStep(50));
        Assert.AreEqual(2, GameplayFeedbackPresenter.MilestoneStep(100));
        Assert.AreEqual(3, GameplayFeedbackPresenter.MilestoneStep(150));
        Assert.AreEqual(3, GameplayFeedbackPresenter.MilestoneStep(1000), "4段で上限");
        Assert.IsTrue(GameplayFeedbackPresenter.IsBigMilestone(100));
        Assert.IsTrue(GameplayFeedbackPresenter.IsBigMilestone(300));
        Assert.IsFalse(GameplayFeedbackPresenter.IsBigMilestone(200));
    }

    [Test]
    public void GoldPlusAppearsOnlyOnTheFinalGoldJudgment()
    {
        var hudGo = new GameObject("FlourishHud"); objects.Add(hudGo);
        var hud = hudGo.AddComponent<GameHUDSkin>();
        hud.Build();
        Spawn(N(1000, "gold"), N(2000, "red", 3));
        Cut(0, 0, SaberHand.Left);
        Assert.IsTrue(score.LastCutWasGold);
        Assert.IsNotEmpty(hud.GoldPlusShown, "金を Perfect で切ったらスコアの横に「+点」");
        typeof(GameHUDSkin).GetField("goldPlusAge", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hud, 999f);
        Cut(1);
        Assert.IsEmpty(hud.GoldPlusShown, "次のロングの途中の1回では、前の金の印が残っていても出さない");
        Invoke(hud, "OnDestroy");
    }

    [Test]
    public void ScoreRollsAndRankStampsLand()
    {
        Assert.AreEqual(0, GameHUDSkin.RolledScore(0, 1000, 0));
        Assert.That(GameHUDSkin.RolledScore(0, 1000, GameHUDSkin.ScoreRollSeconds * .5f), Is.InRange(600, 999), "出だしが速い");
        Assert.AreEqual(1000, GameHUDSkin.RolledScore(0, 1000, GameHUDSkin.ScoreRollSeconds));
        Assert.AreEqual(2.2f, GameHUDSkin.RankStampScale(0, false), 1e-4f, "判子は大きく現れて");
        Assert.AreEqual(1f, GameHUDSkin.RankStampScale(.18f, false), 1e-4f, "0.18 秒で着地する");
        Assert.AreEqual(1f, GameHUDSkin.RankStampScale(GameHUDSkin.RankStampSeconds, false));
        Assert.AreEqual(1f, GameHUDSkin.RankStampScale(.05f, true), "演出控えめでは動かさない");
    }
}
