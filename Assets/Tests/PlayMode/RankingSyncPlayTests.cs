using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 台同士のランキング同期(RankingSync、2026-10-28)を、本物の UDP で確かめる。
// 2つの RankingSync を 127.0.0.1 の空いているポートで立て、手で入れた相手どうしにする(ブロードキャストはしない: 本物の台へ流さない)。
// 2つは同じ PlayerPrefs を使うので、記録した側は表に書かない(Record はログと送信だけ)。表に入ったら、相手が受け取って足したと分かる。
// 2台で表が同じ並びになることは、同じ表を分けられないので EditMode の RankingSyncTests(届いた順に依らない並び)で確かめる。
// 版 2(2026-10-29): 運営モードの回(ns = fes)が相手の本番の表と実績の数に入ること、古い版のメッセージを合流しないこと。
public class RankingSyncPlayTests
{
    const string Song = "__RankingSyncTest";
    const string Fes = OperatorMode.FestivalNamespace;
    // 保存先はすべて専用のキー(本番の RankingSync.Log.v1・RankingSync.Pending.v1・機械 ID・実績の記録には触れない)。
    const string KeyA = "RankingSync.Log.v1.__RankingSyncPlayTestA";
    const string KeyB = "RankingSync.Log.v1.__RankingSyncPlayTestB";
    const string PendingKeyA = "RankingSync.Pending.v1.__RankingSyncPlayTestA";
    const string PendingKeyB = "RankingSync.Pending.v1.__RankingSyncPlayTestB";
    const string AchievementKey = "Achievements.Log.v1.__RankingSyncPlayTest";
    readonly List<RankingSync> created = new List<RankingSync>();
    static readonly string[] ProductionKeys =
    {
        RankingSyncLog.PreferenceKey, RankingSync.PendingPreferenceKey, RankingSync.MachinePreferenceKey, RankingSync.PeersPreferenceKey,
        AchievementStore.PreferenceKey,
    };
    readonly Dictionary<string, string> production = new Dictionary<string, string>();
    string savedAchievementKey;

    [SetUp]
    public void Setup()
    {
        ClearPrefs();
        production.Clear();
        foreach (string key in ProductionKeys) production[key] = PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;
        savedAchievementKey = AchievementStore.Key;
        AchievementStore.UseKeyForTest(AchievementKey);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var sync in created)
        {
            if (sync == null) continue;
            sync.Shutdown();
            UnityEngine.Object.Destroy(sync.gameObject);
        }
        created.Clear();
        ClearPrefs();
        AchievementStore.UseKeyForTest(savedAchievementKey);
        foreach (var pair in production)
            Assert.AreEqual(pair.Value, PlayerPrefs.HasKey(pair.Key) ? PlayerPrefs.GetString(pair.Key) : null, "本番のキーに触れない: " + pair.Key);
    }

    static void ClearPrefs()
    {
        foreach (string key in new[] { KeyA, KeyB, PendingKeyA, PendingKeyB, AchievementKey })
            PlayerPrefs.DeleteKey(key);
        foreach (string ns in new[] { "", Fes })
        {
            PlayerPrefs.DeleteKey(DailyRankingStore.Key(ns, Song, "Normal"));
            PlayerPrefs.DeleteKey(HighScoreStore.Key(ns, Song, "Normal"));
        }
        PlayerPrefs.Save();
    }

    RankingSync Machine(string station, string logKey, string peers = "", int port = 0)
    {
        var sync = RankingSync.Create(new RankingSync.Settings
        {
            Port = port, Broadcast = false, LoopbackOnly = true, Peers = peers,
            MachineId = "test-" + station + "-" + Guid.NewGuid().ToString("N"), Station = station, LogKey = logKey,
            PendingKey = logKey == KeyA ? PendingKeyA : PendingKeyB, ShowNotices = false,
        });
        // 前のテストが Game シーンを開いたまま終わると、演奏中と見なされてログの保存が後回しになる(2026-10-30 の最後のテストで3件落ちた)。
        // このテストは保存まで確かめるので、演奏中ではない扱いに固定する。
        sync.BusyForTest = false;
        created.Add(sync);
        return sync;
    }

    RankingSync StartMachine(string station, string logKey, string peers = "")
    {
        var sync = Machine(station, logKey, peers);
        Assert.IsTrue(sync.NetworkRunning, "127.0.0.1 の空いているポートで受けられる");
        Assert.Greater(sync.LocalPort, 0);
        return sync;
    }

    static string Address(RankingSync sync) => "127.0.0.1:" + sync.LocalPort;

    static RankingPlay Play(int score, string station) => RankingSyncLog.NewPlay(Guid.NewGuid().ToString("N"), Song, "Normal", score,
        PlayRankHelper.Label(PlayRankHelper.FromScore(score)), .9f, DateTime.Now, station);

    static bool InBothTables(string id, string ns = "")
    {
        bool daily = DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(ns, Song, "Normal"), ""), "2000-01-01", out var table) &&
                     table.entries.Exists(e => e.runId == id);
        return daily && HighScoreStore.Load(ns, Song, "Normal").entries.Exists(e => e.runId == id);
    }

    [UnityTest]
    public IEnumerator APlayRecordedOnOneMachineReachesTheOtherWithinOneSecond()
    {
        var a = StartMachine("A", KeyA);
        var b = StartMachine("B", KeyB, Address(a));
        a.AddPeer(Address(b));
        // 双方向と分かる(相手の要約に、こちらの機械 ID が入る)まで待つ。
        float waitFrom = Time.realtimeSinceStartup;
        while ((a.ConnectedCount == 0 || b.ConnectedCount == 0) && Time.realtimeSinceStartup - waitFrom < 3f) yield return null;
        Assert.AreEqual(1, a.ConnectedCount);
        Assert.AreEqual(1, b.ConnectedCount);
        Assert.AreEqual(RankingSync.ConnectedNotice("B"), a.LastNotice);
        Assert.AreEqual(RankingSync.ConnectedNotice("A"), b.LastNotice);

        var play = Play(87654, "A");
        float startedAt = Time.realtimeSinceStartup;
        Assert.IsTrue(a.Record(play));
        Assert.IsFalse(InBothTables(play.id), "記録した台は表に書かない(表は GameSession が残す)");
        float arrivedAfter = -1;
        while (Time.realtimeSinceStartup - startedAt < 2f)
        {
            if (InBothTables(play.id)) { arrivedAfter = Time.realtimeSinceStartup - startedAt; break; }
            yield return null;
        }
        Assert.GreaterOrEqual(arrivedAfter, 0f, "相手の台の通算の表と上位5件に入る");
        Assert.Less(arrivedAfter, 1f, "1秒以内に入る");
        Assert.AreEqual(1, b.Count);
        Assert.AreEqual(1, b.ReceivedPlays);
        Assert.AreEqual(a.Hash, b.Hash, "両方の台が同じ記録になる");
        Assert.Greater(a.SentPlays, 0);
        CollectionAssert.Contains(b.PeerStations, "A");
        // 0.3・1.0 秒の送り直しが届いても、二重に足さない
        yield return new WaitForSecondsRealtime(1.3f);
        Assert.AreEqual(1, b.Count);
        Assert.AreEqual(1, b.ReceivedPlays);
        Assert.AreEqual(1, HighScoreStore.Load("", Song, "Normal").entries.Count);
        Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key("", Song, "Normal")), "2000-01-01", out var table));
        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(1, RankingSyncLog.Load(KeyB).Count, "受け取った側のログにも残る");
    }

    [UnityTest]
    public IEnumerator PlaysRecordedBeforeConnectingArriveThroughTheDigest()
    {
        var a = StartMachine("A", KeyA);
        var mine = new[] { Play(91000, "A"), Play(72000, "A"), Play(45000, "A") };
        foreach (var play in mine) Assert.IsTrue(a.Record(play));
        var b = StartMachine("B", KeyB);
        var theirs = Play(66000, "B");
        Assert.IsTrue(b.Record(theirs));
        // 0.3・1.0 秒の送り直しが終わってからつなぐ(送り直しではなく、要約の違いから合うことを確かめる)。
        yield return new WaitForSecondsRealtime(1.3f);
        Assert.AreEqual(3, a.Count);
        Assert.AreEqual(1, b.Count, "まだつながっていない");
        Assert.AreEqual(0, a.PeerCount);
        Assert.AreEqual(0, a.BundlesStarted + b.BundlesStarted);
        // つながる(B が A を手で入れる)。要約の違いから、両方の台へ足りない分が届く。
        b.AddPeer(Address(a));
        float startedAt = Time.realtimeSinceStartup;
        while ((a.Count < 4 || b.Count < 4) && Time.realtimeSinceStartup - startedAt < 8f) yield return null;
        Assert.AreEqual(4, a.Count);
        Assert.AreEqual(4, b.Count);
        Assert.AreEqual(a.Hash, b.Hash, "両方の台が同じ記録になる");
        Assert.GreaterOrEqual(a.BundlesStarted, 1, "A は B の要約が違うので全部を送った");
        Assert.GreaterOrEqual(b.BundlesStarted, 1, "B も A の要約が違うので全部を送った");
        Assert.AreEqual(3, b.ReceivedPlays);
        Assert.AreEqual(1, a.ReceivedPlays);
        foreach (var play in mine.Concat(new[] { theirs })) Assert.IsTrue(InBothTables(play.id), play.id);
        CollectionAssert.AreEqual(new[] { 91000, 72000, 66000, 45000 }, HighScoreStore.Load("", Song, "Normal").entries.Select(e => e.score));
        CollectionAssert.Contains(a.PeerStations, "B");
        CollectionAssert.Contains(b.PeerStations, "A");
    }

    // ポートを開けない(ほかが使っている)ときは、運営に1回知らせ、記録はログに残して、ゲームはそのまま動く。
    [UnityTest]
    public IEnumerator APortInUseIsReportedOnceAndPlaysAreStillKept()
    {
        var a = StartMachine("A", KeyA);
        var b = Machine("B", KeyB, "", a.LocalPort);
        Assert.IsFalse(b.NetworkRunning);
        Assert.AreEqual(RankingSync.BindFailedNotice(a.LocalPort), b.LastNotice);
        Assert.IsTrue(b.Record(Play(50000, "B")), "同期できなくても、ログには残す(あとで合わせる)");
        for (int i = 0; i < 5; i++) yield return null;
        Assert.AreEqual(1, b.NoticeCount, "知らせは1回(見つからないの知らせも重ねない)");
        Assert.AreEqual(1, RankingSyncLog.Load(KeyB).Count);
    }

    // 運営モードの回(ns = fes)は、相手の台の本番の表に入り(ふだんの表には入らない)、その回の実績(ach)が相手の台の数に足される。
    [UnityTest]
    public IEnumerator AFestivalPlayReachesTheOtherMachinesFestivalTablesWithItsAchievements()
    {
        var a = StartMachine("A", KeyA);
        var b = StartMachine("B", KeyB, Address(a));
        a.AddPeer(Address(b));
        var play = RankingSyncLog.NewPlay(Guid.NewGuid().ToString("N"), Song, "Normal", 93000, "S+", .95f, DateTime.Now, "A", Fes,
            new[] { "test_clear", "test_gold" });
        Assert.IsTrue(a.Record(play));
        float startedAt = Time.realtimeSinceStartup;
        while (!InBothTables(play.id, Fes) && Time.realtimeSinceStartup - startedAt < 2f) yield return null;
        Assert.IsTrue(InBothTables(play.id, Fes), "相手の台の本番の表に入る");
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key("", Song, "Normal")), "ふだんの表には入れない");
        Assert.IsFalse(PlayerPrefs.HasKey(HighScoreStore.Key("", Song, "Normal")));
        Assert.AreEqual(1, AchievementStore.Count(Fes, "test_clear"), "その回の実績を相手の台の数に足す");
        Assert.AreEqual(1, AchievementStore.Count(Fes, "test_gold"));
        Assert.AreEqual(0, AchievementStore.Count("", "test_clear"), "ふだんの数とは別");
        // 送り直しが届いても二重に数えず、ログには ns・ach のまま残る
        yield return new WaitForSecondsRealtime(1.3f);
        Assert.AreEqual(1, AchievementStore.Count(Fes, "test_clear"));
        Assert.AreEqual(1, b.ReceivedPlays);
        var logged = RankingSyncLog.Load(KeyB).Find(play.id);
        Assert.IsNotNull(logged);
        Assert.AreEqual(Fes, logged.ns);
        CollectionAssert.AreEqual(new[] { "test_clear", "test_gold" }, logged.ach);
        Assert.IsFalse(AchievementStore.NeedsWrite, "実績の記録も書き終えた");
    }

    // 古い版(BTSRANK1)の台からのメッセージは合流せず、運営に1回だけ「相手の台の版が古いので同期しません」と知らせる。
    [UnityTest]
    public IEnumerator AMessageFromAnOlderVersionIsReportedOnceAndNotMerged()
    {
        var a = StartMachine("A", KeyA);
        var play = RankingSyncLog.NewPlay(Guid.NewGuid().ToString("N"), Song, "Normal", 50000, "B", .5f, DateTime.Now, "B");
        var log = new RankingSyncLog();
        Assert.IsTrue(log.Add(play));
        string current = log.Bundles("test-old-" + Guid.NewGuid().ToString("N"), "B")[0].Text;
        byte[] old = Encoding.UTF8.GetBytes("BTSRANK1 " + current.Substring(RankingSyncLog.Prefix.Length).Replace("\"v\":3", "\"v\":1"));
        using (var sender = new UdpClient(AddressFamily.InterNetwork))
        {
            for (int i = 0; i < 3; i++)
            {
                sender.Send(old, old.Length, new IPEndPoint(IPAddress.Loopback, a.LocalPort));
                yield return new WaitForSecondsRealtime(.1f);
            }
        }
        float startedAt = Time.realtimeSinceStartup;
        while (a.LastNotice != RankingSync.OldPeerNotice && Time.realtimeSinceStartup - startedAt < 2f) yield return null;
        Assert.AreEqual(RankingSync.OldPeerNotice, a.LastNotice);
        for (int i = 0; i < 5; i++) yield return null;
        Assert.AreEqual(1, a.NoticeCount, "3通届いても知らせは1回");
        Assert.AreEqual(0, a.Count, "合流しない");
        Assert.AreEqual(0, a.PeerCount, "相手として数えない");
        Assert.IsFalse(InBothTables(play.id));
    }
}
