using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// スタートの演出(派手さカタログ 盤3)の実画面の流れ。試聴は止めずに下げていき、幕が閉じきったら止める。
public class FlourishStartPlayTests
{
    SongSelectController controller;
    SongSelectSkin skin;

    [UnityTest]
    public IEnumerator StartFadesThePreviewInsteadOfSilencingItAndCutsTheJacket()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        float deadline = Time.realtimeSinceStartup + 15;
        while (Time.realtimeSinceStartup < deadline)
        {
            controller = Object.FindFirstObjectByType<SongSelectController>();
            skin = Object.FindFirstObjectByType<SongSelectSkin>();
            if (controller != null && skin != null && skin.IsReady) break;
            yield return null;
        }
        Assert.IsTrue(skin != null && skin.IsReady);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        foreach (var aim in Object.FindObjectsByType<SongSelectAimPointer>(FindObjectsSortMode.None)) aim.enabled = false;
        for (int i = 0; i < controller.SongCount && !controller.startButton.interactable; i++) { controller.Select(i); controller.SetDifficulty(0); }
        Assert.IsTrue(controller.startButton.interactable);
        deadline = Time.realtimeSinceStartup + 10;
        while (Time.realtimeSinceStartup < deadline && !(controller.previewSource.isPlaying && controller.previewSource.volume > 0)) yield return null;
        Assert.IsTrue(controller.previewSource.isPlaying, "試聴が鳴っている状態から始める");
        float before = controller.previewSource.volume;

        controller.StartGame();
        Assert.IsTrue(ScreenTransition.IsBusy);
        Assert.AreEqual(before, controller.previewSource.volume, before * .05f, "決めた瞬間に試聴を無音にしない");
        Assert.GreaterOrEqual(skin.StartAge, 0f, "ジャケットを斬る演出が始まる");
        yield return new WaitForSecondsRealtime(.25f);
        Assert.IsTrue(controller.previewSource.isPlaying, "幕が閉じきるまでは鳴らしたまま下げる");
        Assert.Less(controller.previewSource.volume, before);
        Assert.Greater(controller.previewSource.volume, 0f);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        yield return ScreenTransitionPlayTests.WaitForTransition();
    }
}
