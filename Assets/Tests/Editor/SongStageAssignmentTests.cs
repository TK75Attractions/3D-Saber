using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class SongStageAssignmentTests
{
    [Test]
    public void EveryPlayableSongHasAnExplicitUniqueBackground()
    {
        var assigned = new Dictionary<StageTheme,string>();
        // 音源を別途導入する曲も含め、譜面がある全曲を検査する。
        foreach (string dir in Directory.GetDirectories(Path.Combine(Application.streamingAssetsPath,"Songs")))
        {
            string song = Path.GetFileName(dir);
            if (!SongSelectController.HasPlayableChart(song,new[]{"Easy","Normal","Hard"})) continue;
            var data = StagePerformanceTimeline.Load(song);
            Assert.IsFalse(string.IsNullOrEmpty(data.stageTheme), song+" の背景指定がありません");
            Assert.IsTrue(Enum.TryParse(data.stageTheme,out StageTheme theme) && StageThemeCatalog.IsDefined(theme),song);
            Assert.IsFalse(assigned.ContainsKey(theme), song+" の背景が他曲と重複しています: "+theme);
            assigned.Add(theme,song);
            Assert.AreEqual(theme,StageThemeCatalog.ForSong(song));
        }
        Assert.GreaterOrEqual(assigned.Count,14);
    }

    [Test]
    public void ReplayDifficultyAndOtherSongsNeverChangeTheAssignmentOrGameplayRandom()
    {
        string original=GameSession.SelectedDifficulty;
        var state=UnityEngine.Random.state;
        try
        {
            foreach (var id in new[]{"2_23_AM","Aobasou","BenkyouRemaster","ElDorado"})
            {
                StageTheme expected=StageThemeCatalog.ForSong(id);
                foreach (string difficulty in new[]{"Easy","Normal","Hard","E","Normal"})
                {
                    GameSession.SelectedDifficulty=difficulty;
                    StageThemeCatalog.ForSong("Andalusia");
                    Assert.AreEqual(expected,StageThemeCatalog.ForSong(id));
                }
            }
            Assert.AreEqual(state,UnityEngine.Random.state);
        }
        finally { GameSession.SelectedDifficulty=original; }
    }

    [Test]
    public void GoldSongAndDirectGameLaunchUseTheFoundry()
    {
        Assert.AreEqual(StageTheme.AmberFoundry,StageThemeCatalog.ForSong("ElDorado"));
        Assert.AreEqual(StageTheme.AmberFoundry,StageThemeCatalog.ForSong(null));
        Assert.AreEqual(StageTheme.AmberFoundry,StageThemeCatalog.ForSong(""));
    }

    [Test]
    public void MissingSongUsesAStableFallbackAndRemovedIdsStayUnavailable()
    {
        Assert.AreEqual(StageTheme.ObsidianRelay,StageThemeCatalog.ForSong("__MissingSong__"));
        CollectionAssert.AreEquivalent(Enum.GetValues(typeof(StageTheme)),StageThemeCatalog.All);
        Assert.AreEqual(14,StageThemeCatalog.Count);
        foreach (int removed in new[]{-1,11,12,13,17}) Assert.IsFalse(StageThemeCatalog.IsDefined((StageTheme)removed));
        foreach (StageTheme theme in StageThemeCatalog.All)
            Assert.IsFalse(string.IsNullOrEmpty(StageThemeCatalog.DisplayName(theme)));
    }
}
