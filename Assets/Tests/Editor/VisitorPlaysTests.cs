using NUnit.Framework;
using UnityEngine;

// 保存値は読むだけ。差し替えと純関数で曲数の決まりを確かめる。
public class VisitorPlaysTests
{
    bool savedOperated, savedSecretCarry, savedBridge;
    string savedReturnSong, savedReturnDifficulty;
    bool hadMode;
    int savedMode;

    [SetUp]
    public void SetUp()
    {
        savedOperated = GameSession.SongSelectOperated;
        savedSecretCarry = GameSession.RushESecretCarry;
        savedBridge = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
        ResultSelectionReturn.Consume(out savedReturnSong, out savedReturnDifficulty);
        hadMode = PlayerPrefs.HasKey(VisitorPlays.PreferenceKey);
        savedMode = PlayerPrefs.GetInt(VisitorPlays.PreferenceKey, int.MinValue);
        VisitorPlays.SetSongsPerVisitorForTest(2);
        VisitorPlays.BeginVisitor();
        GameSession.SetPlayerSettingsForTest(true);
    }

    [TearDown]
    public void TearDown()
    {
        GameSession.SongSelectOperated = savedOperated;
        GameSession.RushESecretCarry = savedSecretCarry;
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = savedBridge;
        ResultSelectionReturn.Remember(savedReturnSong, savedReturnDifficulty);
        GameSession.SetPlayerSettingsForTest(false);
        VisitorPlays.BeginVisitor();
        VisitorPlays.SetSongsPerVisitorForTest(1);
        AssertSavedModeUnchanged();
    }

    void AssertSavedModeUnchanged()
    {
        Assert.AreEqual(hadMode, PlayerPrefs.HasKey(VisitorPlays.PreferenceKey));
        Assert.AreEqual(savedMode, PlayerPrefs.GetInt(VisitorPlays.PreferenceKey, int.MinValue));
    }

    [Test]
    public void MissingPreferenceDefaultsToTwo()
    {
        Assert.AreEqual(2, VisitorPlays.ResolveSongsPerVisitor(null));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(0, 2)]
    [TestCase(-1, 2)]
    [TestCase(3, 2)]
    [TestCase(int.MaxValue, 2)]
    public void InvalidValuesUseTwo(int stored, int expected)
    {
        Assert.AreEqual(expected, VisitorPlays.ResolveSongsPerVisitor(stored));
        VisitorPlays.SetSongsPerVisitorForTest(stored);
        Assert.AreEqual(expected, VisitorPlays.SongsPerVisitor);
    }

    [Test]
    public void OverrideTogglesWithoutWritingAndNullRestoresTheSavedValue()
    {
        for (int i = 0; i < 4; i++)
        {
            VisitorPlays.ToggleSongsPerVisitor();
            Assert.AreEqual(i % 2 == 0 ? 1 : 2, VisitorPlays.SongsPerVisitor);
            AssertSavedModeUnchanged();
        }
        VisitorPlays.SetSongsPerVisitorForTest(null);
        int expected = VisitorPlays.ResolveSongsPerVisitor(
            PlayerPrefs.GetInt(VisitorPlays.PreferenceKey, 2));
        Assert.AreEqual(expected, VisitorPlays.SongsPerVisitor);
    }

    [Test]
    public void EachRunCountsOnceEvenAfterAnotherResult()
    {
        Assert.True(VisitorPlays.CompleteSong("first"));
        Assert.False(VisitorPlays.CompleteSong("first"));
        Assert.True(VisitorPlays.CompleteSong("second"));
        Assert.False(VisitorPlays.CompleteSong("first"));
        Assert.AreEqual(2, VisitorPlays.CompletedSongs);
    }

    [Test]
    public void MissingIdsShareOneCompletionPerVisitor()
    {
        Assert.True(VisitorPlays.CompleteSong(null));
        Assert.False(VisitorPlays.CompleteSong(""));
        Assert.False(VisitorPlays.CompleteSong(" "));
        Assert.AreEqual(1, VisitorPlays.CompletedSongs);
        Assert.True(VisitorPlays.CompleteSong("real-run"));
        Assert.AreEqual(2, VisitorPlays.CompletedSongs);
        VisitorPlays.BeginVisitor();
        Assert.True(VisitorPlays.CompleteSong(null));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void RemainingAndCurrentSongAreBounded(int songs)
    {
        VisitorPlays.SetSongsPerVisitorForTest(songs);
        Assert.True(VisitorPlays.HasNextSong);
        Assert.AreEqual(1, VisitorPlays.CurrentSongNumber);
        for (int i = 1; i <= 3; i++)
        {
            VisitorPlays.CompleteSong("run-" + i);
            Assert.AreEqual(i < songs, VisitorPlays.HasNextSong);
            Assert.AreEqual(Mathf.Min(i + 1, songs), VisitorPlays.CurrentSongNumber);
        }
        VisitorPlays.BeginVisitor();
        Assert.Zero(VisitorPlays.CompletedSongs);
        Assert.True(VisitorPlays.HasNextSong);
        Assert.True(VisitorPlays.CompleteSong("run-1"), "次の来場者ではIDの集合も空に戻る");
    }

    [Test]
    public void TitleAwakeStartsANewVisitor()
    {
        VisitorPlays.CompleteSong("previous");
        ResultSelectionReturn.Remember("Epilogue", "Hard");
        var go = new GameObject("VisitorTitleTest");
        try
        {
            var title = go.AddComponent<TitleMenuController>();
            // EditModeでは AddComponent で Awake が呼ばれず、SendMessage も使えないので、リフレクションで実際のメソッドを呼ぶ。
            typeof(TitleMenuController).GetMethod("Awake",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(title, null);
            Assert.Zero(VisitorPlays.CompletedSongs);
            Assert.AreEqual(1, VisitorPlays.CurrentSongNumber);
            ResultSelectionReturn.Consume(out var song, out var difficulty);
            Assert.IsNull(song);
            Assert.IsNull(difficulty);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void CopyMatchesBothModesAndBothSongNumbers()
    {
        Assert.AreEqual("1回で1曲 あそべる", VisitorModeBadge.ModeText(1));
        Assert.AreEqual("1回で2曲 あそべる", VisitorModeBadge.ModeText(2));
        Assert.AreEqual("1曲モード: 1人1曲まで", VisitorModeBadge.NoticeText(1));
        Assert.AreEqual("2曲モード: 1人2曲まで", VisitorModeBadge.NoticeText(2));
        Assert.AreEqual("1<ruby=きょくめ>曲目</ruby>", SongSelectSkin.VisitorProgressText(1));
        Assert.AreEqual("2<ruby=きょくめ>曲目</ruby>", SongSelectSkin.VisitorProgressText(2));
        Assert.AreEqual("あと1曲", ResultSkin.NextSongBadgeText);
    }

    [Test]
    public void ProgressDotsFillUpToTheCurrentSong()
    {
        Assert.AreEqual("● ○", SongSelectSkin.VisitorProgressDots(1, 2));
        Assert.AreEqual("● ●", SongSelectSkin.VisitorProgressDots(2, 2));
        Assert.AreEqual("●", SongSelectSkin.VisitorProgressDots(1, 1));
        Assert.AreEqual("● ○", SongSelectSkin.VisitorProgressDots(0, 2), "0 以下は1曲目として出す");
        Assert.AreEqual("● ●", SongSelectSkin.VisitorProgressDots(3, 2), "曲数を超えない");
    }

    [Test]
    public void TitleBadgeSitsInTheBottomLeftCornerOnOneLine()
    {
        var root = new GameObject("VisitorBadgeTest", typeof(RectTransform));
        try
        {
            var badge = VisitorModeBadge.Build(root.transform);
            var rt = (RectTransform)badge.transform;
            Assert.AreEqual(Vector2.zero, rt.anchorMin);
            Assert.AreEqual(Vector2.zero, rt.anchorMax);
            Assert.AreEqual(VisitorModeBadge.Margin, rt.anchoredPosition);
            var label = badge.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            Assert.AreEqual(VisitorModeBadge.ModeText(VisitorPlays.SongsPerVisitor), label.text);
            Assert.GreaterOrEqual(label.fontSize, 26f, "投影で読める大きさ");
            Assert.AreEqual(1, badge.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Length, "1行だけ");
        }
        finally { Object.DestroyImmediate(root); }
    }
}
