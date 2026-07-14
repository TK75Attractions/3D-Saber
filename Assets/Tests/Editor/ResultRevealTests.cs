using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// リザルトの段階出現演出(ResultReveal)の純関数 Evaluate と登録/駆動の検証。
public class ResultRevealTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
    }

    // ---- Slide(シャッ) ----

    [Test]
    public void Slide_BeforeDelay_IsHidden()
    {
        var pose = ResultReveal.Evaluate(ResultReveal.Kind.Slide, -0.1f, 0.2f, new Vector2(90f, 0f));
        Assert.AreEqual(0f, pose.alpha, 1e-4f);
        Assert.AreEqual(90f, pose.offset.x, 1e-3f, "出現前はスライド元の位置");
    }

    [Test]
    public void Slide_End_IsAtBaseFullyVisible()
    {
        var pose = ResultReveal.Evaluate(ResultReveal.Kind.Slide, 0.2f, 0.2f, new Vector2(90f, 0f));
        Assert.AreEqual(1f, pose.alpha, 1e-4f);
        Assert.AreEqual(0f, pose.offset.magnitude, 1e-3f, "完了時は基準位置");
        Assert.AreEqual(1f, pose.scale, 1e-4f);
    }

    [Test]
    public void Slide_OffsetShrinksOverTime()
    {
        var early = ResultReveal.Evaluate(ResultReveal.Kind.Slide, 0.05f, 0.2f, new Vector2(90f, 0f));
        var late = ResultReveal.Evaluate(ResultReveal.Kind.Slide, 0.15f, 0.2f, new Vector2(90f, 0f));
        Assert.Greater(early.offset.magnitude, late.offset.magnitude, "だんだん基準位置へ近づく");
    }

    // ---- Slam(デーン) ----

    [Test]
    public void Slam_StartsBig_LandsAtOne()
    {
        var start = ResultReveal.Evaluate(ResultReveal.Kind.Slam, 0.001f, 0.4f, Vector2.zero);
        Assert.Greater(start.scale, 2.0f, "出現直後は大きい");
        var end = ResultReveal.Evaluate(ResultReveal.Kind.Slam, 0.4f, 0.4f, Vector2.zero);
        Assert.AreEqual(1f, end.scale, 1e-3f, "終了時は等倍");
        Assert.AreEqual(1f, end.alpha, 1e-4f);
    }

    [Test]
    public void Slam_HasBounceAfterLanding()
    {
        // 着地(60%)後のバウンド区間でわずかに等倍を超える
        float landT = 0.4f * ResultReveal.SlamLandPortion;
        var mid = ResultReveal.Evaluate(ResultReveal.Kind.Slam, landT + 0.4f * (1f - ResultReveal.SlamLandPortion) * 0.5f, 0.4f, Vector2.zero);
        Assert.Greater(mid.scale, 1.02f, "着地後にバウンドで膨らむ");
    }

    // ---- Ring(衝撃波) ----

    [Test]
    public void Ring_BeforeDelay_IsHidden()
    {
        var pose = ResultReveal.Evaluate(ResultReveal.Kind.Ring, -0.1f, 0.7f, Vector2.zero);
        Assert.AreEqual(0f, pose.alpha, 1e-4f);
        Assert.AreEqual(ResultReveal.RingStartScale, pose.scale, 1e-4f);
    }

    [Test]
    public void Ring_GrowsWhileFadingOut()
    {
        var early = ResultReveal.Evaluate(ResultReveal.Kind.Ring, 0.1f, 0.7f, Vector2.zero);
        var late = ResultReveal.Evaluate(ResultReveal.Kind.Ring, 0.6f, 0.7f, Vector2.zero);
        Assert.Greater(early.alpha, late.alpha, "だんだん薄くなる");
        Assert.Less(early.scale, late.scale, "だんだん大きくなる");
    }

    [Test]
    public void Ring_End_IsInvisibleAtFullScale()
    {
        var pose = ResultReveal.Evaluate(ResultReveal.Kind.Ring, 0.7f, 0.7f, Vector2.zero);
        Assert.AreEqual(0f, pose.alpha, 1e-4f);
        Assert.AreEqual(ResultReveal.RingEndScale, pose.scale, 1e-3f);
    }

    [Test]
    public void Ring_SkipLeavesItInvisible()
    {
        // スキップ(elapsed=999)で完了状態=非表示のままになる
        var pose = ResultReveal.Evaluate(ResultReveal.Kind.Ring, 999f, 0.7f, Vector2.zero);
        Assert.AreEqual(0f, pose.alpha, 1e-4f);
    }

    // ---- 登録と駆動 ----

    [Test]
    public void Add_HidesImmediately_AndTickReveals()
    {
        var canvasGo = new GameObject("canvas", typeof(Canvas));
        created.Add(canvasGo);
        var reveal = ResultReveal.Ensure(canvasGo.GetComponent<Canvas>());

        var el = new GameObject("el", typeof(RectTransform));
        el.transform.SetParent(canvasGo.transform, false);
        el.GetComponent<RectTransform>().anchoredPosition = new Vector2(10f, 20f);

        reveal.Add(el, 0.5f, 0.2f, ResultReveal.Kind.Slide, new Vector2(90f, 0f));
        var cg = el.GetComponent<CanvasGroup>();
        Assert.IsNotNull(cg, "CanvasGroup が自動付与される");
        Assert.AreEqual(0f, cg.alpha, 1e-4f, "登録直後は非表示");

        reveal.Tick(0.2f);
        Assert.AreEqual(0f, cg.alpha, 1e-4f, "delay 前は非表示のまま");

        reveal.Tick(0.7f);
        Assert.AreEqual(1f, cg.alpha, 1e-4f, "delay+duration 後は全表示");
        Assert.AreEqual(10f, el.GetComponent<RectTransform>().anchoredPosition.x, 1e-3f, "基準位置に戻る");
    }

    [Test]
    public void Ensure_IsIdempotent()
    {
        var canvasGo = new GameObject("canvas", typeof(Canvas));
        created.Add(canvasGo);
        var a = ResultReveal.Ensure(canvasGo.GetComponent<Canvas>());
        var b = ResultReveal.Ensure(canvasGo.GetComponent<Canvas>());
        Assert.AreSame(a, b);
    }
}
