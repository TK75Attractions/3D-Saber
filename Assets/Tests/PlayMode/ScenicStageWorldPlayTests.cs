using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class ScenicStageWorldPlayTests
{
    private static StageTheme nextTheme;
    private static void ForceWorld(Scene scene,LoadSceneMode mode)
    {
        if(scene.name!="Game") return;
        var stage = new GameObject("TestWorld").AddComponent<FloorRenderer>();
        stage.randomizeOnPlay=false; stage.Build(nextTheme);
    }

    [UnityTest]
    public IEnumerator AllSixWorldsAreDrivenByActualGameAndStopWithSong()
    {
        GameSession.SelectedSongId="ElDorado"; GameSession.SelectedDifficulty="normal"; GameSession.IsCalibrationMode=false;
        for(int theme=4;theme<10;theme++)
        {
            nextTheme=(StageTheme)theme; SceneManager.sceneLoaded+=ForceWorld;
            try { yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single); }
            finally { SceneManager.sceneLoaded-=ForceWorld; }
            float deadline=Time.realtimeSinceStartup+25; ScenicStageWorld world=null;
            while(Time.realtimeSinceStartup<deadline)
            {
                world=Object.FindFirstObjectByType<ScenicStageWorld>();
                if(world!=null && world.LastTickSeconds>.2) break;
                yield return null;
            }
            Assert.IsNotNull(world); Assert.AreEqual(nextTheme,world.Theme); Assert.Greater(world.LastTickSeconds,.2);
            var player=Object.FindFirstObjectByType<SongPlayer>();
            Assert.That(world.LastTickSeconds,Is.EqualTo(player.SongTime).Within(.15));
            player.Stop(); double stopped=world.LastTickSeconds;
            yield return new WaitForSeconds(.10f);
            Assert.AreEqual(stopped,world.LastTickSeconds);
        }
    }
}
