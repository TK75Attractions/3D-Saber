using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

// 運営モード(OperatorMode・F12 の OperatorModeHotkey・版の表示の「運営」、2026-10-29)と、名前空間で分けたランキングの表
// (DailyRankingStore・HighScoreStore・SongAchievementStore)。"" の鍵は今までと1文字も変わらない(今の記録がそのまま読める)。
// 専用の曲 ID の鍵だけを使い、テストの後で消す。
public class OperatorModeTests
{
    const string Song = "__OperatorModeTest";
    const string Fes = OperatorMode.FestivalNamespace;
    static readonly DateTime PlayedAt = new DateTime(2026, 10, 29, 10, 0, 0);

    string savedKey;

    // まとめの既定(OperatorModeEditorTestDefaults)は Unity では一部のテストにしか効かないので、ここでも専用の保存先・切った状態にする。
    [SetUp]
    public void UseTestKey()
    {
        savedKey = OperatorMode.Key;
        OperatorMode.UseKeyForTest(OperatorModeEditorTestDefaults.OperatorKey);
        OperatorMode.OverrideForTest(false);
    }

    [TearDown]
    public void Cleanup()
    {
        OperatorMode.OverrideForTest(false);
        OperatorMode.UseKeyForTest(savedKey);
        foreach (string ns in new[] { "", Fes })
            foreach (string difficulty in new[] { "Normal", "Hard" })
            {
                PlayerPrefs.DeleteKey(DailyRankingStore.Key(ns, Song, difficulty));
                PlayerPrefs.DeleteKey(HighScoreStore.Key(ns, Song, difficulty));
                PlayerPrefs.DeleteKey(SongAchievementStore.Key(ns, Song, difficulty));
            }
        PlayerPrefs.Save();
    }

    static HighScoreEntry Entry(int score, string runId, long at) =>
        new HighScoreEntry { score = score, rank = "A", accuracy = .7f, date = "2026/10/29", runId = runId, at = at };

    [Test]
    public void TheOrdinaryNamespaceKeepsTheExistingKeysAndTheFestivalOneAddsAPrefix()
    {
        Assert.AreEqual("dailyRanking_v2_8:ElDorado:hard", DailyRankingStore.Key("", "ElDorado", "Hard"));
        Assert.AreEqual("hiscore_v2_ElDorado_hard", HighScoreStore.Key("", "ElDorado", "Hard"));
        Assert.AreEqual("songAchievements_v2_ElDorado::Hard", SongAchievementStore.Key("", "ElDorado", " hard "));
        Assert.AreEqual("dailyRanking_v2_2:揺籠:normal", DailyRankingStore.Key("", "揺籠", null));
        Assert.AreEqual("songAchievements_v2_揺籠::g", SongAchievementStore.Key("", "揺籠", "G"), "裏譜面の1文字は小文字(今までどおり)");
        Assert.AreEqual("fes_dailyRanking_v2_8:ElDorado:hard", DailyRankingStore.Key(Fes, "ElDorado", "Hard"));
        Assert.AreEqual("fes_hiscore_v2_ElDorado_hard", HighScoreStore.Key(Fes, "ElDorado", "Hard"));
        Assert.AreEqual("fes_songAchievements_v2_ElDorado::Hard", SongAchievementStore.Key(Fes, "ElDorado", "hard"));
        Assert.AreEqual(HighScoreStore.Key("", "ElDorado", "Hard"), HighScoreStore.Key(null, "ElDorado", "Hard"), "null はふだん");
        // 名前空間を渡さない呼び出しは、今の運営モード(テストでは切っている = 今までと同じ鍵)
        Assert.AreEqual(DailyRankingStore.Key("", "ElDorado", "Hard"), DailyRankingStore.Key("ElDorado", "Hard"));
        Assert.AreEqual(HighScoreStore.Key("", "ElDorado", "Hard"), HighScoreStore.Key("ElDorado", "Hard"));
        Assert.AreEqual(SongAchievementStore.Key("", "ElDorado", "Hard"), SongAchievementStore.Key("ElDorado", "Hard"));
        OperatorMode.OverrideForTest(true);
        Assert.AreEqual("fes_dailyRanking_v2_8:ElDorado:hard", DailyRankingStore.Key("ElDorado", "Hard"));
        Assert.AreEqual("fes_hiscore_v2_ElDorado_hard", HighScoreStore.Key("ElDorado", "Hard"));
        Assert.AreEqual("fes_songAchievements_v2_ElDorado::Hard", SongAchievementStore.Key("ElDorado", "Hard"));
    }

    // 名前空間ごとに別の表: 本番の表は初めは空で、ふだんの記録と比べない。同じ回の ID でも表が違えば別に数える。
    [Test]
    public void EachNamespaceHasItsOwnTables()
    {
        var ordinary = DailyRankingStore.Record("", Song, "Normal", "ordinary-run-1", 90000, PlayedAt);
        var festival = DailyRankingStore.Record(Fes, Song, "Normal", "festival-run-1", 50000, PlayedAt);
        Assert.AreEqual(1, ordinary.Rank);
        Assert.AreEqual(1, festival.Rank, "本番の表は初めは空(ふだんの 90000 と比べない)");
        Assert.AreEqual(1, festival.TotalPlays);
        Assert.AreEqual(RankingMergeResult.Added, DailyRankingStore.Merge(Fes, Song, "Normal", "festival-run-2", 70000, PlayedAt));
        Assert.AreEqual(RankingMergeResult.Added, DailyRankingStore.Merge("", Song, "Normal", "festival-run-2", 70000, PlayedAt), "表が違えば別");
        Assert.AreEqual(RankingMergeResult.AlreadyThere,
            DailyRankingStore.MergeAll(Fes, Song, "Normal", new[] { new DailyRankingStore.Entry { runId = "festival-run-1", score = 50000 } }, PlayedAt)[0]);
        var third = DailyRankingStore.Record(Fes, Song, "Normal", "festival-run-3", 60000, PlayedAt);
        Assert.AreEqual(2, third.Rank);
        Assert.AreEqual(3, third.TotalPlays);
        var ordinarySecond = DailyRankingStore.Record("", Song, "Normal", "ordinary-run-2", 80000, PlayedAt);
        Assert.AreEqual(3, ordinarySecond.TotalPlays, "ふだんの表は 90000・70000・80000 の3回");
        Assert.AreEqual(2, ordinarySecond.Rank);

        Assert.AreEqual(0, HighScoreStore.Record("", Song, "Hard", Entry(99000, "ordinary-1", 1), out _));
        Assert.AreEqual(0, HighScoreStore.Record(Fes, Song, "Hard", Entry(10000, "festival-1", 2), out _), "本番の表では1位");
        Assert.AreEqual(RankingMergeResult.Added, HighScoreStore.Merge(Fes, Song, "Hard", Entry(20000, "festival-2", 3)));
        Assert.AreEqual(RankingMergeResult.Added,
            HighScoreStore.RecordRun(Fes, Song, "Hard", Entry(15000, "festival-3", 4), out _, out int index));
        Assert.AreEqual(1, index);
        Assert.IsTrue(HighScoreStore.TryLoad(Fes, Song, "Hard", out var fesTable));
        CollectionAssert.AreEqual(new[] { 20000, 15000, 10000 }, fesTable.entries.Select(e => e.score));
        CollectionAssert.AreEqual(new[] { 99000 }, HighScoreStore.Load("", Song, "Hard").entries.Select(e => e.score));
        HighScoreStore.Clear(Fes, Song, "Hard");
        Assert.AreEqual(0, HighScoreStore.Load(Fes, Song, "Hard").entries.Count);
        Assert.AreEqual(1, HighScoreStore.Load("", Song, "Hard").entries.Count, "消すのもその表だけ");

        Assert.IsTrue(SongAchievementStore.Record(Fes, Song, "Normal", "same-run", 5, 0, 0, 0, 0, ScoreManager.MaxScore));
        Assert.IsTrue(SongAchievementStore.Record("", Song, "Normal", "same-run", 5, 0, 0, 0, 0, ScoreManager.MaxScore), "表ごとに数える");
        Assert.IsFalse(SongAchievementStore.Record(Fes, Song, "Normal", "same-run", 5, 0, 0, 0, 0, ScoreManager.MaxScore));
        Assert.AreEqual(1, SongAchievementStore.Load(Fes, Song, "Normal").ap);
        Assert.AreEqual(1, SongAchievementStore.Load("", Song, "Normal").ap);
    }

    // 名前空間を渡さない読み書き(選曲の TOP 5・達成の数などの表示)は、今の運営モードの表。
    [Test]
    public void CallsWithoutANamespaceUseTheCurrentMode()
    {
        HighScoreStore.Record(Fes, Song, "Normal", Entry(30000, "festival-1", 1), out _);
        HighScoreStore.Record("", Song, "Normal", Entry(80000, "ordinary-1", 1), out _);
        Assert.AreEqual(80000, HighScoreStore.Load(Song, "Normal").entries.Single().score, "切っているときはふだんの表");
        OperatorMode.OverrideForTest(true);
        Assert.AreEqual(30000, HighScoreStore.Load(Song, "Normal").entries.Single().score, "運営モードでは本番の表");
        Assert.IsTrue(SongAchievementStore.Record(Song, "Normal", "run-x", 3, 0, 0, 0, 0, ScoreManager.MaxScore));
        Assert.AreEqual(1, SongAchievementStore.Load(Fes, Song, "Normal").ap);
        Assert.AreEqual(0, SongAchievementStore.Load("", Song, "Normal").ap);
        Assert.AreEqual(1, DailyRankingStore.Record(Song, "Normal", "run-y", 40000, PlayedAt).TotalPlays);
        Assert.IsTrue(PlayerPrefs.HasKey(DailyRankingStore.Key(Fes, Song, "Normal")));
        Assert.IsFalse(PlayerPrefs.HasKey(DailyRankingStore.Key("", Song, "Normal")));
        OperatorMode.OverrideForTest(false);
        Assert.AreEqual(0, SongAchievementStore.Load(Song, "Normal").ap);
        Assert.AreEqual(80000, HighScoreStore.Load(Song, "Normal").entries.Single().score);
    }

    [Test]
    public void ToggleSwitchesTheNamespaceAndTellsListeners()
    {
        bool hadProduction = PlayerPrefs.HasKey(OperatorMode.PreferenceKey);
        int production = PlayerPrefs.GetInt(OperatorMode.PreferenceKey, 0);
        int changes = 0;
        Action listener = () => changes++;
        OperatorMode.Changed += listener;
        try
        {
            Assert.AreNotEqual(OperatorMode.PreferenceKey, OperatorMode.Key, "テストは専用の保存先");
            Assert.IsFalse(OperatorMode.IsOn);
            Assert.AreEqual("", OperatorMode.Namespace);
            OperatorMode.Toggle();
            Assert.IsTrue(OperatorMode.IsOn);
            Assert.AreEqual("fes", OperatorMode.Namespace);
            Assert.AreEqual(1, changes);
            OperatorMode.Set(true);
            Assert.AreEqual(1, changes, "変わらなければ知らせない");
            OperatorMode.Toggle();
            Assert.IsFalse(OperatorMode.IsOn);
            Assert.AreEqual("", OperatorMode.Namespace);
            Assert.AreEqual(2, changes);
            Assert.IsFalse(PlayerPrefs.HasKey(OperatorMode.Key), "差し替え中は保存しない");
            Assert.AreEqual(hadProduction, PlayerPrefs.HasKey(OperatorMode.PreferenceKey), "本番の保存先に触れない");
            Assert.AreEqual(production, PlayerPrefs.GetInt(OperatorMode.PreferenceKey, 0));
        }
        finally { OperatorMode.Changed -= listener; }
    }

    // 差し替えを外すと保存先(本番は OperatorMode.v1)に残す(次の起動でもそのまま)。保存が無ければ切っている。
    // 保存先は専用の鍵に差し替えて確かめる(本番の OperatorMode.v1 は読まない・書かない)。
    [Test]
    public void TheModeIsSavedAndOffByDefault()
    {
        const string TestKey = "OperatorMode.v1.__OperatorModeTest";
        bool hadProduction = PlayerPrefs.HasKey(OperatorMode.PreferenceKey);
        int production = PlayerPrefs.GetInt(OperatorMode.PreferenceKey, 0);
        string savedKey = OperatorMode.Key;
        try
        {
            PlayerPrefs.DeleteKey(TestKey);
            OperatorMode.UseKeyForTest(TestKey);
            Assert.AreEqual(TestKey, OperatorMode.Key);
            OperatorMode.OverrideForTest(null);
            Assert.IsFalse(OperatorMode.IsOn, "保存が無ければ切っている");
            OperatorMode.Toggle();
            Assert.AreEqual(1, PlayerPrefs.GetInt(TestKey, 0));
            Assert.IsTrue(OperatorMode.IsOn);
            OperatorMode.Toggle();
            Assert.AreEqual(0, PlayerPrefs.GetInt(TestKey, -1));
            PlayerPrefs.SetInt(TestKey, 1);
            Assert.IsTrue(OperatorMode.IsOn, "保存した値を読む");
            PlayerPrefs.SetInt(TestKey, 7);
            Assert.IsFalse(OperatorMode.IsOn, "1 のほかは切っている");
            Assert.AreEqual(hadProduction, PlayerPrefs.HasKey(OperatorMode.PreferenceKey), "本番の保存先に触れない");
            Assert.AreEqual(production, PlayerPrefs.GetInt(OperatorMode.PreferenceKey, 0));
            OperatorMode.UseKeyForTest(null);
            Assert.AreEqual(OperatorMode.PreferenceKey, OperatorMode.Key, "null で本番の鍵");
        }
        finally
        {
            OperatorMode.OverrideForTest(false);
            OperatorMode.UseKeyForTest(savedKey);
            PlayerPrefs.DeleteKey(TestKey);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void NamespacesAreShortLowercaseNames()
    {
        Assert.AreEqual("", OperatorMode.KeyPrefix(""));
        Assert.AreEqual("", OperatorMode.KeyPrefix(null));
        Assert.AreEqual("fes_", OperatorMode.KeyPrefix("fes"));
        foreach (string ok in new[] { null, "", "fes", "a", "fes2026", new string('z', OperatorMode.MaxNamespaceLength) })
            Assert.IsTrue(OperatorMode.IsValidNamespace(ok), ok ?? "null");
        foreach (string bad in new[] { "FES", "fes_", "fes ", " ", "フェス", "a-b", new string('z', OperatorMode.MaxNamespaceLength + 1) })
            Assert.IsFalse(OperatorMode.IsValidNamespace(bad), bad);
    }

    // F12: 文字を打っている間・幕の間は何もしない。曲の本編の演奏中は断る(「演奏中は切り替えません」)。それ以外で切り替える。
    [Test]
    public void F12TogglesOnlyOutsideTheSongAndNotWhileTypingOrDuringACurtain()
    {
        Assert.AreEqual(Key.F12, OperatorModeHotkey.ToggleKey);
        Assert.AreEqual(OperatorModeHotkey.Decision.Toggle, OperatorModeHotkey.Decide(false, false, false));
        Assert.AreEqual(OperatorModeHotkey.Decision.Refuse, OperatorModeHotkey.Decide(false, false, true));
        Assert.AreEqual(OperatorModeHotkey.Decision.Ignore, OperatorModeHotkey.Decide(true, false, false), "結果画面の一言の欄で打っている間");
        Assert.AreEqual(OperatorModeHotkey.Decision.Ignore, OperatorModeHotkey.Decide(false, true, false), "幕(暗転)の間");
        Assert.AreEqual(OperatorModeHotkey.Decision.Ignore, OperatorModeHotkey.Decide(true, true, true));
        OperatorModeHotkey.Press(OperatorModeHotkey.Decision.Refuse);
        Assert.IsFalse(OperatorMode.IsOn, "断ったら切り替えない");
        OperatorModeHotkey.Press(OperatorModeHotkey.Decision.Ignore);
        Assert.IsFalse(OperatorMode.IsOn);
        OperatorModeHotkey.Press(OperatorModeHotkey.Decision.Toggle);
        Assert.IsTrue(OperatorMode.IsOn);
        OperatorModeHotkey.Press(OperatorModeHotkey.Decision.Toggle);
        Assert.IsFalse(OperatorMode.IsOn);
        Assert.AreEqual("運営モード オン: ランキングと実績は本番用(2台で共有)", OperatorModeHotkey.OnNotice);
        Assert.AreEqual("運営モード オフ: ふだんのランキングと実績", OperatorModeHotkey.OffNotice);
        Assert.AreEqual("演奏中は切り替えません", OperatorModeHotkey.PlayingNotice);
    }

    // 運営モードの間は、版の表示の左に同じ小ささ・薄さで「運営」。版の文字はそのまま、照準やクリックを横取りしない。
    [Test]
    public void TheOperatorBadgeSitsNextToTheVersionOnlyInOperatorMode()
    {
        var label = GameVersionLabel.Create();
        GameVersionLabel second = null;
        try
        {
            var badge = label.OperatorBadge;
            Assert.IsNotNull(badge);
            Assert.AreEqual(GameVersionLabel.OperatorBadgeText, badge.text);
            Assert.AreEqual("運営", GameVersionLabel.OperatorBadgeText);
            Assert.IsFalse(badge.gameObject.activeSelf, "切っているときは出さない");
            Assert.AreEqual(label.Text.fontSize, badge.fontSize, "版と同じ小ささ");
            Assert.AreEqual(label.Text.color.a, badge.color.a, 1e-4, "版と同じ薄さ");
            Assert.IsFalse(badge.raycastTarget);
            Assert.Less(badge.rectTransform.anchoredPosition.x, label.Text.rectTransform.anchoredPosition.x, "版の左");
            Assert.AreEqual(label.Text.rectTransform.anchoredPosition.y, badge.rectTransform.anchoredPosition.y, 1e-4);
            OperatorMode.OverrideForTest(true);
            label.RefreshOperatorBadge();
            Assert.IsTrue(badge.gameObject.activeSelf, "運営モードの間は出す");
            second = GameVersionLabel.Create();
            Assert.IsTrue(second.OperatorBadge.gameObject.activeSelf, "運営モードで作ったときも出す");
            OperatorMode.OverrideForTest(false);
            label.RefreshOperatorBadge();
            Assert.IsFalse(badge.gameObject.activeSelf);
            Assert.AreEqual(GameVersion.Label, label.Text.text, "版の文字はそのまま");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(label.gameObject);
            if (second != null) UnityEngine.Object.DestroyImmediate(second.gameObject);
        }
    }
}
