using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 実際のStartGame経路で入力の所有権と楽曲の開始タイミングを検証する。
public class HardIntroPlayTests
{
    readonly Dictionary<FieldInfo, object> saved = new Dictionary<FieldInfo, object>();
    [SetUp] public void SaveSession()
    {
        foreach (var f in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!f.IsLiteral && !f.IsInitOnly) saved[f] = f.GetValue(null);
        GameSession.IsCalibrationMode = false; GameSession.TutorialPending = false;
        DisplaySettings.SetReducedEffectsForTest(false);
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        var owner = Object.FindFirstObjectByType<ScreenTransition>(FindObjectsInactive.Include);
        if (owner != null) Object.Destroy(owner.gameObject);
        yield return null;
        var current = SceneManager.GetActiveScene(); var empty = SceneManager.CreateScene("HardIntroCleanup");
        SceneManager.SetActiveScene(empty); if (current.IsValid() && current.isLoaded) yield return SceneManager.UnloadSceneAsync(current);
        foreach (var pair in saved) pair.Key.SetValue(null, pair.Value); saved.Clear();
        DisplaySettings.ResetReducedEffectsCacheForTest(); Time.timeScale = 1;
    }
    static IEnumerator OpenSelection(string songId = "Epilogue")
    {
        yield return SceneManager.LoadSceneAsync("SongSelect"); yield return null; yield return null;
        var select = Object.FindFirstObjectByType<SongSelectController>();
        for (int i = 0; i < select.SongCount; i++) if (select.SongIdAt(i) == songId) { select.Select(i); break; }
        Assert.AreEqual(songId, select.SongIdAt(select.SelectedIndex));
    }
    static IEnumerator WaitUntilReady(float timeout = 40)
    {
        double end = Time.realtimeSinceStartupAsDouble + timeout;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < end) yield return null;
        Assert.False(ScreenTransition.IsBusy, "演出後に入力ロックを残さない");
    }
    [UnityTest] public IEnumerator HardRunsBeforeSongAndRejectsRepeatedStart()
    {
        yield return OpenSelection();
        var select = Object.FindFirstObjectByType<SongSelectController>(); select.SetDifficulty(2);
        select.StartGame(); Assert.True(ScreenTransition.IsBusy); Assert.True(ScreenTransition.IsHardIntro);
        var view = Object.FindFirstObjectByType<HardSongIntro>();
        Assert.True(view.UsesOriginalTitle); Assert.AreEqual(10, view.Level);
        Assert.False(EventSystem.current != null && EventSystem.current.enabled);
        select.StartGame(); Assert.False(ScreenTransition.LoadGame("Game", "ElDorado", "EL DORADO", 9, "hard"));
        Assert.AreEqual("Epilogue", GameSession.SelectedSongId);
        Assert.False(select.previewSource.isPlaying);
        bool sawGame = false, heardSignal = false;
        double limit = Time.realtimeSinceStartupAsDouble + 40;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < limit)
        {
            heardSignal |= view != null && view.IsSignalPlaying;
            var manager = Object.FindFirstObjectByType<GamePlayManager>();
            if (SceneManager.GetActiveScene().name == "Game" && manager != null)
            {
                sawGame = true; Assert.False(manager.songPlayer.IsScheduled, "PVが隠している間に曲を開始しない");
            }
            yield return null;
        }
        Assert.False(ScreenTransition.IsBusy); Assert.True(sawGame); Assert.True(heardSignal);
        yield return null; Assert.IsNull(Object.FindFirstObjectByType<HardSongIntro>());
        var game = Object.FindFirstObjectByType<GamePlayManager>();
        double end = Time.realtimeSinceStartupAsDouble + 10;
        while (!game.songPlayer.IsScheduled && Time.realtimeSinceStartupAsDouble < end) yield return null;
        Assert.True(game.songPlayer.IsScheduled); Assert.True(EventSystem.current.enabled);
    }
    [UnityTest] public IEnumerator EasyAndNormalKeepTheOrdinaryTransition()
    {
        for (int difficulty = 0; difficulty < 2; difficulty++)
        {
            yield return OpenSelection(); var select = Object.FindFirstObjectByType<SongSelectController>();
            select.SetDifficulty(difficulty); select.StartGame();
            Assert.True(ScreenTransition.IsBusy); Assert.False(ScreenTransition.IsHardIntro);
            Assert.IsNull(Object.FindFirstObjectByType<HardSongIntro>());
            yield return WaitUntilReady(15); Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        }
    }
    [UnityTest] public IEnumerator OtherSongsHardUsesTheOrdinaryTransition()
    {
        foreach (string songId in new[] { "Andalusia", "ElDorado" })
        {
            yield return OpenSelection(songId);
            var select = Object.FindFirstObjectByType<SongSelectController>();
            select.SetDifficulty(2); select.StartGame();
            Assert.True(ScreenTransition.IsBusy); Assert.False(ScreenTransition.IsHardIntro);
            Assert.IsNull(Object.FindFirstObjectByType<HardSongIntro>(), songId + "に校歌の演出を出さない");
            Assert.AreEqual(songId, GameSession.SelectedSongId);
            yield return WaitUntilReady(15);
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
            Assert.IsNull(Object.FindFirstObjectByType<HardSongIntro>());
        }
    }
    [UnityTest] public IEnumerator InterruptedIntroStopsAudioAndRestoresOnlyEnabledInput()
    {
        yield return OpenSelection();
        var input = EventSystem.current;
        var disabled = new GameObject("AlreadyDisabledInput").AddComponent<EventSystem>(); disabled.enabled = false;
        var select = Object.FindFirstObjectByType<SongSelectController>(); select.SetDifficulty(2); select.StartGame();
        yield return new WaitForSecondsRealtime(.25f);
        var source = Object.FindFirstObjectByType<HardSongIntro>().GetComponent<AudioSource>(); Assert.True(source.isPlaying);
        Object.Destroy(Object.FindFirstObjectByType<ScreenTransition>().gameObject); yield return null;
        Assert.False(ScreenTransition.IsBusy); Assert.True(input.enabled); Assert.False(disabled.enabled);
        Assert.True(source == null || !source.isPlaying); Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
    }
}
