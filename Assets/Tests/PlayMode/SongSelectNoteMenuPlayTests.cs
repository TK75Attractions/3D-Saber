using System.Collections;
using System.Linq;
using NUnit.Framework;
using TMPro;
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
        aim.BeginArrival(); Hold(Vector2.zero, 7);
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
    [UnityTest] public IEnumerator HoldingThroughTheSlideKeepsSteppingAndPreviewsWithoutStarting()
    {
        int start=controller.SelectedIndex, count=controller.SongCount;
        Vector2 point=GameObject.Find("SongDisc_"+(start+1)%count).GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        double until=Time.realtimeSinceStartupAsDouble+8;
        var shots=new System.Collections.Generic.List<double>();
        bool previewed=false;
        while(aim.ShotCount<3 && Time.realtimeSinceStartupAsDouble<until)
        {
            int before=aim.ShotCount;
            aim.TickAt(point,Mathf.Min(Time.unscaledDeltaTime,.1f));
            if(aim.ShotCount>before) shots.Add(Time.realtimeSinceStartupAsDouble);
            // 2026-10-30 ユーザー「次に移してる間に流していい」: 送って着いた曲は、次をためている間も試聴を始める。
            var preview=controller.ChartPreview;
            previewed|=aim.ShotCount>0 && preview!=null && preview.SongId==controller.SongIdAt(controller.SelectedIndex)
                && (preview.IsPlaying || preview.State==SongSelectChartPreview.PreviewState.Loading);
            Assert.Zero(controller.startButton.GetComponent<SongSelectDiscTarget>().Progress,"滑っていく中央の盤でスタートをためない");
            yield return null;
        }
        Assert.AreEqual(3,aim.ShotCount); Assert.AreEqual((start+3)%count,controller.SelectedIndex);
        Assert.True(previewed,"連続送りの間も、着いた曲の試聴を始める");
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
        // 2026-10-30 から2秒で「この曲で はじめる？」を開き、そこで決めると始まる(③)。
        Hold(point,1); Assert.AreEqual(1,aim.ShotCount); Assert.True(SongSelectConfirm.IsOpen); Assert.False(ScreenTransition.IsBusy);
        SongSelectConfirm.Instance.Tick(.6); SongSelectConfirm.Instance.Confirm("test"); Assert.True(ScreenTransition.IsBusy);
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
    // 2026-10-21 ユーザー「カーソルあってるのにカウント始まんない」: 座標が途切れている間(fresh=false)は、ためを止めて保つ。
    // 以前は外れた扱いで0.25秒後に取り消していた。戻ったら続きから溜まり、ロックもしない。
    [UnityTest] public IEnumerator TrackingGapPausesTheChargeAndResumesWithoutLocking()
    {
        var action = controller.difficultyButtons[1].GetComponent<SongSelectDiscTarget>();
        Vector2 point = action.ScreenRect().center;
        var reticle = aim.Reticle.GetComponent<SongSelectAimGraphic>();
        Hold(point,5); Assert.AreEqual(.5f,aim.Progress01,.01f);
        for (int i = 0; i < 6; i++) aim.TickAt(point,.1f,true,false);
        Assert.AreEqual(.5f,aim.Progress01,.01f,"途切れている間はためを進めず、取り消しもしない");
        Assert.False(aim.NeedsRelease,"途切れてもロックしない"); Assert.True(reticle.Stale,"見失っている間は照準を薄くする");
        Assert.True(aim.Reticle.gameObject.activeSelf);
        Hold(point,4); Assert.Zero(aim.ShotCount); Assert.False(reticle.Stale);
        Hold(point,1); Assert.AreEqual(1,aim.ShotCount,"戻ったら続きから溜まる"); Assert.AreEqual(1,controller.SelectedDifficultyIndex);
        yield return null;
    }
    [UnityTest] public IEnumerator StartHoldOffPausesOnlyTheCentreWithoutLocking()
    {
        int start=controller.SelectedIndex, count=controller.SongCount;
        Vector2 side=GameObject.Find("SongDisc_"+(start+1)%count).GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        Vector2 centre=controller.startButton.GetComponent<SongSelectDiscTarget>().ScreenRect().center;
        aim.HoldOffStart();
        Hold(side,5); Assert.AreEqual(.5f,aim.Progress01,.01f,"曲送りの盤は止めない"); Assert.False(aim.NeedsRelease);
        Hold(centre,4); Assert.Zero(aim.Progress01,"切った直後はスタートをためない"); Assert.False(aim.NeedsRelease,"照準を灰色にロックしない");
        Hold(centre,3); Assert.Greater(aim.Progress01,0f,"止めるのは約1秒だけ");
        Hold(Vector2.zero,4); Assert.Zero(aim.Progress01); Assert.Zero(aim.ShotCount);
        Assert.AreEqual("SongSelect",SceneManager.GetActiveScene().name);
        yield return null;
    }
    // 選曲で一度も操作がなければ、時間切れで曲を始めずにタイトルへ戻る(無人のまま1曲流さない。仕様面の点検 1)。
    [UnityTest] public IEnumerator TimeoutWithoutAnyOperationReturnsToTitle()
    {
        var skin=Object.FindFirstObjectByType<SongSelectSkin>();
        GameSession.SongSelectOperated=false;
        yield return null; yield return null;
        StringAssert.Contains("タイトル",GameObject.Find("TimerConsequence").GetComponent<TextMeshProUGUI>().text,"行き先と同じ案内");
        skin.TickCountdown(101); Assert.True(ScreenTransition.IsBusy);
        Assert.False(controller.StartAccepted,"曲は始めない");
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Title",SceneManager.GetActiveScene().name);
    }
    [UnityTest] public IEnumerator TimeoutStartsSelectedDifficultyAndResetsOnReturn()
    {
        var skin=Object.FindFirstObjectByType<SongSelectSkin>(); controller.SetDifficulty(2);
        // 2026-10-30 から時間切れは「この曲で はじめる？」を開き、8 秒で始める(③)。
        skin.TickCountdown(101); Assert.True(SongSelectConfirm.IsOpen); Assert.True(SongSelectConfirm.Instance.Automatic);
        SongSelectConfirm.Instance.Tick(SongSelectConfirm.AutoStartSeconds + .1); Assert.True(ScreenTransition.IsBusy);
        skin.TickCountdown(101);
        yield return ScreenTransitionPlayTests.WaitForTransition();
        Assert.AreEqual("Game",SceneManager.GetActiveScene().name); Assert.AreEqual("Hard",GameSession.SelectedDifficulty);
        yield return SceneManager.LoadSceneAsync("SongSelect");
        yield return null; yield return null;
        skin=Object.FindFirstObjectByType<SongSelectSkin>();
        Assert.That(skin.RemainingSeconds,Is.GreaterThan(SongSelectCountdown.LimitSeconds-2),"戻ったら制限いっぱいから(2026-10-30 から 45 秒)");
    }
    [UnityTest] public IEnumerator RecordPanelShowsTheBestScoreOfTheSelectedChartInsideThePanel()
    {
        // 結果画面が保存するのと同じ曲ID・難易度名で入れた記録の1位を、記録パネルの最下段に出す。
        string song=controller.SongIdAt(controller.SelectedIndex);
        var keys=controller.difficultyNames.Take(2).Select(d=>HighScoreStore.Key(song,d)).ToArray();
        var stored=keys.ToDictionary(k=>k,k=>PlayerPrefs.HasKey(k)?PlayerPrefs.GetString(k):null);
        try
        {
            foreach(var key in keys) PlayerPrefs.DeleteKey(key);
            foreach(int score in new[]{61234,ScoreManager.MaxScore})
                HighScoreStore.Record(song,controller.CurrentDifficultyName,new HighScoreEntry{score=score,rank="S+",date="2026/10/20"},out _);
            controller.SetDifficulty(0);
            var panel=(RectTransform)GameObject.Find("Achievements").transform;
            var label=panel.Find("HighScoreLabel").GetComponent<TMP_Text>();
            var value=panel.Find("HighScore").GetComponent<TMP_Text>();
            Assert.AreEqual(ScoreManager.MaxScore.ToString("N0")+"<size=24>点</size>",value.text);
            yield return null; Canvas.ForceUpdateCanvases(); label.ForceMeshUpdate(); value.ForceMeshUpdate();
            // いちばん幅を取る 100,000点 でも「HI-SCORE」に重ならず、パネルは周りの表示に重ならない。
            Rect box=WorldRect(panel);
            foreach(var text in new[]{label,value}) { Rect r=GlyphRect(text); Assert.Greater(r.width,0,text.name+" が描かれていない"); Assert.True(box.Contains(r.min)&&box.Contains(r.max),text.name+" がパネルからはみ出す"); }
            Assert.Less(GlyphRect(label).xMax,GlyphRect(value).xMin,"点が HI-SCORE に重なる");
            Rect rule=WorldRect((RectTransform)panel.Find("HighScoreRule")); Assert.True(box.Contains(rule.min)&&box.Contains(rule.max));
            foreach(var name in new[]{"TitleShadow","DifficultyTray"}) Assert.False(box.Overlaps(WorldRect((RectTransform)GameObject.Find(name).transform)),name+" に重なる");
            // 2026-10-30 ユーザー「④もすべてやる」: 曲数は記録パネルの見出し右へ移したので、内側に収まり見出しと重ならないことを確かめる。
            var library=GameObject.Find("LibraryPosition").GetComponent<TMP_Text>();
            library.ForceMeshUpdate();
            Rect libraryBox=GlyphRect(library);
            Assert.True(box.Contains(libraryBox.min)&&box.Contains(libraryBox.max),"曲数は記録パネルの内側");
            foreach(var name in new[]{"AchievementHeading","AchievementDifficulty"})
                Assert.False(libraryBox.Overlaps(WorldRect((RectTransform)panel.Find(name))),name+" と曲数が重なる");
            Rect screen=WorldRect((RectTransform)GameObject.Find("DiscSelectLayout").transform);
            Assert.True(screen.Contains(box.min)&&screen.Contains(box.max),"画面からはみ出す");
            controller.SetDifficulty(1);
            Assert.AreEqual("—",value.text,"まだ遊ばれていない難易度は 0点 と区別する");
        }
        finally
        {
            foreach(var pair in stored) { if(pair.Value==null) PlayerPrefs.DeleteKey(pair.Key); else PlayerPrefs.SetString(pair.Key,pair.Value); }
            PlayerPrefs.Save();
        }
    }
    static Rect WorldRect(RectTransform rect) { var c=new Vector3[4]; rect.GetWorldCorners(c); return Rect.MinMaxRect(c[0].x,c[0].y,c[2].x,c[2].y); }
    static Rect GlyphRect(TMP_Text text)
    {
        var b=text.textBounds; Vector3 min=text.rectTransform.TransformPoint(b.min),max=text.rectTransform.TransformPoint(b.max);
        return Rect.MinMaxRect(min.x,min.y,max.x,max.y);
    }
}
