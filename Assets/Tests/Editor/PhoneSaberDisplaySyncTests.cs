using NUnit.Framework;
using UnityEngine;

public class PhoneSaberDisplaySyncTests
{
    [Test]
    public void FrameCapDefaultAndInvalidValuesNeverWriteTheGameTarget()
    {
        foreach (int mode in new[] { PhoneSaberFrameCapSettings.GameDefault, -1, 7 })
            foreach (int current in new[] { -1, 30, 120, 240 })
                Assert.AreEqual(-1, PhoneSaberFrameCapSettings.TargetFrameRateToApply(mode, 60, current));
        foreach (double hz in new[] { 0, -60, double.NaN, double.PositiveInfinity, double.MaxValue, .01 })
            Assert.AreEqual(-1, PhoneSaberFrameCapSettings.TargetFrameRateToApply(PhoneSaberFrameCapSettings.FourTimes, hz, -1));
    }

    [TestCase(60, 240, 120)]
    [TestCase(59.94, 240, 120)]
    [TestCase(30, 120, 60)]
    [TestCase(120, 480, 240)]
    [TestCase(144, 576, 288)]
    public void FrameCapUsesTheDisplayRateAndReappliesAfterGameWrites(double hz, int four, int two)
    {
        foreach (var pair in new[] { new[] { PhoneSaberFrameCapSettings.FourTimes, four },
            new[] { PhoneSaberFrameCapSettings.TwoTimes, two } })
        {
            Assert.AreEqual(pair[1], PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, -1));
            Assert.AreEqual(-1, PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, pair[1]));
            Assert.AreEqual(pair[1], PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, 0));
            Assert.AreEqual(pair[1], PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, pair[1] + 1));
            // ゲームが自分で低くした値（メニューの 30 fps など）は上げない。
            Assert.AreEqual(-1, PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, 30));
            Assert.AreEqual(-1, PhoneSaberFrameCapSettings.TargetFrameRateToApply(pair[0], hz, pair[1] - 1));
        }
    }

    [Test]
    public void FrameCapPersistsSeparatelyFromDisplaySyncAndOtherStations()
    {
        string a = PhoneSaberFrameCapSettings.Key("fcap-a"), b = PhoneSaberFrameCapSettings.Key("fcap-b");
        string sync = PhoneSaberDisplaySyncSettings.Key("fcap-a");
        PlayerPrefs.DeleteKey(a); PlayerPrefs.DeleteKey(b); PlayerPrefs.DeleteKey(sync);
        try
        {
            // 未設定の台は「画面の2倍」（上限なしの約1100 fps で Mac が熱くなるため）。
            Assert.AreEqual(PhoneSaberFrameCapSettings.TwoTimes, PhoneSaberFrameCapSettings.Load("fcap-a"));
            PhoneSaberFrameCapSettings.Save("fcap-a", PhoneSaberFrameCapSettings.GameDefault);
            Assert.AreEqual(PhoneSaberFrameCapSettings.GameDefault, PhoneSaberFrameCapSettings.Load("fcap-a"));
            PhoneSaberDisplaySyncSettings.Save("fcap-a", PhoneSaberDisplaySyncSettings.VSync);
            PhoneSaberFrameCapSettings.Save("fcap-a", PhoneSaberFrameCapSettings.TwoTimes);
            Assert.AreEqual(PhoneSaberFrameCapSettings.TwoTimes, PhoneSaberFrameCapSettings.Load("fcap-a"));
            Assert.AreEqual(PhoneSaberFrameCapSettings.TwoTimes, PhoneSaberFrameCapSettings.Load("fcap-b"));
            Assert.AreEqual(PhoneSaberDisplaySyncSettings.VSync, PhoneSaberDisplaySyncSettings.Load("fcap-a"));
            PlayerPrefs.SetInt(a, 7);
            Assert.AreEqual(PhoneSaberFrameCapSettings.GameDefault, PhoneSaberFrameCapSettings.Load("fcap-a"));
            PhoneSaberFrameCapSettings.Save("fcap-a", -1);
            Assert.AreEqual(PhoneSaberFrameCapSettings.GameDefault, PlayerPrefs.GetInt(a, -1));
        }
        finally { PlayerPrefs.DeleteKey(a); PlayerPrefs.DeleteKey(b); PlayerPrefs.DeleteKey(sync); }
    }

    [Test]
    public void DefaultLeavesTheGameSettingAlone()
    {
        string key = PhoneSaberDisplaySyncSettings.Key("dsync-new");
        PlayerPrefs.DeleteKey(key);
        Assert.AreEqual(PhoneSaberDisplaySyncSettings.GameDefault, PhoneSaberDisplaySyncSettings.Load("dsync-new"));
        foreach (int current in new[] { 0, 1, 2 })
            Assert.AreEqual(-1, PhoneSaberDisplaySyncSettings.VSyncCountToApply(PhoneSaberDisplaySyncSettings.GameDefault, current));
    }

    [Test]
    public void SlowDisplayNoticeShowsOnMenusButNeverCoversTheGame()
    {
        string slow = PhoneSaberStatsDisplay.DisplayWarning(30);
        Assert.IsNotEmpty(slow);
        StringAssert.Contains("30", PhoneSaberDisplayNotice.TextFor(slow, "Title"));
        StringAssert.Contains("低電力モード", PhoneSaberDisplayNotice.TextFor(slow, "SongSelect"));
        Assert.IsEmpty(PhoneSaberDisplayNotice.TextFor(slow, PhoneSaberDisplayNotice.GameSceneName));
        Assert.IsEmpty(PhoneSaberDisplayNotice.TextFor(PhoneSaberStatsDisplay.DisplayWarning(60), "Title"));
        Assert.IsEmpty(PhoneSaberDisplayNotice.TextFor(null, "Title"));
    }

    [Test]
    public void GameSceneNameMatchesTheBuildSettings()
    {
        bool found = false;
        foreach (var scene in UnityEditor.EditorBuildSettings.scenes)
            if (System.IO.Path.GetFileNameWithoutExtension(scene.path) == PhoneSaberDisplayNotice.GameSceneName) found = true;
        Assert.IsTrue(found, "本編の scene 名が変わったら PhoneSaberDisplayNotice.GameSceneName も直す");
    }

    [Test]
    public void VSyncModeWritesOneOnlyWhenTheGameChangedIt()
    {
        Assert.AreEqual(1, PhoneSaberDisplaySyncSettings.VSyncCountToApply(PhoneSaberDisplaySyncSettings.VSync, 0));
        Assert.AreEqual(1, PhoneSaberDisplaySyncSettings.VSyncCountToApply(PhoneSaberDisplaySyncSettings.VSync, 2));
        Assert.AreEqual(-1, PhoneSaberDisplaySyncSettings.VSyncCountToApply(PhoneSaberDisplaySyncSettings.VSync, 1));
    }

    [Test]
    public void UnknownStoredValuesFallBackToTheGameSettingAndStationsAreSeparate()
    {
        Assert.AreEqual(PhoneSaberDisplaySyncSettings.GameDefault, PhoneSaberDisplaySyncSettings.Clamp(7));
        Assert.AreEqual(PhoneSaberDisplaySyncSettings.GameDefault, PhoneSaberDisplaySyncSettings.Clamp(-1));
        Assert.AreNotEqual(PhoneSaberDisplaySyncSettings.Key("A"), PhoneSaberDisplaySyncSettings.Key("B"));
        // 台名は16文字まで（PhoneSaberStation.Normalize）。長い名前は「台なし」と同じ鍵になる。
        Assert.AreNotEqual(PhoneSaberDisplaySyncSettings.Key("dsync-a"), PhoneSaberDisplaySyncSettings.Key("dsync-b"));
        string key = PhoneSaberDisplaySyncSettings.Key("dsync-a");
        try
        {
            PhoneSaberDisplaySyncSettings.Save("dsync-a", PhoneSaberDisplaySyncSettings.VSync);
            Assert.AreEqual(PhoneSaberDisplaySyncSettings.VSync, PhoneSaberDisplaySyncSettings.Load("dsync-a"));
            Assert.AreEqual(PhoneSaberDisplaySyncSettings.GameDefault, PhoneSaberDisplaySyncSettings.Load("dsync-b"));
        }
        finally { PlayerPrefs.DeleteKey(key); }
    }
}
