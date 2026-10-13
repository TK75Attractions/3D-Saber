using NUnit.Framework;
using UnityEngine;

public class PhoneSaberDisplaySyncTests
{
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
