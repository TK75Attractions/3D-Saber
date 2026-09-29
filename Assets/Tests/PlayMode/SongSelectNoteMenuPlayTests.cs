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
    [UnityTest] public IEnumerator HeldSideDiscKeepsAdvancingWithoutLeaving()
    {
        int start=controller.SelectedIndex, count=controller.SongCount;
        var target=GameObject.Find("SongDisc_"+(start+1)%count).GetComponent<SongSelectDiscTarget>();
        Vector2 point=target.ScreenRect().center;
        Hold(point,10); Assert.AreEqual(1,aim.ShotCount); Assert.AreEqual((start+1)%count,controller.SelectedIndex);
        Assert.False(aim.NeedsRelease,"曲送りの盤は外さなくても次を受け付ける");
        yield return new WaitForSecondsRealtime(.6f);
        Hold(point,9); Assert.AreEqual(1,aim.ShotCount,"次の曲も1秒ため直す");
        Hold(point,1); Assert.AreEqual(2,aim.ShotCount); Assert.AreEqual((start+2)%count,controller.SelectedIndex);
    }
    [UnityTest] public IEnumerator HoldingThroughTheSlideKeepsSteppingWithoutStartingOrPreviewing()
    {
        int start=controller.SelectedIndex, count=controller.SongCount;
        Vector2 point=GameObject.Find("SongDisc_"+(start+1)%count).GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        double until=Time.realtimeSinceStartupAsDouble+8;
        var shots=new System.Collections.Generic.List<double>();
        while(aim.ShotCount<3 && Time.realtimeSinceStartupAsDouble<until)
        {
            int before=aim.ShotCount;
            aim.TickAt(point,Mathf.Min(Time.unscaledDeltaTime,.1f));
            if(aim.ShotCount>before) shots.Add(Time.realtimeSinceStartupAsDouble);
            Assert.False(controller.ChartPreview!=null && controller.ChartPreview.IsPlaying,"連続送りの間は試聴を鳴らさない");
            Assert.Zero(controller.startButton.GetComponent<SongSelectDiscTarget>().Progress,"滑っていく中央の盤でスタートをためない");
            yield return null;
        }
        Assert.AreEqual(3,aim.ShotCount); Assert.AreEqual((start+3)%count,controller.SelectedIndex);
        for(int i=1;i<shots.Count;i++) Assert.GreaterOrEqual(shots[i]-shots[i-1],1.4,"盤が止まってから1秒ため直す");
        Assert.False(ScreenTransition.IsBusy,"滑ってきた中央の盤でスタートしない");
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);
    }
    [UnityTest] public IEnumerator TwoAheadDiscsAreOnScreenTargetsThatJumpTwoSongs()
    {
        int start=controller.SelectedIndex, count=controller.SongCount;
        Assume.That(count,Is.GreaterThanOrEqualTo(5));
        var ahead=GameObject.Find("SongDisc_"+(start+2)%count).GetComponent<SongSelectDiscTarget>();
        Rect r=ahead.ScreenRect(), screen=new Rect(0,0,Screen.width,Screen.height);
        Assert.True(screen.Contains(r.min)&&screen.Contains(r.max),"2曲先の盤が画面に収まる");
        Assert.True(ahead.Available); Assert.AreEqual(1,ahead.HoldSeconds); Assert.True(ahead.RepeatWhileHeld);
        Assert.False(controller.startButton.GetComponent<SongSelectDiscTarget>().RepeatWhileHeld,"スタートは連続にしない");
        Hold(r.center,9); Assert.Zero(aim.ShotCount);
        Hold(r.center,1); Assert.AreEqual(1,aim.ShotCount); Assert.AreEqual((start+2)%count,controller.SelectedIndex);
        yield return new WaitForSecondsRealtime(.6f);
        var back=GameObject.Find("SongDisc_"+start).GetComponent<SongSelectDiscTarget>();
        Assert.True(back.Available); Hold(back.ScreenRect().center,10);
        Assert.AreEqual(2,aim.ShotCount); Assert.AreEqual(start,controller.SelectedIndex,"2曲前へも1回で戻れる");
    }
    [UnityTest] public IEnumerator JumpingTwoSlidesEveryDiscOneWayAndLeavesFiveOnScreen()
    {
        int count=controller.SongCount;
        Assume.That(count,Is.GreaterThanOrEqualTo(6),"端を回り込む盤が出るのは6曲以上");
        var discs=Object.FindObjectsByType<SongSelectDiscTarget>(FindObjectsInactive.Include,FindObjectsSortMode.None)
            .Where(t=>t.name.StartsWith("SongDisc_")).ToArray();
        foreach(int step in new[]{2,-2,-2,2})
        {
            // 見えている盤は動き出した位置、画面外から入る盤は最初に見えた位置を基準に、進む向きと逆へ動かないこと。
            var reference=discs.Where(d=>d.gameObject.activeSelf).ToDictionary(d=>d,d=>((RectTransform)d.transform).anchoredPosition.x);
            var jump=discs.Single(d=>d.name=="SongDisc_"+(controller.SelectedIndex+step+count)%count);
            Assert.True(jump.TryShoot());
            double until=Time.realtimeSinceStartupAsDouble+.7;
            while(Time.realtimeSinceStartupAsDouble<until)
            {
                yield return null;
                foreach(var d in discs)
                {
                    if(!d.gameObject.activeSelf) continue;
                    float x=((RectTransform)d.transform).anchoredPosition.x;
                    if(!reference.ContainsKey(d)) reference[d]=x;
                    Assert.That(step>0?x<=reference[d]+1:x>=reference[d]-1,d.name+" が送る向きと逆へ動いた(画面を横切った)");
                }
            }
            Assert.AreEqual(5,discs.Count(d=>d.gameObject.activeSelf),"止まったら中央と左右2枚ずつだけが見える");
        }
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
