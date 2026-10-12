using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class SaberCutJudgeTests
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
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    private (SaberCutJudge judge, SaberTracker tracker) MakeRig()
    {
        var saberGo = new GameObject("saber");
        created.Add(saberGo);
        var tracker = saberGo.AddComponent<SaberTracker>();
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.minCutSpeed = 3.0f;
        judge.bladeRadius = 0.2f;
        judge.maxCutDistance = 5.0f;
        judge.noteHitRadiusXY = 0.3f;
        return (judge, tracker);
    }

    private CuttableNote MakeNote(Vector3 pos, bool judgeable = true)
    {
        var go = new GameObject("note");
        created.Add(go);
        go.transform.position = pos;
        var n = go.AddComponent<CuttableNote>();
        n.IsJudgeable = judgeable;
        return n;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImuHint_ExpiredReceiveTimeDoesNotRescueOppositeCuts(bool bladeMode)
    {
        CheckSimultaneousHint(bladeMode, 0.40, false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImuHint_FreshEventRescuesBothSimultaneousCuts(bool bladeMode)
    {
        CheckSimultaneousHint(bladeMode, 0.02, true);
    }

    private void CheckSimultaneousHint(bool bladeMode, double receiveAgeSeconds, bool expectedCorrect)
    {
        var loggerGo = new GameObject("swingLogger");
        created.Add(loggerGo);
        var logger = loggerGo.AddComponent<Swing8DirectionLogger>();
        // EditModeではUnityの起動コールバックを明示的に実行する。
        typeof(Swing8DirectionLogger).GetMethod("Awake",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(logger, null);
        // 配送は今でも、方向の有効期間は元の受信時刻から数える。
        long received = SwingMonotonicClock.Timestamp
            - (long)(receiveAgeSeconds * System.Diagnostics.Stopwatch.Frequency);
        var swing = new SwingEvent(1, SwingDirection.Left, 0.8f, 10, received, double.NaN)
            .WithMainThreadHandoff(SwingMonotonicClock.Timestamp);
        typeof(Swing8DirectionLogger).GetMethod("HandleSwing",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(logger, new object[] { swing });

        var first = MakeNote(Vector3.zero);
        var second = MakeNote(new Vector3(0.5f, 0f, 0f));
        first.RequiredDirection = second.RequiredDirection = CutDirection.Left;
        if (bladeMode)
        {
            var (judge, tracker, bridge) = MakeBladeRig();
            tracker.ResetTo(new Vector3(-2f, 0f, 0f));
            tracker.Tick(Vector3.zero, 0.1f);
            bridge.OverrideBlade(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f));
            Assert.AreEqual(0, judge.TryCut());
            tracker.Tick(new Vector3(4f, 0f, 0f), 0.1f);
            bridge.OverrideBlade(new Vector3(3f, 0f, 0f), new Vector3(5f, 0f, 0f));
            Assert.AreEqual(2, judge.TryCut());
        }
        else
        {
            var (judge, tracker) = MakeRig();
            tracker.ResetTo(new Vector3(-2f, 0f, 0f));
            tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
            Assert.AreEqual(2, judge.TryCut());
        }
        Assert.AreEqual(expectedCorrect, first.IsCut);
        Assert.AreEqual(expectedCorrect, second.IsCut);
        Assert.AreEqual(expectedCorrect ? 1 : 0, first.CutsAchieved);
        Assert.AreEqual(expectedCorrect ? 1 : 0, second.CutsAchieved);
    }

    [Test]
    public void TryCut_WithoutPreviousFrame_ReturnsZero()
    {
        var (judge, _) = MakeRig();
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void TryCut_BelowMinSpeed_Skips()
    {
        var (judge, tracker) = MakeRig();
        tracker.ResetTo(Vector3.zero);
        tracker.Tick(new Vector3(0.01f, 0, 0), 1f);
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void TryCut_ExceedsMaxDistance_Skips()
    {
        var (judge, tracker) = MakeRig();
        tracker.ResetTo(Vector3.zero);
        // maxCutDistance=5 を超える 10
        tracker.Tick(new Vector3(10f, 0, 0), 0.1f);
        MakeNote(new Vector3(2f, 0f, 0f));
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void SingleFramePassThrough_CutsNote()
    {
        var (judge, tracker) = MakeRig();
        MakeNote(new Vector3(0f, 0f, 0f));
        // 左 → 右 を一気に通過、終点は十分遠い
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f); // Speed=40, dist=4
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
    }

    [Test]
    public void EnterButNotExit_DoesNotCut()
    {
        var (judge, tracker) = MakeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));
        // 左から note 中心まで入るだけ
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(0f, 0f, 0f), 0.1f); // Speed=20, 終点が note 中心 → まだ出ていない
        int cuts = judge.TryCut();
        Assert.AreEqual(0, cuts, "入ったが出ていないので切らない");
        Assert.IsFalse(note.IsCut);
        Assert.Greater(judge.PendingCount, 0, "保留中として残る");
    }

    [Test]
    public void EnterThenExitAcrossFrames_CutsOnExit()
    {
        var (judge, tracker) = MakeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));
        // フレーム1：入る（まだ出ない）
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(0f, 0f, 0f), 0.1f);
        Assert.AreEqual(0, judge.TryCut());
        // フレーム2：通り抜ける
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
        Assert.IsTrue(note.IsCut);
    }

    [Test]
    public void NonJudgeableNote_Ignored()
    {
        var (judge, tracker) = MakeRig();
        MakeNote(Vector3.zero, judgeable: false);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        Assert.AreEqual(0, judge.TryCut());
    }

    [Test]
    public void ZCoordinate_Ignored()
    {
        var (judge, tracker) = MakeRig();
        // ノーツは Z=+5、セーバーは Z=0 で動く。XY 判定のみなので切れるはず。
        MakeNote(new Vector3(0f, 0f, 5f));
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(2f, 0f, 0f), 0.1f);
        int cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
    }

    [Test]
    public void DistPointToSegment_ComputesPerpendicularDistance()
    {
        float d = SaberCutJudge.DistPointToSegment(
            new Vector2(0f, 3f),
            new Vector2(-5f, 0f), new Vector2(5f, 0f),
            out Vector2 closest);
        Assert.AreEqual(3f, d, 0.001f);
        Assert.AreEqual(0f, closest.x, 0.001f);
        Assert.AreEqual(0f, closest.y, 0.001f);
    }

    // ------ Blade (line) mode ------

    private (SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge) MakeBladeRig()
    {
        var saberGo = new GameObject("saberBlade");
        created.Add(saberGo);
        var tracker = saberGo.AddComponent<SaberTracker>();
        var bridge = saberGo.AddComponent<SaberInputBridge>();
        // ブレードラインの自動生成（LineRenderer / Material）を抑える：HasBlade は OverrideBlade で立てる。
        bridge.useBladeMode = false;
        var judge = saberGo.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        judge.bladeProvider = bridge;
        judge.minCutSpeed = 3.0f;
        judge.bladeRadius = 0.2f;
        judge.maxCutDistance = 5.0f;
        judge.noteHitRadiusXY = 0.3f;
        return (judge, tracker, bridge);
    }

    [Test]
    public void Blade_NoteOnSegment_EnterAndExit_Cuts()
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));

        // 中点はノーツ上、ブレードはノーツを通過中（端点 (-1,0)→(1,0)）
        tracker.ResetTo(new Vector3(0f, 0f, 0f));
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f); // Speed=20
        bridge.OverrideBlade(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f));
        int cuts = judge.TryCut();
        Assert.AreEqual(0, cuts, "ブレード上にノーツが乗ってる間は pending");
        Assert.Greater(judge.PendingCount, 0);

        // ブレードを大きく外す → exit → cut
        bridge.OverrideBlade(new Vector3(3f, 3f, 0f), new Vector3(5f, 3f, 0f));
        tracker.Tick(new Vector3(4f, 3f, 0f), 0.05f);
        cuts = judge.TryCut();
        Assert.AreEqual(1, cuts);
        Assert.IsTrue(note.IsCut);
    }

    [Test]
    public void Blade_NoteFarFromSegment_NoCut()
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        MakeNote(new Vector3(0f, 3f, 0f)); // y=3 のところにノーツ
        tracker.ResetTo(new Vector3(0f, 0f, 0f));
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f);
        // ブレードは y=0 で水平に走る → ノーツとは 3 離れてる（hitRange=0.5）
        bridge.OverrideBlade(new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f));
        int cuts = judge.TryCut();
        Assert.AreEqual(0, cuts);
        Assert.AreEqual(0, judge.PendingCount);
    }

    [Test]
    public void Blade_BelowMinSpeed_DoesNotEnterPending()
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        MakeNote(new Vector3(0f, 0f, 0f));
        tracker.ResetTo(new Vector3(0f, 0f, 0f));
        tracker.Tick(new Vector3(0.01f, 0f, 0f), 1f); // Speed=0.01
        bridge.OverrideBlade(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f));
        int cuts = judge.TryCut();
        Assert.AreEqual(0, cuts);
        Assert.AreEqual(0, judge.PendingCount, "minCutSpeed 未満なら pending に入らない");
    }

    // 進入時は有効でも、抜ける前に判定窓が閉じたら古い進入を使わない。
    [TestCase(false, 1)] [TestCase(true, 1)]
    [TestCase(false, 2)] [TestCase(true, 2)]
    public void PendingCut_ExpiredWindowDoesNotCompleteTapOrLong(bool bladeMode, int count)
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        if (!bladeMode) judge.bladeProvider = null;
        var root = new GameObject("PendingWindowSpawner"); created.Add(root);
        var prefab = new GameObject("PendingWindowPrefab"); created.Add(prefab);
        prefab.AddComponent<CuttableNote>(); prefab.SetActive(false);
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab; spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        CuttableNote note = null;
        spawner.OnNoteSpawned += n => { note = n; n.gameObject.SetActive(true); created.Add(n.gameObject); };
        spawner.SetChart(new ChartData { notes = new List<NoteData> {
            new NoteData { time = 1000, x = 0, y = 0, color = "red", type = count > 1 ? "long" : "tap", count = count }
        } });
        spawner.Tick(1);
        Assert.NotNull(note);
        if (count > 1) note.Cut(Vector3.zero, Vector3.right * 10);
        double end = note.HitTime + spawner.judgeWindow + (count - 1) * spawner.secondsPerLongCut;
        spawner.Tick(end - .01);
        tracker.ResetTo(new Vector3(-2, 0, 0));
        tracker.Tick(Vector3.zero, .05f);
        if (bladeMode) bridge.OverrideBlade(Vector3.left, Vector3.right);
        Assert.AreEqual(0, judge.TryCut());
        Assert.AreEqual(1, judge.PendingCount);

        spawner.Tick(end + .02);
        Assert.False(note.IsJudgeable);
        Assert.False(note.IsMissed, "ミス確定までの猶予中にも古い進入を破棄する");
        tracker.Tick(new Vector3(2, 0, 0), .05f);
        if (bladeMode) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
        Assert.AreEqual(0, judge.TryCut());
        Assert.False(note.IsCut);
        Assert.AreEqual(count - 1, note.CutsAchieved);
        Assert.AreEqual(0, judge.PendingCount);
    }

    // ------ 1回の振り = 1つの時刻(後続ノーツの巻き込み防止) と、いちばん近づいた時刻での採点 ------

    private CuttableNote TimedNote(Vector3 pos, double hitTime)
    {
        var note = MakeNote(pos);
        note.HitTime = hitTime;
        return note;
    }

    [Test]
    public void OneSwingDoesNotRopeInTheFollowingNote_ButTheReturnSwingCutsIt()
    {
        var (judge, tracker) = MakeRig();
        var first = TimedNote(Vector3.zero, 1.0);
        var next = TimedNote(new Vector3(1f, 0f, 0f), 1.3);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(3f, 0f, 0f), 0.1f); // 1回の振りが2つとも通り抜ける
        judge.TryCut(1.0);
        Assert.True(first.IsCut, "タイミングの合ったノーツを切る");
        Assert.False(next.IsCut, "同じ振りの続きで、後のノーツは切らない");
        tracker.Tick(new Vector3(-2f, 0f, 0f), 0.1f); // 振り返し = 次の振り
        judge.TryCut(1.3);
        Assert.True(next.IsCut);
        Assert.AreEqual(1.3, next.LastCutSongTime.Value, 1e-9);
    }

    [Test]
    public void OneSwingPicksTheNoteWhoseTimingMatchesBest()
    {
        var (judge, tracker) = MakeRig();
        var late = TimedNote(Vector3.zero, 1.0);
        var onTime = TimedNote(new Vector3(1f, 0f, 0f), 1.15);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(3f, 0f, 0f), 0.1f);
        judge.TryCut(1.15);
        Assert.True(onTime.IsCut, "曲時計とヒット時刻が近いほうを切る");
        Assert.False(late.IsCut, "同じ振りで2つの時刻は切らない");
    }

    [Test]
    public void SimultaneousNotesAreCutTogetherInOneSwing()
    {
        var (judge, tracker) = MakeRig();
        var left = TimedNote(Vector3.zero, 1.0);
        var right = TimedNote(new Vector3(1f, 0f, 0f), 1.0);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(3f, 0f, 0f), 0.1f);
        Assert.AreEqual(2, judge.TryCut(1.0));
        Assert.True(left.IsCut);
        Assert.True(right.IsCut);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PausingBeforeTheNextNoteStartsANewSwing(bool pause)
    {
        var (judge, tracker) = MakeRig();
        var first = TimedNote(Vector3.zero, 1.0);
        var next = TimedNote(new Vector3(3f, 0f, 0f), 1.3);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.1f); // 1つ目だけを通過
        judge.TryCut(1.0);
        Assert.True(first.IsCut);
        if (pause)
        {
            // 0.1秒止まる(strokeRestSeconds=0.08 を超える)
            tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.05f); judge.TryCut(1.1);
            tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.05f); judge.TryCut(1.15);
        }
        tracker.Tick(new Vector3(5f, 0f, 0f), 0.1f); // 同じ向きに振って2つ目を通過
        judge.TryCut(1.3);
        Assert.AreEqual(pause, next.IsCut, pause ? "止まったあとの振りなら次のノーツを切れる" : "止まらずに振り続けた続きでは切らない");
    }

    [Test]
    public void ScoresAtTheClosestApproachNotAtTheExit()
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(-1f, 0f, 0f));
        tracker.Tick(new Vector3(-0.3f, 0f, 0f), 0.05f);
        bridge.OverrideBlade(new Vector3(-0.3f, -1f, 0f), new Vector3(-0.3f, 1f, 0f)); // ノーツから0.3(範囲0.5の中)
        Assert.AreEqual(0, judge.TryCut(0.98));
        tracker.Tick(Vector3.zero, 0.05f);
        bridge.OverrideBlade(new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f)); // 真上を通る
        Assert.AreEqual(0, judge.TryCut(1.0));
        tracker.Tick(new Vector3(0.4f, 0f, 0f), 0.05f);
        bridge.OverrideBlade(new Vector3(0.4f, -1f, 0f), new Vector3(0.4f, 1f, 0f)); // まだ範囲の中
        Assert.AreEqual(0, judge.TryCut(1.03));
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f);
        bridge.OverrideBlade(new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f)); // 抜けた
        Assert.AreEqual(1, judge.TryCut(1.06));
        Assert.True(note.IsCut);
        Assert.AreEqual(1.0, note.LastCutSongTime.Value, 1e-9, "採点は抜けた時刻ではなく、いちばん近づいた時刻");
    }

    [Test]
    public void SlowPassStillCountsWhenItsClosestApproachWasInsideTheWindow()
    {
        var (judge, tracker) = MakeRig();
        var root = new GameObject("SlowPassSpawner"); created.Add(root);
        var prefab = new GameObject("SlowPassPrefab"); created.Add(prefab);
        prefab.AddComponent<CuttableNote>(); prefab.SetActive(false);
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab; spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        CuttableNote note = null;
        spawner.OnNoteSpawned += n => { note = n; n.gameObject.SetActive(true); created.Add(n.gameObject); };
        spawner.SetChart(new ChartData { notes = new List<NoteData> {
            new NoteData { time = 1000, x = 0, y = 0, color = "red", type = "tap", count = 1 }
        } });
        spawner.Tick(1);
        double end = note.HitTime + spawner.judgeWindow;
        spawner.Tick(end - .01);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
        Assert.AreEqual(0, judge.TryCut(end - .01));
        spawner.Tick(end + .02); // 窓は閉じたが、Miss確定(猶予0.06秒)の前
        Assert.False(note.IsJudgeable);
        Assert.False(note.IsMissed);
        tracker.Tick(new Vector3(2, 0, 0), .05f);
        Assert.AreEqual(1, judge.TryCut(end + .02));
        Assert.True(note.IsCut, "近づいたのが窓の中なら、抜けるのが窓のあとでも切れる");
        Assert.AreEqual(end - .01, note.LastCutSongTime.Value, 1e-9);
    }

    // ------ 曲時計があるとき: 刃がノーツを通り過ぎたら切る(細い側で振るときは先端)・向きは刃の中心が動いた向き ------

    private (SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge) GameBladeRig()
    {
        var rig = MakeBladeRig();
        rig.judge.bladeRadius = .32f; rig.judge.noteHitRadiusXY = .88f; // 本編と同じ当たり(刃との距離 1.2)
        rig.judge.minCutSpeed = 2f;
        return rig;
    }

    // 刃(長さ 2*half、vertical なら縦向き)の中心を center へ動かして判定する。time が NaN なら曲時計なし。
    private static int Step(SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge,
        Vector2 center, double time, bool vertical = true, float half = .5f)
    {
        tracker.Tick(new Vector3(center.x, center.y, 0f), .02f);
        Vector3 c = new Vector3(center.x, center.y, 0f);
        Vector3 along = vertical ? new Vector3(0f, half, 0f) : new Vector3(half, 0f, 0f);
        bridge.OverrideBlade(c - along, c + along);
        return judge.TryCut(time);
    }

    [Test]
    public void VerticalBladeSwungAlongItselfIsCutWhenItsTipPassesTheNote()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Down;
        tracker.ResetTo(new Vector3(.3f, 2f, 0f));
        Assert.AreEqual(0, Step(judge, tracker, bridge, new Vector2(.3f, 1.4f), .94)); // 入る(刃の下端からノーツまで約0.95)
        Assert.AreEqual(0, Step(judge, tracker, bridge, new Vector2(.3f, .45f), .96)); // 刃の先がノーツの高さを越える(入ってから0.95しか動いていない)
        Assert.False(note.IsCut);
        Assert.AreEqual(1, Step(judge, tracker, bridge, new Vector2(.3f, .35f), .98)); // 振りが止まりかけ。刃の中心はまだノーツより上
        Assert.True(note.IsCut, "刃の先が越えていれば、刃の中心が届くまで振り切らなくても切れる");
        Assert.True(note.LastCutCorrectDirection);
        Assert.AreEqual(.94 + .02 * (.9 / .95), note.LastCutSongTime.Value, 1e-6,
            "採点は刃の先がノーツの中心の高さを通り過ぎた時刻(.94と.96の間)");
    }

    [Test]
    public void SlantedBladeIsScoredWhenItReachesTheNoteNotWhenItsCenterPasses()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(new Vector3(0f, .4f, 0f), 1.0);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        // 45°の刃(長さ約1.4)を右へ振る。ノーツは刃の上半分の高さ(.4)にある。
        SlantedStep(-1f, .96);
        SlantedStep(.2f, .98);
        Assert.True(note.IsCut);
        Assert.AreEqual(.97, note.LastCutSongTime.Value, 1e-6,
            "刃がノーツの上を通った時刻(.97)。刃の中心がノーツの横を過ぎた時刻(約.977)ではない");

        void SlantedStep(float x, double time)
        {
            tracker.Tick(new Vector3(x, 0f, 0f), .02f);
            bridge.OverrideBlade(new Vector3(x - .5f, -.5f, 0f), new Vector3(x + .5f, .5f, 0f));
            judge.TryCut(time);
        }
    }

    // 細い側で振る(刃が動く向きに沿う)と振りを横切る刃の幅がないので、そのときだけ当たりを半径1.7まで広げる。
    // カメラの値が届いていて、曲時計があるときだけ。腹で払う刃(幅1.0)は今のまま。
    [TestCase(true, true, true, true)]
    [TestCase(true, false, true, false)]
    [TestCase(true, true, false, false)]
    [TestCase(false, true, true, false)]
    public void ThinSideSwingReachesFurtherSidewaysOnlyWithTheCamera(bool vertical, bool camera, bool clock, bool cut)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        bridge.OverrideCameraInput(camera);
        var note = TimedNote(Vector3.zero, 1.0);
        float x = vertical ? 1.5f : 2f; // どちらも、振り下ろす刃からノーツまで横に1.5(今の当たり1.2の外)
        tracker.ResetTo(new Vector3(x, 3f, 0f));
        Step(judge, tracker, bridge, new Vector2(x, 2f), clock ? .94 : double.NaN, vertical);
        Step(judge, tracker, bridge, new Vector2(x, 1f), clock ? .96 : double.NaN, vertical);
        Step(judge, tracker, bridge, new Vector2(x, -.1f), clock ? .98 : double.NaN, vertical);
        Step(judge, tracker, bridge, new Vector2(x, -2f), clock ? 1.0 : double.NaN, vertical);
        Assert.AreEqual(cut, note.IsCut);
    }

    [Test]
    public void ThinSideIsNotWidenedWhileAnotherNoteIsNearby()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        bridge.OverrideCameraInput(true);
        var note = TimedNote(Vector3.zero, 1.0);
        var other = TimedNote(new Vector3(-1f, 0f, 0f), 1.25); // 時刻の違うノーツが2.9以内で受付中
        tracker.ResetTo(new Vector3(1.5f, 3f, 0f));
        Step(judge, tracker, bridge, new Vector2(1.5f, 2f), .94);
        Step(judge, tracker, bridge, new Vector2(1.5f, 1f), .96);
        Step(judge, tracker, bridge, new Vector2(1.5f, -.1f), .98);
        Assert.False(note.IsCut, "近くに別の時刻のノーツがあるときは広げない(巻き込みを増やさない)");
        Assert.False(other.IsCut);
    }

    [Test]
    public void PokeAndPullBackIsScoredWhenTheBladePassedInTheCutDirection()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(0f, -.6f, 0f));
        Step(judge, tracker, bridge, new Vector2(0f, -.5f), .96, false, 1f); // 円の中で入る
        Step(judge, tracker, bridge, new Vector2(0f, .3f), .98, false, 1f);  // 突き上げて刃がノーツを越える(0.8しか動かず、まだ切れない)
        Step(judge, tracker, bridge, new Vector2(0f, -.4f), 1.0, false, 1f); // 引き戻して、もう一度越える
        Assert.False(note.IsCut);
        Step(judge, tracker, bridge, new Vector2(0f, -1.4f), 1.02, false, 1f); // 円の外へ抜けて切れる
        Assert.True(note.IsCut);
        Assert.AreEqual(.9725, note.LastCutSongTime.Value, 1e-6, "切った向き(突き上げ)で刃が通り過ぎた時刻。引き戻した時刻ではない");
    }

    // 画面上の振りの向き(x, y)。カメラの値は縦が約1/2.4に縮んで届くので、カメラのときは縦を2.4倍して向きを比べる。
    [TestCase(CutDirection.Down, .819f, -.574f, true, true)]   // 画面では真下から55°。体の動きでは約31°
    [TestCase(CutDirection.Down, .819f, -.574f, false, false)] // マウスなど(カメラ以外)は補正しない
    [TestCase(CutDirection.Right, .766f, .643f, true, false)]  // 画面では水平から40°。体の動きでは約64°
    [TestCase(CutDirection.Right, .766f, .643f, false, true)]
    public void CameraSwingDirectionIsComparedWithTheVerticalMotionScaledBack(CutDirection required, float x, float y,
        bool camera, bool correct)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        bridge.OverrideCameraInput(camera);
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = required;
        Vector2 dir = new Vector2(x, y).normalized;
        tracker.ResetTo(-dir * 1.5f);
        Step(judge, tracker, bridge, -dir, .98, false);
        Step(judge, tracker, bridge, dir * .6f, 1.0, false);
        Assert.True(note.IsCut);
        Assert.AreEqual(correct, note.LastCutCorrectDirection);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FastSwingThatJumpsOverTheNoteBetweenTwoValuesStillCuts(bool clock)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(-3f, 0f, 0f));
        Step(judge, tracker, bridge, new Vector2(-2f, 0f), clock ? .98 : double.NaN); // 縦の刃。ノーツから2.0(当たり1.2の外)
        Step(judge, tracker, bridge, new Vector2(2f, 0f), clock ? 1.0 : double.NaN);  // 次の値では反対側の外
        Assert.AreEqual(clock, note.IsCut, clock ? "前の刃から今の刃までが通った面で拾う" : "曲時計のない画面は従来どおり");
        if (clock) Assert.AreEqual(.99, note.LastCutSongTime.Value, 1e-6, "刃がノーツを通り過ぎた時刻(前後の値の間)");
    }

    [Test]
    public void BladeThatJumpsFarAfterAMisdetectionDoesNotSweepThroughNotes()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(-4f, 0f, 0f));
        Step(judge, tracker, bridge, new Vector2(-3f, 0f), .98);
        Step(judge, tracker, bridge, new Vector2(3f, 0f), 1.0); // 中心が6動いた(maxCutDistance 5 を超える誤認識の飛び)
        Assert.False(note.IsCut);
        Assert.AreEqual(0, judge.PendingCount);
    }

    [Test]
    public void ScoredTimeStaysInsideTheJudgmentWindow()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(-3f, 0f, 0f));
        Step(judge, tracker, bridge, new Vector2(-2f, 0f), .68);
        Step(judge, tracker, bridge, new Vector2(2f, 0f), .70); // 通り過ぎたのは .69(早めの窓の外)
        Assert.True(note.IsCut);
        Assert.AreEqual(1.0 - JudgmentTierHelper.EarlyBadSeconds, note.LastCutSongTime.Value, 1e-9, "切れたのに MISS と採点しない");
    }

    [Test]
    public void WithoutTheSongClockTheBladeStillHasToLeaveTheHitRange()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = MakeNote(Vector3.zero);
        tracker.ResetTo(new Vector3(.3f, 2f, 0f));
        Step(judge, tracker, bridge, new Vector2(.3f, 1.4f), double.NaN);
        Step(judge, tracker, bridge, new Vector2(.3f, .2f), double.NaN);
        Step(judge, tracker, bridge, new Vector2(.3f, -.4f), double.NaN);
        Assert.False(note.IsCut, "タイトルなど曲時計のない画面は従来どおり、抜けたときに切る");
        Step(judge, tracker, bridge, new Vector2(.3f, -1.8f), double.NaN);
        Assert.True(note.IsCut);
    }

    [Test]
    public void WindUpThroughADownNoteDoesNotCutAndTheDownSwingIsScoredOnItsOwnTiming()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Down;
        tracker.ResetTo(new Vector3(0f, -1.5f, 0f));
        // 下から振り上げる(構え)。ノーツを通り過ぎても逆向きなので切れない。
        Step(judge, tracker, bridge, new Vector2(0f, -1f), .90, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, -.3f), .92, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, .4f), .94, false, 1f);
        Assert.False(note.IsCut, "振り上げ(逆向き)では切れない");
        Step(judge, tracker, bridge, new Vector2(0f, .9f), .96, false, 1f);
        // 円の中で振り返して振り下ろす。
        Step(judge, tracker, bridge, new Vector2(0f, .7f), .98, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, 0f), 1.0, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, -.6f), 1.02, false, 1f);
        Assert.True(note.IsCut, "振り下ろしで、円を抜ける前に切れる");
        Assert.True(note.LastCutCorrectDirection);
        Assert.AreEqual(1.0, note.LastCutSongTime.Value, 1e-9, "振り上げのときの時刻(0.92)では採点しない");
    }

    [Test]
    public void TurningBackInsideTheHitRangeStillCutsOnTheReturnSwing()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Down;
        tracker.ResetTo(new Vector3(0f, -.6f, 0f));
        // 小さく振り上げ(入ってから0.8しか動かない)、円の中で振り返して振り下ろす。
        Step(judge, tracker, bridge, new Vector2(0f, -.5f), .90, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, -.1f), .92, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, .3f), .94, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, .25f), .96, false, 1f); // 振り返し
        Assert.False(note.IsCut);
        Step(judge, tracker, bridge, new Vector2(0f, 0f), 1.0, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, -.8f), 1.02, false, 1f);
        Assert.True(note.IsCut, "振り返したところから測って、通り過ぎたら切れる");
        Assert.True(note.LastCutCorrectDirection, "向きは振り返したあとの動き(下)");
    }

    [Test]
    public void SmallShakesOnTheNoteDoNotCut()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        tracker.ResetTo(new Vector3(.4f, 0f, 0f));
        for (int i = 0; i < 20; i++)
            Step(judge, tracker, bridge, new Vector2(i % 2 == 0 ? -.4f : .4f, 0f), .9 + i * .01);
        Assert.False(note.IsCut, "刃を止めたときの揺れ(1.0未満の動き)では切らない");
        Assert.AreEqual(1, judge.PendingCount);
    }

    [TestCase(40f, true, true)]
    [TestCase(50f, true, false)]
    [TestCase(40f, false, false)]
    public void DirectionToleranceIs45DegreesOnlyWithTheSongClock(float degrees, bool clock, bool correct)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = MakeNote(Vector3.zero);
        note.HitTime = 1.0;
        note.RequiredDirection = CutDirection.Down;
        float rad = degrees * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad)); // 真下から degrees だけ傾けた向き
        tracker.ResetTo(-dir * 1.5f);
        Step(judge, tracker, bridge, -dir, clock ? .98 : double.NaN, false);
        Step(judge, tracker, bridge, dir * .6f, clock ? 1.0 : double.NaN, false);
        if (!clock) Step(judge, tracker, bridge, dir * 2.6f, double.NaN, false); // 時計がなければ抜けたときに切る
        Assert.True(note.IsCut);
        Assert.AreEqual(correct, note.LastCutCorrectDirection, clock ? "曲時計があるときは ±45°" : "時計がないときは従来の ±30°");
    }

    [Test]
    public void LongNoteIsCutOncePerSwingWhenPassing()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredCutCount = note.RemainingCuts = 3;
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        Step(judge, tracker, bridge, new Vector2(-1f, 0f), 1.0);
        Step(judge, tracker, bridge, new Vector2(.5f, 0f), 1.02);  // 通り過ぎる → 1回
        Step(judge, tracker, bridge, new Vector2(1f, 0f), 1.04);   // 同じ振りのまま、まだ円の中
        Step(judge, tracker, bridge, new Vector2(2.5f, 0f), 1.06); // 抜ける
        Assert.AreEqual(1, note.CutsAchieved, "同じ振りでは1回だけ");
        Step(judge, tracker, bridge, new Vector2(.5f, 0f), 1.10);  // 振り返して入る
        Step(judge, tracker, bridge, new Vector2(-1f, 0f), 1.12);  // 通り過ぎる → 2回目
        Assert.AreEqual(2, note.CutsAchieved);
    }

    [Test]
    public void GameUsesTheEnlargedHitRangeInCodeAndScene()
    {
        var go = new GameObject("HitRangeDefaults"); created.Add(go);
        var manager = go.AddComponent<GamePlayManager>(); manager.enabled = false;
        float codeRange = manager.saberBladeRadiusV2 + manager.saberNoteHitRadiusXYV2;
        Assert.AreEqual(1.20f, codeRange, .001f, "刃との距離 0.92 の 1.3 倍");
        string scene = System.IO.File.ReadAllText("Assets/Scenes/Game.unity");
        float Read(string key) => float.Parse(System.Text.RegularExpressions.Regex.Match(scene,
            @"\n\s*" + key + @": ([0-9.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(codeRange, Read("saberBladeRadiusV2") + Read("saberNoteHitRadiusXYV2"), .001f,
            "シーンに古い値が残っていない(シーンの値がコードの既定値より優先される)");
    }

    [TestCase(false, "inactive")] [TestCase(true, "inactive")]
    [TestCase(false, "disabled")] [TestCase(true, "disabled")]
    [TestCase(false, "trackingReset")] [TestCase(true, "trackingReset")]
    [TestCase(false, "trackingResetThenSample")] [TestCase(true, "trackingResetThenSample")]
    public void PendingCut_InterruptedContactDoesNotFireLater(bool bladeMode, string interruption)
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        if (!bladeMode) judge.bladeProvider = null;
        var note = MakeNote(Vector3.zero);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
        if (bladeMode) bridge.OverrideBlade(Vector3.left, Vector3.right);
        Assert.AreEqual(0, judge.TryCut());
        Assert.AreEqual(1, judge.PendingCount);
        if (interruption == "inactive") note.gameObject.SetActive(false);
        if (interruption == "disabled")
        {
            judge.enabled = false;
            // EditModeではライフサイクルを明示的に呼ぶ。
            typeof(SaberCutJudge).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.Invoke(judge, null);
            judge.enabled = true;
        }
        tracker.Tick(new Vector3(2, 0, 0), .05f);
        // 再開後の新しい通過軌跡を作らず、古い保留だけの有無を調べる。
        tracker.Tick(new Vector3(2, 0, 0), .05f);
        if (bladeMode) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
        if (interruption.StartsWith("trackingReset"))
        {
            tracker.ResetTo(new Vector3(2, 0, 0));
            if (interruption == "trackingResetThenSample") tracker.Tick(new Vector3(2, 0, 0), .05f);
        }
        Assert.AreEqual(0, judge.TryCut());
        Assert.False(note.IsCut);
        Assert.AreEqual(0, judge.PendingCount);
        // 有効な新しい進入・退出は、中断後も通常どおり受け付ける。
        note.gameObject.SetActive(true);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .05f);
        if (bladeMode) bridge.OverrideBlade(Vector3.left, Vector3.right);
        Assert.AreEqual(0, judge.TryCut());
        tracker.Tick(new Vector3(2, 0, 0), .05f);
        if (bladeMode) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
        Assert.AreEqual(1, judge.TryCut());
        Assert.True(note.IsCut);
    }
}
