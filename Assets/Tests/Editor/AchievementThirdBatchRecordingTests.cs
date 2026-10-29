using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 第3弾で増やす記録。自動起動や本番の保存を使わず、通知の受け取りと回・来場者の寿命を確かめる。
public class AchievementThirdBatchRecordingTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    GameObject root;
    PlayStatsRecorder recorder;
    Func<IReadOnlyList<RankingPlay>> savedPlays;
    Func<string, long, int> savedClears;
    Func<IReadOnlyCollection<string>> savedSongs;

    [SetUp]
    public void SetUp()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        savedPlays = AchievementTracker.PlaysSource;
        savedClears = AchievementTracker.ClearsSince;
        savedSongs = AchievementTracker.SongsSource;
        AchievementTracker.PlaysSource = () => Array.Empty<RankingPlay>();
        AchievementTracker.ClearsSince = (ns, at) => -1;
        AchievementTracker.SongsSource = () => Array.Empty<string>();
        AchievementRunInfo.Clear();
        root = new GameObject("AchievementThirdBatchTest");
        GameSession.SelectedSongId = "__AchievementThirdBatchTest";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.AchievementRunId = "third-test";
    }

    [TearDown]
    public void TearDown()
    {
        recorder?.Unbind();
        recorder = null;
        UnityEngine.Object.DestroyImmediate(root);
        AchievementRunInfo.Clear();
        AchievementTracker.PlaysSource = savedPlays;
        AchievementTracker.ClearsSince = savedClears;
        AchievementTracker.SongsSource = savedSongs;
        foreach (var entry in session) entry.Key.SetValue(null, entry.Value);
        session.Clear();
    }

    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
    static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
    static void Property(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);

    AchievementTracker Tracker()
    {
        var tracker = AchievementTracker.CreateForTest();
        tracker.transform.SetParent(root.transform);
        return tracker;
    }

    static TutorialController.TutorialStepRecord Step(string id, string reason = "ok", bool firstLoop = false) =>
        new TutorialController.TutorialStepRecord { Id = id, Reason = reason, Seconds = 1, FirstLoop = firstLoop };

    static AchievementContext Context() => AchievementTracker.BuildContext(null, new DateTime(2026, 10, 29, 12, 0, 0));

    [TestCase(SaberHand.Left, 0f, 1f, true)]
    [TestCase(SaberHand.Right, 1f, 1f, true)]
    [TestCase(SaberHand.Left, -1f, 1f, true)]
    [TestCase(SaberHand.Left, 1.01f, 1f, false)]
    [TestCase(SaberHand.Left, 0f, 0f, false)]
    [TestCase(SaberHand.Right, 0f, -1f, false)]
    [TestCase(SaberHand.Any, 0f, 1f, false)]
    public void TitleDirectionComesThroughTheTrackerAndLivesUntilARecordedRun(SaberHand hand, float x, float y, bool expected)
    {
        var tracker = Tracker();
        var note = root.AddComponent<CuttableNote>();
        Property(note, "LastCutterHand", hand);
        Call(tracker, "TitleCut", note, Vector3.zero, new Vector3(x, y, 0));
        AchievementRunInfo.BeginRun("third-test");
        Assert.AreEqual(expected, Context().TitleUpward);
        Assert.AreEqual(hand, Context().TitleHand);

        GameSession.AchievementRunId = "unrecorded-next";
        AchievementRunInfo.BeginRun(GameSession.AchievementRunId);
        Assert.AreEqual(expected, Context().TitleUpward, "記録されなかった曲では使い切らない");
        AchievementRunInfo.RunRecorded("third-test");
        Assert.AreEqual(expected, AchievementRunInfo.PendingTitleUpward, "別の回では消さない");
        AchievementRunInfo.RunRecorded(GameSession.AchievementRunId);
        GameSession.AchievementRunId = "after-recorded";
        AchievementRunInfo.BeginRun(GameSession.AchievementRunId);
        Assert.IsFalse(Context().TitleUpward);
        Assert.AreEqual(SaberHand.Any, Context().TitleHand);

        AchievementRunInfo.TitleCut(SaberHand.Left, Vector3.up);
        AchievementRunInfo.BeginRun("visitor-old");
        AchievementRunInfo.BeginVisitor();
        Assert.IsFalse(AchievementRunInfo.PendingTitleUpward);
        Assert.IsFalse(AchievementRunInfo.ForRun("visitor-old", out _, out _));
    }

    [TestCase("ok", "ok", true)]
    [TestCase("timeout", "ok", false)]
    [TestCase("ok", "skipped", false)]
    [TestCase("done", "ok", false)]
    public void PracticePairRequiresBothOkRecordsFromOnePractice(string arrow, string longNote, bool expected)
    {
        AchievementRunInfo.PracticeFinished(false, "done",
            new[] { Step("arrowEasy", arrow), Step("long", longNote), Step(TutorialProgram.AllStepId, firstLoop: true) });
        AchievementRunInfo.BeginRun("third-test");
        Assert.AreEqual(expected, Context().PracticePairBefore);
        Assert.AreEqual(AchievementRunInfo.PracticedBefore, Context().PracticedBefore);
        Assert.AreEqual(AchievementRunInfo.PracticedBefore, Context().PracticeAllFirstLoop);

        GameSession.AchievementRunId = "retry";
        AchievementRunInfo.BeginRun("retry");
        Assert.AreEqual(expected, Context().PracticePairBefore);
        AchievementRunInfo.RunRecorded("retry");
        GameSession.AchievementRunId = "next";
        AchievementRunInfo.BeginRun("next");
        Assert.IsFalse(Context().PracticePairBefore);
        Assert.IsFalse(Context().PracticeAllFirstLoop);
        Assert.IsFalse(Context().PracticedBefore);
    }

    [Test]
    public void PracticeDoesNotInventMissingStagesOrCombineDifferentPractices()
    {
        AchievementRunInfo.PracticeFinished(false, "done", new[] { Step("arrowEasy") });
        AchievementRunInfo.PracticeFinished(false, "done", new[] { Step("long") });
        AchievementRunInfo.BeginRun("third-test");
        Assert.IsFalse(Context().PracticePairBefore);
        Assert.IsFalse(Context().PracticeAllFirstLoop);

        AchievementRunInfo.Clear();
        AchievementRunInfo.PracticeFinished(true, "done",
            new[] { Step("arrowEasy"), Step("long"), Step("all", firstLoop: true) });
        AchievementRunInfo.BeginRun("third-test");
        Assert.IsFalse(Context().PracticePairBefore);
        Assert.IsFalse(Context().PracticedBefore);
        Assert.IsFalse(Context().PracticeAllFirstLoop);

        AchievementRunInfo.PracticeFinished(false, "done",
            new[] { Step("arrowEasy"), Step("long"), Step("all", firstLoop: true), Step("tapRed", "timeout") });
        AchievementRunInfo.BeginRun("third-test");
        Assert.IsTrue(Context().PracticePairBefore, "2段の ok はある");
        Assert.IsFalse(Context().PracticeAllFirstLoop, "practice が不成立");

        AchievementRunInfo.BeginVisitor();
        AchievementRunInfo.BeginRun("third-test");
        Assert.IsFalse(Context().PracticePairBefore);
        Assert.IsFalse(Context().PracticeAllFirstLoop);
    }

    [TestCase("all", "ok", -.001, true)]
    [TestCase("all", "ok", 0, false)]
    [TestCase("all", "ok", .001, false)]
    [TestCase("all", "done", -.001, false)]
    [TestCase("all", "timeout", -.001, false)]
    [TestCase("long", "ok", -.001, false)]
    public void TutorialRecordsTheFirstPatternDeadline(string id, string reason, double delta, bool expected)
    {
        var controller = root.AddComponent<TutorialController>();
        var spawner = root.AddComponent<NoteSpawner>();
        var step = TutorialProgram.Get(id);
        Set(controller, "spawner", spawner);
        Set(controller, "steps", new[] { step });
        Call(controller, "BeginStep", 0, 0d);
        double first = Field<double>(controller, "firstPatternAt");
        Assert.AreEqual(Field<double>(controller, "nextPatternAt"), first);
        // 先読みで次の型の時刻が進んでも、最初の型の時刻は変えない。
        Set(controller, "nextPatternAt", first + 2 * step.PeriodSeconds);
        Call(controller, "Complete", first + step.PeriodSeconds + delta, reason);
        Assert.AreEqual(1, controller.Records.Count);
        Assert.AreEqual(reason, controller.Records[0].Reason);
        Assert.AreEqual(expected, controller.Records[0].FirstLoop);
    }

    [TestCase(true, false, true, 1)]
    [TestCase(false, false, true, 0)]
    [TestCase(true, true, true, 0)]
    [TestCase(true, false, false, 0)]
    public void HintsAreCapturedAtTheUnlockingCut(bool hintBefore, bool restored, bool sameSong, int expected)
    {
        var select = root.AddComponent<SongSelectController>();
        var skin = root.AddComponent<SongSelectSkin>();
        var tracker = Tracker();
        Call(tracker, "WatchSongSelect");
        var hint = Field<SongSelectSecretHint>(skin, "secretHint");
        Assert.AreEqual(0, skin.SecretHintsShown);
        if (hintBefore) hint.Show(0);
        var secret = select.RushESecret;
        secret.Select(RushESecretState.RushE.SongId);
        if (restored)
        {
            secret.RestoreUnlocked();
            Field<Action>(select, "OnRushESecretChanged")?.Invoke();
        }
        else
        {
            for (int i = 0; i < RushESecretState.RequiredHits; i++)
            {
                Assert.IsTrue(secret.TryHit(i * .2));
                Field<Action>(select, "OnRushESecretChanged")?.Invoke();
            }
            // 最後の切断から Reveal までの間に読んだ分を加えない。
            hint.Show(10);
            Assert.IsTrue(secret.Reveal());
            Field<Action>(select, "OnRushESecretChanged")?.Invoke();
            Assert.IsTrue(secret.Finish());
            Field<Action>(select, "OnRushESecretChanged")?.Invoke();
        }
        Assert.AreEqual(hint.Shown, skin.SecretHintsShown);
        GameSession.SelectedSongId = sameSong ? RushESecretState.RushE.SongId : RushESecretState.MorningMemory.SongId;
        Property(select, "StartAccepted", true);
        Call(tracker, "WatchSongSelect");
        AchievementRunInfo.BeginRun("third-test");
        Assert.AreEqual(expected, Context().HintsBeforeOpen);
        Assert.AreEqual(!restored && sameSong, Context().OpenedSecretHere);
        Assert.AreEqual(expected > 0, AchievementCatalog.Find("hintkey").Met(Context()));

        // 判定調整から戻った新しい選曲では、表示回数も解除の記録も新しくする。
        Call(tracker, "ResetSceneState");
        UnityEngine.Object.DestroyImmediate(skin);
        skin = root.AddComponent<SongSelectSkin>();
        Property(select, "StartAccepted", false);
        secret.Reset();
        secret.RestoreUnlocked();
        Set(tracker, "calibrationReturn", true);
        Call(tracker, "WatchSongSelect");
        Property(select, "StartAccepted", true);
        Call(tracker, "WatchSongSelect");
        GameSession.AchievementRunId = "after-calibration";
        AchievementRunInfo.BeginRun(GameSession.AchievementRunId);
        Assert.AreEqual(0, skin.SecretHintsShown);
        Assert.AreEqual(0, Context().HintsBeforeOpen);
        Assert.IsFalse(Context().OpenedSecretHere);
        Assert.IsNull(Context().SelectionSeconds);
    }

    GamePlayManager PrepareRecorder(ChartData chart, out NoteSpawner spawner, out ScoreManager score)
    {
        spawner = root.AddComponent<NoteSpawner>();
        score = root.AddComponent<ScoreManager>();
        score.Bind(spawner);
        spawner.noteRoot = root.transform;
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        var prefab = new GameObject("Note", typeof(CuttableNote));
        prefab.transform.SetParent(root.transform);
        prefab.GetComponent<CuttableNote>().shatterDebrisCount = 0;
        spawner.notePrefab = spawner.notePrefabRed = spawner.notePrefabBlue = prefab;
        var manager = root.AddComponent<GamePlayManager>();
        manager.enabled = false;
        manager.noteSpawner = spawner;
        manager.scoreManager = score;
        spawner.SetChart(chart);
        recorder = PlayStatsRecorder.TryBind(manager, GameSession.AchievementRunId);
        Assert.NotNull(recorder);
        return manager;
    }

    static CuttableNote Live(NoteSpawner spawner, double time) =>
        spawner.LiveNotes.Single(n => n != null && !n.IsFinalized && Math.Abs(n.HitTime - time) < 1e-6);

    static void Cut(CuttableNote note, double time)
    {
        var hand = note.RequiredHand == SaberHand.Any ? SaberHand.Right : note.RequiredHand;
        Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position, Vector3.up * 12, hand, time, .866f));
    }

    [Test]
    public void CutsDoneComesFromFinalJudgmentsIncludingPartialTimeouts()
    {
        var chart = PlayStatsTests.Chart(
            PlayStatsTests.Note(1000, count: 4), PlayStatsTests.Note(5000, "blue", count: 4),
            PlayStatsTests.Note(9000, count: 4), PlayStatsTests.Note(12000));
        PrepareRecorder(chart, out var spawner, out var score);
        spawner.Tick(1);
        var full = Live(spawner, 1);
        for (int i = 0; i < 4; i++) Cut(full, 1 + i * .1);
        spawner.Tick(5);
        var partial = Live(spawner, 5);
        Cut(partial, 5);
        Cut(partial, 5.1);
        partial.MarkMiss();
        spawner.Tick(9);
        Live(spawner, 9).MarkMiss();
        spawner.Tick(12);
        Cut(Live(spawner, 12), 12);
        var stats = recorder.Snapshot();
        CollectionAssert.AreEqual(new[] { 4, 2, 0, 1 }, stats.Notes.Select(n => n.CutsDone).ToArray());
        CollectionAssert.AreEqual(new JudgmentTier?[] { JudgmentTier.Perfect, JudgmentTier.Miss, JudgmentTier.Miss, JudgmentTier.Perfect },
            stats.Notes.Select(n => n.Result).ToArray());

        spawner.SetChart(chart);
        score.Reset();
        recorder.Tick();
        Assert.AreNotSame(stats, recorder.Stats);
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 0 }, recorder.Stats.Notes.Select(n => n.CutsDone).ToArray());
        spawner.Tick(1);
        var detached = recorder.Stats;
        recorder.Unbind();
        var note = Live(spawner, 1);
        Cut(note, 1);
        note.MarkMiss();
        Assert.AreEqual(0, detached.Notes[0].CutsDone);
        Assert.IsNull(detached.Notes[0].Result);
    }

    [Test]
    public void VoltageReachesSubscribeOnceFollowReplacementAndResetWithTheChart()
    {
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000));
        var manager = PrepareRecorder(chart, out var spawner, out var score);
        recorder.Tick();
        var show = manager.gameObject.AddComponent<PlayExcitement>();
        var meter = new VoltageMeter();
        Property(show, "Meter", meter);
        recorder.Tick();
        recorder.Tick();
        meter.ObserveCombo(32, false);
        Assert.AreEqual(1, recorder.Stats.VoltageMaxReaches);
        meter.ObserveCombo(40, false);
        Assert.AreEqual(1, recorder.Stats.VoltageMaxReaches, "4を維持した更新は再到達でない");
        meter.ObserveCombo(0, true);
        meter.ObserveCombo(8, false);
        Assert.AreEqual(2, recorder.Stats.VoltageMaxReaches);
        Assert.IsTrue(AchievementCatalog.Find("regain").Met(new AchievementContext { Stats = recorder.Snapshot() }));

        var before = recorder.Stats;
        spawner.SetChart(chart);
        score.Reset();
        meter.ObserveCombo(0, true);
        recorder.Tick();
        Assert.AreNotSame(before, recorder.Stats);
        Assert.AreEqual(0, recorder.Stats.VoltageMaxReaches);
        meter.ObserveCombo(8, false);
        Assert.AreEqual(1, recorder.Stats.VoltageMaxReaches);

        var replacement = new VoltageMeter(3);
        Property(show, "Meter", replacement);
        recorder.Tick();
        meter.ObserveCombo(0, true);
        meter.ObserveCombo(8, false);
        Assert.AreEqual(1, recorder.Stats.VoltageMaxReaches, "古い Meter は外れている");
        replacement.ObserveCombo(8, false);
        Assert.AreEqual(2, recorder.Stats.VoltageMaxReaches);
        recorder.Unbind();
        replacement.ObserveCombo(0, true);
        replacement.ObserveCombo(8, false);
        Assert.AreEqual(2, recorder.Stats.VoltageMaxReaches, "退出後は増えない");
    }

    [Test]
    public void AContextForAnotherRunDoesNotReceivePreparationRecords()
    {
        AchievementRunInfo.TitleCut(SaberHand.Left, Vector3.up);
        AchievementRunInfo.PracticeFinished(false, "done",
            new[] { Step("arrowEasy"), Step("long"), Step("all", firstLoop: true) });
        AchievementRunInfo.SongChosen(50, false, true, 2);
        AchievementRunInfo.BeginRun("third-test");
        Assert.IsTrue(Context().PracticePairBefore);
        Assert.IsTrue(Context().PracticeAllFirstLoop);
        Assert.IsTrue(Context().TitleUpward);
        Assert.AreEqual(2, Context().HintsBeforeOpen);
        GameSession.AchievementRunId = "different";
        var other = Context();
        Assert.IsFalse(other.PracticePairBefore || other.PracticeAllFirstLoop || other.TitleUpward || other.OpenedSecretHere);
        Assert.AreEqual(0, other.HintsBeforeOpen);
        Assert.IsNull(other.SelectionSeconds);
    }
}
