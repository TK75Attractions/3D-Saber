using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class FiveSongLoadingPlayTests
{
    [UnityTest]
    public IEnumerator ActualGameStartsEachNewSongWithoutTrimmingItsChart()
    {
        string song=GameSession.SelectedSongId,difficulty=GameSession.SelectedDifficulty;
        bool calibration=GameSession.IsCalibrationMode;
        try
        {
            foreach(string id in new[]{"ManicSonic","escapade","logic","BenkyouRemaster","Aobasou"})
            {
                GameSession.SelectedSongId=id;GameSession.SelectedDifficulty="Hard";GameSession.IsCalibrationMode=false;
                yield return SceneManager.LoadSceneAsync("Game",LoadSceneMode.Single);
                var manager=Object.FindFirstObjectByType<GamePlayManager>();
                Assert.IsNotNull(manager);
                float deadline=Time.realtimeSinceStartup+25;
                while(!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup<deadline)yield return null;
                Assert.IsTrue(manager.songPlayer.IsScheduled,id);
                Assert.IsNotNull(manager.songPlayer.Clip,id);
                var chart=ChartLoader.LoadFromStreamingAssets(id,"hard");
                Assert.AreEqual(chart.notes.Count,manager.noteSpawner.TotalNoteCount,id+" / 末尾切り詰めなし");
                foreach(var section in StagePerformanceTimeline.Load(id).sections)
                    Assert.Less(section.endSeconds,manager.songPlayer.Duration,id);
                manager.songPlayer.Stop();
            }
        }
        finally
        {
            GameSession.SelectedSongId=song;GameSession.SelectedDifficulty=difficulty;GameSession.IsCalibrationMode=calibration;
        }
    }
}
