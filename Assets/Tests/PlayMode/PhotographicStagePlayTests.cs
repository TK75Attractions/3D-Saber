using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class PhotographicStagePlayTests
{
    private static StageTheme selected;
    private string originalSong, originalDifficulty;
    private bool originalCalibration;
    [SetUp] public void SaveSession()
    {
        originalSong=GameSession.SelectedSongId;
        originalDifficulty=GameSession.SelectedDifficulty;
        originalCalibration=GameSession.IsCalibrationMode;
    }
    [TearDown] public void RestoreSession()
    {
        SceneManager.sceneLoaded-=ForceBackground;
        GameSession.SelectedSongId=originalSong;
        GameSession.SelectedDifficulty=originalDifficulty;
        GameSession.IsCalibrationMode=originalCalibration;
    }
    private static void ForceBackground(Scene scene, LoadSceneMode mode)
    {
        if(scene.name!="Game") return;
        var floor = new GameObject("PhotoStagePlayTest").AddComponent<FloorRenderer>();
        floor.randomizeOnPlay=false;
        floor.Build(selected);
    }

    [UnityTest]
    public IEnumerator ThreeBackgroundsFollowRealGameplayAndFreezeWhenSongStops()
    {
        GameSession.SelectedSongId="ElDorado";
        GameSession.SelectedDifficulty="normal";
        GameSession.IsCalibrationMode=false;
        foreach(var theme in new[]{StageTheme.AuroraLake,StageTheme.RainyCity,StageTheme.SunlitOcean})
        {
            selected=theme;
            SceneManager.sceneLoaded+=ForceBackground;
            try { yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single); }
            finally { SceneManager.sceneLoaded-=ForceBackground; }
            float deadline=Time.realtimeSinceStartup+25;
            PhotographicStage stage=null;
            while(Time.realtimeSinceStartup<deadline)
            {
                stage=Object.FindFirstObjectByType<PhotographicStage>();
                if(stage!=null && stage.LastTickSeconds>.25) break;
                yield return null;
            }
            Assert.NotNull(stage);
            Assert.AreEqual(theme,stage.Theme);
            Assert.Greater(stage.LastTickSeconds,.25);
            var song=Object.FindFirstObjectByType<SongPlayer>();
            Assert.That(stage.LastTickSeconds,Is.EqualTo(song.SongTime).Within(.15));
            song.Stop();
            double stopped=stage.LastTickSeconds;
            yield return new WaitForSeconds(.15f);
            Assert.AreEqual(stopped,stage.LastTickSeconds);
        }
    }
}
