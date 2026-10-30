using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 選曲の「この曲で はじめる？」(2026-10-30 ユーザー「③はすべて実装」)と、3・2・1 の間の取り消し。
public class SongSelectConfirmPlayTests
{
    SongSelectController ctl;
    SongSelectSkin skin;
    SongSelectConfirm confirm;
    SongSelectAimPointer aim;

    [UnitySetUp] public IEnumerator Open()
    {
        GameSession.IsCalibrationMode = false; GameSession.TutorialPending = false;
        yield return SceneManager.LoadSceneAsync("SongSelect");
        yield return WaitForSelect();
        ctl.Select(Enumerable.Range(0, ctl.SongCount).Single(i => ctl.SongIdAt(i) == "Epilogue"));
        ctl.SetDifficulty(1);
        var guide = Object.FindFirstObjectByType<SongSelectIdleGuide>();
        if (guide != null) guide.RegisterActivity();
        yield return new WaitForSecondsRealtime(.6f);
        Canvas.ForceUpdateCanvases();
    }

    IEnumerator WaitForSelect()
    {
        float until = Time.realtimeSinceStartup + 15;
        while (((skin = Object.FindFirstObjectByType<SongSelectSkin>()) == null || !skin.IsReady) && Time.realtimeSinceStartup < until) yield return null;
        Assert.NotNull(skin); Assert.IsTrue(skin.IsReady);
        ctl = Object.FindFirstObjectByType<SongSelectController>();
        aim = Object.FindFirstObjectByType<SongSelectAimPointer>(); aim.enabled = false;
        confirm = skin.Confirm;
        Assert.NotNull(confirm);
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene("SongSelectConfirmCleanup"));
        yield return SceneManager.UnloadSceneAsync(scene);
    }

    [UnityTest]
    public IEnumerator OpeningShowsTheSongPausesTheClockAndGoingBackKeepsTime()
    {
        Assert.IsFalse(SongSelectConfirm.IsOpen);
        Assert.IsTrue(confirm.Open(false, "test"));
        Assert.IsTrue(SongSelectConfirm.IsOpen);
        StringAssert.Contains("こうか", confirm.SongTitleMarkup);
        StringAssert.StartsWith("NORMAL", confirm.DifficultyText);
        StringAssert.Contains("選曲に もどる", confirm.FooterText, "なにもしないとどうなるかを、開いた瞬間から出す");
        double before = skin.RemainingSeconds;
        skin.TickCountdown(5);
        Assert.AreEqual(before, skin.RemainingSeconds, 1e-6, "確認の間は選曲の時計を止める");
        confirm.Back("test");
        Assert.IsTrue(SongSelectConfirm.IsOpen, "開いた直後の0.5秒は受けない");
        confirm.Tick(.6);
        confirm.Back("test");
        Assert.IsFalse(SongSelectConfirm.IsOpen);
        Assert.IsFalse(ctl.StartAccepted, "えらびなおすでは始めない");
        skin.TickCountdown(skin.RemainingSeconds - 3);
        Assert.IsTrue(confirm.Open(false, "test")); confirm.Tick(.6); confirm.Back("test");
        Assert.GreaterOrEqual(skin.RemainingSeconds, SongSelectConfirm.MinimumSecondsAfterBack - .01, "戻ったら残りを最低15秒に");
        yield return null;
    }

    [UnityTest]
    public IEnumerator StartBeginsTheChartThatWasShown()
    {
        Assert.IsTrue(confirm.Open(false, "test"));
        confirm.Tick(.6);
        confirm.Confirm("test");
        Assert.IsTrue(ctl.StartAccepted);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
        Assert.AreEqual("Epilogue", GameSession.SelectedSongId);
        Assert.AreEqual("Normal", GameSession.SelectedDifficulty);
    }

    [UnityTest]
    public IEnumerator DoingNothingClosesTheConfirmWithoutStarting()
    {
        Assert.IsTrue(confirm.Open(false, "test"));
        confirm.Tick(SongSelectConfirm.IdleCloseSeconds - .5);
        Assert.IsTrue(SongSelectConfirm.IsOpen);
        confirm.Tick(.6);
        Assert.IsFalse(SongSelectConfirm.IsOpen, "10秒で選曲へ戻る");
        Assert.IsFalse(ctl.StartAccepted);
        yield return null;
    }

    [UnityTest]
    public IEnumerator TimeoutOpensTheConfirmAndStartsAfterEightSeconds()
    {
        GameSession.SongSelectOperated = true;
        skin.TickCountdown(SongSelectCountdown.LimitSeconds + 1);
        Assert.IsTrue(SongSelectConfirm.IsOpen, "時間切れでも、まず何が始まるかを見せる");
        Assert.IsTrue(confirm.Automatic);
        Assert.IsFalse(ScreenTransition.IsBusy);
        confirm.Tick(SongSelectConfirm.AutoStartSeconds - .5);
        Assert.IsFalse(ctl.StartAccepted);
        confirm.Tick(.6);
        Assert.IsTrue(ctl.StartAccepted, "8秒で始める");
        yield return ScreenTransitionPlayTests.WaitForTransition();
    }

    [UnityTest]
    public IEnumerator HoldingTheCentreOpensTheConfirmAndOnlyTheRightTargetStarts()
    {
        var centre = ctl.startButton.GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        aim.BeginArrival();
        for (int i = 0; i < 8; i++) aim.TickAt(new Vector2(-50, -50), .1f);
        for (int i = 0; i < 24; i++) aim.TickAt(centre, .1f);
        Assert.IsTrue(SongSelectConfirm.IsOpen, "まん中の盤に2秒で確認が開く");
        Assert.IsFalse(ctl.StartAccepted, "すぐには始めない");
        // 開いた確認の部品は、次の描画で照準の当たりに入る(同じコマでは裏の盤に当たる)。実機では手が動く前に必ずコマが進む。
        yield return null; Canvas.ForceUpdateCanvases();
        for (int i = 0; i < 30; i++) { confirm.Tick(.1); aim.TickAt(centre, .1f); }
        Assert.IsTrue(SongSelectConfirm.IsOpen, "まん中に乗せたままでは決まらない");
        Assert.IsFalse(ctl.StartAccepted);
        Vector2 start = confirm.StartTarget.ScreenRect().center;
        for (int i = 0; i < 12; i++) { confirm.Tick(.1); aim.TickAt(start, .1f); }
        Assert.IsFalse(ctl.StartAccepted, "1.5秒たまるまでは始めない");
        for (int i = 0; i < 12; i++) { confirm.Tick(.1); aim.TickAt(start, .1f); }
        Assert.IsTrue(ctl.StartAccepted, "右の「スタート！」に1.5秒で始まる");
        yield return ScreenTransitionPlayTests.WaitForTransition();
    }

    [UnityTest]
    public IEnumerator EnterGoesThroughTheConfirm()
    {
        Assert.NotNull(ctl.OpenStartConfirm, "Enter・Space はコントローラーから確認を開く");
        Assert.IsTrue(ctl.OpenStartConfirm("key"));
        Assert.IsTrue(SongSelectConfirm.IsOpen);
        Assert.IsFalse(ctl.StartAccepted);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MiddleClickDuringTheCountInGoesBackToTheSameChart()
    {
        ctl.StartGame(); // 確認を通さない、テスト用の入口
        yield return ScreenTransitionPlayTests.WaitForTransition();
        var manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 15;
        GameStartCountdown countdown = null;
        while (((countdown = Object.FindFirstObjectByType<GameStartCountdown>()) == null || !countdown.IsRunning)
            && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.IsTrue(countdown != null && countdown.IsRunning, "3・2・1 が始まる");
        Assert.IsTrue(manager.CancelCountInToSongSelect());
        yield return ScreenTransitionPlayTests.WaitForTransition();
        yield return WaitForSelect();
        Assert.AreEqual("Epilogue", ctl.SongIdAt(ctl.SelectedIndex), "同じ曲を選んだ状態で戻る");
        Assert.AreEqual(1, ctl.SelectedDifficultyIndex);
    }
}
