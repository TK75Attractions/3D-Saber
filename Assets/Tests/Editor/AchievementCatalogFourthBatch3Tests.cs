using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

// 第4弾(3陣目)。保存・通信・Unity のシーン起動をせず、写しと同期の形を確かめる。
public class AchievementCatalogFourthBatch3Tests
{
    static readonly DateTime Noon = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Local);
    static readonly string[] Ids = { "daynight", "songswap", "sync", "omoteura", "tachiai", "neckneck", "pairfc",
        "honjitsu", "kinou_koe", "kinou_ima", "hitoributai", "shushifu", "fushime" };

    static AchievementContext Ctx(string ns = "") => new AchievementContext
    {
        RunId = "run-current", SongId = "Morning", Difficulty = "Normal", Station = "A", Namespace = ns,
        Score = 80000, Hit = 2, Perfect = 2, StartedLocal = Noon.AddMinutes(-2), EndedLocal = Noon,
        EndedUnixMs = new DateTimeOffset(Noon).ToUnixTimeMilliseconds(), ClearsToday = 0, ClearsBeforeTodayBatch5b = 0,
    };

    static PlayStats Stats(AchievementContext c, int count = 2)
    {
        var chart = PlayStatsTests.Chart(Enumerable.Range(0, count).Select(i => PlayStatsTests.Note(1000 + i * 1000)).ToArray());
        var stats = PlayStats.FromChart(chart, 0, NoteSpawner.DefaultSecondsPerLongCut, null, c.RunId);
        stats.StartedAt = c.StartedLocal;
        stats.EndedAt = c.EndedLocal;
        for (int i = 0; i < count; i++)
            stats.Record(i, JudgmentTier.Perfect, 0, c.EndedLocal.AddSeconds(-count + i), SaberHand.Left, false);
        return stats;
    }

    static RankingPlay Play(AchievementContext c, string id = "run-neighbor", DateTime? ended = null, string station = "B",
        string song = null, string diff = null, int score = 80000, int cuts = -1, int dur = -1) =>
        RankingSyncLog.NewPlay(id, song ?? c.SongId, diff ?? c.Difficulty, score,
            PlayRankHelper.Label(PlayRankHelper.FromScore(score)), .8f, ended ?? c.EndedLocal, station, c.Namespace,
            new[] { AchievementCatalog.ClearId }, cuts, -1, dur);

    static bool Has(AchievementContext c, string id)
    {
        var definition = AchievementCatalog.Find(id);
        Assert.NotNull(definition, id);
        return definition.Met(c);
    }

    static void NoteGate(AchievementContext c, string id)
    {
        Assert.IsTrue(Has(c, id), id);
        c.AimMode = true;
        Assert.IsFalse(Has(c, id), "D");
        c.AimMode = false;
        c.Stats.Complete = false;
        Assert.IsFalse(Has(c, id), "途中からの記録");
        c.Stats.Complete = true;
        var stats = c.Stats;
        c.Stats = null;
        Assert.IsFalse(Has(c, id), "記録なし");
        c.Stats = stats;
    }

    [Test]
    public void DefinitionsHaveTheRequestedIdsAndTiers()
    {
        CollectionAssert.AllItemsAreUnique(Ids);
        foreach (string id in Ids)
        {
            var d = AchievementCatalog.Find(id);
            Assert.NotNull(d, id);
            Assert.IsTrue(AchievementStore.IsValidId(id));
            Assert.LessOrEqual(id.Length, 12);
            Assert.AreEqual(id == "pairfc" ? AchievementTier.Rainbow :
                id == "neckneck" ? AchievementTier.Gold : AchievementTier.Bronze, d.Tier, id);
        }
        Assert.IsNull(AchievementCatalog.Find("awasewaza"));
        Assert.IsNull(AchievementCatalog.Find("lastorder"));
    }

    [Test]
    public void DaynightWorksBothWaysAndUsesTheNearestRun()
    {
        var c = Ctx();
        var p = Play(c, song: "2_23_AM", ended: Noon.AddSeconds(-60));
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "daynight"));
        p.at--;
        Assert.IsFalse(Has(c, "daynight"));
        p.at++;
        c.SongId = "2_23_AM"; p.song = "Morning";
        Assert.IsTrue(Has(c, "daynight"));
        var closer = Play(c, "run-closer", song: "RushE");
        c.Plays = new[] { p, closer };
        Assert.IsFalse(Has(c, "daynight"));
    }

    [Test]
    public void SongswapNeedsFourImmediateRunsWithinThirtyMinutes()
    {
        var c = Ctx();
        var other = Play(c, song: "RushE");
        var mineBefore = Play(c, "run-old-a", Noon.AddMinutes(-30), "A", "RushE");
        var otherBefore = Play(c, "run-old-b", Noon.AddMinutes(-10), "B", "Morning");
        c.Plays = new[] { otherBefore, other, mineBefore, mineBefore };
        Assert.IsTrue(Has(c, "songswap"));
        mineBefore.at--;
        Assert.IsFalse(Has(c, "songswap"));
        mineBefore.at++;
        c.Plays = new[] { other, mineBefore };
        Assert.IsFalse(Has(c, "songswap"), "片方の前の回が無い");
        c.Plays = new[] { other, mineBefore, otherBefore, Play(c, "run-middle", Noon.AddMinutes(-5), "A", "logic") };
        Assert.IsFalse(Has(c, "songswap"), "途中に別の曲");
        c.Plays = new[] { other, mineBefore, otherBefore };
        other.song = c.SongId;
        Assert.IsFalse(Has(c, "songswap"), "交換する曲は別々");
    }

    [Test]
    public void SyncNeedsSongDifficultyAndScoreRank()
    {
        var c = Ctx();
        var p = Play(c, diff: "normal", ended: Noon.AddSeconds(60));
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "sync"));
        p.score = 0;
        Assert.IsFalse(Has(c, "sync"));
        p.score = c.Score; p.diff = "Hard";
        Assert.IsFalse(Has(c, "sync"));
        p.diff = c.Difficulty; p.song = "RushE";
        Assert.IsFalse(Has(c, "sync"));
        p.song = c.SongId; p.at++;
        Assert.IsFalse(Has(c, "sync"));
    }

    [Test]
    public void OmoteuraLooksForEitherOrderAndRejectsUnknownDifficulty()
    {
        var c = Ctx();
        var p = Play(c, diff: "M", ended: Noon.AddSeconds(-60));
        c.Plays = new[] { Play(c, "run-closer"), p };
        Assert.IsTrue(Has(c, "omoteura"), "窓の中の同じ曲を探す");
        c.Plays = new[] { p };
        c.Difficulty = "M"; p.diff = "Easy";
        Assert.IsTrue(Has(c, "omoteura"));
        p.diff = "M";
        Assert.IsFalse(Has(c, "omoteura"));
        p.diff = "unknown";
        Assert.IsFalse(Has(c, "omoteura"));
        p.diff = "Normal"; p.song = "RushE";
        Assert.IsFalse(Has(c, "omoteura"));
        p.song = c.SongId; p.at--;
        Assert.IsFalse(Has(c, "omoteura"));
    }

    [Test]
    public void TachiaiUsesTheNeighborsKiribanId()
    {
        var c = Ctx();
        var p = Play(c);
        p.ach.Add("kiriban");
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "tachiai"));
        p.ach.Clear();
        Assert.IsFalse(Has(c, "tachiai"));
        p.ach = null;
        Assert.IsFalse(Has(c, "tachiai"));
    }

    [Test]
    public void NeckneckIncludesBothHundredPointBoundaries()
    {
        var c = Ctx();
        var p = Play(c, song: "RushE", score: c.Score - 100);
        c.Plays = new[] { p };
        Assert.IsTrue(Has(c, "neckneck"));
        p.score--;
        Assert.IsFalse(Has(c, "neckneck"));
        p.score = c.Score + 100;
        Assert.IsTrue(Has(c, "neckneck"));
        p.score++;
        Assert.IsFalse(Has(c, "neckneck"));
    }

    [Test]
    public void PairfcNeedsOwnNotesAndTheOtherRunsFc()
    {
        var c = Ctx();
        c.Stats = Stats(c);
        var p = Play(c, song: "RushE");
        p.ach.Add("fc");
        c.Plays = new[] { p };
        NoteGate(c, "pairfc");
        c.Bad = 1;
        Assert.IsFalse(Has(c, "pairfc"));
        c.Bad = 0; c.Miss = 1;
        Assert.IsFalse(Has(c, "pairfc"));
        c.Miss = 0; p.ach.Remove("fc");
        Assert.IsFalse(Has(c, "pairfc"));
    }

    [TestCase("daynight")]
    [TestCase("songswap")]
    [TestCase("sync")]
    [TestCase("omoteura")]
    [TestCase("tachiai")]
    [TestCase("neckneck")]
    [TestCase("pairfc")]
    [TestCase("shushifu")]
    public void NeighborAchievementsRejectMissingOrDifferentStationHistory(string id)
    {
        var c = Ctx();
        c.Stats = Stats(c);
        var p = Play(c, cuts: 2);
        p.lc = c.Stats.LastCutUnixMsBatch5b(Noon);
        p.ach.AddRange(new[] { "fc", "kiriban" });
        if (id == "daynight") p.song = "2_23_AM";
        if (id == "omoteura") p.diff = "M";
        var plays = new List<RankingPlay> { p };
        if (id == "songswap")
        {
            p.song = "RushE";
            plays.Add(Play(c, "run-old-a", Noon.AddMinutes(-10), "A", "RushE"));
            plays.Add(Play(c, "run-old-b", Noon.AddMinutes(-10), "B", "Morning"));
        }
        c.Plays = plays;
        Assert.IsTrue(Has(c, id));
        p.ns = OperatorMode.FestivalNamespace;
        Assert.IsFalse(Has(c, id), "別モード");
        p.ns = ""; p.st = "A";
        Assert.IsFalse(Has(c, id), "自分の台");
        p.st = "";
        Assert.IsFalse(Has(c, id), "台が不明");
        p.st = "B"; p.id = c.RunId;
        Assert.IsFalse(Has(c, id), "同じ回");
        p.id = "run-neighbor"; c.Plays = null;
        Assert.IsFalse(Has(c, id), "履歴が無い");
    }

    [Test]
    public void HonjitsuUsesBothStationsAndIgnoresDifficulty()
    {
        var c = Ctx();
        var p = Play(c, ended: Noon.AddSeconds(-1), diff: "Hard");
        c.Plays = new[] { p, p }; c.ClearsToday = 1;
        Assert.IsFalse(Has(c, "honjitsu"));
        p.st = "A";
        Assert.IsFalse(Has(c, "honjitsu"));
        p.song = "RushE";
        Assert.IsTrue(Has(c, "honjitsu"));
        p.song = c.SongId; p.ns = "fes"; c.ClearsToday = 0;
        Assert.IsTrue(Has(c, "honjitsu"), "名前空間は別");
        p.ns = ""; p.date = Noon.AddDays(-1).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        Assert.IsTrue(Has(c, "honjitsu"), "日付は別");
        c.ClearsToday = 1;
        Assert.IsFalse(Has(c, "honjitsu"), "今日の履歴が1回欠けている");
        c.ClearsToday = -1;
        Assert.IsFalse(Has(c, "honjitsu"), "数が不明");
    }

    [Test]
    public void KinouKoeUsesThePreviousEventDayAndOnlyTheFirstExcessRun()
    {
        var c = Ctx("fes");
        var old = Play(c, "run-oldest", Noon.AddDays(-10));
        var previous = Play(c, "run-prevday", Noon.AddDays(-3));
        var today = Play(c, "run-today-a", Noon.AddMinutes(-3), "A");
        var plays = new List<RankingPlay> { old, previous, previous, today };
        c.Plays = plays; c.ClearsBeforeTodayBatch5b = 2; c.ClearsToday = 1;
        Assert.IsTrue(Has(c, "kinou_koe"), "前の開催日は3日前で、完走1回");
        plays.Add(Play(c, "run-today-b", Noon.AddMinutes(-1)));
        c.ClearsToday = 2;
        Assert.IsFalse(Has(c, "kinou_koe"), "超えたあとの回");
        plays.RemoveAt(plays.Count - 1); c.ClearsToday = 1;
        c.ClearsBeforeTodayBatch5b = 3;
        Assert.IsFalse(Has(c, "kinou_koe"), "過去の履歴が不足");
        c.ClearsBeforeTodayBatch5b = 2; c.Namespace = "";
        Assert.IsFalse(Has(c, "kinou_koe"));
        c = Ctx("fes");
        Assert.IsFalse(Has(c, "kinou_koe"), "初開催");
    }

    [Test]
    public void KinouImaUsesFinishTimeAndTheLastEventDay()
    {
        var c = Ctx("fes");
        var p = Play(c, ended: Noon.AddDays(-3).AddSeconds(60), diff: "normal");
        var plays = new List<RankingPlay> { p, p };
        c.Plays = plays; c.ClearsBeforeTodayBatch5b = 1;
        Assert.IsTrue(Has(c, "kinou_ima"));
        p.at++;
        Assert.IsFalse(Has(c, "kinou_ima"));
        p.at--; p.diff = "Hard";
        Assert.IsFalse(Has(c, "kinou_ima"));
        p.diff = "Normal"; p.song = "RushE";
        Assert.IsFalse(Has(c, "kinou_ima"));
        p.song = c.SongId;
        plays.Add(Play(c, "run-newday", Noon.AddDays(-1).AddHours(1), song: "RushE"));
        c.ClearsBeforeTodayBatch5b = 2;
        Assert.IsFalse(Has(c, "kinou_ima"), "もっと前の開催日に合っても取れない");
        c.Namespace = "";
        Assert.IsFalse(Has(c, "kinou_ima"));
    }

    [Test]
    public void HitoributaiRequiresTwentyOneEarlierClearsAndNoOverlappingInterval()
    {
        var c = Ctx();
        c.Stats = Stats(c);
        var plays = Enumerable.Range(0, 21).Select(i =>
            Play(c, "run-early-" + i, Noon.AddMinutes(-3).AddSeconds(-i), i % 2 == 0 ? "A" : "B")).ToList();
        c.Plays = plays; c.ClearsToday = 21;
        Assert.IsTrue(Has(c, "hitoributai"));
        var last = plays[20]; plays.RemoveAt(20); c.ClearsToday = 20;
        Assert.IsFalse(Has(c, "hitoributai"));
        plays.Add(last); c.ClearsToday = 21;
        var other = Play(c, "run-overlap", Noon.AddSeconds(10), dur: 150000);
        plays.Add(other); c.ClearsToday = 22;
        Assert.IsFalse(Has(c, "hitoributai"), "相手の演奏がこの回全体を含む");
        other.dur = 9000;
        Assert.IsTrue(Has(c, "hitoributai"), "相手はこの回が終わった後に開始");
        other.dur = 10000;
        Assert.IsFalse(Has(c, "hitoributai"), "境界の開始も重なりとする");
        other.dur = -1;
        Assert.IsFalse(Has(c, "hitoributai"), "相手の開始が不明");
        plays.Remove(other); c.ClearsToday = 21;
        c.Stats.StartedAt = null;
        Assert.IsFalse(Has(c, "hitoributai"), "自分の開始が不明");
    }

    [Test]
    public void ShushifuIncludesHalfASecondButRequiresBothTimestamps()
    {
        var c = Ctx();
        c.Stats = Stats(c);
        var p = Play(c, cuts: 2);
        p.lc = c.Stats.LastCutUnixMsBatch5b(Noon) - 500;
        c.Plays = new[] { p };
        NoteGate(c, "shushifu");
        p.lc--;
        Assert.IsFalse(Has(c, "shushifu"));
        p.lc = -1;
        Assert.IsFalse(Has(c, "shushifu"), "旧版の回");
        p.lc = c.Stats.LastCutUnixMsBatch5b(Noon);
        c.Stats.Notes[0].JudgedAt = null;
        Assert.IsFalse(Has(c, "shushifu"), "最後かもしれない時刻が不明");
    }

    [TestCase(49998, false)]
    [TestCase(49999, true)]
    [TestCase(50000, true)]
    [TestCase(50001, false)]
    [TestCase(99999, true)]
    public void FushimeUsesStrictCrossingsAcrossBothStationsAndAllEventDays(int before, bool expected)
    {
        var c = Ctx("fes");
        c.Stats = Stats(c);
        var p = Play(c, ended: Noon.AddDays(-3), cuts: before);
        c.Plays = new[] { p, p }; c.ClearsBeforeTodayBatch5b = 1;
        Assert.AreEqual(expected, Has(c, "fushime"));
    }

    [Test]
    public void FushimeRejectsUnknownCountsAndDoesNotUseRawCutCount()
    {
        var c = Ctx("fes");
        c.Stats = Stats(c);
        var p = Play(c, ended: Noon.AddDays(-1), cuts: 49999);
        c.Plays = new[] { p }; c.ClearsBeforeTodayBatch5b = 1;
        NoteGate(c, "fushime");
        p.cuts = -1;
        Assert.IsFalse(Has(c, "fushime"));
        p.cuts = 49999; c.Stats.Record(1, JudgmentTier.Miss);
        c.CutCount = 50000;
        Assert.IsFalse(Has(c, "fushime"), "MISS やロングの途中の切りを水増ししない");
        c.Stats.Record(1, JudgmentTier.Bad);
        Assert.IsTrue(Has(c, "fushime"), "BAD は切った数に入る");
        c.Namespace = "";
        Assert.IsFalse(Has(c, "fushime"));
    }

    [Test]
    public void SummaryUsesFinalNotesAndTheSameRun()
    {
        var c = Ctx();
        var chart = PlayStatsTests.Chart(PlayStatsTests.Note(1000), PlayStatsTests.Note(2000, count: 5), PlayStatsTests.Note(3000));
        var stats = PlayStats.FromChart(chart, 0, NoteSpawner.DefaultSecondsPerLongCut, null, c.RunId);
        stats.StartedAt = c.StartedLocal;
        stats.Record(0, JudgmentTier.Bad, 0, Noon.AddSeconds(-3), SaberHand.Left, false);
        stats.Record(1, JudgmentTier.Perfect, null, Noon.AddSeconds(-2), SaberHand.Right, false);
        stats.Record(2, JudgmentTier.Miss, null, null, SaberHand.Any, false);
        var summary = PlayStats.SummaryBatch5b(stats, c.RunId, false, Noon);
        Assert.AreEqual(2, summary.cuts);
        Assert.AreEqual(new DateTimeOffset(Noon.AddSeconds(-2)).ToUnixTimeMilliseconds(), summary.lc);
        Assert.AreEqual(120000, summary.dur);
        Assert.AreEqual((-1, -1L, -1), PlayStats.SummaryBatch5b(stats, "another-run", false, Noon));
        Assert.AreEqual((0, -1L, 120000), PlayStats.SummaryBatch5b(stats, c.RunId, true, Noon));
        stats.Complete = false;
        Assert.AreEqual((-1, -1L, -1), PlayStats.SummaryBatch5b(stats, c.RunId, false, Noon));
        stats.Complete = true; stats.Notes[2].Result = null;
        Assert.AreEqual(-1, stats.CutNotesBatch5b);
    }

    [Test]
    public void TrackerCopiesPastCountsForTheFinishDay()
    {
        string oldRun = GameSession.AchievementRunId, oldNs = GameSession.RunNamespace;
        var oldPlays = AchievementTracker.PlaysSource;
        var oldSongs = AchievementTracker.SongsSource;
        var oldClears = AchievementTracker.ClearsSince;
        try
        {
            GameSession.AchievementRunId = Guid.NewGuid().ToString("N");
            GameSession.RunNamespace = "fes";
            AchievementTracker.PlaysSource = () => Array.Empty<RankingPlay>();
            AchievementTracker.SongsSource = () => Array.Empty<string>();
            long midnight = new DateTimeOffset(Noon.Date).ToUnixTimeMilliseconds();
            AchievementTracker.ClearsSince = (ns, since) => ns == "fes" ? (since == 0 ? 5 : since == midnight ? 2 : -1) : -1;
            var stats = new PlayStats { StartedAt = Noon.AddMinutes(-2), EndedAt = Noon };
            var c = AchievementTracker.BuildContext(stats, Noon.AddDays(1));
            Assert.AreEqual(Noon, c.EndedLocal);
            Assert.AreEqual(2, c.ClearsToday);
            Assert.AreEqual(3, c.ClearsBeforeTodayBatch5b);
            AchievementTracker.ClearsSince = (ns, since) => -1;
            Assert.AreEqual(-1, AchievementTracker.BuildContext(stats, Noon).ClearsBeforeTodayBatch5b);
        }
        finally
        {
            GameSession.AchievementRunId = oldRun; GameSession.RunNamespace = oldNs;
            AchievementTracker.PlaysSource = oldPlays; AchievementTracker.SongsSource = oldSongs;
            AchievementTracker.ClearsSince = oldClears;
        }
    }

    [Test]
    public void OptionalFieldsSurviveCopyStorageAndWireAndOldRunsRemainValid()
    {
        var c = Ctx("fes");
        var p = Play(c, cuts: 123, dur: 120000);
        p.lc = p.at - 1000;
        var log = new RankingSyncLog();
        Assert.IsTrue(log.Add(p));
        var copy = log.CopyPlays()[0];
        Assert.AreEqual(p.cuts, copy.cuts); Assert.AreEqual(p.lc, copy.lc); Assert.AreEqual(p.dur, copy.dur);
        var saved = RankingSyncLog.FromJson(log.ToJson(), out bool broken);
        Assert.IsFalse(broken);
        Assert.IsTrue(RankingSyncLog.SameContent(p, saved.Find(p.id)));
        var pending = RankingSyncLog.FromJson(RankingSyncLog.PlaysJson(new[] { p }), out broken);
        Assert.IsFalse(broken);
        Assert.AreEqual(p.lc, pending.Find(p.id).lc);
        var packet = log.Bundles("machine-test", "A").Single();
        Assert.IsTrue(RankingSyncLog.TryParse(packet.Text, out var message));
        Assert.IsTrue(RankingSyncLog.SameContent(p, message.plays.Single()));

        var old = Play(c, "run-oldwire");
        var oldLog = new RankingSyncLog();
        Assert.IsTrue(oldLog.Add(old));
        string json = oldLog.ToJson();
        Assert.IsFalse(json.Contains("\"cuts\"") || json.Contains("\"lc\"") || json.Contains("\"dur\""));
        var read = RankingSyncLog.FromJson(json, out broken).Find(old.id);
        Assert.IsFalse(broken);
        Assert.AreEqual(-1, read.cuts); Assert.AreEqual(-1, read.lc); Assert.AreEqual(-1, read.dur);
        Assert.IsTrue(RankingSyncLog.IsValid(read));
        var stripped = RankingSyncLog.Copy(p);
        stripped.cuts = stripped.dur = -1; stripped.lc = -1;
        Assert.IsTrue(RankingSyncLog.SameContent(p, stripped), "旧版を経由して任意欄が消えても内容衝突にしない");
        var changed = RankingSyncLog.Copy(p);
        changed.cuts++;
        Assert.IsFalse(RankingSyncLog.SameContent(p, changed));
        changed = RankingSyncLog.Copy(p); changed.lc--;
        Assert.IsFalse(RankingSyncLog.SameContent(p, changed));
        changed = RankingSyncLog.Copy(p); changed.dur++;
        Assert.IsFalse(RankingSyncLog.SameContent(p, changed));
    }

    [Test]
    public void BadOptionalFieldsAreDroppedButThePlayIsKept()
    {
        var c = Ctx();
        var p = Play(c, cuts: 1, dur: 120000);
        p.lc = p.at - 1000;
        var changes = new Action<RankingPlay>[]
        {
            x => x.cuts = -2, x => x.dur = 0, x => x.dur = -2,
            x => x.dur = AchievementCatalog.Batch5bMaxDurationMs + 1,
            x => x.lc = 0, x => x.lc = -2, x => x.lc = x.at + 1,
            x => x.lc = x.at - x.dur - 1, x => x.cuts = 0,
        };
        Assert.IsTrue(RankingSyncLog.IsValid(p));
        // おかしい任意の欄は捨てて(-1)、回そのものは順位のために残す(2026-10-30 9a)。
        foreach (var change in changes)
        {
            var bad = RankingSyncLog.Copy(p);
            change(bad);
            var copy = RankingSyncLog.Copy(bad);
            Assert.IsTrue(RankingSyncLog.IsValid(copy));
            Assert.IsTrue(copy.cuts >= -1);
            Assert.IsTrue(copy.dur == -1 || (copy.dur > 0 && copy.dur <= AchievementCatalog.Batch5bMaxDurationMs));
            Assert.IsTrue(copy.lc == -1 || (copy.cuts > 0 && copy.lc > 0 && copy.lc <= copy.at && (copy.dur == -1 || copy.lc >= copy.at - copy.dur)));
            Assert.IsTrue(new RankingSyncLog().Add(bad), "回は残る");
        }
    }

    [Test]
    public void LongestSixtyIdsStillFitWithAllOptionalFields()
    {
        var ids = AchievementCatalog.All.Select(d => d.Id).OrderByDescending(id => id.Length)
            .ThenBy(id => id, StringComparer.Ordinal).Take(RankingSyncLog.RealisticAchievementsPerPlay).ToList();
        Assert.AreEqual(60, ids.Count);
        Assert.AreEqual(80, RankingSyncLog.MaxAchievementsPerPlay);
        Assert.AreEqual("BTSRANK3 ", RankingSyncLog.Prefix);
        foreach (string directory in System.IO.Directory.GetDirectories(System.IO.Path.Combine(Application.streamingAssetsPath, "Songs")))
        {
            var p = RankingSyncLog.NewPlay(Guid.NewGuid().ToString("N"), System.IO.Path.GetFileName(directory),
                "Normal", 100000, "S+", 1f / 3, Noon, new string('B', 16), "fes", ids,
                int.MaxValue, new DateTimeOffset(Noon).ToUnixTimeMilliseconds() - 1, AchievementCatalog.Batch5bMaxDurationMs);
            CollectionAssert.AreEqual(ids, p.ach);
            Assert.IsTrue(RankingSyncLog.IsValid(p));
            var log = new RankingSyncLog();
            Assert.IsTrue(log.Add(p));
            var packets = log.Bundles(new string('f', 64), new string('A', 16));
            Assert.AreEqual(1, packets.Count);
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(packets[0].Text), 1200);
            Assert.IsTrue(RankingSyncLog.TryParse(packets[0].Text, out var parsed));
            CollectionAssert.AreEqual(ids, parsed.plays.Single().ach);
            Assert.AreEqual(p.cuts, parsed.plays.Single().cuts);
            Assert.AreEqual(p.lc, parsed.plays.Single().lc);
            Assert.AreEqual(p.dur, parsed.plays.Single().dur);
        }
    }
}
