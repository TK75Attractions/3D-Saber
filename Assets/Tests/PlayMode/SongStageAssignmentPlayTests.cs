using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class SongStageAssignmentPlayTests
{
    [UnityTest]
    [Timeout(240000)]
    public IEnumerator AllSongsBuildTheirAssignedWorldAndReplayKeepsIt()
    {
        string oldSong=GameSession.SelectedSongId, oldDifficulty=GameSession.SelectedDifficulty;
        bool oldCalibration=GameSession.IsCalibrationMode, oldTutorial=GameSession.TutorialPending;
        try
        {
            var songs=new[]{"ElDorado","2_23_AM","Aobasou","BenkyouRemaster","Andalusia","Epilogue",
                "escapade","logic","ManicSonic","Morning","Phobia PARTⅠ","RushE","揺籠","製作中","ElDorado","Aobasou"};
            for (int index=0;index<songs.Length;index++)
            {
                string id=songs[index];
                GameSession.SelectedSongId=id; GameSession.SelectedDifficulty=index<14?"Normal":"Hard";
                GameSession.IsCalibrationMode=false; GameSession.TutorialPending=false;
                yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single);
                var manager=Object.FindFirstObjectByType<GamePlayManager>(); Assert.NotNull(manager);
                float deadline=Time.realtimeSinceStartup+25;
                while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup<deadline) yield return null;
                Assert.IsTrue(manager.songPlayer.IsScheduled,id);
                var floor=Object.FindFirstObjectByType<FloorRenderer>(); Assert.NotNull(floor,id);
                Assert.AreEqual(StageThemeCatalog.ForSong(id),floor.ActiveTheme,id);
                Assert.AreEqual(id=="Morning",manager.noteSpawner.outlineLeftNotes,id+": 青ノーツの暗い縁は明るい空の背景だけ");
                if (id=="ElDorado") Assert.NotNull(floor.GetComponentInChildren<FoundryStageMotion>());
                if ((int)floor.ActiveTheme>=14)
                {
                    var world=floor.GetComponentInChildren<ScenicStageWorld>(); Assert.NotNull(world,id);
                    deadline=Time.realtimeSinceStartup+10;
                    while(world.LastTickSeconds<.15 && Time.realtimeSinceStartup<deadline) yield return null;
                    Assert.Greater(world.LastTickSeconds,.1,id);
                    Assert.That(world.LastTickSeconds,Is.EqualTo(manager.songPlayer.SongTime).Within(.15),id);
                    manager.songPlayer.Stop(); double stopped=world.LastTickSeconds;
                    yield return new WaitForSeconds(.10f);
                    Assert.AreEqual(stopped,world.LastTickSeconds,id);
                }
                manager.songPlayer.Stop();
            }
        }
        finally
        {
            GameSession.SelectedSongId=oldSong; GameSession.SelectedDifficulty=oldDifficulty;
            GameSession.IsCalibrationMode=oldCalibration; GameSession.TutorialPending=oldTutorial;
        }
    }
}
