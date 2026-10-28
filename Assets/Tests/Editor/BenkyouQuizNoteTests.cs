using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 勉強の裏譜面 Q 第3版のノーツの部品(NoteSpawner と CuttableNote)。飾りの金・発射点・隠した向き。
// どれも EditMode の NoteSpawner(使い回さない)を曲の時計の代わりに直接進めて確かめる。
public abstract class BenkyouQuizSpawnerFixture
{
    protected readonly List<GameObject> created = new List<GameObject>();
    protected GameObject root;
    protected NoteSpawner spawner;
    protected ScoreManager score;

    [SetUp]
    public void SetUpSpawner()
    {
        DisplaySettings.SetReducedEffectsForTest(false);
        root = new GameObject(GetType().Name);
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
    }

    [TearDown]
    public void TearDownSpawner()
    {
        foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
        created.Clear();
        foreach (var piece in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (piece != null) Object.DestroyImmediate(piece.gameObject);
        foreach (var link in Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (link != null) Object.DestroyImmediate(link.gameObject);
        DisplaySettings.ResetReducedEffectsCacheForTest();
    }

    // 本編と同じカメラ(高さ 2.35、7 手前、12° 下向き、画角 60、16:9)。
    protected Camera MakeCamera()
    {
        var go = new GameObject("QuizNoteTestCamera");
        created.Add(go);
        var camera = go.AddComponent<Camera>();
        camera.fieldOfView = 60f;
        camera.aspect = 16f / 9f;
        go.transform.SetPositionAndRotation(new Vector3(0f, 2.35f, -7f), Quaternion.Euler(12f, 0f, 0f));
        return camera;
    }

    // 刃の無い点の剣(本編の当たりの大きさ: 刃 0.32・当たり 0.88)。
    protected (SaberCutJudge judge, SaberTracker tracker) Saber()
    {
        var go = new GameObject("QuizNoteTestSaber");
        created.Add(go);
        var tracker = go.AddComponent<SaberTracker>();
        var judge = go.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 2f;
        judge.maxCutDistance = 5f;
        judge.bladeRadius = .32f;
        judge.noteHitRadiusXY = .88f;
        return (judge, tracker);
    }

    // x を上から下へ一息に通る振り(曲の時計 songTime で判定する)。
    protected static void Swing(SaberCutJudge judge, SaberTracker tracker, float x, double songTime)
    {
        tracker.ResetTo(new Vector3(x, 1.5f, 0f));
        tracker.Tick(new Vector3(x, -1.5f, 0f), .05f);
        judge.TryCut(songTime);
    }
}

// 飾りの金(quiz.decoys)。本物の金と同じ見た目・同じ接近・同じ合図で飛ぶが、判定・点・MISS に入らない。
public class BenkyouQuizDecoyTests : BenkyouQuizSpawnerFixture
{
    [Test]
    public void DecoysFlyExactlyLikeTheRealGoldNoteAndCuttingOneIsAMiss()
    {
        var chart = new ChartData { bpm = 100 };
        chart.notes.Add(new NoteData { time = 2000, x = -.6f, y = -.15f, color = "gold", hitScale = QuizRules.PillarHitScale });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = .6f, y = -.15f });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2150, x = 1.8f, y = -.15f });
        int spawned = 0, decoys = 0;
        spawner.OnNoteSpawned += _ => spawned++;
        spawner.OnDecoySpawned += _ => decoys++;
        spawner.SetChart(chart);
        Assert.AreEqual(2, spawner.DecoyCount);
        spawner.Tick(1.2);
        Assert.AreEqual(1, spawner.LiveNotes.Count, "飾りは生きているノーツの一覧(MISS・曲の終わり)に入れない");
        Assert.AreEqual(2, spawner.LiveDecoys.Count);
        Assert.AreEqual(1, spawned, "OnNoteSpawned は本物だけ(採点・音・演出は飾りを知らない)");
        Assert.AreEqual(2, decoys, "飾りは別の知らせ(QuizMode が試験用紙の上へ上げる)");
        var real = spawner.LiveNotes[0];
        var decoy = spawner.LiveDecoys.First(d => Math.Abs(d.HitTime - 2.0) < 1e-6);
        Assert.IsTrue(decoy.IsDecoy);
        Assert.IsFalse(real.IsDecoy);
        Assert.IsTrue(decoy.IsGold, "飾りも金");
        Assert.AreEqual(new Vector2(.6f, -.15f), decoy.LandingPosition);
        Assert.AreEqual(real.transform.position.z, decoy.transform.position.z, 1e-5, "同じ接近");
        Assert.AreEqual(real.transform.localScale, decoy.transform.localScale, "同じ大きさ");
        Assert.AreEqual(real.Visuals.DisplayColor, decoy.Visuals.DisplayColor, "同じ色");
        Assert.AreEqual(real.Veil, decoy.Veil);
        Assert.AreSame(spawner, decoy.JudgmentOwner, "金の予兆の「チリッ」も本物と同じに鳴る");
        Assert.AreEqual(QuizRules.PillarHitScale, decoy.HitScale, 1e-6, "当たりは本物の柱と同じ大きさ");
        Assert.IsFalse(decoy.IsJudgeable, "判定の窓の外では刃を受けない");

        spawner.Tick(1.95);
        Assert.IsTrue(real.IsJudgeable);
        Assert.IsTrue(CuttableNote.ActiveNotes.Contains(real));
        Assert.IsTrue(decoy.IsJudgeable, "飾りも本物と同じ判定の窓で刃を受ける(切ったら MISS)");
        Assert.IsTrue(CuttableNote.ActiveNotes.Contains(decoy), "判定の候補(SaberCutJudge)に入る");
        Assert.IsTrue(decoy.TimingCue.InWindow, "判定窓の光も本物と同じ");
        Assert.AreEqual(real.TimingCue.InWindow, decoy.TimingCue.InWindow);
        Assert.AreEqual(real.TimingCue.GhostRoot.activeSelf, decoy.TimingCue.GhostRoot.activeSelf, "着地の枠");
        Assert.AreEqual(real.TimingCue.ApproachRoot.transform.localScale, decoy.TimingCue.ApproachRoot.transform.localScale, "縮む枠");
        Assert.AreEqual(decoy.LandingPosition, (Vector2)decoy.TimingCue.GhostRoot.transform.position, "枠は飾りの着地点");

        // 本物を切ってコンボを作ってから、飾りの柱を本編の当たりで振る → 飾りは割れずに暗くなり、MISS でコンボが切れる。
        spawner.Tick(2.0);
        Assert.IsTrue(real.CutAtSongTime(real.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, 2.0));
        Assert.AreEqual(1, score.Combo);
        var misses = new List<JudgmentTier>();
        score.OnJudgment += (tier, _) => { if (tier == JudgmentTier.Miss) misses.Add(tier); };
        var (judge, tracker) = Saber();
        Swing(judge, tracker, .6f, 2.0);
        Assert.IsTrue(decoy.DecoyHit, "違う答えの柱を切った");
        Assert.IsFalse(decoy.IsCut, "飾りは割れない(切った音・光・点は出ない)");
        Assert.AreEqual(1, score.MissCount, "MISS になる");
        Assert.AreEqual(0, score.Combo, "コンボが切れる");
        Assert.AreEqual(1, misses.Count, "MISS の知らせ(判定の字・音)");
        Assert.IsFalse(decoy.CutAtSongTime(decoy.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, 2.0));
        Assert.AreEqual(1, score.MissCount, "同じ飾りを二度は数えない");
        Assert.AreEqual(1, score.HitCount);
        Assert.AreEqual(1, score.JudgedNoteCount, "判定したノーツの数に飾りは入らない(曲の終わりの判断を変えない)");

        spawner.Tick(3.0);
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "届いて薄れたら消える");
        Assert.AreEqual(1, score.ComboUnits, "満コンボの数に飾りを数えない");
        Assert.AreEqual(ScoreManager.NoteScore, score.Score, "ノーツの点は 90000(分母に飾りは入らない)");
    }

    [Test]
    public void DecoysOutsideTheJudgmentWindowAreNotAMiss()
    {
        var chart = new ChartData { bpm = 100 };
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = .6f, y = -.15f });
        spawner.SetChart(chart);
        spawner.Tick(1.5);
        var decoy = spawner.LiveDecoys.Single();
        Assert.IsFalse(decoy.IsJudgeable);
        Assert.IsFalse(decoy.CutAtSongTime(decoy.transform.position, Vector3.down * 8f, CutDirection.None, SaberHand.Any, 1.5));
        Assert.IsFalse(decoy.DecoyHit, "判定の窓より前に刃が通っても MISS にしない");
        Assert.AreEqual(0, score.MissCount);
        spawner.Tick(3.0);
        Assert.AreEqual(0, score.MissCount, "切らずに通した飾りは MISS にならない");
    }

    [Test]
    public void DecoysFadeOutJustPastTheJudgePlaneWithoutTheMissDim()
    {
        var chart = new ChartData { bpm = 100 };
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = .6f, y = -.15f });
        spawner.SetChart(chart);
        spawner.Tick(1.5);
        var decoy = spawner.LiveDecoys.Single();
        spawner.Tick(2.0);
        Assert.AreEqual(0f, decoy.Veil, "届くまでは本物と同じ姿");
        Assert.AreEqual(spawner.judgeZ, decoy.transform.position.z, 1e-4, "届く時刻に判定面");
        spawner.Tick(2.15);
        Assert.Less(decoy.transform.position.z, spawner.judgeZ, "判定面を少し過ぎて進む");
        Assert.AreEqual(.5f, decoy.Veil, 1e-3, "0.3 秒で薄れる");
        Assert.AreEqual(.5f, decoy.Visuals.Veil, 1e-3, "本体も薄れる");
        Assert.IsFalse(decoy.IsMissed, "MISS にはならない(暗くならない)");
        Assert.AreEqual(1, spawner.LiveDecoys.Count);
        spawner.Tick(2.31);
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "消えたら片付ける");
        Assert.AreEqual(0, score.MissCount);
    }

    [Test]
    public void PracticeDecoysAreFaintAndHideTheirCues()
    {
        var chart = new ChartData { bpm = 100 };
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = .6f, y = -.15f, veil = .7f });
        spawner.SetChart(chart);
        spawner.Tick(1.6);
        var decoy = spawner.LiveDecoys.Single();
        Assert.AreEqual(.7f, decoy.Veil, 1e-6, "NoteData.veil と同じ見えにくさ");
        Assert.AreEqual(.7f, decoy.Visuals.Veil, 1e-6, "本体も薄い");
        Assert.IsFalse(decoy.TimingCue.GhostRoot.activeSelf, "見えにくさ 0.5 以上は着地の枠を出さない");
    }

    [Test]
    public void DecoysAreClearedOnChartResetOrClearDecoysAndSkippedWhenTheClockJumpsAhead()
    {
        ChartData Chart()
        {
            var c = new ChartData { bpm = 100 };
            foreach (float ms in new[] { 2000f, 2100f, 5000f }) c.quiz.decoys.Add(new QuizDecoyData { ms = ms, x = .6f, y = -.15f });
            return c;
        }
        spawner.SetChart(Chart());
        spawner.Tick(1.5);
        Assert.AreEqual(2, spawner.LiveDecoys.Count);
        spawner.SetChart(new ChartData { bpm = 100 });
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "譜面のやり直しで片付ける");
        Assert.AreEqual(0, spawner.DecoyCount);

        spawner.SetChart(Chart());
        spawner.Tick(1.5);
        spawner.ClearDecoys();
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "Q の終わり(曲の終わり・途中でやめたとき)で片付ける");
        spawner.Tick(4.5);
        Assert.AreEqual(0, spawner.LiveDecoys.Count, "残りの飾りも出さない");

        spawner.SetChart(Chart());
        spawner.Tick(4.2);
        Assert.AreEqual(1, spawner.LiveDecoys.Count, "先へ飛んだ時計では、もう消えているはずの飾りを出さない");
        Assert.AreEqual(5.0, spawner.LiveDecoys[0].HitTime, 1e-6);
        float z = spawner.LiveDecoys[0].transform.position.z;
        spawner.Tick(4.2);
        Assert.AreEqual(z, spawner.LiveDecoys[0].transform.position.z, 1e-6, "止まっている間(同じ曲の時計)は止まる");
    }

    [Test]
    public void DecoysRingTheGoldOmenAndAreProtectedFromTheCutLightLikeRealNotes()
    {
        var view = MakeCamera();
        // 画面の画素で保護の矩形を作るので、画素の大きさを決めておく(EditMode では画面の大きさが決まらない)。
        view.pixelRect = new Rect(0, 0, 800, 450);
        var chart = new ChartData { bpm = 100 };
        chart.notes.Add(new NoteData { time = 2000, x = -.6f, y = -.15f, color = "gold" });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = .6f, y = -.15f });
        var omens = new List<CuttableNote>();
        Action<CuttableNote> listen = omens.Add;
        NoteTimingCue.GoldOmen += listen;
        try
        {
            spawner.SetChart(chart);
            spawner.Tick(1.5);
            // 予兆は判定の窓に入る 0.2 秒前(届く 0.3 秒前)から。枠を回る光は 0.15 秒なので、細かいコマで進める。
            double omen = 2.0 - spawner.earlyJudgeWindow - NoteTimingCue.OmenLeadSeconds;
            spawner.Tick(omen - .01);
            Assert.AreEqual(0, omens.Count);
            spawner.Tick(omen + .05);
            var decoy = spawner.LiveDecoys.Single();
            Assert.AreEqual(2, omens.Count, "判定の窓に入る 0.2 秒前の予兆(枠を回る光と「チリッ」)は本物と飾りの両方");
            Assert.Contains(decoy, omens);
            Assert.IsTrue(decoy.TimingCue.OmenShown, "枠を回る光");
            Assert.AreEqual(spawner.LiveNotes.Single().TimingCue.OmenShown, decoy.TimingCue.OmenShown);
            // 切った光の保護(届く直前のノーツを光の下からくり抜く)にも本物と同じく入る。
            var feedback = root.GetComponentInChildren<GameplayCutFeedback>();
            Assert.NotNull(feedback);
            feedback.PrepareProtection(view, 2.0 - GameplayCutFeedback.ProtectionLeadSeconds * .5);
            Assert.AreEqual(2, feedback.ProtectionCount, "本物と飾りの両方を守る");
        }
        finally
        {
            NoteTimingCue.GoldOmen -= listen;
        }
    }

    [Test]
    public void DecoysNeitherJoinNorHoldBackRealNotes()
    {
        var chart = new ChartData { bpm = 100 };
        chart.notes.Add(new NoteData { time = 2050, x = -.6f, y = -.15f, color = "gold" });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2050, x = .6f, y = -.15f });
        chart.quiz.decoys.Add(new QuizDecoyData { ms = 2000, x = -.6f, y = .3f });
        spawner.SetChart(chart);
        spawner.Tick(1.5);
        Assert.AreEqual(0, Object.FindObjectsByType<SimultaneousNoteLink>(FindObjectsSortMode.None).Length,
            "同じ時刻の飾りと本物を同時押しの線で結ばない(本物どうしは結ぶ)");
        spawner.Tick(1.99);
        var real = spawner.LiveNotes.Single();
        Assert.IsTrue(real.IsJudgeable);
        Assert.AreEqual(double.NegativeInfinity, real.CutOrderStartTime(SaberHand.Any), "先に届く飾りは、本物の早切り防止に数えない");
    }
}

// 発射点(NoteData.launch)。見た目の子だけが札の位置から着地点へまっすぐ飛び、判定の根元・着地の枠・届く時刻はそのまま。
public class BenkyouQuizLaunchTests : BenkyouQuizSpawnerFixture
{
    [TestCase(.8f)]
    [TestCase(1f)]
    [TestCase(2f)]
    public void LaunchedNoteAppearsOnTheLabelAndLandsOnTimeAtEveryApproach(float approach)
    {
        var view = MakeCamera();
        spawner.LaunchView = view;
        spawner.approachTime = approach;
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 5000, x = 0f, y = -.15f, color = "gold", type = "direction", direction = "left",
            launch = true, fromX = -2.1f, fromY = 1.1f });
        spawner.SetChart(chart);
        spawner.Tick(5.0 - approach + 1e-4);
        var note = spawner.LiveNotes.Single();
        Assert.IsTrue(note.HasLaunch);
        Assert.AreEqual(5.0, note.HitTime, 1e-9, "届く時刻は変わらない");
        Assert.AreEqual(new Vector2(0f, -.15f), note.LandingPosition, "着地点は x・y のまま");
        Assert.AreEqual(0f, note.transform.position.x, 1e-6, "判定の根元は着地点");
        Assert.AreEqual(-.15f, note.transform.position.y, 1e-6);
        Assert.Greater(note.transform.position.z, spawner.judgeZ + 15f, "奥から");
        Vector3 label = view.WorldToViewportPoint(new Vector3(-2.1f, 1.1f, spawner.judgeZ));
        Vector3 body = view.WorldToViewportPoint(note.VisualTransform.position);
        Assert.AreEqual(label.x, body.x, 2e-3, "出る瞬間は画面で札の上");
        Assert.AreEqual(label.y, body.y, 2e-3);

        spawner.Tick(5.0 - approach * .5);
        Vector2 far = note.LaunchFar;
        Assert.AreEqual(far.x * .5f, note.VisualTransform.position.x, 1e-3, "途中は奥の点から着地点へまっすぐ");
        Assert.AreEqual(-.15f + (far.y + .15f) * .5f, note.VisualTransform.position.y, 1e-3);
        Assert.AreEqual(note.transform.position.z, note.VisualTransform.position.z, 1e-4, "奥行きは根元のまま");
        Assert.AreEqual(0f, note.transform.position.x, 1e-6, "根元は動かない");

        spawner.Tick(5.0);
        Assert.AreEqual(0f, note.VisualTransform.position.x, 1e-4, "届く時刻にちょうど着地点");
        Assert.AreEqual(-.15f, note.VisualTransform.position.y, 1e-4);
        Assert.AreEqual(spawner.judgeZ, note.transform.position.z, 1e-4);
        Assert.AreEqual(new Vector2(0f, -.15f), (Vector2)note.TimingCue.GhostRoot.transform.position, "着地の枠は着地点");
    }

    [Test]
    public void LaunchedNotesAreJudgedAtTheLandingPoint()
    {
        spawner.LaunchView = MakeCamera();
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 2000, x = 0f, y = -.15f, color = "gold", launch = true, fromX = -2.1f, fromY = 1.1f });
        spawner.SetChart(chart);
        spawner.Tick(1.2);
        var note = spawner.LiveNotes.Single();
        Assert.Greater(Mathf.Abs(note.VisualTransform.position.x - note.transform.position.x), .5f, "飛んでいる間、見た目だけが札の側");
        spawner.Tick(2.0);
        var (judge, tracker) = Saber();
        Swing(judge, tracker, -2.1f, 2.0);
        Assert.IsFalse(note.IsCut, "札の下を振っても切れない");
        Swing(judge, tracker, 0f, 2.0);
        Assert.IsTrue(note.IsCut, "当たりは着地点");
        Assert.AreEqual(1, score.PerfectCount);
    }

    [Test]
    public void LaunchUsesItsOwnVisualChildAndPoolKeyAndResetsOnReuse()
    {
        spawner.LaunchView = MakeCamera();
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 2000, x = 0f, y = -.15f, color = "gold", launch = true, fromX = 2.1f, fromY = 1.1f });
        chart.notes.Add(new NoteData { time = 2000, x = 1f, y = -.15f, color = "gold" });
        spawner.SetChart(chart);
        spawner.Tick(1.5);
        var launched = spawner.LiveNotes.Single(n => n.HasLaunch);
        var plain = spawner.LiveNotes.Single(n => !n.HasLaunch);
        Assert.AreNotSame(launched.transform, launched.VisualTransform, "見た目の子");
        Assert.AreSame(plain.transform, plain.VisualTransform, "ふつうのノーツは今までどおり");
        Assert.AreSame(launched.VisualTransform, launched.BodyRenderer.transform);
        Assert.IsTrue(launched.BodyRenderer.enabled);
        Assert.IsFalse(launched.GetComponent<MeshRenderer>().enabled, "本体の描画は子へ移す");
        var key = typeof(NoteSpawner).GetMethod("Key", BindingFlags.Instance | BindingFlags.NonPublic);
        object launchKey = key.Invoke(spawner, new object[] { new NoteData { color = "gold", launch = true } });
        object plainKey = key.Invoke(spawner, new object[] { new NoteData { color = "gold" } });
        Assert.AreNotEqual(launchKey, plainKey, "見た目の子を持つ発射点のノーツは別のプール");
        launched.ResetForSpawn();
        Assert.IsFalse(launched.HasLaunch);
        Assert.AreEqual(Vector3.zero, launched.VisualTransform.localPosition, "ずれを持ち越さない");
    }
}

// 隠した向き(NoteData.sideArrow "hidden")。判定は本当の向き、本体には左右の両矢印、違う向きに振ると本当の矢印が光る。
public class BenkyouQuizSideArrowTests : BenkyouQuizSpawnerFixture
{
    CuttableNote SpawnSide(string sideArrow, string direction = "left")
    {
        var chart = new ChartData { bpm = 160 };
        chart.notes.Add(new NoteData { time = 2000, x = 0f, y = -.15f, color = "gold", type = "direction", direction = direction,
            sideArrow = sideArrow });
        spawner.SetChart(chart);
        spawner.Tick(1.5);
        return spawner.LiveNotes.Single();
    }

    int FloorVertices() => spawner.FloorGuide.GetComponent<MeshFilter>().sharedMesh.vertexCount;

    [Test]
    public void HiddenSideArrowShowsTheDoubleArrowAndKeepsTheFloorNeutral()
    {
        var note = SpawnSide("hidden");
        Assert.IsTrue(note.SideArrowHidden);
        Assert.IsTrue(note.HidesSideArrow);
        Assert.AreEqual(CutDirection.Left, note.RequiredDirection, "判定は本当の向き");
        Assert.IsTrue(note.ShowsSideArrow, "本体には左右の両矢印");
        var sideBars = note.Arrow.Part(true, 1);
        var trueBars = note.Arrow.Part(false, 1);
        Assert.IsTrue(sideBars.activeInHierarchy);
        Assert.IsFalse(trueBars.activeSelf, "本当の矢印は出さない");
        Assert.IsFalse(note.Arrow.Part(false, 0).activeSelf);
        Assert.AreEqual(0f, Quaternion.Angle(Quaternion.identity, sideBars.transform.rotation), 1e-3, "両矢印は横向き(矢印の根の回転を打ち消す)");
        Assert.AreSame(trueBars.GetComponent<MeshRenderer>().sharedMaterial, sideBars.GetComponent<MeshRenderer>().sharedMaterial,
            "本当の矢印と同じ材質(見えにくさ・暗さ・光り方が同じ)");
        Assert.AreEqual(1, spawner.FloorGuide.MarkerCount);
        int neutral = FloorVertices();
        // 違う向きに振ると本当の向きが見え、床の印も本当の向きの矢印になる(中立の横線 3 本 = 12 点 → 矢印 2 枚 = 24 点)。
        Assert.IsFalse(note.CutAtSongTime(note.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, 1.9));
        spawner.Tick(1.91);
        Assert.AreEqual(neutral + 12, FloorVertices(), "床の印は、本当の向きが見えるまで中立の横線");
    }

    [Test]
    public void WrongSwingRevealsAndFlashesTheTrueArrowAndTheTrueSwingCuts()
    {
        var note = SpawnSide("hidden");
        spawner.Tick(2.0);
        Assert.IsFalse(note.CutAtSongTime(note.transform.position, Vector3.right * 8f, CutDirection.None, SaberHand.Any, 2.0),
            "違う向きでは割れない");
        Assert.IsFalse(note.IsCut);
        Assert.IsFalse(note.IsMissed, "MISS にもしない(そのまま逃せば MISS)");
        Assert.IsTrue(note.SideArrowRevealed);
        Assert.IsFalse(note.HidesSideArrow);
        Assert.IsFalse(note.ShowsSideArrow, "本当の向きの矢印に変える");
        var trueBars = note.Arrow.Part(false, 1);
        Assert.IsTrue(trueBars.activeSelf);
        Assert.IsFalse(note.Arrow.Part(true, 1).activeSelf);
        Color flash = trueBars.GetComponent<MeshRenderer>().sharedMaterial.GetColor(NoteArrowMaterials.BaseColorId);
        Color gold = note.Visuals.baseColor;
        Assert.AreEqual(gold.r, flash.r, 1e-3, "光らせる(ノーツの色を乗せてから白へ戻す)");
        Assert.AreEqual(gold.g, flash.g, 1e-3);
        Assert.AreEqual(gold.b, flash.b, 1e-3);
        Assert.IsTrue(note.CutAtSongTime(note.transform.position, Vector3.left * 8f, CutDirection.None, SaberHand.Any, 2.0),
            "本当の向き(左)なら切れる");
        Assert.AreEqual(1, score.PerfectCount);
    }

    [Test]
    public void OrdinaryArrowsKeepTheirArrowAndOnlyLeftOrRightCanBeHidden()
    {
        var note = SpawnSide("");
        Assert.IsFalse(note.SideArrowHidden);
        Assert.IsFalse(note.ShowsSideArrow);
        Assert.IsTrue(note.Arrow.Part(false, 1).activeSelf, "ふつうの矢印");
        Assert.IsNull(note.Arrow.Part(true, 1), "両矢印の部品は作らない");
        var up = SpawnSide("hidden", "up");
        Assert.IsFalse(up.SideArrowHidden, "上下の矢印は隠さない(左右の2択だけ)");
        Assert.IsFalse(up.ShowsSideArrow);
        Assert.IsTrue(up.Arrow.Part(false, 1).activeSelf);
    }

    [Test]
    public void DoubleArrowMeshIsSymmetricAndAsLargeAsTheArrow()
    {
        var mesh = FlickArrowShape.CreateSideMesh();
        var outline = FlickArrowShape.CreateSideMesh(true);
        try
        {
            Assert.AreEqual(0f, mesh.bounds.center.x, 1e-5, "左右対称");
            Assert.AreEqual(0f, mesh.bounds.center.y, 1e-5);
            Assert.AreEqual(.88f, mesh.bounds.size.x, .01f, "幅 0.88");
            Assert.AreEqual(.49f, mesh.bounds.size.y, .01f, "高さ 0.49");
            Assert.Greater(outline.bounds.size.x, mesh.bounds.size.x, "縁取りは外へ");
            Assert.AreEqual(30, mesh.triangles.Length, "山形2つと軸");
        }
        finally
        {
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(outline);
        }
    }
}
