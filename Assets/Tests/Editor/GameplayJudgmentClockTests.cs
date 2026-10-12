using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// 現在の曲時計に対して、ゲーム本体のフレーム順序で判定窓を確認する。
public class GameplayJudgmentClockTests
{
    GameObject root;
    SongPlayer song;
    NoteSpawner spawner;
    SaberTracker tracker;
    SaberInputBridge bridge;
    SaberCutJudge judge;
    GamePlayManager manager;
    CuttableNote note;
    const BindingFlags Fields = BindingFlags.NonPublic | BindingFlags.Instance;

    void Create(bool blade, bool secondJudge, int count)
    {
        root = new GameObject("JudgmentClockTest");
        song = root.AddComponent<SongPlayer>();
        spawner = root.AddComponent<NoteSpawner>();
        spawner.buildTimingCues = false;
        spawner.simultaneousGuideEnabled = false;
        var prefab = new GameObject("ClockNotePrefab"); prefab.transform.SetParent(root.transform);
        prefab.AddComponent<CuttableNote>(); prefab.SetActive(false);
        spawner.notePrefab = prefab; spawner.noteRoot = root.transform;
        spawner.OnNoteSpawned += n => { note = n; n.gameObject.SetActive(true); };
        spawner.SetChart(new ChartData { notes = new List<NoteData> {
            new NoteData { time = 1000, x = 0, y = 0, color = "red", count = count, type = count > 1 ? "long" : "tap" }
        } });
        var saber = new GameObject("ClockSaber"); saber.transform.SetParent(root.transform);
        tracker = saber.AddComponent<SaberTracker>(); tracker.enabled = false;
        bridge = saber.AddComponent<SaberInputBridge>(); bridge.useBladeMode = false; bridge.enabled = false;
        judge = saber.AddComponent<SaberCutJudge>(); judge.autonomous = false;
        judge.saber = tracker; judge.bladeProvider = blade ? bridge : null;
        judge.maxCutDistance = 5;
        manager = root.AddComponent<GamePlayManager>(); manager.enabled = false;
        manager.songPlayer = song; manager.noteSpawner = spawner;
        if (secondJudge) Set(manager, "cutJudge2", judge); else manager.cutJudge = judge;
        Set(manager, "ready", true);
    }

    [TearDown]
    public void Cleanup()
    {
        if (song != null) song.Stop();
        if (root != null) Object.DestroyImmediate(root);
    }

    [TestCase(false, false, false)] [TestCase(false, true, false)]
    [TestCase(true, false, false)] [TestCase(true, true, false)]
    [TestCase(false, false, true)] [TestCase(true, false, true)]
    public void NewlyOpenedWindowAcceptsCutOnThisFrame(bool blade, bool secondJudge, bool calibration)
    {
        Create(blade, secondJudge, 1);
        Set(manager, "inCalibration", calibration);
        spawner.Tick(.76); // 早めの窓(0.2025秒)の外
        Assert.False(note.IsJudgeable);
        tracker.ResetTo(new Vector3(-2, 0, 0));
        tracker.Tick(blade ? Vector3.zero : new Vector3(2, 0, 0), .033f);
        if (blade) bridge.OverrideBlade(Vector3.left, Vector3.right);
        TickManager(.94);
        if (blade)
        {
            tracker.Tick(new Vector3(2, 0, 0), .033f);
            bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
            TickManager(.97);
        }
        Assert.True(note.IsCut, "今の曲時計では判定窓が開いている。前フレームのfalseを使わない");
    }

    [TestCase(false, false, 1, false)] [TestCase(false, true, 1, false)]
    [TestCase(true, false, 1, false)] [TestCase(true, true, 1, false)]
    [TestCase(false, false, 3, false)] [TestCase(false, true, 3, false)]
    [TestCase(true, false, 3, false)] [TestCase(true, true, 3, false)]
    [TestCase(false, false, 1, true)] [TestCase(true, false, 3, true)]
    public void ClosedWindowCannotFinishPendingTapOrLong(bool blade, bool secondJudge, int count, bool calibration)
    {
        Create(blade, secondJudge, count);
        Set(manager, "inCalibration", calibration);
        double end = 1 + spawner.judgeWindow + (count - 1) * spawner.secondsPerLongCut;
        spawner.Tick(end - .03);
        for (int i = 1; i < count; i++) note.Cut(Vector3.zero, Vector3.right * 10);
        Assert.True(note.IsJudgeable);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .033f);
        if (blade) bridge.OverrideBlade(Vector3.left, Vector3.right);
        Assert.AreEqual(0, judge.TryCut());
        Assert.AreEqual(1, judge.PendingCount);
        tracker.Tick(new Vector3(2, 0, 0), .033f);
        if (blade) bridge.OverrideBlade(new Vector3(2, 0, 0), new Vector3(4, 0, 0));
        TickManager(end + .03);
        Assert.False(note.IsCut, "窓終了後は前フレームのtrueで完了させない");
        Assert.AreEqual(count - 1, note.CutsAchieved);
        Assert.False(note.IsJudgeable);
        Assert.AreEqual(0, judge.PendingCount);
    }

    [Test]
    public void GameLoopScoresAtTheClosestApproachNotAtTheExit()
    {
        Create(true, false, 1);
        spawner.Tick(.95);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .033f);
        bridge.OverrideBlade(Vector3.left, Vector3.right); // ノーツの真上
        TickManager(1.0);
        tracker.Tick(new Vector3(.3f, 0, 0), .033f);
        bridge.OverrideBlade(new Vector3(.3f, -1, 0), new Vector3(.3f, 1, 0)); // まだ範囲の中
        TickManager(1.05);
        Assert.False(note.IsCut);
        tracker.Tick(new Vector3(3, 0, 0), .033f);
        bridge.OverrideBlade(new Vector3(3, 0, 0), new Vector3(5, 0, 0)); // 抜けた
        TickManager(1.12);
        Assert.True(note.IsCut);
        Assert.AreEqual(1.0, note.LastCutSongTime.Value, .03, "抜けた時刻(1.12)ではなく、いちばん近づいた時刻(1.0)を採点に使う");
    }

    [Test]
    public void ScheduledPrerollUsesNegativeClock()
    {
        Create(false, false, 1);
        spawner.Tick(0);
        note.HitTime = 0;
        tracker.ResetTo(new Vector3(-2, 0, 0));
        tracker.Tick(new Vector3(2, 0, 0), .033f);
        TickManager(-.06);
        Assert.True(note.IsCut, "開始前でも表頭の早め窓を使える");
    }

    [Test]
    public void StoppedClockDoesNotReopenExpiredWindowOrRewindNote()
    {
        Create(false, false, 1);
        spawner.Tick(0);
        note.HitTime = 0;
        spawner.Tick(.4);
        Vector3 position = note.transform.position;
        song.Stop();
        typeof(GamePlayManager).GetMethod("Update", Fields).Invoke(manager, null);
        Assert.False(note.IsJudgeable);
        Assert.AreEqual(position, note.transform.position);
    }

    [Test]
    public void WindowRefreshDoesNotMoveOrFinalizeNotes()
    {
        Create(false, false, 1);
        spawner.Tick(.94);
        Vector3 position = note.transform.position;
        int misses = 0;
        spawner.OnNoteMissed += _ => misses++;
        spawner.RefreshJudgmentWindows(5);
        Assert.False(note.IsJudgeable);
        Assert.False(note.IsMissed, "Miss通知と回収は判定後のTickに任せる");
        Assert.AreEqual(0, misses);
        Assert.AreEqual(1, spawner.AliveCount);
        Assert.AreEqual(position, note.transform.position);
    }

    // カメラの値が届いているときは判定の時計だけを遅らせる。マウス等(カメラなし)では遅らせない。
    [TestCase(true, 1.0)]
    [TestCase(false, 1.12)]
    public void CameraInputDelaysOnlyTheJudgeClock(bool camera, double expectedScoredTime)
    {
        Create(true, false, 1);
        bridge.OverrideCameraInput(camera);
        spawner.Tick(.9);
        tracker.ResetTo(new Vector3(-2, 0, 0)); tracker.Tick(Vector3.zero, .033f);
        bridge.OverrideBlade(Vector3.left, Vector3.right); // ノーツの真上
        TickManager(1.12);
        Assert.AreEqual(camera ? manager.cameraJudgeLatencySeconds : 0, spawner.JudgeLatencySeconds, 1e-6);
        Assert.AreEqual(-.12f * spawner.Speed, note.transform.position.z, .05f, "ノーツの位置は曲の時計のまま");
        tracker.Tick(new Vector3(3, 0, 0), .033f);
        bridge.OverrideBlade(new Vector3(3, 0, 0), new Vector3(5, 0, 0)); // 抜けた
        TickManager(1.15);
        Assert.True(note.IsCut);
        Assert.AreEqual(expectedScoredTime, note.LastCutSongTime.Value, .02);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CameraLatencyKeepsTheWindowOpenLongerAndDelaysTheMiss(bool camera)
    {
        Create(true, false, 1);
        bridge.OverrideCameraInput(camera);
        spawner.Tick(.9);
        TickManager(1.30); // 判定の時計では、カメラなら0.18秒過ぎ(窓の中)、なしなら0.30秒過ぎ
        Assert.AreEqual(camera, note.IsJudgeable);
        Assert.AreEqual(!camera, note.IsMissed);
        TickManager(1.40);
        Assert.True(note.IsMissed);
    }

    [Test]
    public void ImuJudgedSaberDoesNotDelayTheJudgeClock()
    {
        Create(true, false, 1);
        bridge.OverrideCameraInput(true);
        judge.ExternalJudgment = true;
        spawner.Tick(.9);
        TickManager(1.0);
        Assert.AreEqual(0, spawner.JudgeLatencySeconds, 1e-9, "IMU 判定は自前で遅れを補正するので二重にしない");
    }

    // 遅めに近づき、締め切り(Miss)のときにまだノーツの上を通っている刃は、通り過ぎたら切れる。
    // 通り過ぎないまま止まっていたら、少し待ってから Miss。
    [TestCase(true)]
    [TestCase(false)]
    public void BladeStillPassingTheNoteAtTheDeadlineIsCutWhenItPasses(bool passes)
    {
        Create(true, false, 1);
        spawner.HoldMiss = (System.Func<CuttableNote, bool>)typeof(GamePlayManager)
            .GetMethod("SaberCuttingThrough", Fields).CreateDelegate(typeof(System.Func<CuttableNote, bool>), manager);
        judge.bladeRadius = .32f; judge.noteHitRadiusXY = .88f; // 本編と同じ当たり(刃との距離 1.2)
        spawner.Tick(.9);
        tracker.ResetTo(new Vector3(.3f, 2f, 0));
        StepVerticalBlade(new Vector2(.3f, 1.4f), 1.17); // 入る(判定窓の終わり近く)
        StepVerticalBlade(new Vector2(.3f, .8f), 1.19);  // 窓の中でいちばん近づく
        StepVerticalBlade(new Vector2(.3f, .5f), 1.27);  // 締め切り(1 + 0.2025 + 0.06)を過ぎたが、刃の先はノーツの高さまでで、まだ越えていない
        Assert.False(note.IsMissed, "刃がノーツの上を通っている最中は Miss を待つ");
        Assert.False(note.IsCut);
        if (passes)
        {
            StepVerticalBlade(new Vector2(.3f, -.4f), 1.30); // 刃の先がノーツより下へ
            Assert.True(note.IsCut);
            Assert.False(note.IsMissed);
            Assert.AreEqual(1.19, note.LastCutSongTime.Value, .02, "採点はいちばん近づいた時刻");
        }
        else
        {
            StepVerticalBlade(new Vector2(.3f, .5f), 1.45); // 止まったまま、待つ時間(0.15秒)も過ぎた
            Assert.True(note.IsMissed);
            Assert.False(note.IsCut);
        }
    }

    // 縦の刃(長さ1)の中心を動かして、1フレーム進める。
    void StepVerticalBlade(Vector2 center, double time)
    {
        tracker.Tick(new Vector3(center.x, center.y, 0), .02f);
        bridge.OverrideBlade(new Vector3(center.x, center.y - .5f, 0), new Vector3(center.x, center.y + .5f, 0));
        TickManager(time);
    }

    void TickManager(double time)
    {
        Set(song, "scheduled", true);
        Set(song, "clockSynchronized", true);
        Set(song, "startDspTime", AudioSettings.dspTime - time);
        typeof(GamePlayManager).GetMethod("Update", Fields).Invoke(manager, null);
    }

    static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Fields).SetValue(target, value);
}
