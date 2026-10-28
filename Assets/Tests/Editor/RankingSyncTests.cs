using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 台同士のランキング同期(RankingSync、2026-10-28)の純粋な部分(RankingSyncLog)と、表(HighScoreStore・DailyRankingStore)への併合。
// ネットワークは開かない(受け取ったあとの処理は ReceiveForTest、時間の進みは Tick に時刻を渡して確かめる)。本物の UDP は PlayMode の
// RankingSyncPlayTests。PlayerPrefs は専用の曲 ID と専用のログのキーだけを使い、テストの後で消す。
// 版 2(2026-10-29 運営モードと実績)の ns・ach・ほかの版の扱いは、このファイルの後ろの方。
public class RankingSyncTests
{
    const string Song = "__RankingSyncTest";
    const string JapaneseSong = "__RankingSyncTest揺籠";
    const string LogKey = "RankingSync.Log.v1.__RankingSyncTest";
    const string PendingKey = "RankingSync.Pending.v1.__RankingSyncTest";
    const string AchievementKey = "Achievements.Log.v1.__RankingSyncTest";
    const string Fes = OperatorMode.FestivalNamespace;
    static readonly string[] Difficulties = { "Normal", "Hard" };
    static readonly DateTime PlayedAt = new DateTime(2026, 10, 28, 13, 0, 0);
    static readonly IPEndPoint From = new IPEndPoint(IPAddress.Parse("192.168.137.20"), RankingSync.DefaultPort);
    readonly List<RankingSync> created = new List<RankingSync>();
    // 本番のキー(ログ・足し直し待ち・機械 ID・手で入れた相手・実績の記録)には触れないことを、どのテストでも確かめる。
    static readonly string[] ProductionKeys =
    {
        RankingSyncLog.PreferenceKey, RankingSync.PendingPreferenceKey, RankingSync.MachinePreferenceKey, RankingSync.PeersPreferenceKey,
        AchievementStore.PreferenceKey,
    };
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
            foreach (string song in new[] { Song, JapaneseSong })
                foreach (string difficulty in Difficulties)
                {
                    PlayerPrefs.DeleteKey(DailyRankingStore.Key(ns, song, difficulty));
                    PlayerPrefs.DeleteKey(HighScoreStore.Key(ns, song, difficulty));
                }
        PlayerPrefs.DeleteKey(LogKey);
        PlayerPrefs.DeleteKey(PendingKey);
        PlayerPrefs.DeleteKey(AchievementKey);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedAchievementKey);
        OperatorMode.OverrideForTest(false);
        foreach (var pair in production)
            Assert.AreEqual(pair.Value, PlayerPrefs.HasKey(pair.Key) ? PlayerPrefs.GetString(pair.Key) : null, "本番のキーに触れない: " + pair.Key);
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    // GameSession の静的な状態を、private のもの(HI-SCORE を残した回・残す前の最高点・遊び終わった時刻など)も含めて退避して戻す。
    static Dictionary<FieldInfo, object> SaveGameSession()
    {
        var saved = new Dictionary<FieldInfo, object>();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) saved[field] = field.GetValue(null);
        return saved;
    }

    static void RestoreGameSession(Dictionary<FieldInfo, object> saved)
    {
        foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
    }

    static RankingPlay Play(string id = null, int score = 50000, string song = Song, string difficulty = "Normal", string station = "A",
        DateTime? playedAt = null)
        => RankingSyncLog.NewPlay(id ?? NewId(), song, difficulty, score, PlayRankHelper.Label(PlayRankHelper.FromScore(score)), .8f,
            playedAt ?? PlayedAt, station);

    static string PlaysMessage(string from, string station, params RankingPlay[] plays)
    {
        var log = new RankingSyncLog();
        foreach (var play in plays) log.Add(play);
        return log.Bundles(from, station)[0].Text;
    }

    static string EmptyHash => new RankingSyncLog().HashText;

    RankingSync Offline(string machineId)
    {
        var sync = RankingSync.Create(new RankingSync.Settings
        {
            Offline = true, Broadcast = false, MachineId = machineId, Station = "A", LogKey = LogKey, PendingKey = PendingKey, ShowNotices = false,
        });
        created.Add(sync);
        return sync;
    }

    // ふだんの表(名前空間 "")。
    static DailyRankingStore.Table DailyTable(string song, string difficulty)
    {
        Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key("", song, difficulty), ""), "2000-01-01", out var table));
        return table;
    }

    // ---- ログの併合と検査 ----

    [Test]
    public void MergeAddsOnlyPlaysWhoseIdIsNew()
    {
        var log = new RankingSyncLog();
        var first = Play();
        var second = Play();
        Assert.IsTrue(log.Add(first));
        Assert.IsFalse(log.Add(first), "同じ回は二度足さない");
        // 同じ id で中身の違う回が届いても、先に来たほうのまま
        var added = log.Merge(new[] { Play(first.id, 99999), second, second });
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual(second.id, added[0].id);
        Assert.AreEqual(2, log.Count);
        Assert.AreEqual(first.score, log.Find(first.id).score);
        Assert.AreEqual(0, log.Merge(null).Count);
    }

    [Test]
    public void BrokenValuesAreDroppedInsteadOfMakingFakeRecords()
    {
        var cases = new (string name, Action<RankingPlay> breakIt)[]
        {
            ("空の曲", p => p.song = " "),
            ("制御文字の曲", p => p.song = "a\nb"),
            ("長すぎる曲", p => p.song = new string('a', 65)),
            ("対になっていないサロゲートの曲", p => p.song = "a\ud800b"),
            ("負の点", p => p.score = -1),
            ("満点を超える点", p => p.score = ScoreManager.MaxScore + 1),
            ("精度が NaN", p => p.acc = float.NaN),
            ("精度が負", p => p.acc = -.01f),
            ("精度が1を超える", p => p.acc = 1.01f),
            ("id が無い", p => p.id = null),
            ("短すぎる id", p => p.id = "abc1234"),
            ("長すぎる id", p => p.id = new string('a', 65)),
            ("id に記号", p => p.id = "abcd_efgh"),
            ("id に空白", p => p.id = "abcd efgh"),
            ("難易度が空", p => p.diff = ""),
            ("難易度に空白", p => p.diff = "No rmal"),
            ("ランクが空", p => p.rank = ""),
            ("ランクが長すぎる", p => p.rank = "SSSSSSSSS"),
            ("日付の形が違う", p => p.date = "2026-10-28"),
            ("ありえない日付", p => p.date = "2026/13/40"),
            ("台名に空白", p => p.st = "A B"),
            ("時刻が0", p => p.at = 0),
            ("時刻が大きすぎる", p => p.at = long.MaxValue),
        };
        foreach (var (name, breakIt) in cases)
        {
            var play = Play();
            breakIt(play);
            Assert.IsFalse(RankingSyncLog.IsValid(play), name);
            var log = new RankingSyncLog();
            Assert.IsFalse(log.Add(play), name);
            Assert.AreEqual(0, log.Count, name);
        }
        Assert.IsFalse(RankingSyncLog.IsValid(null));
        Assert.IsTrue(RankingSyncLog.IsValid(Play(song: JapaneseSong, station: "")), "日本語の曲 ID・台名なしは通る");
        Assert.IsTrue(RankingSyncLog.IsValid(Play(song: "Phobia PARTⅠ", difficulty: "D")), "空白と記号の入った曲 ID・裏譜面の1文字は通る");
        Assert.IsTrue(RankingSyncLog.IsValid(Play(score: 0)) && RankingSyncLog.IsValid(Play(score: ScoreManager.MaxScore)), "0点と満点は通る");
    }

    // 必須の数の欄が JSON に無ければ、0 として受けずに捨てる(初期値 -1 のまま検査に落ちる)。
    [Test]
    public void MissingNumbersAreRejectedInsteadOfBecomingZero()
    {
        var play = Play(score: 70000);
        string PlayJson(string skip)
        {
            var fields = new List<string>
            {
                "\"id\":\"" + play.id + "\"", "\"song\":\"" + play.song + "\"", "\"diff\":\"" + play.diff + "\"",
                "\"score\":" + play.score.ToString(CultureInfo.InvariantCulture), "\"rank\":\"" + play.rank + "\"",
                "\"acc\":0.8", "\"date\":\"" + play.date + "\"", "\"st\":\"A\"", "\"at\":" + play.at.ToString(CultureInfo.InvariantCulture),
            };
            fields.RemoveAll(f => f.StartsWith("\"" + skip + "\"", StringComparison.Ordinal));
            return "{" + string.Join(",", fields) + "}";
        }
        Assert.IsTrue(RankingSyncLog.IsValid(JsonUtility.FromJson<RankingPlay>(PlayJson("none"))), "全部の欄があれば通る");
        foreach (string skip in new[] { "score", "acc", "at", "id", "song", "diff", "rank", "date" })
            Assert.IsFalse(RankingSyncLog.IsValid(JsonUtility.FromJson<RankingPlay>(PlayJson(skip))), skip + " が無い");
        Assert.IsTrue(RankingSyncLog.IsValid(JsonUtility.FromJson<RankingPlay>(PlayJson("st"))), "台名は無くてもよい");

        // 欠けた回の混じった束は、その回だけを捨てる
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        var good = Play(score: 60000, station: "B");
        string message = RankingSyncLog.BuildPlays(new[] { PlayJson("score"), JsonUtility.ToJson(good) }, other, "B", 2, EmptyHash)[0].Text;
        Assert.AreEqual(1, sync.ReceiveForTest(message, From, t0));
        Assert.IsTrue(sync.Count == 1 && DailyTable(Song, "Normal").entries.Single().runId == good.id);
        // 件数の欠けた要約は読まない
        string digest = RankingSyncLog.BuildDigest(other, "B", 3, EmptyHash);
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"count\":3,", ""), out _));
    }

    [Test]
    public void SavedLogRoundTripsAndBrokenSavedValuesAreDiscarded()
    {
        var log = new RankingSyncLog();
        var japanese = Play(song: JapaneseSong);
        var hard = Play(score: ScoreManager.MaxScore, difficulty: "Hard", station: "");
        Assert.IsTrue(log.Add(japanese) && log.Add(hard));
        log.Save(LogKey);
        var loaded = RankingSyncLog.Load(LogKey);
        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual(log.HashText, loaded.HashText);
        Assert.IsTrue(RankingSyncLog.SameContent(japanese, loaded.Find(japanese.id)), "中身がそのまま戻る");
        Assert.AreEqual(JapaneseSong, loaded.Find(japanese.id).song);
        Assert.AreEqual("", loaded.Find(hard.id).st);

        foreach (string text in new[] { "not json", "{\"v\":3,\"plays\":[]}", "{\"v\":0,\"plays\":[]}", "{\"plays\":[]}" })
        {
            var empty = RankingSyncLog.FromJson(text, out bool broken);
            Assert.IsTrue(broken, text);
            Assert.AreEqual(0, empty.Count, text);
        }
        // 一部の回だけ壊れていれば、その回だけ捨てる(同じ回が二度あれば1回に)
        string mixed = "{\"v\":1,\"plays\":[" + JsonUtility.ToJson(japanese) + ",{\"id\":\"x\",\"song\":\"\"}," + JsonUtility.ToJson(japanese) + "]}";
        var partial = RankingSyncLog.FromJson(mixed, out bool partlyBroken);
        Assert.IsTrue(partlyBroken);
        Assert.AreEqual(1, partial.Count);
        Assert.AreEqual(0, RankingSyncLog.FromJson("", out bool none).Count);
        Assert.IsFalse(none, "保存が無いのは壊れていない");
    }

    // 整形された(空白・改行のある)JSON・欄の順の違う JSON も読む。plays が無い・配列でない・2つある・後ろに余計なものがあれば壊れ。
    // 配列の中のオブジェクトでない要素は、壊れた1回として捨てる。
    [Test]
    public void FormattedJsonIsReadAndAMissingOrWrongPlaysIsBroken()
    {
        var play = Play(song: JapaneseSong);
        string formatted = "{\n  \"v\" : 1 ,\n  \"plays\" : [\n    " + JsonUtility.ToJson(play, true) + "\n  ]\n}\n";
        var log = RankingSyncLog.FromJson(formatted, out bool broken);
        Assert.IsFalse(broken);
        Assert.AreEqual(1, log.Count);
        Assert.IsTrue(RankingSyncLog.SameContent(play, log.Find(play.id)));
        var reordered = RankingSyncLog.FromJson("{\"plays\":[" + JsonUtility.ToJson(play) + "],\"v\":1}", out bool reorderedBroken);
        Assert.IsFalse(reorderedBroken);
        Assert.AreEqual(1, reordered.Count);
        foreach (string text in new[] { "{\"v\":1}", "{\"v\":1,\"plays\":3}", "{\"v\":1,\"plays\":{}}", "{\"v\":1,\"plays\":[],\"plays\":[]}",
            "{\"v\":1,\"plays\":[]} x", "{\"v\":1,\"plays\":[}", "[]" })
        {
            Assert.AreEqual(0, RankingSyncLog.FromJson(text, out bool bad).Count, text);
            Assert.IsTrue(bad, text);
        }
        var mixed = RankingSyncLog.FromJson("{\"v\":1,\"plays\":[1,\"x\"," + JsonUtility.ToJson(play) + ",[]]}", out bool mixedBroken);
        Assert.IsTrue(mixedBroken, "オブジェクトでない要素は壊れた1回");
        Assert.AreEqual(1, mixed.Count);
        // 受け取った1通も、空白があっても読み、plays の無い1通は読まない
        string from = NewId();
        string digest = RankingSyncLog.BuildDigest(from, "A", 0, EmptyHash).Replace("\"plays\":[]", "\"plays\" : [ ]").Replace(",\"hash\"", ", \"hash\"");
        Assert.IsTrue(RankingSyncLog.TryParse(digest, out var message));
        Assert.AreEqual(from, message.from);
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{\"v\":" + RankingSyncLog.Version + ",\"kind\":\"digest\",\"from\":\"" + from +
                                               "\",\"st\":\"A\",\"count\":0,\"hash\":\"" + EmptyHash + "\"}", out _));
    }

    [Test]
    public void ReceivedTextIsCheckedForPrefixSizeJsonAndVersion()
    {
        string from = NewId(), heardId = NewId();
        string digest = RankingSyncLog.BuildDigest(from, "A", 3, "0123456789abcdef", new[] { heardId });
        Assert.IsTrue(RankingSyncLog.TryParse(digest, out var message));
        Assert.AreEqual(RankingSyncLog.DigestKind, message.kind);
        Assert.AreEqual(from, message.from);
        Assert.AreEqual("A", message.st);
        Assert.AreEqual(3, message.count);
        Assert.AreEqual("0123456789abcdef", message.hash);
        CollectionAssert.AreEqual(new[] { heardId }, message.heard);
        Assert.AreEqual(0, message.plays.Count);

        Assert.IsFalse(RankingSyncLog.TryParse(null, out _));
        Assert.IsFalse(RankingSyncLog.TryParse("PHONESABER_DISCOVER 1", out _), "ほかの目印");
        Assert.AreEqual("BTSRANK2 ", RankingSyncLog.Prefix, "通信の版は 2");
        Assert.IsFalse(RankingSyncLog.TryParse("BTSRANK1 " + digest.Substring(RankingSyncLog.Prefix.Length), out _), "版の違う目印(古い版)");
        Assert.IsFalse(RankingSyncLog.TryParse("BTSRANK3 " + digest.Substring(RankingSyncLog.Prefix.Length), out _), "版の違う目印(新しい版)");
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{broken", out _), "JSON として読めない");
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{}", out _), "版が無い");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"v\":2", "\"v\":1"), out _), "版が違う");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"v\":2", "\"v\":3"), out _), "版が違う");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"digest\"", "\"hello\""), out _), "知らない種類");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace(from, "bad id!"), out _), "差出人の ID が壊れている");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"st\":\"A\"", "\"st\":\"A B\""), out _), "台名が壊れている");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("0123456789abcdef", "xyz"), out _), "要約が壊れている");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"count\":3", "\"count\":-1"), out _), "件数が負");
        string padded = digest.Substring(0, digest.Length - 1) + ",\"pad\":\"" + new string('x', RankingSyncLog.MaxReceiveBytes) + "\"}";
        Assert.IsFalse(RankingSyncLog.TryParse(padded, out _), "1400 バイトを超える");

        byte[] bytes = Encoding.UTF8.GetBytes(digest);
        Assert.IsTrue(RankingSyncLog.TryDecode(bytes, bytes.Length, out string text));
        Assert.AreEqual(digest, text);
        var big = new byte[RankingSyncLog.MaxReceiveBytes + 1];
        Array.Copy(bytes, big, bytes.Length);
        Assert.IsFalse(RankingSyncLog.TryDecode(big, big.Length, out _), "1400 バイトを超える");
        var broken = (byte[])bytes.Clone();
        broken[broken.Length - 3] = 0xff;
        Assert.IsFalse(RankingSyncLog.TryDecode(broken, broken.Length, out _), "壊れた UTF-8");
        var other = Encoding.ASCII.GetBytes("PHONESABER_DISCOVER 1");
        Assert.IsFalse(RankingSyncLog.TryDecode(other, other.Length, out _), "ほかの目印");
    }

    // 新しい1回(遊び終わった直後の1回)と要約は、全部の記録の束より先に処理する。受信スレッドで JSON を読まずに分ける。
    [Test]
    public void NewSinglePlaysAndDigestsAreToldApartFromBundles()
    {
        string from = NewId();
        Assert.IsTrue(RankingSyncLog.IsFresh(PlaysMessage(from, "A", Play())), "1回だけ");
        Assert.IsTrue(RankingSyncLog.IsFresh(RankingSyncLog.BuildDigest(from, "A", 0, EmptyHash)), "要約");
        Assert.IsFalse(RankingSyncLog.IsFresh(PlaysMessage(from, "A", Play(), Play())), "2回以上の束");
    }

    [Test]
    public void DigestIsTheSameWhateverTheOrder()
    {
        var plays = Enumerable.Range(0, 20).Select(i => Play(score: i * 1000)).ToList();
        var forward = new RankingSyncLog();
        var backward = new RankingSyncLog();
        foreach (var play in plays) forward.Add(play);
        for (int i = plays.Count - 1; i >= 0; i--) backward.Add(plays[i]);
        Assert.AreEqual(forward.HashText, backward.HashText);
        Assert.IsTrue(backward.SameDigest(forward.Count, forward.HashText));
        var fewer = new RankingSyncLog();
        foreach (var play in plays.Skip(1)) fewer.Add(play);
        Assert.IsFalse(fewer.SameDigest(forward.Count, forward.HashText));
        Assert.AreNotEqual(forward.HashText, fewer.HashText);
        Assert.AreEqual("0000000000000000", EmptyHash, "空なら 0");
        // 64bit FNV-1a の既知の値
        Assert.AreEqual(0xcbf29ce484222325UL, RankingSyncLog.IdHash(""));
        Assert.AreEqual(0xaf63dc4c8601ec8cUL, RankingSyncLog.IdHash("a"));
    }

    [Test]
    public void BundlesStayWithin1200BytesAndCarryEveryPlay()
    {
        var log = new RankingSyncLog();
        for (int i = 0; i < 150; i++)
        {
            // 検査を通る一番大きな形(64文字の id・64文字の日本語の曲・16文字の難易度・8文字のランク・16文字の台名)も混ぜる
            bool largest = i % 3 == 0;
            var play = Play(id: new string('a', 56) + i.ToString("D8", CultureInfo.InvariantCulture),
                song: largest ? new string('揺', RankingSyncLog.MaxSongLength) : Song,
                difficulty: largest ? new string('d', RankingSyncLog.MaxDifficultyLength) : "Hard",
                score: i * 600, station: largest ? new string('B', 16) : "B");
            if (largest) play.rank = "ABCDEFGH";
            Assert.IsTrue(log.Add(play), "検査を通る");
        }
        string from = new string('f', RankingSyncLog.MaxIdLength);
        string station = new string('A', 16);
        var bundles = log.Bundles(from, station);
        Assert.Greater(bundles.Count, 1);
        var copy = new RankingSyncLog();
        int total = 0;
        foreach (var bundle in bundles)
        {
            Assert.LessOrEqual(Encoding.UTF8.GetByteCount(bundle.Text), RankingSyncLog.MaxDatagramBytes);
            Assert.IsTrue(RankingSyncLog.TryParse(bundle.Text, out var message));
            Assert.AreEqual(RankingSyncLog.PlaysKind, message.kind);
            Assert.AreEqual(bundle.Plays, message.plays.Count);
            Assert.AreEqual(log.Count, message.count);
            Assert.AreEqual(log.HashText, message.hash);
            total += copy.Merge(message.plays).Count;
        }
        Assert.AreEqual(150, total);
        Assert.AreEqual(log.HashText, copy.HashText, "束を全部足すと同じ要約になる");
        // 少しずつ作っても(1回に2通ずつ)、同じ束になる
        var stepped = new List<string>();
        for (int next = 0; next < log.Count;)
        {
            var part = log.Bundles(from, station, next, 2, out int after);
            Assert.LessOrEqual(part.Count, 2);
            Assert.Greater(after, next, "必ず進む");
            stepped.AddRange(part.Select(d => d.Text));
            next = after;
        }
        CollectionAssert.AreEqual(bundles.Select(d => d.Text), stepped);
        var last = log.Bundles(from, station, log.Count - 1);
        Assert.AreEqual(1, last.Count);
        Assert.AreEqual(1, last[0].Plays, "最後の1回だけを送る形");
        // 要約は「聞こえた相手」を8件まで入れても 1200 バイト以下
        var heard = Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), RankingSyncLog.MaxIdLength)).ToArray();
        string digest = log.DigestMessage(from, station, heard);
        Assert.LessOrEqual(Encoding.UTF8.GetByteCount(digest), RankingSyncLog.MaxDatagramBytes);
        Assert.IsTrue(RankingSyncLog.TryParse(digest, out var parsed));
        Assert.AreEqual(RankingSyncLog.MaxHeard, parsed.heard.Count);
    }

    // ---- 表への併合 ----

    static HighScoreEntry Entry(int score, string runId, long at = 0) =>
        new HighScoreEntry { score = score, rank = "A", accuracy = .7f, date = "2026/10/28", runId = runId, at = at };

    [Test]
    public void HighScoreMergeSkipsTheSameRunAndKeepsTheBestFive()
    {
        Assert.AreEqual(RankingMergeResult.Added, HighScoreStore.Merge(Song, "Normal", Entry(500, "run-aaaa-1", 100)));
        Assert.AreEqual(RankingMergeResult.AlreadyThere, HighScoreStore.Merge(Song, "Normal", Entry(500, "run-aaaa-1", 100)), "同じ回は足さない");
        Assert.AreEqual(RankingMergeResult.AlreadyThere, HighScoreStore.Merge(Song, "Normal", Entry(900, "run-aaaa-1", 100)),
            "同じ回なら点が違っても足さない");
        // 自分の台で残した回(Record)と同じ runId も足さない
        HighScoreStore.Record(Song, "Normal", Entry(700, "run-cccc-3", 300), out _);
        Assert.AreEqual(RankingMergeResult.AlreadyThere, HighScoreStore.Merge(Song, "Normal", Entry(700, "run-cccc-3", 300)));
        var batch = HighScoreStore.MergeAll(Song, "Normal", new[] { Entry(800, "run-dddd-0", 400), Entry(801, "run-dddd-1", 400),
            Entry(802, "run-dddd-2", 400), Entry(500, "run-bbbb-2", 200) });
        CollectionAssert.AreEqual(new[] { RankingMergeResult.Added, RankingMergeResult.Added, RankingMergeResult.Added, RankingMergeResult.OutsideTop },
            batch, "まとめて足しても、押し出された回は上位5件の外");
        var table = HighScoreStore.Load(Song, "Normal");
        CollectionAssert.AreEqual(new[] { 802, 801, 800, 700, 500 }, table.entries.Select(e => e.score));
        Assert.AreEqual("run-aaaa-1", table.entries[4].runId, "同点の 500 は時刻の早い回が残る");
        Assert.AreEqual(RankingMergeResult.OutsideTop, HighScoreStore.Merge(Song, "Normal", Entry(400, "run-eeee-5", 500)), "上位5件の外");
        Assert.AreEqual(RankingMergeResult.Added, HighScoreStore.Merge(Song, "Normal", Entry(1000, "run-gggg-7", 600)));
        CollectionAssert.AreEqual(new[] { 1000, 802, 801, 800, 700 }, HighScoreStore.Load(Song, "Normal").entries.Select(e => e.score));
        Assert.AreEqual(RankingMergeResult.Failed, HighScoreStore.Merge(Song, "Normal", Entry(1200, "")), "回の ID の無い記録は足さない");
        Assert.AreEqual(RankingMergeResult.Failed, HighScoreStore.Merge(Song, "Normal", null));
        Assert.AreEqual(0, HighScoreStore.Load(Song, "Hard").entries.Count, "難易度ごとに分かれる");
    }

    // 同点の並び(点の降順 → 時刻の昇順 → runId の昇順)は届いた順に依らない。2台が違う順に受け取っても、上位5件は同じ中身・同じ並び。
    [Test]
    public void TiesEndUpInTheSameOrderWhateverTheArrivalOrder()
    {
        var plays = new[]
        {
            Entry(900, "run-c", 300), Entry(900, "run-b", 100), Entry(900, "run-a", 100),
            Entry(900, "run-d", 200), Entry(900, "run-e", 50), Entry(950, "run-f", 900),
        };
        var expected = new[] { "run-f", "run-e", "run-a", "run-b", "run-d" };
        foreach (var arrival in new[] { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 4, 3, 2, 1, 0 }, new[] { 3, 0, 5, 2, 4, 1 } })
        {
            var table = new HighScoreTable();
            foreach (int i in arrival) HighScoreStore.Insert(table, plays[i], HighScoreStore.MaxEntries);
            CollectionAssert.AreEqual(expected, table.entries.Select(e => e.runId), string.Join(",", arrival));
        }
        // 保存を通しても同じ(片方は1回ずつ、もう片方は逆の順にまとめて)
        foreach (var play in plays) HighScoreStore.Merge(Song, "Hard", play);
        HighScoreStore.MergeAll(JapaneseSong, "Hard", plays.Reverse().ToArray());
        CollectionAssert.AreEqual(expected, HighScoreStore.Load(Song, "Hard").entries.Select(e => e.runId));
        CollectionAssert.AreEqual(expected, HighScoreStore.Load(JapaneseSong, "Hard").entries.Select(e => e.runId));
    }

    // 同じ台の記録は時刻が増えるので、前からの「同点は先の記録が上」と同じ。古い記録(時刻 0)は同点で一番古い扱い。
    [Test]
    public void LocalTiesKeepTheEarlierRecordAboveAndOldRecordsCountAsOldest()
    {
        HighScoreStore.Record(Song, "Normal", new HighScoreEntry { score = 600, rank = "A", date = "old" }, out _);
        Assert.AreEqual(1, HighScoreStore.Record(Song, "Normal", Entry(600, "run-later-2", 2000), out _), "あとの記録は下");
        Assert.AreEqual(2, HighScoreStore.Record(Song, "Normal", Entry(600, "run-later-3", 3000), out _), "あとの記録は下");
        // 別の台で先に遊んだ回(時刻が早い)は、あとから届いても、この台のあとの回より上
        Assert.AreEqual(RankingMergeResult.Added, HighScoreStore.Merge(Song, "Normal", Entry(600, "run-earlier-1", 1000)));
        var table = HighScoreStore.Load(Song, "Normal");
        CollectionAssert.AreEqual(new[] { "old", "2026/10/28", "2026/10/28", "2026/10/28" }, table.entries.Select(e => e.date));
        CollectionAssert.AreEqual(new[] { "", "run-earlier-1", "run-later-2", "run-later-3" }, table.entries.Select(e => e.runId ?? ""));
    }

    [Test]
    public void HighScoreTryLoadTellsABrokenTableFromAMissingOne()
    {
        Assert.IsTrue(HighScoreStore.TryLoad(Song, "Normal", out var missing));
        Assert.AreEqual(0, missing.entries.Count, "無ければ空の表");
        PlayerPrefs.SetString(HighScoreStore.Key(Song, "Normal"), "{ broken");
        Assert.IsFalse(HighScoreStore.TryLoad(Song, "Normal", out var broken));
        Assert.AreEqual(0, broken.entries.Count);
        Assert.AreEqual(0, HighScoreStore.Load(Song, "Normal").entries.Count, "Load は今までどおり空の表");
        Assert.AreEqual(RankingMergeResult.Failed, HighScoreStore.Merge(Song, "Normal", Entry(900, "run-into-broken", 10)));
        Assert.AreEqual("{ broken", PlayerPrefs.GetString(HighScoreStore.Key(Song, "Normal")), "壊れた表は上書きしない");
    }

    [Test]
    public void HighScoreRecordKeepsTheRunIdAndTime()
    {
        HighScoreStore.Record(Song, "Normal", Entry(600, "run-keep-1", 1234), out _);
        var entry = HighScoreStore.Load(Song, "Normal").entries[0];
        Assert.AreEqual("run-keep-1", entry.runId);
        Assert.AreEqual(1234, entry.at);
    }

    [Test]
    public void DailyRankingMergeSkipsTheSameRunAndKeepsTheFirstDay()
    {
        DailyRankingStore.Record(Song, "Normal", "local-run-1", 500, new DateTime(2026, 9, 30, 12, 0, 0));
        Assert.AreEqual(RankingMergeResult.Added, DailyRankingStore.Merge(Song, "Normal", "remote-run-2", 800, PlayedAt));
        Assert.AreEqual(RankingMergeResult.AlreadyThere, DailyRankingStore.Merge(Song, "Normal", "remote-run-2", 800, PlayedAt), "同じ回は足さない");
        Assert.AreEqual(RankingMergeResult.AlreadyThere, DailyRankingStore.Merge(Song, "Normal", "local-run-1", 500, PlayedAt),
            "自分の台で残した回も足さない");
        var table = DailyTable(Song, "Normal");
        Assert.AreEqual(2, table.entries.Count);
        Assert.AreEqual("2026-09-30", table.day, "数え始めた日はそのまま");
        // 足したあとの自分の回は、相手の回も数えた順位になる
        var result = DailyRankingStore.Record(Song, "Normal", "local-run-3", 600, PlayedAt);
        Assert.AreEqual(2, result.Rank);
        Assert.AreEqual(3, result.TotalPlays);
        // まとめて足す(表の読み書きは1回)。同じ束の中の重なりも1回
        var batch = DailyRankingStore.MergeAll(Song, "Normal", new[]
        {
            new DailyRankingStore.Entry { runId = "remote-run-5", score = 10 }, new DailyRankingStore.Entry { runId = "remote-run-5", score = 10 },
            new DailyRankingStore.Entry { runId = "local-run-3", score = 600 }, new DailyRankingStore.Entry { runId = "", score = 1 },
        }, PlayedAt);
        CollectionAssert.AreEqual(new[] { RankingMergeResult.Added, RankingMergeResult.AlreadyThere, RankingMergeResult.AlreadyThere,
            RankingMergeResult.Failed }, batch);
        Assert.AreEqual(4, DailyTable(Song, "Normal").entries.Count);
        // 表がまだ無ければ、足した回の日から数え始める
        Assert.AreEqual(RankingMergeResult.Added, DailyRankingStore.Merge(Song, "Hard", "remote-run-4", 100, PlayedAt));
        Assert.AreEqual("2026-10-28", DailyTable(Song, "Hard").day);
        Assert.AreEqual(RankingMergeResult.Failed, DailyRankingStore.Merge(Song, "Normal", "bad-score", -1, PlayedAt));
        Assert.AreEqual(RankingMergeResult.Failed, DailyRankingStore.Merge(" ", "Normal", "no-song-run", 100, PlayedAt));
    }

    [Test]
    public void DailyRankingMergeKeepsABrokenTableAsItIs()
    {
        PlayerPrefs.SetString(DailyRankingStore.Key(Song, "Normal"), "not json");
        Assert.AreEqual(RankingMergeResult.Failed, DailyRankingStore.Merge(Song, "Normal", "remote-run-1", 100, PlayedAt));
        Assert.AreEqual("not json", PlayerPrefs.GetString(DailyRankingStore.Key(Song, "Normal")));
    }

    // ---- 受け取ったあとの処理(RankingSync) ----

    [Test]
    public void PlaysSentByThisMachineAreIgnoredAndOthersReachBothTables()
    {
        string self = NewId(), other = NewId();
        var sync = Offline(self);
        double t0 = Time.realtimeSinceStartupAsDouble;
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(self, "A", Play(score: 70000)), From, t0),
            "自分の送ったもの(ブロードキャストは自分にも届く)は捨てる");
        Assert.AreEqual(0, sync.Count);
        Assert.AreEqual(0, sync.PeerCount, "自分を相手として数えない");
        Assert.IsFalse(PlayerPrefs.HasKey(HighScoreStore.Key(Song, "Normal")));

        var theirs = Play(score: 80000, station: "B");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", theirs), From, t0 + 1));
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", theirs), From, t0 + 2), "同じ回は何度届いても1回");
        Assert.AreEqual(1, sync.Count);
        Assert.AreEqual(1, sync.ReceivedPlays);
        Assert.AreEqual(1, sync.PeerCount);
        Assert.AreEqual(0, sync.ConflictingPlays, "同じ中身の送り直しは食い違いではない");
        CollectionAssert.AreEqual(new[] { "B" }, sync.PeerStations);
        var daily = DailyTable(Song, "Normal");
        Assert.AreEqual(1, daily.entries.Count);
        Assert.AreEqual(theirs.id, daily.entries[0].runId);
        Assert.AreEqual(80000, daily.entries[0].score);
        var high = HighScoreStore.Load(Song, "Normal").entries.Single();
        Assert.AreEqual(theirs.id, high.runId);
        Assert.AreEqual(80000, high.score);
        Assert.AreEqual(theirs.rank, high.rank);
        Assert.AreEqual(theirs.date, high.date);
        Assert.AreEqual(theirs.acc, high.accuracy);
        Assert.AreEqual(theirs.at, high.at, "同点の並びに使う時刻も、相手の台と同じ");
        sync.Tick(t0 + 2); // ログを書き出す
        Assert.AreEqual(1, RankingSyncLog.Load(LogKey).Count, "ログにも残る(次の起動で相手と合わせられる)");
        // 束の中の壊れた回だけを捨て、残りは足す
        var next = Play(score: 60000, station: "B");
        string mixed = RankingSyncLog.BuildPlays(new[] { "{\"id\":\"x\",\"song\":\"\"}", JsonUtility.ToJson(next) }, other, "B", 3,
            "0123456789abcdef")[0].Text;
        Assert.AreEqual(1, sync.ReceiveForTest(mixed, From, t0 + 3));
        Assert.AreEqual(2, sync.Count);
        Assert.AreEqual(2, HighScoreStore.Load(Song, "Normal").entries.Count);
        // 自分の台で記録した回は、表には書かない(表は GameSession が残す)
        var mine = Play(score: 90000);
        Assert.IsTrue(sync.Record(mine));
        Assert.IsFalse(sync.Record(mine), "同じ回は1回だけ");
        Assert.AreEqual(3, sync.Count);
        Assert.AreEqual(2, HighScoreStore.Load(Song, "Normal").entries.Count);
        Assert.IsFalse(HighScoreStore.Load(Song, "Normal").entries.Exists(e => e.runId == mine.id));
        Assert.AreEqual(3, RankingSyncLog.Load(LogKey).Count, "自分の回はすぐログに書く");
    }

    [Test]
    public void APlayWithTheSameIdButDifferentContentKeepsTheFirstAndWarnsOnce()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        var first = Play(score: 70000, station: "B");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", first), From, t0));
        var changed = RankingSyncLog.Copy(first);
        changed.score = 90000;
        LogAssert.Expect(LogType.Warning, "RankingSync: 同じ回の ID で中身の違う記録が届きました。先に来たほうを残します。");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", changed), From, t0 + 1));
        changed.score = 95000;
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", changed), From, t0 + 2));
        Assert.AreEqual(2, sync.ConflictingPlays, "数えるが、警告は1回");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", first), From, t0 + 3), "同じ中身は数えない");
        Assert.AreEqual(2, sync.ConflictingPlays);
        Assert.AreEqual(70000, HighScoreStore.Load(Song, "Normal").entries.Single().score, "先に来たほうのまま");
        Assert.AreEqual(70000, DailyTable(Song, "Normal").entries.Single().score);
        Assert.IsFalse(sync.Record(changed), "自分の台の記録でも、同じ id の違う中身は足さない");
        Assert.AreEqual(3, sync.ConflictingPlays);
    }

    // 表が壊れていて足せなかった回は、ログに入れずに適用待ちに残し、表が直ったら足し直す(壊れた表は上書きしない)。
    [Test]
    public void APlayThatCannotBeAddedWaitsAndIsAddedOnceTheTableIsFixed()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        PlayerPrefs.SetString(HighScoreStore.Key(Song, "Normal"), "{ broken");
        var play = Play(score: 88000, station: "B");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", play), From, t0), "ログには入れない");
        Assert.AreEqual(0, sync.Count);
        Assert.AreEqual(1, sync.PendingCount);
        Assert.AreEqual("{ broken", PlayerPrefs.GetString(HighScoreStore.Key(Song, "Normal")), "壊れた表は上書きしない");
        Assert.AreEqual(1, DailyTable(Song, "Normal").entries.Count, "通算の表には足せた");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", play), From, t0 + 1), "送り直しが来ても重ねない");
        Assert.AreEqual(1, sync.PendingCount);
        sync.Tick(t0 + 5);
        Assert.AreEqual(1, sync.PendingCount, "すぐには足し直さない");
        PlayerPrefs.DeleteKey(HighScoreStore.Key(Song, "Normal")); // 表を直す(消す)
        sync.Tick(t0 + 11);
        Assert.AreEqual(0, sync.PendingCount);
        Assert.AreEqual(1, sync.Count, "足せたのでログに入る");
        Assert.AreEqual(play.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        Assert.AreEqual(1, DailyTable(Song, "Normal").entries.Count, "通算の表に二重に足さない");
    }

    [Test]
    public void FailedSavesAreRetriedAndTheLogIsNotMarkedSavedUntilItIs()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.FailSavesForTest = true;
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(NewId(), "B", Play(station: "B")), From, t0));
        Assert.IsTrue(sync.Record(Play()), "自分の回も、保存に失敗してもログには残す");
        sync.Tick(t0);
        Assert.AreEqual(0, RankingSyncLog.Load(LogKey).Count, "保存できていない");
        sync.FailSavesForTest = false;
        sync.Tick(t0 + 1);
        Assert.AreEqual(0, RankingSyncLog.Load(LogKey).Count, "失敗のあとは少し待つ");
        sync.Tick(t0 + 2.1);
        Assert.AreEqual(2, RankingSyncLog.Load(LogKey).Count, "あとで保存し直す");
    }

    // ディスクへの保存(PlayerPrefs.Save)だけが失敗したときも、保存待ちのまま残して、あとで保存し直す。
    [Test]
    public void OnlyTheDiskSaveFailingIsRetriedToo()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.FailDiskSavesForTest = true;
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(NewId(), "B", Play(station: "B")), From, t0));
        sync.Tick(t0);
        Assert.AreEqual(1, RankingSyncLog.Load(LogKey).Count, "ログは PlayerPrefs へ書けた");
        Assert.IsTrue(sync.SaveWaiting, "ディスクへの保存は失敗したので、まだ待っている");
        sync.FailDiskSavesForTest = false;
        sync.Tick(t0 + 1);
        Assert.IsTrue(sync.SaveWaiting, "失敗のあとは少し待つ");
        sync.Tick(t0 + 2.1);
        Assert.IsFalse(sync.SaveWaiting, "あとで保存し直す");
    }

    // 自分の回で表に書けなかった分は、起動を待たずに、動いている間に足し直す(書けた表には二重にしない)。
    // 上位5件は、結果画面で書けなかった知らせ(LocalHighScoreFailed)があったときだけ足し直す(結果画面より先に足すと NEW RECORD が変わる)。
    [Test]
    public void ALocalPlayThatCouldNotBeStoredIsPutBackWhileRunning()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        PlayerPrefs.SetString(DailyRankingStore.Key(Song, "Normal"), "not json");
        var play = Play(score: 70000);
        LogAssert.Expect(LogType.Warning, "DailyRankingStore: 順位の記録を読み込めないため、順位を表示しません。");
        Assert.IsFalse(DailyRankingStore.Record(Song, "Normal", play.id, play.score, PlayedAt).Available, "通算の表に書けなかった(結果画面の順位は失敗の結果)");
        Assert.IsTrue(sync.Record(play, false));
        Assert.AreEqual(1, sync.PendingCount, "通算の表の足し直し待ち");
        sync.Tick(t0 + 11);
        Assert.AreEqual(1, sync.PendingCount, "まだ壊れている");
        Assert.AreEqual("not json", PlayerPrefs.GetString(DailyRankingStore.Key(Song, "Normal")), "壊れた表は上書きしない");
        PlayerPrefs.DeleteKey(DailyRankingStore.Key(Song, "Normal")); // 表を直す(消す)
        sync.Tick(t0 + 22);
        Assert.AreEqual(0, sync.PendingCount);
        Assert.AreEqual(play.id, DailyTable(Song, "Normal").entries.Single().runId, "直ったら、動いている間に足す");
        Assert.AreEqual(0, HighScoreStore.Load(Song, "Normal").entries.Count, "上位5件は結果画面が残すので触らない");

        // 結果画面で上位5件に書けなかった回も同じ
        PlayerPrefs.SetString(HighScoreStore.Key(Song, "Normal"), "{ broken");
        Assert.AreEqual(RankingMergeResult.Failed, HighScoreStore.RecordRun(Song, "Normal", RankingSyncLog.ToHighScoreEntry(play), out _, out _));
        sync.MarkHighScorePending(play.id);
        Assert.AreEqual(1, sync.PendingCount);
        PlayerPrefs.DeleteKey(HighScoreStore.Key(Song, "Normal"));
        sync.Tick(t0 + 33);
        Assert.AreEqual(0, sync.PendingCount);
        Assert.AreEqual(play.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        Assert.AreEqual(1, DailyTable(Song, "Normal").entries.Count, "通算の表に二重に足さない");
        // 結果画面の保存と足し直しが重なっても、二重にしない
        Assert.AreEqual(RankingMergeResult.AlreadyThere,
            HighScoreStore.RecordRun(Song, "Normal", RankingSyncLog.ToHighScoreEntry(play), out _, out int index));
        Assert.AreEqual(0, index);
        Assert.AreEqual(1, HighScoreStore.Load(Song, "Normal").entries.Count);
        Assert.AreEqual(1, sync.Count, "ログはそのまま");
    }

    // 片方の表に書けたら、もう片方が失敗しても保存待ちを立てる。足し直し待ちの相手の回は別のキーにも残り、起動し直しても消えない。
    [Test]
    public void APartlyAppliedPlayIsSavedAndItsWaitingPartSurvivesARestart()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        PlayerPrefs.SetString(HighScoreStore.Key(Song, "Normal"), "{ broken");
        var play = Play(score: 66000, station: "B");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(NewId(), "B", play), From, t0));
        Assert.IsTrue(sync.SaveWaiting, "通算の表には書けたので、保存待ち");
        sync.Tick(t0);
        Assert.IsFalse(sync.SaveWaiting, "保存した");
        Assert.AreEqual(1, RankingSyncLog.Load(PendingKey).Count, "足し直し待ちの回は別のキーに残る");
        Assert.AreEqual(0, RankingSyncLog.Load(LogKey).Count, "ログにはまだ入れない");
        // 起動し直す(作り直す)。表が直っていれば、前回の足し直し待ちを足す
        sync.Shutdown();
        PlayerPrefs.DeleteKey(HighScoreStore.Key(Song, "Normal"));
        var restarted = Offline(NewId());
        Assert.AreEqual(1, restarted.QueuedCount);
        restarted.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(1, restarted.Count, "両方の表に足せたので、ログに入る");
        Assert.AreEqual(play.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        Assert.AreEqual(1, DailyTable(Song, "Normal").entries.Count, "通算の表に二重に足さない");
        Assert.IsFalse(PlayerPrefs.HasKey(PendingKey), "足せたら別のキーから消す");
        Assert.AreEqual(1, RankingSyncLog.Load(LogKey).Count);
    }

    // 終了のときも保存の順番を守る: ログを書けないまま終わっても、足せた回を足し直し待ちの別のキーから消さない。
    [Test]
    public void ShuttingDownWhileTheLogCannotBeWrittenKeepsTheWaitingCopy()
    {
        var earlier = Play(score: 64000, station: "B");
        PlayerPrefs.SetString(PendingKey, RankingSyncLog.PlaysJson(new[] { earlier })); // 前回の足し直し待ち
        var sync = Offline(NewId());
        sync.FailLogWritesForTest = true;
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.Tick(t0); // 表は直っているので足せる(ログはメモリにだけ入り、書けない)
        Assert.AreEqual(1, sync.Count);
        Assert.AreEqual(earlier.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        // 別の相手の回は表が壊れていて足せない(別のキーを書き直すきっかけになる)
        PlayerPrefs.SetString(HighScoreStore.Key(JapaneseSong, "Normal"), "{ broken");
        var waitingPlay = Play(score: 65000, song: JapaneseSong, station: "B");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(NewId(), "B", waitingPlay), From, t0 + 1));
        sync.Shutdown(); // ログは書けないまま終わる
        CollectionAssert.AreEquivalent(new[] { earlier.id, waitingPlay.id }, RankingSyncLog.Load(PendingKey).Plays.Select(p => p.id),
            "ログに書けなかった回も、別のキーに残る");
        Assert.AreEqual(0, RankingSyncLog.Load(LogKey).Count);
        // 起動し直して(ログを書ければ)両方を足してログへ入れ、別のキーから消す
        PlayerPrefs.DeleteKey(HighScoreStore.Key(JapaneseSong, "Normal"));
        var restarted = Offline(NewId());
        restarted.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(2, RankingSyncLog.Load(LogKey).Count);
        Assert.IsFalse(PlayerPrefs.HasKey(PendingKey));
    }

    // 演奏中に束で届いて待っていた回・足し直し待ちの回も、曲が終わって順位を決める前に、その曲×難易度の分だけ先に足す
    // (結果画面の順位に入る。ほかの曲の分は待ったまま)。
    [Test]
    public void TheWaitingPlaysOfTheFinishedChartAreAddedBeforeItsRankIsDecided()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        PlayerPrefs.SetString(DailyRankingStore.Key(Song, "Normal"), "not json");
        var waitingPlay = Play(score: 91000, station: "B");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", waitingPlay), From, t0));
        Assert.AreEqual(1, sync.PendingCount, "表が壊れていて足し直し待ち");
        PlayerPrefs.DeleteKey(DailyRankingStore.Key(Song, "Normal")); // 表を直す(消す)
        sync.BusyForTest = true; // 曲の演奏中
        var bundled = new[] { Play(score: 95000, station: "B"), Play(score: 60000, station: "B") };
        var elsewhere = new[] { Play(score: 99000, song: JapaneseSong, station: "B"), Play(score: 98000, song: JapaneseSong, station: "B") };
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", bundled), From, t0 + 1), "束は演奏中は待つ");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", elsewhere), From, t0 + 2));
        // 曲が終わった(GameSession.RecordCompletedDailyRanking の前の RankingSync.ApplyWaitingFor と同じ)
        Assert.AreEqual(0, sync.ApplyChart(Fes, Song, "Normal", t0 + 3), "ほかの名前空間の表の待ちは取らない");
        Assert.AreEqual(3, sync.ApplyChart("", Song, "Normal", t0 + 3));
        Assert.AreEqual(0, sync.PendingCount);
        Assert.AreEqual(2, sync.QueuedCount, "ほかの曲の束は待ったまま");
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key(JapaneseSong, "Normal")));
        var local = DailyRankingStore.Record(Song, "Normal", NewId(), 80000, PlayedAt);
        Assert.AreEqual(3, local.Rank, "届いていた 95000・91000 を入れた順位");
        Assert.AreEqual(4, local.TotalPlays);
    }

    // 束で待っている回に、同じ回の新しい1回の知らせが届いたら、新しい回の待ちへ移す(演奏中でも足す。二重には足さない)。
    [Test]
    public void ANewNoticeForAPlayWaitingInABundleMovesItAhead()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.BusyForTest = true;
        string other = NewId();
        var first = Play(score: 70000, station: "B");
        var second = Play(score: 71000, station: "B");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", first, second), From, t0), "束は演奏中は待つ");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", second), From, t0 + 1), "同じ回の新しい1回の知らせで、すぐ足す");
        Assert.AreEqual(1, sync.Count);
        Assert.AreEqual(second.id, DailyTable(Song, "Normal").entries.Single().runId);
        sync.BusyForTest = false;
        sync.Tick(t0 + 2);
        Assert.AreEqual(2, sync.Count, "束の残りはあとで足す");
        Assert.AreEqual(2, DailyTable(Song, "Normal").entries.Count, "移した回を二重に足さない");
        Assert.AreEqual(2, HighScoreStore.Load(Song, "Normal").entries.Count);
        Assert.AreEqual(0, sync.ConflictingPlays);
    }

    // 曲の演奏中は、新しい1回だけを足し、束の併合・ディスクへの保存は止める(結果・選曲・タイトルの間にまとめて行う)。
    [Test]
    public void DuringASongOnlyNewSinglePlaysAreAddedAndTheRestWaits()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.BusyForTest = true;
        string other = NewId();
        var single = Play(score: 61000, station: "B");
        var bundle = new[] { Play(score: 62000, station: "B"), Play(score: 63000, station: "B") };
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", single), From, t0), "新しい1回は演奏中でもすぐ足す");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", bundle), From, t0 + 1), "束は待つ");
        Assert.AreEqual(2, sync.QueuedCount);
        sync.Tick(t0 + 2);
        Assert.AreEqual(2, sync.QueuedCount, "演奏中は束を足さない");
        Assert.IsTrue(sync.SaveWaiting, "演奏中はディスクへ保存しない");
        Assert.AreEqual(0, RankingSyncLog.Load(LogKey).Count);
        sync.BusyForTest = false; // 結果画面へ
        sync.Tick(t0 + 3);
        Assert.AreEqual(0, sync.QueuedCount);
        Assert.AreEqual(3, sync.Count);
        Assert.IsFalse(sync.SaveWaiting);
        Assert.AreEqual(3, RankingSyncLog.Load(LogKey).Count);
    }

    // 起動時に、ログの各回が表(通算・上位5件)にあるかを確かめ、無ければ足し直す(ある回は二重に足さない)。
    [Test]
    public void StartupPutsLoggedPlaysBackIntoTheTables()
    {
        var saved = new RankingSyncLog();
        var normal = Play(score: 70000);
        var hard = Play(score: 80000, difficulty: "Hard");
        var present = Play(score: 60000);
        Assert.IsTrue(saved.Add(normal) && saved.Add(hard) && saved.Add(present));
        saved.Save(LogKey);
        DailyRankingStore.Record(Song, "Normal", present.id, present.score, PlayedAt);
        HighScoreStore.Record(Song, "Normal", RankingSyncLog.ToHighScoreEntry(present), out _);

        var sync = Offline(NewId());
        Assert.AreEqual(3, sync.QueuedCount, "起動時の照合を待っている");
        sync.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(0, sync.QueuedCount);
        Assert.AreEqual(3, sync.Count, "ログはそのまま");
        Assert.AreEqual(0, sync.ReceivedPlays, "相手から受け取った回ではない");
        CollectionAssert.AreEquivalent(new[] { present.id, normal.id }, DailyTable(Song, "Normal").entries.Select(e => e.runId));
        CollectionAssert.AreEqual(new[] { normal.id, present.id }, HighScoreStore.Load(Song, "Normal").entries.Select(e => e.runId));
        Assert.AreEqual(hard.id, DailyTable(Song, "Hard").entries.Single().runId);
        Assert.AreEqual(hard.id, HighScoreStore.Load(Song, "Hard").entries.Single().runId);
    }

    // 「つながりました」は、相手の要約にこちらの機械 ID が入っていた(双方向)ときだけ。届くのにこちらが聞こえていなければ片方向と知らせる。
    [Test]
    public void TheOperatorIsToldAboutTwoWayOneWayAndLostPeersOnce()
    {
        string self = NewId(), peer = NewId();
        var sync = Offline(self);
        double t0 = Time.realtimeSinceStartupAsDouble;
        string oneWay = RankingSyncLog.BuildDigest(peer, "B", 0, EmptyHash);
        string twoWay = RankingSyncLog.BuildDigest(peer, "B", 0, EmptyHash, new[] { self });
        sync.ReceiveForTest(oneWay, From, t0 + 1);
        Assert.AreEqual(0, sync.NoticeCount, "届いただけでは、まだ「つながりました」と言わない");
        Assert.AreEqual(1, sync.PeerCount);
        Assert.AreEqual(0, sync.ConnectedCount);
        Assert.AreEqual(t0 + 1, sync.LastPeerMessageAt);
        sync.Tick(t0 + 10);
        Assert.AreEqual(0, sync.NoticeCount);
        sync.Tick(t0 + 17);
        Assert.AreEqual("ランキング同期: 台Bからは届いていますが、こちらから届いていません(ファイアウォール)", sync.LastNotice);
        Assert.AreEqual(1, sync.NoticeCount);
        sync.Tick(t0 + 30);
        Assert.AreEqual(1, sync.NoticeCount, "片方向の知らせは1回");
        sync.ReceiveForTest(twoWay, From, t0 + 31);
        Assert.AreEqual("ランキング同期: 台Bとつながりました", sync.LastNotice);
        Assert.AreEqual(1, sync.ConnectedCount);
        int shown = sync.NoticeCount;
        sync.ReceiveForTest(twoWay, From, t0 + 35);
        sync.Tick(t0 + 94); // 最後に届いてから 59 秒
        Assert.AreEqual(shown, sync.NoticeCount, "見えている間は何も出さない");
        sync.Tick(t0 + 96); // 61 秒
        Assert.AreEqual("ランキング同期: 台Bと途切れています(記録はあとで合わせます)", sync.LastNotice);
        Assert.AreEqual(0, sync.PeerCount);
        sync.Tick(t0 + 300);
        Assert.AreEqual(shown + 1, sync.NoticeCount, "途切れの知らせは1回");
        sync.ReceiveForTest(twoWay, From, t0 + 301);
        Assert.AreEqual("ランキング同期: 台Bとつながりました", sync.LastNotice, "戻ったら知らせる");
        // 相手がこちらを聞こえなくなった(相手の要約からこちらが消えた)まま 60 秒
        sync.ReceiveForTest(oneWay, From, t0 + 330);
        sync.ReceiveForTest(oneWay, From, t0 + 360);
        sync.Tick(t0 + 362);
        Assert.AreEqual(RankingSync.OneWayNotice("B"), sync.LastNotice);
        Assert.AreEqual(0, sync.ConnectedCount);
        Assert.AreEqual("ランキング同期: もう1台とつながりました", RankingSync.ConnectedNotice(""), "台名が無いとき");
    }

    // 「ポートを開けない」が直ったら、見つからない知らせの30秒はそこから数える(警告したかどうかとは別の状態)。
    [Test]
    public void TheNoPeerNoticeCountsFromWhenThePortCouldBeOpened()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.BindStateForTest(true, t0);
        sync.Tick(t0 + 40);
        Assert.AreEqual(0, sync.NoticeCount, "ポートを開けない間は、見つからないとは言わない");
        sync.BindStateForTest(false, t0 + 40); // あとで開けた
        sync.Tick(t0 + 69);
        Assert.AreEqual(0, sync.NoticeCount);
        sync.Tick(t0 + 71);
        Assert.AreEqual(RankingSync.NoPeerNotice, sync.LastNotice, "開けてから30秒で知らせる");
    }

    [Test]
    public void TheOperatorIsToldOnceWhenNoPeerShowsUpIn30Seconds()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.Tick(t0 + 29);
        Assert.AreEqual(0, sync.NoticeCount);
        sync.Tick(t0 + 31);
        Assert.AreEqual("ランキング同期: 相手の台が見つかりません(同じネットワークか・Windows はファイアウォールで UDP 5017 の受信を許可しているか・-rankingPeer)",
            sync.LastNotice);
        sync.Tick(t0 + 90);
        Assert.AreEqual(1, sync.NoticeCount, "1回だけ");
    }

    // ---- GameSession からの受け渡し ----

    [Test]
    public void AFinishedRunIsHandedToTheSyncOnlyOnce()
    {
        var saved = SaveGameSession();
        var received = new List<RankingPlay>();
        Action<RankingPlay> handler = received.Add;
        RankingSync.LocalPlayRecorded += handler;
        try
        {
            GameSession.SelectedSongId = Song;
            GameSession.SelectedDifficulty = "Normal";
            GameSession.IsCalibrationMode = false;
            GameSession.ResetResult();
            GameSession.FinalPerfect = 10;
            GameSession.FinalGreat = 2;
            GameSession.FinalMiss = 1;
            GameSession.FinalCutCount = 12;
            GameSession.FinalScore = 91234;
            GameSession.RecordCompletedDailyRanking();
            GameSession.RecordCompletedDailyRanking();
            Assert.AreEqual(1, received.Count, "同じ回は1回だけ渡す");
            var play = received[0];
            Assert.AreEqual(GameSession.AchievementRunId, play.id);
            Assert.AreEqual(Song, play.song);
            Assert.AreEqual("Normal", play.diff);
            Assert.AreEqual(91234, play.score);
            Assert.AreEqual("S+", play.rank);
            Assert.AreEqual(GameSession.FinalAccuracy, play.acc);
            Assert.AreEqual(DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), play.date);
            Assert.AreEqual(PhoneSaberStation.Read(), play.st);
            Assert.IsTrue(RankingSyncLog.IsValid(play), "そのまま送れる形");
            // 結果画面で残す HI-SCORE は、送った回と同じ runId・時刻(2台で同じ並びにするため)
            GameSession.RecordCompletedHighScore();
            var entry = HighScoreStore.Load(Song, "Normal").entries.Single();
            Assert.AreEqual(play.id, entry.runId);
            Assert.AreEqual(play.at, entry.at);
            // 記録しない回(判定調整・1回も切っていない回)は渡さない
            GameSession.ResetResult();
            GameSession.IsCalibrationMode = true;
            GameSession.FinalPerfect = 5;
            GameSession.RecordCompletedDailyRanking();
            GameSession.IsCalibrationMode = false;
            GameSession.ResetResult();
            GameSession.FinalMiss = 5;
            GameSession.RecordCompletedDailyRanking();
            Assert.AreEqual(1, received.Count);
        }
        finally
        {
            RankingSync.LocalPlayRecorded -= handler;
            RestoreGameSession(saved);
        }
    }

    // 送る記録の時刻は、曲が終わったとき(終わりの演出の前、GameSession.MarkRunEnded)。2台で「となりと同じ曲」を比べる実績と同じ時刻。
    // 最初の1回だけ覚え、次の回(ResetResult・回の ID が変わった)には持ち越さない。
    [Test]
    public void TheSentTimeIsWhenTheSongEndedNotWhenTheEndingEffectsFinished()
    {
        var saved = SaveGameSession();
        var received = new List<RankingPlay>();
        Action<RankingPlay> handler = received.Add;
        RankingSync.LocalPlayRecorded += handler;
        try
        {
            GameSession.SelectedSongId = Song;
            GameSession.SelectedDifficulty = "Normal";
            GameSession.IsCalibrationMode = false;
            GameSession.ResetResult();
            Assert.IsNull(GameSession.RunEndedAt);
            var ended = DateTime.Now.AddSeconds(-7);
            GameSession.MarkRunEnded(ended);
            GameSession.MarkRunEnded(DateTime.Now);
            Assert.AreEqual(ended, GameSession.RunEndedAt, "最初の1回だけ");
            GameSession.FinalPerfect = 10;
            GameSession.FinalCutCount = 10;
            GameSession.FinalScore = 81234;
            GameSession.RecordCompletedDailyRanking();
            Assert.AreEqual(1, received.Count);
            Assert.AreEqual(new DateTimeOffset(ended).ToUnixTimeMilliseconds(), received[0].at);
            GameSession.ResetResult();
            Assert.IsNull(GameSession.RunEndedAt, "次の回には持ち越さない");
            GameSession.MarkRunEnded(ended);
            GameSession.AchievementRunId = "another-run";
            Assert.IsNull(GameSession.RunEndedAt, "別の回の時刻は返さない");
        }
        finally
        {
            RankingSync.LocalPlayRecorded -= handler;
            RestoreGameSession(saved);
        }
    }

    // 結果画面の HI-SCORE も、壊れた上位5件の表を上書きしない(その回は RankingSync の足し直し待ちへ)。同じ回は二重にしない。
    [Test]
    public void RecordingAHighScoreNeverOverwritesABrokenTable()
    {
        PlayerPrefs.SetString(HighScoreStore.Key(Song, "Normal"), "{ broken");
        Assert.AreEqual(-1, HighScoreStore.Record(Song, "Normal", Entry(900, "run-broken-1", 10), out _));
        Assert.AreEqual(RankingMergeResult.Failed, HighScoreStore.RecordRun(Song, "Normal", Entry(900, "run-broken-1", 10), out _, out _));
        Assert.AreEqual("{ broken", PlayerPrefs.GetString(HighScoreStore.Key(Song, "Normal")));
        var saved = SaveGameSession();
        try
        {
            GameSession.SelectedSongId = Song;
            GameSession.SelectedDifficulty = "Normal";
            GameSession.IsCalibrationMode = false;
            GameSession.ResetResult();
            GameSession.FinalPerfect = 3;
            GameSession.FinalCutCount = 3;
            GameSession.FinalScore = 50000;
            GameSession.RecordCompletedHighScore();
            Assert.AreEqual("{ broken", PlayerPrefs.GetString(HighScoreStore.Key(Song, "Normal")), "来場者が遊んでも上書きしない");
        }
        finally { RestoreGameSession(saved); }
        // 同じ回は二重にしない(もうある位置を返す)
        PlayerPrefs.DeleteKey(HighScoreStore.Key(Song, "Normal"));
        Assert.AreEqual(0, HighScoreStore.Record(Song, "Normal", Entry(900, "run-same-2", 10), out _));
        Assert.AreEqual(0, HighScoreStore.Record(Song, "Normal", Entry(900, "run-same-2", 10), out _));
        Assert.AreEqual(RankingMergeResult.AlreadyThere, HighScoreStore.RecordRun(Song, "Normal", Entry(900, "run-same-2", 10), out _, out _));
        Assert.AreEqual(1, HighScoreStore.Load(Song, "Normal").entries.Count);
    }

    // ---- 送り先と、動かすかどうか ----

    [Test]
    public void PeersBroadcastAddressesAndTheSwitchAreWorkedOutFromText()
    {
        var peers = RankingSyncLog.ParsePeers(" 192.168.137.5, 10.0.0.2:5018,bad,1,300.1.1.1, 192.168.137.5 ;10.0.0.3:99999", 5017);
        CollectionAssert.AreEqual(new[] { "192.168.137.5:5017", "10.0.0.2:5018" }, peers.Select(p => p.ToString()));
        Assert.AreEqual(0, RankingSyncLog.ParsePeers(null, 5017).Count);

        Assert.AreEqual(IPAddress.Parse("192.168.137.255"),
            RankingSyncLog.DirectedBroadcast(IPAddress.Parse("192.168.137.5"), IPAddress.Parse("255.255.255.0"), out bool guessedWithMask));
        Assert.IsFalse(guessedWithMask, "マスクがあれば推測ではない");
        Assert.AreEqual(IPAddress.Parse("10.1.255.255"),
            RankingSyncLog.DirectedBroadcast(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.255.0.0")));
        Assert.AreEqual(IPAddress.Parse("192.168.1.255"), RankingSyncLog.DirectedBroadcast(IPAddress.Parse("192.168.1.20"), null, out bool guessed),
            "マスクが取れないときは /24 と推測");
        Assert.IsTrue(guessed, "推測した宛先だと分かる");
        Assert.AreEqual(IPAddress.Parse("192.168.1.255"), RankingSyncLog.DirectedBroadcast(IPAddress.Parse("192.168.1.20"), IPAddress.Any),
            "0.0.0.0 も /24");
        Assert.IsNull(RankingSyncLog.DirectedBroadcast(IPAddress.Loopback, IPAddress.Parse("255.0.0.0")));
        Assert.IsNull(RankingSyncLog.DirectedBroadcast(IPAddress.Parse("10.8.0.2"), IPAddress.Parse("255.255.255.255")), "1対1のつながり");
        Assert.IsNull(RankingSyncLog.DirectedBroadcast(IPAddress.IPv6Loopback, null));

        Assert.AreEqual("10.0.0.9,192.168.0.7,192.168.0.8",
            RankingSync.ResolvePeers(new[] { "app", "-rankingPeer", "192.168.0.7", "-rankingPeer", "192.168.0.8" }, "10.0.0.9"));
        Assert.AreEqual("", RankingSync.ResolvePeers(new[] { "app", "-rankingPeer" }, ""));

        // ビルドでは動かす。Editor ではメニューで入れたときだけ。テストを回す起動・-rankingSync off・試験の起動では動かさない。
        Assert.IsTrue(RankingSync.EnabledHere(new[] { "app" }, false, false));
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app" }, true, false));
        Assert.IsTrue(RankingSync.EnabledHere(new[] { "app" }, true, true));
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app", "-batchmode", "-runTests", "-testPlatform", "PlayMode" }, true, true));
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app", "-runTests" }, false, false));
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app", "-rankingSync", "off" }, false, false));
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app", "-phonesaberSoak", "5" }, false, false), "試験の起動では動かさない");
        Assert.IsFalse(RankingSync.EnabledHere(new[] { "app", "-phonesaberSmoke" }, false, false));
    }

    // ---- 版 2(2026-10-29): 名前空間(ns)・実績(ach)・ほかの版 ----

    static RankingPlay FesPlay(int score = 50000, string station = "B", params string[] achievements) =>
        RankingSyncLog.NewPlay(NewId(), Song, "Normal", score, PlayRankHelper.Label(PlayRankHelper.FromScore(score)), .8f, PlayedAt, station,
            Fes, achievements);

    [Test]
    public void ARecordCarriesItsNamespaceAndAchievementsThroughTheLogAndTheBundles()
    {
        RankingSyncLog.ResetWarningsForTest();
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"^RankingSync: この回の実績のうち 3 個を同期の記録に入れられませんでした"));
        var play = RankingSyncLog.NewPlay(NewId(), Song, "Hard", 91000, "S+", .95f, PlayedAt, "A", Fes,
            new[] { "hard_clear", "gold_all", "hard_clear", "bad id!", "", null, "full_combo" });
        Assert.AreEqual(Fes, play.ns);
        CollectionAssert.AreEqual(new[] { "hard_clear", "gold_all", "full_combo" }, play.ach, "形の正しい ID だけを、重ねずに取った順で");
        Assert.IsTrue(RankingSyncLog.IsValid(play));
        var plain = Play();
        Assert.AreEqual("", plain.ns, "名前空間を渡さなければふだん");
        Assert.AreEqual(0, plain.ach.Count);

        var log = new RankingSyncLog();
        Assert.IsTrue(log.Add(play) && log.Add(plain));
        log.Save(LogKey);
        StringAssert.StartsWith("{\"v\":2,", PlayerPrefs.GetString(LogKey), "保存の形は版 2");
        var loaded = RankingSyncLog.Load(LogKey);
        Assert.IsTrue(RankingSyncLog.SameContent(play, loaded.Find(play.id)));
        Assert.AreEqual(Fes, loaded.Find(play.id).ns);
        CollectionAssert.AreEqual(play.ach, loaded.Find(play.id).ach);
        Assert.AreEqual("", loaded.Find(plain.id).ns);
        Assert.AreEqual(log.HashText, loaded.HashText);

        // 束と、受け取った1通の読み方でもそのまま
        Assert.IsTrue(RankingSyncLog.TryParse(log.Bundles(NewId(), "A")[0].Text, out var message));
        var received = message.plays.Single(p => p != null && p.id == play.id);
        Assert.IsTrue(RankingSyncLog.SameContent(play, received));
        CollectionAssert.AreEqual(play.ach, received.ach);
        // 名前空間・実績が違えば、同じ回の違う中身。表をまとめる鍵は名前空間で分かれる
        var otherNs = RankingSyncLog.Copy(play);
        otherNs.ns = "";
        var otherAch = RankingSyncLog.Copy(play);
        otherAch.ach.RemoveAt(0);
        Assert.IsFalse(RankingSyncLog.SameContent(play, otherNs));
        Assert.IsFalse(RankingSyncLog.SameContent(play, otherAch));
        Assert.AreEqual(3, play.ach.Count, "写しを変えても元は変わらない");
        Assert.AreNotEqual(RankingSyncLog.ChartKey(play), RankingSyncLog.ChartKey(otherNs));
    }

    // 前の版(1)が残したログ(ns・ach の欄なし)は、ふだんの表・実績なしの回として読み、起動時の照合でふだんの表へ入れる。
    [Test]
    public void AVersionOneSavedLogIsReadAsOrdinaryPlays()
    {
        var play = Play(score: 70000);
        string v1Play = "{\"id\":\"" + play.id + "\",\"song\":\"" + play.song + "\",\"diff\":\"Normal\",\"score\":70000,\"rank\":\"" + play.rank +
                        "\",\"acc\":0.8,\"date\":\"" + play.date + "\",\"st\":\"A\",\"at\":" + play.at.ToString(CultureInfo.InvariantCulture) + "}";
        string v1Log = "{\"v\":1,\"plays\":[" + v1Play + "]}";
        var log = RankingSyncLog.FromJson(v1Log, out bool broken);
        Assert.IsFalse(broken);
        Assert.AreEqual("", log.Find(play.id).ns);
        Assert.AreEqual(0, log.Find(play.id).ach.Count);
        PlayerPrefs.SetString(LogKey, v1Log);
        PlayerPrefs.SetString(PendingKey, "{\"v\":1,\"plays\":[]}");
        var sync = Offline(NewId());
        Assert.AreEqual(1, sync.Count, "前の版のログをそのまま読む");
        sync.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(play.id, DailyTable(Song, "Normal").entries.Single().runId);
        Assert.AreEqual(play.id, HighScoreStore.Load("", Song, "Normal").entries.Single().runId);
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key(Fes, Song, "Normal")), "本番の表には入れない");
        Assert.IsFalse(PlayerPrefs.HasKey(HighScoreStore.Key(Fes, Song, "Normal")));
    }

    [Test]
    public void BrokenNamespacesAndAchievementsMakeARecordInvalid()
    {
        var cases = new (string name, Action<RankingPlay> breakIt)[]
        {
            ("大文字の名前空間", p => p.ns = "FES"),
            ("記号の名前空間", p => p.ns = "fes_1"),
            ("空白の名前空間", p => p.ns = " "),
            ("長すぎる名前空間", p => p.ns = new string('f', OperatorMode.MaxNamespaceLength + 1)),
            ("空の実績 ID", p => p.ach = new List<string> { "" }),
            ("null の実績 ID", p => p.ach = new List<string> { null }),
            ("空白の入った実績 ID", p => p.ach = new List<string> { "hard clear" }),
            ("長すぎる実績 ID", p => p.ach = new List<string> { new string('a', AchievementStore.MaxIdLength + 1) }),
            ("重なった実績", p => p.ach = new List<string> { "hard_clear", "gold_all", "hard_clear" }),
            ("多すぎる実績", p => p.ach = Enumerable.Range(0, RankingSyncLog.MaxAchievementsPerPlay + 1).Select(i => "a" + i).ToList()),
        };
        foreach (var (name, breakIt) in cases)
        {
            var play = Play();
            breakIt(play);
            Assert.IsFalse(RankingSyncLog.IsValid(play), name);
            Assert.IsFalse(new RankingSyncLog().Add(play), name);
        }
        var ok = Play();
        ok.ns = Fes;
        ok.ach = Enumerable.Range(0, RankingSyncLog.MaxAchievementsPerPlay).Select(i => "a" + i).ToList();
        Assert.IsTrue(RankingSyncLog.IsValid(ok), "上限ちょうどは通る");
        ok.ach = null;
        ok.ns = null;
        Assert.IsTrue(RankingSyncLog.IsValid(ok), "欄が無い(null)のは、ふだんの表・実績なし");
        Assert.AreEqual("", RankingSyncLog.Copy(ok).ns);
        Assert.AreEqual(0, RankingSyncLog.Copy(ok).ach.Count);
    }

    // 実績の一覧(44個、2026-10-29 b1 の catalog.md)。
    static readonly string[] CatalogIds =
    {
        "ap", "senbon", "quiz", "bullseye", "fubuki", "mirror", "shingan", "memory", "matador", "clock", "hardsplus", "secret_s", "wind",
        "overtake", "dark", "ichiban", "streak50", "fc", "kiriban", "zorome", "splus", "combo100", "streak20", "top", "secret", "pioneer",
        "jackpot", "comeback", "duet", "school", "ranks", "hard", "chorus", "quick", "combo50", "goldall", "dual", "arrows", "rainbow",
        "firstcut", "finale", "xcut", "practice", "clear",
    };

    // 長さの上限(12 字)の ID を n 個。
    static List<string> LongIds(int count) => Enumerable.Range(0, count)
        .Select(i => i.ToString("D2", CultureInfo.InvariantCulture) + new string('x', AchievementStore.MaxIdLength - 2)).ToList();

    // 1回の記録に、どの曲でも(長い曲 ID・日本語の曲 ID・16 字の台名)、一覧の44個も、12 字の ID の44個・48個(上限)も、外さずに入り、1通で送れる。
    [Test]
    public void AllFortyFourAchievementsFitInOneDatagramForEverySong()
    {
        Assert.AreEqual(44, CatalogIds.Length);
        Assert.AreEqual(48, RankingSyncLog.MaxAchievementsPerPlay);
        Assert.AreEqual(12, AchievementStore.MaxIdLength);
        var songs = System.IO.Directory.GetDirectories(System.IO.Path.Combine(Application.streamingAssetsPath, "Songs"))
            .Select(System.IO.Path.GetFileName).ToList();
        Assert.GreaterOrEqual(songs.Count, 10, "曲のフォルダを読めた");
        CollectionAssert.Contains(songs, "Phobia PARTⅠ");
        var warnings = new List<string>();
        Application.LogCallback collect = (condition, stack, type) => { if (type == LogType.Warning) warnings.Add(condition); };
        RankingSyncLog.ResetWarningsForTest();
        Application.logMessageReceived += collect;
        try
        {
            foreach (string song in songs)
                foreach (var ids in new[] { CatalogIds.ToList(), LongIds(44), LongIds(RankingSyncLog.MaxAchievementsPerPlay) })
                {
                    var play = RankingSyncLog.NewPlay(NewId(), song, "Normal", ScoreManager.MaxScore, "S+", 1f / 3f, PlayedAt,
                        new string('B', RankingSyncLog.MaxStationLength), Fes, ids);
                    CollectionAssert.AreEqual(ids, play.ach, song + " の " + ids.Count + " 個を外さずに入れる");
                    Assert.IsTrue(RankingSyncLog.IsValid(play), song);
                    var log = new RankingSyncLog();
                    Assert.IsTrue(log.Add(play), song);
                    // 束の頭が一番大きな形(64 字の差出人・16 字の台名)でも1通
                    var bundles = log.Bundles(new string('f', RankingSyncLog.MaxIdLength), new string('A', RankingSyncLog.MaxStationLength));
                    Assert.AreEqual(1, bundles.Count, song);
                    Assert.LessOrEqual(Encoding.UTF8.GetByteCount(bundles[0].Text), RankingSyncLog.MaxDatagramBytes, song);
                    Assert.IsTrue(RankingSyncLog.TryParse(bundles[0].Text, out var message), song);
                    CollectionAssert.AreEqual(ids, message.plays.Single().ach, song);
                }
        }
        finally { Application.logMessageReceived -= collect; }
        Assert.IsFalse(warnings.Any(w => w.StartsWith("RankingSync: この回の実績のうち", StringComparison.Ordinal)), "外さないので警告しない");
    }

    // 入れられない実績(形が違う・上限を超える・1通に収まらない)は黙って外さず、起動の間に1回だけ警告する。外しても送れる回にする。
    [Test]
    public void AchievementsThatCannotBeKeptAreReportedOnce()
    {
        var droppedWarning = new System.Text.RegularExpressions.Regex(@"^RankingSync: この回の実績のうち \d+ 個を同期の記録に入れられませんでした");
        var warnings = new List<string>();
        Application.LogCallback collect = (condition, stack, type) =>
        {
            if (type == LogType.Warning && droppedWarning.IsMatch(condition)) warnings.Add(condition);
        };
        RankingSyncLog.ResetWarningsForTest();
        Application.logMessageReceived += collect;
        try
        {
            // 検査を通る一番大きな形(64 字の回の ID・64 字の日本語の曲・16 字の難易度と台名・8 字の名前空間)は、48個は入らない
            LogAssert.Expect(LogType.Warning, droppedWarning);
            var largest = RankingSyncLog.NewPlay(new string('a', RankingSyncLog.MaxIdLength), new string('揺', RankingSyncLog.MaxSongLength),
                new string('d', RankingSyncLog.MaxDifficultyLength), ScoreManager.MaxScore, "ABCDEFGH", 1f / 3f, PlayedAt,
                new string('B', RankingSyncLog.MaxStationLength), new string('f', OperatorMode.MaxNamespaceLength), LongIds(48));
            Assert.Greater(largest.ach.Count, 0);
            Assert.Less(largest.ach.Count, 48);
            CollectionAssert.AreEqual(LongIds(largest.ach.Count), largest.ach, "前から順に残す");
            Assert.IsTrue(RankingSyncLog.IsValid(largest), "外したあとは送れる形");
            var log = new RankingSyncLog();
            Assert.IsTrue(log.Add(largest));
            Assert.AreEqual(1, log.Bundles(new string('f', RankingSyncLog.MaxIdLength), new string('A', RankingSyncLog.MaxStationLength)).Count);
            // 上限を超える数・形の違う ID も入れない(警告は起動の間に1回だけ)
            var many = Enumerable.Range(0, RankingSyncLog.MaxAchievementsPerPlay + 2).Select(i => "a" + i).ToList();
            var capped = RankingSyncLog.NewPlay(NewId(), Song, "Normal", 90000, "S+", .9f, PlayedAt, "A", Fes, many);
            CollectionAssert.AreEqual(many.Take(RankingSyncLog.MaxAchievementsPerPlay), capped.ach);
            var shaped = RankingSyncLog.NewPlay(NewId(), Song, "Normal", 90000, "S+", .9f, PlayedAt, "A", Fes, new[] { "ok", "bad id!", "ok" });
            CollectionAssert.AreEqual(new[] { "ok" }, shaped.ach, "重なりは1つに、形の違う ID は入れない");
        }
        finally { Application.logMessageReceived -= collect; }
        Assert.AreEqual(1, warnings.Count, "警告は1回だけ");
    }

    // 相手の回は、その回の名前空間の表へ足す(ふだんの表と本番の表は混ざらない)。ログは1つで、要約は全部の回で。
    [Test]
    public void PlaysAreMergedIntoTheTablesOfTheirNamespace()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        var festival = FesPlay(88000);
        var ordinary = Play(score: 77000, station: "B");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", festival), From, t0));
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", ordinary), From, t0 + 1));
        Assert.AreEqual(2, sync.Count, "ログは1つ");
        Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(Fes, Song, "Normal"), ""), "2000-01-01", out var fesDaily));
        Assert.AreEqual(festival.id, fesDaily.entries.Single().runId);
        Assert.AreEqual(ordinary.id, DailyTable(Song, "Normal").entries.Single().runId);
        Assert.AreEqual(festival.id, HighScoreStore.Load(Fes, Song, "Normal").entries.Single().runId);
        Assert.AreEqual(ordinary.id, HighScoreStore.Load("", Song, "Normal").entries.Single().runId);
        var both = new RankingSyncLog();
        Assert.IsTrue(both.Add(ordinary) && both.Add(festival));
        Assert.AreEqual(both.HashText, sync.Hash, "要約は名前空間に依らず全部の回で");
        // 名前空間を渡さない表示(選曲の TOP 5 など)は、今の運営モードの表
        OperatorMode.OverrideForTest(true);
        Assert.AreEqual(festival.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        OperatorMode.OverrideForTest(false);
        Assert.AreEqual(ordinary.id, HighScoreStore.Load(Song, "Normal").entries.Single().runId);
        // 起動し直しても(照合)、それぞれの表のまま
        sync.Tick(t0 + 2);
        sync.Shutdown();
        var restarted = Offline(NewId());
        restarted.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(1, HighScoreStore.Load(Fes, Song, "Normal").entries.Count);
        Assert.AreEqual(1, HighScoreStore.Load("", Song, "Normal").entries.Count);
        Assert.AreEqual(Fes, RankingSyncLog.Load(LogKey).Find(festival.id).ns);
    }

    // 曲が終わって順位を決める前に先に足すのは、その回の名前空間の表で待っている回だけ。
    [Test]
    public void TheFinishedChartOnlyTakesWaitingPlaysOfItsNamespace()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        sync.BusyForTest = true;
        string other = NewId();
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", FesPlay(95000), FesPlay(60000)), From, t0), "束は演奏中は待つ");
        Assert.AreEqual(0, sync.ReceiveForTest(PlaysMessage(other, "B", Play(score: 99000, station: "B"), Play(score: 98000, station: "B")), From, t0 + 1));
        Assert.AreEqual(2, sync.ApplyChart(Fes, Song, "Normal", t0 + 2));
        Assert.AreEqual(2, sync.QueuedCount, "ふだんの表の回は待ったまま");
        var local = DailyRankingStore.Record(Fes, Song, "Normal", NewId(), 80000, PlayedAt);
        Assert.AreEqual(2, local.Rank, "本番の表の 95000 だけが上");
        Assert.AreEqual(3, local.TotalPlays);
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key("", Song, "Normal")));
    }

    // 相手の台の回の実績(ach)は、この台の数に足す(同じ回は二重に数えない。ポップアップは出さない)。名前空間ごとに別に数える。
    [Test]
    public void AchievementsOfPlaysFromTheOtherMachineAreCounted()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, NewId(), "hard_clear", "A", 1000), "この台で1人目");
        var theirs = FesPlay(91000, "B", "hard_clear", "gold_all");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", theirs), From, t0));
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"), "相手の台の回を足して2人");
        Assert.AreEqual(1, AchievementStore.Count(Fes, "gold_all"));
        Assert.AreEqual(0, AchievementStore.Count("", "hard_clear"), "ふだんの数とは別");
        CollectionAssert.AreEqual(new[] { "hard_clear", "gold_all" }, AchievementStore.UnlockedIn(Fes, theirs.id));
        // 同じ回が束で届き直しても、二重に数えない
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(other, "B", theirs, Play(station: "B")), From, t0 + 1), "新しい回だけ足す");
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
        // PlayerPrefs へは FlushSaves がまとめて書く(読み直しても同じ数)
        Assert.IsTrue(AchievementStore.NeedsWrite);
        Assert.IsTrue(sync.SaveWaiting);
        sync.Tick(t0 + 2);
        Assert.IsFalse(AchievementStore.NeedsWrite);
        Assert.IsFalse(sync.SaveWaiting);
        AchievementStore.UseKeyForTest(AchievementKey); // 読み直す
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(1, AchievementStore.Count(Fes, "gold_all"));
    }

    // ほかの版の相手のメッセージは合流せず、運営に1回だけ知らせる(古い版・新しい版)。相手が見つからないの知らせは重ねない。
    [Test]
    public void APeerOnAnotherVersionIsToldOnceAndNotMerged()
    {
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        string other = NewId();
        string current = PlaysMessage(other, "B", Play(station: "B"));
        string old = "BTSRANK1 " + current.Substring(RankingSyncLog.Prefix.Length).Replace("\"v\":2", "\"v\":1");
        Assert.IsTrue(RankingSyncLog.IsOtherVersion(old, out int oldVersion));
        Assert.AreEqual(1, oldVersion);
        Assert.IsFalse(RankingSyncLog.IsOtherVersion(current, out _), "今の版");
        Assert.IsFalse(RankingSyncLog.IsOtherVersion("BTSRANK 1", out _));
        Assert.IsFalse(RankingSyncLog.IsOtherVersion("BTSRANKX {}", out _));
        Assert.IsFalse(RankingSyncLog.IsOtherVersion("PHONESABER_DISCOVER 1", out _));
        Assert.IsFalse(RankingSyncLog.IsOtherVersion((string)null, out _));
        byte[] oldBytes = Encoding.UTF8.GetBytes(old);
        Assert.IsTrue(RankingSyncLog.IsOtherVersion(oldBytes, oldBytes.Length, out int fromBytes));
        Assert.AreEqual(1, fromBytes);
        byte[] currentBytes = Encoding.UTF8.GetBytes(current);
        Assert.IsFalse(RankingSyncLog.IsOtherVersion(currentBytes, currentBytes.Length, out _));
        Assert.IsFalse(RankingSyncLog.TryDecode(oldBytes, oldBytes.Length, out _), "受信スレッドのふるいも通さない");

        Assert.AreEqual(0, sync.ReceiveForTest(old, From, t0));
        Assert.AreEqual(0, sync.Count, "合流しない");
        Assert.AreEqual(0, sync.PeerCount, "相手として数えない");
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key(Song, "Normal")));
        Assert.AreEqual(RankingSync.OldPeerNotice, sync.LastNotice);
        Assert.AreEqual("ランキング同期: 相手の台の版が古いので同期しません", RankingSync.OldPeerNotice);
        Assert.AreEqual(1, sync.NoticeCount);
        sync.ReceiveForTest(old, From, t0 + 5);
        sync.Tick(t0 + 40);
        Assert.AreEqual(1, sync.NoticeCount, "1回だけ。相手が見つからないの知らせも重ねない");
        // 新しい版の相手
        Assert.AreEqual(0, sync.ReceiveForTest("BTSRANK3 " + current.Substring(RankingSyncLog.Prefix.Length), From, t0 + 41));
        Assert.AreEqual(RankingSync.NewerPeerNotice, sync.LastNotice);
        Assert.AreEqual(2, sync.NoticeCount);
        // 今の版の相手とは、そのまま合わせる
        Assert.AreEqual(1, sync.ReceiveForTest(current, From, t0 + 42));
    }

    // 1回のプレイの表は、曲を始めたとき(ResetResult)の運営モードで決まり、あとで切り替えても、その回はその表に残る。
    // 送る記録には ns と、その回の実績(RunRecording で決めた FinalAchievements と、この回に解除した記録)が入る。
    [Test]
    public void AFinishedRunGoesToTheNamespaceItStartedInWithItsAchievements()
    {
        var saved = SaveGameSession();
        var received = new List<RankingPlay>();
        Action<RankingPlay> handler = received.Add;
        int recordingCalls = 0;
        Action decide = () =>
        {
            recordingCalls++;
            Assert.IsNotNull(GameSession.FinalDailyRanking, "順位はもう出ている");
            GameSession.FinalAchievements.Add("hard_clear");
            AchievementStore.Unlock(GameSession.RunNamespace, GameSession.AchievementRunId, "hard_clear", "A", 0);
        };
        RankingSync.LocalPlayRecorded += handler;
        GameSession.RunRecording += decide;
        try
        {
            OperatorMode.OverrideForTest(true);
            GameSession.SelectedSongId = Song;
            GameSession.SelectedDifficulty = "Normal";
            GameSession.IsCalibrationMode = false;
            GameSession.ResetResult();
            Assert.AreEqual(Fes, GameSession.RunNamespace, "曲を始めたときの運営モード");
            Assert.AreEqual(0, GameSession.FinalAchievements.Count);
            // 選曲などで、この回に先に取った実績
            Assert.AreEqual(1, AchievementStore.Unlock(Fes, GameSession.AchievementRunId, "secret_open", "A", 0));
            OperatorMode.OverrideForTest(false); // 曲の途中で切り替えても
            GameSession.FinalPerfect = 10;
            GameSession.FinalCutCount = 10;
            GameSession.FinalScore = 95000;
            GameSession.RecordCompletedAchievements();
            GameSession.RecordCompletedDailyRanking();
            GameSession.RecordCompletedDailyRanking();
            GameSession.RecordCompletedHighScore();
            Assert.AreEqual(1, recordingCalls, "実績を決めるのは1回だけ");
            var play = received.Single();
            Assert.AreEqual(Fes, play.ns);
            CollectionAssert.AreEqual(new[] { "hard_clear", "secret_open" }, play.ach);
            Assert.IsTrue(RankingSyncLog.IsValid(play));
            Assert.AreEqual(1, GameSession.FinalDailyRanking.TotalPlays);
            Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(Fes, Song, "Normal")), "2000-01-01", out var fesDaily));
            Assert.AreEqual(play.id, fesDaily.entries.Single().runId);
            Assert.AreEqual(play.id, HighScoreStore.Load(Fes, Song, "Normal").entries.Single().runId);
            Assert.AreEqual(1, SongAchievementStore.Load(Fes, Song, "Normal").ap);
            Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key("", Song, "Normal")), "ふだんの表には残さない");
            Assert.IsFalse(PlayerPrefs.HasKey(HighScoreStore.Key("", Song, "Normal")));
            Assert.IsFalse(PlayerPrefs.HasKey(SongAchievementStore.Key("", Song, "Normal")));
            // 次の回は、そのときの運営モード(切った)で
            GameSession.ResetResult();
            Assert.AreEqual("", GameSession.RunNamespace);
            Assert.AreEqual(0, GameSession.FinalAchievements.Count);
        }
        finally
        {
            RankingSync.LocalPlayRecorded -= handler;
            GameSession.RunRecording -= decide;
            OperatorMode.OverrideForTest(false);
            PlayerPrefs.DeleteKey(SongAchievementStore.Key(Fes, Song, "Normal"));
            PlayerPrefs.DeleteKey(SongAchievementStore.Key("", Song, "Normal"));
            RestoreGameSession(saved);
        }
    }

    // 同期のメモリの記録を外から読む口(AchievementTracker の「となりと同じ曲」など)。足した順の写しで、書き換えても同期の記録は変わらず、
    // 一覧も書き換えられない。本番の同期(Instance)が無ければ KnownPlays は空。
    [Test]
    public void LoggedPlaysAreReadOnlyCopiesInTheOrderTheyWereAdded()
    {
        Assert.AreEqual(0, RankingSync.KnownPlays.Count, "テストでは本番の同期は動いていない");
        var sync = Offline(NewId());
        double t0 = Time.realtimeSinceStartupAsDouble;
        Assert.AreEqual(0, sync.LoggedPlays.Count);
        var theirs = FesPlay(88000, "B", "clear", "hard");
        Assert.AreEqual(1, sync.ReceiveForTest(PlaysMessage(NewId(), "B", theirs), From, t0));
        var mine = Play(score: 70000);
        Assert.IsTrue(sync.Record(mine));
        var plays = sync.LoggedPlays;
        CollectionAssert.AreEqual(new[] { theirs.id, mine.id }, plays.Select(p => p.id), "足した順(届いた回も、この台の回も)");
        Assert.IsTrue(RankingSyncLog.SameContent(theirs, plays[0]));
        CollectionAssert.AreEqual(new[] { "clear", "hard" }, plays[0].ach);
        Assert.Throws<NotSupportedException>(() => ((IList<RankingPlay>)plays).Add(Play()), "一覧は書き換えられない");
        Assert.Throws<NotSupportedException>(() => ((IList<RankingPlay>)plays)[0] = Play());
        plays[0].score = 1;
        plays[0].ns = "";
        plays[0].ach.Add("changed");
        var again = sync.LoggedPlays;
        Assert.AreNotSame(plays[0], again[0], "読むたびに新しい写し");
        Assert.AreEqual(88000, again[0].score, "写しを書き換えても同期の記録は変わらない");
        Assert.AreEqual(Fes, again[0].ns);
        CollectionAssert.AreEqual(new[] { "clear", "hard" }, again[0].ach);
        Assert.AreEqual(Fes, RankingSyncLog.Copy(again[0]).ns);
    }

    // AchievementStore.CountSince: ある時刻から後に、その実績を取った回の数(回の ID が同じなら1回)。この台の解除・相手の台から足した分に、
    // 同期のメモリの記録(まだ数に足していない回)も合わせる。名前空間ごと。保存した記録が読めないときは -1。
    [Test]
    public void CountSinceAddsTheSyncedPlaysAndCountsEachRunOnce()
    {
        string early = NewId(), late = NewId();
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, early, "clear", "A", 1000));
        Assert.AreEqual(2, AchievementStore.Unlock(Fes, late, "clear", "A", 5000));
        Assert.AreEqual(1, AchievementStore.Unlock("", NewId(), "clear", "A", 6000));
        Assert.AreEqual(2, AchievementStore.CountSince(Fes, "clear", 0));
        Assert.AreEqual(1, AchievementStore.CountSince(Fes, "clear", 5000), "その時刻ちょうどは入る");
        Assert.AreEqual(0, AchievementStore.CountSince(Fes, "clear", 5001));
        Assert.AreEqual(1, AchievementStore.CountSince("", "clear", 0), "名前空間ごと");
        Assert.AreEqual(0, AchievementStore.CountSince(Fes, "hard", 0));
        Assert.AreEqual(0, AchievementStore.CountSince(Fes, "bad id!", 0));
        // 同期のメモリにだけある回(起動して、照合でまだ数に足していない回)も数える。同じ回の ID は1回
        var remote = FesPlay(80000, "B", "clear");
        var sameRun = RankingSyncLog.NewPlay(late, Song, "Normal", 85000, "S", .85f, PlayedAt, "A", Fes, new[] { "clear" });
        var otherAchievement = FesPlay(70000, "B", "hard");
        var ordinary = Play(score: 60000, station: "B");
        ordinary.ach = new List<string> { "clear" };
        var saved = new RankingSyncLog();
        Assert.IsTrue(saved.Add(remote) && saved.Add(sameRun) && saved.Add(otherAchievement) && saved.Add(ordinary));
        saved.Save(LogKey);
        var sync = Offline(NewId()); // 照合(Tick)の前
        Assert.AreEqual(2, AchievementStore.CountSince(Fes, "clear", 0), "本番の同期が無ければ、実績の記録だけ");
        Assert.AreEqual(3, AchievementStore.CountSince(Fes, "clear", 0, sync), "同期の記録の回を足す(late は1回)");
        Assert.AreEqual(2, AchievementStore.CountSince(Fes, "clear", remote.at, sync), "遊び終わった時刻で見る(remote と late)");
        Assert.AreEqual(2, AchievementStore.CountSince("", "clear", 0, sync), "ふだんの回はふだんの数");
        // 照合で数に足したあとも、同じ数
        sync.Tick(Time.realtimeSinceStartupAsDouble);
        Assert.AreEqual(3, AchievementStore.CountSince(Fes, "clear", 0));
        Assert.AreEqual(3, AchievementStore.CountSince(Fes, "clear", 0, sync));
        // 保存した記録に読めない所があれば分からない(-1)
        PlayerPrefs.SetString(AchievementKey, "not json");
        AchievementStore.UseKeyForTest(AchievementKey);
        LogAssert.Expect(LogType.Warning, AchievementStore.UnreadableWarning(AchievementKey));
        Assert.AreEqual(-1, AchievementStore.CountSince(Fes, "clear", 0, sync));
        Assert.AreEqual(-1, AchievementStore.CountSince(Fes, "clear", 0));
    }
}
