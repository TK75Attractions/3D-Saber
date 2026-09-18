using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class StageReactiveEffectsPlayTests
{
    [UnityTest]
    public IEnumerator GameConnectsStageReactionsAndUsesTheSameSongClock()
    {
        string originalSong = GameSession.SelectedSongId, originalDifficulty = GameSession.SelectedDifficulty;
        bool originalCalibration = GameSession.IsCalibrationMode;
        try
        {
            GameSession.SelectedSongId = "Epilogue"; GameSession.SelectedDifficulty = "easy";
            GameSession.IsCalibrationMode = false;
            yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            float deadline = Time.realtimeSinceStartup + 25;
            while (!manager.songPlayer.IsPlaying && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.True(manager.songPlayer.IsPlaying);
            var effect = Object.FindFirstObjectByType<StageReactiveEffects>(); Assert.NotNull(effect);
            var timeline = StagePerformanceTimeline.Load("Epilogue");
            double entrance = timeline.sections[0].startSeconds;
            var clock = typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic);
            clock.SetValue(manager.songPlayer, AudioSettings.dspTime - entrance - .06);
            yield return null; yield return null;
            Assert.That(effect.LastTickSeconds, Is.EqualTo(manager.songPlayer.SongTime).Within(.1));
            Assert.Greater(effect.Presentation.impact, .1f);
            manager.songPlayer.Stop();
            double time = effect.LastTickSeconds; yield return new WaitForSeconds(.05f);
            Assert.AreEqual(time, effect.LastTickSeconds);
            var mesh = effect.GetComponent<MeshFilter>().sharedMesh;
            var material = effect.GetComponent<MeshRenderer>().sharedMaterial;
            Object.Destroy(effect.gameObject); yield return null; yield return null;
            Assert.True(mesh == null); Assert.True(material == null);
        }
        finally
        {
            GameSession.SelectedSongId = originalSong; GameSession.SelectedDifficulty = originalDifficulty;
            GameSession.IsCalibrationMode = originalCalibration;
        }
    }
}
