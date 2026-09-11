using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class MetalStageDynamicsPlayTests
{
    private static StageTheme theme;
    private static void ForceStage(Scene scene,LoadSceneMode mode)
    {
        if(scene.name!="Game") return;
        var stage=new GameObject("MetalMotionTest").AddComponent<FloorRenderer>(); stage.randomizeOnPlay=false; stage.Build(theme);
    }
    [UnityTest]
    public IEnumerator ActualGameDrivesMetalFloorClockAndFreezesWhenAudioStops()
    {
        GameSession.SelectedSongId="ElDorado"; GameSession.SelectedDifficulty="normal"; GameSession.IsCalibrationMode=false;
        foreach(var selected in new[]{StageTheme.ObsidianRelay,StageTheme.VioletVault,StageTheme.AzurePrism})
        {
            theme=selected; SceneManager.sceneLoaded+=ForceStage;
            try { yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single); }
            finally { SceneManager.sceneLoaded-=ForceStage; }
            FloorRenderer stage=null; float deadline=Time.realtimeSinceStartup+25;
            while(Time.realtimeSinceStartup<deadline)
            {
                stage=Object.FindFirstObjectByType<FloorRenderer>();
                if(stage!=null && stage.LastTickSeconds>.2) break;
                yield return null;
            }
            Assert.IsNotNull(stage); Assert.AreEqual(theme,stage.ActiveTheme); Assert.Greater(stage.LastTickSeconds,.2);
            var player=Object.FindFirstObjectByType<SongPlayer>(); Assert.That(stage.LastTickSeconds,Is.EqualTo(player.SongTime).Within(.15));
            player.Stop(); double stopped=stage.LastTickSeconds;
            yield return new WaitForSeconds(.1f); Assert.AreEqual(stopped,stage.LastTickSeconds);
        }
    }
}
