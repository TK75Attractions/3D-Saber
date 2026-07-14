using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class JudgmentOffsetTests
{
    private readonly List<GameObject> created = new List<GameObject>();
    private int savedOffset;

    [SetUp]
    public void SaveOffset()
    {
        savedOffset = GameSession.JudgmentOffsetMs;
        GameSession.ResetJudgmentOffset();
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        // テスト終了後は元の値に戻す
        GameSession.JudgmentOffsetMs = savedOffset;
    }

    // -- GameSession --

    [Test]
    public void GameSession_DefaultOffsetIsRecommended()
    {
        // 未保存時は推奨既定値(+60ms)から始まる
        Assert.AreEqual(GameSession.JudgmentOffsetDefaultMs, GameSession.JudgmentOffsetMs);
    }

    [Test]
    public void GameSession_SetAndGet()
    {
        GameSession.JudgmentOffsetMs = 50;
        Assert.AreEqual(50, GameSession.JudgmentOffsetMs);
        GameSession.JudgmentOffsetMs = -75;
        Assert.AreEqual(-75, GameSession.JudgmentOffsetMs);
    }

    [Test]
    public void GameSession_ClampsToBounds()
    {
        GameSession.JudgmentOffsetMs = 9999;
        Assert.AreEqual(GameSession.JudgmentOffsetMaxMs, GameSession.JudgmentOffsetMs);
        GameSession.JudgmentOffsetMs = -9999;
        Assert.AreEqual(GameSession.JudgmentOffsetMinMs, GameSession.JudgmentOffsetMs);
    }

    [Test]
    public void GameSession_ResetReturnsToRecommendedDefault()
    {
        GameSession.JudgmentOffsetMs = 42;
        GameSession.ResetJudgmentOffset();
        Assert.AreEqual(GameSession.JudgmentOffsetDefaultMs, GameSession.JudgmentOffsetMs);
    }

    // -- Widget --

    private Canvas MakeCanvas()
    {
        var go = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        created.Add(go);
        return go.GetComponent<Canvas>();
    }

    [Test]
    public void Widget_Ensure_AddsToCanvas()
    {
        var canvas = MakeCanvas();
        var w = JudgmentOffsetWidget.Ensure(canvas);
        Assert.IsNotNull(w);
        Assert.AreEqual(canvas.transform, w.transform.parent);
    }

    [Test]
    public void Widget_Ensure_IsIdempotent()
    {
        var canvas = MakeCanvas();
        var a = JudgmentOffsetWidget.Ensure(canvas);
        var b = JudgmentOffsetWidget.Ensure(canvas);
        Assert.AreSame(a, b);
    }

    [Test]
    public void Widget_Change_UpdatesGameSession()
    {
        // ウィジェットは保存済みの現在値(未保存なら推奨既定値)から相対で動く
        var canvas = MakeCanvas();
        var w = JudgmentOffsetWidget.Ensure(canvas);
        int baseMs = GameSession.JudgmentOffsetDefaultMs;
        w.Change(10);
        Assert.AreEqual(baseMs + 10, GameSession.JudgmentOffsetMs);
        Assert.AreEqual(baseMs + 10, w.currentMs);
        w.Change(-3);
        Assert.AreEqual(baseMs + 7, GameSession.JudgmentOffsetMs);
    }

    [Test]
    public void Widget_Change_ClampsToBounds()
    {
        var canvas = MakeCanvas();
        var w = JudgmentOffsetWidget.Ensure(canvas);
        for (int i = 0; i < 200; i++) w.Change(10);
        Assert.AreEqual(GameSession.JudgmentOffsetMaxMs, w.currentMs);
    }

    [Test]
    public void Widget_Build_HasFourButtons()
    {
        var canvas = MakeCanvas();
        var w = JudgmentOffsetWidget.Ensure(canvas);
        var btns = w.GetComponentsInChildren<Button>(true);
        Assert.AreEqual(4, btns.Length, "-10 / -1 / +1 / +10 の 4 ボタン");
    }

    [Test]
    public void Widget_SetValue_OverridesCurrent()
    {
        var canvas = MakeCanvas();
        var w = JudgmentOffsetWidget.Ensure(canvas);
        w.SetValue(-25);
        Assert.AreEqual(-25, w.currentMs);
        Assert.AreEqual(-25, GameSession.JudgmentOffsetMs);
    }
}
