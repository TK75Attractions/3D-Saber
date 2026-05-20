using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class JudgmentTestWidgetTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
    }

    private Canvas MakeCanvas()
    {
        var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        created.Add(go);
        return go.GetComponent<Canvas>();
    }

    [Test]
    public void Ensure_CreatesWidgetOnCanvas()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        Assert.IsNotNull(w);
        Assert.AreEqual(canvas.transform, w.transform.parent);
    }

    [Test]
    public void Ensure_IsIdempotent()
    {
        var canvas = MakeCanvas();
        var a = JudgmentTestWidget.Ensure(canvas);
        var b = JudgmentTestWidget.Ensure(canvas);
        Assert.AreSame(a, b);
    }

    [Test]
    public void StartPractice_SetsRunningTrue()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        w.StartPractice();
        Assert.IsTrue(w.IsRunning);
    }

    [Test]
    public void Stop_SetsRunningFalseAndClearsNotes()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        w.StartPractice();
        Assert.IsTrue(w.IsRunning);
        w.Stop();
        Assert.IsFalse(w.IsRunning);
        Assert.AreEqual(0, w.LiveNoteCount);
    }

    [Test]
    public void Toggle_AlternatesRunningState()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        Assert.IsFalse(w.IsRunning);
        w.Toggle();
        Assert.IsTrue(w.IsRunning);
        w.Toggle();
        Assert.IsFalse(w.IsRunning);
    }

    [Test]
    public void HitAttempt_NoNotes_DoesNotThrow()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        Assert.DoesNotThrow(() => w.HitAttempt(0.5));
    }

    [Test]
    public void Build_AddsBackgroundImageAndToggleButton()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        Assert.IsNotNull(w.GetComponent<Image>(), "背景 Image が必要");
        var btns = w.GetComponentsInChildren<Button>(true);
        Assert.AreEqual(1, btns.Length, "START/STOP トグル 1 ボタン");
    }

    [Test]
    public void Widget_HasLaneWithTarget()
    {
        var canvas = MakeCanvas();
        var w = JudgmentTestWidget.Ensure(canvas);
        var lane = w.transform.Find("Lane");
        Assert.IsNotNull(lane, "Lane が存在する");
        Assert.IsNotNull(lane.Find("Target"), "Target ラインが存在する");
    }
}
