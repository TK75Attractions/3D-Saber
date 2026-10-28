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
public class RankingSyncTests
{
    const string Song = "__RankingSyncTest";
    const string JapaneseSong = "__RankingSyncTest揺籠";
    const string LogKey = "RankingSync.Log.v1.__RankingSyncTest";
    const string PendingKey = "RankingSync.Pending.v1.__RankingSyncTest";
    static readonly string[] Difficulties = { "Normal", "Hard" };
    static readonly DateTime PlayedAt = new DateTime(2026, 10, 28, 13, 0, 0);
    static readonly IPEndPoint From = new IPEndPoint(IPAddress.Parse("192.168.137.20"), RankingSync.DefaultPort);
    readonly List<RankingSync> created = new List<RankingSync>();

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
        foreach (string song in new[] { Song, JapaneseSong })
            foreach (string difficulty in Difficulties)
            {
                PlayerPrefs.DeleteKey(DailyRankingStore.Key(song, difficulty));
                PlayerPrefs.DeleteKey(HighScoreStore.Key(song, difficulty));
            }
        PlayerPrefs.DeleteKey(LogKey);
        PlayerPrefs.DeleteKey(PendingKey);
        PlayerPrefs.Save();
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

    static DailyRankingStore.Table DailyTable(string song, string difficulty)
    {
        Assert.IsTrue(DailyRankingStore.TryRead(PlayerPrefs.GetString(DailyRankingStore.Key(song, difficulty), ""), "2000-01-01", out var table));
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

        foreach (string text in new[] { "not json", "{\"v\":2,\"plays\":[]}", "{\"plays\":[]}" })
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
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{\"v\":1,\"kind\":\"digest\",\"from\":\"" + from +
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
        Assert.IsFalse(RankingSyncLog.TryParse("BTSRANK2 " + digest.Substring(RankingSyncLog.Prefix.Length), out _), "版の違う目印");
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{broken", out _), "JSON として読めない");
        Assert.IsFalse(RankingSyncLog.TryParse(RankingSyncLog.Prefix + "{}", out _), "版が無い");
        Assert.IsFalse(RankingSyncLog.TryParse(digest.Replace("\"v\":1", "\"v\":2"), out _), "版が違う");
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
}
