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

    // 刃が消えて点の判定に切り替わったとき(カメラの受信が途切れて HideBlade)、触れていたノーツの保留を持ち越さない。
    // 持ち越すと、止まっている点(古い刃の中心)とノーツの距離で「抜けた」と数え、振っていないのに切れていた(2026-10-22)。
    [Test]
    public void Blade_LosingTheBladeMidContactDoesNotCut()
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        var note = MakeNote(new Vector3(0f, 0f, 0f));
        tracker.ResetTo(new Vector3(0f, 0f, 0f));
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f); // Speed=20
        bridge.OverrideBlade(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f));
        Assert.AreEqual(0, judge.TryCut(), "刃がノーツの上にある間は保留");
        Assert.Greater(judge.PendingCount, 0);
        judge.bladeProvider = null;                    // 刃が消えた(点の判定へ)
        tracker.Tick(new Vector3(1f, 0f, 0f), 0.05f);  // 位置は止まったまま(速さ0)
        Assert.AreEqual(0, judge.TryCut());
        Assert.IsFalse(note.IsCut, "振っていないのに切らない");
        Assert.AreEqual(0, judge.PendingCount);
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

    [TestCase(.019, -.100, true)]
    [TestCase(.020, -.100, false)]
    [TestCase(.021, -.100, false)]
    [TestCase(.200, -.050001, false)]
    [TestCase(.200, -.050, true)]
    [TestCase(.200, .100, true)]
    public void FollowingNoteWaitsForItsPerfectZone(double gap, double error, bool allowed)
    {
        var first = TimedNote(Vector3.up * 10, 1.0);
        var next = TimedNote(Vector3.zero, 1.0 + gap);
        first.RequiredHand = next.RequiredHand = SaberHand.Right;
        Assert.AreEqual(allowed, next.CutAtSongTime(Vector3.zero, Vector3.right * 10,
            CutDirection.None, SaberHand.Right, next.HitTime + error));
        Assert.AreEqual(allowed, next.IsCut);
        Assert.False(first.IsCut);
    }

    [TestCase(SaberHand.Left, SaberHand.Right, SaberHand.Any, true)]
    [TestCase(SaberHand.Right, SaberHand.Right, SaberHand.Any, false)]
    [TestCase(SaberHand.Any, SaberHand.Right, SaberHand.Right, false)]
    [TestCase(SaberHand.Left, SaberHand.Any, SaberHand.Right, true)]
    [TestCase(SaberHand.Right, SaberHand.Any, SaberHand.Right, false)]
    public void CutOrderKeepsHandsIndependentIncludingMouseAndGold(SaberHand previousHand,
        SaberHand nextHand, SaberHand cutter, bool allowed)
    {
        var first = TimedNote(Vector3.up * 10, 1);
        var next = TimedNote(Vector3.zero, 1.2);
        first.RequiredHand = previousHand; next.RequiredHand = nextHand;
        Assert.AreEqual(allowed, next.CanBeginCutAt(1.1, cutter));
    }

    [TestCase(1)] [TestCase(3)]
    public void FirstAcceptedCutReleasesFollowingNoteIncludingLong(int count)
    {
        var first = TimedNote(Vector3.up * 10, 1);
        var next = TimedNote(Vector3.zero, 1.2);
        first.RequiredCutCount = first.RemainingCuts = count;
        Assert.False(next.CanBeginCutAt(1.1, SaberHand.Right));
        first.Cut(Vector3.up * 10, Vector3.right * 10);
        Assert.True(next.CutAtSongTime(Vector3.zero, Vector3.right * 10,
            CutDirection.None, SaberHand.Right, 1.1));
    }

    [Test]
    public void MissedOrInactiveNotesDoNotKeepTheFollowingNoteLocked()
    {
        var first = TimedNote(Vector3.up * 10, 1);
        var next = TimedNote(Vector3.zero, 1.2);
        first.gameObject.SetActive(false);
        Assert.True(next.CanBeginCutAt(1.1, SaberHand.Right));
        first.gameObject.SetActive(true);
        Assert.False(next.CanBeginCutAt(1.1, SaberHand.Right));
        first.MarkMiss();
        Assert.True(next.CanBeginCutAt(1.1, SaberHand.Right));
    }

    [TestCase(false)] [TestCase(true)]
    public void ProtectedContactAndOldSweptPathAreNotSavedUntilUnlock(bool blade)
    {
        var (judge, tracker, bridge) = MakeBladeRig();
        if (!blade) judge.bladeProvider = null;
        TimedNote(Vector3.up * 10, 1);
        var next = TimedNote(Vector3.zero, 1.2);
        tracker.ResetTo(Vector3.left * 2);
        tracker.Tick(new Vector3(-.2f, 0, 0), .02f);
        bridge.OverrideBlade(new Vector3(-.2f, -1, 0), new Vector3(-.2f, 1, 0));
        judge.TryCut(1.14);
        Assert.AreEqual(0, judge.PendingCount, "保護中の接触は保存しない");
        tracker.Tick(Vector3.right * 2, .02f);
        bridge.OverrideBlade(new Vector3(2, -1, 0), new Vector3(2, 1, 0));
        judge.TryCut(1.16);
        Assert.False(next.IsCut, "Perfectに入ったとき既に通り過ぎていた古い軌跡は使わない");
        tracker.Tick(Vector3.left * 2, .04f);
        bridge.OverrideBlade(new Vector3(-2, -1, 0), new Vector3(-2, 1, 0));
        judge.TryCut(1.2);
        Assert.True(next.IsCut, "解放後の新しい振りでは切れる");
    }

    [Test]
    public void NotesFromAnotherSpawnerDoNotBlockGameplay()
    {
        TimedNote(Vector3.up * 10, 1);
        var (_, _, _, _, _, notes) = SweepRig(SweepNote(1200, 0));
        Assert.True(notes[0].CanBeginCutAt(1.1, SaberHand.Right));
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
            tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.05f); judge.TryCut(1.05);
            tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.05f); judge.TryCut(1.1);
        }
        // 同じ向きに振って2つ目を通過。2つ目の時刻より0.18秒早く、1つ目からの間も譜面の間隔の半分(0.15秒)に届かない。
        tracker.Tick(new Vector3(5f, 0f, 0f), 0.02f);
        judge.TryCut(1.12);
        Assert.AreEqual(pause, next.IsCut, pause ? "止まったあとの振りなら次のノーツを切れる"
            : "止まらずに振り続けた勢いで早く届いたノーツは切らない(巻き込み)");
    }

    // ------ なぞり: 隣のマスへ順番に来るノーツを、その間隔どおりに1回の振りでなぞると続けて切れる(曲時計があるときだけ) ------

    // 本編と同じ当たりの刃と、譜面どおりに受付窓を開け閉めするノーツ(NoteSpawner)と採点(ScoreManager)。
    private (SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge, NoteSpawner spawner, ScoreManager score,
        List<CuttableNote> notes) SweepRig(params NoteData[] chart)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var root = new GameObject("SweepSpawner"); created.Add(root);
        var prefab = new GameObject("SweepPrefab"); created.Add(prefab);
        prefab.AddComponent<CuttableNote>(); prefab.SetActive(false);
        var spawner = root.AddComponent<NoteSpawner>();
        spawner.notePrefab = prefab; spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        var score = root.AddComponent<ScoreManager>(); score.Bind(spawner);
        var notes = new List<CuttableNote>();
        spawner.OnNoteSpawned += n => { n.gameObject.SetActive(true); created.Add(n.gameObject); notes.Add(n); };
        spawner.SetChart(new ChartData { notes = new List<NoteData>(chart) });
        spawner.Tick(.55); // 先読み(1秒)の境界ちょうどに置かない
        return (judge, tracker, bridge, spawner, score, notes);
    }

    private static NoteData SweepNote(int ms, float x, float y = 0f, string direction = "none")
        => new NoteData { time = ms, x = x, y = y, color = "red", type = direction == "none" ? "tap" : "direction",
            direction = direction, count = 1 };

    // 縦の刃を、本編と同じ順(受付窓の更新 → 刃の判定 → ノーツの更新)で0.02秒ずつ動かす。
    private static void SweepStep(SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge, NoteSpawner spawner,
        Vector2 center, double time)
    {
        spawner.RefreshJudgmentWindows(time);
        Step(judge, tracker, bridge, center, time);
        spawner.Tick(time);
    }

    // 4つのノーツ(横に1ずつ・0.2秒おき)を、刃を秒速5で右へ動かしてなぞる。lateBy だけ遅れてなぞる。
    private static void SweepRight(SaberCutJudge judge, SaberTracker tracker, SaberInputBridge bridge, NoteSpawner spawner,
        double lateBy, double from = .7, double to = 2.0)
    {
        tracker.ResetTo(new Vector3(-1.5f + 5f * (float)(from - .02 - 1.0 - lateBy), 0f, 0f));
        for (int i = 0; ; i++)
        {
            double t = from + i * .02;
            if (t > to + 1e-9) break;
            SweepStep(judge, tracker, bridge, spawner, new Vector2(-1.5f + 5f * (float)(t - 1.0 - lateBy), 0f), t);
        }
    }

    private static NoteData[] FourInARow(string thirdDirection = "none") => new[]
    {
        SweepNote(1000, -1.5f), SweepNote(1200, -.5f), SweepNote(1400, .5f, 0f, thirdDirection), SweepNote(1600, 1.5f)
    };

    [Test]
    public void OneSwingSweepsFourNotesInAdjacentCellsWhenEachIsReachedOnTime()
    {
        var (judge, tracker, bridge, spawner, score, notes) = SweepRig(FourInARow());
        SweepRight(judge, tracker, bridge, spawner, 0);
        Assert.AreEqual(4, notes.Count);
        Assert.True(notes.TrueForAll(n => n.IsCut), "隣の4マスを順番どおりになぞれば、1回の振りで4つとも切れる");
        Assert.AreEqual(4, score.HitCount);
        Assert.AreEqual(0, score.MissCount);
        Assert.AreEqual(4, score.PerfectCount, "それぞれ刃が届いた時刻で採点する");
        foreach (var n in notes) Assert.AreEqual(n.HitTime, n.LastCutSongTime.Value, .011);
    }

    [Test]
    public void LateSweepStillCutsTheFirstNote()
    {
        // 間隔0.2秒の半分(0.1秒)より遅れてなぞる。まだ刃の届かない2つ目と比べて1つ目を見送らない。
        var (judge, tracker, bridge, spawner, score, notes) = SweepRig(FourInARow());
        SweepRight(judge, tracker, bridge, spawner, .12);
        Assert.True(notes.TrueForAll(n => n.IsCut));
        Assert.AreEqual(0, score.MissCount);
        Assert.AreEqual(4, score.GreatCount, "4つとも0.12秒遅れ");
    }

    [Test]
    public void SweepDoesNotCutANoteWhoseArrowPointsElsewhere()
    {
        var (judge, tracker, bridge, spawner, score, notes) = SweepRig(FourInARow("down"));
        SweepRight(judge, tracker, bridge, spawner, 0);
        Assert.True(notes[0].IsCut && notes[1].IsCut && notes[3].IsCut, "矢印の合うノーツはなぞりで切れる");
        Assert.False(notes[2].IsCut, "下向きの矢印は右へのなぞりでは切らない");
        Assert.True(notes[2].IsMissed);
        Assert.AreEqual(3, score.PerfectCount);
        Assert.AreEqual(1, score.MissCount);
    }

    [Test]
    public void FastFollowThroughDoesNotCutTheNextNoteButTheReturnSwingDoes()
    {
        // 0.25秒あとのノーツが隣にある。速い振り(秒速25)の勢いで0.04秒後に届いても切らず、振り返しで自分の時刻に切る。
        var (judge, tracker, bridge, spawner, score, notes) = SweepRig(SweepNote(1000, -.5f), SweepNote(1250, .5f));
        tracker.ResetTo(new Vector3(-3f, 0f, 0f));
        for (int i = 0; i <= 10; i++) SweepStep(judge, tracker, bridge, spawner, new Vector2(-2.5f + .5f * i, 0f), .96 + i * .02);
        Assert.True(notes[0].IsCut);
        Assert.False(notes[1].IsCut, "振り抜けの勢いで、譜面の間隔の半分より早く届いた後ろのノーツは切らない");
        for (int i = 1; i <= 7; i++) SweepStep(judge, tracker, bridge, spawner, new Vector2(2.5f - .5f * i, 0f), 1.16 + i * .02);
        Assert.True(notes[1].IsCut, "振り返した次の振りなら切れる");
        Assert.AreEqual(2, score.PerfectCount, "どちらも刃が自分の時刻に届いた振りで採点する");
        Assert.AreEqual(0, score.MissCount);
    }

    [Test]
    public void SweepingIsOffWithoutTheSongClock()
    {
        var (judge, tracker) = MakeRig();
        var first = TimedNote(Vector3.zero, 1.0);
        var next = TimedNote(new Vector3(3f, 0f, 0f), 1.3);
        tracker.ResetTo(new Vector3(-2f, 0f, 0f));
        tracker.Tick(new Vector3(1.5f, 0f, 0f), 0.1f);
        judge.TryCut();
        tracker.Tick(new Vector3(5f, 0f, 0f), 0.1f);
        judge.TryCut();
        Assert.True(first.IsCut);
        Assert.False(next.IsCut, "曲時計のない画面(タイトル・選曲)は従来どおり、1回の振りで1つの時刻だけ");
    }

    [Test]
    public void SweepingCanBeTurnedOff()
    {
        var (judge, tracker, bridge, spawner, score, notes) = SweepRig(FourInARow());
        judge.sweepEnabled = false;
        SweepRight(judge, tracker, bridge, spawner, 0);
        Assert.AreEqual(1, notes.FindAll(n => n.IsCut).Count, "sweepEnabled=false なら従来どおり1回の振りで1つだけ");
        Assert.AreEqual(3, score.MissCount);
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
    // 向きが合わない振りでは切らない(2026-10-22〜、幅は±70°)。
    [TestCase(CutDirection.Down, .966f, -.259f, true, true)]   // 画面では真下から75°。体の動きでは約57°
    [TestCase(CutDirection.Down, .966f, -.259f, false, false)] // マウスなど(カメラ以外)は補正しない
    [TestCase(CutDirection.Right, .574f, .819f, true, false)]  // 画面では水平から55°。体の動きでは約74°
    [TestCase(CutDirection.Right, .574f, .819f, false, true)]
    public void CameraSwingDirectionIsComparedWithTheVerticalMotionScaledBack(CutDirection required, float x, float y,
        bool camera, bool cut)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        bridge.OverrideCameraInput(camera);
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = required;
        Vector2 dir = new Vector2(x, y).normalized;
        tracker.ResetTo(-dir * 1.5f);
        Step(judge, tracker, bridge, -dir, .98, false);
        Step(judge, tracker, bridge, dir * .6f, 1.0, false);
        Assert.AreEqual(cut, note.IsCut);
        Assert.False(note.IsMissed);
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

    // 2.0 秒は、窓の端に収めた誤差が丸めで -0.10000000000000009 秒になる(以前は切れたのに MISS)。
    [TestCase(1.0)]
    [TestCase(2.0)]
    public void ScoredTimeStaysInsideTheJudgmentWindow(double hit)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, hit);
        tracker.ResetTo(new Vector3(-3f, 0f, 0f));
        Step(judge, tracker, bridge, new Vector2(-2f, 0f), hit - .32);
        Step(judge, tracker, bridge, new Vector2(2f, 0f), hit - .30); // 通り過ぎたのは 0.31 秒前(早めの窓の外)
        Assert.True(note.IsCut);
        Assert.AreEqual(hit - JudgmentTierHelper.EarlyBadSeconds, note.LastCutSongTime.Value, 1e-9, "切れたのに MISS と採点しない");
        Assert.AreEqual(JudgmentTier.Good, JudgmentTierHelper.Classify(note.LastCutSongTime.Value - note.HitTime),
            "窓の入口で切れた早めの振りは GOOD(コンボを切らない)");
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

    // 標準の±70°を残し、Easy の±80°と直角・逆向きの拒否も確かめる。
    [TestCase("Normal", 65f, true, true)]
    [TestCase("Normal", 75f, true, false)]
    [TestCase("Normal", 115f, true, false)]
    [TestCase("Normal", 25f, false, true)]
    [TestCase("Normal", 40f, false, false)]
    [TestCase("Hard", 75f, true, false)]
    [TestCase("Easy", 75f, true, true)]
    [TestCase("Easy", 81f, true, false)]
    [TestCase("Easy", 90f, true, false)]
    [TestCase("Easy", 180f, true, false)]
    [TestCase("Easy", 40f, false, false)]
    public void DirectionToleranceFollowsTheSongDifficulty(string difficulty, float degrees, bool clock, bool cut)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        GameDifficultyRules.ConfigureJudges(difficulty, true, judge, null, null);
        var note = MakeNote(Vector3.zero);
        note.HitTime = 1.0;
        note.RequiredDirection = CutDirection.Down;
        float rad = degrees * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad)); // 真下から degrees だけ傾けた向き
        tracker.ResetTo(-dir * 1.5f);
        Step(judge, tracker, bridge, -dir, clock ? .98 : double.NaN, false);
        Step(judge, tracker, bridge, dir * .6f, clock ? 1.0 : double.NaN, false);
        if (!clock) Step(judge, tracker, bridge, dir * 2.6f, double.NaN, false); // 時計がなければ抜けたときに切る
        Assert.AreEqual(cut, note.IsCut, clock ? difficulty : "時計がないときは従来の ±30°");
        Assert.False(note.IsMissed, "向きの合わない振りでもミスにはしない");
    }

    // 上向きの矢印を、横から入って斜め上へ切り上げる(2026-10-22 ユーザー)。入ったところからの動きは横寄りでも、
    // ノーツを通り過ぎたときの動きが斜め上なら切れる。y は体の動き(カメラのときは縦を約1/2.4に縮めて届ける)。
    [TestCase(-.6f, -.6f, 45f, false)]
    [TestCase(-.6f, -.6f, 45f, true)]
    [TestCase(-.6f, -.6f, 35f, false)]
    [TestCase(-.6f, -.6f, 35f, true)]
    [TestCase(-.4f, -.2f, 50f, false)]
    [TestCase(-.4f, -.2f, 50f, true)]
    public void UpArrowCutDiagonallyAfterEnteringFromTheSide(float approachY, float turnX, float angle, bool camera)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        bridge.OverrideCameraInput(camera);
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Up;
        float squash = camera ? 1f / 2.4f : 1f;
        Vector2 at = new Vector2(-3f, approachY);
        tracker.ResetTo(new Vector3(at.x, at.y * squash, 0f));
        double t = .86;
        // 横から近づく(ノーツの少し下の高さを右へ)
        while (at.x < turnX - .01f) { at.x = Mathf.Min(turnX, at.x + .6f); Step(judge, tracker, bridge, new Vector2(at.x, at.y * squash), t += .02, true, .8f); }
        Assert.False(note.IsCut, "横に近づいただけでは切れない");
        // 斜め上へ切り上げる
        Vector2 up = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * .5f;
        for (int i = 0; i < 8 && !note.IsCut; i++) { at += up; Step(judge, tracker, bridge, new Vector2(at.x, at.y * squash), t += .02, true, .8f); }
        Assert.True(note.IsCut, "ノーツを通り過ぎたときの動きは斜め上");
    }

    // 通り過ぎたときの1コマの動きで救うのは、全体の動きから60°以内・矢印から100°以内のときだけ(10/23 点検)。
    // 斜めの矢印を真逆に振った振りは、1コマが矢印寄りにぶれても切れない。「横から入って斜め上」は今までどおり切れる。
    [TestCase(CutDirection.UpRight, 225f, 136f, 2.4f, false)] // カメラ: 右上の矢印を左下へ真逆に。通過の1コマだけ左上(全体から89°)
    [TestCase(CutDirection.Up, 200f, 155f, 1f, false)]        // マウス: 矢印から110°の振り。1コマは矢印から65°
    [TestCase(CutDirection.Up, 0f, 75f, 1f, false)]           // 1コマだけ全体から75°曲がった値では救わない
    [TestCase(CutDirection.Up, 0f, 45f, 2.4f, true)]          // カメラ: 横から入って斜め上
    [TestCase(CutDirection.Up, 0f, 45f, 1f, true)]            // マウスでも同じ
    [TestCase(CutDirection.Up, 15f, 70f, 1f, true)]           // 急な切り上げ(全体から55°)も救う
    public void CrossingRescuesOnlyNearMisses(CutDirection arrow, float travelDeg, float crossingDeg, float verticalScale, bool cut)
    {
        Vector2 Dir(float deg) => new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
        Vector2 judged = SaberCutJudge.JudgedCutDirection(CutDirectionHelper.ToVector(arrow), Dir(travelDeg),
            1.0, Dir(crossingDeg), double.NaN, Vector2.zero, verticalScale);
        Assert.AreEqual(cut, CutDirectionHelper.Matches(arrow, judged,
            Mathf.Cos(SaberCutJudge.DefaultDirectionToleranceDegrees * Mathf.Deg2Rad)));
    }

    // 横に払う振りは、上向きの矢印を切らない(通り過ぎたときの動きも横)。
    [TestCase(false, "Normal")]
    [TestCase(true, "Normal")]
    [TestCase(false, "Easy")]
    [TestCase(true, "Easy")]
    public void UpArrowIsNotCutByASidewaysSwing(bool camera, string difficulty)
    {
        var (judge, tracker, bridge) = GameBladeRig();
        GameDifficultyRules.ConfigureJudges(difficulty, true, judge, null, null);
        bridge.OverrideCameraInput(camera);
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Up;
        tracker.ResetTo(new Vector3(-3f, -.1f, 0f));
        double t = .9;
        for (float x = -2.4f; x <= 3f; x += .6f) Step(judge, tracker, bridge, new Vector2(x, -.1f + x * .05f), t += .02, true, .8f);
        Assert.False(note.IsCut);
    }

    // 向きの合わない振りでは切れないが、ノーツは残り、構え直して向きの合う次の振りで切れる。採点はその振りの時刻。
    [Test]
    public void WrongWaySwingLeavesTheNoteForTheNextSwing()
    {
        var (judge, tracker, bridge) = GameBladeRig();
        var note = TimedNote(Vector3.zero, 1.0);
        note.RequiredDirection = CutDirection.Down;
        tracker.ResetTo(new Vector3(-1.5f, 0f, 0f));
        // 右へ払う(下向きの矢印に90°)。通り過ぎても切れない。
        Step(judge, tracker, bridge, new Vector2(-1f, 0f), .90, false, 1f);
        Step(judge, tracker, bridge, new Vector2(.6f, 0f), .92, false, 1f);
        Step(judge, tracker, bridge, new Vector2(1.6f, 0f), .94, false, 1f);
        Assert.False(note.IsCut, "横に払っても切れない");
        Assert.False(note.IsMissed);
        // 上へ構え直して、振り下ろす。
        Step(judge, tracker, bridge, new Vector2(0f, 1.6f), .96, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, 1f), .98, false, 1f);
        Step(judge, tracker, bridge, new Vector2(0f, -.6f), 1.0, false, 1f);
        Assert.True(note.IsCut, "向きの合う振りで切れる");
        Assert.AreEqual(.98 + .02 * (1.0 / 1.6), note.LastCutSongTime.Value, 1e-6, "採点は振り下ろしでノーツを通り過ぎた時刻");
    }

    // 抜けて切ったロングに、同じ振りのまま(振り返さず・止まらず)曲げて入り直しても、2回目は数えない(通り過ぎて切るときと同じ)。
    [Test]
    public void LongNoteExitCutIsAlsoOncePerSwing()
    {
        var (j, t, b) = GameBladeRig();
        var n = TimedNote(Vector3.zero, 1);
        n.RequiredCutCount = n.RemainingCuts = 3;
        Step(j, t, b, new Vector2(-1, 1.69f), .98);
        Step(j, t, b, new Vector2(.2f, 1.69f), 1);
        Assert.AreEqual(1, n.CutsAchieved);
        // 反転せず曲げて入り直す。
        Step(j, t, b, new Vector2(.21f, 1.65f), 1.02);
        Step(j, t, b, new Vector2(.22f, -1.7f), 1.04);
        Assert.AreEqual(1, n.CutsAchieved);
    }

    // 窓の終わり(1.18秒)までに刃が通り過ぎていれば、そのフレームが窓のあとでも BAD で切れる。窓のあとに通り過ぎたら MISS。
    [TestCase(1.16, 1)]
    [TestCase(1.18, 0)]
    public void CrossingBeforeTheLateEdgeCountsEvenIfTheFrameEndsAfterIt(double from, int hits)
    {
        var (j, t, b, s, score, _) = SweepRig(SweepNote(1000, 0));
        SweepStep(j, t, b, s, new Vector2(-1.3f, 0), from);
        SweepStep(j, t, b, s, new Vector2(1.3f, 0), from + .04);
        s.Tick(2);
        Assert.AreEqual(hits, score.BadCount);
        Assert.AreEqual(1 - hits, score.MissCount);
        Assert.AreEqual(1, score.JudgedNoteCount);
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
