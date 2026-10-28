using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// 実績の判定に使う演奏の記録(PlayStats、2026-10-29)。譜面からのノーツの種類・最後の結果・出たノーツとの結び付け・連続 PERFECT・
// 最初と最後の組・最後のサビ。
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
