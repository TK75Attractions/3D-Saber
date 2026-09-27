using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public class SongSelectNoteMenuPlayTests
{
    SongSelectController controller;
    SongSelectAimPointer aim;
    [UnitySetUp] public IEnumerator Open()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect");
        float until = Time.realtimeSinceStartup + 15;
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
        Assert.IsNull(Object.FindFirstObjectByType<SaberCutJudge>());
        Assert.IsNull(Object.FindFirstObjectByType<SaberInputBridge>());
        Assert.IsNull(Object.FindFirstObjectByType<CuttableNote>(), "旧3Dノーツを選曲へ戻さない");
        var action = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        Vector2 point = action.ScreenRect().center;
        Hold(point,9); Assert.Zero(aim.ShotCount);
        Hold(point,1); Assert.AreEqual(1,aim.ShotCount); Assert.AreEqual(1,controller.SelectedDifficultyIndex);
        Hold(point,80); Assert.AreEqual(1,aim.ShotCount);
        Hold(Vector2.zero,2); Hold(point,10); Assert.AreEqual(2,aim.ShotCount);
        controller.difficultyButtons[2].onClick.Invoke(); Assert.AreEqual(2,controller.SelectedDifficultyIndex);
        yield return null;
    }
    [UnityTest] public IEnumerator TargetChange_InputLoss_AndDisabledTargetCancelCharge()
    {
        var normal = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        var master = controller.difficultyButtons[2].GetComponent<SongSelectDiscTarget>();
        Vector2 n=normal.ScreenRect().center,m=master.ScreenRect().center;
        Hold(n,7); Hold(m,7); Assert.Zero(aim.ShotCount);
        aim.TickAt(m,.1f,false); Hold(m,9); Assert.Zero(aim.ShotCount);
        controller.difficultyButtons[2].interactable=false; Hold(m,20); Assert.Zero(aim.ShotCount);
        controller.difficultyButtons[2].interactable=true; Hold(m,10);
        Assert.AreEqual(1,aim.ShotCount); Assert.AreEqual(2,controller.SelectedDifficultyIndex);
        yield return null;
    }
    [UnityTest] public IEnumerator MovingDiscsCannotFireAgainUntilAimLeaves()
    {
        int start=controller.SelectedIndex, next=(start+1)%controller.SongCount;
        var target=GameObject.Find("SongDisc_"+next).GetComponent<SongSelectDiscTarget>();
        Vector2 point=target.ScreenRect().center;
        Hold(point,10); Assert.AreEqual(1,aim.ShotCount); Assert.AreEqual(next,controller.SelectedIndex);
        yield return new WaitForSecondsRealtime(.6f);
        Hold(point,50); Assert.AreEqual(1,aim.ShotCount);
        Hold(Vector2.zero,2); Hold(point,10); Assert.AreEqual(2,aim.ShotCount);
        Assert.AreEqual((start+2)%controller.SongCount,controller.SelectedIndex);
    }
    [UnityTest] public IEnumerator CenterNeedsTwoSecondsAndTransitionPreservesReleaseLock()
    {
        var target=controller.startButton.GetComponent<SongSelectDiscTarget>();
        Vector2 point=target.ScreenRect().center;
        Hold(point,19); Assert.Zero(aim.ShotCount); Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);
        Hold(point,1); Assert.AreEqual(1,aim.ShotCount); Assert.True(ScreenTransition.IsBusy);
        aim.TickAt(Vector2.zero,.15f); Assert.True(aim.NeedsRelease); Assert.Zero(aim.Progress01);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game",SceneManager.GetActiveScene().name); Assert.AreEqual("Epilogue",GameSession.SelectedSongId);
    }
    [UnityTest] public IEnumerator DiscCornersAreNotTargets()
    {
        var target=controller.startButton.GetComponent<SongSelectDiscTarget>(); var rect=target.ScreenRect();
        Vector2 corner=rect.max-Vector2.one*2;
        Assert.False(target.IsRaycastLocationValid(corner,null));
        Hold(corner,25); Assert.Zero(aim.ShotCount);
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);
        yield return null;
    }
    [UnityTest] public IEnumerator TimeoutStartsSelectedDifficultyAndResetsOnReturn()
    {
        var skin=Object.FindFirstObjectByType<SongSelectSkin>(); controller.SetDifficulty(2);
        skin.TickCountdown(101); Assert.True(ScreenTransition.IsBusy);
        skin.TickCountdown(101);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game",SceneManager.GetActiveScene().name); Assert.AreEqual("Hard",GameSession.SelectedDifficulty);
        yield return SceneManager.LoadSceneAsync("SongSelect");
        yield return null; yield return null;
        skin=Object.FindFirstObjectByType<SongSelectSkin>();
        Assert.That(skin.RemainingSeconds,Is.GreaterThan(98));
    }
}
