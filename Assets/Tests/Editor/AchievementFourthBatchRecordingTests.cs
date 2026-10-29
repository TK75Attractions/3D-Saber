using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 保存・音声再生を呼ばず、記録の入口と寿命を確かめる。
public class AchievementFourthBatchRecordingTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly Dictionary<FieldInfo, object> sessionFields = new Dictionary<FieldInfo, object>();
    GameObject root;
    PlayStatsRecorder recorder;
    SongPlayer song;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionFields[field] = field.GetValue(null);
        AchievementRunInfo.Clear();
        GameSession.AchievementRunId = "fourth-test";
        GameSession.SelectedSongId = "__AchievementFourthTest";
        GameSession.SelectedDifficulty = "Normal";
        root = new GameObject("AchievementFourthTest");
    }

    [TearDown]
    public void TearDown()
    {
        recorder?.Unbind();
        UnityEngine.Object.DestroyImmediate(root);
        AchievementRunInfo.Clear();
        foreach (var item in sessionFields) item.Key.SetValue(null, item.Value);
        sessionFields.Clear();
    }

    static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
    static void Property(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);
    static void Call(object target, string name, params object[] values) => target.GetType().GetMethod(name, Private).Invoke(target, values);

    GamePlayManager Prepare(ChartData chart, out NoteSpawner spawner, out ScoreManager score)
    {
        spawner = root.AddComponent<NoteSpawner>();
        spawner.noteRoot = root.transform;
        spawner.buildTimingCues = spawner.simultaneousGuideEnabled = false;
        var prefab = new GameObject("Note", typeof(CuttableNote));
        prefab.transform.SetParent(root.transform);
        prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
        spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
        score = root.AddComponent<ScoreManager>();
        score.Bind(spawner);
        song = root.AddComponent<SongPlayer>();
        Set(song, "scheduled", true);
        Set(song, "paused", true);
        song.ProgressEndSeconds = 100;
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.noteSpawner = spawner; manager.scoreManager = score; manager.songPlayer = song;
        spawner.SetChart(chart);
        recorder = PlayStatsRecorder.TryBind(manager, GameSession.AchievementRunId);
        Assert.NotNull(recorder);
        recorder.Tick();
        return manager;
    }

    void Clock(double time) => Set(song, "pausedSongTime", time);
    static CuttableNote Live(NoteSpawner spawner, double time) =>
        spawner.LiveNotes.Single(n => n != null && !n.IsFinalized && Math.Abs(n.HitTime - time) < .000001);

    void Cut(CuttableNote note, SaberHand hand, double time, int sample)
    {
        Clock(time);
        Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position, Vector3.up * 12, hand, time, .866f, 23,
            new SaberCutRecord { Frame = sample, Sample = sample, ObservedAt = time, StrokeId = 23,
                DirectionValid = true, Direction = Vector2.up }));
    }

    [Test]
    public void AcceptedCutsAreRecordedOnceAndResetWithPoolGenerationAndChart()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000, "gold", count: 3), PlayStatsTests.Note(5000));
        Prepare(chart, out var spawner, out var score);
        spawner.Tick(1);
        var note = Live(spawner, 1);
        Cut(note, SaberHand.Left, 1, 10);
        Cut(note, SaberHand.Right, 1.2, 11);
        Cut(note, SaberHand.Left, 1.4, 12);
        var s = recorder.Snapshot();
        Assert.AreEqual(3, s.AcceptedCuts.Count);
        Assert.AreEqual(3, s.Notes[0].Accepted.Count, "OnJudgedで最終打を重複しない");
        Assert.AreEqual(3, s.Notes[0].HandMask);
        Assert.AreEqual(1, s.Notes[0].FirstCutTime);
        Assert.AreEqual(1.4, s.Notes[0].LastCutTime);
        Assert.AreEqual(1.4, s.Notes[0].ResolvedTime);
        Assert.AreEqual(12, s.AcceptedCuts[2].Sample);
        Assert.AreEqual(note.SpawnVersion, s.AcceptedCuts[0].Generation);
        Assert.AreEqual(JudgmentTier.Perfect, s.Notes[0].Result);
        s.AcceptCut(0, 2, note.SpawnVersion, SaberHand.Left, 1.4, note.LastCutRecord);
        Assert.AreEqual(3, s.AcceptedCuts.Count, "同じ打鍵番号は増えない");
        spawner.Tick(5);
        Clock(5.3);
        Live(spawner, 5).MarkMiss();
        Assert.AreEqual(5.3, s.Notes[1].ResolvedTime);

        spawner.SetChart(chart);
        score.Reset();
        recorder.Tick();
        Assert.AreNotSame(s, recorder.Stats);
        Assert.IsEmpty(recorder.Stats.AcceptedCuts);
        spawner.Tick(1);
        var fresh = recorder.Stats;
        note = Live(spawner, 1);
        Cut(note, SaberHand.Right, 1, 20);
        Assert.AreEqual(1, fresh.AcceptedCuts.Count);
        Assert.AreEqual(3, s.AcceptedCuts.Count, "前の回の購読を外す");
        recorder.Unbind();
        Cut(note, SaberHand.Left, 1.2, 21);
        Assert.AreEqual(1, fresh.AcceptedCuts.Count);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void VoltageCauseDoesNotDependOnSubscriptionOrder(bool voltageFirst)
    {
        var chart = PlayStatsTests.Chart(Enumerable.Range(0, 32).Select(i => PlayStatsTests.Note(1000 + i * 500)).ToArray());
        Prepare(chart, out var spawner, out var score);
        recorder.Unbind();
        VoltageScoreBinding binding = null;
        try
        {
            var meter = new VoltageMeter();
            if (voltageFirst) binding = new VoltageScoreBinding(score, meter);
            recorder = PlayStatsRecorder.TryBind(root.GetComponent<GamePlayManager>(), GameSession.AchievementRunId);
            if (!voltageFirst) binding = new VoltageScoreBinding(score, meter);
            for (int i = 0; i < 32; i++)
            {
                double at = 1 + i * .5;
                spawner.Tick(at);
                Cut(Live(spawner, at), SaberHand.Right, at, i);
            }
            var s = recorder.Snapshot();
            Assert.NotNull(s.FirstRainbowCause);
            Assert.AreEqual(31, s.FirstRainbowCause.NoteIndex);
            Assert.AreEqual(0, s.FirstRainbowCause.CutIndex);
            Assert.AreEqual(3, s.FirstRainbowCause.Before);
            Assert.AreEqual(4, s.FirstRainbowCause.After);
            Assert.IsTrue(s.HasLastRainbow());
        }
        finally { binding?.Dispose(); }
    }

    [Test]
    public void AimRecordingUsesTheActualSamplesAndIgnoresStationarySongClock()
    {
        // EditMode では、的が終わったときの演出の片付け(Destroy)がエラーの記録を出す。記録の確かめには関係しないので、ここだけ無視する。
        LogAssert.ignoreFailingMessages = true;
        var chart = PlayStatsTests.Chart();
        chart.aim.targets.Add(new AimTargetData { time = 8000, color = "red" });
        chart.aim.spikes.Add(new AimSpikeData { time = 5000, x = 3, y = 2 });
        var manager = Prepare(chart, out _, out var score);
        song.ProgressEndSeconds = 10;
        var aim = AimDodgeMode.Create(root.transform, chart.aim, 0, 10, score, null, null);
        Set(manager, "aimMode", aim);
        bool visible = true;
        aim.SampleOverride = hand => new AimHandSample(visible, Vector2.zero);
        recorder.Tick();
        aim.Tick(-1, -1);
        aim.Tick(0, 0);
        aim.Tick(4, 4);
        aim.Tick(4, 4);
        Assert.AreEqual(4, recorder.Stats.AimObserved.ActiveSeconds);
        visible = false;
        aim.Tick(5, 5);
        visible = true;
        aim.Tick(6, 6);
        aim.Tick(10, 10);
        var observation = recorder.Stats.AimObserved;
        Assert.AreEqual(10, observation.ActiveSeconds);
        Assert.AreEqual(8, observation.RedSeconds);
        Assert.AreEqual(8, observation.BlueSeconds);
        Assert.IsTrue(observation.MissingDanger);
        Assert.IsTrue(observation.Resolved);
        Assert.IsFalse(observation.Nothorn(1, 0));
        recorder.Unbind();
        aim.Tick(11, 11);
        Assert.AreEqual(10, observation.ActiveSeconds);
    }

    static void Listen(AchievementSelectionHistory history, string songId, double seconds, int generation = 1, bool manual = true)
    {
        history.Select(songId, manual);
        history.Sample(songId, generation, 20, true);
        history.Sample(songId, generation, 20 + seconds, true);
    }

    [Test]
    public void PreviewHistoryConsumesOnlyAudibleContinuousManualSelections()
    {
        var h = AchievementRunInfo.Selection;
        h.Begin(false);
        Listen(h, "A", 3);
        Listen(h, "B", 3, 2);
        h.Select("A", true);
        h.Finish("A", true);
        AchievementRunInfo.BeginRun("first");
        Assert.IsTrue(AchievementRunInfo.Backpick);
        AchievementRunInfo.BeginRun("second");
        Assert.IsFalse(AchievementRunInfo.Backpick);

        foreach (bool automatic in new[] { false, true })
        {
            h.Begin(false);
            Listen(h, "A", automatic ? 3 : 2.999, manual: !automatic);
            Listen(h, "B", 3, 2);
            h.Select("A", true); h.Finish("A", true);
            Assert.IsFalse(h.Consume());
        }
        h.Begin(false);
        Listen(h, "A", 3);
        Listen(h, "A", 3, 2);
        h.Finish("A", true);
        Assert.IsFalse(h.Consume(), "難易度変更だけ");
        h.Begin(false);
        Listen(h, "A", 3); Listen(h, "B", 3, 2);
        h.Select("A", true); h.Finish("A", false);
        Assert.IsFalse(h.Consume(), "時間切れ");
        h.Begin(false);
        h.Select("A", true);
        h.Sample("A", 1, 20, false);
        h.Sample("A", 1, 24, true);
        Assert.IsNull(h.FirstSong, "待ち時間を数えない");
        h.Sample("A", 1, 26, true);
        h.Sample("A", 1, 26, false);
        h.Sample("A", 1, 26, true);
        h.Sample("A", 1, 28, true);
        Assert.IsNull(h.FirstSong, "消音で分かれた断片を足さない");
        h.Sample("A", 1, 29, true);
        Assert.AreEqual("A", h.FirstSong);
        Listen(h, "B", 3, 2);
        h.Select("A", true);
        h.Begin(true);
        h.Select("A", false);
        h.Finish("A", true);
        Assert.IsTrue(h.Consume(), "調整から同じ曲へ戻り手動開始");
        h.Begin(false); Listen(h, "A", 3); Listen(h, "B", 3, 2);
        AchievementRunInfo.BeginVisitor();
        h.Select("A", true); h.Finish("A", true);
        Assert.IsFalse(h.Consume(), "来場者が替わる");
        h.Begin(false); Listen(h, "A", 2);
        h.Begin(true); h.Select("A", false);
        h.Sample("A", 2, 30, true); h.Sample("A", 2, 31, true);
        Assert.IsNull(h.FirstSong, "調整で現在区間を数え直す");
    }

    [Test]
    public void ScreenDirectionRemovesInputRotationAndRejectsStationarySamples()
    {
        var camera = root.AddComponent<Camera>();
        camera.orthographic = true; camera.orthographicSize = 3;
        camera.transform.position = new Vector3(0, 0, -10);
        Vector3 raw = new Vector3(.5f, .2f, 0);
        Vector3 from = WorldTurn.MapInput(raw, 0), to = WorldTurn.MapInput(raw, 180);
        var staticCut = SaberCutRecord.Create(1, 1, false, from, to, 0, 180, 2.4f, camera, true);
        Assert.IsFalse(staticCut.DirectionValid, "入力回転だけで動いたように見せない");
        camera.transform.rotation = Quaternion.Euler(0, 0, 180);
        from = WorldTurn.MapInput(raw, 180);
        to = WorldTurn.MapInput(raw + Vector3.up, 180);
        var up = SaberCutRecord.Create(2, 2, false, from, to, 180, 180, 2.4f, camera, true);
        Assert.IsTrue(up.DirectionValid);
        Assert.Greater(up.Direction.y, 0);
        Assert.That(up.Direction.x, Is.EqualTo(0).Within(.001));
    }

    [Test]
    public void FourthBatchConservativeEnvelopeFitsWithoutChangingProtocolLimits()
    {
        // 同時成立しない条件を7組・難易度・失敗数で除いた上限。残る不可能な組は安全側に含める。
        string[] ids = "clear setsujoku xcut finale practice octave firstcut silence fivekinds eclipse rainbow arrows goldpair dual learnpair lefthand goldall chorus combo50 reswing kiriage lunchtime backpick mochitsuki holdcut sweep3 ittoryu rising scenemaster triple ranks center10 storm secret_s balance comeback jackpot secret souage harvest duet xcut10 redblue buzzer pairrainbow trend firstever hintkey ichiban lastpiece urafutari secretall slowstart streak20 combo100 jihou duo150k splus handperfect slidein streak50 sebango datescore early lastsec pioneer ichien lastrainbow twinprime zorome modulate school seisho fubuki".Split(' ');
        Assert.AreEqual(74, ids.Length);
        Assert.AreEqual(754, ids.Sum(id => id.Length + 3));
        Assert.AreEqual(80, RankingSyncLog.MaxAchievementsPerPlay);
        Assert.AreEqual(1200, RankingSyncLog.MaxDatagramBytes);
        Assert.AreEqual("BTSRANK3 ", RankingSyncLog.Prefix);
        foreach (string id in ids) Assert.NotNull(AchievementCatalog.Find(id), id);
        foreach (string songId in SongSelectController.EnumerateSongIds())
        {
            var play = new RankingPlay { id = new string('a', 32), song = songId, diff = "Normal",
                score = 99999, rank = "S+", acc = 1f / 3, date = "2026/10/29", at = 1793232000000,
                st = new string('A', 16), ns = new string('f', OperatorMode.MaxNamespaceLength), ach = ids.ToList() };
            Assert.IsTrue(RankingSyncLog.IsValid(play), songId);
            var log = new RankingSyncLog();
            Assert.IsTrue(log.Add(play));
            var bundles = log.Bundles(new string('f', RankingSyncLog.MaxIdLength), new string('A', 16));
            Assert.AreEqual(1, bundles.Count);
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(bundles[0].Text), 1200);
        }
    }
}
