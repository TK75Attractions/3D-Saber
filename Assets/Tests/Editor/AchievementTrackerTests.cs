using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 実績の判定と解除(AchievementTracker・AchievementRunInfo、2026-10-29)。1回の演奏で1回だけ解除して番号(○人目)を出す・
// clear を先に・キリ番・名前空間ごと・今日の clear の数・結果の写し・選曲の秒と練習の終わり方。
// 実績の記録は専用の鍵だけを使い、テストの後で消す(本番の Achievements.Log.v1 には触れない)。
public class AchievementTrackerTests
{
    const string Key = "Achievements.Log.v1.__AchievementTrackerTest";
    const string Fes = OperatorMode.FestivalNamespace;
    static readonly DateTime Noon = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Local);
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    string savedKey;
    Func<IReadOnlyList<RankingPlay>> savedPlays;
    Func<string, long, int> savedClears;

    [SetUp]
    public void UseTestKey()
    {
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        savedKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(Key);
        savedPlays = AchievementTracker.PlaysSource;
        savedClears = AchievementTracker.ClearsSince;
        GameSession.FinalAchievements = new List<string>();
        AchievementRunInfo.Clear();
    }

    [TearDown]
    public void Restore()
    {
        PlayerPrefs.DeleteKey(Key);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedKey);
        AchievementTracker.PlaysSource = savedPlays;
        AchievementTracker.ClearsSince = savedClears;
        AchievementRunInfo.Clear();
        foreach (var entry in session) entry.Key.SetValue(null, entry.Value);
        session.Clear();
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    static AchievementContext Ctx(string run, string ns = "", Action<AchievementContext> change = null)
    {
        var c = new AchievementContext
        {
            SongId = "__AchievementTrackerTest", Difficulty = "Normal",
            Score = 30123, MaxCombo = 10, Hit = 10, Perfect = 5, Great = 3, Good = 2, Miss = 2, CutCount = 10, Accuracy = .5f,
            Station = "A", Namespace = ns, RunId = run,
            StartedLocal = Noon.AddMinutes(-3), EndedLocal = Noon, EndedUnixMs = new DateTimeOffset(Noon).ToUnixTimeMilliseconds(),
            ClearsToday = 5,
        };
        change?.Invoke(c);
        return c;
    }

    static void Excellent(AchievementContext c) { c.Score = 95123; c.MaxCombo = 120; c.Miss = 0; c.Perfect = 10; c.Great = c.Good = 0; }

    static List<string> Ids(List<AchievementUnlock> unlocks) => unlocks.ConvertAll(u => u.Id);

    [Test]
    public void RecordUnlocksEachAchievementOnceAndReturnsItsNumberInDisplayOrder()
    {
        string run = NewId();
        var unlocks = AchievementTracker.Record(Ctx(run, change: Excellent));
        CollectionAssert.AreEqual(new[] { "ap", "fc", "splus", "combo100", "ranks", "combo50", "clear" }, Ids(unlocks), "珍しい順、clear が最後");
        foreach (var unlock in unlocks) Assert.AreEqual(1, unlock.Number, unlock.Id + " の1人目");
        Assert.AreEqual("clear", GameSession.FinalAchievements[0], "相手の台へ送る記録では clear を先に");
        CollectionAssert.AreEquivalent(Ids(unlocks), GameSession.FinalAchievements);
        CollectionAssert.AreEquivalent(Ids(unlocks), AchievementStore.UnlockedIn("", run));

        // 同じ回をもう一度決めても、数は増えず、同じ番号のまま
        var again = AchievementTracker.Record(Ctx(run, change: Excellent));
        CollectionAssert.AreEqual(Ids(unlocks), Ids(again));
        foreach (var unlock in again) Assert.AreEqual(1, unlock.Number);
        Assert.AreEqual(unlocks.Count, GameSession.FinalAchievements.Count, "FinalAchievements に二重に入れない");
        Assert.AreEqual(1, AchievementStore.Count("", "clear"));
        Assert.AreEqual(1, AchievementStore.Count("", "ap"));

        // 次の人(別の回)
        GameSession.FinalAchievements = new List<string>();
        var next = AchievementTracker.Record(Ctx(NewId()));
        CollectionAssert.AreEqual(new[] { "clear" }, Ids(next));
        Assert.AreEqual(2, next[0].Number, "2人目");
        Assert.AreEqual(1, AchievementStore.Count("", "ap"));
        CollectionAssert.AreEqual(new[] { "clear" }, GameSession.FinalAchievements);
    }

    // 曲の終わりの保存は、clear(キリ番の番号のため先に)と残りのまとめての2回まで。
    [Test]
    public void ARunSavesTheRecordAtMostTwice()
    {
        int before = AchievementStore.WriteCountForTest;
        var unlocks = AchievementTracker.Record(Ctx(NewId(), change: Excellent));
        Assert.AreEqual(7, unlocks.Count);
        Assert.LessOrEqual(AchievementStore.WriteCountForTest - before, 2);
        before = AchievementStore.WriteCountForTest;
        Assert.AreEqual(1, AchievementTracker.Record(Ctx(NewId())).Count);
        Assert.LessOrEqual(AchievementStore.WriteCountForTest - before, 1, "clear だけの回は1回");
    }

    [Test]
    public void KiribanUsesTheNumberOfThisRunsClear()
    {
        long at = new DateTimeOffset(Noon.AddDays(-1)).ToUnixTimeMilliseconds();
        for (int i = 0; i < AchievementCatalog.KiribanEvery - 2; i++) AchievementStore.Merge("", NewId(), new[] { "clear" }, "B", at);
        Assert.AreEqual(1, AchievementTracker.Record(Ctx(NewId())).Count, "99人目はキリ番ではない");
        var hundredth = AchievementTracker.Record(Ctx(NewId()));
        CollectionAssert.AreEqual(new[] { "kiriban", "clear" }, Ids(hundredth));
        Assert.AreEqual(AchievementCatalog.KiribanEvery, hundredth[1].Number);
        Assert.AreEqual(1, hundredth[0].Number, "キリ番の1人目");
        Assert.AreEqual(1, AchievementTracker.Record(Ctx(NewId())).Count, "101人目");
    }

    [Test]
    public void EachModeHasItsOwnNumbers()
    {
        Assert.AreEqual(1, AchievementTracker.Record(Ctx(NewId(), Fes))[0].Number);
        Assert.AreEqual(1, AchievementTracker.Record(Ctx(NewId(), ""))[0].Number, "ふだんの数は1人目から");
        Assert.AreEqual(2, AchievementTracker.Record(Ctx(NewId(), Fes))[0].Number);
        Assert.AreEqual(2, AchievementStore.Count(Fes, "clear"));
        Assert.AreEqual(1, AchievementStore.Count("", "clear"));
    }

    [Test]
    public void RecordWithoutARunDoesNothing()
    {
        Assert.AreEqual(0, AchievementTracker.Record(null).Count);
        Assert.AreEqual(0, AchievementTracker.Record(Ctx("")).Count);
        Assert.AreEqual(0, GameSession.FinalAchievements.Count);
        Assert.AreEqual(0, AchievementStore.EntryCount);
    }

    // 一番乗りの数え方は AchievementStore.CountSince(この台の解除と、相手の台から足した分・同期のメモリの記録)。読めなければ -1 で、出さない。
    [Test]
    public void ClearsSinceCountsTheClearsOfThisModeFromThatTime()
    {
        long since = new DateTimeOffset(Noon.Date).ToUnixTimeMilliseconds();
        Assert.AreEqual(0, AchievementTracker.ClearsSince("", since), "記録が無い");
        string today = NewId();
        AchievementStore.Unlock("", NewId(), "clear", "A", since - 1);
        AchievementStore.Unlock("", today, "clear", "A", since);
        AchievementStore.Unlock(Fes, NewId(), "clear", "A", since + 1000);
        AchievementStore.Unlock("", NewId(), "fc", "A", since + 2000);
        AchievementStore.Merge("", NewId(), new[] { "clear" }, "B", since + 3000);
        Assert.AreEqual(AchievementStore.CountSince("", "clear", since), AchievementTracker.ClearsSince("", since));
        Assert.AreEqual(2, AchievementTracker.ClearsSince("", since), "相手の台から足した分も入る");
        Assert.AreEqual(1, AchievementTracker.ClearsSince(Fes, since));
        Assert.AreEqual(3, AchievementTracker.ClearsSince("", since - 1));
    }

    [Test]
    public void IchibanGoesToTheFirstClearOfTheDayAndNotWhenTheRecordIsUnknown()
    {
        AchievementTracker.PlaysSource = () => Array.Empty<RankingPlay>();
        long since = new DateTimeOffset(Noon.Date).ToUnixTimeMilliseconds();
        AchievementStore.Unlock("", NewId(), "clear", "B", since - 60000);
        var first = Ctx(NewId(), change: c => c.ClearsToday = AchievementTracker.ClearsSince("", since));
        Assert.AreEqual(0, first.ClearsToday);
        CollectionAssert.Contains(Ids(AchievementTracker.Record(first)), "ichiban");
        var second = Ctx(NewId(), change: c => c.ClearsToday = AchievementTracker.ClearsSince("", since));
        Assert.AreEqual(1, second.ClearsToday, "1人目の clear が今日の分に入る");
        CollectionAssert.DoesNotContain(Ids(AchievementTracker.Record(second)), "ichiban");
        CollectionAssert.DoesNotContain(Ids(AchievementTracker.Record(Ctx(NewId(), change: c => c.ClearsToday = -1))), "ichiban",
            "記録が読めない(分からない)ときは出さない");
    }

    [Test]
    public void BuildContextCopiesTheResultOfTheRun()
    {
        GameSession.ResetResult();
        string run = GameSession.AchievementRunId;
        GameSession.SelectedSongId = RushESecretState.AndalusiaBull.SongId;
        GameSession.SelectedDifficulty = RushESecretState.AndalusiaBull.Difficulty;
        GameSession.RunNamespace = Fes;
        GameSession.FinalScore = 81234; GameSession.FinalMaxCombo = 77; GameSession.FinalHit = 90; GameSession.FinalMiss = 3;
        GameSession.FinalPerfect = 60; GameSession.FinalGreat = 20; GameSession.FinalGood = 6; GameSession.FinalBad = 4;
        GameSession.FinalCutCount = 95;
        GameSession.FinalDailyRanking = new DailyRankingStore.Result { Rank = 2, TotalPlays = 9, TiedPlays = 1 };
        var plays = new List<RankingPlay> { new RankingPlay { id = NewId(), song = "x", ns = Fes } };
        AchievementTracker.PlaysSource = () => plays;
        AchievementRunInfo.SongChosen(AchievementRunInfo.SelectionStartSeconds - 12, false);
        AchievementRunInfo.PracticeFinished(false, "done", null);
        AchievementRunInfo.BeginRun(run);
        var stats = PlayStatsTests.Stats(PlayStatsTests.Chart(PlayStatsTests.Note(1000)));
        stats.StartedAt = Noon.AddMinutes(-2);
        stats.EndedAt = Noon.AddSeconds(-6);
        var asked = new List<(string ns, long since)>();
        AchievementTracker.ClearsSince = (ns, since) => { asked.Add((ns, since)); return 0; };

        var c = AchievementTracker.BuildContext(stats, Noon);
        Assert.AreEqual(RushESecretState.AndalusiaBull.SongId, c.SongId);
        Assert.AreEqual(RushESecretState.AndalusiaBull.Difficulty, c.Difficulty);
        Assert.IsTrue(c.Secret);
        Assert.AreEqual(81234, c.Score);
        Assert.AreEqual(77, c.MaxCombo);
        Assert.AreEqual(90, c.Hit); Assert.AreEqual(3, c.Miss); Assert.AreEqual(60, c.Perfect); Assert.AreEqual(20, c.Great);
        Assert.AreEqual(6, c.Good); Assert.AreEqual(4, c.Bad); Assert.AreEqual(95, c.CutCount);
        Assert.AreEqual(GameSession.FinalAccuracy, c.Accuracy);
        Assert.IsTrue(c.RankingAvailable); Assert.AreEqual(2, c.DailyRank); Assert.AreEqual(9, c.TotalPlays); Assert.AreEqual(1, c.TiedPlays);
        Assert.AreSame(stats, c.Stats);
        Assert.AreEqual(Noon.AddMinutes(-2), c.StartedLocal, "曲が鳴り始めた時刻");
        Assert.AreEqual(Noon.AddSeconds(-6), c.EndedLocal, "曲が終わった時刻(終わりの演出の前)。決めた時刻ではない");
        Assert.AreEqual(new DateTimeOffset(Noon.AddSeconds(-6)).ToUnixTimeMilliseconds(), c.EndedUnixMs);
        Assert.AreEqual(Fes, c.Namespace, "その回の名前空間(曲を始めたときの運営モード)");
        Assert.AreEqual(run, c.RunId);
        Assert.AreEqual(12, c.SelectionSeconds.Value, 1e-9);
        Assert.IsTrue(c.PracticedBefore);
        Assert.AreSame(plays, c.Plays);
        Assert.AreEqual(0, c.ClearsToday);
        CollectionAssert.AreEqual(new[] { (Fes, new DateTimeOffset(Noon.Date).ToUnixTimeMilliseconds()) }, asked, "その回のモードの、終えた日の分");

        GameSession.FinalDailyRanking = null;
        AchievementTracker.ClearsSince = (ns, since) => -1;
        var noRanking = AchievementTracker.BuildContext(null, Noon);
        Assert.IsFalse(noRanking.RankingAvailable);
        Assert.AreEqual(Noon, noRanking.EndedLocal, "演奏の記録が無ければ、決めた時刻");
        Assert.AreEqual(Noon, noRanking.StartedLocal, "始めた時刻が分からなければ終わりの時刻");
        Assert.AreEqual(-1, noRanking.ClearsToday);
        GameSession.ResetResult();
        Assert.IsFalse(AchievementTracker.BuildContext(null, Noon).PracticedBefore, "別の回には練習を付けない");
    }

    // となりの台と同じ曲(duet)は、RankingSync がメモリに持つ同期の記録(演奏中に届いた相手の台の回も入っている)を読む。
    // 同期が動いていなければ空。
    [Test]
    public void DuetReadsTheSyncedPlaysOfRankingSync()
    {
        Assert.AreEqual(0, AchievementTracker.PlaysSource().Count, "同期が動いていなければ空");
        const string logKey = "RankingSync.Log.v1.__AchievementTrackerTest", pendingKey = "RankingSync.Pending.v1.__AchievementTrackerTest";
        var sync = RankingSync.Create(new RankingSync.Settings
        {
            Offline = true, Broadcast = false, MachineId = NewId(), Station = "B", LogKey = logKey, PendingKey = pendingKey, ShowNotices = false,
        });
        try
        {
            var theirs = RankingSyncLog.NewPlay(NewId(), "__AchievementTrackerTest", "Hard", 1000, "C", .5f, Noon.AddSeconds(-20), "B", Fes,
                new[] { "clear" });
            Assert.IsTrue(sync.Record(theirs));
            var context = Ctx(NewId(), Fes, c => c.Plays = sync.LoggedPlays);
            Assert.AreEqual(1, context.Plays.Count);
            CollectionAssert.Contains(Ids(AchievementTracker.Record(context)), "duet", "20秒前にとなりの台が同じ曲を終えた");
        }
        finally
        {
            sync.Shutdown();
            UnityEngine.Object.DestroyImmediate(sync.gameObject);
            PlayerPrefs.DeleteKey(logKey);
            PlayerPrefs.DeleteKey(pendingKey);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void TestAndDiagnosticLaunchesDoNotDecideAchievements()
    {
        Assert.IsTrue(AchievementTracker.EnabledHere(new[] { "game.exe" }));
        Assert.IsTrue(AchievementTracker.EnabledHere(null));
        Assert.IsTrue(AchievementTracker.EnabledHere(new[] { "game.exe", RankingSync.SwitchArgument, "off" }), "同期を止めても実績は出す");
        foreach (string argument in new[] { RankingSync.TestRunArgument, PhoneSaberPlayerSmoke.Argument, PhoneSaberSoakModel.Argument,
                     PhoneSaberLatencyProbe.AutoArgument, PhoneSaberTrackingProbe.Argument })
            Assert.IsFalse(AchievementTracker.EnabledHere(new[] { "game.exe", argument }), argument);
    }

    [Test]
    public void SelectionSecondsAreCountedLikeTheSongSelectTimer()
    {
        Assert.AreEqual(new SongSelectCountdown().Remaining, AchievementRunInfo.SelectionStartSeconds);
        AchievementRunInfo.SongChosen(AchievementRunInfo.SelectionStartSeconds - 15, false);
        Assert.AreEqual(15, AchievementRunInfo.PendingSelectionSeconds.Value, 1e-9);
        AchievementRunInfo.BeginRun("run-one-1");
        Assert.IsNull(AchievementRunInfo.PendingSelectionSeconds, "曲を始めたら使い切る");
        Assert.IsTrue(AchievementRunInfo.ForRun("run-one-1", out var seconds, out _));
        Assert.AreEqual(15, seconds.Value, 1e-9);
        Assert.IsFalse(AchievementRunInfo.ForRun("run-two-2", out var other, out _));
        Assert.IsNull(other);
        AchievementRunInfo.SongChosen(90, true);
        Assert.IsNull(AchievementRunInfo.PendingSelectionSeconds, "判定調整から戻った選曲は数えない");
        AchievementRunInfo.SongChosen(null, false);
        Assert.IsNull(AchievementRunInfo.PendingSelectionSeconds);
        Assert.AreEqual(AchievementRunInfo.SelectionStartSeconds, AchievementRunInfo.SelectionElapsed(0), "時間切れの自動開始");
        Assert.AreEqual(0, AchievementRunInfo.SelectionElapsed(AchievementRunInfo.SelectionStartSeconds + 5));
        AchievementRunInfo.SongChosen(95, false);
        AchievementRunInfo.BeginVisitor();
        Assert.IsNull(AchievementRunInfo.PendingSelectionSeconds, "来場者が替わったら消す");
    }

    [Test]
    public void OnlyAFinishedFullPracticeCountsAndOnlyForTheNextSong()
    {
        var ok = new[] { Record("intro", "ok"), Record("range", "noanswer"), Record("tap", "ok") };
        Assert.IsTrue(AchievementRunInfo.CountsAsPractice(false, "done", ok), "振る範囲の返事が無いのは来場者のせいではない");
        Assert.IsTrue(AchievementRunInfo.CountsAsPractice(false, "done", null));
        Assert.IsFalse(AchievementRunInfo.CountsAsPractice(false, "skipped", ok));
        Assert.IsFalse(AchievementRunInfo.CountsAsPractice(false, "no-steps", ok));
        Assert.IsFalse(AchievementRunInfo.CountsAsPractice(true, "done", ok), "振る範囲だけの回");
        Assert.IsFalse(AchievementRunInfo.CountsAsPractice(false, "done", new[] { Record("tap", "ok"), Record("arrow", "timeout") }), "時間切れ");
        Assert.IsFalse(AchievementRunInfo.CountsAsPractice(false, "done", new[] { Record("tap", "skipped") }));

        AchievementRunInfo.PracticeFinished(false, "skipped", ok);
        Assert.IsFalse(AchievementRunInfo.PracticePending);
        AchievementRunInfo.PracticeFinished(false, "done", ok);
        Assert.IsTrue(AchievementRunInfo.PracticePending);
        AchievementRunInfo.BeginRun("first-song");
        Assert.IsTrue(AchievementRunInfo.ForRun("first-song", out _, out bool practiced) && practiced, "練習のあとの最初の1曲");
        AchievementRunInfo.BeginRun("second-song");
        AchievementRunInfo.ForRun("second-song", out _, out bool again);
        Assert.IsFalse(again, "2曲目には付けない");
        AchievementRunInfo.PracticeFinished(false, "done", ok);
        AchievementRunInfo.BeginVisitor();
        Assert.IsFalse(AchievementRunInfo.PracticePending, "次の来場者へ持ち越さない");
    }

    static TutorialController.TutorialStepRecord Record(string id, string reason) =>
        new TutorialController.TutorialStepRecord { Id = id, Reason = reason, Seconds = 1 };
}
