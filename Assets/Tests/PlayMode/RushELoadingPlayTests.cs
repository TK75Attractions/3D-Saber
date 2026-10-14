using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class RushELoadingPlayTests
{
    [UnityTest]
    public IEnumerator EBeatSaberGridLongsAndFinalChordRenderInTheGame()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav")))
            Assert.Ignore("文化祭用ローカル音源が必要です。");
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty;
        bool calibration = GameSession.IsCalibrationMode, tutorial = GameSession.TutorialPending;
        bool reduced = DisplaySettings.ReducedEffects;
        try
        {
            GameSession.SelectedSongId = "RushE"; GameSession.SelectedDifficulty = "E";
            GameSession.IsCalibrationMode = GameSession.TutorialPending = false;
            yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            double deadline = Time.realtimeSinceStartupAsDouble + 25;
            while (!manager.songPlayer.IsScheduled && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.IsTrue(manager.songPlayer.IsScheduled);
            Assert.AreEqual(1158, manager.noteSpawner.TotalNoteCount);
            manager.enabled = false; manager.songPlayer.Stop();
            foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
            foreach (var countdown in Object.FindObjectsByType<GameStartCountdown>(FindObjectsSortMode.None)) Object.Destroy(countdown.gameObject);
            yield return null;
            var chart = ChartLoader.LoadFromStreamingAssets("RushE", "E");
            var spawner = manager.noteSpawner; spawner.approachTime = 1; spawner.SetExtraOffsetSeconds(0);
            foreach (bool low in new[] { false, true })
            {
                DisplaySettings.SetReducedEffectsForTest(low);
                foreach (double time in new[] { 78.45, 128.0, 136.281 })
                {
                    // その瞬間に見える部分だけを実ノーツとして描く。過去ノーツのMiss演出を混ぜない。
                    spawner.SetChart(new ChartData { bpm = chart.bpm, notes = chart.notes.Where(n => n.TimeSeconds >= time - .2 && n.TimeSeconds <= time + 1).ToList() });
                    spawner.Tick(time);
                    yield return null;
                    Assert.Greater(spawner.AliveCount, 0);
                    Assert.IsTrue(spawner.LiveNotes.All(n => n.RequiredDirection == CutDirection.None));
                    if (time < 79)
                    {
                        var longs = spawner.LiveNotes.Where(n => n.RequiredCutCount > 1).ToArray();
                        Assert.AreEqual(2, longs.Length, "両手ロングを実際に生成する");
                        Assert.IsTrue(longs.All(n => n.countLabel != null && n.countLabel.gameObject.activeInHierarchy));
                    }
                    if (time > 136) Assert.AreEqual(4, spawner.AliveCount, "最後の上下2段の両手同時を表示する");
                    UICatalogPlayTests.Capture($"rush-e-{(low ? "low" : "normal")}-{time:F3}");
                }
            }
        }
        finally
        {
            GameSession.SelectedSongId = song; GameSession.SelectedDifficulty = difficulty;
            GameSession.IsCalibrationMode = calibration; GameSession.TutorialPending = tutorial;
            DisplaySettings.SetReducedEffectsForTest(reduced);
        }
    }

    [UnityTest]
    public IEnumerator EachDifficultyStartsInTheRealGameWithoutTrimmingNotes()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav")))
            Assert.Ignore("文化祭用の音源をローカルに導入した端末で実行します。");
        string song = GameSession.SelectedSongId, difficulty = GameSession.SelectedDifficulty;
        bool calibration = GameSession.IsCalibrationMode;
        try
        {
            foreach (string level in new[] { "Easy", "Normal", "Hard", "E" })
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
