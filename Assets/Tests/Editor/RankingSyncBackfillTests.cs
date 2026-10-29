using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NUnit.Framework;
using UnityEngine;

// 同期していない間の回も合わせる(RankingSyncBackfill、2026-10-29 ユーザー「完全に同期しちゃって欲しい」)。
// 通算の表にあって同期の記録に無い回を、動き出すときにこの台の回として記録に足し、相手の台の表まで届くこと。
// ネットワークは開かない。PlayerPrefs は専用の曲 ID と専用のキーだけを使い、テストの後で消す(本番のキーに触れないことも確かめる)。
public class RankingSyncBackfillTests
{
    const string Song = "__RankingSyncBackfillTest";
    const string LogKey = "RankingSync.Log.v1.__RankingSyncBackfillTest";
    const string PendingKey = "RankingSync.Pending.v1.__RankingSyncBackfillTest";
    const string OtherLogKey = "RankingSync.Log.v1.__RankingSyncBackfillTestB";
    const string OtherPendingKey = "RankingSync.Pending.v1.__RankingSyncBackfillTestB";
    const string AchievementKey = "Achievements.Log.v1.__RankingSyncBackfillTest";
    const string Fes = OperatorMode.FestivalNamespace;
    static readonly string[] Difficulties = { "Hard", "T" };
    static readonly IPEndPoint From = new IPEndPoint(IPAddress.Parse("192.168.137.20"), RankingSync.DefaultPort);
    static readonly string[] ProductionKeys =
    {
        RankingSyncLog.PreferenceKey, RankingSync.PendingPreferenceKey, RankingSync.MachinePreferenceKey, RankingSync.PeersPreferenceKey,
        AchievementStore.PreferenceKey,
    };
    readonly List<RankingSync> created = new List<RankingSync>();
    readonly Dictionary<string, string> production = new Dictionary<string, string>();
    string savedAchievementKey;

    [SetUp]
    public void RememberProductionKeys()
    {
        production.Clear();
        foreach (string key in ProductionKeys) production[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        savedAchievementKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(AchievementKey);
        AchievementStore.UseKeyForTest(AchievementKey);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var sync in created)
        {
            if (sync == null) continue;
            sync.Shutdown();
            UnityEngine.Object.DestroyImmediate(sync.gameObject);
        }
        created.Clear();
        foreach (string ns in new[] { "", Fes })
            foreach (string difficulty in Difficulties)
            {
                PlayerPrefs.DeleteKey(DailyRankingStore.Key(ns, Song, difficulty));
                PlayerPrefs.DeleteKey(HighScoreStore.Key(ns, Song, difficulty));
            }
        foreach (string key in new[] { LogKey, PendingKey, OtherLogKey, OtherPendingKey, AchievementKey }) PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedAchievementKey);
        foreach (var pair in production)
            Assert.AreEqual(pair.Value, PlayerPrefs.HasKey(pair.Key) ? PlayerPrefs.GetString(pair.Key) : null, "本番のキーに触れない: " + pair.Key);
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    // この台の 0 時から数えた時刻(UTC の Unix ミリ秒)。
    static long LocalMs(int month, int day, int hour = 0, int minute = 0) =>
        new DateTimeOffset(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    static void WriteDaily(string ns, string difficulty, string day, params (string id, int score)[] entries)
    {
        var table = new DailyRankingStore.Table { day = day };
        foreach (var (id, score) in entries) table.entries.Add(new DailyRankingStore.Entry { runId = id, score = score });
        PlayerPrefs.SetString(DailyRankingStore.Key(ns, Song, difficulty), JsonUtility.ToJson(table));
    }

    static void WriteTop(string ns, string difficulty, params HighScoreEntry[] entries) =>
        PlayerPrefs.SetString(HighScoreStore.Key(ns, Song, difficulty), JsonUtility.ToJson(new HighScoreTable { entries = entries.ToList() }));

    static DailyRankingStore.Table DailyTable(string ns, string difficulty)
    {
        Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(ns, Song, difficulty), ""), "2000-01-01", out var table));
        return table;
    }

    RankingSync Station(string station, string logKey, string pendingKey, bool backfill, params RankingSyncBackfill.Chart[] charts)
    {
        var sync = RankingSync.Create(new RankingSync.Settings
        {
            Offline = true, Broadcast = false, MachineId = NewId(), Station = station, LogKey = logKey, PendingKey = pendingKey, ShowNotices = false,
            Backfill = backfill, BackfillCharts = charts,
        });
        created.Add(sync);
        return sync;
    }

    // 台A の記録の全部を、台A から届いた束にする(1通に収まる数で使う)。
    static string Bundle(RankingSync sync)
    {
        var log = new RankingSyncLog();
        foreach (var play in sync.LoggedPlays) log.Add(play);
        var datagrams = log.Bundles(sync.MachineId, sync.Station);
        Assert.AreEqual(1, datagrams.Count);
        return datagrams[0].Text;
    }

    // 同期が入る前に遊んだ回(記録に無い)を、表の順にこの台の回として記録の後ろへ足す。上位5件に同じ回があればランク・精度・日付・時刻は
    // そこから(ID の無い古い記録は点で引く)。無ければランクは点から・精度は点の割合・時刻は1つ前の回の 1 ミリ秒あと。表はそのまま。
    [Test]
    public void BackfillAddsThisStationsTablePlaysThatTheLogMisses()
    {
        string old = NewId(), older = NewId(), synced = NewId(), late = NewId();
        var syncedPlay = RankingSyncLog.NewPlay(synced, Song, "Hard", 60000, "A", .7f, new DateTime(2026, 10, 28, 15, 0, 0), "A");
        var saved = new RankingSyncLog();
        Assert.IsTrue(saved.Add(syncedPlay));
        saved.Save(LogKey);
        WriteDaily("", "Hard", "2026-10-20", (old, 73000), (older, 55959), (synced, 60000), (late, 82000));
        WriteTop("", "Hard",
            new HighScoreEntry { score = 82000, rank = "S", accuracy = .87f, date = "2026/10/29", runId = late, at = LocalMs(10, 29, 11, 30) },
            new HighScoreEntry { score = 73000, rank = "A", accuracy = .79f, date = "2026/10/21" },
            new HighScoreEntry { score = 60000, rank = "A", accuracy = .7f, date = "2026/10/28", runId = synced, at = syncedPlay.at });

        var sync = Station("A", LogKey, PendingKey, true, new RankingSyncBackfill.Chart("", Song, "Hard"));
        Assert.AreEqual(3, sync.BackfilledPlays);
        CollectionAssert.AreEqual(new[] { synced, old, older, late }, sync.LoggedPlays.Select(p => p.id).ToArray(), "記録の後ろに表の順で足す");
        var plays = sync.LoggedPlays.ToDictionary(p => p.id);
        foreach (string id in new[] { old, older, late })
        {
            var play = plays[id];
            Assert.IsTrue(play.st == "A" && play.song == Song && play.diff == "Hard" && play.ns == "" && play.ach.Count == 0, id);
        }
        var first = plays[old];
        Assert.AreEqual(73000, first.score);
        Assert.AreEqual("A", first.rank, "ID の無い古い上位5件の記録を点で引く");
        Assert.AreEqual(.79f, first.acc, 1e-5f);
        Assert.AreEqual("2026/10/21", first.date);
        Assert.AreEqual(LocalMs(10, 21), first.at, "表を数え始めた日より、上位5件の日付の 0 時が後ならそちら");
        var second = plays[older];
        Assert.AreEqual(PlayRankHelper.Label(PlayRankHelper.FromScore(55959)), second.rank, "上位5件に無ければランクは点から");
        Assert.AreEqual(.55959f, second.acc, 1e-5f, "精度は点の割合");
        Assert.AreEqual(first.at + 1, second.at, "1つ前の回の 1 ミリ秒あと");
        Assert.AreEqual("2026/10/21", second.date);
        var last = plays[late];
        Assert.IsTrue(last.rank == "S" && Math.Abs(last.acc - .87f) < 1e-5f && last.date == "2026/10/29" && last.at == LocalMs(10, 29, 11, 30),
            "ID が同じ上位5件の記録があれば、その中身と時刻");
        Assert.AreEqual(4, DailyTable("", "Hard").entries.Count, "表へは足さない");

        sync.Tick(Time.realtimeSinceStartupAsDouble + 1);
        Assert.AreEqual(4, RankingSyncLog.Load(LogKey).Count, "足した回も記録に保存する");
        var again = Station("A", LogKey, PendingKey, true, new RankingSyncBackfill.Chart("", Song, "Hard"));
        Assert.AreEqual(0, again.BackfilledPlays, "次の起動では、もう記録にあるので足さない");
        Assert.AreEqual(4, again.Count);
    }

    // 足した回は相手の台の通算の表と上位5件に入る(運営モードの表も、裏譜面も)。相手の台は今のままで受け取れる(記録の形は版 3 のまま)。
    // 1つの PlayerPrefs で2台を試すので、台A が記録を作ったあとに表を消して台B の表にする。
    [Test]
    public void BackfilledPlaysReachTheOtherStationsTables()
    {
        string low = NewId(), high = NewId();
        WriteDaily(Fes, "T", "2026-10-25", (low, 64000), (high, 91000));
        var stationA = Station("A", LogKey, PendingKey, true, new RankingSyncBackfill.Chart(Fes, Song, "T"));
        Assert.AreEqual(2, stationA.BackfilledPlays);
        Assert.IsTrue(stationA.LoggedPlays.All(p => p.ns == Fes && p.diff == "T" && p.st == "A"));
        string message = Bundle(stationA);
        PlayerPrefs.DeleteKey(DailyRankingStore.Key(Fes, Song, "T"));
        PlayerPrefs.DeleteKey(HighScoreStore.Key(Fes, Song, "T"));

        var stationB = Station("B", OtherLogKey, OtherPendingKey, false);
        Assert.AreEqual(2, stationB.ReceiveForTest(message, From, Time.realtimeSinceStartupAsDouble));
        CollectionAssert.AreEquivalent(new[] { low, high }, DailyTable(Fes, "T").entries.Select(e => e.runId).ToArray(), "台B の通算の表に入る");
        var top = HighScoreStore.Load(Fes, Song, "T").entries;
        Assert.AreEqual(2, top.Count);
        Assert.IsTrue(top[0].runId == high && top[0].score == 91000 && top[0].rank == "S+", "上位5件にも入る");
        Assert.IsTrue(stationB.LoggedPlays.All(p => p.st == "A"), "台B の記録でも台A の回");
        Assert.AreEqual(0, DailyTable("", "T").entries.Count, "ふだんの表には入らない");
    }

    // 読めない表は飛ばして上書きしない。表へ足す待ちにある相手の回・形の違う ID は、この台の回として作らない。
    [Test]
    public void BackfillSkipsBrokenTablesWaitingPlaysAndBadIds()
    {
        PlayerPrefs.SetString(DailyRankingStore.Key("", Song, "Hard"), "{broken");
        string waiting = NewId();
        var otherPlay = RankingSyncLog.NewPlay(waiting, Song, "T", 50000, "B", .5f, new DateTime(2026, 10, 26, 10, 0, 0), "B");
        PlayerPrefs.SetString(PendingKey, RankingSyncLog.PlaysJson(new[] { otherPlay }));
        WriteDaily("", "T", "2026-10-26", (waiting, 50000), ("short", 40000));

        var sync = Station("A", LogKey, PendingKey, true,
            new RankingSyncBackfill.Chart("", Song, "Hard"), new RankingSyncBackfill.Chart("", Song, "T"));
        Assert.AreEqual(0, sync.BackfilledPlays);
        Assert.AreEqual("{broken", PlayerPrefs.GetString(DailyRankingStore.Key("", Song, "Hard")), "読めない表は上書きしない");
        sync.Tick(Time.realtimeSinceStartupAsDouble + 1);
        var play = sync.LoggedPlays.Single();
        Assert.IsTrue(play.id == waiting && play.st == "B", "待ちにあった回は相手の台の回のまま記録に入る");
    }

    // その回に取った実績も入れる(相手の台の「○人目」の数にも入る)。
    [Test]
    public void BackfillCarriesTheRunsAchievements()
    {
        string run = NewId();
        WriteDaily("", "Hard", "2026-10-29", (run, 70000));
        Assert.Greater(AchievementStore.Unlock("", run, AchievementCatalog.ClearId, "A", LocalMs(10, 29, 9)), 0);
        var sync = Station("A", LogKey, PendingKey, true, new RankingSyncBackfill.Chart("", Song, "Hard"));
        CollectionAssert.AreEqual(new[] { AchievementCatalog.ClearId }, sync.LoggedPlays.Single().ach);
    }

    // 足すのは頼んだとき(本番のビルドの1つ)だけ。テストや Editor の同期は今までどおり、表の回を記録へ足さない。
    [Test]
    public void BackfillIsOffUnlessAsked()
    {
        WriteDaily("", "Hard", "2026-10-29", (NewId(), 70000));
        var sync = Station("A", LogKey, PendingKey, false, new RankingSyncBackfill.Chart("", Song, "Hard"));
        Assert.AreEqual(0, sync.BackfilledPlays);
        Assert.AreEqual(0, sync.Count);
    }

    // 本番で調べる表: 全曲 × Easy・Normal・Hard とその曲の裏譜面 × ふだん・運営モードの表。テストの仮の曲は入れない。
    [Test]
    public void StationChartsCoverEverySongWithItsSecretInBothTables()
    {
        var charts = RankingSyncBackfill.StationCharts();
        bool Has(string ns, string song, string difficulty) => charts.Any(c => c.Ns == ns && c.Song == song && c.Difficulty == difficulty);
        Assert.IsTrue(Has("", "Andalusia", "Hard") && Has("", "Andalusia", "T") && Has(Fes, "Andalusia", "Easy") && Has(Fes, "Andalusia", "T"));
        foreach (var secret in RushESecretState.Charts)
            if (System.IO.Directory.Exists(System.IO.Path.Combine(Application.streamingAssetsPath, "Songs", secret.SongId)))
                Assert.IsTrue(Has("", secret.SongId, secret.Difficulty) && Has(Fes, secret.SongId, secret.Difficulty), secret.SongId);
        Assert.IsFalse(charts.Any(c => c.Song.StartsWith("__", StringComparison.Ordinal)));
        Assert.AreEqual(charts.Count, charts.Select(c => c.Ns + "\n" + c.Song + "\n" + c.Difficulty).Distinct().Count(), "同じ表を二度調べない");
    }
}
