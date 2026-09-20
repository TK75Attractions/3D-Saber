using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public class CameraImuJudgmentTests
{
    GameObject root;
    NoteSpawner spawner;
    ScoreManager score;
    CameraImuJudgment fusion;
    SaberCutJudge judge, judge2;
    readonly List<CuttableNote> notes = new List<CuttableNote>();
    const double Clock = 100;

    void Create(CameraImuMode mode = CameraImuMode.ImuAndCamera, int count = 1,
        string direction = "", bool twoNotes = false)
    {
        root = new GameObject("CameraImuTest");
        spawner = root.AddComponent<NoteSpawner>();
        spawner.buildTimingCues = false; spawner.simultaneousGuideEnabled = false;
        spawner.noteRoot = root.transform;
        var prefab = new GameObject("NotePrefab"); prefab.transform.SetParent(root.transform);
        prefab.AddComponent<CuttableNote>(); prefab.SetActive(false); spawner.notePrefab = prefab;
        score = root.AddComponent<ScoreManager>(); score.Bind(spawner);
        spawner.OnNoteSpawned += n => { n.gameObject.SetActive(true); notes.Add(n); };
        var data = new ChartData { notes = new List<NoteData> {
            new NoteData { time = 1000, color = "red", count = count, direction = direction }
        } };
        if (twoNotes) data.notes.Add(new NoteData { time = 1040, color = "red", count = 1 });
        spawner.SetChart(data); spawner.Tick(.9);
        judge = MakeJudge("Red", SaberHand.Right);
        judge2 = MakeJudge("Blue", SaberHand.Left);
        fusion = root.AddComponent<CameraImuJudgment>();
        fusion.GetComponent<GameplaySwingAdapter>().readLegacyStream = false;
        fusion.mode = mode; fusion.Configure(spawner, judge, judge2);
        Advance(0);
    }

    SaberCutJudge MakeJudge(string name, SaberHand hand)
    {
        var go = new GameObject(name); go.transform.SetParent(root.transform);
        var tracker = go.AddComponent<SaberTracker>(); tracker.enabled = false;
        var j = go.AddComponent<SaberCutJudge>(); j.autonomous = false; j.saber = tracker;
        j.hand = hand; j.maxCutDistance = 10; return j;
    }

    void Swing(double at = 0, ushort seq = 1, PhysicalSaberSide side = PhysicalSaberSide.Right, uint xiao = 123)
        => fusion.ReceiveSwing(new GameplaySwing(seq, side, Clock + at, xiao));

    void Sample(double receive, Vector3 center, CameraSaberColor color = CameraSaberColor.Red, float halfLength = 0)
        => fusion.AddCameraSample(new CameraSaberSample(Clock + receive,
            center + Vector3.up * halfLength, center - Vector3.up * halfLength, color));

    void Path(double at = 0, bool reversed = false, CameraSaberColor color = CameraSaberColor.Red)
    {
        float sign = reversed ? -1 : 1;
        Sample(at + .08, Vector3.left * 2 * sign, color);
        Sample(at + .12, Vector3.right * 2 * sign, color);
        Sample(at + .17, Vector3.right * 3 * sign, color);
    }

    void Advance(double at)
    {
        fusion.Tick(Clock + at, 1 + at);
        spawner.Tick(1 + at);
    }

    [TearDown]
    public void Cleanup()
    {
        if (root != null) Object.DestroyImmediate(root);
        notes.Clear();
    }

    [Test]
    public void CameraCrossingWithoutSwingDoesNotHitInStrictMode()
    {
        Create(); Path(); Advance(.17);
        judge.saber.ResetTo(Vector3.left * 2); judge.saber.Tick(Vector3.right * 2, .03f);
        Assert.That(judge.TryCut(), Is.Zero);
        Assert.False(notes[0].IsCut); Assert.True(judge.ExternalJudgment);
    }

    [Test]
    public void SwingFarFromCameraDoesNotHit()
    {
        Create(); Swing(); Sample(.08, new Vector3(-2, 5)); Sample(.12, new Vector3(2, 5));
        Advance(.12); Assert.False(notes[0].IsCut);
        Advance(.31); Assert.That(score.MissCount, Is.EqualTo(1)); Assert.That(fusion.PendingSwingCount, Is.Zero);
    }

    [Test]
    public void DelayedCameraUsesSwingTimeForPerfectScoreAndSweepsBetweenFrames()
    {
        Create(); Swing(); Advance(.05); Assert.False(notes[0].IsCut);
        Path(); Advance(.17);
        Assert.True(notes[0].IsCut); Assert.That(score.LastTier, Is.EqualTo(JudgmentTier.Perfect));
        Assert.That(score.LastErrorMs, Is.EqualTo(0).Within(.001));
        Assert.That(fusion.LastDecision, Does.Contain("swept=True"));
    }

    [Test]
    public void SweptBladeHitsEvenWhenItsCenterMisses()
    {
        Create(); Swing();
        Sample(.08, new Vector3(-2, 1.5f), halfLength: 2);
        Sample(.12, new Vector3(2, 1.5f), halfLength: 2);
        Advance(.12); Assert.True(notes[0].IsCut);
    }

    [Test]
    public void LateSwingWaitsForCameraBeyondNormalMissDeadline()
    {
        Create(); Advance(.18); Swing(.18); Advance(.19);
        Advance(.28); Assert.False(notes[0].IsMissed); Assert.False(notes[0].IsJudgeable);
        Path(.18); Advance(.35);
        Assert.True(notes[0].IsCut); Assert.That(score.LastTier, Is.EqualTo(JudgmentTier.Bad));
        Assert.That(score.MissCount, Is.Zero);
    }

    [Test]
    public void SwingOutsideOriginalTimingWindowCannotUseDeferredWindow()
    {
        Create(); Swing(.23); Path(.23); Advance(.4);
        Assert.False(notes[0].IsCut); Assert.True(notes[0].IsMissed);
    }

    [Test]
    public void ExpiredSwingCannotHitEvenWithMatchingHistory()
    {
        Create(); Swing(); Path(); Advance(.31);
        Assert.False(notes[0].IsCut); Assert.That(fusion.PendingSwingCount, Is.Zero);
    }

    [Test]
    public void DuplicatePacketAndOneEventCannotCutLongTwice()
    {
        Create(count: 3); Swing(); Swing(); Path(); Advance(.17);
        Assert.That(notes[0].CutsAchieved, Is.EqualTo(1));
        Swing(.18); Advance(.19);
        Assert.That(notes[0].CutsAchieved, Is.EqualTo(1));
        Assert.That(fusion.PendingSwingCount, Is.Zero);
        Swing(.2, 2, xiao: 456); Path(.2); Advance(.37);
        Assert.That(notes[0].CutsAchieved, Is.EqualTo(2));
    }

    [Test]
    public void MultipleEventsCannotScoreSameNoteTwice()
    {
        Create(); Swing(); Swing(.001, 2, xiao: 456); Path(); Advance(.17); Advance(.32);
        Assert.That(score.HitCount, Is.EqualTo(1)); Assert.That(score.MissCount, Is.Zero);
    }

    [Test]
    public void OverlappingNotesConsumeOnlyOneEventChoosingNearestTime()
    {
        Create(twoNotes: true); Swing(); Path(); Advance(.17);
        Assert.True(notes[0].IsCut); Assert.False(notes[1].IsCut);
        Assert.That(score.HitCount, Is.EqualTo(1));
    }

    [TestCase("right", false, JudgmentTier.Perfect)]
    [TestCase("right", true, JudgmentTier.Great)]
    [TestCase("up", false, JudgmentTier.Great)]
    public void DirectionComesFromCameraAndOppositeDirectionDowngrades(string direction, bool reverse, JudgmentTier expected)
    {
        Create(direction: direction); Swing(); Path(reversed: reverse);
        Advance(.12); Assert.False(notes[0].IsCut, "方向はSwing後の軌跡まで待つ");
        Advance(.17); Assert.True(notes[0].IsCut); Assert.That(score.LastTier, Is.EqualTo(expected));
    }

    [TestCase(.00, JudgmentTier.Great)]
    [TestCase(.10, JudgmentTier.Good)]
    [TestCase(.15, JudgmentTier.Bad)]
    [TestCase(.19, JudgmentTier.Miss)]
    public void WrongDirectionDropsExactlyOneExistingTier(double at, JudgmentTier expected)
    {
        Create(direction: "right"); Advance(at); Swing(at); Path(at, true); Advance(at + .17);
        Assert.True(notes[0].IsCut); Assert.That(score.LastTier, Is.EqualTo(expected));
        Assert.That(score.HitCount + score.MissCount, Is.EqualTo(1));
    }

    [TestCase(SwingDirection.Left, false, JudgmentTier.Perfect)]
    [TestCase(SwingDirection.Right, true, JudgmentTier.Great)]
    public void ImuDirectionIsNotUsed(SwingDirection imuDirection, bool reverse, JudgmentTier expected)
    {
        Create(direction: "right");
        long ticks = (long)(Clock * System.Diagnostics.Stopwatch.Frequency);
        fusion.swingInput.Publish(new SwingEvent(1, imuDirection, 1, 123, ticks, 0), PhysicalSaberSide.Right);
        Path(reversed: reverse); Advance(.17);
        Assert.That(score.LastTier, Is.EqualTo(expected));
    }

    [TestCase(CutDirection.Up)] [TestCase(CutDirection.Down)]
    [TestCase(CutDirection.Left)] [TestCase(CutDirection.Right)]
    [TestCase(CutDirection.UpLeft)] [TestCase(CutDirection.UpRight)]
    [TestCase(CutDirection.DownLeft)] [TestCase(CutDirection.DownRight)]
    public void AllEightChartDirectionsMatchCameraMovement(CutDirection direction)
    {
        Create(direction: direction.ToString()); Swing();
        Vector3 v = CutDirectionHelper.ToVector(direction);
        Sample(.08, -v * 2); Sample(.12, v * 2); Sample(.17, v * 3); Advance(.17);
        Assert.That(score.LastTier, Is.EqualTo(JudgmentTier.Perfect));
    }

    [TestCase(20, JudgmentTier.Great)] [TestCase(50, JudgmentTier.Perfect)]
    public void InspectorDirectionToleranceIsApplied(float degrees, JudgmentTier expected)
    {
        Create(direction: "right"); fusion.directionToleranceDegrees = degrees; Swing();
        var v = new Vector3(1, 1).normalized;
        Sample(.08, -v * 2); Sample(.12, v * 2); Sample(.17, v * 3); Advance(.17);
        Assert.That(score.LastTier, Is.EqualTo(expected));
    }

    [Test]
    public void IndependentSidesMayUseSameSequence()
    {
        Create(twoNotes: true); notes[1].RequiredHand = SaberHand.Left; notes[1].HitTime = 1;
        Swing(); Swing(side: PhysicalSaberSide.Left);
        Path(); Path(color: CameraSaberColor.Blue); Advance(.17);
        Assert.True(notes[0].IsCut); Assert.True(notes[1].IsCut);
        Assert.That(score.PerfectCount, Is.EqualTo(2));
    }

    [Test]
    public void SwappedBladeEndpointsDoNotInventMotion()
    {
        var previous = new CameraSaberSample(1, Vector3.up, Vector3.down, CameraSaberColor.Red);
        Vector3 a = Vector3.down, b = Vector3.up;
        CameraSaberHistory.AlignEndpoints(previous, ref a, ref b);
        Assert.That(a, Is.EqualTo(Vector3.up)); Assert.That(b, Is.EqualTo(Vector3.down));
    }

    [Test]
    public void RecycledNoteDoesNotKeepPriorSwingClock()
    {
        Create(); Swing(); Path(); Advance(.17);
        Assert.That(notes[0].LastCutSongTime.Value, Is.EqualTo(1).Within(.000001));
        typeof(CuttableNote).GetMethod("ResetForSpawn", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(notes[0], null);
        Assert.That(notes[0].LastCutSongTime, Is.Null);
    }

    [Test]
    public void PartialLongTimeoutPreservesCompletionScoreAndDoesNotDoubleMiss()
    {
        Create(count: 3); Swing(); Path(); Advance(.17);
        Advance(1); Advance(2);
        Assert.That(score.LastTier, Is.EqualTo(JudgmentTier.Bad));
        Assert.That(score.HitCount, Is.EqualTo(1)); Assert.That(score.MissCount, Is.Zero);
    }

    [TestCase(CameraImuMode.Auto)]
    [TestCase(CameraImuMode.CameraOnly)]
    public void WithoutImuLegacyCameraPlayStillWorks(CameraImuMode mode)
    {
        Create(mode); Advance(.02);
        judge.saber.ResetTo(Vector3.left * 2); judge.saber.Tick(Vector3.right * 2, .03f);
        Assert.That(judge.TryCut(), Is.EqualTo(1)); Assert.True(notes[0].IsCut);
    }

    [Test]
    public void UnknownSideDoesNotGuessOrDisableFallback()
    {
        Create(CameraImuMode.Auto); Swing(side: PhysicalSaberSide.Unknown); Advance(.1);
        Assert.False(judge.ExternalJudgment); Assert.False(judge2.ExternalJudgment);
        Assert.That(fusion.PendingSwingCount, Is.Zero);
    }

    [Test]
    public void CameraMissingExpiresWithoutExceptionAndResolvesMissOnce()
    {
        Create(); Swing(); Advance(.27); Assert.False(notes[0].IsMissed);
        Advance(.31); Advance(.4);
        Assert.True(notes[0].IsMissed); Assert.That(score.MissCount, Is.EqualTo(1));
    }

    [Test]
    public void AutoActivationIsPerSideAndDoesNotTimeOutBetweenSwings()
    {
        Create(CameraImuMode.Auto, count: 5); Swing(); Advance(.1);
        Assert.True(judge.ExternalJudgment); Assert.False(judge2.ExternalJudgment);
        Advance(1); Assert.True(judge.ExternalJudgment);
    }

    [Test]
    public void PhysicalMappingCanBeReversedWithoutChangingNoteColor()
    {
        Create(); fusion.leftCamera = CameraSaberColor.Red; fusion.rightCamera = CameraSaberColor.Blue;
        Advance(.01); Swing(.02, side: PhysicalSaberSide.Left); Path(.02); Advance(.19);
        Assert.True(notes[0].IsCut); Assert.That(notes[0].LastCutterHand, Is.EqualTo(SaberHand.Right));
    }

    [Test]
    public void BlueSwingCannotUseRedCameraHistory()
    {
        Create(); Swing(side: PhysicalSaberSide.Left); Path(); Advance(.17);
        Assert.False(notes[0].IsCut);
    }

    [Test]
    public void MainThreadDeliveryDelayDoesNotChangeSwingTiming()
    {
        Create(); Advance(.1); Swing(); Path(); Advance(.17);
        Assert.That(score.LastTier, Is.EqualTo(JudgmentTier.Perfect));
    }

    [Test]
    public void LongCameraGapIsNotInventedAsSweep()
    {
        Create(); Swing(); Sample(.001, Vector3.left * 2); Sample(.22, Vector3.right * 2);
        Advance(.23); Assert.False(notes[0].IsCut);
    }

    [Test]
    public void CameraTrajectoryOutsideSwingIntervalCannotHit()
    {
        Create(); Swing(); Sample(.27, Vector3.left * 2); Sample(.29, Vector3.right * 2);
        Advance(.29); Assert.False(notes[0].IsCut);
    }

    [Test]
    public void DisabledFusionClearsPendingAndRestoresLegacyOnManagerTick()
    {
        Create(); Swing(); Advance(.02); fusion.enabled = false;
        fusion.Tick(Clock + .03, 1.03);
        Assert.That(fusion.PendingSwingCount, Is.Zero); Assert.False(judge.ExternalJudgment);
        fusion.enabled = true; Advance(.04); Assert.That(fusion.PendingSwingCount, Is.Zero);
    }

    [Test]
    public void StoppingClockDropsPendingBeforeRestart()
    {
        Create(); Swing(); Path(); fusion.Tick(Clock + .1, 0, false);
        Advance(.17); Assert.False(notes[0].IsCut); Assert.That(fusion.PendingSwingCount, Is.Zero);
    }

    [Test]
    public void HistoryIsBoundedAndRejectsDuplicatesInvalidAndOutOfOrderSamples()
    {
        var history = new CameraSaberHistory();
        for (int i = 0; i < 2000; i++) history.Add(new CameraSaberSample(i * .001, Vector3.zero, Vector3.one, CameraSaberColor.Red), .75);
        Assert.That(history.Count, Is.EqualTo(128));
        Assert.False(history.Add(history[127], .75));
        Assert.False(history.Add(new CameraSaberSample(double.NaN, Vector3.zero, Vector3.zero, CameraSaberColor.Red), .75));
        history.Trim(3); Assert.That(history.Count, Is.Zero);
    }

    [Test]
    public void CameraLatencySettingChangesAssociation()
    {
        Create(); fusion.cameraLatencyCompensationMs = 0; Swing(); Path(); Advance(.17);
        Assert.False(notes[0].IsCut);
        fusion.cameraLatencyCompensationMs = 100; Advance(.18); Assert.True(notes[0].IsCut);
    }

    [Test]
    public void GameManagerCallsFusionBeforeLegacyJudgesAndMissFinalization()
    {
        Create();
        var song = root.AddComponent<SongPlayer>();
        var manager = root.AddComponent<GamePlayManager>(); manager.enabled = false;
        manager.songPlayer = song; manager.noteSpawner = spawner; manager.cutJudge = judge;
        manager.cameraImuJudgment = fusion;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(GamePlayManager).GetField("ready", flags).SetValue(manager, true);
        typeof(SongPlayer).GetField("scheduled", flags).SetValue(song, true);
        typeof(SongPlayer).GetField("clockSynchronized", flags).SetValue(song, true);
        typeof(SongPlayer).GetField("startDspTime", flags).SetValue(song, AudioSettings.dspTime - 1);
        judge.saber.ResetTo(Vector3.left * 2); judge.saber.Tick(Vector3.right * 2, .03f);
        typeof(GamePlayManager).GetMethod("Update", flags).Invoke(manager, null);
        Assert.False(notes[0].IsCut); Assert.True(judge.ExternalJudgment);
    }
}
