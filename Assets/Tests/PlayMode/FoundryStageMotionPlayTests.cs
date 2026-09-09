using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class FoundryStageMotionPlayTests
{
    private static void ForceFoundry(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "Game") return;
        var stage = new GameObject("TestFoundry").AddComponent<FloorRenderer>();
        stage.randomizeOnPlay = false;
        stage.Build(StageTheme.AmberFoundry);
    }

    [UnityTest]
    public IEnumerator GameDriverMovesEquipmentWithSongClockAndStopsWithIt()
    {
        GameSession.SelectedSongId = "ElDorado";
        GameSession.SelectedDifficulty = "normal";
        GameSession.IsCalibrationMode = false;
        SceneManager.sceneLoaded += ForceFoundry;
        try { yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single); }
        finally { SceneManager.sceneLoaded -= ForceFoundry; }
        float deadline = Time.realtimeSinceStartup + 25;
        FoundryStageMotion motion = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            motion = Object.FindFirstObjectByType<FoundryStageMotion>();
            if (motion != null && motion.LastTickSeconds > .4) break;
            yield return null;
        }
        Assert.IsNotNull(motion);
        Assert.Greater(motion.LastTickSeconds, .4, "GamePlayManager が背景を駆動する");
        var player = Object.FindFirstObjectByType<SongPlayer>();
        Assert.That(motion.LastTickSeconds, Is.EqualTo(player.SongTime).Within(.15));
        Assert.LessOrEqual(motion.LiveParticleCount, 96);
        player.Stop();
        double stopped = motion.LastTickSeconds;
        yield return new WaitForSeconds(.15f);
        Assert.AreEqual(stopped, motion.LastTickSeconds);
        Assert.IsEmpty(motion.GetComponentsInChildren<Collider>());
    }
}
