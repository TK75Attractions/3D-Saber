using System;
using UnityEngine;

public enum CameraImuMode { CameraOnly, Auto, ImuAndCamera }

[RequireComponent(typeof(GameplaySwingAdapter))]
public class CameraImuJudgment : MonoBehaviour
{
    [Header("Input mode")]
    [Tooltip("Autoは左右が判明した最初のSwing以降、その側だけIMUを必須にする。無入力時間では切り替えない。")]
    public CameraImuMode mode = CameraImuMode.Auto;
    public GameplaySwingAdapter swingInput;
    [Header("Physical side to camera mapping")]
    public CameraSaberColor leftCamera = CameraSaberColor.Blue;
    public CameraSaberColor rightCamera = CameraSaberColor.Red;
    [Header("Judgment timing (ms)")]
    [Range(0, 250)] public float cameraLatencyCompensationMs = 100;
    [Range(100, 500)] public float pendingSwingWindowMs = 300;
    [Range(10, 150)] public float swingCameraToleranceMs = 60;
    [Range(20, 200)] public float maxCameraSampleGapMs = 100;
    [Range(.5f, 1f)] public float cameraHistorySeconds = .75f;
    [Header("Camera direction")]
    [Range(0, 90)] public float directionToleranceDegrees = 30;
    [Header("Debug")]
    public bool debugJudgment;
    public string LastDecision { get; private set; }
    public int PendingSwingCount { get { int count = 0; foreach (var p in pending) if (p.Active) count++; return count; } }

    struct PendingSwing
    {
        public bool Active, HasSongTime;
        public GameplaySwing Swing;
        public double SongTime;
        public string Failure;
        public int NoteId;
        public bool PositionPassed, Swept;
        public JudgmentTier Original;
    }

    struct SeenSwing { public bool Valid; public GameplaySwing Swing; }
    struct Contact
    {
        public Vector3 Point, Velocity;
        public double SampleTime, ReceiveTime;
        public bool Swept;
    }

    readonly PendingSwing[] pending = new PendingSwing[64];
    readonly SeenSwing[] seen = new SeenSwing[256];
    readonly CameraSaberHistory red = new CameraSaberHistory();
    readonly CameraSaberHistory blue = new CameraSaberHistory();
    int seenNext;
    bool leftConfirmed, rightConfirmed;
    double now, oldestAllowedSwing = double.NegativeInfinity;
    double previousSongTime, previousNow;
    bool hasClock;
    NoteSpawner spawner;
    SaberCutJudge primary, secondary;
    IGameplaySwingSource source;
    CameraImuMode configuredMode;
    CameraSaberColor configuredLeft, configuredRight;

    public void Configure(NoteSpawner notes, SaberCutJudge first, SaberCutJudge second)
    {
        Detach();
        spawner = notes; primary = first; secondary = second;
        if (swingInput == null) swingInput = GetComponent<GameplaySwingAdapter>();
        source = swingInput;
        if (source != null) { source.SwingReceived += ReceiveSwing; source.SessionReset += ResetSession; }
        if (spawner != null) { spawner.OnChartReset += ResetSession; spawner.ShouldDeferMiss = ShouldDeferMiss; }
        configuredMode = mode; configuredLeft = leftCamera; configuredRight = rightCamera;
        ClearState(double.NegativeInfinity);
    }

    void Detach()
    {
        if (source != null) { source.SwingReceived -= ReceiveSwing; source.SessionReset -= ResetSession; }
        if (spawner != null)
        {
            spawner.OnChartReset -= ResetSession;
            if (spawner.ShouldDeferMiss == ShouldDeferMiss) spawner.ShouldDeferMiss = null;
        }
        ReleaseLegacyJudges();
        source = null;
    }

    void OnDisable() { ClearState(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp)); }
    void OnDestroy() { Detach(); }
    public void ResetSession() => ClearState(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp));

    void ClearState(double boundary)
    {
        Array.Clear(pending, 0, pending.Length);
        Array.Clear(seen, 0, seen.Length);
        red.Clear(); blue.Clear(); seenNext = 0;
        leftConfirmed = rightConfirmed = hasClock = false;
        oldestAllowedSwing = boundary;
        ReleaseLegacyJudges();
    }

    void ReleaseLegacyJudges()
    {
        if (primary != null) primary.ExternalJudgment = false;
        if (secondary != null) secondary.ExternalJudgment = false;
    }

    // 入力アダプターからメインスレッドで呼ぶ。XIAO時計は識別・ログ用で、未同期の時刻を曲時計に使わない。
    public void ReceiveSwing(GameplaySwing swing)
    {
        if (!isActiveAndEnabled || mode == CameraImuMode.CameraOnly) return;
        if (swing.Side != PhysicalSaberSide.Left && swing.Side != PhysicalSaberSide.Right)
        { Decision($"seq={swing.Sequence} side=Unknown reason=unknown_side"); return; }
        if (!CameraSaberHistory.Finite(swing.ReceiveTime) || swing.ReceiveTime < oldestAllowedSwing) return;
        for (int i = 0; i < seen.Length; i++)
        {
            GameplaySwing previous = seen[i].Swing;
            if (seen[i].Valid && previous.Side == swing.Side && previous.Sequence == swing.Sequence
                && previous.XiaoTimestampUs == swing.XiaoTimestampUs)
            { Decision($"seq={swing.Sequence} side={swing.Side} reason=duplicate"); return; }
        }
        for (int i = 0; i < pending.Length; i++)
        {
            if (pending[i].Active) continue;
            pending[i] = new PendingSwing { Active = true, Swing = swing, Failure = "camera_missing" };
            seen[seenNext] = new SeenSwing { Valid = true, Swing = swing };
            seenNext = (seenNext + 1) % seen.Length;
            return;
        }
        Decision($"seq={swing.Sequence} side={swing.Side} reason=pending_capacity");
    }

    public bool AddCameraSample(CameraSaberSample sample)
    {
        if (sample.ReceiveTime < oldestAllowedSwing) return false;
        return History(sample.Color).Add(sample, Mathf.Clamp(cameraHistorySeconds, .5f, 1f));
    }

    CameraSaberHistory History(CameraSaberColor color) => color == CameraSaberColor.Red ? red : blue;
    CameraSaberColor CameraFor(PhysicalSaberSide side) => side == PhysicalSaberSide.Left ? leftCamera : rightCamera;

    CameraSaberColor ColorFor(SaberCutJudge judge)
    {
        if (judge.bladeProvider != null) return judge.bladeProvider.stickIndex == 2 ? CameraSaberColor.Blue : CameraSaberColor.Red;
        return judge == secondary ? CameraSaberColor.Blue : CameraSaberColor.Red;
    }

    bool UsesFusion(SaberCutJudge judge)
    {
        if (!isActiveAndEnabled || mode == CameraImuMode.CameraOnly || judge == null || !judge.isActiveAndEnabled) return false;
        if (judge.bladeProvider != null && judge.bladeProvider.UsingMouseFallback) return false;
        if (leftCamera == rightCamera) return false; // 曖昧な2対1設定では既存入力を維持する。
        CameraSaberColor color = ColorFor(judge);
        return mode == CameraImuMode.ImuAndCamera || (leftConfirmed && leftCamera == color) || (rightConfirmed && rightCamera == color);
    }

    SaberCutJudge JudgeFor(CameraSaberColor color)
    {
        if (primary != null && ColorFor(primary) == color) return primary;
        if (secondary != null && ColorFor(secondary) == color) return secondary;
        return null;
    }

    // 必ずGamePlayManagerから旧判定とNoteSpawner.Tickより前に呼ぶ。
    public void Tick(double monotonicNow, double songTime, bool clockActive = true)
    {
        now = monotonicNow;
        if (swingInput != null) swingInput.RefreshBinding();
        if (!isActiveAndEnabled || !clockActive || mode == CameraImuMode.CameraOnly)
        { ClearState(monotonicNow); return; }
        if (configuredMode != mode || configuredLeft != leftCamera || configuredRight != rightCamera)
        {
            ClearState(monotonicNow);
            configuredMode = mode; configuredLeft = leftCamera; configuredRight = rightCamera;
        }
        // シーク・曲再開で別の時刻へ履歴を持ち越さない。
        if (hasClock && (monotonicNow < previousNow || songTime < previousSongTime - .01
            || Math.Abs((songTime - previousSongTime) - (monotonicNow - previousNow)) > .15))
            ClearState(monotonicNow);
        hasClock = true; previousNow = monotonicNow; previousSongTime = songTime;
        Capture(primary); Capture(secondary);
        red.Trim(monotonicNow - cameraHistorySeconds); blue.Trim(monotonicNow - cameraHistorySeconds);
        double window = Mathf.Clamp(pendingSwingWindowMs, 100, 500) / 1000.0;
        for (int i = 0; i < pending.Length; i++)
        {
            if (!pending[i].Active) continue;
            var p = pending[i];
            double age = monotonicNow - p.Swing.ReceiveTime;
            if (age < 0 || age > window)
            {
                TraceUnresolved(p, age);
                pending[i].Active = false;
                continue;
            }
            if (!p.HasSongTime)
            {
                p.SongTime = songTime - age;
                p.HasSongTime = true;
                if (p.Swing.Side == PhysicalSaberSide.Left) leftConfirmed = true; else rightConfirmed = true;
            }
            if (TryResolve(ref p)) p.Active = false;
            pending[i] = p;
        }
        if (primary != null) primary.ExternalJudgment = UsesFusion(primary);
        if (secondary != null) secondary.ExternalJudgment = UsesFusion(secondary);
    }

    void Capture(SaberCutJudge judge)
    {
        if (judge != null && judge.bladeProvider != null && judge.bladeProvider.TryGetCameraSample(out var sample))
            AddCameraSample(sample);
    }

    bool IsCandidate(CuttableNote note, PendingSwing p, SaberCutJudge judge)
    {
        if (note == null || !note.gameObject.activeInHierarchy || note.IsCut || note.IsMissed || note.IsFinalized) return false;
        if (!SaberHandHelper.CanCut(note.RequiredHand, judge.EffectiveHand())) return false;
        double error = p.SongTime - note.HitTime;
        return error >= -spawner.earlyJudgeWindow && error <= spawner.LateWindowFor(note);
    }

    bool TryResolve(ref PendingSwing p)
    {
        if (spawner == null) return false;
        CameraSaberColor color = CameraFor(p.Swing.Side);
        SaberCutJudge judge = JudgeFor(color);
        if (!UsesFusion(judge)) { p.Failure = "unavailable_mapping_or_camera"; return false; }
        CameraSaberHistory history = History(color);
        if (history.Count < 2) return false;
        CuttableNote best = null;
        Contact contact = default;
        double bestError = double.PositiveInfinity;
        p.Failure = "no_note_in_swing_window";
        p.PositionPassed = p.Swept = false;
        for (int n = 0; n < spawner.LiveNotes.Count; n++)
        {
            CuttableNote note = spawner.LiveNotes[n];
            if (!IsCandidate(note, p, judge)) continue;
            p.NoteId = note.GetInstanceID();
            p.Original = note.RequiredCutCount > 1
                ? ScoreManager.TierByCompletionRatio((note.CutsAchieved + 1f) / note.RequiredCutCount)
                : JudgmentTierHelper.Classify(p.SongTime - note.HitTime);
            p.Failure = "position_fail";
            if (!FindContact(history, p.Swing.ReceiveTime, note, judge, out var hit)) continue;
            double error = Math.Abs(p.SongTime - note.HitTime);
            if (best != null && (error > bestError || (error == bestError && note.GetInstanceID() >= best.GetInstanceID()))) continue;
            best = note; bestError = error; contact = hit;
        }
        if (best == null) return false;
        p.PositionPassed = true; p.Swept = contact.Swept; p.NoteId = best.GetInstanceID();
        if (best.RequiredDirection != CutDirection.None && !best.DirectionVisualOnly)
        {
            // 接触直前の静止フレームだけでは方向を確定しない。Swing後の軌跡まで待つ。
            if (!TryCameraDirection(history, p.Swing.ReceiveTime, out Vector3 directionVelocity))
            { p.Failure = "direction_history_missing"; return false; }
            contact.Velocity = directionVelocity;
        }
        int noteId = best.GetInstanceID();
        // ノーツはXYが固定でZだけ流れる。判定時刻のZを再構成して演出にも渡す。
        contact.Point.z = spawner.ComputeNoteZ(best, best.HitTime - p.SongTime, spawner.Speed);
        JudgmentTier original = best.RequiredCutCount > 1
            ? ScoreManager.TierByCompletionRatio((best.CutsAchieved + 1f) / best.RequiredCutCount)
            : JudgmentTierHelper.Classify(p.SongTime - best.HitTime);
        bool direction = best.DirectionVisualOnly || CutDirectionHelper.Matches(best.RequiredDirection,
            contact.Velocity, Mathf.Cos(directionToleranceDegrees * Mathf.Deg2Rad));
        bool cumulativeDirection = direction && (best.CutsAchieved == 0 || best.LastCutCorrectDirection);
        var tier = cumulativeDirection ? original : ScoreManager.DowngradeTier(original);
        bool accepted = best.TryCutWithCameraTiming(contact.Point, contact.Velocity, judge.EffectiveHand(),
            p.SongTime, Mathf.Cos(directionToleranceDegrees * Mathf.Deg2Rad));
        if (!accepted) { p.Failure = "note_rejected"; return false; }
        // 成功した時点でこのイベントを消費。ロングも1イベントにつき1カット。
        Decision($"seq={p.Swing.Sequence} side={p.Swing.Side} camera={color} receive={p.Swing.ReceiveTime:F6} xiao={p.Swing.XiaoTimestampUs} song={p.SongTime:F6} sample={contact.SampleTime:F6} sampleAgeMs={(now - contact.ReceiveTime) * 1000:F1} latencyMs={cameraLatencyCompensationMs:F1} deltaMs={(contact.SampleTime - p.Swing.ReceiveTime) * 1000:F1} note={noteId} position=pass swept={contact.Swept} direction={direction} original={original} final={tier} reason={(tier == JudgmentTier.Miss ? "direction_downgrade_miss" : "hit")}");
        return true;
    }

    bool FindContact(CameraSaberHistory history, double swingTime, CuttableNote note, SaberCutJudge judge, out Contact contact)
    {
        contact = default;
        double latency = Mathf.Clamp(cameraLatencyCompensationMs, 0, 250) / 1000.0;
        double tolerance = Mathf.Clamp(swingCameraToleranceMs, 10, 150) / 1000.0;
        for (int i = 1; i < history.Count; i++)
        {
            CameraSaberSample prev = history[i - 1], next = history[i];
            double t0 = prev.ReceiveTime - latency, t1 = next.ReceiveTime - latency;
            if (next.ReceiveTime > now || t1 - t0 > maxCameraSampleGapMs / 1000.0) continue;
            double begin = Math.Max(t0, swingTime - tolerance), end = Math.Min(t1, swingTime + tolerance);
            if (end <= begin) continue;
            Vector3 nextA = next.EndA, nextB = next.EndB;
            CameraSaberHistory.AlignEndpoints(prev, ref nextA, ref nextB);
            float alpha0 = (float)((begin - t0) / (t1 - t0)), alpha1 = (float)((end - t0) / (t1 - t0));
            Vector3 a0 = Vector3.Lerp(prev.EndA, nextA, alpha0), b0 = Vector3.Lerp(prev.EndB, nextB, alpha0);
            Vector3 a1 = Vector3.Lerp(prev.EndA, nextA, alpha1), b1 = Vector3.Lerp(prev.EndB, nextB, alpha1);
            Vector3 velocity = ((a1 + b1) - (a0 + b0)) * (.5f / (float)(end - begin));
            float distance = CameraSaberHistory.SweptDistance(note.transform.position, a0, b0, a1, b1, out Vector2 point);
            float range = Mathf.Max(0, judge.bladeRadius + judge.noteHitRadiusXY);
            if (distance > range) continue;
            bool swept = SaberCutJudge.DistPointToSegment(note.transform.position, a0, b0, out _) > range
                && SaberCutJudge.DistPointToSegment(note.transform.position, a1, b1, out _) > range;
            contact = new Contact { Point = point, Velocity = velocity, SampleTime = end, ReceiveTime = next.ReceiveTime, Swept = swept };
            return true;
        }
        return false;
    }

    bool TryCameraDirection(CameraSaberHistory history, double swingTime, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        double latency = Mathf.Clamp(cameraLatencyCompensationMs, 0, 250) / 1000.0;
        double tolerance = Mathf.Clamp(swingCameraToleranceMs, 10, 150) / 1000.0;
        double start = swingTime - tolerance, finish = swingTime + tolerance;
        bool found = false;
        Vector3 from = Vector3.zero, to = Vector3.zero;
        double firstTime = 0, lastTime = 0;
        for (int i = 1; i < history.Count; i++)
        {
            var a = history[i - 1]; var b = history[i];
            double t0 = a.ReceiveTime - latency, t1 = b.ReceiveTime - latency;
            if (b.ReceiveTime > now || t1 < start || t0 > finish) continue;
            if (t1 - t0 > maxCameraSampleGapMs / 1000.0) { found = false; continue; }
            double begin = Math.Max(t0, start), end = Math.Min(t1, finish);
            if (end <= begin) continue;
            if (!found)
            {
                from = Vector3.Lerp(a.Center, b.Center, (float)((begin - t0) / (t1 - t0)));
                firstTime = begin; found = true;
            }
            to = Vector3.Lerp(a.Center, b.Center, (float)((end - t0) / (t1 - t0)));
            lastTime = end;
        }
        if (!found || lastTime < finish - .000001 || firstTime > swingTime || lastTime <= firstTime) return false;
        velocity = (to - from) / (float)(lastTime - firstTime);
        return true;
    }

    // 判定窓は延長しない。有効なSwingのCamera照合が未解決の場合だけMiss確定を待つ。
    bool ShouldDeferMiss(CuttableNote note, double songTime)
    {
        if (!isActiveAndEnabled || spawner == null) return false;
        foreach (var p in pending)
        {
            if (!p.Active || !p.HasSongTime || now - p.Swing.ReceiveTime > pendingSwingWindowMs / 1000.0) continue;
            SaberCutJudge judge = JudgeFor(CameraFor(p.Swing.Side));
            if (UsesFusion(judge) && IsCandidate(note, p, judge)) return true;
        }
        return false;
    }

    void Decision(string detail)
    {
        LastDecision = detail;
        if (debugJudgment) Debug.Log("[CameraIMU] " + detail, this);
    }

    void TraceUnresolved(PendingSwing p, double age)
    {
        CameraSaberHistory history = History(CameraFor(p.Swing.Side));
        double receive = history.Count > 0 ? history[history.Count - 1].ReceiveTime : double.NaN;
        double sample = receive - cameraLatencyCompensationMs / 1000.0;
        Decision($"seq={p.Swing.Sequence} side={p.Swing.Side} receive={p.Swing.ReceiveTime:F6} xiao={p.Swing.XiaoTimestampUs} song={p.SongTime:F6} sample={sample:F6} sampleAgeMs={(now - receive) * 1000:F1} latencyMs={cameraLatencyCompensationMs:F1} deltaMs={(sample - p.Swing.ReceiveTime) * 1000:F1} note={p.NoteId} position={(p.PositionPassed ? "pass" : "fail")} swept={p.Swept} direction=unresolved original={(p.NoteId == 0 ? "unresolved" : p.Original.ToString())} final=no_hit reason={(age < 0 ? "future_swing" : "expired_" + p.Failure)}");
    }
}
