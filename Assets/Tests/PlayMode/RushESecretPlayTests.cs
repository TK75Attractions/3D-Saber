using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class RushESecretPlayTests
{
    [UnityTest]
    public IEnumerator JacketClicksRevealOnlyEThenSongChangeRestoresNormalMenu()
    {
        if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "Songs", "RushE", "audio.wav"))) Assert.Ignore("文化祭用ローカル音源が必要です。");
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var skin = Object.FindFirstObjectByType<SongSelectSkin>();
        float deadline = Time.realtimeSinceStartup + 15;
        while (!skin.IsReady && Time.realtimeSinceStartup < deadline) yield return null;
        Assert.IsTrue(skin.IsReady);
        Object.FindFirstObjectByType<SongSelectAimPointer>().enabled = false;
        int rush = Enumerable.Range(0, ctl.SongCount).First(i => ctl.SongIdAt(i) == "RushE");
        ctl.Select(rush); ctl.SetDifficulty(2);
        yield return new WaitForSecondsRealtime(.8f);
        while (ScreenTransition.IsBusy || !skin.DiscsSettled) yield return null;
        for (int i = 0; i < 9; i++) { ctl.startButton.onClick.Invoke(); yield return new WaitForSecondsRealtime(.25f); }
        Assert.AreEqual(9, ctl.RushESecret.Hits); Assert.IsFalse(ctl.RushESecret.Unlocked);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name, "ジャケットを切っても開始しない");
        ctl.startButton.onClick.Invoke(); Assert.IsTrue(ctl.RushESecret.Transitioning);
        double remaining = skin.RemainingSeconds; skin.TickCountdown(500);
        Assert.AreEqual(remaining, skin.RemainingSeconds); ctl.StartGame();
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        yield return new WaitForSecondsRealtime(.4f);
        var curtain = Object.FindFirstObjectByType<RushETransitionGraphic>();
        Assert.IsNotNull(curtain, "切替中にタイルが有効になる");
        Canvas.ForceUpdateCanvases();
        Assert.IsNotNull(curtain.canvasRenderer, "タイルの描画コンポーネントが必要");
        Assert.Greater(curtain.canvasRenderer.GetMesh().vertexCount, 0, "四角形の描画メッシュが作られる");
        yield return new WaitForSecondsRealtime(1.6f);
        Assert.IsTrue(ctl.RushESecret.Unlocked); Assert.IsFalse(ctl.RushESecret.Transitioning);
        Assert.AreEqual("E", ctl.CurrentDifficultyName); Assert.AreEqual(10, ctl.CurrentDifficultyDisplayLevel());
        Assert.IsTrue(skin.Background.SecretTheme); Assert.IsNotNull(GameObject.Find("RushEOnlyDifficulty"));
        Assert.IsTrue(ctl.difficultyButtons.All(b => !b.gameObject.activeSelf)); Assert.IsTrue(ctl.startButton.interactable);
        ctl.ChangeDifficulty(1); ctl.SetDifficulty(0); Assert.AreEqual("E", ctl.CurrentDifficultyName);
        ctl.Select((rush + 1) % ctl.SongCount);
        Assert.IsFalse(ctl.RushESecret.Unlocked); Assert.AreEqual(0, ctl.RushESecret.Hits); Assert.IsFalse(skin.Background.SecretTheme);
        Assert.IsTrue(ctl.difficultyButtons.All(b => b.gameObject.activeSelf));
        ctl.Select(rush); Assert.AreEqual("Hard", ctl.CurrentDifficultyName); Assert.AreEqual(0, ctl.RushESecret.Hits);
        ctl.StopPreview();
    }
}
