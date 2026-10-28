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

// 勉強の裏譜面 Q 第3.1版(chart_q.json)を本編で読み、試験用紙で黒い紙が出て HUD と床の印が消え剣が上に描かれること、
// 答えの柱(本物は正しい柱だけ、ほかは判定に入らない飾りの金)、斬り分け(札から発射・隠した向き)、
// C の点の札の答えの柱、D の最後の問題の花丸を確かめる(第3.1版で C・D も答えの柱になった。count・pi は譜面に無い)。
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
    public IEnumerator PaperPillarsDecoysSortDotsAndFlowerWorkInTheGame()
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
        var score = manager.scoreManager;
        Assert.NotNull(quiz, "Q の譜面では QuizMode を作る");
        Assert.IsTrue(manager.songPlayer.IsScheduled);
        Assert.IsTrue(spawner.CurrentChart.quiz.HasContent);
        Assert.IsNull(GameObject.Find("FirstHandHint"), "裏譜面は始まりの一言を出さない");
        CollectionAssert.IsEmpty(quiz.MissingImages, "quiz/ の画像がそろっている");
        Assert.Greater(quiz.LoadedImageCount, 20);
        Assert.IsFalse(quiz.UsesFallbackImageShader, "試験用紙の画像の shader(Effects/QuizBoard)が使える");
        Assert.IsTrue(spawner.CutFeedbackAbovePaper, "切った光は試験用紙の上");
        Assert.Greater(spawner.DecoyCount, 0, "答えの柱の飾りの金");
        Assert.AreSame(Camera.main, spawner.LaunchView, "発射点は札を描くカメラから");
        Assert.IsFalse(spawner.CurrentChart.notes.Any(n => n.hidden), "第3版は見えないノーツを使わない");

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
        Assert.IsTrue(quiz.FloorGuideStopped, "床の印は紙の間だけ止める(飾りの金には床の印が無い)");
        Assert.IsFalse(spawner.FloorGuide.gameObject.activeSelf);
        Assert.IsTrue(quiz.GateOutlineDrawn, "判定面のゲートの枠を白い細線で");
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
            Assert.IsTrue(bridge.DrawAboveMemoryVeil, "剣は試験用紙の上");
        var gate = GameObject.Find("JudgeGate");
        if (gate != null)
            foreach (var renderer in gate.GetComponentsInChildren<Renderer>())
                Assert.Less(renderer.sortingOrder, paper.sortingOrder, "ゲートの枠は試験用紙の下");

        var items = quiz.Quiz.items;
        var (cutter, tracker) = Rig(sceneJudge);

        // 答えの柱(2問目、練習でない): 本物は正しい柱だけ。ほかの柱から来る金は飾りで、判定・点に入らない。
        int r = items.FindIndex(i => i.kind == QuizRules.Rhythm && !i.hint);
        Assert.GreaterOrEqual(r, 0);
        var rhythm = items[r];
        var realSource = spawner.CurrentChart.notes.First(n => n.time >= rhythm.playMs - .5f && n.time < rhythm.endMs - .5f);
        double hit = spawner.EffectiveTime(realSource);
        var real = Spawn(spawner, hit);
        Assert.IsFalse(real.IsDecoy);
        Assert.IsTrue(real.IsGold, "流れてくるのは金のノーツ");
        Assert.AreEqual(QuizRules.PillarX[rhythm.answer], real.LandingPosition.x, 1e-4, "本物は正しい答えの柱だけ");
        Assert.AreEqual(QuizRules.PillarHitScale, real.HitScale, 1e-6, "隣の柱では切れない当たり");
        Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, real.BodyRenderer.sortingOrder, "本物は試験用紙の上");
        Assert.Greater(spawner.LiveDecoys.Count, 0, "ほかの柱からは飾りの金");
        foreach (var decoy in spawner.LiveDecoys)
        {
            Assert.IsTrue(decoy.IsDecoy);
            Assert.IsTrue(decoy.IsGold, "飾りも同じ金");
            Assert.IsFalse(spawner.LiveNotes.Contains(decoy), "飾りは生きているノーツの一覧に入らない");
            Assert.IsFalse(CuttableNote.ActiveNotes.Contains(decoy), "飾りは判定の候補に入らない");
            Assert.AreEqual(MemoryVeilLayers.AboveNoteSortingOrder, decoy.BodyRenderer.sortingOrder, "飾りも本物と同じく試験用紙の上");
            Assert.Greater(Mathf.Abs(decoy.LandingPosition.x - QuizRules.PillarX[rhythm.answer]), 1f, "飾りは正しくない柱");
            Assert.AreEqual(real.transform.localScale, decoy.transform.localScale, "同じ大きさ");
        }
        var target = spawner.LiveDecoys[0];
        int hits = score.HitCount;
        Swing(cutter, tracker, target.LandingPosition.x, hit);
        Assert.IsFalse(target.IsCut, "飾りの柱を振っても割れない");
        Assert.IsFalse(real.IsCut, "隣の柱の本物も切れない");
        Assert.AreEqual(hits, score.HitCount, "飾りは点にならない");
        Swing(cutter, tracker, real.LandingPosition.x, hit);
        Assert.IsTrue(real.IsCut, "正しい柱を振ると本物が切れる");
        Assert.AreEqual(hits + 1, score.HitCount);
        quiz.Tick(hit + .02);
        string option = rhythm.options[rhythm.answer];
        Assert.IsTrue(quiz.ImageShown(rhythm.question), "問題");
        Assert.IsTrue(quiz.ImageShown(option), "札");
        Assert.AreEqual(UISkinPalette.NoteGold.b, quiz.ImageColor(option).b, 1e-3, "切った柱の札が金に光る");
        Assert.AreEqual(QuizMode.SlotCut, quiz.SlotState(r, 0));
        Assert.AreEqual(4, quiz.Squares, "柱の判定面の薄い四角");

        // 斬り分け(2問目、隠した向き): 出る瞬間は札の上、本体には左右の両矢印。違う向きでは割れず本当の向きが見え、本当の向きで切れる。
        var sorts = items.Where(i => i.kind == QuizRules.Sort).ToList();
        Assert.AreEqual(4, sorts.Count);
        var sort = sorts[1];
        int s = items.IndexOf(sort);
        var sortSource = spawner.CurrentChart.notes.First(n => n.time >= sort.playMs - .5f && n.time < sort.endMs - .5f);
        double sortHit = spawner.EffectiveTime(sortSource);
        double launch = sortHit - spawner.approachTime + .01;
        spawner.Tick(launch);
        var launched = spawner.LiveNotes.First(n => n != null && Math.Abs(n.HitTime - sortHit) < 1e-6);
        Assert.IsTrue(launched.HasLaunch, "札から発射");
        Assert.IsTrue(launched.HidesSideArrow, "本当の向きは隠す");
        Assert.IsTrue(launched.ShowsSideArrow, "本体には左右の両矢印");
        Assert.AreEqual(QuizRules.SortTarget.x, launched.LandingPosition.x, 1e-4, "真ん中の的へ");
        Assert.AreEqual(QuizRules.SortTarget.y, launched.LandingPosition.y, 1e-4);
        var cam = Camera.main;
        Vector3 label = cam.WorldToViewportPoint(new Vector3(sortSource.fromX, sortSource.fromY, spawner.judgeZ));
        Vector3 body = cam.WorldToViewportPoint(launched.VisualTransform.position);
        Assert.AreEqual(label.x, body.x, .02, "出る瞬間は画面で札の上");
        Assert.AreEqual(label.y, body.y, .02);
        quiz.Tick(launch);
        Assert.AreEqual(s, quiz.ActiveSortItem(launch));
        Assert.IsTrue(quiz.ImageShown(sort.question), "見出し");
        Assert.IsTrue(quiz.ImageShown(sort.options[0]), "札");
        Assert.IsTrue(quiz.ImageShown(sort.boxes[0]), "左の箱の語");
        spawner.Tick(sortHit);
        Assert.AreEqual(0f, launched.VisualTransform.position.x - launched.LandingPosition.x, 1e-3, "届く時刻に的の上");
        float wrong = launched.RequiredDirection == CutDirection.Left ? 1f : -1f;
        SwingSide(cutter, tracker, launched.LandingPosition.y, wrong, sortHit);
        Assert.IsFalse(launched.IsCut, "違う向きでは割れない");
        Assert.IsFalse(launched.ShowsSideArrow, "本当の向きの矢印に変わる");
        SwingSide(cutter, tracker, launched.LandingPosition.y, -wrong, sortHit);
        Assert.IsTrue(launched.IsCut, "本当の向きで切れる");
        quiz.Tick(sortHit + .3);
        Assert.AreEqual(QuizMode.SlotCut, quiz.SlotState(s, 0));
        Assert.AreEqual(UISkinPalette.NoteGold.b, quiz.ImageColor(sort.options[0]).b, 1e-3, "切れた札は金で箱へ");
        Assert.Less(quiz.Squares, 4, "斬り分けの間は答えの柱の四角を出さない");
        CollectionAssert.IsEmpty(items.Where(i => i.kind == QuizRules.Count || i.kind == QuizRules.Pi), "第3.1版に count・pi は無い");

        // C「数で答える」(斬り分けのあとの答えの柱): 札は点の数。本物は正しい札の柱だけ、ほかの3本の柱には同じ時刻に飾りの金。
        int c = items.FindIndex(i => i.kind == QuizRules.Rhythm && i.showMs > sort.showMs);
        Assert.Greater(c, s, "斬り分けのあとの答えの柱");
        var dots = items[c];
        var dotSource = spawner.CurrentChart.notes.First(n => n.time >= dots.playMs - .5f && n.time < dots.endMs - .5f);
        double dotHit = spawner.EffectiveTime(dotSource);
        var dotNote = Spawn(spawner, dotHit);
        Assert.IsFalse(dotNote.IsDecoy);
        Assert.IsTrue(dotNote.IsGold);
        Assert.AreEqual(QuizRules.PillarX[dots.answer], dotNote.LandingPosition.x, 1e-4, "本物は正しい札の柱だけ");
        Assert.AreEqual(QuizRules.PillarHitScale, dotNote.HitScale, 1e-6);
        var sameTime = spawner.LiveDecoys.Where(d => Math.Abs(d.HitTime - dotHit) < 1e-6).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(0, 4).Where(pillar => pillar != dots.answer).ToArray(),
            sameTime.Select(d => QuizRules.NearestPillar(d.LandingPosition.x)).OrderBy(pillar => pillar).ToArray(),
            "同じ時刻に、ほかの3本の柱から飾りの金");
        int dotHits = score.HitCount;
        Swing(cutter, tracker, sameTime[0].LandingPosition.x, dotHit);
        Assert.IsFalse(dotNote.IsCut, "飾りの柱を振っても本物は切れない");
        Assert.AreEqual(dotHits, score.HitCount, "飾りは点にならない");
        Swing(cutter, tracker, dotNote.LandingPosition.x, dotHit);
        Assert.IsTrue(dotNote.IsCut, "正しい札の柱で切れる");
        Assert.AreEqual(dotHits + 1, score.HitCount);
        quiz.Tick(dotHit + .02);
        Assert.IsTrue(quiz.ImageShown(dots.question), "問題");
        foreach (var file in dots.options) Assert.IsTrue(quiz.ImageShown(file), "点の札 " + file);
        Assert.AreEqual(UISkinPalette.NoteGold.b, quiz.ImageColor(dots.options[dots.answer]).b, 1e-3, "切った柱の札が金に光る");
        Assert.AreEqual(4, quiz.Squares, "答えの柱の問題の間は柱の判定面の薄い四角");

        // D: C と D のあいだも黒いまま。最後の問題(D3)は、最後の本物が届いたら正しい札を赤い花丸で囲む。
        var lastPaper = spawner.CurrentChart.quiz.papers[spawner.CurrentChart.quiz.papers.Count - 1];
        var first = items.First(i => i.kind == QuizRules.Rhythm && Math.Abs(i.showMs - lastPaper.startMs) < .5f);
        double join = QuizRules.Seconds(first.showMs, offset);
        quiz.Tick(join - .05);
        Assert.AreEqual(1f, quiz.PaperVisibility, "C と D のあいだも黒いまま");
        quiz.Tick(join + .05);
        Assert.AreEqual(1f, quiz.PaperVisibility);
        Assert.IsTrue(quiz.ImageShown(first.question), "D の1問目");
        var final = items[items.Count - 1];
        Assert.AreEqual(QuizRules.Rhythm, final.kind, "最後は答えの柱");
        Assert.IsFalse(string.IsNullOrEmpty(final.flowerImage), "最後の問題は花丸");
        double reveal = QuizRules.Seconds(final.revealMs, offset);
        quiz.Tick(reveal - .05);
        Assert.IsTrue(quiz.ImageShown(final.options[final.answer]), "正しい札");
        Assert.IsFalse(quiz.ImageShown(final.flowerImage), "最後の本物が届くまで花丸は出さない");
        quiz.Tick(reveal + .3);
        Assert.IsTrue(quiz.ImageShown(final.flowerImage), "最後の本物のあと、正しい札を花丸で囲む");
        Assert.AreEqual(4, quiz.Squares);

        // 試験用紙の外(ふつうの Hard の所)では、黒い紙・HUD・床の印を戻す。
        quiz.Tick(65);
        Assert.AreEqual(0f, quiz.PaperVisibility);
        Assert.AreEqual(1f, hudGroup.alpha, 1e-5);
        Assert.IsTrue(spawner.FloorGuide.gameObject.activeSelf);

        quiz.EndPlay();
        Assert.IsFalse(quiz.PaperRenderer.enabled);
        Assert.AreEqual(1f, hudGroup.alpha, 1e-5);
        Assert.IsFalse(spawner.CutFeedbackAbovePaper);
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "終わりに飾りを片付ける");
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

    // 高さ y を横に一息に通る振り(direction が +1 なら右へ、−1 なら左へ)。
    static void SwingSide(SaberCutJudge judge, SaberTracker tracker, float y, float direction, double songTime)
    {
        tracker.ResetTo(new Vector3(-1.5f * direction, y, 0f));
        tracker.Tick(new Vector3(1.5f * direction, y, 0f), .05f);
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
