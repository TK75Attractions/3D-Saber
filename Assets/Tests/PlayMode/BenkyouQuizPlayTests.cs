using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

// 勉強の裏譜面 Q(chart_q.json)を本編で読み、試験用紙で黒い紙が出て HUD が消え剣が上に描かれること、
// 見えないノーツを切るとその玉が的で弾けること、行列の的は隣では切れないこと、最後の大問の数式と見えるノーツを確かめる。
// 曲は止め、時刻を選んで同じ本体(NoteSpawner・QuizMode)を進める。結果画面には進めず、失敗時もシーン・選曲・設定を戻す。
public class BenkyouQuizPlayTests
{
    const string SongId = "BenkyouRemaster";
    readonly Dictionary<FieldInfo, object> sessionValues = new Dictionary<FieldInfo, object>();
    readonly List<GameObject> created = new List<GameObject>();
    GamePlayManager manager;

    [SetUp]
    public void SetUp()
    {
        sessionValues.Clear();
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) sessionValues[field] = field.GetValue(null);
        // 後始末でタイトルへ入る(ResetPlayerSettings)。本物の判定のずらし・判定調整の下書き・速度は消さない
        // (速度もこのテストの中だけの値にする)。解除はタイトルへの移動と後始末が終わってから。
        GameSession.SetPlayerSettingsForTest(true);
        DisplaySettings.SetReducedEffectsForTest(false);
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        foreach (var go in created) if (go != null) Object.Destroy(go);
        created.Clear();
        if (manager != null)
        {
            manager.enabled = false;
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        }
        if (SceneManager.GetActiveScene().name == "Game")
            yield return SceneManager.LoadSceneAsync("Title", LoadSceneMode.Single);
        foreach (var pair in sessionValues) pair.Key.SetValue(null, pair.Value);
        GameSession.SetPlayerSettingsForTest(false);
        DisplaySettings.ResetReducedEffectsCacheForTest();
        manager = null;
    }

    [UnityTest]
    public IEnumerator PaperHiddenNotesMatrixAndFormulaWorkInTheGame()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", SongId);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "chart_q.json")), "chart_q.json は必須。通常譜面へのフォールバックで通さない");

        if (InputPoint.Instance != null)
        {
            Object.Destroy(InputPoint.Instance.gameObject);
            yield return null;
        }
        GameSession.SelectedSongId = SongId;
        GameSession.SelectedSongTitle = "勉強(Remaster)";
        GameSession.SelectedDifficulty = "Q";
        GameSession.IsCalibrationMode = false;
        GameSession.TutorialPending = false;
        GameSession.NoteApproachTime = 1f;
        yield return SceneManager.LoadSceneAsync("Game", LoadSceneMode.Single);

        manager = Object.FindFirstObjectByType<GamePlayManager>();
        Assert.NotNull(manager);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager.songPlayer == null || !manager.songPlayer.IsScheduled || manager.noteSpawner == null
            || manager.noteSpawner.CurrentChart == null || manager.QuizMode == null)
            && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        var spawner = manager.noteSpawner;
        var quiz = manager.QuizMode;
        Assert.NotNull(quiz, "Q の譜面では QuizMode を作る");
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.IsTrue(spawner.CurrentChart.quiz.HasContent);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりの一言を出さない");
        CollectionAssert.IsEmpty(quiz.MissingImages, "quiz/ の画像がそろっている");
        Assert.Greater(quiz.LoadedImageCount, 20);
        Assert.IsFalse(quiz.UsesFallbackImageShader, "黒板の画像の shader(Effects/QuizBoard)が使える");
        Assert.IsTrue(spawner.CutFeedbackAbovePaper, "見えるノーツを切った光は試験用紙の上");

        // 実機やマウスの入力で切らない。曲を止め、時刻を選んで同じ本体を進める。
        var sceneJudge = manager.cutJudge;
        foreach (var judge in Object.FindObjectsByType<SaberCutJudge>(FindObjectsSortMode.None)) judge.enabled = false;
        manager.cameraImuJudgment = null;
        manager.enabled = false;
        manager.songPlayer.Stop();
        // 判定の時計は曲の時計そのもの(カメラの遅れを引かない)。
        spawner.JudgeLatencySeconds = 0;

        double offset = quiz.OffsetSeconds;
        var papers = quiz.Papers;
        Assert.AreEqual(3, papers.Count, "A・B と、つながった C+D");
        var hud = Object.FindFirstObjectByType<GameHUDSkin>();
        Assert.NotNull(hud);
        var hudGroup = hud.GetComponent<CanvasGroup>();
        Assert.NotNull(hudGroup, "HUD は CanvasGroup で薄める");

        quiz.Tick(papers[0].Start - 1);
        Assert.AreEqual(0f, quiz.PaperVisibility);
        Assert.IsFalse(quiz.PaperRenderer.enabled);
        Assert.AreEqual(1f, hudGroup.alpha, 1e-5);

        quiz.Tick(papers[0].Start + .5);
        Assert.AreEqual(1f, quiz.PaperVisibility);
        var paper = quiz.PaperRenderer;
        Assert.IsTrue(paper.enabled, "黒い試験用紙");
        Assert.AreEqual(MemoryVeilLayers.NoiseSortingOrder, paper.sortingOrder);
        Assert.AreSame(Camera.main.transform, paper.transform.parent, "カメラのすぐ前の板");
        foreach (var color in quiz.PaperMesh.colors)
        {
            Assert.AreEqual(0f, color.r);
            Assert.AreEqual(0f, color.g);
            Assert.AreEqual(0f, color.b);
            Assert.AreEqual(1f, color.a);
        }
        Assert.AreEqual(0f, hudGroup.alpha, 1e-5, "HUD(点・コンボ)は消える");
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
            Assert.IsTrue(bridge.DrawAboveMemoryVeil, "剣は試験用紙の上");
        var gate = GameObject.Find("JudgeGate");
        if (gate != null)
            foreach (var renderer in gate.GetComponentsInChildren<Renderer>())
                Assert.Less(renderer.sortingOrder, paper.sortingOrder, "ゲートの枠は試験用紙の下");

        // 1問目(練習): 問題と答えの行。
        var items = quiz.Quiz.items;
        int r0 = items.FindIndex(i => i.kind == QuizRules.Rhythm);
        Assert.GreaterOrEqual(r0, 0);
        var rhythm = items[r0];
        quiz.Tick(QuizRules.Seconds(rhythm.showMs, offset) + .5);
        Assert.IsTrue(quiz.ImageShown(rhythm.question), "問題の画像");
        foreach (var row in rhythm.rows) Assert.IsTrue(quiz.ImageShown(row), "答えの行 " + row);

        // 見えないノーツ: 最初の本物の玉と同じ時刻のノーツを、的を通る振りで切ると、その玉が的で弾けて消える。
        var (cutter, tracker) = Rig(sceneJudge);
        int dot = rhythm.dots.FindIndex(d => d.real);
        double hit = QuizRules.Seconds(rhythm.dots[dot].ms, offset);
        var note = Spawn(spawner, hit);
        Assert.IsTrue(note.IsHidden);
        Assert.IsFalse(note.BodyRenderer.enabled, "本体を出さない");
        Assert.IsFalse(note.TimingCue.GhostRoot.activeSelf, "着地の枠を出さない");
        Assert.IsTrue(note.IsJudgeable, "受付窓はふつうどおり");
        Swing(cutter, tracker, QuizRules.RhythmTargetX, hit);
        Assert.IsTrue(note.IsCut, "見えないノーツを切れる");
        quiz.Tick(hit + .05);
        Assert.IsTrue(quiz.IsDotPopped(r0, dot), "その玉は的で弾けて消える");
        Assert.GreaterOrEqual(quiz.ActivePops, 1, "的で輪が広がる");

        // 行列: 当たりを MatrixHitScale(0.45)倍にした的は、隣の丸の内側の縁(的の中心から 1.2 − 0.3 = 0.9)を
        // 縦に通る振りでは切れず、正しい的の丸の中(中心から 0.25)を通る振りで切れる。
        int m0 = items.FindIndex(i => i.kind == QuizRules.Matrix);
        Assert.GreaterOrEqual(m0, 0);
        var matrix = items[m0];
        double matrixHit = QuizRules.Seconds(matrix.guides[0].ms, offset);
        var target = Spawn(spawner, matrixHit);
        Assert.IsTrue(target.IsHidden);
        Assert.AreEqual(QuizRules.MatrixHitScale, target.HitScale, 1e-6);
        float neighbourEdge = QuizRules.MatrixTargetSpacing - QuizRules.MatrixTargetRadius;
        Assert.Less(cutter.HitRangeFor(target), neighbourEdge, "隣の丸の縁まで届かない当たり");
        float x = target.LandingPosition.x;
        Assert.AreEqual(QuizRules.MatrixTargetX[QuizRules.NearestMatrixTarget(x)], x, 1e-4, "見えないノーツは的の上");
        float toward = x < 0f ? 1f : -1f; // まん中の側には必ず隣の的がある
        Swing(cutter, tracker, x + toward * neighbourEdge, matrixHit);
        Assert.IsFalse(target.IsCut, "隣の丸の内側の縁では切れない");
        Swing(cutter, tracker, x + toward * (QuizRules.MatrixTargetRadius - .05f), matrixHit);
        Assert.IsTrue(target.IsCut, "正しい的の丸の中で切れる");
        quiz.Tick(matrixHit + .05);
        Assert.GreaterOrEqual(quiz.ActivePops, 1, "切った的で輪が広がる");
        Assert.IsTrue(quiz.ImageShown(matrix.question), "行列の式");
        for (int t = 0; t < QuizRules.MatrixTargetX.Length; t++)
        {
            string label = t < matrix.labels.Count ? matrix.labels[t] : "";
            var text = quiz.TargetLabel(t);
            if (string.IsNullOrEmpty(label)) Assert.IsNull(text, "空の文字は出さない");
            else
            {
                Assert.NotNull(text);
                Assert.AreEqual(label, text.text);
            }
        }

        // 最後の大問: 数式の画像(幅 FormulaWidth・濃さ FormulaAlpha)と、試験用紙の上に描く見えるノーツ。
        var formula = items.Find(i => i.kind == QuizRules.Formula);
        Assert.NotNull(formula);
        var source = spawner.CurrentChart.notes.First(n => !n.hidden && n.time >= formula.showMs);
        double finaleHit = spawner.EffectiveTime(source);
        var visible = Spawn(spawner, finaleHit);
        Assert.IsFalse(visible.IsHidden);
        Assert.IsTrue(visible.BodyRenderer.enabled);
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, visible.BodyRenderer.sortingOrder, "見えるノーツは試験用紙の上");
        double shown = Math.Max(finaleHit, QuizRules.Seconds(formula.showMs, offset) + .3);
        quiz.Tick(shown);
        Assert.AreEqual(1f, quiz.PaperVisibility, "C と D のあいだも黒いまま");
        string image = QuizRules.Revealed(formula, offset, shown) ? formula.answerImage : formula.question;
        Assert.IsTrue(quiz.ImageShown(image), "数式の画像");
        Assert.AreEqual(QuizRules.FormulaAlpha, quiz.ImageColor(image).a, 1e-4);
        Assert.AreEqual(QuizRules.FormulaWidth, quiz.ImageSize(image).x, 1e-3);

        // 試験用紙の外(ふつうの Hard の所)では、黒い紙と HUD を戻す。
        quiz.Tick(65);
        Assert.AreEqual(0f, quiz.PaperVisibility);
        Assert.AreEqual(1f, hudGroup.alpha, 1e-5);

        quiz.EndPlay();
        Assert.IsFalse(quiz.PaperRenderer.enabled);
        Assert.AreEqual(1f, hudGroup.alpha, 1e-5);
        Assert.IsFalse(spawner.CutFeedbackAbovePaper);
        Assert.AreEqual(0, visible.BodyRenderer.sortingOrder, "終わりに元の並び順へ戻す");
    }

    // 刃の無い点の剣(本編と同じ当たりの大きさ)。位置はテストが入れるので、毎コマの追従は止める。
    (SaberCutJudge judge, SaberTracker tracker) Rig(SaberCutJudge scene)
    {
        var go = new GameObject("QuizTestSaber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        tracker.enabled = false;
        var judge = go.AddComponent<SaberCutJudge>();
        judge.autonomous = false;
        judge.saber = tracker;
        judge.bladeRadius = scene != null ? scene.bladeRadius : .32f;
        judge.noteHitRadiusXY = scene != null ? scene.noteHitRadiusXY : .88f;
        judge.minCutSpeed = 2f;
        judge.maxCutDistance = 5f;
        return (judge, tracker);
    }

    // x を上から下へ一息に通る振り(曲の時計 songTime で判定する)。振るたびに前の振りを持ち越さない。
    static void Swing(SaberCutJudge judge, SaberTracker tracker, float x, double songTime)
    {
        tracker.ResetTo(new Vector3(x, 1.4f, 0f));
        tracker.Tick(new Vector3(x, -1.6f, 0f), .05f);
        judge.TryCut(songTime);
    }

    // 届く時刻 hit のノーツを出し、受付窓をその時刻へ合わせる。
    static CuttableNote Spawn(NoteSpawner spawner, double hit)
    {
        spawner.Tick(hit - spawner.approachTime * .5);
        var note = spawner.LiveNotes.FirstOrDefault(n => n != null && Math.Abs(n.HitTime - hit) < 1e-6);
        Assert.NotNull(note, "時刻 " + hit + " のノーツ");
        spawner.Tick(hit);
        return note;
    }
}
