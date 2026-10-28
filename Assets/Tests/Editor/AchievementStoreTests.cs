using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 実績の解除の記録(AchievementStore、2026-10-29)。「○人目」の番号・二重に足さない・名前空間ごと・相手の台の分(Merge)・壊れた値。
// 専用の鍵だけを使い、テストの後で消す(本番の Achievements.Log.v1 には触れない)。
public class AchievementStoreTests
{
    const string Key = "Achievements.Log.v1.__AchievementStoreTest";
    const string Fes = OperatorMode.FestivalNamespace;
    string savedKey, production;

    [SetUp]
    public void UseTestKey()
    {
        production = PlayerPrefs.HasKey(AchievementStore.PreferenceKey) ? PlayerPrefs.GetString(AchievementStore.PreferenceKey) : null;
        savedKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(Key);
    }

    [TearDown]
    public void Restore()
    {
        PlayerPrefs.DeleteKey(Key);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedKey);
        Assert.AreEqual(production, PlayerPrefs.HasKey(AchievementStore.PreferenceKey) ? PlayerPrefs.GetString(AchievementStore.PreferenceKey) : null,
            "本番の鍵に触れない");
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    [Test]
    public void UnlockReturnsTheNumberOfPeopleWhoGotItIncludingThisOne()
    {
        string first = NewId(), second = NewId(), third = NewId();
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, first, "hard_clear", "A", 1000));
        Assert.AreEqual(2, AchievementStore.Unlock(Fes, second, "hard_clear", "B", 2000));
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, second, "gold_all", "B", 2000), "実績ごとに数える");
        Assert.AreEqual(2, AchievementStore.Unlock(Fes, second, "hard_clear", "B", 9000), "同じ回の同じ実績は足さず、そのときの番号");
        Assert.AreEqual(3, AchievementStore.Unlock(Fes, third, "hard_clear", "A", 3000));
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, first, "hard_clear", "A", 1000), "前の回の番号も変わらない");
        Assert.AreEqual(3, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(1, AchievementStore.Count(Fes, "gold_all"));
        Assert.AreEqual(0, AchievementStore.Count(Fes, "never"));
        // 名前空間ごとに別に数える
        Assert.AreEqual(0, AchievementStore.Count("", "hard_clear"));
        Assert.AreEqual(1, AchievementStore.Unlock("", first, "hard_clear", "A", 1000), "ふだんの数は1人目から");
        Assert.AreEqual(1, AchievementStore.Unlock(null, third, "gold_all", "A", 1000), "null はふだん");
        Assert.AreEqual(1, AchievementStore.Count("", "gold_all"));
        Assert.AreEqual(3, AchievementStore.Count(Fes, "hard_clear"), "ふだんで取っても本番の数は変わらない");
        CollectionAssert.AreEqual(new[] { "hard_clear", "gold_all" }, AchievementStore.UnlockedIn(Fes, second), "その回に取った順");
        CollectionAssert.AreEqual(new[] { "hard_clear" }, AchievementStore.UnlockedIn("", first));
        Assert.AreEqual(0, AchievementStore.UnlockedIn(Fes, NewId()).Count);
        Assert.AreEqual(0, AchievementStore.UnlockedIn(Fes, null).Count);
        Assert.AreEqual(6, AchievementStore.EntryCount);
    }

    [Test]
    public void UnlocksAreSavedAndReadBack()
    {
        string run = NewId();
        AchievementStore.Unlock(Fes, run, "hard_clear", "A", 1234);
        AchievementStore.Unlock(Fes, NewId(), "hard_clear", "B", 5678);
        Assert.IsTrue(PlayerPrefs.HasKey(Key), "すぐ保存する");
        StringAssert.StartsWith("{\"v\":1,\"entries\":[", PlayerPrefs.GetString(Key));
        Assert.IsFalse(AchievementStore.NeedsWrite);
        AchievementStore.UseKeyForTest(Key); // 読み直す
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, run, "hard_clear", "A", 1234), "読み直しても番号は同じ");
        CollectionAssert.AreEqual(new[] { "hard_clear" }, AchievementStore.UnlockedIn(Fes, run));
        Assert.AreEqual(3, AchievementStore.Unlock(Fes, NewId(), "hard_clear", "A", 9999));
    }

    // 曲の終わりにまとめて解除する: 番号は Unlock と同じ意味で同じ順(重なりは同じ番号・形の違う ID は 0)。保存は最後に1回。
    [Test]
    public void UnlockManyNumbersEachLikeUnlockAndSavesOnce()
    {
        string first = NewId(), second = NewId();
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, NewId(), "combo50", "A", 500));
        int writes = AchievementStore.WriteCountForTest;
        var numbers = AchievementStore.UnlockMany(Fes, first, new[] { "clear", "combo50", "clear", "bad id!", "fc" }, "A", 1000);
        CollectionAssert.AreEqual(new[] { 1, 2, 1, 0, 1 }, numbers);
        Assert.AreEqual(writes + 1, AchievementStore.WriteCountForTest, "保存は最後に1回");
        CollectionAssert.AreEqual(new[] { "clear", "combo50", "fc" }, AchievementStore.UnlockedIn(Fes, first), "取った順");
        CollectionAssert.AreEqual(new[] { 2, 1 }, AchievementStore.UnlockMany(Fes, second, new[] { "clear", "dual" }, "B", 2000));
        writes = AchievementStore.WriteCountForTest;
        CollectionAssert.AreEqual(new[] { 1, 2 }, AchievementStore.UnlockMany(Fes, first, new[] { "clear", "combo50" }, "A", 3000),
            "同じ回は、そのときの番号のまま");
        Assert.AreEqual(writes, AchievementStore.WriteCountForTest, "足していなければ書かない");
        Assert.AreEqual(0, AchievementStore.UnlockMany(Fes, first, new string[0], "A", 1000).Length);
        Assert.AreEqual(0, AchievementStore.UnlockMany(Fes, first, null, "A", 1000).Length);
        CollectionAssert.AreEqual(new[] { 0, 0 }, AchievementStore.UnlockMany(Fes, "short", new[] { "clear", "fc" }, "A", 1000), "回の ID が違う");
        AchievementStore.UseKeyForTest(Key); // 読み直しても同じ
        Assert.AreEqual(2, AchievementStore.Count(Fes, "clear"));
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, first, "fc", "A", 0));
        Assert.AreEqual(1, AchievementStore.CountSince(Fes, "clear", 2000), "second だけ(取った時刻 2000)");
    }

    // 相手の台の回の実績は数に足すだけ(番号は返さない・PlayerPrefs へはまとめて書く)。同じ回は二重に足さない。
    [Test]
    public void TheOtherMachinesAchievementsAreMergedWithoutDoubleCounting()
    {
        string theirs = NewId();
        AchievementStore.Unlock(Fes, NewId(), "hard_clear", "A", 1000);
        AchievementStore.Merge(Fes, theirs, new[] { "hard_clear", "gold_all", "hard_clear", "bad id!", "", null }, "B", 2000);
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(1, AchievementStore.Count(Fes, "gold_all"));
        CollectionAssert.AreEqual(new[] { "hard_clear", "gold_all" }, AchievementStore.UnlockedIn(Fes, theirs), "形の違う ID は捨てる");
        AchievementStore.Merge(Fes, theirs, new[] { "hard_clear" }, "B", 2000);
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"), "同じ回は二重に足さない");
        AchievementStore.Merge(Fes, "bad run!", new[] { "hard_clear" }, "B", 2000);
        AchievementStore.Merge(Fes, NewId(), new[] { "hard_clear" }, "A B", 2000);
        AchievementStore.Merge(Fes, NewId(), new[] { "hard_clear" }, "B", 0);
        AchievementStore.Merge("FES", NewId(), new[] { "hard_clear" }, "B", 2000);
        AchievementStore.Merge(Fes, NewId(), null, "B", 2000);
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"), "壊れた回の ID・台名・時刻・名前空間は足さない");
        Assert.IsTrue(AchievementStore.NeedsWrite, "PlayerPrefs へはまだ書かない(RankingSync がまとめて書く)");
        AchievementStore.UseKeyForTest(Key);
        Assert.AreEqual(1, AchievementStore.Count(Fes, "hard_clear"), "書く前に読み直すと、この台で取った分だけ");
        AchievementStore.Merge(Fes, theirs, new[] { "hard_clear", "gold_all" }, "B", 2000);
        Assert.IsTrue(AchievementStore.WriteToPrefs());
        Assert.IsFalse(AchievementStore.NeedsWrite);
        Assert.IsTrue(AchievementStore.WriteToPrefs(), "書く分が無くても true");
        AchievementStore.UseKeyForTest(Key);
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(2, AchievementStore.Unlock(Fes, theirs, "hard_clear", "A", 3000), "足した回の同じ実績は、足した番号のまま");
        Assert.AreEqual(2, AchievementStore.Count(Fes, "hard_clear"));
    }

    [Test]
    public void BadArgumentsAreNotRecorded()
    {
        Assert.AreEqual(0, AchievementStore.Unlock(Fes, "short", "hard_clear", "A", 1000), "回の ID が短い");
        Assert.AreEqual(0, AchievementStore.Unlock(Fes, null, "hard_clear", "A", 1000));
        Assert.AreEqual(0, AchievementStore.Unlock(Fes, NewId(), "hard clear", "A", 1000), "実績の ID に空白");
        Assert.AreEqual(0, AchievementStore.Unlock(Fes, NewId(), new string('a', AchievementStore.MaxIdLength + 1), "A", 1000));
        Assert.AreEqual(0, AchievementStore.Unlock("Fes", NewId(), "hard_clear", "A", 1000), "名前空間の形が違う");
        Assert.AreEqual(0, AchievementStore.Count(Fes, "hard_clear"));
        Assert.IsFalse(PlayerPrefs.HasKey(Key), "何も書かない");
        // 台名の形が違えば空にして残す。時刻が 0 以下なら今の時刻
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.AreEqual(1, AchievementStore.Unlock(Fes, NewId(), "hard_clear", "A B", 0));
        string saved = PlayerPrefs.GetString(Key);
        StringAssert.Contains("\"st\":\"\"", saved);
        var at = Regex.Match(saved, "\"at\":(\\d+)");
        Assert.IsTrue(at.Success, saved);
        Assert.GreaterOrEqual(long.Parse(at.Groups[1].Value), before, "今の時刻");
        Assert.IsTrue(AchievementStore.IsValidId("a") && AchievementStore.IsValidId("Hard-Clear_2") &&
                      AchievementStore.IsValidId(new string('z', AchievementStore.MaxIdLength)));
        Assert.IsFalse(AchievementStore.IsValidId(null) || AchievementStore.IsValidId("") || AchievementStore.IsValidId("ハード") ||
                       AchievementStore.IsValidId("a.b") || AchievementStore.IsValidId(new string('z', AchievementStore.MaxIdLength + 1)));
    }

    // 1件でも壊れた値・重なった値があれば、全体が読めないときと同じく上書きしない(この起動の間だけ覚え、番号は出さない)。
    // 読めた1件を詰めて数え直すと、前に出した「○人目」とずれるため。読めた分は、その回の実績(UnlockedIn)には使う。
    [Test]
    public void APartlyBrokenLogIsNotOverwrittenEither()
    {
        string run = NewId();
        string good = "{\"ns\":\"fes\",\"run\":\"" + run + "\",\"ach\":\"hard_clear\",\"st\":\"A\",\"at\":1000}";
        string noTime = "{\"ns\":\"fes\",\"run\":\"" + NewId() + "\",\"ach\":\"hard_clear\",\"st\":\"A\"}";
        string badNs = "{\"ns\":\"FES\",\"run\":\"" + NewId() + "\",\"ach\":\"hard_clear\",\"st\":\"A\",\"at\":1000}";
        string badId = "{\"ns\":\"fes\",\"run\":\"" + NewId() + "\",\"ach\":\"hard clear\",\"st\":\"A\",\"at\":1000}";
        string noRun = "{\"ns\":\"fes\",\"ach\":\"hard_clear\",\"st\":\"A\",\"at\":1000}";
        foreach (string bad in new[] { good, noTime, badNs, badId, noRun, "3", "[]" })
        {
            string saved = "{\"v\":1,\"entries\":[" + good + "," + bad + "]}";
            PlayerPrefs.SetString(Key, saved);
            AchievementStore.UseKeyForTest(Key);
            LogAssert.Expect(LogType.Warning, AchievementStore.UnreadableWarning(Key));
            Assert.IsFalse(AchievementStore.Available, bad);
            Assert.AreEqual(0, AchievementStore.Count(Fes, "hard_clear"), "番号は出さない: " + bad);
            CollectionAssert.AreEqual(new[] { "hard_clear" }, AchievementStore.UnlockedIn(Fes, run), "読めた1件はその回の実績に使う");
            Assert.AreEqual(0, AchievementStore.Unlock(Fes, NewId(), "hard_clear", "B", 2000), bad);
            AchievementStore.Merge(Fes, NewId(), new[] { "gold_all" }, "B", 2000);
            Assert.IsFalse(AchievementStore.NeedsWrite, bad);
            Assert.IsTrue(AchievementStore.WriteToPrefs(), bad);
            Assert.AreEqual(saved, PlayerPrefs.GetString(Key), "上書きしない: " + bad);
        }
        // 正しい記録だけなら、そのまま読んで数える
        PlayerPrefs.SetString(Key, "{\"v\":1,\"entries\":[" + good + "]}");
        AchievementStore.UseKeyForTest(Key);
        Assert.IsTrue(AchievementStore.Available);
        Assert.AreEqual(1, AchievementStore.Count(Fes, "hard_clear"));
        Assert.AreEqual(2, AchievementStore.Unlock(Fes, NewId(), "hard_clear", "B", 2000));
    }

    // 全体が読めない・版が違う記録は上書きしない(この起動の間だけ覚え、○人目は出さない: Unlock・Count は 0)。
    [Test]
    public void AnUnreadableLogIsNotOverwritten()
    {
        foreach (string broken in new[] { "not json", "{\"v\":2,\"entries\":[]}", "{\"entries\":[]}", "{\"v\":1}", "{\"v\":1,\"entries\":{}}" })
        {
            PlayerPrefs.SetString(Key, broken);
            AchievementStore.UseKeyForTest(Key);
            LogAssert.Expect(LogType.Warning, AchievementStore.UnreadableWarning(Key));
            Assert.IsFalse(AchievementStore.Available, broken);
            string run = NewId();
            Assert.AreEqual(0, AchievementStore.Unlock(Fes, run, "hard_clear", "A", 1000), broken);
            Assert.AreEqual(0, AchievementStore.Unlock(Fes, run, "hard_clear", "A", 1000), broken);
            Assert.AreEqual(0, AchievementStore.Count(Fes, "hard_clear"), broken);
            CollectionAssert.AreEqual(new[] { "hard_clear" }, AchievementStore.UnlockedIn(Fes, run), "その回に取ったことは覚えている(同期の記録に入れる)");
            AchievementStore.Merge(Fes, NewId(), new[] { "gold_all" }, "B", 1000);
            Assert.IsFalse(AchievementStore.NeedsWrite, broken);
            Assert.IsTrue(AchievementStore.WriteToPrefs(), broken);
            Assert.AreEqual(broken, PlayerPrefs.GetString(Key), "上書きしない");
        }
    }
}
