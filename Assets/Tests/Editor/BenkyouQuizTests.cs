using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 勉強の裏譜面 Q 第3版(2026-10-28、spec_v3)。決まり(QuizRules)の純関数と、読み込み。
public class BenkyouQuizRulesTests
{
    [Test]
    public void PapersFadeInAndOutInAQuarterSecondAndJoinWhenTheyTouch()
    {
        var quiz = new QuizChartData();
        quiz.papers.Add(new QuizPaperData { startMs = 97674, endMs = 112674 });
        quiz.papers.Add(new QuizPaperData { startMs = 29800, endMs = 49400 });
        quiz.papers.Add(new QuizPaperData { startMs = 112674, endMs = 137550 });
        quiz.papers.Add(new QuizPaperData { startMs = 5000, endMs = 5000 });
        var spans = QuizRules.BuildPapers(quiz, .1);
        Assert.AreEqual(2, spans.Count, "長さの無い時間帯は捨て、接する C と D は1枚にする");
        Assert.AreEqual(29.9, spans[0].Start, 1e-9);
        Assert.AreEqual(49.5, spans[0].End, 1e-9);
        Assert.AreEqual(97.774, spans[1].Start, 1e-9);
        Assert.AreEqual(137.65, spans[1].End, 1e-9);
        Assert.AreEqual(0f, QuizRules.PaperVisibility(spans, 29.9));
        Assert.AreEqual(.5f, QuizRules.PaperVisibility(spans, 29.9 + .125), 1e-5);
        Assert.AreEqual(1f, QuizRules.PaperVisibility(spans, 29.9 + .25), 1e-5, "0.25 秒で黒い紙になる");
        Assert.AreEqual(1f, QuizRules.PaperVisibility(spans, 40));
        Assert.AreEqual(.5f, QuizRules.PaperVisibility(spans, 49.5 - .125), 1e-5);
        Assert.AreEqual(0f, QuizRules.PaperVisibility(spans, 49.5), "終わりの 0.25 秒で戻す");
        Assert.AreEqual(1f, QuizRules.PaperVisibility(spans, 112.774), "C と D の境目で黒を戻さない");
        Assert.AreEqual(0f, QuizRules.PaperVisibility(spans, 60));
        Assert.AreEqual(0f, QuizRules.PaperVisibility(spans, double.NaN));
        Assert.IsTrue(QuizRules.InPaper(spans, spans[0].Start));
        Assert.IsTrue(QuizRules.InPaper(spans, 112.774));
        Assert.IsFalse(QuizRules.InPaper(spans, spans[0].Start - .01));
    }

    [Test]
    public void LaunchStartsOnTheCameraRayThroughTheLabelAndLandsOnTheTarget()
    {
        var camera = new Vector3(0f, 2.35f, -7f);
        var label = new Vector2(-2.1f, 1.1f);
        var landing = new Vector2(0f, -.15f);
        Vector2 far = QuizRules.LaunchFar(camera, label, 0f, 20f);
        float k = (20f - camera.z) / (0f - camera.z);
        Assert.AreEqual(camera.x + (label.x - camera.x) * k, far.x, 1e-4);
        Assert.AreEqual(camera.y + (label.y - camera.y) * k, far.y, 1e-4);
        Vector3 toLabel = (new Vector3(label.x, label.y, 0f) - camera).normalized;
        Vector3 toFar = (new Vector3(far.x, far.y, 20f) - camera).normalized;
        Assert.AreEqual(1f, Vector3.Dot(toLabel, toFar), 1e-6, "カメラ・札・奥の点が一直線(画面では札の上に重なる)");
        Assert.AreEqual(far - landing, QuizRules.LaunchOffset(far, landing, 20f, 0f, 20f), "出る瞬間(根元が spawnZ)は奥の点");
        Vector2 half = QuizRules.LaunchOffset(far, landing, 10f, 0f, 20f);
        Assert.AreEqual((far.x - landing.x) * .5f, half.x, 1e-5, "途中はまっすぐ(ずれ = (奥の点 − 着地点) × 残り ÷ 接近)");
        Assert.AreEqual((far.y - landing.y) * .5f, half.y, 1e-5);
        Assert.AreEqual(Vector2.zero, QuizRules.LaunchOffset(far, landing, 0f, 0f, 20f), "届く時刻に着地点");
        Assert.AreEqual(Vector2.zero, QuizRules.LaunchOffset(far, landing, -3f, 0f, 20f), "届いたあとは着地点のまま");
        Assert.AreEqual(far - landing, QuizRules.LaunchOffset(far, landing, 25f, 0f, 20f), "奥より遠くても奥の点まで");
        Assert.AreEqual(label, QuizRules.LaunchFar(null, label, 0f, 20f), "カメラが無ければ札の真後ろ");
        Assert.AreEqual(label, QuizRules.LaunchFar(new Vector3(0f, 2f, 3f), label, 0f, 20f), "判定面より奥のカメラは使わない");
    }

    [Test]
    public void DecoysLookTheSameUntilTheyArriveThenFadeInAThirdOfASecond()
    {
        Assert.AreEqual(1f, QuizRules.DecoyVisibility(9.0, 10.0));
        Assert.AreEqual(1f, QuizRules.DecoyVisibility(10.0, 10.0), "届くまでは本物と同じ姿");
        Assert.AreEqual(.5f, QuizRules.DecoyVisibility(10.15, 10.0), 1e-5, "届いたら判定面を少し過ぎながら薄れる");
        Assert.AreEqual(0f, QuizRules.DecoyVisibility(10.3, 10.0), 1e-5, "0.3 秒で消える");
        Assert.AreEqual(1f, QuizRules.DecoyVisibility(double.NaN, 10.0));
        Assert.AreEqual(.3, QuizRules.DecoyFadeSeconds);
        Assert.AreEqual(0f, QuizRules.DecoyVeil(0f, 1f));
        Assert.AreEqual(.7f, QuizRules.DecoyVeil(.7f, 1f), 1e-6, "練習の飾りは veil 0.7 のまま");
        Assert.AreEqual(.85f, QuizRules.DecoyVeil(.7f, .5f), 1e-6);
        Assert.AreEqual(1f, QuizRules.DecoyVeil(0f, 0f));
    }

    [Test]
    public void SideArrowIsHiddenOnlyForLeftAndRightArrows()
    {
        Assert.AreEqual("hidden", QuizRules.SideArrow(" Hidden ", "left"));
        Assert.AreEqual("hidden", QuizRules.SideArrow("hidden", "RIGHT"));
        Assert.AreEqual("", QuizRules.SideArrow("hidden", "up"), "左右の2択だけ");
        Assert.AreEqual("", QuizRules.SideArrow("hidden", "none"));
        Assert.AreEqual("", QuizRules.SideArrow("", "left"));
        Assert.AreEqual("", QuizRules.SideArrow("other", "left"));
        Assert.IsTrue(QuizRules.HidesSide(new NoteData { direction = "left", sideArrow = "hidden" }));
        Assert.IsFalse(QuizRules.HidesSide(new NoteData { direction = "left" }));
        Assert.IsFalse(QuizRules.HidesSide(null));
        Assert.AreEqual("left", QuizRules.Side(" LEFT "));
        Assert.AreEqual("", QuizRules.Side("up"));
        Assert.AreEqual(-1f, QuizRules.SideSign("left"));
        Assert.AreEqual(1f, QuizRules.SideSign("right"));
        Assert.AreEqual(0f, QuizRules.SideSign(""));
    }

    [Test]
    public void LabelsFitTheBoardAndTallLabelsKeepTheTextSize()
    {
        var plain = QuizRules.LabelSize(143, 180f / 143f, QuizRules.OptionHeight, QuizRules.OptionReferencePixels, QuizRules.OptionMaxWidth);
        Assert.AreEqual(QuizRules.OptionHeight, plain.y, 1e-5, "ふつうの札は高さ 0.42");
        var fraction = QuizRules.LabelSize(299, 112f / 299f, QuizRules.OptionHeight, QuizRules.OptionReferencePixels, QuizRules.OptionMaxWidth);
        Assert.AreEqual(QuizRules.OptionHeight * 299f / 143f, fraction.y, 1e-4, "分数の札(299px)は画素の比で大きく、字の大きさをそろえる");
        Assert.AreEqual(112f / 299f, fraction.x / fraction.y, 1e-4);
        var wide = QuizRules.LabelSize(143, 4f, QuizRules.OptionHeight, QuizRules.OptionReferencePixels, QuizRules.OptionMaxWidth);
        Assert.AreEqual(QuizRules.OptionMaxWidth, wide.x, 1e-5, "幅 1.1 を超えたら縮める");
        Assert.AreEqual(4f, wide.x / wide.y, 1e-4, "縦横比はそのまま");
        Assert.AreEqual(QuizRules.OptionHeight, QuizRules.LabelSize(0, 1f, QuizRules.OptionHeight, 143, 9f).y, 1e-6, "画素が分からなければ決めた高さ");
        var word = QuizRules.LabelSize(175, 701f / 175f, QuizRules.BoxWordHeight, QuizRules.BoxWordReferencePixels, QuizRules.BoxWordMaxWidth);
        Assert.AreEqual(QuizRules.BoxWordHeight, word.y, 1e-5, "箱の語は高さ 0.36");
        Assert.LessOrEqual(word.x, QuizRules.BoxWordMaxWidth + 1e-5f, "箱の語は幅 1.8 まで");
        var tooWide = QuizRules.FitSize(5f, .30f, 1.35f);
        Assert.AreEqual(1.35f, tooWide.x, 1e-5, "幅 1.35 を超えたら縮める");
        Assert.AreEqual(1f, QuizRules.FitSize(float.NaN, 1f, 0f).x, "壊れた縦横比は 1");

        CollectionAssert.AreEqual(new[] { -1.8f, -.6f, .6f, 1.8f }, QuizRules.PillarX);
        for (int i = 1; i < QuizRules.PillarX.Length; i++)
            Assert.AreEqual(QuizRules.PillarSpacing, QuizRules.PillarX[i] - QuizRules.PillarX[i - 1], 1e-6, "柱は 1.2 おき");
        Assert.AreEqual(.45f, QuizRules.PillarHitScale);
        Assert.AreEqual(3, QuizRules.NearestPillar(1.4f));
        Assert.AreEqual(0, QuizRules.NearestPillar(-9f));
        Assert.Greater(QuizRules.OptionY - QuizRules.OptionHeight * .5f, QuizRules.GateTopY, "札はゲートの上");
        Assert.Greater(QuizRules.RhythmQuestionY - QuizRules.RhythmQuestionHeight * .5f, QuizRules.OptionY + fraction.y * .5f,
            "問題は札(背の高い分数の札も)の上");
        Assert.Less(QuizRules.OptionMaxWidth, QuizRules.PillarSpacing, "隣の札と重ねない");
        CollectionAssert.AreEqual(new[] { -2.1f, -.7f, .7f, 2.1f }, QuizRules.SortLabelX);
        Assert.Less(QuizRules.SortLabelMaxWidth, 1.4f, "斬り分けの札は札の間 1.4 に収める");
        Assert.AreEqual(new Vector2(-3.7f, -.15f), QuizRules.BoxCenter(-1f));
        Assert.AreEqual(new Vector2(3.7f, -.15f), QuizRules.BoxCenter(1f));
        Assert.Less(QuizRules.BoxWordMaxWidth, QuizRules.BoxWidth, "語は箱の中");
        for (int s = -1; s <= 1; s += 2)
            for (int i = 0; i < 4; i++)
            {
                Vector2 slot = QuizRules.BoxSlot(s, i);
                Assert.Less(Mathf.Abs(slot.x - QuizRules.BoxX * s) + QuizRules.BoxLabelMaxWidth * .5f, QuizRules.BoxWidth * .5f, "箱の中 " + i);
                Assert.Less(slot.y + QuizRules.BoxLabelHeight * .5f, QuizRules.BoxWordCenter(s).y - QuizRules.BoxWordHeight * .5f, "語の下 " + i);
                Assert.Greater(slot.y - QuizRules.BoxLabelHeight * .5f, QuizRules.BoxY - QuizRules.BoxHeight * .5f, "箱の下の縁より上 " + i);
            }
        Assert.AreNotEqual(QuizRules.BoxSlot(1f, 0), QuizRules.BoxSlot(1f, 1), "積んだ札は重ねない");
    }

    [Test]
    public void SortBoardsComeEarlyOnlyForTheSlowApproachAndLabelsSlideIntoTheBox()
    {
        double show = 79.674, play = show + 1.5;
        Assert.AreEqual(show, QuizRules.SortBoardStart(show, play - 1.0), 1e-9, "接近 1.0 秒は問題と同時");
        Assert.AreEqual(show, QuizRules.SortBoardStart(show, play - .8), 1e-9);
        Assert.AreEqual(play - 2.0 - .2, QuizRules.SortBoardStart(show, play - 2.0), 1e-9, "接近 2.0 秒は最初の金が出る 0.2 秒前");
        Assert.AreEqual(show, QuizRules.SortBoardStart(show, double.NaN), 1e-9);
        Assert.AreEqual(0f, QuizRules.SortSlide(10, 10));
        Assert.AreEqual(0f, QuizRules.SortSlide(10.24, 10), "遅めの判定の窓(0.18 秒)が閉じるまでは動かさない(答えを見てから切れない)");
        Assert.Greater(QuizRules.SortSlide(10.29, 10), .5f, "終わりはゆっくり");
        Assert.AreEqual(1f, QuizRules.SortSlide(10.34, 10), 1e-6, "届いて 0.24 秒後から 0.10 秒で箱へ");
        Assert.AreEqual(QuizRules.SortDimAlpha, QuizRules.SortLabelAlpha(9.9, 10));
        Assert.AreEqual(1f, QuizRules.SortLabelAlpha(10.1, 10), 1e-5, "金が出たら少し明るく");
        Assert.Less(QuizRules.SortDimAlpha, 1f);
        var papers = new List<QuizRules.Span> { new QuizRules.Span(29.8, 49.4), new QuizRules.Span(79.674, 91.674) };
        var covered = QuizRules.CoverBoard(papers, 78.974, QuizRules.SortCoverLeadSeconds);
        Assert.AreEqual(78.974, covered[1].Start, 1e-9, "1問目の板と一緒に紙も出す");
        Assert.AreEqual(91.674, covered[1].End, 1e-9);
        Assert.AreEqual(29.8, covered[0].Start, 1e-9);
        Assert.AreEqual(79.674, QuizRules.CoverBoard(papers, 79.674, 2.5)[1].Start, 1e-9, "紙と同時ならそのまま");
        Assert.AreEqual(79.674, QuizRules.CoverBoard(papers, 80.5, 2.5)[1].Start, 1e-9, "紙の中ならそのまま");
        Assert.AreEqual(79.674, QuizRules.CoverBoard(papers, 70.0, 2.5)[1].Start, 1e-9, "遠すぎる板では動かさない");
    }

    [Test]
    public void CountGoesUpAtEveryArrivalAndTheSlotIsMeasuredFromTheTopLeft()
    {
        var times = new List<double> { 1, 1, 2.5 };
        Assert.AreEqual(0, QuizRules.CountAt(times, .99));
        Assert.AreEqual(2, QuizRules.CountAt(times, 1), "同時の2つは一度に 2");
        Assert.AreEqual(3, QuizRules.CountAt(times, 9));
        Assert.AreEqual(0, QuizRules.CountAt(null, 9));
        Vector2 slot = QuizRules.SlotPoint(new Vector2(0f, 2f), new Vector2(4f, 1f), .75f, .25f);
        Assert.AreEqual(1f, slot.x, 1e-6, "x は左から");
        Assert.AreEqual(2.25f, slot.y, 1e-6, "y は上から");
        Assert.AreEqual(.85f * .62f, QuizRules.GlyphHeight(0, 100, .85f, .85f * .62f, .2f, .85f), 1e-6, "画素が分からなければ決めた高さ");
        Assert.AreEqual(.85f * 150f / 208f, QuizRules.GlyphHeight(150, 208, .85f, .5f, .2f, .85f), 1e-6,
            "問題と同じ字の大きさで書き出した数は画素の比");
        Assert.AreEqual(.5f, QuizRules.GlyphHeight(500, 100, .85f, .5f, .2f, .85f), 1e-6, "比が外れたら決めた高さ");
    }

    [Test]
    public void PiCellsFollowTheRowLengthAndFullRowsScrollUp()
    {
        Assert.AreEqual(.38f, QuizRules.PiPitch(10), 1e-6, "10 マスの行は 0.38 おき");
        Assert.AreEqual(.342f, QuizRules.PiPitch(11), 1e-6, "11 マスの行はゲートの中に収まるよう 0.342 おき");
        Assert.AreEqual(.38f, QuizRules.PiPitch(9), 1e-6);
        Assert.AreEqual(.38f, QuizRules.PiPitch(1), 1e-6);
        Assert.AreEqual(-1.71f, QuizRules.CellX(0, 10), 1e-6);
        Assert.AreEqual(1.71f, QuizRules.CellX(9, 10), 1e-5, "10 マスの行の右の端");
        Assert.AreEqual(1.71f, QuizRules.CellX(10, 11), 1e-5, "11 マスの行の右の端も同じ");
        Assert.AreEqual(-1.71f + .38f * 8, QuizRules.CellX(8, 9), 1e-5);
        var rows = new List<int> { 10, 11, 9, 10 };
        QuizRules.CellOf(rows, 0, out int row, out int column);
        Assert.AreEqual((0, 0), (row, column));
        QuizRules.CellOf(rows, 9, out row, out column);
        Assert.AreEqual((0, 9), (row, column));
        QuizRules.CellOf(rows, 10, out row, out column);
        Assert.AreEqual((1, 0), (row, column), "行が埋まったら次の行の左から");
        QuizRules.CellOf(rows, 20, out row, out column);
        Assert.AreEqual((1, 10), (row, column), "11 マスの行");
        QuizRules.CellOf(rows, 39, out row, out column);
        Assert.AreEqual((3, 9), (row, column));
        QuizRules.CellOf(null, 23, out row, out column);
        Assert.AreEqual((2, 3), (row, column), "rows が無ければ 10 マスずつ");
        Assert.AreEqual(11, QuizRules.RowLength(rows, 1));
        Assert.AreEqual(QuizRules.PiRowCells, QuizRules.RowLength(rows, 7));
        var ends = new List<double> { 10, 20, double.NaN };
        Assert.AreEqual(-.15f, QuizRules.RowY(1, ends, 15), 1e-6, "書く行は y −0.15");
        Assert.AreEqual(-.15f + .9f, QuizRules.RowY(0, ends, 10.3), 1e-5, "行が埋まると 0.3 秒で 0.9 上へ");
        Assert.AreEqual(-.15f + 1.8f, QuizRules.RowY(0, ends, 20.5), 1e-5, "前の行もさらに上へ");
        Assert.AreEqual(-.15f + .9f, QuizRules.RowY(1, ends, 20.5), 1e-5);
        Assert.AreEqual(-.15f, QuizRules.RowY(2, ends, 30), 1e-6);
        Assert.Greater(QuizRules.RowY(0, ends, 10.1), -.15f + .45f, "送りの終わりはゆっくり");
        Assert.AreEqual("31415", QuizRules.Digits("3.1415"));
        Assert.AreEqual(4, QuizRules.Digit("31415", 2));
        Assert.AreEqual(-1, QuizRules.Digit("31415", 9));
    }

    [Test]
    public void RedPenCirclesDrawInAFifthOfASecondAndEdgeFlashesFadeInAThirdOfASecond()
    {
        Assert.AreEqual(0f, QuizRules.PenProgress(9.9, 10));
        Assert.AreEqual(.5f, QuizRules.PenProgress(10.1, 10), 1e-5);
        Assert.AreEqual(1f, QuizRules.PenProgress(11, 10));
        Assert.AreEqual(0f, QuizRules.PenProgress(11, double.NaN));
        Assert.AreEqual(new Color(.92f, .18f, .16f), QuizRules.RedPen, "赤ペン");
        Assert.AreEqual(.07f, QuizRules.RedPenWidth);
        Assert.IsTrue(QuizRules.OptionFlashing(10.1, 10));
        Assert.IsFalse(QuizRules.OptionFlashing(10.16, 10), "0.15 秒だけ");
        Assert.IsFalse(QuizRules.OptionFlashing(10.1, double.NaN));
        Assert.AreEqual(QuizRules.EdgeFlashAlpha, QuizRules.EdgeFlashOpacity(0), 1e-6);
        Assert.AreEqual(0f, QuizRules.EdgeFlashOpacity(.3));
        Assert.AreEqual(0f, QuizRules.EdgeFlashOpacity(-.01));
        Assert.IsTrue(QuizRules.Crossed(1, 2, 2));
        Assert.IsFalse(QuizRules.Crossed(2, 2, 2), "止まっている間は越えない");
        Assert.IsFalse(QuizRules.Crossed(3, 2, 2.5), "巻き戻しでは越えない");
        Assert.AreEqual(0f, QuizRules.FlowerAmount(9, 10));
        Assert.AreEqual(1f, QuizRules.FlowerAmount(10.25, 10), 1e-6);
    }

    [Test]
    public void HitScaleZeroAndBrokenValuesMeanOne()
    {
        Assert.AreEqual(1f, QuizRules.HitScale(0f));
        Assert.AreEqual(1f, QuizRules.HitScale(-.5f));
        Assert.AreEqual(1f, QuizRules.HitScale(float.NaN));
        Assert.AreEqual(1f, QuizRules.HitScale(float.PositiveInfinity));
        Assert.AreEqual(.5f, QuizRules.HitScale(.5f));
    }

    [Test]
    public void LoaderReadsTheThirdEditionAndDropsBrokenParts()
    {
        var chart = ChartLoader.Parse("{\"bpm\":160,\"notes\":["
            + "{\"time\":1000,\"x\":0,\"y\":-0.15,\"type\":\"direction\",\"direction\":\"left\",\"color\":\"gold\","
            + "\"launch\":true,\"fromX\":-2.1,\"fromY\":1.1,\"sideArrow\":\" Hidden \"},"
            + "{\"time\":2000,\"x\":0,\"y\":-0.15,\"type\":\"direction\",\"direction\":\"up\",\"sideArrow\":\"hidden\",\"fromX\":3},"
            + "{\"time\":3000,\"x\":0.5,\"y\":0,\"launch\":true,\"fromX\":99,\"fromY\":-99,\"hidden\":true,\"hitScale\":0.45}],"
            + "\"quiz\":{\"papers\":[{\"startMs\":5000,\"endMs\":9000}],"
            + "\"decoys\":[{\"ms\":7000,\"x\":0.6,\"y\":-0.15,\"veil\":0.7},{\"ms\":6000,\"x\":1.8,\"y\":-0.15,\"veil\":3}],"
            + "\"items\":["
            + "{\"kind\":\" Sort \",\"showMs\":5000,\"playMs\":6500,\"endMs\":8000,\"question\":\"h.png\","
            + "\"options\":[\" a.png \",\"b.png\"],\"sides\":[\"LEFT\",\"up\",\"right\"],\"boxes\":[\"l.png\",\"r.png\"]},"
            + "{\"kind\":\"pi\",\"showMs\":8000,\"playMs\":8000,\"endMs\":9000,\"question\":\"p.png\",\"digits\":\"3.1415\","
            + "\"rows\":[3,0,2],\"numbers\":[\"n0.png\"],\"flashesMs\":[8800,8200],\"revealMs\":8900,\"slotX\":0.2,\"slotY\":1.5},"
            + "{\"kind\":\"rhythm\",\"showMs\":1000,\"playMs\":2000,\"endMs\":3000,\"answer\":7,\"hint\":true},"
            + "{\"kind\":\"count\",\"showMs\":20,\"playMs\":10,\"endMs\":10,\"count\":-3}]}}");
        Assert.AreEqual(3, chart.notes.Count);
        var launched = chart.notes[0];
        Assert.IsTrue(launched.launch);
        Assert.AreEqual(-2.1f, launched.fromX, 1e-6);
        Assert.AreEqual(1.1f, launched.fromY, 1e-6);
        Assert.AreEqual("hidden", launched.sideArrow, "隠した向きの名前をそろえる");
        Assert.AreEqual("", chart.notes[1].sideArrow, "上下の矢印は隠さない");
        Assert.AreEqual(0f, chart.notes[1].fromX, "発射点でないノーツの札の位置は 0");
        Assert.AreEqual(8f, chart.notes[2].fromX, 1e-6, "札の位置は画面の中へ");
        Assert.AreEqual(-4f, chart.notes[2].fromY, 1e-6);
        Assert.IsTrue(chart.notes[2].hidden, "見えないノーツ(第2版)のコードは残す");
        Assert.AreEqual(.45f, chart.notes[2].hitScale, 1e-6);

        var quiz = chart.quiz;
        Assert.IsTrue(quiz.HasContent);
        CollectionAssert.AreEqual(new[] { 6000f, 7000f }, quiz.decoys.Select(d => d.ms), "飾りは時刻順");
        Assert.AreEqual(1f, quiz.decoys[0].veil, "見えにくさは 0〜1");
        Assert.AreEqual(.7f, quiz.decoys[1].veil, 1e-6);
        Assert.AreEqual(3, quiz.items.Count, "終わりが始まりより前の問題は捨てる");
        var rhythm = quiz.items[0];
        var sort = quiz.items[1];
        var pi = quiz.items[2];
        Assert.AreEqual(QuizRules.Rhythm, rhythm.kind);
        Assert.AreEqual(-1, rhythm.answer, "柱の外の答えは使わない");
        Assert.AreEqual(QuizRules.Sort, sort.kind, "種類の名前をそろえる");
        CollectionAssert.AreEqual(new[] { "a.png", "b.png" }, sort.options);
        CollectionAssert.AreEqual(new[] { "left", "", "right" }, sort.sides);
        CollectionAssert.AreEqual(new[] { "l.png", "r.png" }, sort.boxes);
        Assert.AreEqual(QuizRules.Pi, pi.kind);
        Assert.AreEqual("31415", pi.digits, "点を除いた数字");
        CollectionAssert.AreEqual(new[] { 3, 2 }, pi.rows, "0 の行は捨てる");
        CollectionAssert.AreEqual(new[] { 8200f, 8800f }, pi.flashesMs);
        Assert.AreEqual(.2f, pi.slotX, 1e-6);
        Assert.AreEqual(1f, pi.slotY, "割合は 0〜1");
        Assert.AreEqual("", pi.markImage);
        Assert.AreEqual("", pi.pointImage);

        var plain = ChartLoader.Parse("{\"notes\":[{\"time\":0}]}");
        Assert.NotNull(plain.quiz);
        Assert.IsFalse(plain.quiz.HasContent, "ふつうの譜面は空のまま");
        Assert.IsFalse(plain.notes[0].launch);
        Assert.AreEqual("", plain.notes[0].sideArrow);
        Assert.IsFalse(plain.notes[0].hidden);
    }
}

// 見えないノーツ(NoteData.hidden)。合図を出さずに、判定・採点・音はふつうのノーツと同じ。Q の第2版で使った(コードは残す)。
public class BenkyouQuizHiddenNoteTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("BenkyouQuizHiddenNoteTests");
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.approachTime = 1f;
        spawner.ConfigureFloorGuide(-3f);
        score = root.AddComponent<ScoreManager>();
        score.Reset();
        score.Bind(spawner);
    }

    [TearDown]
    public void TearDown()
    {
        if (root != null) Object.DestroyImmediate(root);
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var link in Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (link != null) Object.DestroyImmediate(link.gameObject);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    // 見えないノーツは、ゲートの外の光(GateAccents の光の筋・金の星・ロングの満ち引き)も出さない。
    [Test]
    public void HiddenNotesGiveNoGateStreakStarOrLongFill()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        var gate = new GameObject("JudgeGate");
        gate.transform.SetParent(root.transform, false);
        gate.AddComponent<JudgeGateFrame>().Build(new Vector3(11, 6, 1));
        var accents = GateAccents.Create(spawner, score, null);
        Assert.NotNull(accents, "ゲートの光を作る");
        try
        {
            var chart = new ChartData { bpm = 160 };
            chart.notes.Add(new NoteData { time = 2000, x = -1.6f, y = -.15f, hidden = true });
            chart.notes.Add(new NoteData { time = 2300, x = 0f, y = -.35f, hidden = true, color = "gold" });
            chart.notes.Add(new NoteData { time = 2600, x = 1.5f, y = -.15f, hidden = true, type = "long", count = 2, lengthMs = 600 });
            chart.notes.Add(new NoteData { time = 3600, x = 1f, y = 0f });
            spawner.SetChart(chart);
            spawner.Tick(1.7);
            var hidden = spawner.LiveNotes.OrderBy(n => n.HitTime).ToList();
            Assert.AreEqual(3, hidden.Count);
            foreach (var note in hidden)
            {
                Assert.IsTrue(note.IsHidden);
                spawner.Tick(note.HitTime);
                Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, note.HitTime),
                    "見えないノーツも切れる");
            }
            accents.Tick(.1f);
            Assert.GreaterOrEqual(score.PerfectCount, 2, "採点はふつうどおり");
            Assert.AreEqual(0, accents.StreakCount, "見えないノーツの Perfect でも光の筋を飛ばさない");
            Assert.AreEqual(0, accents.StarCount, "金の星も出さない");
            Assert.AreEqual(0f, accents.FillLevel, 1e-6, "ロングの満ち引きも出さない");

            spawner.Tick(2.9);
            var visible = spawner.LiveNotes.First(n => n != null && !n.IsHidden);
            spawner.Tick(visible.HitTime);
            Assert.IsTrue(visible.CutAtSongTime(visible.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, visible.HitTime));
            Assert.AreEqual(1, accents.StreakCount, "見えるノーツの Perfect はいつもどおり光の筋を飛ばす");
        }
        finally
        {
            // EditMode では OnDestroy が呼ばれないので、作った材質とメッシュをここで片付ける。
            var renderer = accents.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null) Object.DestroyImmediate(renderer.sharedMaterial);
            if (accents.Mesh != null) Object.DestroyImmediate(accents.Mesh);
        }
    }

    [Test]
    public void HiddenNotesShowNoBodyArrowFrameFloorMarkCountOrLinkButAreJudged()
    {
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 2000, x = -1.6f, y = -.15f, hidden = true, type = "long", count = 2, lengthMs = 600, direction = "left" });
        chart.notes.Add(new NoteData { time = 2000, x = 1.5f, y = -.15f, hidden = true, hitScale = .5f });
        spawner.SetChart(chart);
        spawner.Tick(1.45);
        Assert.AreEqual(2, spawner.LiveNotes.Count);
        foreach (var note in spawner.LiveNotes)
        {
            Assert.IsTrue(note.IsHidden);
            Assert.AreEqual(1f, note.Veil, "合図ごと隠す");
            Assert.IsFalse(note.BodyRenderer.enabled, "本体を出さない");
            Assert.AreEqual(1f, note.Visuals.Veil);
            Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf, "着地の枠を出さない");
            Assert.IsFalse(note.TimingCue.ApproachRoot.activeSelf);
        }
        var longNote = spawner.LiveNotes.First(n => n.RequiredCutCount == 2);
        var small = spawner.LiveNotes.First(n => n.RequiredCutCount == 1);
        Assert.IsFalse(longNote.Arrow.gameObject.activeSelf, "矢印を出さない");
        Assert.IsFalse(longNote.countLabel.gameObject.activeSelf, "ロングの数字を出さない");
        Assert.AreEqual(0, spawner.FloorGuide.MarkerCount, "床の印を出さない");
        Assert.AreEqual(0, Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None).Length, "同時押しの線を出さない");
        Assert.AreEqual(1f, longNote.HitScale, "0 は 1");
        Assert.AreEqual(.5f, small.HitScale);

        spawner.Tick(2.0);
        Assert.IsTrue(longNote.IsJudgeable, "受付窓はふつうどおり");
        Assert.IsTrue(small.IsJudgeable);
        Assert.AreEqual(2.0, longNote.HitTime, 1e-9);
        Assert.AreEqual(new Vector2(-1.6f, -.15f), longNote.LandingPosition);

        // 使い回しで次に出るときは、ふつうのノーツへ戻る。
        small.ResetForSpawn();
        Assert.IsFalse(small.IsHidden);
        Assert.AreEqual(1f, small.HitScale);
    }

    [Test]
    public void HiddenNotesAreCutAndScoredLikeAnyNoteWithoutPiecesOrCutLight()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 2000, x = -1.6f, y = -.15f, hidden = true });
        chart.notes.Add(new NoteData { time = 2300, x = 0f, y = -.35f, hidden = true, color = "gold" });
        chart.notes.Add(new NoteData { time = 2600, x = 1f, y = 0f });
        spawner.SetChart(chart);
        spawner.Tick(1.7);
        Assert.AreEqual(3, spawner.LiveNotes.Count);
        Assert.AreEqual(3, score.ComboUnits, "見えないノーツも満コンボに数える");
        var feedback = root.GetComponentInChildren<GameplayCutFeedback>();
        Assert.NotNull(feedback);
        var notes = spawner.LiveNotes.OrderBy(n => n.HitTime).ToList();
        // ほかのテストの残りの破片は数えない。
        var earlier = new HashSet<SlicePieceDecay>(Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        List<SlicePieceDecay> NewPieces() => Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(p => !earlier.Contains(p)).ToList();

        for (int i = 0; i < 2; i++)
        {
            var note = notes[i];
            spawner.Tick(note.HitTime);
            Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, note.HitTime));
            Assert.IsTrue(note.IsCut, "見えないノーツも切れる");
        }
        Assert.AreEqual(2, score.PerfectCount, "ふつうに採点する");
        Assert.AreEqual(0, feedback.ActiveCount, "切った光を出さない");
        var hiddenPieces = NewPieces();
        Assert.Greater(hiddenPieces.Count, 0, "破片は作られる(描かないだけ)");
        foreach (var piece in hiddenPieces)
            Assert.IsFalse(piece.GetComponent<MeshRenderer>().enabled, "見えないノーツの破片は描かない");
        foreach (var piece in hiddenPieces) earlier.Add(piece);

        var visible = notes[2];
        spawner.Tick(visible.HitTime);
        Assert.IsTrue(visible.CutAtSongTime(visible.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, visible.HitTime));
        Assert.AreEqual(1, feedback.ActiveCount, "見えるノーツはいつもどおり光る");
        Assert.IsTrue(NewPieces().Any(p => p.GetComponent<MeshRenderer>().enabled), "見えるノーツの破片は描く");

        Assert.AreEqual(3, score.HitCount);
        Assert.AreEqual(3, score.PerfectCount);
        Assert.AreEqual(ScoreManager.NoteScore, score.Score, "全部 Perfect でノーツの点 90000(金は重み2)");
        score.FinalizeScore();
        Assert.AreEqual(ScoreManager.MaxScore, score.Score, "満点は 100000 のまま");
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }
}

// 当たりの大きさの倍率(NoteData.hitScale)。SaberCutJudge の当たり(と CameraImuJudgment)がノーツの当たり半径に掛ける。
// Q の答えの柱の本物は 0.45 倍で、隣の柱(1.2 離れ、薄い四角の内側の縁は 0.95)では切れない。
public class BenkyouQuizHitScaleTests
{
    readonly List<GameObject> created = new List<GameObject>();

    // 隣の柱の薄い四角(一辺 0.5)の内側の縁までの距離。
    static float NeighbourEdge => QuizRules.PillarSpacing - QuizRules.PillarSquareSize * .5f;

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
    }

    (SaberCutJudge judge, SaberTracker tracker) PointRig()
    {
        var go = new GameObject("quizPointSaber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        var judge = go.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.bladeRadius = .2f;
        judge.noteHitRadiusXY = .6f;
        return (judge, tracker);
    }

    (SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge) BladeRig()
    {
        var go = new GameObject("quizBladeSaber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        var bridge = go.AddComponent<SaberInputBridge>();
        bridge.useBladeMode = false;
        var judge = go.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.minCutSpeed = 3f;
        judge.maxCutDistance = 5f;
        judge.bladeRadius = .2f;
        judge.noteHitRadiusXY = .6f;
        return (judge, tracker, bridge);
    }

    CuttableNote Note(Vector3 position, float scale)
    {
        var go = new GameObject("quizScaledNote");
        created.Add(go);
        go.transform.position = position;
        var note = go.AddComponent<CuttableNote>();
        note.HitScale = scale;
        note.IsJudgeable = true;
        return note;
    }

    // 振りの道から 0.65 離れたノーツ。当たり 0.2 + 0.6 = 0.8 なら切れ、0.5 倍(0.2 + 0.3 = 0.5)なら切れない。
    [TestCase(1f, true)]
    [TestCase(.5f, false)]
    public void PointSwingPassingBesideTheNoteCutsOnlyAtFullSize(float scale, bool cut)
    {
        var (judge, tracker) = PointRig();
        var note = Note(new Vector3(0f, .65f, 0f), scale);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), .1f);
        judge.TryCut();
        Assert.AreEqual(cut, note.IsCut);
    }

    [TestCase(1f, true)]
    [TestCase(.5f, false)]
    public void BladeSwingPassingBesideTheNoteCutsOnlyAtFullSize(float scale, bool cut)
    {
        var (judge, tracker, bridge) = BladeRig();
        var note = Note(new Vector3(0f, .65f, 0f), scale);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(-1f, 0f, 0f), .1f);
        bridge.OverrideBlade(new Vector3(-1.1f, 0f, 0f), new Vector3(-.9f, 0f, 0f));
        judge.TryCut();
        tracker.Tick(new Vector3(0f, 0f, 0f), .1f);
        bridge.OverrideBlade(new Vector3(-.1f, 0f, 0f), new Vector3(.1f, 0f, 0f));
        judge.TryCut();
        tracker.Tick(new Vector3(1.5f, 0f, 0f), .1f);
        bridge.OverrideBlade(new Vector3(1.4f, 0f, 0f), new Vector3(1.6f, 0f, 0f));
        judge.TryCut();
        Assert.AreEqual(cut, note.IsCut);
    }

    [Test]
    public void HitRangeKeepsTheOldValueAtScaleOneAndPillarsDoNotReachTheNeighbour()
    {
        var (judge, _) = PointRig();
        var plain = Note(Vector3.zero, 1f);
        var small = Note(Vector3.zero, QuizRules.PillarHitScale);
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY, judge.HitRangeFor(plain), "倍率 1 は今までと同じ値");
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY, judge.HitRangeFor(null));
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY * QuizRules.PillarHitScale, judge.HitRangeFor(small), 1e-6);
        // 本編の値(刃 0.32・当たり 0.88、GamePlayManager の V2)。隣の柱は 1.2 離れ、その四角の内側の縁は 0.95。
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        Assert.AreEqual(.95f, NeighbourEdge, 1e-6);
        Assert.Less(judge.HitRangeFor(small), NeighbourEdge, "0.45 倍の柱は隣の柱の四角の縁まで届かない");
        Assert.Greater(judge.HitRangeFor(small), QuizRules.PillarSquareSize * .5f, "正しい柱の四角の中ならどこでも届く");
        Assert.Greater(judge.HitRangeFor(plain), NeighbourEdge, "ふつうの大きさなら隣の柱の中まで届いてしまう");
    }

    // 細い側の振りで広げた当たり(SaberCutJudge.ThinRangeFor)。倍率 1 は今までと同じ値。
    // 小さくした的は広げる量にも倍率を掛け、ScaledThinRangeLimit(0.85)まで。隣の柱の四角の縁(0.95)には届かない。
    [Test]
    public void ThinRangeIsUnchangedAtScaleOneAndCappedForSmallTargets()
    {
        const float width = 3.4f, plain = 1.2f;
        Vector2 down = Vector2.down;
        Vector2 a = new Vector2(0f, -.5f), b = new Vector2(0f, .5f); // 縦の刃を縦に振る(振りを横切る幅 0 → 広げる量 1.7)
        Assert.AreEqual(1.7f, SaberCutJudge.ThinRangeFor(a, b, down, plain, 1f, width), 1e-6, "倍率 1 は今までと同じ");
        Assert.AreEqual(plain, SaberCutJudge.ThinRangeFor(new Vector2(-1f, 0f), new Vector2(1f, 0f), down, plain, 1f, width), 1e-6,
            "腹で払う刃(幅 2.0)は広げない");
        Assert.AreEqual(plain, SaberCutJudge.ThinRangeFor(a, b, Vector2.zero, plain, 1f, width), "動いていなければ広げない");

        float small = .32f + .88f * QuizRules.PillarHitScale;
        float thin = SaberCutJudge.ThinRangeFor(a, b, down, small, QuizRules.PillarHitScale, width);
        Assert.AreEqual(1.7f * QuizRules.PillarHitScale, thin, 1e-6);
        Assert.Less(thin, NeighbourEdge, "答えの柱は、細い側で振っても隣の柱の四角の縁まで届かない");
        Assert.AreEqual(SaberCutJudge.ScaledThinRangeLimit, SaberCutJudge.ThinRangeFor(a, b, down, .5f, .8f, width), 1e-6,
            "小さくした的は 0.85 まで");
        Assert.Less(SaberCutJudge.ScaledThinRangeLimit, NeighbourEdge);
    }

    // 答えの柱(PillarHitScale 倍)を、本編の当たり(刃 0.32・当たり 0.88)とカメラの縦の刃で縦に振る(曲時計あり = 細い側で広げる道も通る)。
    // 隣の柱の側へ 0.9 ずれた所を振っても切れず、正しい柱の四角の中(0.25)なら切れる。ふつうの大きさなら 0.9 でも切れてしまう。
    [TestCase(.9f, QuizRules.PillarHitScale, false)]
    [TestCase(.25f, QuizRules.PillarHitScale, true)]
    [TestCase(.9f, 1f, true)]
    public void VerticalCameraSwingTowardTheNeighbourDoesNotCutThePillar(float offset, float scale, bool cut)
    {
        var (judge, tracker, bridge) = BladeRig();
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        judge.minCutSpeed = 2f;
        bridge.OverrideCameraInput(true);
        var note = Note(Vector3.zero, scale);
        note.HitTime = 1.0;
        tracker.ResetTo(new Vector3(offset, 3f, 0f));
        VerticalStep(judge, tracker, bridge, new Vector2(offset, 2f), .94);
        VerticalStep(judge, tracker, bridge, new Vector2(offset, 1f), .96);
        VerticalStep(judge, tracker, bridge, new Vector2(offset, -.1f), .98);
        VerticalStep(judge, tracker, bridge, new Vector2(offset, -2f), 1.0);
        Assert.AreEqual(cut, note.IsCut);
    }

    // 刃の無い点の剣でも同じ(上から下へ一息に通る振り)。
    [TestCase(.9f, false)]
    [TestCase(.25f, true)]
    public void PointSwingTowardTheNeighbourDoesNotCutThePillar(float offset, bool cut)
    {
        var (judge, tracker) = PointRig();
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        var note = Note(Vector3.zero, QuizRules.PillarHitScale);
        tracker.ResetTo(new Vector3(offset, 1.5f, 0f));
        tracker.Tick(new Vector3(offset, -1.5f, 0f), .1f);
        judge.TryCut();
        Assert.AreEqual(cut, note.IsCut);
    }

    // 縦の刃(長さ 1.0)の中心を center へ動かし、曲の時計 time で判定する。
    static void VerticalStep(SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge, Vector2 center, double time)
    {
        tracker.Tick(new Vector3(center.x, center.y, 0f), .02f);
        bridge.OverrideBlade(new Vector3(center.x, center.y - .5f, 0f), new Vector3(center.x, center.y + .5f, 0f));
        judge.TryCut(time);
    }
}

// QuizMode を曲の時計の代わりに直接進め、試験用紙・床の印・答えの柱・斬り分け・答えの形・π を書く・赤ペン・紙の縁の光と、
// ノーツと飾りの金の上げ下げを確かめる。画像は一時フォルダに作った PNG。EditMode では OnDestroy が呼ばれないので、ReleaseResources で片付ける。
public class BenkyouQuizModeTests
{
    const int R1 = 0, R2 = 1, S1 = 2, S2 = 3, C1 = 4, P = 5;
    readonly List<GameObject> created = new List<GameObject>();
    string folder;
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    Camera view;
    GameObject hud;
    QuizMode mode;
    ChartData chart;

    [SetUp]
    public void SetUp()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        folder = Path.Combine(Path.GetTempPath(), "BenkyouQuizModeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, QuizRules.ImageFolder));
        WritePng("rq.png", 400, 100);
        // 札は 143px が ふつうの高さ。ro2 は背の高い分数の札(299px)、ro3 は横に長い札。
        WritePng("ro0.png", 180, 143);
        WritePng("ro1.png", 180, 143);
        WritePng("ro2.png", 112, 299);
        WritePng("ro3.png", 700, 143);
        WritePng("sq.png", 600, 100);
        for (int i = 0; i < 4; i++) WritePng("so" + i + ".png", 100, 178);
        WritePng("sbl.png", 340, 175);
        WritePng("sbr.png", 700, 175);
        WritePng("cq.png", 950, 208);
        for (int n = 0; n <= 12; n++) WritePng("n" + n + ".png", n < 10 ? 112 : 192, 150);
        WritePng("qm.png", 108, 150);
        WritePng("pq.png", 302, 150);
        WritePng("pt.png", 77, 150);
        WritePng("el.png", 219, 150);
        WritePng("fl.png", 390, 390);

        var cameraObject = new GameObject("QuizTestCamera") { tag = "MainCamera" };
        created.Add(cameraObject);
        view = cameraObject.AddComponent<Camera>();
        view.fieldOfView = 60f;
        view.aspect = 16f / 9f;
        view.transform.SetPositionAndRotation(new Vector3(0f, 2.35f, -7f), Quaternion.Euler(12f, 0f, 0f));

        root = new GameObject("BenkyouQuizModeTests");
        created.Add(root);
        var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prefab.transform.SetParent(root.transform, false);
        prefab.AddComponent<CuttableNote>();
        prefab.SetActive(false);
        spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab;
        spawner.noteRoot = root.transform;
        spawner.reuseNotes = false;
        spawner.approachTime = 1f;
        spawner.ConfigureFloorGuide(-3f);
        score = root.AddComponent<ScoreManager>();
        score.Reset();
        score.Bind(spawner);
        hud = new GameObject("QuizTestHud");
        created.Add(hud);
        hud.AddComponent<GameHUDSkin>();

        chart = new ChartData { bpm = 100 };
        chart.quiz.papers.Add(new QuizPaperData { startMs = 10000, endMs = 20000 });
        chart.quiz.papers.Add(new QuizPaperData { startMs = 30000, endMs = 40000 });
        chart.quiz.papers.Add(new QuizPaperData { startMs = 50000, endMs = 53000 });
        chart.quiz.papers.Add(new QuizPaperData { startMs = 53000, endMs = 70000 });
        // 答えの柱: 1問目は練習(正しい柱 1)、2問目は正しい柱 2。ほかの柱は飾りの金。
        chart.quiz.items.Add(Rhythm(10000, true, 1));
        chart.quiz.items.Add(Rhythm(14000, false, 2));
        foreach (float ms in new[] { 12000f, 12300f }) Gold(ms, QuizRules.PillarX[1], QuizRules.PillarY, QuizRules.PillarHitScale);
        foreach (float ms in new[] { 16000f, 17500f }) Gold(ms, QuizRules.PillarX[2], QuizRules.PillarY, QuizRules.PillarHitScale);
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 12000, x = QuizRules.PillarX[0], y = QuizRules.PillarY, veil = .7f });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 12150, x = QuizRules.PillarX[2], y = QuizRules.PillarY, veil = .7f });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 12300, x = QuizRules.PillarX[3], y = QuizRules.PillarY, veil = .7f });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 16150, x = QuizRules.PillarX[0], y = QuizRules.PillarY });
        // 斬り分け: 1問目は練習(本当の矢印)、2問目は隠した向き。
        chart.quiz.items.Add(Sort(30000, new[] { "left", "right", "right", "left" }, false));
        chart.quiz.items.Add(Sort(33000, new[] { "right", "left", "right", "left" }, true));
        // 答えの形: 2つ。
        var count = new QuizItemData { kind = QuizRules.Count, showMs = 50000, playMs = 51500, endMs = 53000, question = "cq.png",
            count = 2, slotX = .8f, slotY = .5f, markImage = "qm.png", revealMs = 52750 };
        for (int n = 0; n <= 12; n++) count.numbers.Add("n" + n + ".png");
        chart.quiz.items.Add(count);
        Gold(52000, -.9f, -.6f);
        Gold(52750, .9f, -.6f);
        // π を書く: 2行 × 3マス(56.5 秒の同時の2つは隣どうしのマス)。
        var pi = new QuizItemData { kind = QuizRules.Pi, showMs = 53000, playMs = 53000, endMs = 70000, question = "pq.png",
            digits = "314159", pointImage = "pt.png", ellipsisImage = "el.png", flowerImage = "fl.png", revealMs = 57000,
            slotX = .2f, slotY = .6f };
        pi.rows.AddRange(new[] { 3, 3 });
        for (int n = 0; n <= 9; n++) pi.numbers.Add("n" + n + ".png");
        pi.flashesMs.AddRange(new[] { 55000f, 55250f });
        chart.quiz.items.Add(pi);
        Gold(55000, QuizRules.CellX(0, 3), QuizRules.PiLineY);
        Gold(55500, QuizRules.CellX(1, 3), QuizRules.PiLineY);
        Gold(56000, QuizRules.CellX(2, 3), QuizRules.PiLineY);
        Gold(56500, QuizRules.CellX(0, 3), QuizRules.PiLineY);
        Gold(56500, QuizRules.CellX(1, 3), QuizRules.PiLineY);
        Gold(57000, QuizRules.CellX(2, 3), QuizRules.PiLineY);
        chart.notes.Sort((a, b) => a.time != b.time ? a.time.CompareTo(b.time) : a.x.CompareTo(b.x));
        chart.quiz = QuizRules.Normalize(chart.quiz);
        spawner.SetChart(chart);
        mode = QuizMode.Create(root.transform, chart, spawner, folder, view);
    }

    static QuizItemData Rhythm(float show, bool hint, int answer)
    {
        var item = new QuizItemData { kind = QuizRules.Rhythm, showMs = show, playMs = show + 2000, endMs = show + 4000,
            question = "rq.png", hint = hint, answer = answer };
        for (int i = 0; i < 4; i++) item.options.Add("ro" + i + ".png");
        return item;
    }

    QuizItemData Sort(float show, string[] sides, bool hidden)
    {
        var item = new QuizItemData { kind = QuizRules.Sort, showMs = show, playMs = show + 1500, endMs = show + 3000, question = "sq.png" };
        for (int k = 0; k < 4; k++)
        {
            item.options.Add("so" + k + ".png");
            item.sides.Add(sides[k]);
            chart.notes.Add(new NoteData { time = show + 1500 + 375 * k, x = 0f, y = -.15f, type = "direction", direction = sides[k],
                color = "gold", launch = true, fromX = QuizRules.SortLabelX[k], fromY = QuizRules.SortLabelY,
                sideArrow = hidden ? "hidden" : "" });
        }
        item.boxes.AddRange(new[] { "sbl.png", "sbr.png" });
        return item;
    }

    void Gold(float ms, float x, float y, float hitScale = 0f)
        => chart.notes.Add(new NoteData { time = ms, x = x, y = y, color = "gold", hitScale = hitScale });

    void WritePng(string file, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, (byte)(i % 5 == 0 ? 255 : 0));
        texture.SetPixels32(pixels);
        texture.Apply();
        File.WriteAllBytes(Path.Combine(folder, QuizRules.ImageFolder, file), texture.EncodeToPNG());
        Object.DestroyImmediate(texture);
    }

    [TearDown]
    public void TearDown()
    {
        if (mode != null) mode.ReleaseResources();
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var link in Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (link != null) Object.DestroyImmediate(link.gameObject);
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    // 届く時刻 hit(と x)のノーツを出し、受付窓をその時刻へ合わせる。
    CuttableNote Spawn(double hit, float? x = null)
    {
        spawner.Tick(hit - .5);
        var note = spawner.LiveNotes.FirstOrDefault(n => Math.Abs(n.HitTime - hit) < 1e-6
            && (!x.HasValue || Math.Abs(n.LandingPosition.x - x.Value) < 1e-4));
        Assert.NotNull(note, "時刻 " + hit + " のノーツ");
        spawner.Tick(hit);
        return note;
    }

    static void Cut(CuttableNote note)
        => Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, note.HitTime));

    static void Rgba(Color expected, Color actual, string message = null)
    {
        Assert.AreEqual(expected.r, actual.r, 1e-4, message);
        Assert.AreEqual(expected.g, actual.g, 1e-4, message);
        Assert.AreEqual(expected.b, actual.b, 1e-4, message);
        Assert.AreEqual(expected.a, actual.a, 1e-4, message);
    }

    [Test]
    public void LateSpawnedDecoysStaySilentOnThePaper()
    {
        var omens = new List<CuttableNote>();
        Action<CuttableNote> listen = omens.Add;
        NoteTimingCue.GoldOmen += listen;
        try
        {
            // 本物(16.0秒)と飾り(16.15秒)を、両方の予兆の時間帯へ飛んでから初めて出す。
            double time = 16.0 - spawner.earlyJudgeWindow - .025;
            spawner.Tick(time);
            mode.Tick(time);
            var real = spawner.LiveNotes.Single(n => Math.Abs(n.HitTime - 16.0) < 1e-6);
            var decoy = spawner.LiveDecoys.Single(n => Math.Abs(n.HitTime - 16.15) < 1e-6);
            Assert.IsTrue(real.TimingCue.OmenShown);
            Assert.IsTrue(decoy.TimingCue.OmenShown);
            CollectionAssert.IsEmpty(omens, "遅れて生成されても本物・飾りとも予兆音は鳴らさない");
        }
        finally
        {
            NoteTimingCue.GoldOmen -= listen;
        }
    }

    [Test]
    public void GoldOmenLightsButStaysSilentOnThePaperForRealAndDecoyAlike()
    {
        var omens = new List<CuttableNote>();
        Action<CuttableNote> listen = omens.Add;
        NoteTimingCue.GoldOmen += listen;
        try
        {
            // 2問目(練習でない): 16.0 秒に本物(柱 2)、16.15 秒に飾り(柱 0)。練習の飾りは veil 0.7 で合図ごと隠れるので使わない。
            // 予兆は判定の窓に入る 0.2 秒前から。枠を回る光は 0.15 秒で終わるので、予兆の直前で一度止めてから細かく進める。
            double early = spawner.earlyJudgeWindow + NoteTimingCue.OmenLeadSeconds;
            spawner.Tick(15.4);
            mode.Tick(15.4);
            spawner.Tick(16.0 - early - .01);
            spawner.Tick(16.0 - early + .05);
            var real = spawner.LiveNotes.Single(n => Math.Abs(n.HitTime - 16.0) < 1e-6);
            Assert.IsTrue(real.TimingCue.OmenShown, "枠を回る光は本物にも出す");
            spawner.Tick(16.15 - early - .01);
            spawner.Tick(16.15 - early + .05);
            var decoy = spawner.LiveDecoys.Single(d => Math.Abs(d.HitTime - 16.15) < 1e-6);
            Assert.IsTrue(decoy.TimingCue.OmenShown, "飾りにも同じに出す");
            foreach (var note in new[] { real, decoy })
            {
                var runner = note.TimingCue.GhostRoot.transform.Find("GoldOmen");
                Assert.NotNull(runner);
                Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder,
                    runner.GetComponent<Renderer>().sortingOrder, "初回の予兆も本物・飾りとも紙の上");
            }
            Assert.IsTrue(real.TimingCue.MuteOmen && decoy.TimingCue.MuteOmen);
            CollectionAssert.IsEmpty(omens, "試験用紙の間は「チリッ」を鳴らさない(本物と飾りで同じなので、音で答えはばれない)");
        }
        finally
        {
            NoteTimingCue.GoldOmen -= listen;
        }
    }

    [Test]
    public void CutLightShrinksOnThePaperAndComesBackAfterwards()
    {
        mode.Tick(5);
        Assert.AreEqual(1f, spawner.CutFeedbackSizeScale, 1e-6, "紙の外はふつうの大きさ");
        mode.Tick(10.125);
        Assert.AreEqual(Mathf.Lerp(1f, QuizRules.PaperCutFeedbackScale, .5f), spawner.CutFeedbackSizeScale, 1e-4, "紙の濃さに合わせて下げる");
        mode.Tick(11);
        Assert.AreEqual(QuizRules.PaperCutFeedbackScale, spawner.CutFeedbackSizeScale, 1e-6, "紙の間は小さく(問題の字を覆わない)");
        mode.EndPlay();
        Assert.AreEqual(1f, spawner.CutFeedbackSizeScale, 1e-6, "終わりに戻す");
    }

    [Test]
    public void PaperCoversTheScreenStopsTheFloorGuideAndRestoresEverythingAtTheEnd()
    {
        Assert.NotNull(mode);
        Assert.AreEqual(31, mode.LoadedImageCount, "曲フォルダの quiz/ の画像を読む");
        CollectionAssert.IsEmpty(mode.MissingImages);
        Assert.AreEqual(3, mode.Papers.Count, "接する C と D は1枚");
        Assert.IsTrue(spawner.CutFeedbackAbovePaper, "切った光は試験用紙の上");
        Assert.AreSame(view, spawner.LaunchView, "発射点の奥の点は札を描くカメラから");
        var guide = spawner.FloorGuide.gameObject;

        mode.Tick(5);
        Assert.AreEqual(0f, mode.PaperVisibility);
        Assert.IsFalse(mode.PaperRenderer.enabled);
        var group = hud.GetComponent<CanvasGroup>();
        Assert.NotNull(group, "HUD は CanvasGroup で薄める");
        Assert.AreEqual(1f, group.alpha, 1e-5);
        Assert.IsTrue(guide.activeSelf);
        Assert.IsFalse(mode.FloorGuideStopped);

        mode.Tick(10.125);
        Assert.AreEqual(.5f, mode.PaperVisibility, 1e-4);
        Assert.AreEqual(.5f, group.alpha, 1e-4, "HUD は試験用紙と同じだけ薄める");
        Assert.IsFalse(guide.activeSelf, "紙の出入りの間も床の印は止める(飾りの金には床の印が無い)");
        Assert.IsTrue(mode.FloorGuideStopped);

        mode.Tick(11);
        Assert.AreEqual(1f, mode.PaperVisibility);
        var paper = mode.PaperRenderer;
        Assert.IsTrue(paper.enabled);
        Assert.AreEqual(MemoryVeilLayers.NoiseSortingOrder, paper.sortingOrder, "ステージより上、Q の絵・ノーツ・剣より下");
        Assert.AreSame(view.transform, paper.transform.parent, "カメラのすぐ前の板");
        foreach (var color in mode.PaperMesh.colors) Rgba(new Color(0f, 0f, 0f, 1f), color);
        foreach (var corner in mode.PaperMesh.vertices)
        {
            Vector3 viewport = view.WorldToViewportPoint(paper.transform.TransformPoint(corner));
            Assert.IsTrue(viewport.x < 0f || viewport.x > 1f, "画面の端より外まで覆う");
            Assert.IsTrue(viewport.y < 0f || viewport.y > 1f);
        }
        Assert.AreEqual(0f, group.alpha, 1e-5, "HUD(点・コンボ)は消える");
        Assert.IsTrue(mode.GateOutlineDrawn, "判定面のゲートの枠を白い細線で");
        Assert.Less(mode.PaperRenderer.sortingOrder, MemoryVeilLayers.AboveNoteSortingOrder, "紙はノーツより下");
        Assert.Greater(mode.MarkRenderer.sortingOrder, MemoryVeilLayers.AboveSortingOrder, "字と線はノーツ・切った光より手前(字を隠さない)");

        mode.Tick(25);
        Assert.AreEqual(0f, mode.PaperVisibility);
        Assert.AreEqual(1f, group.alpha, 1e-5);
        Assert.IsTrue(guide.activeSelf, "紙の外では床の印を戻す");
        Assert.IsFalse(mode.GateOutlineDrawn);
        mode.Tick(35);
        Assert.AreEqual(1f, mode.PaperVisibility);
        mode.EndPlay();
        Assert.IsTrue(mode.Ended);
        Assert.IsFalse(mode.PaperRenderer.enabled);
        Assert.AreEqual(1f, group.alpha, 1e-5, "終わりに HUD を戻す");
        Assert.IsFalse(spawner.CutFeedbackAbovePaper);
        Assert.IsTrue(guide.activeSelf, "終わりに床の印を戻す");
        Assert.IsNull(spawner.LaunchView, "発射点のカメラを戻す");
        mode.Tick(36);
        Assert.AreEqual(0f, mode.PaperVisibility, "終わったあとは描かない");
    }

    [Test]
    public void RhythmBoardPutsTheOptionsAboveThePillarsAndCirclesTheAnswerInRedPen()
    {
        mode.Tick(11);
        Assert.IsTrue(mode.ImageShown("rq.png"));
        Assert.AreEqual(QuizRules.RhythmQuestionY, mode.ImagePosition("rq.png").y, 1e-4, "問題は上");
        Assert.AreEqual(QuizRules.RhythmQuestionHeight, mode.ImageSize("rq.png").y, 1e-4);
        Assert.AreEqual(QuizRules.RhythmQuestionHeight * 4f, mode.ImageSize("rq.png").x, 1e-3, "幅は画像の縦横比");
        for (int p = 0; p < 4; p++)
        {
            string file = "ro" + p + ".png";
            Assert.AreEqual(QuizRules.PillarX[p], mode.ImagePosition(file).x, 1e-4, "札は柱の真上 " + p);
            Assert.AreEqual(QuizRules.OptionY, mode.ImagePosition(file).y, 1e-4);
            Assert.LessOrEqual(mode.ImageSize(file).x, QuizRules.OptionMaxWidth + 1e-4f);
        }
        Assert.AreEqual(QuizRules.OptionHeight, mode.ImageSize("ro0.png").y, 1e-4, "ふつうの札は高さ 0.42");
        Assert.AreEqual(QuizRules.OptionHeight * 299f / 143f, mode.ImageSize("ro2.png").y, 1e-3, "背の高い分数の札は画素の比で大きく");
        Assert.AreEqual(QuizRules.OptionMaxWidth, mode.ImageSize("ro3.png").x, 1e-4, "横に長い札は幅 1.1 に縮める");
        Assert.Less(mode.ImageSize("ro3.png").y, QuizRules.OptionHeight);
        Assert.AreEqual(4, mode.Squares, "柱の判定面の薄い四角");
        Assert.IsTrue(mode.GateOutlineDrawn);
        Assert.AreEqual(1, mode.RedCircles, "練習は正しい札を最初から赤い丸で囲む");
        Color red = QuizRules.RedPen.linear;
        Assert.IsTrue(mode.MarkRenderer.GetComponent<MeshFilter>().sharedMesh.colors.Any(c =>
            Mathf.Abs(c.r - red.r) < 1e-3f && Mathf.Abs(c.g - red.g) < 1e-3f && Mathf.Abs(c.b - red.b) < 1e-3f), "赤ペンの色");

        mode.Tick(15);
        Assert.IsTrue(mode.ImageShown("rq.png"));
        Assert.AreEqual(0, mode.RedCircles, "練習でなければ、最後の本物が届くまで囲まない");
        mode.Tick(17.45);
        Assert.AreEqual(0, mode.RedCircles);
        mode.Tick(17.55);
        Assert.AreEqual(1, mode.RedCircles, "最後の本物(17.5 秒)のあと、end まで");
        mode.Tick(25);
        Assert.IsFalse(mode.ImageShown("rq.png"), "試験用紙の外では出さない");
        Assert.AreEqual(0, mode.Squares);
    }

    [Test]
    public void CuttingARealNoteFlashesItsOptionGoldForAMoment()
    {
        var note = Spawn(16.0);
        Assert.IsFalse(note.IsDecoy);
        Assert.AreEqual(QuizRules.PillarHitScale, note.HitScale);
        mode.Tick(16.0);
        Rgba(Color.white, mode.ImageColor("ro2.png"));
        Cut(note);
        Assert.AreEqual(QuizMode.SlotCut, mode.SlotState(R2, 0));
        mode.Tick(16.02);
        Rgba(UISkinPalette.NoteGold, mode.ImageColor("ro2.png"), "本物を切ると、その柱の札が金に光る");
        Rgba(Color.white, mode.ImageColor("ro1.png"));
        mode.Tick(16.2);
        Rgba(Color.white, mode.ImageColor("ro2.png"), "0.15 秒で戻る");
    }

    [Test]
    public void DecoysAreLiftedAboveThePaperLikeRealNotesAndClearedAtTheEnd()
    {
        spawner.Tick(11.5);
        Assert.AreEqual(3, spawner.LiveDecoys.Count, "練習の問題の飾り(12.0・12.15・12.3 秒)");
        var real = spawner.LiveNotes.First(n => Math.Abs(n.HitTime - 12.0) < 1e-6);
        foreach (var decoy in spawner.LiveDecoys)
        {
            Assert.IsTrue(decoy.IsDecoy);
            Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, decoy.BodyRenderer.sortingOrder, "飾りも本物と同じく試験用紙の上");
            Assert.AreEqual(.7f, decoy.Veil, 1e-6, "練習の飾りは薄い");
        }
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, real.BodyRenderer.sortingOrder);
        var frame = real.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>(true);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, frame.sortingOrder, "着地の枠も");
        var decoyFrame = spawner.LiveDecoys[0].TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>(true);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, decoyFrame.sortingOrder, "飾りの着地の枠も");
        mode.EndPlay();
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "終わりに飾りを消す");
        Assert.AreEqual(0, real.BodyRenderer.sortingOrder, "本物は元の並び順へ");
        Assert.AreEqual(0, frame.sortingOrder);
        spawner.Tick(16.0);
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "残りの飾りも出さない");
    }

    [Test]
    public void SortLabelsBrightenWhenTheirGoldLeavesAndSlideIntoTheAnswerBox()
    {
        mode.Tick(30.6);
        Assert.AreEqual(S1, mode.ActiveSortItem(30.6));
        Assert.IsTrue(mode.ImageShown("sq.png"), "見出し");
        Assert.AreEqual(QuizRules.SortHeaderY, mode.ImagePosition("sq.png").y, 1e-4);
        Assert.AreEqual(QuizRules.SortHeaderHeight, mode.ImageSize("sq.png").y, 1e-4);
        Assert.IsTrue(mode.ImageShown("sbl.png"));
        Assert.IsTrue(mode.ImageShown("sbr.png"));
        Assert.AreEqual(QuizRules.BoxWordCenter(-1f).x, mode.ImagePosition("sbl.png").x, 1e-4, "左の箱の語");
        Assert.AreEqual(QuizRules.BoxWordCenter(1f).x, mode.ImagePosition("sbr.png").x, 1e-4, "右の箱の語");
        Assert.AreEqual(QuizRules.BoxWordHeight, mode.ImageSize("sbl.png").y, 1e-4, "箱の語は高さ 0.30");
        Assert.LessOrEqual(mode.ImageSize("sbr.png").x, QuizRules.BoxWordMaxWidth + 1e-4f, "幅 1.35 を超えたら縮める");
        for (int k = 0; k < 4; k++)
        {
            Assert.AreEqual(QuizRules.SortLabelX[k], mode.ImagePosition("so" + k + ".png").x, 1e-4, "札 " + k);
            Assert.AreEqual(QuizRules.SortLabelY, mode.ImagePosition("so" + k + ".png").y, 1e-4);
        }
        Assert.AreEqual(QuizRules.SortLabelHeight, mode.ImageSize("so0.png").y, 1e-4);
        Assert.AreEqual(1f, mode.ImageColor("so0.png").a, 1e-4, "1つ目の金(31.5 秒に届く)は 30.5 秒に出たので明るい");
        Assert.AreEqual(QuizRules.SortDimAlpha, mode.ImageColor("so1.png").a, 1e-4, "2つ目はまだ出ていない");
        Assert.AreEqual(1, mode.Squares, "真ん中の的の薄い四角");

        // 1つ目(練習なので本当の矢印): 左へ切ると、届いたあと左の箱へ金で積む。
        var first = Spawn(31.5);
        Assert.IsTrue(first.HasLaunch, "札から発射");
        Assert.IsFalse(first.HidesSideArrow, "練習は本当の矢印");
        Assert.IsTrue(first.CutAtSongTime(first.transform.position, Vector3.left * 8f, CutDirection.None, SaberHand.Any, 31.5));
        Assert.AreEqual(QuizMode.SlotCut, mode.SlotState(S1, 0));
        mode.Tick(31.7);
        Assert.AreEqual(QuizRules.SortLabelX[0], mode.ImagePosition("so0.png").x, 1e-4, "遅めの判定の窓が閉じるまでは札を動かさない");
        mode.Tick(31.79);
        Assert.Less(mode.ImagePosition("so0.png").x, QuizRules.SortLabelX[0], "届いて 0.24 秒後から箱へ滑っていく");
        mode.Tick(31.9);
        Vector2 slot = QuizRules.BoxSlot(-1f, 0);
        Assert.AreEqual(slot.x, mode.ImagePosition("so0.png").x, 1e-3, "0.10 秒で左の箱へ");
        Assert.AreEqual(slot.y, mode.ImagePosition("so0.png").y, 1e-3);
        Assert.AreEqual(QuizRules.BoxLabelHeight, mode.ImageSize("so0.png").y, 1e-3, "箱の中では小さく");
        Rgba(UISkinPalette.NoteGold, mode.ImageColor("so0.png"), "切れた札は金");

        // 2つ目(右)は切らずに逃す → 灰色で右の箱の1つ目へ。
        spawner.Tick(32.2);
        Assert.AreEqual(QuizMode.SlotMissed, mode.SlotState(S1, 1));
        mode.Tick(32.2);
        var gray = mode.ImageColor("so1.png");
        Assert.AreEqual(QuizRules.MissGray.r, gray.r, 1e-4, "逃した札は灰色");
        Assert.AreEqual(QuizRules.MissGray.g, gray.g, 1e-4);
        // 2つ目は 31.875 秒に届く。箱へは 0.24 秒後から 0.10 秒で滑るので、32.215 秒より後に右の箱に着く。
        mode.Tick(32.3);
        Vector2 right = QuizRules.BoxSlot(1f, 0);
        Assert.AreEqual(right.x, mode.ImagePosition("so1.png").x, 1e-3, "右の箱の1つ目");
        Assert.AreEqual(right.y, mode.ImagePosition("so1.png").y, 1e-3);
    }

    [Test]
    public void SlowApproachShowsTheFirstSortBoardEarlyTogetherWithThePaper()
    {
        spawner.approachTime = 2f;
        mode.Tick(28);
        Assert.AreEqual(29.3, mode.Papers[1].Start, 1e-6, "最初の金(31.5 秒に届き、2.0 秒前に出る)の 0.2 秒前から紙と板");
        mode.Tick(29.5);
        Assert.AreEqual(S1, mode.ActiveSortItem(29.5));
        Assert.Greater(mode.PaperVisibility, .5f);
        Assert.IsTrue(mode.ImageShown("sq.png"), "問題(30.0 秒)より前に見出しと札");
        Assert.IsTrue(mode.ImageShown("so0.png"));
        Assert.AreEqual(S1, mode.ActiveSortItem(32.25));
        Assert.AreEqual(S2, mode.ActiveSortItem(32.35), "2問目の板は 32.3 秒から(前の問題の板はそこで消える)");
        mode.Tick(32.35);
        Assert.AreEqual(1, mode.ImageCount("sq.png"), "見出しは1枚");
        spawner.approachTime = 1f;
        mode.Tick(33.5);
        Assert.AreEqual(30.0, mode.Papers[1].Start, 1e-6, "接近 1.0 秒は紙の始まりのまま");
        Assert.AreEqual(S1, mode.ActiveSortItem(32.9), "接近 1.0 秒は問題と同時に板を出す");
        Assert.AreEqual(S2, mode.ActiveSortItem(33.0));
    }

    [Test]
    public void CountShowsARedQuestionMarkUntilTheLastGoldThenCirclesTheAnswer()
    {
        mode.Tick(50.5);
        Assert.IsTrue(mode.ImageShown("cq.png"));
        Assert.AreEqual(QuizRules.CountQuestionY, mode.ImagePosition("cq.png").y, 1e-4);
        Assert.AreEqual(QuizRules.CountQuestionHeight, mode.ImageSize("cq.png").y, 1e-4);
        Assert.IsTrue(mode.ImageShown("qm.png"), "最初は「?」");
        Rgba(new Color(.92f, .18f, .16f, 1f), mode.ImageColor("qm.png"), "白い「?」を赤ペンの色で塗る");
        Vector3 question = mode.ImagePosition("cq.png"), size = mode.ImageSize("cq.png");
        Vector3 mark = mode.ImagePosition("qm.png");
        Assert.AreEqual(question.x + (.8f - .5f) * size.x, mark.x, 1e-4, "「?」の所(slotX は左から)");
        Assert.AreEqual(question.y, mark.y, 1e-4, "slotY 0.5 は真ん中");
        Assert.AreEqual(QuizRules.CountQuestionHeight * 150f / 208f, mode.ImageSize("qm.png").y, 1e-3, "問題と同じ字の大きさ(画素の比)");
        Assert.AreEqual(0, mode.CountShown(C1, 50.5));
        mode.Tick(52.01);
        Assert.AreEqual(1, mode.CountShown(C1, 52.01), "届いた金は数える(切らなくても)");
        Assert.IsTrue(mode.ImageShown("qm.png"), "途中の数は等号の右に出さない(「= 1」のような間違った式にしない)");
        Assert.IsFalse(mode.ImageShown("n1.png"));
        Assert.AreEqual(0, mode.RedCircles);
        mode.Tick(52.8);
        Assert.IsFalse(mode.ImageShown("qm.png"));
        Assert.IsTrue(mode.ImageShown("n2.png"), "最後の金のあと答え");
        Rgba(Color.white, mode.ImageColor("n2.png"), "数は白");
        Assert.AreEqual(mark.x, mode.ImagePosition("n2.png").x, 1e-4, "「?」と同じ所");
        Assert.IsFalse(mode.ImageShown("n1.png"));
        Assert.AreEqual(2, mode.CountShown(C1, 52.8));
        Assert.AreEqual(1, mode.RedCircles, "最後の金のあと、答えの数を赤い丸で囲む");
        mode.Tick(53.2);
        Assert.IsFalse(mode.ImageShown("cq.png"), "次の部分では出さない");
        Assert.AreEqual(0, mode.RedCircles);
    }

    [Test]
    public void PiWritesADigitPerCutGrayForMissesAndScrollsFullRows()
    {
        var first = Spawn(55.0);
        Cut(first);
        mode.Tick(55.05);
        Assert.IsTrue(mode.ImageShown("pq.png"), "「π =」");
        Assert.Less(mode.ImagePosition("pq.png").x + mode.ImageSize("pq.png").x * .5f,
            QuizRules.CellX(0, 3) - QuizRules.PiPitch(3) * .5f, "最初のマスに重ねない");
        Assert.IsTrue(mode.ImageShown("n3.png"), "切ったマスに桁(最初は 3)");
        Assert.AreEqual(QuizRules.CellX(0, 3), mode.ImagePosition("n3.png").x, 1e-4);
        Assert.AreEqual(QuizRules.PiLineY, mode.ImagePosition("n3.png").y, 1e-4, "書く行 y −0.15");
        Assert.AreEqual(QuizRules.PiDigitHeight, mode.ImageSize("n3.png").y, 1e-4);
        Rgba(Color.white, mode.ImageColor("n3.png"));
        Assert.IsTrue(mode.ImageShown("pt.png"), "最初のマスは「3.」");
        Assert.AreEqual(QuizRules.CellX(0, 3) + QuizRules.PiPitch(3) * .5f, mode.ImagePosition("pt.png").x, 1e-4);
        Assert.AreEqual(QuizRules.PiLineY - QuizRules.PiDigitHeight * .5f,
            mode.ImagePosition("pt.png").y - mode.ImageSize("pt.png").y * .5f, 1e-4, "点は字の下の端にそろえる");

        // 2マス目は逃す → 灰色で書く。
        spawner.Tick(55.9);
        Assert.AreEqual(QuizMode.SlotMissed, mode.SlotState(P, 1));
        mode.Tick(55.9);
        Assert.IsTrue(mode.ImageShown("n1.png"));
        Assert.AreEqual(QuizRules.PiMissAlpha, mode.ImageColor("n1.png").a, 1e-4, "逃したら濃さ 0.35");

        // 到着予定時刻を過ぎても、最後のマスが空白なら行送りを始めない。
        var rowEnd = Spawn(56.0);
        mode.Tick(56.0);
        mode.Tick(56.05);
        Assert.AreEqual(QuizMode.SlotWaiting, mode.SlotState(P, 2));
        Assert.AreEqual(QuizRules.PiLineY, mode.RowY(0, 56.05), 1e-6);

        // 3マス目を描いたコマから、0.3 秒で 0.9 上へ。
        Cut(rowEnd);
        mode.Tick(56.05);
        mode.Tick(56.35);
        Assert.AreEqual(QuizRules.PiLineY + QuizRules.PiRowStep, mode.ImagePosition("n3.png").y, 1e-4, "行送り");
        Assert.AreEqual(QuizRules.PiLineY + QuizRules.PiRowStep, mode.RowY(0, 56.35), 1e-4);
        Assert.AreEqual(QuizRules.PiLineY, mode.RowY(1, 56.35), 1e-6, "次の行はまた書く行で");

        // 2行目: 同時の2つは隣どうしのマス。
        Cut(Spawn(56.5, QuizRules.CellX(0, 3)));
        Cut(spawner.LiveNotes.First(n => Math.Abs(n.HitTime - 56.5) < 1e-6 && !n.IsCut));
        mode.Tick(56.6);
        Assert.AreEqual(QuizMode.SlotCut, mode.SlotState(P, 3));
        Assert.AreEqual(QuizMode.SlotCut, mode.SlotState(P, 4));
        Assert.AreEqual(2, mode.ImageCount("n1.png"), "1 は2マスに(灰色の 1 と白い 1)");
        Assert.IsTrue(mode.ImageShown("n5.png"));
        Assert.AreEqual(QuizRules.CellX(1, 3), mode.ImagePosition("n5.png").x, 1e-4);
        Assert.AreEqual(QuizRules.PiLineY, mode.ImagePosition("n5.png").y, 1e-4);
        Assert.IsFalse(mode.ImageShown("el.png"));
        Assert.IsFalse(mode.ImageShown("fl.png"));

        // 最後のノーツのあと、最後の桁の右に「…」、「π」に赤い花丸。
        Cut(Spawn(57.0));
        mode.Tick(57.3);
        Assert.IsTrue(mode.ImageShown("n9.png"));
        Assert.IsTrue(mode.ImageShown("el.png"), "最後のノーツのあと「…」");
        Assert.Greater(mode.ImagePosition("el.png").x - mode.ImageSize("el.png").x * .5f,
            QuizRules.CellX(2, 3) + QuizRules.PiPitch(3) * .25f, "最後の桁の右");
        Assert.AreEqual(mode.ImagePosition("n9.png").y, mode.ImagePosition("el.png").y, 1e-4, "同じ行");
        Assert.IsTrue(mode.ImageShown("fl.png"), "「π」の赤い花丸");
        Assert.AreEqual(QuizMode.MarkSortingOrder, mode.ImageRenderer("fl.png").sortingOrder, "花丸は字より手前");
        Vector3 label = mode.ImagePosition("pq.png"), labelSize = mode.ImageSize("pq.png");
        Assert.AreEqual(label.x + (.2f - .5f) * labelSize.x, mode.ImagePosition("fl.png").x, 1e-4, "「π」の中心(slotX)");
        Assert.AreEqual(label.y + (.5f - .6f) * labelSize.y, mode.ImagePosition("fl.png").y, 1e-4, "「π」の中心(slotY は上から)");
    }

    [Test]
    public void EdgeFlashesUseTheBudgetStayOutsideTheGateAndRespectF3()
    {
        Assume.That(PlayExcitement.Current, Is.Null, "EditMode では予算の持ち主がいない(いれば予算に従う)");
        mode.Tick(54.9);
        mode.Tick(55.05);
        Assert.AreEqual(1, mode.ActiveEdgeFlashes, "盛り上がりの拍で紙の縁が光る");
        var vertices = mode.PaperMesh.vertices;
        var colors = mode.PaperMesh.colors;
        Assert.Greater(vertices.Length, 4, "黒い紙のあとに縁の光");
        var paper = mode.PaperRenderer.transform;
        Rect gate = GateRect();
        for (int i = 4; i + 3 < vertices.Length; i += 4)
        {
            Vector3 center = (vertices[i] + vertices[i + 1] + vertices[i + 2] + vertices[i + 3]) * .25f;
            Vector3 viewport = view.WorldToViewportPoint(paper.TransformPoint(center));
            Assert.IsFalse(gate.Contains(new Vector2(viewport.x, viewport.y)), "ゲートの内側は光らせない");
            Assert.AreEqual(1f, colors[i].r, 1e-4, "白い光");
            Assert.Less(colors[i].a, 1f);
        }
        mode.Tick(55.2);
        Assert.AreEqual(1, mode.ActiveEdgeFlashes);
        DisplaySettings.SetReducedEffectsForTest(true);
        mode.Tick(55.3);
        Assert.AreEqual(0, mode.ActiveEdgeFlashes, "F3 の弱める設定では出さない");
        DisplaySettings.SetReducedEffectsForTest(false);
        mode.Tick(55.6);
        Assert.AreEqual(0, mode.ActiveEdgeFlashes, "0.3 秒で消える");
    }

    // 判定面のゲートの枠(x ±1.9、y −0.6〜0.3)の画面での矩形。
    Rect GateRect()
    {
        float left = 1f, right = 0f, bottom = 1f, top = 0f;
        foreach (var corner in new[] { new Vector3(-1.9f, -.6f, 0f), new Vector3(1.9f, -.6f, 0f), new Vector3(-1.9f, .3f, 0f), new Vector3(1.9f, .3f, 0f) })
        {
            Vector3 p = view.WorldToViewportPoint(corner + Vector3.forward * spawner.judgeZ);
            left = Mathf.Min(left, p.x); right = Mathf.Max(right, p.x);
            bottom = Mathf.Min(bottom, p.y); top = Mathf.Max(top, p.y);
        }
        return Rect.MinMaxRect(left, bottom, right, top);
    }
}

// 勉強の Q の譜面(chart_q.json、下請け G が gen_quiz.py で作る)を仕様(spec_v3)と 9a の決めた点で確かめる。
// 9a の決めた点: 飾りの柱も 4〜6 打で表拍を3つ以上(本物と打つ数・形で見分けがつかないように)、π のマスの間隔は行の長さで決める。
public class BenkyouQuizChartTests
{
    const string Song = "BenkyouRemaster";
    const string Pi40 = "3141592653589793238462643383279502884197";
    // 正しい柱は左から 2→4→1→3(9a、10/28。巡回 2→3→4→1 をやめた)。
    static readonly int[] RhythmAnswers = { 1, 3, 0, 2 };
    static readonly string[][] SortSides =
    {
        new[] { "left", "right", "right", "left" }, new[] { "right", "left", "right", "left" },
        new[] { "left", "left", "right", "right" }, new[] { "right", "left", "left", "right" }
    };
    static readonly float[] CountShows = { 97674, 100674, 103674, 108174 };
    static readonly int[] CountAnswers = { 2, 6, 8, 12 };

    static string Folder => Path.Combine(Application.streamingAssetsPath, "Songs", Song);

    static ChartData Load()
    {
        string path = Path.Combine(Folder, "chart_q.json");
        Assert.IsTrue(File.Exists(path), "chart_q.json は必須(gen_quiz.py --write で作る)");
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    static bool Near(float a, double b, double tolerance = .5) => Math.Abs(a - b) <= tolerance;

    static List<QuizItemData> Items(ChartData chart, string kind) => chart.quiz.items.Where(i => i.kind == kind).ToList();

    // その問題のノーツ(showMs〜endMs、時刻順・同じ時刻は x の順)。
    static List<NoteData> NotesOf(ChartData chart, QuizItemData item)
        => chart.notes.Where(n => n.time >= item.showMs - .5f && n.time < item.endMs - .5f).OrderBy(n => n.time).ThenBy(n => n.x).ToList();

    [Test]
    public void QReplacesCInTheSecretTableWithTheChalkColors()
    {
        var secret = RushESecretState.BenkyouQuiz;
        Assert.AreSame(secret, RushESecretState.For(Song));
        Assert.AreSame(secret, RushESecretState.ForDifficulty(" q "));
        Assert.IsNull(RushESecretState.ForDifficulty("C"), "C は無くなった");
        Assert.AreSame(secret, RushESecretState.Charts.Last());
        Assert.AreSame(RushESecretState.YurikagoOvertake, RushESecretState.Charts[RushESecretState.Charts.Length - 2]);
        Assert.AreEqual("Q", secret.Difficulty);
        Assert.AreEqual(RushESecretState.Chalk, secret.Accent);
        Assert.AreEqual(ChameleonRules.Chalk, secret.Accent, "色は C のチョークのまま");
        Assert.AreEqual(secret.Accent, GameHUDSkin.DifficultyColor("Q"));
        Assert.AreEqual(secret.Accent, GameStartCountdown.AccentForDifficulty("q"));
        var state = new RushESecretState();
        state.Select(Song);
        for (int i = 0; i < RushESecretState.RequiredHits; i++) Assert.IsTrue(state.TryHit(i * .2));
        Assert.IsTrue(state.Reveal());
        Assert.IsTrue(state.Finish());
        Assert.AreSame(secret, state.Current);
        Assert.IsTrue(ChartLoader.LoadFromStreamingAssets(Song, "Q").quiz.HasContent, "Q は chart_q.json を読む");
    }

    [Test]
    public void PapersAndPartsFollowTheSpecTimeline()
    {
        var chart = Load();
        var quiz = chart.quiz;
        Assert.IsTrue(quiz.HasContent);
        var papers = QuizRules.BuildPapers(quiz, 0);
        Assert.AreEqual(3, papers.Count, "A・B と、つながった C+D");
        Assert.AreEqual(29.8, papers[0].Start, .002); Assert.AreEqual(49.4, papers[0].End, .002);
        Assert.AreEqual(79.674, papers[1].Start, .002); Assert.AreEqual(91.674, papers[1].End, .002);
        Assert.AreEqual(97.674, papers[2].Start, .002);
        Assert.GreaterOrEqual(papers[2].End, 137.0, "D は曲の終わりまで");

        var kinds = quiz.items.Select(i => i.kind).ToArray();
        CollectionAssert.AreEqual(Enumerable.Repeat(QuizRules.Rhythm, 4).Concat(Enumerable.Repeat(QuizRules.Sort, 4))
            .Concat(Enumerable.Repeat(QuizRules.Count, 4)).Concat(new[] { QuizRules.Pi }), kinds);
        foreach (var item in quiz.items)
        {
            Assert.LessOrEqual(item.showMs, item.playMs, item.kind);
            Assert.Less(item.playMs, item.endMs, item.kind);
            Assert.IsTrue(QuizRules.InPaper(papers, item.showMs / 1000.0 + .3), item.kind + " は試験用紙の中");
            Assert.IsFalse(string.IsNullOrEmpty(item.question), item.kind + " の問題の画像");
        }

        var rhythm = Items(chart, QuizRules.Rhythm);
        for (int i = 0; i < 4; i++)
        {
            var item = rhythm[i];
            Assert.IsTrue(Near(item.showMs, 30024 + 4800 * i), "show " + i);
            Assert.IsTrue(Near(item.playMs, item.showMs + 2400), "読む1小節 " + i);
            Assert.IsTrue(Near(item.endMs, item.playMs + 2400), "叩く1小節 " + i);
            Assert.AreEqual(RhythmAnswers[i], item.answer, "正しい柱 " + i);
            Assert.AreEqual(i == 0, item.hint, "練習は1問目だけ");
            Assert.AreEqual(4, item.options.Count, "札4つ " + i);
        }
        var sorts = Items(chart, QuizRules.Sort);
        for (int k = 0; k < 4; k++)
        {
            var item = sorts[k];
            Assert.IsTrue(Near(item.showMs, 79674 + 3000 * k), "show " + k);
            Assert.IsTrue(Near(item.playMs, item.showMs + 1500), "読む時間は1小節 " + k);
            Assert.IsTrue(Near(item.endMs, item.showMs + 3000), "end " + k);
            Assert.AreEqual(4, item.options.Count, "札4つ " + k);
            CollectionAssert.AreEqual(SortSides[k], item.sides, "答えの側 " + k);
            Assert.AreEqual(2, item.boxes.Count, "左右の箱の語 " + k);
        }
        var counts = Items(chart, QuizRules.Count);
        for (int q = 0; q < 4; q++)
        {
            var item = counts[q];
            Assert.IsTrue(Near(item.showMs, CountShows[q]), "show " + q);
            Assert.IsTrue(Near(item.playMs, item.showMs + 1500), "最初の1小節は読む時間 " + q);
            Assert.IsTrue(Near(item.endMs, q < 3 ? CountShows[q + 1] : 112674), "end は次の show " + q);
            Assert.AreEqual(CountAnswers[q], item.count, "答え " + q);
            Assert.AreEqual(13, item.numbers.Count, "0〜12 の数の画像 " + q);
            Assert.That(item.slotX, Is.InRange(.01f, .99f), "「?」の所 " + q);
            Assert.That(item.slotY, Is.InRange(.01f, .99f));
            Assert.IsFalse(string.IsNullOrEmpty(item.markImage), "最初の「?」の画像 " + q);
        }
        var pi = Items(chart, QuizRules.Pi).Single();
        Assert.IsTrue(Near(pi.showMs, 112674), "D は 112.674 秒から");
        Assert.GreaterOrEqual(pi.endMs, 137000f, "曲の終わりまで");
        Assert.AreEqual(10, pi.numbers.Count, "0〜9 の桁の画像");
        Assert.AreEqual(Pi40, pi.digits, "円周率の 40 桁");
        Assert.AreEqual(40, pi.rows.Sum(), "合計 40 マス");
        foreach (int length in pi.rows) Assert.That(length, Is.InRange(9, 11), "1行 9〜11 マス");
        Assert.IsFalse(string.IsNullOrEmpty(pi.pointImage));
        Assert.IsFalse(string.IsNullOrEmpty(pi.ellipsisImage));
        Assert.IsFalse(string.IsNullOrEmpty(pi.flowerImage));
        Assert.That(pi.slotX, Is.InRange(.01f, .99f), "「π」の中心");
        Assert.That(pi.slotY, Is.InRange(.01f, .99f));

        // 使う画像がそろっている。
        foreach (var item in quiz.items)
            foreach (var file in new[] { item.question, item.markImage, item.pointImage, item.ellipsisImage, item.flowerImage }
                         .Concat(item.options).Concat(item.boxes).Concat(item.numbers))
                if (!string.IsNullOrEmpty(file))
                    Assert.IsTrue(File.Exists(Path.Combine(Folder, QuizRules.ImageFolder, file)), "画像 " + file);
    }

    [Test]
    public void RhythmRealGoldsComeOnlyOnTheAnswerPillarAndDecoysOnTheOthers()
    {
        var chart = Load();
        var rhythm = Items(chart, QuizRules.Rhythm);
        var decoys = chart.quiz.decoys;
        Assert.Greater(decoys.Count, 0, "飾りの金");
        for (int i = 0; i < rhythm.Count; i++)
        {
            var item = rhythm[i];
            var real = NotesOf(chart, item);
            int[] Cells(IEnumerable<float> times) => times.Select(t => (int)Math.Round((t - item.playMs) / 150.0)).OrderBy(c => c).ToArray();
            foreach (var note in real)
            {
                Assert.AreEqual("gold", note.color, "流れてくるのは金 " + i);
                Assert.AreEqual(QuizRules.PillarX[item.answer], note.x, 1e-4, "本物は正しい答えの柱だけ " + i);
                Assert.AreEqual(QuizRules.PillarY, note.y, 1e-4);
                Assert.AreEqual(QuizRules.PillarHitScale, note.hitScale, 1e-4, "隣の柱では切れない当たり");
                Assert.AreEqual(1, note.count);
                Assert.AreEqual("none", note.direction);
                Assert.IsFalse(note.launch);
                Assert.IsFalse(note.hidden, "第3版は見えないノーツを使わない");
                double cell = (note.time - item.playMs) / 150.0;
                Assert.AreEqual(Math.Round(cell), cell, .01, "16分の格子 " + i);
                Assert.That(cell, Is.InRange(0, 15), "叩く1小節の中 " + i);
            }
            var hits = Cells(real.Select(n => n.time));
            Assert.That(hits.Length, Is.InRange(4, 6), "正しい柱は 4〜6 打 " + i);
            Assert.AreEqual(0, hits[0], "1拍目(小節の頭)を必ず含む " + i);
            Assert.GreaterOrEqual(hits.Count(h => h % 4 == 0) * 2, hits.Length, "表拍を多めに " + i);
            for (int k = 2; k < hits.Length; k++) Assert.IsFalse(hits[k] - hits[k - 2] == 2, "16分の連続は2つまで " + i);
            Assert.IsTrue(Near(item.revealMs, real.Last().time), "赤ペンは最後の本物のあと " + i);

            var mine = decoys.Where(d => d.ms >= item.playMs - .5f && d.ms < item.endMs - .5f).ToList();
            var patterns = new List<HashSet<int>> { new HashSet<int>(hits) };
            for (int p = 0; p < 4; p++)
            {
                var pillar = mine.Where(d => Math.Abs(d.x - QuizRules.PillarX[p]) < 1e-4).ToList();
                if (p == item.answer)
                {
                    Assert.IsEmpty(pillar, "正しい柱に飾りは来ない " + i);
                    continue;
                }
                Assert.That(pillar.Count, Is.InRange(4, 6), $"飾りの柱も 4〜6 打(9a) {i}/{p}");
                var cells = Cells(pillar.Select(d => d.ms));
                Assert.GreaterOrEqual(cells.Count(c => c % 4 == 0), 3, $"飾りも表拍を3つ以上(9a) {i}/{p}");
                foreach (var d in pillar)
                {
                    Assert.AreEqual(QuizRules.PillarY, d.y, 1e-4);
                    Assert.AreEqual(i == 0 ? .7f : 0f, d.veil, 1e-4, "練習の飾りだけ veil 0.7");
                }
                patterns.Add(new HashSet<int>(cells));
            }
            Assert.AreEqual(4, patterns.Count, "4本の柱 " + i);
            Assert.AreEqual(mine.Count, mine.Count(d => QuizRules.PillarX.Any(x => Math.Abs(d.x - x) < 1e-4)), "飾りは柱の上だけ " + i);
            // 4本の柱は同じリズム(リズムの形で正解の柱を当てられないように。9a、10/28 Codex の点検)。
            for (int p = 1; p < patterns.Count; p++)
                Assert.IsTrue(patterns[p].SetEquals(patterns[0]), $"4本の柱は同じリズム {i}: 0/{p}");
            var sorted = patterns[0].OrderBy(c => c).ToList();
            for (int k = 1; k < sorted.Count; k++)
                Assert.GreaterOrEqual(sorted[k] - sorted[k - 1], 2, $"同じ柱へ 0.15 秒の連打は置かない {i}");
        }
        foreach (var d in decoys)
        {
            Assert.IsTrue(rhythm.Any(it => d.ms >= it.playMs - .5f && d.ms < it.endMs - .5f), "飾りは答えの柱の叩く1小節の中だけ " + d.ms);
            Assert.IsFalse(chart.notes.Any(n => Math.Abs(n.time - d.ms) < .5f && Math.Abs(n.x - d.x) < 1e-3 && Math.Abs(n.y - d.y) < 1e-3),
                "飾りは chart.notes に入れない " + d.ms);
        }
    }

    [Test]
    public void SortGoldsLaunchFromTheirLabelsWithTheAnswerSideHidden()
    {
        var chart = Load();
        var sorts = Items(chart, QuizRules.Sort);
        for (int k = 0; k < sorts.Count; k++)
        {
            var item = sorts[k];
            var notes = NotesOf(chart, item);
            Assert.AreEqual(4, notes.Count, "4分で4つ、全部本物 " + k);
            for (int j = 0; j < notes.Count; j++)
            {
                var note = notes[j];
                Assert.IsTrue(Near(note.time, item.playMs + 375 * j), "play + 0/0.375/0.75/1.125 " + k);
                Assert.IsTrue(note.launch, "札から発射 " + k);
                Assert.AreEqual(QuizRules.SortLabelX[j], note.fromX, 1e-3, $"{j} 番目の札から {k}");
                Assert.AreEqual(QuizRules.SortLabelY, note.fromY, .15, "札の高さ " + k);
                Assert.AreEqual(QuizRules.SortTarget.x, note.x, 1e-4, "真ん中の的へ");
                Assert.AreEqual(QuizRules.SortTarget.y, note.y, 1e-4);
                Assert.AreEqual("gold", note.color);
                Assert.AreEqual("direction", note.type);
                Assert.AreEqual(SortSides[k][j], note.direction, "答えの側へ切る " + k);
                Assert.AreEqual(k == 0 ? "" : QuizRules.HiddenSide, note.sideArrow, "1問目の練習だけ本当の矢印 " + k);
                Assert.AreEqual(1, note.count);
            }
        }
        Assert.IsFalse(chart.quiz.decoys.Any(d => sorts.Any(s => d.ms >= s.showMs - .5f && d.ms < s.endMs)), "斬り分けに飾りはない");
    }

    [Test]
    public void CountGoldsComeInTheShapeOfTheAnswer()
    {
        var chart = Load();
        var counts = Items(chart, QuizRules.Count);
        var shapes = new[]
        {
            new[] { (4f, -.9f, -.6f), (6f, .9f, -.6f) },
            new[] { (4f, -1.5f, .3f), (4f, -1.5f, -.6f), (6f, .6f, .3f), (6f, .6f, -.6f), (7f, 1.5f, .3f), (7f, 1.5f, -.6f) },
            Enumerable.Range(0, 8).Select(k => (4f + .5f * k, -1.75f + .5f * k, -.15f)).ToArray(),
            Enumerable.Range(0, 12).Select(k => (4f + k / 2, -1.75f + .7f * (k / 2), k % 2 == 0 ? .3f : -.6f)).ToArray()
        };
        for (int q = 0; q < counts.Count; q++)
        {
            var item = counts[q];
            var notes = NotesOf(chart, item);
            Assert.AreEqual(CountAnswers[q], notes.Count, "金の数が答え " + q);
            foreach (var (beat, x, y) in shapes[q])
                Assert.IsTrue(notes.Any(n => Near(n.time, item.showMs + 375 * beat) && Math.Abs(n.x - x) < 1e-3 && Math.Abs(n.y - y) < 1e-3),
                    $"答えに関係した形 {q}: 拍 {beat} ({x}, {y})");
            foreach (var note in notes)
            {
                Assert.AreEqual("gold", note.color, "全部ふつうの金ノーツ " + q);
                Assert.AreEqual("none", note.direction);
                Assert.AreEqual(1, note.count);
                Assert.IsFalse(note.launch);
            }
            Assert.IsTrue(Near(item.revealMs, notes.Max(n => n.time)), "赤い丸は最後の金のあと " + q);
        }
    }

    [Test]
    public void PiGoldsWriteTheDigitsAcrossTheRowsAtTheOldTimes()
    {
        var chart = Load();
        var pi = Items(chart, QuizRules.Pi).Single();
        var notes = NotesOf(chart, pi);
        Assert.AreEqual(40, notes.Count, "元の D のノーツ 40 個");
        for (int k = 0; k < notes.Count; k++)
        {
            var note = notes[k];
            QuizRules.CellOf(pi.rows, k, out int row, out int column);
            int length = QuizRules.RowLength(pi.rows, row);
            Assert.AreEqual(QuizRules.CellX(column, length), note.x, 1e-3, $"左のマスから右へ(行 {row}・列 {column})");
            Assert.AreEqual(QuizRules.PiLineY, note.y, 1e-4, "書く行 y −0.15");
            Assert.That(note.x, Is.InRange(-QuizRules.GateHalfWidth, QuizRules.GateHalfWidth), "マスはゲートの中");
            Assert.AreEqual("gold", note.color, "全部金");
            Assert.AreEqual("none", note.direction, "矢印なし");
            Assert.AreEqual(1, note.count, "ロングなし");
            Assert.IsFalse(note.launch);
        }
        for (int k = 1; k < notes.Count; k++)
        {
            if (Math.Abs(notes[k].time - notes[k - 1].time) > .5f) continue;
            QuizRules.CellOf(pi.rows, k - 1, out int rowA, out int columnA);
            QuizRules.CellOf(pi.rows, k, out int rowB, out int columnB);
            Assert.AreEqual(rowA, rowB, "同時の2つは行をまたがない " + notes[k].time);
            Assert.AreEqual(columnA + 1, columnB, "同時の2つは隣どうしのマス " + notes[k].time);
        }
        // 時刻は元の D のまま(70 BPM の Hard と 360 BPM の締め)。最初の1秒には置かない。
        var hard = ChartLoader.Parse(File.ReadAllText(Path.Combine(Folder, "chart_hard.json")));
        foreach (var note in notes)
            Assert.IsTrue(hard.notes.Any(h => Near(h.time, note.time, .05)), "Hard の時刻 " + note.time);
        Assert.GreaterOrEqual(notes[0].time, pi.showMs + 1000 - .5f, "大問の最初の1秒にノーツを置かない");
        // 盛り上がり4(119.53 秒)から最後のノーツまで紙の縁を光らせる。大きな光は1秒に3回まで(360 BPM は4拍ごと)。
        Assert.Greater(pi.flashesMs.Count, 0);
        Assert.GreaterOrEqual(pi.flashesMs[0], 119530f - 1f, "盛り上がり4から");
        Assert.LessOrEqual(pi.flashesMs[pi.flashesMs.Count - 1], notes[notes.Count - 1].time + 1f, "最後のノーツまで");
        for (int f = 1; f < pi.flashesMs.Count; f++)
            Assert.GreaterOrEqual(pi.flashesMs[f] - pi.flashesMs[f - 1], 334f, "大きな光は1秒に3回まで");
        Assert.IsTrue(Near(pi.revealMs, notes[notes.Count - 1].time), "「…」と花丸は最後のノーツのあと");
    }

    [Test]
    public void OrdinaryNotesKeepClearOfThePapersAndComeFromTheCMix()
    {
        var chart = Load();
        var papers = QuizRules.BuildPapers(chart.quiz, 0);
        const double beat = .375;
        foreach (var note in chart.notes)
        {
            double t = note.TimeSeconds;
            Assert.IsFalse(note.hidden, "第3版は見えないノーツを使わない " + note.time);
            if (QuizRules.InPaper(papers, t))
            {
                Assert.AreEqual("gold", note.color, "試験用紙の中で流れてくるのは金のノーツ " + note.time);
                continue;
            }
            foreach (var paper in papers)
                Assert.IsFalse((t > paper.Start - beat && t < paper.Start) || (t > paper.End && t < paper.End + beat),
                    "試験用紙の前後1拍に、ふつうのノーツを置かない " + note.time);
        }
        int ordinary = MixedSecretChartAssertions.CheckOrdinarySubsequence(Song, chart, 50424,
            n => !QuizRules.InPaper(papers, n.TimeSeconds));
        Assert.Greater(ordinary, 100, "試験用紙の外は C の中身(Normal → Hard)");
    }
}
