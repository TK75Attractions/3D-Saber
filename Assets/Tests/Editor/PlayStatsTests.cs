using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// 実績の判定に使う演奏の記録(PlayStats、2026-10-29)。譜面からのノーツの種類・最後の結果・出たノーツとの結び付け・連続 PERFECT・
// 最初と最後の組・最後のサビ。第2弾: 組・切る回数・消え方・食・止まり・回転・区間の名前・密な窓・休み・赤青の交互と、
// 本物の切りで集める誤差・結果の時刻・切った手・向き違いの振り・最後の組の前後の HUD の点。
public class PlayStatsTests
{
    // 譜面を作る小道具(実績のテストで共通に使う)。
    internal static NoteData Note(float timeMs, string color = "red", string direction = "none", int count = 1, float x = 0f, float y = 0f)
        => new NoteData { time = timeMs, color = color, direction = direction, count = count, x = x, y = y };

    internal static ChartData Chart(params NoteData[] notes)
    {
        var chart = new ChartData();
        chart.notes.AddRange(notes);
        return chart;
    }

    internal static PlayStats Stats(ChartData chart, StagePerformanceTimeline stage = null) =>
        PlayStats.FromChart(chart, chart.offsetMs / 1000.0, NoteSpawner.DefaultSecondsPerLongCut, stage, "run");

    // 全部のノーツに同じ結果を入れる。
    internal static PlayStats AllResults(PlayStats stats, JudgmentTier tier)
    {
        for (int k = 0; k < stats.Notes.Count; k++) stats.Record(k, tier);
        return stats;
    }

    [Test]
    public void NotesTakeTheirKindAndChartTimeFromTheChart()
    {
        var chart = Chart(Note(1000), Note(2000, "gold", "up"), Note(3000, "blue", count: 4));
        chart.offsetMs = 250f;
        chart.notes[2].lengthMs = 2500f;
        var stats = PlayStats.FromChart(chart, .25 + .04, .7f, null, "abc");
        Assert.AreEqual("abc", stats.RunId);
        Assert.AreEqual(3, stats.Notes.Count);
        Assert.AreEqual(1.25, stats.Notes[0].Time, 1e-9, "譜面の時刻 = time + offsetMs");
        Assert.AreEqual(1.29, stats.Notes[0].HitTime, 1e-9, "出たノーツの時刻は判定のずらしも入れる");
        Assert.IsTrue(stats.Notes[0].Red);
        Assert.IsFalse(stats.Notes[0].Arrow);
        Assert.IsTrue(stats.Notes[1].Gold);
        Assert.IsTrue(stats.Notes[1].Arrow);
        Assert.IsTrue(stats.Notes[2].Blue);
        Assert.IsTrue(stats.Notes[2].Long);
        Assert.AreEqual(3.25 + 2.5, stats.Notes[2].EndTime, 1e-9, "ロングの終わりは lengthMs");
        Assert.AreEqual(1.25, stats.Notes[0].EndTime, 1e-9);
    }

    [Test]
    public void SecretMarksComeFromTheChartData()
    {
        var chart = Chart(Note(1000), Note(2000), Note(3000), Note(4000, count: 3), Note(5000), Note(6000));
        chart.fall.enabled = true;
        chart.notes[0].fall = true;
        chart.reverse.enabled = true;
        chart.notes[1].mirror = true;
        chart.notes[3].mirror = true; // ロングは鏡にしない
        chart.notes[2].vanish = "hide";
        chart.notes[4].veil = .4f;
        chart.notes[4].windX = .5f;
        chart.notes[5].approachScale = 1.4f;
        var stats = Stats(chart);
        Assert.IsTrue(stats.Notes[0].Fall);
        Assert.IsFalse(stats.Notes[1].Fall);
        Assert.IsTrue(stats.Notes[1].Mirror);
        Assert.IsFalse(stats.Notes[3].Mirror, "鏡は NoteSpawner.UsesReverse と同じくタップだけ");
        Assert.IsTrue(stats.Notes[2].Hide);
        Assert.IsTrue(stats.Notes[4].Veiled);
        Assert.IsTrue(stats.Notes[4].Wind);
        Assert.IsTrue(stats.Notes[5].Overtake);
        Assert.IsFalse(stats.Notes[0].Wind || stats.Notes[0].Overtake || stats.Notes[0].Veiled || stats.Notes[0].Hide);

        chart.fall.enabled = false;
        chart.reverse.enabled = false;
        var off = Stats(chart);
        Assert.IsFalse(off.Notes[0].Fall, "譜面全体の fall が無ければ降らない");
        Assert.IsFalse(off.Notes[1].Mirror, "譜面全体の reverse が無ければ鏡にしない");
    }

    [Test]
    public void QuizNotesAreThoseInsideAPaperFromStartToBeforeEnd()
    {
        var chart = Chart(Note(999), Note(1000), Note(1999), Note(2000));
        chart.quiz.papers.Add(new QuizPaperData { startMs = 1000, endMs = 2000 });
        chart.offsetMs = 300f; // 試験用紙はノーツの time と同じ時計
        var stats = Stats(chart);
        CollectionAssert.AreEqual(new[] { false, true, true, false }, Flags(stats, n => n.Quiz));
    }

    [Test]
    public void TheLastChorusIsTheStrongSectionThatStartsLatest()
    {
        var stage = new StagePerformanceTimeline
        {
            sections = new[]
            {
                new StagePerformanceTimeline.Section { startSeconds = 10, endSeconds = 20, intensity = 1f },
                new StagePerformanceTimeline.Section { startSeconds = 30, endSeconds = 40, intensity = StagePerformanceTimeline.StrongIntensity },
                new StagePerformanceTimeline.Section { startSeconds = 50, endSeconds = 60, intensity = .5f },
            },
        };
        Assert.IsTrue(PlayStats.TryLastChorus(stage, out double start, out double end));
        Assert.AreEqual(30, start);
        Assert.AreEqual(40, end, "弱い区間(.65 未満)は数えない");
        var chart = Chart(Note(15000), Note(29400), Note(29600), Note(39499), Note(39500), Note(55000));
        chart.offsetMs = 500f; // 譜面の時刻 = time + offsetMs で区間と比べる
        var stats = Stats(chart, stage);
        CollectionAssert.AreEqual(new[] { false, false, true, true, false, false }, Flags(stats, n => n.Chorus),
            "始まりを含み、終わりは含まない");
        Assert.IsFalse(PlayStats.TryLastChorus(null, out _, out _));
        Assert.IsFalse(PlayStats.TryLastChorus(new StagePerformanceTimeline(), out _, out _));
    }

    [Test]
    public void SpawnedNotesAreMatchedByHitTimeAndLandingPosition()
    {
        var chart = Chart(Note(1000, x: -1f), Note(1000, "blue", x: 1f), Note(2000), Note(2000), Note(3000));
        chart.coordScale = 2f;
        var stats = PlayStats.FromChart(chart, .1, .7f, null);
        Assert.AreEqual(1, stats.Match(1.1, 2f, 0f), "同じ時刻は着地点で見分ける");
        Assert.AreEqual(0, stats.Match(1.1, -2f, 0f));
        Assert.AreEqual(-1, stats.Match(1.1, 2f, 0f), "使ったノーツは二度使わない");
        Assert.AreEqual(2, stats.Match(2.1, 0f, 0f));
        Assert.AreEqual(3, stats.Match(2.1, 0f, 0f), "同じ時刻・同じ位置は順番に");
        Assert.AreEqual(4, stats.Match(3.1, 5f, 5f), "位置が合わなくても、同じ時刻のまだ使っていないノーツ");
        Assert.AreEqual(-1, stats.Match(4.1, 0f, 0f), "無い時刻");
    }

    [Test]
    public void TheLastResultOfEachNoteIsKept()
    {
        var stats = Stats(Chart(Note(1000), Note(2000)));
        Assert.IsNull(stats.Notes[0].Result);
        stats.Record(0, JudgmentTier.Great);
        stats.Record(0, JudgmentTier.Perfect);
        stats.Record(5, JudgmentTier.Miss); // 範囲の外は無視
        Assert.AreEqual(JudgmentTier.Perfect, stats.Notes[0].Result);
        Assert.IsTrue(stats.Notes[0].Cut);
        Assert.IsFalse(stats.Notes[1].Cut, "結果の無いノーツは切ったことにしない");
    }

    [Test]
    public void PerfectStreakCountsLongCutsAndResetsOnAnythingElse()
    {
        var stats = Stats(Chart(Note(1000)));
        for (int i = 0; i < 7; i++) stats.Judgment(JudgmentTier.Perfect);
        stats.Judgment(JudgmentTier.Great);
        for (int i = 0; i < 4; i++) stats.Judgment(JudgmentTier.Perfect);
        stats.Judgment(JudgmentTier.Miss);
        stats.Judgment(JudgmentTier.Perfect);
        Assert.AreEqual(7, stats.MaxPerfectStreak);
        for (int i = 0; i < 9; i++) stats.Judgment(JudgmentTier.Perfect);
        Assert.AreEqual(10, stats.MaxPerfectStreak);
        stats.Judgment(JudgmentTier.Bad);
        Assert.AreEqual(10, stats.MaxPerfectStreak);
    }

    [Test]
    public void AllCutAllCleanAndCutCountUseTheFilter()
    {
        var stats = Stats(Chart(Note(1000, "gold"), Note(2000, "gold"), Note(3000, "gold"), Note(4000)));
        stats.Record(0, JudgmentTier.Bad);
        stats.Record(1, JudgmentTier.Perfect);
        stats.Record(2, JudgmentTier.Good);
        stats.Record(3, JudgmentTier.Miss);
        Assert.IsTrue(stats.AllCut(n => n.Gold, 3), "BAD でもよい");
        Assert.IsFalse(stats.AllCut(n => n.Gold, 4), "数が足りない");
        Assert.IsFalse(stats.AllClean(n => n.Gold, 3), "BAD があるとノーミスではない");
        Assert.IsTrue(stats.AllClean(n => n.Gold && n.Result != JudgmentTier.Bad, 2));
        Assert.AreEqual(3, stats.CutCount(n => true));
        Assert.IsFalse(stats.AllCut(n => true, 1));
        Assert.IsFalse(stats.AllCut(n => false, 0), "1個もなければ取れない");
        var unknown = Stats(Chart(Note(1000, "gold"), Note(2000, "gold"), Note(3000, "gold")));
        unknown.Record(0, JudgmentTier.Perfect);
        unknown.Record(1, JudgmentTier.Perfect);
        Assert.IsFalse(unknown.AllCut(n => n.Gold, 2), "結果の無いノーツがあれば取れない");
    }

    [Test]
    public void FirstAndFinalGroupsTakeNotesWithinOneMillisecond()
    {
        // 最後の組は、譜面の時刻+ロングの長さがいちばん遅い組(9秒からの2秒のロングが、10秒のタップより後に終わる)。
        var chart = Chart(Note(500), Note(500.9f, "blue"), Note(502), Note(9000, count: 3), Note(10000), Note(11000.5f, "blue"));
        chart.notes[3].lengthMs = 2000f;
        var stats = Stats(chart);
        CollectionAssert.AreEqual(new[] { 0, 1 }, stats.FirstGroup, "1ms 以内は全部");
        CollectionAssert.AreEqual(new[] { 3, 5 }, stats.FinalGroup);
        AllResults(stats, JudgmentTier.Perfect);
        stats.Record(2, JudgmentTier.Good);
        stats.Record(4, JudgmentTier.Good);
        Assert.IsTrue(stats.AllPerfect(stats.FirstGroup));
        Assert.IsTrue(stats.AllPerfect(stats.FinalGroup));
        stats.Record(1, JudgmentTier.Great);
        Assert.IsFalse(stats.AllPerfect(stats.FirstGroup), "組のどれかが PERFECT でなければ取れない");
        Assert.IsFalse(Stats(Chart()).AllPerfect(Stats(Chart()).FirstGroup), "ノーツが無い");
    }

    // 本物の NoteSpawner → ScoreManager → PlayStatsRecorder(RushESongTests と同じく、届く時刻ちょうどの振りで切る)。
    // 同時の赤青の組は X 斬り、全部 PERFECT なら初太刀・締めの一太刀・連続 PERFECT。同じ譜面を置き直したら、次のノーツが出る前に作り直す。
    // 外したら、ノーツごとの購読も外れて数えない。
    [Test]
    public void TheRecorderFollowsRealCutsAndStartsOverWhenTheChartIsSetAgain()
    {
        var root = new UnityEngine.GameObject("PlayStatsRecorderTest");
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty, run = GameSession.AchievementRunId;
        PlayStatsRecorder recorder = null;
        try
        {
            GameSession.SelectedSongId = "__PlayStatsRecorderTest";
            GameSession.SelectedDifficulty = "Normal";
            GameSession.AchievementRunId = "recorder-run-1";
            var spawner = root.AddComponent<NoteSpawner>();
            var score = root.AddComponent<ScoreManager>();
            score.Bind(spawner);
            spawner.noteRoot = root.transform; spawner.buildTimingCues = false; spawner.simultaneousGuideEnabled = false;
            var prefab = new UnityEngine.GameObject("Note", typeof(CuttableNote));
            prefab.transform.SetParent(root.transform);
            prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
            spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
            var manager = root.AddComponent<GamePlayManager>();
            manager.enabled = false;
            manager.noteSpawner = spawner;
            manager.scoreManager = score;
            var chart = Chart(Note(1000, "blue", x: -1), Note(1000, "red", x: 1), Note(1500, "red", x: 1), Note(2000, "blue", "up", x: -1));
            Assert.IsNull(PlayStatsRecorder.TryBind(manager, "recorder-run-1"), "譜面を渡す前は結び付けない");
            spawner.SetChart(chart);
            recorder = PlayStatsRecorder.TryBind(manager, "recorder-run-1");
            Assert.NotNull(recorder);
            Assert.IsTrue(recorder.Stats.Complete);
            foreach (double t in new[] { 1.0, 1.5, 2.0 }) CutDue(spawner, t);
            spawner.Tick(4);
            var stats = recorder.Snapshot();
            CollectionAssert.AreEqual(new JudgmentTier?[] { JudgmentTier.Perfect, JudgmentTier.Perfect, JudgmentTier.Perfect, JudgmentTier.Perfect },
                Results(stats));
            Assert.AreEqual(1, stats.CrossCuts, "同時の赤と青を一緒に切ると X 斬り");
            Assert.AreEqual(4, stats.MaxPerfectStreak);
            Assert.IsTrue(stats.AllPerfect(stats.FirstGroup), "初太刀");
            Assert.IsTrue(stats.AllPerfect(stats.FinalGroup), "締めの一太刀");

            // 同じ譜面を置き直して(と採点を戻して)やり直す: 次のノーツが出る前に作り直し、前の結果・連続を持ち越さない。
            spawner.SetChart(chart);
            score.Reset();
            Assert.IsTrue(recorder.Stale);
            spawner.Tick(1.0);
            Assert.IsFalse(recorder.Stale, "最初のノーツが出たときに作り直す");
            Assert.AreNotSame(stats, recorder.Stats);
            Assert.IsTrue(recorder.Stats.Complete);
            spawner.Tick(1.5); // 組を見逃す
            CutDue(spawner, 1.5);
            CutDue(spawner, 2.0);
            spawner.Tick(4);
            CollectionAssert.AreEqual(new JudgmentTier?[] { JudgmentTier.Miss, JudgmentTier.Miss, JudgmentTier.Perfect, JudgmentTier.Perfect },
                Results(recorder.Stats));
            Assert.AreEqual(2, recorder.Stats.MaxPerfectStreak);
            Assert.IsFalse(recorder.Stats.AllPerfect(recorder.Stats.FirstGroup));

            // 回が変わったら作り直す(曲の途中なら、途中から見始めた回)。
            recorder.Rebuild("recorder-run-2");
            Assert.AreEqual("recorder-run-2", recorder.Stats.RunId);
            Assert.IsFalse(recorder.Stats.Complete);

            // 外したら、出ていたノーツを切っても数えない。
            spawner.SetChart(chart);
            score.Reset();
            recorder.Tick();
            var before = recorder.Stats;
            spawner.Tick(1.0);
            recorder.Unbind();
            CutDue(spawner, 1.0);
            CollectionAssert.AreEqual(new JudgmentTier?[] { null, null, null, null }, Results(before), "外したあとは数えない");
        }
        finally
        {
            if (recorder != null) recorder.Unbind();
            UnityEngine.Object.DestroyImmediate(root);
            GameSession.SelectedSongId = song;
            GameSession.SelectedDifficulty = difficulty;
            GameSession.AchievementRunId = run;
        }
    }

    // ---- 第2弾 ----

    [Test]
    public void GroupsAreNotesWithinOneMillisecondInChartTimeOrder()
    {
        var chart = Chart(Note(2000), Note(1000), Note(1000.5f, "blue", x: 1), Note(1002), Note(3000, count: 3));
        chart.offsetMs = 40f;
        var stats = Stats(chart);
        Assert.AreEqual(4, stats.Groups.Count);
        CollectionAssert.AreEqual(new[] { 1, 2 }, stats.Groups[0], "譜面の時刻の順、1ms 以内は同じ組");
        CollectionAssert.AreEqual(new[] { 3 }, stats.Groups[1], "2ms 離れたら別の組");
        CollectionAssert.AreEqual(new[] { 0 }, stats.Groups[2]);
        CollectionAssert.AreEqual(new[] { 4 }, stats.Groups[3]);
        Assert.AreSame(stats.Groups[1], stats.GroupFrom(1.042, true), "time + offsetMs の時計");
        Assert.AreSame(stats.Groups[2], stats.GroupFrom(1.042, false));
        Assert.IsNull(stats.GroupFrom(3.05, false));
        Assert.AreSame(stats.Groups[2], stats.NearestGroup(2.3, .5));
        Assert.AreSame(stats.Groups[0], stats.NearestGroup(1.0405, .1), "組の時刻は組の最初のノーツ(1.04 秒)");
        Assert.IsNull(stats.NearestGroup(2.55, .45), "いちばん近い組(3.04 秒)でも 0.49 秒離れている");
        Assert.AreEqual(0, Stats(Chart()).Groups.Count);
    }

    [Test]
    public void CutsVanishEclipseAndTheFinalFlagComeFromTheChartAndStage()
    {
        var chart = Chart(Note(1000), Note(108000, count: 9), Note(109000), Note(110000, "blue"));
        chart.notes[0].vanish = "blink";
        chart.notes[2].vanish = "sudden";
        var stage = new StagePerformanceTimeline
        {
            stageTheme = "AstralOrbit",
            sections = new[] { new StagePerformanceTimeline.Section { startSeconds = 100, endSeconds = 116, intensity = 1f } },
        };
        var stats = Stats(chart, stage);
        Assert.AreEqual(1, stats.Notes[0].Cuts);
        Assert.AreEqual(9, stats.Notes[1].Cuts);
        Assert.AreEqual(VanishMode.Blink, stats.Notes[0].Vanish);
        Assert.AreEqual(VanishMode.Sudden, stats.Notes[2].Vanish);
        Assert.AreEqual(VanishMode.None, stats.Notes[3].Vanish);
        Assert.AreEqual(1f, stats.Notes[1].Eclipse, 1e-4f, "本編と同じ EvaluateEclipse(譜面の時刻)");
        Assert.AreEqual(stage.EvaluateEclipse(109), stats.Notes[2].Eclipse, 1e-6f);
        Assert.AreEqual(0f, stats.Notes[0].Eclipse);
        Assert.IsTrue(stats.Notes[1].Final, "最後に終わる組(ロングの終わり)");
        Assert.IsFalse(stats.Notes[3].Final);
        Assert.AreSame(stage, stats.Stage);
        // 背景は本編と同じ StageThemeCatalog.ForSong を渡す(stage の stageTheme より先)。ほかの背景には食が無い。
        var other = PlayStats.FromChart(chart, 0, NoteSpawner.DefaultSecondsPerLongCut, stage, "run", StageTheme.VioletVault);
        Assert.AreEqual(0f, other.Notes[1].Eclipse);
        var astral = PlayStats.FromChart(chart, 0, NoteSpawner.DefaultSecondsPerLongCut, new StagePerformanceTimeline { sections = stage.sections }, "run",
            StageTheme.AstralOrbit);
        Assert.AreEqual(1f, astral.Notes[1].Eclipse, 1e-4f);
    }

    // 速さの表の止まり(ScrollSpeedMap.Stops)と、0.75 秒以上の止まりそれぞれのあとの最初の組(S の「時を動かす剣」)。
    [Test]
    public void LongStopsOfTheScrollAndTheFirstGroupsAfterThem()
    {
        var chart = Chart(Note(500), Note(1500), Note(2100), Note(2100, "blue", x: -1), Note(4200), Note(9000));
        chart.offsetMs = 200f;   // 速さの表は time の時計(止まりの終わり 1.9 秒 → 譜面の時刻 2.1 秒)
        chart.scroll.Add(new ScrollSpeedData { time = 1000, speed = 0 });
        chart.scroll.Add(new ScrollSpeedData { time = 1900, speed = 1 });
        chart.scroll.Add(new ScrollSpeedData { time = 3000, speed = 0, ramp = 500 });   // 3.5 秒で 0 になって
        chart.scroll.Add(new ScrollSpeedData { time = 3600, speed = 0 });               // 続けて止まり
        chart.scroll.Add(new ScrollSpeedData { time = 4000, speed = 1 });               // 0.5 秒(短い)
        chart.scroll.Add(new ScrollSpeedData { time = 8000, speed = 0 });               // 最後まで止まったまま
        var map = ScrollSpeedMap.Build(chart.scroll);
        var stops = map.Stops();
        Assert.AreEqual(3, stops.Count);
        Assert.AreEqual(1.0, stops[0].Start, 1e-9);
        Assert.AreEqual(1.9, stops[0].End, 1e-9);
        Assert.AreEqual(3.5, stops[1].Start, 1e-9, "ramp で 0 になった所から");
        Assert.AreEqual(4.0, stops[1].End, 1e-9, "続けて止まる区間はつなぐ");
        Assert.IsTrue(double.IsPositiveInfinity(stops[2].End));
        var stats = Stats(chart);
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, stats.FirstGroupsAfterStops(AchievementCatalog.RestartMinStopSeconds),
            "0.9 秒の止まりのあとの組(止まりの間の 1.5 秒は入らない。0.5 秒の止まりと、最後まで止まったままの区間は数えない)");
        CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, stats.FirstGroupsAfterStops(.5), "0.5 秒の止まりも数えれば 4.2 秒の組");
        var atEnd = Chart(Note(500), Note(1900), Note(2100));
        atEnd.scroll.AddRange(chart.scroll);
        CollectionAssert.AreEqual(new[] { 1 }, Stats(atEnd).FirstGroupsAfterStops(.75), "止まりの終わりちょうどの組も「あと」");
        var cut = Chart(Note(500), Note(1500));   // 音源で切り詰めて、止まりのあとの組が無い
        cut.scroll.AddRange(chart.scroll);
        Assert.AreEqual(0, Stats(cut).FirstGroupsAfterStops(.75).Count, "どれかの止まりのあとに組が無ければ、場面がそろっていない(空)");
        chart.fall.enabled = true;
        Assert.AreEqual(0, Stats(chart).FirstGroupsAfterStops(.5).Count, "降る譜面では速さの表を使わない(NoteSpawner と同じ)");
    }

    [Test]
    public void TheFirstTurnAndTheGroupsAfterIt()
    {
        var chart = Chart(Note(1000), Note(2000, "gold"), Note(3000), Note(4000), Note(5000), Note(6000));
        chart.reverse.enabled = true;
        chart.reverse.turns.Add(new ReverseTurnData { time = 5500, endTime = 5800, degrees = 360 });
        chart.reverse.turns.Add(new ReverseTurnData { time = 2000, endTime = 3000, degrees = 180 });
        var stats = Stats(chart);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, stats.AroundFirstTurn(.1, 2), "始まりの組と、終わり(3.0 秒ちょうども)のあとの2組");
        CollectionAssert.AreEqual(new[] { 1, 2 }, stats.AroundFirstTurn(.1, 1));
        Assert.AreEqual(0, stats.AroundFirstTurn(.1, 5).Count, "回転のあとの組が足りなければ(音源で切り詰めた譜面など)空");
        var late = Chart(Note(1000), Note(2300, "gold"), Note(3000), Note(4000));
        late.reverse.enabled = true;
        late.reverse.turns.Add(new ReverseTurnData { time = 2000, endTime = 3000, degrees = 180 });
        Assert.AreEqual(0, Stats(late).AroundFirstTurn(.1, 2).Count, "始まりから 0.1 秒以内に組が無ければ空");
        chart.reverse.enabled = false;
        Assert.AreEqual(0, Stats(chart).AroundFirstTurn(.1, 2).Count, "回転の無い譜面");
    }

    // stage.json の区間の名前(label)を読み、その区間の始まりの少し前より後の最初の組を探す。
    [Test]
    public void TheSectionLabelIsReadFromStageJson()
    {
        var stage = UnityEngine.JsonUtility.FromJson<StagePerformanceTimeline>(
            "{\"sections\":[{\"label\":\"主旋律の展開\",\"startSeconds\":62.631,\"endSeconds\":90.091,\"intensity\":0.65}," +
            "{\"label\":\"転調後の主題（PDF第53〜70小節）\",\"startSeconds\":111.208,\"endSeconds\":145.401,\"intensity\":0.72}]}");
        Assert.AreEqual("転調後の主題（PDF第53〜70小節）", stage.sections[1].label);
        var chart = Chart(Note(109800), Note(109911.66f, "blue", x: -1), Note(109911.66f, "red", x: 1), Note(110500));
        chart.offsetMs = 1307f;   // 校歌と同じずれ(111.107 秒は 111.158 秒より前、111.218660 秒の組がそのあと最初)
        var stats = Stats(chart, stage);
        CollectionAssert.AreEqual(new[] { 1, 2 }, stats.GroupAfterSection(AchievementCatalog.ModulationLabel, AchievementCatalog.ModulationLeadSeconds));
        Assert.IsNull(stats.GroupAfterSection("無い区間", .05));
        Assert.IsNull(Stats(chart).GroupAfterSection(AchievementCatalog.ModulationLabel, .05), "stage が無い");
    }

    [Test]
    public void DensestWindowLongestRestAndRedBlueRelay()
    {
        var stats = Stats(Chart(Note(1000), Note(1500, "blue"), Note(4000), Note(4100, "blue"), Note(4200), Note(5900, "blue"), Note(6100), Note(9000, "blue")));
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, stats.DensestWindow(2), "[4.0, 6.0) の4個(6.1 秒は入らない)");
        Assert.AreSame(stats.Groups[7], stats.AfterLongestRest(out double rest));
        Assert.AreEqual(2.9, rest, 1e-9);
        Assert.IsNull(Stats(Chart(Note(1000))).AfterLongestRest(out double none));
        Assert.IsTrue(double.IsNegativeInfinity(none));
        AllResults(stats, JudgmentTier.Great);
        Assert.AreEqual(8, stats.LongestRedBlueRelay(), "赤青が交互に8個");
        stats.Record(2, JudgmentTier.Bad);
        Assert.AreEqual(5, stats.LongestRedBlueRelay(), "BAD で数え直し(4.1 秒からの5個)");
        Assert.AreEqual(0, Stats(Chart()).DensestWindow(2).Count);
    }

    [Test]
    public void DetailedResultsWrongSwingsAndFinalScoresAreKept()
    {
        var stats = Stats(Chart(Note(1000), Note(2000, direction: "up")));
        var at = new System.DateTime(2026, 10, 29, 13, 0, 0, 250);
        stats.MarkWrongSwing(1);
        stats.Record(1, JudgmentTier.Perfect, -12.5, at, SaberHand.Right, true);
        stats.MarkWrongSwing(0);
        stats.Record(0, JudgmentTier.Miss, null, null, SaberHand.Any, false);
        stats.MarkWrongSwing(0);   // 結果のあとの知らせは数えない
        Assert.AreEqual(-12.5, stats.Notes[1].ErrorMs.Value, 1e-9);
        Assert.AreEqual(at, stats.Notes[1].JudgedAt);
        Assert.AreEqual(SaberHand.Right, stats.Notes[1].Hand);
        Assert.IsTrue(stats.Notes[1].OtherStunned);
        Assert.IsTrue(stats.Notes[1].WrongSwing);
        Assert.IsTrue(stats.Notes[0].WrongSwing, "結果の前の知らせ");
        Assert.IsNull(stats.Notes[0].ErrorMs);
        Assert.IsTrue(stats.Notes[1].Success && !stats.Notes[0].Success);
        Assert.IsNull(stats.FinalScoreBefore);
        stats.FinalJudgment(40000, 52000);
        stats.FinalJudgment(52000, 61000);
        Assert.AreEqual(40000, stats.FinalScoreBefore, "最初の判定の直前");
        Assert.AreEqual(61000, stats.FinalScoreAfter, "最後の判定の直後");
    }

    // 本物の NoteSpawner → ScoreManager → PlayStatsRecorder で、第2弾の記録を集める: 誤差(遅め 30ms)・結果の時刻・切った手・
    // 矢印の向き違いの振りのあとの PERFECT・MISS(誤差も時刻も無い)・最後の組の前後の HUD の点(B から A へ)。
    [Test]
    public void TheRecorderKeepsErrorsTimesHandsWrongSwingsAndTheFinalScores()
    {
        var root = new UnityEngine.GameObject("PlayStatsRecorderTest2");
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty, run = GameSession.AchievementRunId;
        PlayStatsRecorder recorder = null;
        try
        {
            GameSession.SelectedSongId = "__PlayStatsRecorderTest";
            GameSession.SelectedDifficulty = "Normal";
            GameSession.AchievementRunId = "recorder-run-3";
            var spawner = root.AddComponent<NoteSpawner>();
            var score = root.AddComponent<ScoreManager>();
            score.Bind(spawner);
            spawner.noteRoot = root.transform; spawner.buildTimingCues = false; spawner.simultaneousGuideEnabled = false;
            var prefab = new UnityEngine.GameObject("Note", typeof(CuttableNote));
            prefab.transform.SetParent(root.transform);
            prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
            spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
            var manager = root.AddComponent<GamePlayManager>();
            manager.enabled = false;
            manager.noteSpawner = spawner;
            manager.scoreManager = score;
            spawner.SetChart(Chart(Note(1000, "blue", x: -1), Note(1500, "red", "up", x: 1), Note(2000, "red", x: 1), Note(2500, "blue", x: -1)));
            recorder = PlayStatsRecorder.TryBind(manager, "recorder-run-3");
            var before = System.DateTime.Now;
            spawner.Tick(1.03);
            var first = Live(spawner, 1.0);
            Assert.IsTrue(first.TryCutWithCameraTiming(first.transform.position, UnityEngine.Vector3.up * 12, SaberHand.Left, 1.03, .866f), "30ms 遅れて切る");
            spawner.Tick(1.5);
            var arrow = Live(spawner, 1.5);
            Assert.IsFalse(arrow.TryCutWithCameraTiming(arrow.transform.position, UnityEngine.Vector3.down * 12, SaberHand.Right, 1.5, .866f),
                "向きの合わない振りでは切れない");
            Assert.IsTrue(arrow.TryCutWithCameraTiming(arrow.transform.position, UnityEngine.Vector3.up * 12, SaberHand.Right, 1.5, .866f), "振り直す");
            spawner.Tick(2.45);   // 2.0 秒のノーツを見逃す
            CutDue(spawner, 2.5);
            var after = System.DateTime.Now;
            var stats = recorder.Snapshot();
            CollectionAssert.AreEqual(new JudgmentTier?[] { JudgmentTier.Perfect, JudgmentTier.Perfect, JudgmentTier.Miss, JudgmentTier.Perfect }, Results(stats));
            Assert.AreEqual(30, stats.Notes[0].ErrorMs.Value, 1e-6, "ScoreManager.LastErrorMs(正は遅め)");
            Assert.AreEqual(0, stats.Notes[1].ErrorMs.Value, 1e-6);
            Assert.IsNull(stats.Notes[2].ErrorMs, "MISS に誤差は無い");
            Assert.AreEqual(SaberHand.Left, stats.Notes[0].Hand);
            Assert.AreEqual(SaberHand.Right, stats.Notes[1].Hand);
            Assert.AreEqual(SaberHand.Any, stats.Notes[2].Hand);
            foreach (int k in new[] { 0, 1, 3 })
                Assert.IsTrue(stats.Notes[k].JudgedAt >= before && stats.Notes[k].JudgedAt <= after, "結果が出た台の時刻 " + k);
            Assert.IsNull(stats.Notes[2].JudgedAt);
            Assert.IsTrue(stats.Notes[1].WrongSwing, "向き違いの知らせのあとで PERFECT");
            Assert.IsFalse(stats.Notes[0].WrongSwing || stats.Notes[3].WrongSwing);
            Assert.IsFalse(stats.Notes[0].OtherStunned, "闘牛の無い曲");
            // 2個 PERFECT と MISS のあとは 45000 + コンボ点 5000、最後の組(2.5 秒)の PERFECT のあとは 67500 + 5000。
            CollectionAssert.AreEqual(new[] { 3 }, stats.FinalGroup);
            Assert.AreEqual(50000, stats.FinalScoreBefore, "最後の組の判定の直前の HUD の点(ScoreWithCombo)");
            Assert.AreEqual(72500, stats.FinalScoreAfter);
            Assert.AreEqual(score.ScoreWithCombo, stats.FinalScoreAfter);
            var context = new AchievementContext { Score = 72500, Stats = stats };
            Assert.IsTrue(AchievementCatalog.Find("buzzer").Met(context), "B から A へ上がった");
        }
        finally
        {
            if (recorder != null) recorder.Unbind();
            UnityEngine.Object.DestroyImmediate(root);
            GameSession.SelectedSongId = song;
            GameSession.SelectedDifficulty = difficulty;
            GameSession.AchievementRunId = run;
        }
    }

    // 出ていて、まだ結果の出ていない、届く時刻 hitTime のノーツ。
    static CuttableNote Live(NoteSpawner spawner, double hitTime)
    {
        foreach (var note in spawner.LiveNotes)
            if (note != null && !note.IsFinalized && System.Math.Abs(note.HitTime - hitTime) < 1e-6) return note;
        Assert.Fail("出ていない: " + hitTime);
        return null;
    }

    // 判定の時計 t に届いたノーツを、届く時刻ちょうどの振りで切る(矢印はその向き、ほかは上へ)。
    static void CutDue(NoteSpawner spawner, double t)
    {
        spawner.Tick(t);
        foreach (var note in spawner.LiveNotes.ToArray())
        {
            if (note == null || note.IsFinalized || note.HitTime > t + 1e-6) continue;
            var direction = note.RequiredDirection != CutDirection.None
                ? CutDirectionHelper.ToVector(note.RequiredDirection).normalized : UnityEngine.Vector2.up;
            var hand = note.RequiredHand != SaberHand.Any ? note.RequiredHand : note.LandingPosition.x < 0 ? SaberHand.Left : SaberHand.Right;
            Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position, new UnityEngine.Vector3(direction.x, direction.y, 0) * 12,
                hand, note.HitTime, .866f), "切れる: " + note.HitTime);
        }
    }

    static JudgmentTier?[] Results(PlayStats stats)
    {
        var result = new List<JudgmentTier?>();
        foreach (var note in stats.Notes) result.Add(note.Result);
        return result.ToArray();
    }

    static bool[] Flags(PlayStats stats, System.Func<PlayStats.Note, bool> flag)
    {
        var result = new List<bool>();
        foreach (var note in stats.Notes) result.Add(flag(note));
        return result.ToArray();
    }
}
