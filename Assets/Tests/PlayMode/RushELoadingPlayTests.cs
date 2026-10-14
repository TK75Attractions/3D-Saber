using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class RushELoadingPlayTests
{
    [UnityTest]
    public IEnumerator EachDifficultyStartsInTheRealGameWithoutTrimmingNotes()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav")))
            Assert.Ignore("文化祭用の音源をローカルに導入した端末で実行します。");
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty;
        bool calibration = GameSession.IsCalibrationMode;
        try
        {
            foreach (string level in new[] { "Easy", "Normal", "Hard" })
            {
                GameSession.SelectedSongId = "RushE"; GameSession.SelectedDifficulty = level; GameSession.IsCalibrationMode = false;
                yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
                var manager = Object.FindFirstObjectByType<GamePlayManager>();
                Assert.IsNotNull(manager);
                float deadline = Time.realtimeSinceStartup + 25;
                while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(manager.songPlayer.IsScheduled, level);
                Assert.IsNotNull(manager.songPlayer.Clip, level);
                var chart = ChartLoader.LoadFromStreamingAssets("RushE", level);
                Assert.AreEqual(chart.notes.Count, manager.noteSpawner.TotalNoteCount, "末尾切り詰めなし: " + level);
                Assert.Greater(manager.songPlayer.Duration, 144);
                manager.songPlayer.Stop();
            }
        }
        finally
        {
            GameSession.SelectedSongId = song; GameSession.SelectedDifficulty = difficulty; GameSession.IsCalibrationMode = calibration;
        }
    }
}
