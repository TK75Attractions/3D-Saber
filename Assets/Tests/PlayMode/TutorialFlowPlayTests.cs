using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// Game シーンをチュートリアルで開き、練習 → 選曲へ、の流れが本物の部品で通ることを確かめる。
// 2026-10-31 夜から、範囲合わせのあとは左右交互の練習だけ(持ち方の説明も流さない)。テストではスマホが無いので範囲の段も無い。
public class TutorialFlowPlayTests
{
    bool savedCalibration;

    [UnitySetUp] public IEnumerator Setup()
    {
        savedCalibration = GameSession.IsCalibrationMode; GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = true;
        yield return SceneManager.LoadSceneAsync("Game");
        for (int i = 0; i < 120 && Object.FindFirstObjectByType<GamePlayManager>()?.Tutorial == null; i++) yield return null;
    }

    [UnityTearDown] public IEnumerator Cleanup()
    {
        GameSession.TutorialPending = false; GameSession.IsCalibrationMode = savedCalibration;
        var empty = SceneManager.CreateScene("TutorialCleanup"); SceneManager.SetActiveScene(empty);
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene != empty && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest] public IEnumerator OpensOnTheAlternatingPracticeWithMusicAndNoSaberPointer()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        Assert.NotNull(t, "GamePlayManager がチュートリアルを始める");
        Assert.IsFalse(GameSession.TutorialPending, "フラグは読んだら消す");
        var floor = Object.FindFirstObjectByType<FloorRenderer>();
        Assert.NotNull(floor);
        Assert.AreEqual(StageTheme.PracticeCourtyard,floor.ActiveTheme,"選曲状態に関係なく練習専用の中庭");
        var world = floor.GetComponentInChildren<ScenicStageWorld>();
        Assert.NotNull(world);
        floor.Build();
        Assert.AreEqual(StageTheme.PracticeCourtyard,floor.ActiveTheme,"入場フラグを消した後も背景を保持する");
        Assert.NotNull(t.Overlay); Assert.NotNull(t.Overlay.StaffSkipButton);
        // 2026-11-01: 来場者が飛ばす的は無いので、剣の照準(画面下の帯)は作らない。見出しは「試し切り」。
        Assert.IsNull(Object.FindFirstObjectByType<SaberUIPointer>(), "剣で押す的が無いので照準は作らない");
        Assert.AreEqual("試し切り", t.Overlay.transform.Find("Chip").GetComponent<TMPro.TextMeshProUGUI>().text);
        yield return new WaitForSecondsRealtime(.6f);
        Assert.IsTrue(m.songPlayer.IsPlaying, "音源と共通の時計が回る"); Assert.NotNull(m.songPlayer.Clip);
        Assert.AreSame(Resources.Load<AudioClip>(TutorialController.MusicResourcePath), m.songPlayer.Clip);
        var source = m.songPlayer.GetComponent<AudioSource>();
        Assert.IsTrue(source.loop);
        Assert.AreEqual(TutorialController.MusicVolume, source.volume, .001f);
        Assert.AreEqual(4f, m.songPlayer.Clip.length, .0001f);
        EnsureClockAdvances(m.songPlayer);
        t.Tick(.016f);
        Assert.NotNull(t.Step); Assert.AreEqual("tapMix", t.Step.Id, "持ち方の説明なしで、すぐ左右交互");
        Assert.IsFalse(t.Overlay.HandsVisible, "持ち方の絵は出さない");
        StringAssert.Contains("同じ手", t.Overlay.TitleText);
        Assert.AreEqual(2, t.StepCount, "左右交互と準備OKだけ");
        double beforeLoop = m.songPlayer.SongTime;
        yield return new WaitForSecondsRealtime(4.1f);
        Assert.Greater(m.songPlayer.SongTime, beforeLoop + 4, "音源が一周しても譜面時計は戻らない");
        Assert.AreSame(Resources.Load<AudioClip>(TutorialController.MusicResourcePath), m.songPlayer.Clip);
        Assert.Greater(world.LastTickSeconds,4,"ループ音源と同じ時計で中庭の風鈴が動く");
        Assert.That(world.LastTickSeconds,Is.EqualTo(m.songPlayer.SongTime).Within(.15));
        m.songPlayer.Stop();
        double stopped = world.LastTickSeconds;
        yield return new WaitForSecondsRealtime(.1f);
        Assert.AreEqual(stopped,world.LastTickSeconds,"音を止めると背景も止まる");
    }

    [UnityTest] public IEnumerator AlternatingPracticeSpawnsRedOnTheRightOnTheDownbeat()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        EnsureClockAdvances(m.songPlayer);
        float deadline = Time.realtimeSinceStartup + 8f;
        while (Time.realtimeSinceStartup < deadline && m.noteSpawner.AliveCount == 0) yield return null;
        Assert.NotNull(t.Step); Assert.AreEqual("tapMix", t.Step.Id);
        Assert.AreEqual(0, t.Records.Count, "まだどの段も終わっていない");
        Assert.Greater(m.noteSpawner.AliveCount, 0, "型のノーツが流れる");
        var note = m.noteSpawner.LiveNotes[0];
        Assert.AreEqual(SaberHand.Right, note.RequiredHand, "最初の型は赤(右手)");
        Assert.Greater(note.transform.position.x, 0f, "赤は画面の右");
        Assert.AreEqual(1, note.RequiredCutCount);
        double chartHit = note.HitTime - m.noteSpawner.TotalOffsetSeconds;
        Assert.AreEqual(System.Math.Round(chartHit / TutorialProgram.BarSeconds), chartHit / TutorialProgram.BarSeconds, .0001,
            "判定調整を除いた最初のノーツは小節頭");
    }

    [UnityTest] public IEnumerator SkipFinishesAndLeavesForSongSelect()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        var music = m.songPlayer.Clip;
        t.Skip();
        Assert.IsTrue(t.IsFinished); Assert.AreEqual("skipped", t.FinishReason);
        Assert.IsFalse(t.Overlay.StaffSkipButton.gameObject.activeSelf, "終わったらスタッフの的も消す");
        Assert.IsTrue(ScreenTransition.IsBusy, "選曲への幕が始まる");
        t.Skip();
        Assert.AreEqual("skipped", t.FinishReason, "二度目は何もしない");
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline && ScreenTransition.IsBusy) yield return null;
        Assert.IsFalse(ScreenTransition.IsBusy);
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
        Assert.IsFalse(GameSession.TutorialPending);
        Assert.IsTrue(m == null, "選曲へ移ったら古いゲームと再生元を破棄する");
        Assert.IsTrue(music != null, "共有音源は破棄せず、再入場にも使える");
        Assert.AreSame(music, Resources.Load<AudioClip>(TutorialController.MusicResourcePath));
    }

    // 2026-11-01 ユーザー「右下にクリックでしか押せないボタン置いて押したら自然に飛ばす(客に気づかれないように)」: 右下の角の透明な的。
    // マウスで角を押すと、合格しないまま周が終わったときと同じ「次に進むよ」→ 準備OK → 選曲。記録は skipped。
    [UnityTest] public IEnumerator StaffCornerClickSkipsThePracticeLikeTheLoopsRanOut()
    {
        var m = Object.FindFirstObjectByType<GamePlayManager>(); var t = m.Tutorial;
        yield return new WaitForSecondsRealtime(.6f);
        EnsureClockAdvances(m.songPlayer);
        t.Tick(.016f);
        Assert.AreEqual("tapMix", t.Step.Id);
        var button = t.Overlay.StaffSkipButton;
        Assert.IsTrue(button.gameObject.activeInHierarchy);
        Assert.AreEqual(0f, ((UnityEngine.UI.Image)button.targetGraphic).color.a, "見た目は何も描かない");
        // 画面の右下の角をクリックしたときに当たるのは、この的(上に重なる版の表示などは当たりを持たない)。
        var events = UnityEngine.EventSystems.EventSystem.current;
        Assert.NotNull(events);
        var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        events.RaycastAll(new UnityEngine.EventSystems.PointerEventData(events) { position = new Vector2(Screen.width - 4, 4) }, hits);
        Assert.Greater(hits.Count, 0);
        Assert.AreSame(button.gameObject, hits[0].gameObject, "角のクリックはスタッフの的に当たる");
        Assert.AreEqual(UnityEngine.UI.Navigation.Mode.None, button.navigation.mode, "Enter では押さない");
        button.onClick.Invoke();
        Assert.IsTrue(t.StaffSkipRequested); Assert.IsFalse(t.IsFinished, "すぐには終えず、いつもの流れで進める");
        t.Tick(.016f);
        Assert.IsTrue(t.StepDone);
        Assert.AreEqual("次に進むよ", t.Overlay.TitleText, "周が終わったときと同じ見え方");
        Assert.AreEqual("tapMix", t.Records[t.Records.Count - 1].Id); Assert.AreEqual("skipped", t.Records[t.Records.Count - 1].Reason);
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline && t.Step.Id != "ready") yield return null;
        Assert.AreEqual("ready", t.Step.Id);
        Assert.AreEqual("曲を選びに行こう", t.Overlay.TitleText);
        deadline = Time.realtimeSinceStartup + 4f;
        while (Time.realtimeSinceStartup < deadline && !t.IsFinished) yield return null;
        Assert.IsTrue(t.IsFinished); Assert.AreEqual("done", t.FinishReason, "最後は、ふつうに終えたときと同じ");
        Assert.IsTrue(ScreenTransition.IsBusy, "選曲への幕が始まる");
        while (ScreenTransition.IsBusy) yield return null;
        Assert.AreEqual("SongSelect", SceneManager.GetActiveScene().name);
    }

    // バッチ実行で音声機器が無く sample 位置が進まない環境でも、DSP 時計で進行を確かめられるようにする。
    static void EnsureClockAdvances(SongPlayer player)
    {
        if (player.SongTime > 0) return;
        typeof(SongPlayer).GetField("clockSynchronized", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, true);
        typeof(SongPlayer).GetField("startDspTime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(player, AudioSettings.dspTime - .05);
    }
}
