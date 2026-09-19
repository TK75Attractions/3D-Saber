using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

public class SongSelectNoteMenuPlayTests
{
    [UnityTest]
    public IEnumerator AllActionsUseNotes_RequireSpeed_AndKeepClicks()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        float deadline = Time.realtimeSinceStartup + 10;
        while (GameObject.Find(ProjectorModeToggleUI.ButtonName) == null && Time.realtimeSinceStartup < deadline)
            yield return null;
        Assert.IsNotNull(SongSelectNoteMenu.Instance);
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.autonomous = false;
        yield return new WaitForSecondsRealtime(.85f);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        var canvas = ctl.GetComponentInParent<Canvas>();
        foreach (var button in canvas.GetComponentsInChildren<Button>())
            Assert.IsNotNull(button.GetComponent<MenuNoteAction>(), button.name + "も斬れる");
        Assert.IsTrue(Object.FindFirstObjectByType<SaberUIPointer>().SlashOnly, "滞留で発動しない");

        ctl.SetDifficulty(0);
        var normal = ctl.difficultyButtons[1].GetComponent<MenuNoteAction>();
        var master = ctl.difficultyButtons[2].GetComponent<MenuNoteAction>();
        normal.Sync(); master.Sync();
        var note = normal.Note;
        Assert.IsNotNull(note);
        note.Cut(note.transform.position, Vector3.right * 2.99f);
        Assert.IsFalse(note.IsCut, "速度不足では破壊も実行もしない");
        Assert.AreEqual(0, ctl.SelectedDifficultyIndex);

        note.Cut(note.transform.position, Vector3.right * SongSelectNoteMenu.MinimumCutSpeed);
        Assert.IsTrue(note.IsCut, "閾値以上の斬撃は通る");
        var second = master.Note;
        second.Cut(second.transform.position, Vector3.up * 10);
        Assert.IsFalse(second.IsCut, "同じ振りで別操作を巻き込まない");
        var nav = Object.FindFirstObjectByType<SongSelectSlashNav>();
        Assert.IsFalse(nav.UpNote.IsJudgeable, "曲送りともクールタイムを共有");
        yield return new WaitForSecondsRealtime(.2f);
        Assert.AreEqual(1, ctl.SelectedDifficultyIndex);

        yield return new WaitForSecondsRealtime(.7f);
        normal.Sync(); master.Sync();
        Assert.IsNotNull(normal.Note, "斬ったノーツは復帰する");
        Assert.IsTrue(master.Note.IsJudgeable);
        ctl.difficultyButtons[2].onClick.Invoke();
        Assert.AreEqual(2, ctl.SelectedDifficultyIndex, "通常のクリックを維持");

        var start = ctl.startButton.GetComponent<MenuNoteAction>();
        ctl.startButton.interactable = false;
        start.Sync();
        Assert.IsTrue(start.Note == null || !start.Note.gameObject.activeSelf, "開始できないときはノーツも無効");
        var hidden = canvas.GetComponentsInChildren<MenuNoteAction>()
            .FirstOrDefault(a => a.name.StartsWith("WheelRow_") && !a.IsAvailable);
        if (hidden != null)
        {
            hidden.Sync();
            Assert.IsTrue(hidden.Note == null || !hidden.Note.gameObject.activeSelf, "画面外の曲は切れない");
        }
    }

    [UnityTest]
    public IEnumerator RealJudgeRejectsSlowPass_AndAcceptsFastPass()
    {
        yield return SceneManager.LoadSceneAsync("SongSelect", LoadSceneMode.Single);
        yield return new WaitForSecondsRealtime(1);
        var ctl = Object.FindFirstObjectByType<SongSelectController>();
        Assert.IsNotNull(ctl);
        foreach (var existing in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) existing.autonomous = false;
        ctl.SetDifficulty(0);
        var action = ctl.difficultyButtons[1].GetComponent<MenuNoteAction>();
        action.Sync();
        var go = new GameObject("MenuJudgeTest");
        var tracker = go.AddComponent<SaberTracker>();
        var judge = go.AddComponent<SaberCutJudge>();
        judge.autonomous = false; judge.saber = tracker;
        judge.bladeRadius = .08f; judge.noteHitRadiusXY = .26f;
        judge.minCutSpeed = SongSelectNoteMenu.MinimumCutSpeed;
        try
        {
            var center = action.Note.transform.position;
            tracker.ResetTo(center + Vector3.left * .6f);
            tracker.Tick(center + Vector3.right * .6f, 1f);
            judge.TryCut();
            Assert.AreEqual(0, judge.PendingCount);
            Assert.IsFalse(action.Note.IsCut);
            tracker.ResetTo(center + Vector3.left * .6f);
            tracker.Tick(center + Vector3.right * .6f, .1f);
            judge.TryCut();
            Assert.IsNull(action.Note, "速い通過で切断イベントまで到達");
            yield return new WaitForSecondsRealtime(.2f);
            Assert.AreEqual(1, ctl.SelectedDifficultyIndex);
        }
        finally { Object.Destroy(go); }
    }
}
