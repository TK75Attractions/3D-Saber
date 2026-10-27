using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 勉強の裏譜面 Q(2026-10-27、Milthm「Algebra」の4つの仕組み)。決まり(QuizRules)の純関数と、読み込み。
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
    public void RhythmBallsComeFromTheRightAndReachTheTargetOnTimeThenFade()
    {
        // 速さは rhythm 4.0・count 3.6・行列 2.2(仕様の 2.8・2.4 から、読む1小節のうちに画面の端から入るよう b1 の指示で変えた)。
        Assert.AreEqual(4f, QuizRules.RhythmSpeed);
        Assert.AreEqual(3.6f, QuizRules.CountSpeed);
        Assert.AreEqual(2.2f, QuizRules.GuideSpeed);
        Assert.AreEqual(-1.6f, QuizRules.BallX(10, 10), 1e-6, "届く時刻に的の中心");
        Assert.AreEqual(-1.6f + 4f, QuizRules.BallX(10, 9), 1e-5, "1 秒前は 4.0 右");
        Assert.AreEqual(-1.6f - .6f, QuizRules.BallX(10, 10.15), 1e-5, "届いたあとは的を過ぎて左へ");
        Assert.AreEqual(1f, QuizRules.PassFade(10, 10));
        Assert.AreEqual(.5f, QuizRules.PassFade(10, 10.15), 1e-5);
        Assert.AreEqual(0f, QuizRules.PassFade(10, 10.3), 1e-5, "0.3 秒で薄れる");
        double lead = QuizRules.LeadSeconds(QuizRules.RhythmTargetX, 7.3f, QuizRules.RhythmSpeed);
        Assert.AreEqual((7.3 + 1.6) / 4.0, lead, 1e-5);
        Assert.Less(QuizRules.LeadSeconds(QuizRules.RhythmTargetX, 7.6f + QuizRules.BallRadius, QuizRules.RhythmSpeed), 2.4,
            "読む1小節(2.4 秒)のうちに右の画面の端から入る");
        Assert.Less(QuizRules.LeadSeconds(QuizRules.CountTargetY, 4.62f + QuizRules.BallRadius, QuizRules.CountSpeed), 1.5,
            "数える玉は読む1小節(1.5 秒)のうちに上の画面の端から入る");
        Assert.IsFalse(QuizRules.BallShown(10, 10 - lead - .01, lead), "画面の右の端より外");
        Assert.IsTrue(QuizRules.BallShown(10, 10 - lead + .01, lead));
        Assert.IsFalse(QuizRules.BallShown(10, 10.3, lead), "薄れ切った");
        Assert.AreEqual(.25f, QuizRules.BallAlpha(true, false, 10, 9), "練習の問題の飾りは薄い");
        Assert.AreEqual(1f, QuizRules.BallAlpha(true, true, 10, 9));
        Assert.AreEqual(1f, QuizRules.BallAlpha(false, false, 10, 9), "練習でなければ本物と飾りは同じ見た目");
        Assert.AreEqual(-.35f + 3.6f, QuizRules.FallY(QuizRules.CountTargetY, QuizRules.CountSpeed, 10, 9), 1e-5, "数える玉は 3.6/秒");
        Assert.AreEqual(-.35f + 1.1f, QuizRules.FallY(QuizRules.LaneBottomY, QuizRules.GuideSpeed, 10, 9.5), 1e-5, "行列の印は 2.2/秒");
        Assert.AreEqual(-.35f, QuizRules.FallY(QuizRules.LaneBottomY, QuizRules.GuideSpeed, 10, 10), 1e-6, "届く時刻に小さな丸");
    }

    [Test]
    public void BallsAppearOnlyFromTheirQuestionAndFadeInQuickly()
    {
        var item = new QuizItemData { kind = QuizRules.Rhythm, showMs = 30000, playMs = 32400, endMs = 34800 };
        Assert.AreEqual(0f, QuizRules.AppearFade(item, .5, 30.49), "問題が出る(showMs)前は出さない");
        Assert.AreEqual(0f, QuizRules.AppearFade(item, .5, 30.5));
        Assert.AreEqual(.5f, QuizRules.AppearFade(item, .5, 30.5 + .075), 1e-4);
        Assert.AreEqual(1f, QuizRules.AppearFade(item, .5, 30.5 + QuizRules.AppearSeconds), 1e-4, "0.15 秒で現れる");
        Assert.AreEqual(1f, QuizRules.AppearFade(item, .5, 40));
        Assert.AreEqual(0f, QuizRules.AppearFade(null, 0, 40));
        Assert.AreEqual(0f, QuizRules.AppearFade(item, 0, double.NaN));
    }

    [Test]
    public void LanesFollowTheLineThroughTheirBottomAndTopPoints()
    {
        var bottom = new Vector2(-6f, -.35f);
        var top = new Vector2(-5.2f, 4.65f);
        Assert.AreEqual(-6f, QuizRules.LaneXAt(bottom, top, -.35f), 1e-5);
        Assert.AreEqual(-5.2f, QuizRules.LaneXAt(bottom, top, 4.65f), 1e-5);
        Assert.AreEqual(-5.6f, QuizRules.LaneXAt(bottom, top, 2.15f), 1e-5, "途中は2点を結ぶ線の上");
        Assert.Less(QuizRules.LaneXAt(bottom, top, -.62f), -6f, "下の字は同じ線の延長");
        Assert.AreEqual(-6f, QuizRules.LaneXAt(bottom, bottom, 3f), 1e-6, "上下が同じ点ならまっすぐ");
    }

    [Test]
    public void CountQuestionIsAboutTwoAndAHalfTimesBiggerAndStaysLeftOfTheLine()
    {
        Assert.AreEqual(QuizRules.QuestionHeight * 2.5f, QuizRules.CountQuestionHeight, 1e-5, "式は約 2.5 倍");
        Assert.AreEqual(.20f * QuizRules.BoardScale * 2.5f, QuizRules.LegendHeight, 1e-5, "凡例も約 2.5 倍");
        Assert.AreEqual(.045f * QuizRules.BoardScale * 2.5f, QuizRules.UnitMarkRadius, 1e-5, "金の丸も合わせて大きく");
        var narrow = QuizRules.CountQuestionSize(1.52f, -7.2f);
        Assert.AreEqual(QuizRules.CountQuestionHeight, narrow.y, 1e-5);
        Assert.AreEqual(QuizRules.CountQuestionHeight * 1.52f, narrow.x, 1e-4);
        var wide = QuizRules.CountQuestionSize(3.36f, -7.2f);
        Assert.Less(wide.y, QuizRules.CountQuestionHeight, "横に長い式は縮める");
        Assert.AreEqual(3.36f, wide.x / wide.y, 1e-4, "縦横比はそのまま");
        Assert.GreaterOrEqual(QuizRules.CountQuestionRightX - wide.x, -7.2f + QuizRules.CountQuestionLeftMargin - 1e-4f, "画面の左の端に収まる");
        Assert.Less(QuizRules.CountQuestionRightX + QuizRules.BallRadius, QuizRules.CountLineX, "式の右の端は縦の線と玉の左");
        // 凡例(左上)の下の端より、式の上の端が下(重ねない)。
        float legendBottom = 4.62f - QuizRules.LegendMargin - QuizRules.LegendHeight;
        Assert.Less(QuizRules.CountQuestionCenterY + QuizRules.CountQuestionHeight * .5f, legendBottom);
    }

    [Test]
    public void GreenShowsTheAnswerRowForTheHintOrOnlyAfterTheLastRealBall()
    {
        var hint = new QuizItemData { kind = QuizRules.Rhythm, showMs = 30024, playMs = 32424, endMs = 34824, hint = true, answer = 1 };
        hint.rows.AddRange(new[] { "a", "b", "c", "d" });
        Assert.IsFalse(QuizRules.Green(hint, 0, 32.4));
        Assert.IsTrue(QuizRules.Green(hint, 0, 32.43), "練習は playMs から緑");
        Assert.IsTrue(QuizRules.Green(hint, 0, 34.8));
        Assert.IsFalse(QuizRules.Green(hint, 0, 34.824), "endMs まで");

        var plain = new QuizItemData { kind = QuizRules.Rhythm, showMs = 34824, playMs = 37224, endMs = 39624, answer = 0 };
        plain.rows.AddRange(new[] { "a", "b", "c", "d" });
        plain.dots.Add(new QuizDotData { ms = 37224, real = true });
        plain.dots.Add(new QuizDotData { ms = 38724, real = true });
        plain.dots.Add(new QuizDotData { ms = 39474, real = false });
        Assert.IsTrue(QuizRules.GreenSpan(plain, 0, out var span));
        Assert.AreEqual(39.224, span.Start, 1e-6, "endMs までの最後の 0.4 秒(飾りの玉は数えない)");
        Assert.AreEqual(39.624, span.End, 1e-6);
        Assert.IsFalse(QuizRules.Green(plain, 0, 39.2));
        Assert.IsTrue(QuizRules.Green(plain, 0, 39.3));
        plain.dots.Add(new QuizDotData { ms = 39474, real = true });
        Assert.IsTrue(QuizRules.GreenSpan(plain, 0, out span));
        Assert.AreEqual(39.474, span.Start, 1e-6, "最後の本物の玉が届くまで答えを見せない");
        plain.answer = -1;
        Assert.IsFalse(QuizRules.GreenSpan(plain, 0, out _), "正しい行の無い問題は緑にしない");
    }

    [Test]
    public void RingsStayOutsideTheGateAndPopsGrowFromTheTarget()
    {
        Assert.AreEqual(2.2f, QuizRules.RingRadius(0), 1e-5);
        Assert.AreEqual(6f, QuizRules.RingRadius(.6), 1e-5);
        Assert.Greater(QuizRules.RingRadius(.3), 2.2f);
        Assert.Less(QuizRules.RingRadius(.3), 6f);
        Assert.AreEqual(.3f, QuizRules.RingOpacity(0), 1e-5, "濃さ 0.3");
        Assert.AreEqual(.15f, QuizRules.RingOpacity(.3), 1e-5);
        Assert.AreEqual(0f, QuizRules.RingOpacity(.6));
        Assert.IsFalse(QuizRules.RingShown(-.01));
        Assert.IsFalse(QuizRules.RingShown(.6), "0.6 秒で消える");
        // ゲート(x −1.9〜1.9、y −0.6〜0.3)の内側は光らせない(いちばん小さい輪の内側の縁でも、中心から角より遠い)。
        float corner = Mathf.Sqrt(1.9f * 1.9f + .45f * .45f);
        Assert.Greater(QuizRules.RingFromRadius - QuizRules.RingWidth * .5f, corner);
        Assert.AreEqual(new Vector2(0f, -.15f), QuizRules.GateCenter);
        Assert.IsTrue(QuizRules.Crossed(1, 2, 2));
        Assert.IsFalse(QuizRules.Crossed(2, 2, 2), "止まっている間は越えない");
        Assert.IsFalse(QuizRules.Crossed(3, 2, 2.5), "巻き戻しでは越えない");
        Assert.AreEqual(.24f, QuizRules.PopRadius(.24f, 0), 1e-6);
        Assert.AreEqual(.24f * QuizRules.PopGrowth, QuizRules.PopRadius(.24f, .3), 1e-5);
        Assert.AreEqual(QuizRules.PopAlpha, QuizRules.PopOpacity(0), 1e-6);
        Assert.AreEqual(0f, QuizRules.PopOpacity(.3));
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
    public void LoaderReadsQuizHiddenAndHitScaleAndDropsBrokenParts()
    {
        var chart = ChartLoader.Parse("{\"bpm\":100,\"notes\":["
            + "{\"time\":1000,\"x\":-1.6,\"y\":-0.15,\"hidden\":true},"
            + "{\"time\":2000,\"x\":0.5,\"y\":-0.15,\"hidden\":true,\"hitScale\":0.5},"
            + "{\"time\":3000,\"x\":0,\"y\":0}],"
            + "\"quiz\":{\"papers\":[{\"startMs\":5000,\"endMs\":9000},{\"startMs\":100,\"endMs\":900},{\"startMs\":7000,\"endMs\":7000}],"
            + "\"items\":["
            + "{\"kind\":\" Matrix \",\"showMs\":6000,\"playMs\":6750,\"endMs\":9000,\"question\":\"m.png\","
            + "\"labels\":[\" A \",\"\",\"C\",\"D\"],\"guides\":[{\"ms\":8000,\"lane\":2},{\"ms\":7000,\"lane\":0},{\"ms\":7500,\"lane\":9}]},"
            + "{\"kind\":\"rhythm\",\"showMs\":100,\"playMs\":400,\"endMs\":900,\"rows\":[\"r0.png\",\"r1.png\"],\"answer\":5,\"hint\":true,"
            + "\"dots\":[{\"ms\":700,\"real\":true},{\"ms\":400,\"real\":false,\"unit\":true}]},"
            + "{\"kind\":\"count\",\"showMs\":20,\"playMs\":10,\"endMs\":10}]}}");
        Assert.AreEqual(3, chart.notes.Count);
        Assert.IsTrue(chart.notes[0].hidden);
        Assert.AreEqual(0f, chart.notes[0].hitScale, "省略は 0(使うときに 1)");
        Assert.AreEqual(.5f, chart.notes[1].hitScale);
        Assert.IsFalse(chart.notes[2].hidden);
        var quiz = chart.quiz;
        Assert.IsTrue(quiz.HasContent);
        CollectionAssert.AreEqual(new[] { 100f, 5000f }, quiz.papers.Select(p => p.startMs), "長さの無い時間帯を捨てて時刻順");
        Assert.AreEqual(2, quiz.items.Count, "終わりが始まりより前の問題は捨てる");
        var rhythm = quiz.items[0];
        var matrix = quiz.items[1];
        Assert.AreEqual(QuizRules.Rhythm, rhythm.kind);
        Assert.AreEqual(QuizRules.Matrix, matrix.kind, "種類の名前をそろえる");
        Assert.AreEqual(-1, rhythm.answer, "行の無い答えは使わない");
        Assert.IsTrue(rhythm.hint);
        CollectionAssert.AreEqual(new[] { 400f, 700f }, rhythm.dots.Select(d => d.ms));
        Assert.IsTrue(rhythm.dots[0].unit);
        Assert.IsFalse(rhythm.dots[0].real);
        CollectionAssert.AreEqual(new[] { 7000f, 8000f }, matrix.guides.Select(g => g.ms), "レーンの外の印を捨てて時刻順");
        CollectionAssert.AreEqual(new[] { "A", "", "C", "D" }, matrix.labels);
        Assert.AreEqual("", matrix.answerImage);
        Assert.AreEqual("", matrix.legend);

        var plain = ChartLoader.Parse("{\"notes\":[{\"time\":0}]}");
        Assert.NotNull(plain.quiz);
        Assert.IsFalse(plain.quiz.HasContent, "ふつうの譜面は空のまま");
        Assert.IsFalse(plain.notes[0].hidden);
    }

    [Test]
    public void BallsAndGuidesAreFoundByTheHiddenNoteTime()
    {
        var item = new QuizItemData { kind = QuizRules.Rhythm };
        item.dots.Add(new QuizDotData { ms = 1000 });
        item.dots.Add(new QuizDotData { ms = 1150 });
        Assert.AreEqual(1, QuizRules.FindDot(item, .5, 1.65));
        Assert.AreEqual(-1, QuizRules.FindDot(item, .5, 1.66), "別の時刻の玉と取り違えない");
        var matrix = new QuizItemData { kind = QuizRules.Matrix };
        matrix.guides.Add(new QuizGuideData { ms = 2000, lane = 3 });
        Assert.AreEqual(0, QuizRules.FindGuide(matrix, 0, 2.0005));
        Assert.AreEqual(3, QuizRules.NearestMatrixTarget(1.4f));
        Assert.AreEqual(0, QuizRules.NearestMatrixTarget(-9f));
        var count = new QuizItemData { kind = QuizRules.Count };
        count.dots.Add(new QuizDotData { ms = 1, unit = true });
        count.dots.Add(new QuizDotData { ms = 2 });
        count.dots.Add(new QuizDotData { ms = 3, unit = true });
        Assert.AreEqual(2, QuizRules.UnitCount(count), "金の玉の数が答え");
    }

    [Test]
    public void BoardStacksTheRowsUnderTheQuestionAboveTheGate()
    {
        for (int r = 0; r < 3; r++)
            Assert.AreEqual(QuizRules.RowHeight + QuizRules.RowGap, QuizRules.RowCenterY(r, 4) - QuizRules.RowCenterY(r + 1, 4), 1e-5);
        Assert.AreEqual(QuizRules.BoardBottomY + QuizRules.RowHeight * .5f, QuizRules.RowCenterY(3, 4), 1e-5, "一番下の行の下が黒板の下");
        Assert.Greater(QuizRules.BoardBottomY, .3f, "ゲート(y 0.3 まで)の上");
        float questionBottom = QuizRules.QuestionCenterY(4) - QuizRules.QuestionHeight * .5f;
        float topRow = QuizRules.RowCenterY(0, 4) + QuizRules.RowHeight * .5f;
        Assert.AreEqual(QuizRules.QuestionGap, questionBottom - topRow, 1e-5);
        Assert.Less(QuizRules.QuestionCenterY(4) + QuizRules.QuestionHeight * .5f, QuizRules.DefaultTopEdgeY, "画面の上の端より下");
        // 黒板の画像だけを BoardScale 倍(仕様の 0.30・0.17・0.05)。玉・的は譜面の座標のまま。
        Assert.AreEqual(.30f * QuizRules.BoardScale, QuizRules.QuestionHeight, 1e-6);
        Assert.AreEqual(.17f * QuizRules.BoardScale, QuizRules.RowHeight, 1e-6);
        Assert.AreEqual(.05f * QuizRules.BoardScale, QuizRules.RowGap, 1e-6);
        Assert.AreEqual(.24f, QuizRules.RhythmTargetRadius);
        Assert.AreEqual(.16f, QuizRules.BallRadius);
        CollectionAssert.AreEqual(new[] { -1.8f, -.6f, .6f, 1.8f }, QuizRules.MatrixTargetX);
        for (int i = 1; i < QuizRules.MatrixTargetX.Length; i++)
            Assert.AreEqual(QuizRules.MatrixTargetSpacing, QuizRules.MatrixTargetX[i] - QuizRules.MatrixTargetX[i - 1], 1e-6, "的は 1.2 おき");
        Assert.AreEqual(.30f, QuizRules.MatrixTargetRadius);
        Assert.AreEqual(.45f, QuizRules.MatrixHitScale);
        var mark = QuizRules.UnitMark(2, -3f, 1f);
        Assert.AreEqual(-3f + QuizRules.UnitMarkRadius + 2 * QuizRules.UnitMarkSpacing, mark.x, 1e-5);
        Assert.Less(mark.y + QuizRules.UnitMarkRadius, 1f, "金の小さな丸は問題の下");
    }
}

// 見えないノーツ(NoteData.hidden)。合図を出さずに、判定・採点・音はふつうのノーツと同じ。
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
    // 試験用紙が薄れる間や終わった直後に、0.34 秒の光の筋が見えてしまうのを防ぐ。見えるノーツはいつもどおり。
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
        Assert.AreEqual(0, feedback.ActiveCount, "切った光を出さない(弾けは QuizMode が的に描く)");
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
public class BenkyouQuizHitScaleTests
{
    readonly List<GameObject> created = new List<GameObject>();

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
    public void HitRangeKeepsTheOldValueAtScaleOneAndMatrixTargetsDoNotReachTheNeighbour()
    {
        var (judge, _) = PointRig();
        var plain = Note(Vector3.zero, 1f);
        var small = Note(Vector3.zero, QuizRules.MatrixHitScale);
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY, judge.HitRangeFor(plain), "倍率 1 は今までと同じ値");
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY, judge.HitRangeFor(null));
        Assert.AreEqual(judge.bladeRadius + judge.noteHitRadiusXY * QuizRules.MatrixHitScale, judge.HitRangeFor(small), 1e-6);
        // 本編の値(刃 0.32・当たり 0.88、GamePlayManager の V2)。隣の的は 1.2 離れ、その丸(半径 0.3)の内側の縁は 0.9。
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        float neighbourEdge = QuizRules.MatrixTargetSpacing - QuizRules.MatrixTargetRadius;
        Assert.AreEqual(.9f, neighbourEdge, 1e-6);
        Assert.Less(judge.HitRangeFor(small), neighbourEdge, "0.45 倍の的は隣の丸の縁まで届かない");
        Assert.Greater(judge.HitRangeFor(small), QuizRules.MatrixTargetRadius, "正しい的の丸の中ならどこでも届く");
        Assert.Greater(judge.HitRangeFor(plain), neighbourEdge, "ふつうの大きさなら隣の丸の中まで届いてしまう");
    }

    // 細い側の振りで広げた当たり(SaberCutJudge.ThinRangeFor)。倍率 1 は今までと同じ値。
    // 小さくした的は広げる量にも倍率を掛け、ScaledThinRangeLimit(0.85)まで。行列の隣の丸の縁(0.9)には届かない。
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

        float neighbourEdge = QuizRules.MatrixTargetSpacing - QuizRules.MatrixTargetRadius;
        float small = .32f + .88f * QuizRules.MatrixHitScale;
        float thin = SaberCutJudge.ThinRangeFor(a, b, down, small, QuizRules.MatrixHitScale, width);
        Assert.AreEqual(1.7f * QuizRules.MatrixHitScale, thin, 1e-6);
        Assert.Less(thin, neighbourEdge, "行列の的は、細い側で振っても隣の丸の縁まで届かない");
        Assert.AreEqual(SaberCutJudge.ScaledThinRangeLimit, SaberCutJudge.ThinRangeFor(a, b, down, .5f, .8f, width), 1e-6,
            "小さくした的は 0.85 まで");
        Assert.Less(SaberCutJudge.ScaledThinRangeLimit, neighbourEdge);
    }

    // 行列の的(MatrixHitScale 倍)を、本編の当たり(刃 0.32・当たり 0.88)とカメラの縦の刃で縦に振る(曲時計あり = 細い側で広げる道も通る)。
    // 隣の丸の内側の縁(的の中心から 0.9)を振っても切れず、正しい的の丸の中(0.25)なら切れる。ふつうの大きさなら 0.9 でも切れてしまう。
    [TestCase(.9f, QuizRules.MatrixHitScale, false)]
    [TestCase(.25f, QuizRules.MatrixHitScale, true)]
    [TestCase(.9f, 1f, true)]
    public void VerticalCameraSwingAtTheNeighbourEdgeDoesNotCutTheMatrixTarget(float offset, float scale, bool cut)
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
    public void PointSwingAtTheNeighbourEdgeDoesNotCutTheMatrixTarget(float offset, bool cut)
    {
        var (judge, tracker) = PointRig();
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        var note = Note(Vector3.zero, QuizRules.MatrixHitScale);
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

// QuizMode を曲の時計の代わりに直接進め、試験用紙・黒板・弾け・数える丸・大問・見えるノーツの上げ下げを確かめる。
// 画像は一時フォルダに作った PNG。EditMode では OnDestroy が呼ばれないので、ReleaseResources で片付ける。
public class BenkyouQuizModeTests
{
    readonly List<GameObject> created = new List<GameObject>();
    string folder;
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
        WritePng("q1.png", 400, 100);
        WritePng("q2.png", 400, 100);
        WritePng("r0.png", 600, 100);
        WritePng("r1.png", 400, 100);
        WritePng("r2.png", 500, 100);
        WritePng("r3.png", 300, 100);
        WritePng("m.png", 300, 200);
        WritePng("c.png", 300, 100);
        WritePng("ca.png", 300, 100);
        WritePng("lg.png", 200, 100);
        WritePng("f.png", 420, 100);
        WritePng("fa.png", 420, 100);

        var cameraObject = new GameObject("QuizTestCamera") { tag = "MainCamera" };
        created.Add(cameraObject);
        view = cameraObject.AddComponent<Camera>();
        view.fieldOfView = 60f;
        view.aspect = 16f / 9f;
        view.transform.SetPositionAndRotation(new Vector3(0f, 2.35f, -7f), Quaternion.Euler(12f, 0f, 0f));

        var root = new GameObject("BenkyouQuizModeTests");
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
        score = root.AddComponent<ScoreManager>();
        score.Reset();
        score.Bind(spawner);
        hud = new GameObject("QuizTestHud");
        created.Add(hud);
        hud.AddComponent<GameHUDSkin>();

        chart = new ChartData { bpm = 100 };
        chart.quiz.papers.Add(new QuizPaperData { startMs = 10000, endMs = 20000 });
        chart.quiz.papers.Add(new QuizPaperData { startMs = 30000, endMs = 40000 });
        var first = Rhythm(10000, "q1.png", true, 1);
        first.dots.Add(new QuizDotData { ms = 12000, real = true });
        first.dots.Add(new QuizDotData { ms = 12150 });
        first.dots.Add(new QuizDotData { ms = 12300, real = true });
        var second = Rhythm(14000, "q2.png", false, 2);
        second.dots.Add(new QuizDotData { ms = 16000, real = true });
        second.dots.Add(new QuizDotData { ms = 17500, real = true });
        var matrix = new QuizItemData { kind = QuizRules.Matrix, showMs = 30000, playMs = 30750, endMs = 33000, question = "m.png" };
        matrix.labels.AddRange(new[] { "B", "A", "", "" });
        matrix.guides.Add(new QuizGuideData { ms = 31000, lane = 0 });
        var count = new QuizItemData { kind = QuizRules.Count, showMs = 33000, playMs = 34500, endMs = 36000,
            question = "c.png", answerImage = "ca.png", revealMs = 35900, legend = "lg.png" };
        count.dots.Add(new QuizDotData { ms = 34500, real = true, unit = true });
        count.dots.Add(new QuizDotData { ms = 35000, real = true });
        var formula = new QuizItemData { kind = QuizRules.Formula, showMs = 36000, playMs = 36000, endMs = 40000,
            question = "f.png", answerImage = "fa.png", revealMs = 39000 };
        formula.ringsMs.AddRange(new[] { 37000f, 38000f });
        chart.quiz.items.AddRange(new[] { first, second, matrix, count, formula });
        foreach (float ms in new[] { 12000f, 12300f, 16000f, 17500f })
            chart.notes.Add(new NoteData { time = ms, x = -1.6f, y = -.15f, hidden = true });
        chart.notes.Add(new NoteData { time = 31000, x = QuizRules.MatrixTargetX[1], y = QuizRules.MatrixTargetY, hidden = true,
            hitScale = QuizRules.MatrixHitScale });
        chart.notes.Add(new NoteData { time = 34500, x = 0f, y = -.35f, hidden = true, color = "gold" });
        chart.notes.Add(new NoteData { time = 35000, x = 0f, y = -.35f, hidden = true });
        chart.notes.Add(new NoteData { time = 38500, x = .5f, y = 0f, color = "red" });
        chart.notes.Sort((a, b) => a.time.CompareTo(b.time));
        chart.quiz = QuizRules.Normalize(chart.quiz);
        spawner.SetChart(chart);
        mode = QuizMode.Create(root.transform, chart, spawner, folder, view);
    }

    static QuizItemData Rhythm(float show, string question, bool hint, int answer)
    {
        var item = new QuizItemData { kind = QuizRules.Rhythm, showMs = show, playMs = show + 2000, endMs = show + 4000,
            question = question, hint = hint, answer = answer };
        item.rows.AddRange(new[] { "r0.png", "r1.png", "r2.png", "r3.png" });
        return item;
    }

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
        if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    CuttableNote Spawn(double hit)
    {
        spawner.Tick(hit - .5);
        var note = spawner.LiveNotes.FirstOrDefault(n => Math.Abs(n.HitTime - hit) < 1e-6);
        Assert.NotNull(note, "時刻 " + hit + " のノーツ");
        spawner.Tick(hit);
        return note;
    }

    static void Cut(CuttableNote note)
        => Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, note.HitTime));

    static void Rgba(Color expected, Color actual)
    {
        Assert.AreEqual(expected.r, actual.r, 1e-4);
        Assert.AreEqual(expected.g, actual.g, 1e-4);
        Assert.AreEqual(expected.b, actual.b, 1e-4);
        Assert.AreEqual(expected.a, actual.a, 1e-4);
    }

    [Test]
    public void PaperCoversTheScreenInBlackFadesTheHudAndRestoresAtTheEnd()
    {
        Assert.NotNull(mode);
        Assert.AreEqual(12, mode.LoadedImageCount, "曲フォルダの quiz/ の画像を読む");
        CollectionAssert.IsEmpty(mode.MissingImages);
        Assert.AreEqual(2, mode.Papers.Count);
        Assert.IsTrue(spawner.CutFeedbackAbovePaper, "見えるノーツを切った光は試験用紙の上");

        mode.Tick(5);
        Assert.AreEqual(0f, mode.PaperVisibility);
        Assert.IsFalse(mode.PaperRenderer.enabled);
        var group = hud.GetComponent<CanvasGroup>();
        Assert.NotNull(group, "HUD は CanvasGroup で薄める");
        Assert.AreEqual(1f, group.alpha, 1e-5);

        mode.Tick(10.125);
        Assert.AreEqual(.5f, mode.PaperVisibility, 1e-4);
        Assert.AreEqual(.5f, group.alpha, 1e-4, "HUD は試験用紙と同じだけ薄める");

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
        Assert.Less(mode.PaperRenderer.sortingOrder, mode.MarkRenderer.sortingOrder);
        Assert.Less(mode.MarkRenderer.sortingOrder, MemoryVeilLayers.AboveNoteSortingOrder);

        mode.Tick(25);
        Assert.AreEqual(0f, mode.PaperVisibility);
        Assert.AreEqual(1f, group.alpha, 1e-5);
        mode.Tick(35);
        Assert.AreEqual(1f, mode.PaperVisibility);
        mode.EndPlay();
        Assert.IsTrue(mode.Ended);
        Assert.IsFalse(mode.PaperRenderer.enabled);
        Assert.AreEqual(1f, group.alpha, 1e-5, "終わりに HUD を戻す");
        Assert.IsFalse(spawner.CutFeedbackAbovePaper);
        mode.Tick(36);
        Assert.AreEqual(0f, mode.PaperVisibility, "終わったあとは描かない");
    }

    [Test]
    public void RhythmBoardShowsTheQuestionRowsAndTheGreenAnswerRow()
    {
        mode.Tick(11);
        Assert.IsTrue(mode.ImageShown("q1.png"));
        Assert.IsFalse(mode.ImageShown("q2.png"));
        foreach (var row in new[] { "r0.png", "r1.png", "r2.png", "r3.png" }) Assert.IsTrue(mode.ImageShown(row), row);
        Assert.AreEqual(QuizRules.QuestionHeight, mode.ImageSize("q1.png").y, 1e-4);
        Assert.AreEqual(QuizRules.QuestionHeight * 4f, mode.ImageSize("q1.png").x, 1e-3, "幅は画像の縦横比");
        Assert.AreEqual(QuizRules.RowHeight, mode.ImageSize("r0.png").y, 1e-4);
        float left = mode.ImagePosition("r0.png").x - mode.ImageSize("r0.png").x * .5f;
        foreach (var row in new[] { "r1.png", "r2.png", "r3.png" })
            Assert.AreEqual(left, mode.ImagePosition(row).x - mode.ImageSize(row).x * .5f, 1e-4, "答えの行は左をそろえる");
        Assert.Greater(mode.ImagePosition("r0.png").y, mode.ImagePosition("r3.png").y, "上から順");
        Assert.Greater(mode.ImagePosition("q1.png").y, mode.ImagePosition("r0.png").y, "問題は行の上");
        Assert.Greater(mode.ImagePosition("r3.png").y - QuizRules.RowHeight * .5f, .3f, "黒板はゲートの上");
        Rgba(Color.white, mode.ImageColor("r1.png"));

        mode.Tick(12.1);
        Rgba(new Color(.5f, 1f, .35f, 1f), mode.ImageColor("r1.png"));
        Rgba(Color.white, mode.ImageColor("r0.png"));

        mode.Tick(15);
        Assert.IsFalse(mode.ImageShown("q1.png"));
        Assert.IsTrue(mode.ImageShown("q2.png"));
        Rgba(Color.white, mode.ImageColor("r2.png"));
        mode.Tick(17.55);
        Rgba(Color.white, mode.ImageColor("r2.png"));
        mode.Tick(17.65);
        Rgba(new Color(.5f, 1f, .35f, 1f), mode.ImageColor("r2.png"));
        mode.Tick(25);
        Assert.IsFalse(mode.ImageShown("q2.png"), "試験用紙の外では出さない");
    }

    [Test]
    public void BallsOfTheNextQuestionWaitForItsShowTime()
    {
        mode.Tick(11.9);
        Assert.AreEqual(3, mode.BallsDrawn, "1問目の玉(右の端から入ってくる)");
        mode.Tick(13.9);
        Assert.AreEqual(0, mode.BallsDrawn, "2問目の玉は 2問目が出る(showMs 14.0)まで出さない");
        mode.Tick(14.05);
        Assert.AreEqual(1, mode.BallsDrawn, "2問目が出たら、最初の玉から薄く現れる");
    }

    [Test]
    public void CuttingAHiddenNotePopsItsBallAtTheTarget()
    {
        var note = Spawn(12.0);
        Assert.IsTrue(note.IsHidden);
        mode.Tick(12.0);
        Assert.IsFalse(mode.IsDotPopped(0, 0));
        Cut(note);
        Assert.IsTrue(mode.IsDotPopped(0, 0), "本物の玉は的で弾けて消える");
        Assert.IsFalse(mode.IsDotPopped(0, 2));
        mode.Tick(12.02);
        Assert.AreEqual(1, mode.ActivePops, "的で輪が広がる");
        mode.Tick(12.4);
        Assert.AreEqual(0, mode.ActivePops, "0.3 秒で消える");

        DisplaySettings.SetReducedEffectsForTest(true);
        var next = Spawn(12.3);
        Cut(next);
        mode.Tick(12.32);
        Assert.IsTrue(mode.IsDotPopped(0, 2));
        Assert.AreEqual(1, mode.ActivePops, "F3 でも切ったことは的で知らせる(輪は広げない)");
    }

    [Test]
    public void MatrixShowsTheMatrixAndOnlyTheNonEmptyLabels()
    {
        var target = Spawn(31.0);
        Assert.AreEqual(QuizRules.MatrixHitScale, target.HitScale);
        Cut(target);
        mode.Tick(31.05);
        Assert.AreEqual(1, mode.ActivePops, "切った的で輪が広がる");
        mode.Tick(31.5);
        Assert.IsTrue(mode.ImageShown("m.png"));
        Assert.AreEqual("B", mode.TargetLabel(0).text);
        Assert.AreEqual("A", mode.TargetLabel(1).text);
        Assert.IsNull(mode.TargetLabel(2), "空の文字は出さない");
        Assert.IsNull(mode.TargetLabel(3));
        Assert.AreEqual(QuizRules.MatrixLabelY, mode.TargetLabel(0).transform.position.y, 1e-4);
        Assert.AreEqual(QuizRules.MatrixTargetX[1], mode.TargetLabel(1).transform.position.x, 1e-4);

        // 左のレーンは、カメラが下を向いた透視でも画面でまっすぐ縦(判定面の上では傾けてある)。
        float judgeZ = spawner.judgeZ;
        for (int lane = 0; lane < QuizRules.LaneCount; lane++)
        {
            Vector2 bottom = mode.LaneBottom(lane), top = mode.LaneTop(lane);
            Assert.AreEqual(QuizRules.LaneBottomY, bottom.y, 1e-5, "小さな丸は y −0.35");
            Assert.Greater(top.y, mode.TopEdgeY, "画面の上の端より上から");
            float bottomX = view.WorldToViewportPoint(new Vector3(bottom.x, bottom.y, judgeZ)).x;
            float topX = view.WorldToViewportPoint(new Vector3(top.x, top.y, judgeZ)).x;
            float midY = 2f;
            float midX = view.WorldToViewportPoint(new Vector3(QuizRules.LaneXAt(bottom, top, midY), midY, judgeZ)).x;
            Assert.AreEqual(bottomX, topX, 1e-3, "画面で縦 " + lane);
            Assert.AreEqual(bottomX, midX, 1e-3, "印の道も画面で縦 " + lane);
            Assert.Greater(Mathf.Abs(top.x - bottom.x), .05f, "判定面の上では傾けて透視を打ち消す");
            if (lane > 0) Assert.AreEqual(QuizRules.LaneSpacing, bottom.x - mode.LaneBottom(lane - 1).x, 1e-5, "間隔 0.28");
        }
        Assert.Less(mode.LaneBottom(0).x, -5f, "画面の左の端の近く");
        mode.Tick(33.5);
        Assert.IsFalse(mode.ImageShown("m.png"));
        Assert.IsNull(mode.TargetLabel(0));
    }

    [Test]
    public void CountingAddsAGoldMarkPerGoldBallAndRevealsTheAnswer()
    {
        mode.Tick(33.5);
        Assert.IsTrue(mode.ImageShown("lg.png"), "左上の凡例");
        Assert.IsTrue(mode.ImageShown("c.png"));
        Assert.Less(mode.ImagePosition("lg.png").x, mode.ImagePosition("c.png").x);
        Assert.Greater(mode.ImagePosition("lg.png").y, mode.ImagePosition("c.png").y);
        Assert.Less(mode.ImagePosition("c.png").x + mode.ImageSize("c.png").x * .5f, QuizRules.CountLineX - QuizRules.BallRadius,
            "問題は縦の線の左(線・玉と重ねない)");
        Assert.AreEqual(QuizRules.LegendHeight, mode.ImageSize("lg.png").y, 1e-4, "凡例は大きく");
        Assert.Greater(mode.ImageSize("c.png").y, QuizRules.QuestionHeight * 2f, "式は大きく(横に長いので少し縮めても rhythm の問題の 2 倍より大きい)");
        Assert.Less(mode.ImagePosition("c.png").y + mode.ImageSize("c.png").y * .5f,
            mode.ImagePosition("lg.png").y - mode.ImageSize("lg.png").y * .5f, "式と凡例を重ねない");
        float viewportLeft = view.WorldToViewportPoint(new Vector3(mode.ImagePosition("c.png").x - mode.ImageSize("c.png").x * .5f,
            mode.ImagePosition("c.png").y, spawner.judgeZ)).x;
        Assert.Greater(viewportLeft, 0f, "式は画面の左の端に収まる");
        Assert.AreEqual(0, mode.UnitMarks(3));
        var gold = Spawn(34.5);
        Cut(gold);
        Assert.AreEqual(1, mode.UnitMarks(3), "金の玉を切るたびに金の小さな丸");
        var white = Spawn(35.0);
        Cut(white);
        Assert.AreEqual(1, mode.UnitMarks(3), "白い玉は数えない");
        mode.Tick(35.5);
        Assert.IsTrue(mode.ImageShown("c.png"));
        Assert.IsFalse(mode.ImageShown("ca.png"));
        mode.Tick(35.95);
        Assert.IsFalse(mode.ImageShown("c.png"));
        Assert.IsTrue(mode.ImageShown("ca.png"), "revealMs に答えの画像へ差し替える");
    }

    [Test]
    public void FormulaIsFaintBehindTheNotesAndRingsUseTheBudget()
    {
        Assume.That(PlayExcitement.Current, Is.Null, "EditMode では予算の持ち主がいない(いれば予算に従う)");
        mode.Tick(36.5);
        Assert.IsTrue(mode.ImageShown("f.png"));
        Assert.AreEqual(QuizRules.FormulaAlpha, mode.ImageColor("f.png").a, 1e-4, "濃さ 0.55");
        Assert.AreEqual(9f, QuizRules.FormulaWidth, "数式の幅は 9.0(仕様の 4.2 では読めない、b1 の指示)");
        Assert.AreEqual(QuizRules.FormulaWidth, mode.ImageSize("f.png").x, 1e-3, "幅 9.0");
        var center = mode.ImagePosition("f.png");
        Assert.AreEqual(0f, center.x, 1e-4);
        Assert.AreEqual(-.15f, center.y, 1e-4, "ゲートの中央");
        Assert.Greater(center.z, spawner.judgeZ, "判定面よりわずかに奥");
        Assert.AreEqual(0, mode.ActiveRings);
        mode.Tick(36.95);
        mode.Tick(37.05);
        Assert.AreEqual(1, mode.ActiveRings, "ringsMs に輪");
        mode.Tick(37.7);
        Assert.AreEqual(0, mode.ActiveRings, "0.6 秒で消える");
        DisplaySettings.SetReducedEffectsForTest(true);
        mode.Tick(37.95);
        mode.Tick(38.05);
        Assert.AreEqual(0, mode.ActiveRings, "F3 の弱める設定では輪を出さない");
        mode.Tick(39.5);
        Assert.IsTrue(mode.ImageShown("fa.png"), "revealMs に答えの入った数式へ");
        Assert.IsFalse(mode.ImageShown("f.png"));
    }

    [Test]
    public void VisibleNotesAreDrawnAboveThePaperUntilTheEnd()
    {
        var visible = Spawn(38.5);
        Assert.IsFalse(visible.IsHidden);
        Assert.IsTrue(visible.BodyRenderer.enabled);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, visible.BodyRenderer.sortingOrder, "見えるノーツは試験用紙の上");
        var frame = visible.TimingCue.GhostRoot.GetComponentInChildren<MeshRenderer>(true);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, frame.sortingOrder, "着地の枠も");
        Assert.Greater(MemoryVeilLayers.AboveNoteSortingOrder, mode.MarkRenderer.sortingOrder, "数式・線・輪より上");
        mode.EndPlay();
        Assert.AreEqual(0, visible.BodyRenderer.sortingOrder, "終わりに元の並び順へ戻す");
        Assert.AreEqual(0, frame.sortingOrder);
    }
}

// 勉強の Q の譜面(chart_q.json、下請け G が gen_quiz.py で作る)を仕様(spec_v2)の形で確かめる。
public class BenkyouQuizChartTests
{
    const string Song = "BenkyouRemaster";
    static readonly string[] Columns = { "ABCD", "BADC", "CDAB", "DCBA" };

    static ChartData Load()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", Song, "chart_q.json");
        Assert.IsTrue(File.Exists(path), "chart_q.json は必須(gen_quiz.py --write で作る)");
        return ChartLoader.Parse(File.ReadAllText(path));
    }

    static bool Near(float a, double b, double tolerance = .5) => Math.Abs(a - b) <= tolerance;

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
    public void PapersAndQuestionsFollowTheSpecTimeline()
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
        CollectionAssert.AreEqual(Enumerable.Repeat(QuizRules.Rhythm, 4).Concat(Enumerable.Repeat(QuizRules.Matrix, 4))
            .Concat(Enumerable.Repeat(QuizRules.Count, 4)).Concat(new[] { QuizRules.Formula }), kinds);
        foreach (var item in quiz.items)
        {
            Assert.LessOrEqual(item.showMs, item.playMs, item.kind);
            Assert.Less(item.playMs, item.endMs, item.kind);
            Assert.IsTrue(QuizRules.InPaper(papers, item.showMs / 1000.0 + .3), item.kind + " は試験用紙の中");
        }

        var rhythm = quiz.items.Where(i => i.kind == QuizRules.Rhythm).ToList();
        int[] answers = { 1, 0, 3, 2 };
        for (int i = 0; i < 4; i++)
        {
            var item = rhythm[i];
            Assert.IsTrue(Near(item.showMs, 30024 + 4800 * i), "show " + i);
            Assert.IsTrue(Near(item.playMs, item.showMs + 2400), "読む1小節 " + i);
            Assert.IsTrue(Near(item.endMs, item.playMs + 2400), "叩く1小節 " + i);
            Assert.AreEqual(answers[i], item.answer, "正しい行 " + i);
            Assert.AreEqual(i == 0, item.hint, "練習は1問目だけ");
            Assert.AreEqual(4, item.rows.Count);
            Assert.IsFalse(string.IsNullOrEmpty(item.question));
            Assert.AreEqual(16, item.dots.Count, "玉は 16 分の全部の位置");
            for (int d = 0; d < 16; d++) Assert.IsTrue(Near(item.dots[d].ms, item.playMs + 150 * d), "16 分ごと " + i);
            var hits = Enumerable.Range(0, 16).Where(d => item.dots[d].real).ToList();
            Assert.That(hits.Count, Is.InRange(4, 8), "行のリズムは4〜8打");
            for (int k = 2; k < hits.Count; k++)
                Assert.IsFalse(hits[k] - hits[k - 2] == 2, "16 分の連続は2つまで " + i);
        }

        var matrix = quiz.items.Where(i => i.kind == QuizRules.Matrix).ToList();
        for (int k = 0; k < 4; k++)
        {
            var item = matrix[k];
            Assert.IsTrue(Near(item.showMs, 79674 + 3000 * k), "show " + k);
            Assert.IsTrue(Near(item.playMs, item.showMs + 750), "play " + k);
            Assert.IsTrue(Near(item.endMs, item.showMs + 3000), "end " + k);
            Assert.IsFalse(string.IsNullOrEmpty(item.question), "行列の式の画像");
            var labels = Enumerable.Range(0, 4).Select(t => t < item.labels.Count ? item.labels[t] : "").ToArray();
            if (k < 2) CollectionAssert.AreEqual(Columns[k].Select(c => c.ToString()), labels, "文字あり " + k);
            else Assert.IsTrue(labels.All(string.IsNullOrEmpty), "文字なし(行列から求める) " + k);
            Assert.Greater(item.guides.Count, 0);
            foreach (var guide in item.guides)
                Assert.IsTrue(guide.ms >= item.playMs - .5 && guide.ms < item.endMs, "印は叩く時間の中 " + k);
        }

        var count = quiz.items.Where(i => i.kind == QuizRules.Count).ToList();
        float[] bars = { 2, 2, 3, 3 };
        float show = 97674;
        for (int q = 0; q < 4; q++)
        {
            var item = count[q];
            Assert.IsTrue(Near(item.showMs, show), "show " + q);
            Assert.IsTrue(Near(item.endMs, show + 1500 * bars[q]), "小節の数 " + q);
            Assert.IsTrue(Near(item.playMs, item.showMs + 1500), "最初の1小節は読む時間 " + q);
            show = item.endMs;
            Assert.AreEqual(q + 1, QuizRules.UnitCount(item), "金の玉の数が答え " + q);
            Assert.IsFalse(string.IsNullOrEmpty(item.answerImage), "答えの入った画像 " + q);
            Assert.Greater(item.revealMs, item.showMs);
            Assert.LessOrEqual(item.revealMs, item.endMs + .5f);
        }

        var formula = quiz.items.Single(i => i.kind == QuizRules.Formula);
        Assert.IsTrue(Near(formula.showMs, 112674), "D は 112.674 秒から");
        Assert.IsFalse(string.IsNullOrEmpty(formula.question));
        Assert.IsFalse(string.IsNullOrEmpty(formula.answerImage));
        Assert.Greater(formula.ringsMs.Count, 0, "盛り上がり4の輪");
        Assert.IsTrue(formula.ringsMs.All(r => r >= 119500f), "輪は盛り上がり4(119.53 秒〜)だけ");
        var last = chart.notes.Last();
        Assert.IsTrue(Near(formula.revealMs, last.time), "答えは最後のノーツの時刻に出す");
        Assert.AreEqual("gold", last.color, "最後は真ん中の金");
        Assert.AreEqual(0f, last.x, 1e-4);
        Assert.AreEqual(-.15f, last.y, 1e-4);
        Assert.IsFalse(last.hidden);
    }

    [Test]
    public void EveryRealBallGuideAndCountBallHasExactlyOneHiddenNote()
    {
        var chart = Load();
        var hidden = chart.notes.Where(n => n.hidden).ToList();
        var used = new HashSet<NoteData>();
        NoteData Find(float ms)
        {
            var found = hidden.Where(n => Near(n.time, ms)).ToList();
            Assert.AreEqual(1, found.Count, "時刻 " + ms + " の見えないノーツは1つ");
            Assert.IsTrue(used.Add(found[0]));
            return found[0];
        }

        foreach (var item in chart.quiz.items)
        {
            if (item.kind == QuizRules.Rhythm)
                foreach (var dot in item.dots)
                {
                    if (!dot.real)
                    {
                        Assert.IsFalse(hidden.Any(n => Near(n.time, dot.ms)), "飾りの玉はノーツではない");
                        continue;
                    }
                    var note = Find(dot.ms);
                    Assert.AreEqual(QuizRules.RhythmTargetX, note.x, 1e-4);
                    Assert.AreEqual(QuizRules.RhythmLineY, note.y, 1e-4);
                    Assert.AreEqual("default", note.color, "どちらの手でもよい");
                    Assert.AreEqual(1f, QuizRules.HitScale(note.hitScale));
                    Assert.AreEqual(1, note.count);
                    Assert.AreEqual("none", note.direction);
                }
            else if (item.kind == QuizRules.Matrix)
            {
                int index = chart.quiz.items.Where(i => i.kind == QuizRules.Matrix).ToList().IndexOf(item);
                foreach (var guide in item.guides)
                {
                    var note = Find(guide.ms);
                    int target = Columns[index].IndexOf((char)('A' + guide.lane));
                    Assert.AreEqual(QuizRules.MatrixTargetX[target], note.x, 1e-4, "印が落ちたレーンの文字の的 " + index);
                    Assert.AreEqual(QuizRules.MatrixTargetY, note.y, 1e-4);
                    Assert.AreEqual(QuizRules.MatrixHitScale, note.hitScale, 1e-4, "隣の丸の中では切れない大きさ");
                    Assert.AreEqual("default", note.color);
                }
            }
            else if (item.kind == QuizRules.Count)
                foreach (var dot in item.dots)
                {
                    var note = Find(dot.ms);
                    Assert.AreEqual(QuizRules.CountLineX, note.x, 1e-4);
                    Assert.AreEqual(QuizRules.CountTargetY, note.y, 1e-4);
                    Assert.AreEqual(dot.unit ? "gold" : "default", note.color, "金の玉は金のノーツ(重み2)");
                }
        }
        Assert.AreEqual(hidden.Count, used.Count, "玉・印の無い見えないノーツを置かない");
        var papers = QuizRules.BuildPapers(chart.quiz, 0);
        foreach (var note in hidden) Assert.IsTrue(QuizRules.InPaper(papers, note.TimeSeconds), "見えないノーツは試験用紙の中だけ " + note.time);
    }

    [Test]
    public void OrdinaryNotesKeepClearOfThePapersAndComeFromTheCMix()
    {
        var chart = Load();
        var papers = QuizRules.BuildPapers(chart.quiz, 0);
        const double beat = .375;
        double formulaStart = 112.674;
        var visible = chart.notes.Where(n => !n.hidden).ToList();
        foreach (var note in visible)
        {
            double t = note.TimeSeconds;
            if (t >= formulaStart - .001) continue;
            foreach (var paper in papers)
                Assert.IsFalse(t > paper.Start - beat && t < Math.Min(paper.End, formulaStart) + beat,
                    "試験用紙とその前後1拍に、ふつうのノーツを置かない " + note.time);
        }
        int ordinary = MixedSecretChartAssertions.CheckOrdinarySubsequence(Song, chart, 50424,
            n => !n.hidden && n.TimeSeconds < formulaStart - .001);
        Assert.Greater(ordinary, 100, "試験用紙の外は C の中身(Normal → Hard)");

        var hard = ChartLoader.Parse(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "Songs", Song, "chart_hard.json")));
        var finale = visible.Where(n => n.TimeSeconds >= formulaStart - .001).ToList();
        Assert.Greater(finale.Count, 10, "最後の大問は見えるノーツで");
        // ノーツは数式の画像(幅 QuizRules.FormulaWidth、中心 (0, −0.15))の記号の上。縦の範囲は画像の縦横比で決まる。
        var formula = chart.quiz.items.Single(i => i.kind == QuizRules.Formula);
        float aspect = PngAspect(Path.Combine(Application.streamingAssetsPath, "Songs", Song, QuizRules.ImageFolder, formula.question));
        float halfWidth = QuizRules.FormulaWidth * .5f, halfHeight = QuizRules.FormulaWidth / aspect * .5f;
        foreach (var note in finale)
        {
            Assert.That(note.x, Is.InRange(QuizRules.GateCenter.x - halfWidth - .1f, QuizRules.GateCenter.x + halfWidth + .1f),
                "数式の上 " + note.time);
            Assert.That(note.y, Is.InRange(QuizRules.GateCenter.y - halfHeight - .1f, QuizRules.GateCenter.y + halfHeight + .1f),
                "数式の上 " + note.time);
        }
        // 数える問題の最後に、大問のノーツが奥から見えないよう、112.674 秒から1秒はノーツを置かない(b1 の指示で G が抜く)。
        Assert.IsTrue(finale.All(n => n.TimeSeconds >= formulaStart + 1.0 - .001), "大問の最初の1秒にノーツを置かない");
        foreach (var note in finale.Take(finale.Count - 1))
            Assert.IsTrue(hard.notes.Any(h => Near(h.time, note.time, .05)), "Hard の中身の時刻 " + note.time);
    }

    // PNG の幅 ÷ 高さ(IHDR の値)。
    static float PngAspect(string path)
    {
        Assert.IsTrue(File.Exists(path), path);
        byte[] header = new byte[24];
        using (var stream = File.OpenRead(path)) Assert.AreEqual(24, stream.Read(header, 0, 24));
        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        Assert.Greater(height, 0);
        return (float)width / height;
    }
}
