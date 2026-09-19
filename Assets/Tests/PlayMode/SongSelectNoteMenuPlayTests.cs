using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class SongSelectNoteMenuPlayTests
{
    SongSelectController controller;
    SongSelectAimPointer aim;
    [UnitySetUp] public IEnumerator Open()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float until = Time.realtimeSinceStartup + 10;
        while (Object.FindFirstObjectByType<SongSelectAimPointer>() == null && Time.realtimeSinceStartup < until) yield return null;
        aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); Assert.NotNull(aim); aim.enabled = false;
        controller = Object.FindFirstObjectByType<SongSelectController>();
        controller.Select(Enumerable.Range(0, controller.SongCount).Single(i => controller.SongIdAt(i) == "Epilogue"));
        controller.SetDifficulty(0);
        yield return new WaitForSecondsRealtime(.6f); Canvas.ForceUpdateCanvases();
    }
    [UnityTearDown] public IEnumerator Cleanup()
    {
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("AimCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
    }
    void Hold(Vector2 point, int frames) { for (int i = 0; i < frames; i++) aim.TickAt(point, .1f); }

    [UnityTest] public IEnumerator OneSecondShootsDifficulty_HeldAimDoesNotRepeat_AndClicksStillWork()
    {
        Assert.IsNull(Object.FindFirstObjectByType<SaberCutJudge>(), "選曲はセーバーの通過判定を生成しない");
        Assert.IsNull(Object.FindFirstObjectByType<SaberInputBridge>(), "選曲は銃の照準だけを描く");
        var action = controller.difficultyButtons[1].GetComponent<MenuNoteAction>(); action.Sync();
        action.Note.Cut(action.Note.transform.position, Vector3.right * 100);
        Assert.False(action.Note.IsCut, "高速移動でも選択しない");
        Vector2 point = action.ScreenRect().center;
        Hold(point, 9); Assert.Zero(aim.ShotCount); Assert.AreEqual(0, controller.SelectedDifficultyIndex);
        Hold(point, 1); Assert.AreEqual(1, aim.ShotCount); Assert.IsNull(action.Note);
        var effect = SongSelectNoteMenu.Instance.ShotEffect;
        Assert.True(effect.IsActive); Assert.NotNull(effect.Clip);
        yield return new WaitForSecondsRealtime(.65f);
        Assert.AreEqual(1, controller.SelectedDifficultyIndex);
        Assert.NotNull(action.Note); Hold(point, 80); Assert.AreEqual(1, aim.ShotCount);
        Hold(Vector2.zero, 2); Hold(point, 10); Assert.AreEqual(2, aim.ShotCount);
        yield return new WaitForSecondsRealtime(.25f);
        controller.difficultyButtons[2].onClick.Invoke(); Assert.AreEqual(2, controller.SelectedDifficultyIndex);
    }

    [UnityTest] public IEnumerator AimSwitch_Cancel_InputLoss_AndDisabledTargetDoNotAccumulate()
    {
        var normal = controller.difficultyButtons[1].GetComponent<MenuNoteAction>();
        var master = controller.difficultyButtons[2].GetComponent<MenuNoteAction>();
        Vector2 n = normal.ScreenRect().center, m = master.ScreenRect().center;
        Hold(n, 7); Hold(m, 7); Assert.Zero(aim.ShotCount);
        aim.TickAt(m, .1f, false); Hold(m, 9); Assert.Zero(aim.ShotCount);
        controller.difficultyButtons[2].interactable = false; Hold(m, 20); Assert.Zero(aim.ShotCount);
        controller.difficultyButtons[2].interactable = true;
        Hold(m, 9); Assert.Zero(aim.ShotCount); Hold(m, 1); Assert.AreEqual(1, aim.ShotCount);
        yield return new WaitForSecondsRealtime(.25f); Assert.AreEqual(2, controller.SelectedDifficultyIndex);
    }

    [UnityTest] public IEnumerator NavigationMovingTheListCannotFireAgainUntilAimLeaves()
    {
        var dock = GameObject.Find("NavDownDock").GetComponent<RectTransform>();
        Vector2 point = SongSelectAimPointer.RectOnScreen(dock).center;
        int start = controller.SelectedIndex;
        Hold(point, 10); Assert.AreEqual(1, aim.ShotCount);
        Assert.AreEqual((start + 1) % controller.SongCount, controller.SelectedIndex);
        yield return new WaitForSecondsRealtime(.6f);
        Hold(point, 50); Assert.AreEqual(1, aim.ShotCount);
        Hold(Vector2.zero, 2); Hold(point, 10); Assert.AreEqual(2, aim.ShotCount);
        Assert.AreEqual((start + 2) % controller.SongCount, controller.SelectedIndex);
    }

    [UnityTest] public IEnumerator StartShootsOnceAndEntersTheSelectedGame()
    {
        Vector2 point = controller.startButton.GetComponent<MenuNoteAction>().ScreenRect().center;
        Hold(point, 9); Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Hold(point, 1); Assert.AreEqual(1, aim.ShotCount);
        yield return new WaitForSecondsRealtime(.25f);
        Assert.True(ScreenTransition.IsBusy);
        aim.TickAt(point, .1f); Assert.Zero(aim.Progress01);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual("Epilogue", GameSession.SelectedSongId);
    }

    [UnityTest] public IEnumerator SelectingAgainDuringShotAnimationCancelsTheOldAction()
    {
        var normal = controller.difficultyButtons[1].GetComponent<MenuNoteAction>();
        Hold(normal.ScreenRect().center, 10); Assert.AreEqual(1, aim.ShotCount);
        controller.difficultyButtons[2].onClick.Invoke();
        yield return new WaitForSecondsRealtime(.3f);
        Assert.AreEqual(2, controller.SelectedDifficultyIndex, "古い発射予約が新しい選択を上書きしない");
    }
}
