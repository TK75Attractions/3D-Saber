using System;
using System.Collections.Generic;
using UnityEngine;

// 熱の進行は GamePlayManager の判定直前の Tick だけが担当する。
// IMU の遅れて確定したカットも、T と同じ現在の判定時計で受理する。
// LastCutSongTime は採点用のまま残し、熱の時計を過去へ戻さない。
public sealed class HeatMode : MonoBehaviour
{
    public const double RejectInterval = .3;
    public HeatSession Session { get; private set; }
    public HeatView View { get; private set; }
    public HeatSfx Sfx { get; private set; }
    public event Action<HeatHand> Overheated;
    public event Action<HeatHand, int> Cooled;
    public event Action<CuttableNote, HeatHand, Vector3> Rejected;

    struct Rejection { public uint version; public double at; }
    readonly HashSet<CuttableNote> observed = new HashSet<CuttableNote>(512);
    readonly Dictionary<CuttableNote, Rejection> rejected = new Dictionary<CuttableNote, Rejection>(512);
    readonly SaberCutJudge[] judges = new SaberCutJudge[2];
    readonly double[] nextCoolingCue = { double.NegativeInfinity, double.NegativeInfinity };
    NoteSpawner spawner;
    double judgeTime = double.NaN;
    double songTime = double.NaN;
    bool ended;

    public static HeatMode Create(Transform parent, ChartData chart, NoteSpawner spawner,
        SaberCutJudge first, SaberCutJudge second, bool showVisuals = true)
    {
        if (chart == null || chart.heat == null) return null;
        var go = new GameObject("HeatMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<HeatMode>();
        mode.spawner = spawner;
        double offset = spawner != null ? spawner.TotalOffsetSeconds : chart.offsetMs / 1000.0;
        mode.Session = new HeatSession(chart.heat,
            new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap), offset);
        mode.judges[0] = first;
        mode.judges[1] = second;
        foreach (var judge in mode.judges)
        {
            if (judge == null) continue;
            if (judge.Heat != null && judge.Heat != mode) judge.Heat.EndPlay();
            judge.Heat = mode;
        }
        if (showVisuals)
        {
            mode.View = HeatView.Create(go.transform, mode.judges, mode.Session.Max, chart.notes?.Count ?? 32);
            mode.Sfx = new HeatSfx(go.transform);
        }
        if (spawner != null)
        {
            spawner.OnNoteSpawned += mode.Observe;
            spawner.OnChartReset += mode.EndPlay;
            foreach (var note in spawner.LiveNotes) mode.Observe(note);
        }
        return mode;
    }

    // 生成済みノーツへの接続にも使う。プールの退役通知で必ず解除する。
    public void Observe(CuttableNote note)
    {
        if (ended || note == null || note.IsFinalized || !observed.Add(note)) return;
        note.OnPartialCut += Cut;
        note.OnRetired += Forget;
        if (note.IsHeat) View?.Observe(note);
    }

    void Forget(CuttableNote note)
    {
        observed.Remove(note);
        rejected.Remove(note);
        if (note == null) return;
        note.OnPartialCut -= Cut;
        note.OnRetired -= Forget;
        View?.Forget(note);
    }

    static HeatHand HeatHandOf(SaberHand hand)
        => hand == SaberHand.Right ? HeatHand.Right
            : hand == SaberHand.Left ? HeatHand.Left : HeatHand.Any;

    static SaberHand SaberHandOf(HeatHand hand)
        => hand == HeatHand.Left ? SaberHand.Left : SaberHand.Right;

    // マウスの Any だけは仮想の二本として扱う。
    // 色指定はその手、金などは熱の低い手。実機の手は変更しない。
    public SaberHand CutterHand(SaberCutJudge judge, CuttableNote note)
    {
        var hand = judge.EffectiveHand();
        if (ended || note == null || !observed.Contains(note) || hand != SaberHand.Any) return hand;
        return SaberHandOf(Session.ChooseHand(HeatHandOf(note.RequiredHand)));
    }

    // 照会だけで音や火花を出さない。接触を確認した判定部品が別の入口で通知する。
    public bool Allows(SaberCutJudge judge, CuttableNote note)
    {
        if (ended || note == null || !observed.Contains(note)) return true;
        if (!HeatRules.Finite(judgeTime)) return false;
        return Session.CanCut(HeatHandOf(CutterHand(judge, note)), note.IsHeat);
    }

    internal bool RejectedContact(SaberCutJudge judge, CuttableNote note, Vector3 point)
    {
        if (ended || judge == null || judge.Stunned || note == null || note.IsFinalized
            || !note.isActiveAndEnabled || !observed.Contains(note) || !HeatRules.Finite(judgeTime)
            || !HeatRules.Finite(songTime)) return false;
        var cutter = CutterHand(judge, note);
        if (!SaberHandHelper.CanCut(note.RequiredHand, cutter) || Allows(judge, note)) return false;
        var hand = HeatHandOf(cutter);
        if (hand == HeatHand.Any || !Session.IsOverheated(hand)) return false;
        if (rejected.TryGetValue(note, out var previous) && previous.version == note.SpawnVersion
            && songTime - previous.at + 1e-9 < RejectInterval) return false;
        rejected[note] = new Rejection { version = note.SpawnVersion, at = songTime };
        // 火花だけを本体の手前へ置く。判定位置・ノーツ・ゲートは動かさない。
        var body = note.BodyRenderer;
        point.z = (body != null ? Mathf.Min(point.z, body.bounds.min.z) : point.z) - .04f;
        Emit(HeatCue.Reject, hand, songTime, point);
        Rejected?.Invoke(note, hand, point);
        return true;
    }

    void Emit(HeatCue cue, HeatHand hand, double at, Vector3 point = default)
    {
        View?.Notify(cue, hand, at, point);
        Sfx?.Schedule(cue, hand, at);
    }

    void Cut(CuttableNote note, int cutIndex, int total)
    {
        if (ended || !HeatRules.Finite(judgeTime)) return;
        var hand = HeatHandOf(note.LastCutterHand);
        if (hand == HeatHand.Any)
            hand = Session.ChooseHand(HeatHandOf(note.RequiredHand));
        // 上限のまま普通のノーツを切っても、上限到達の音は繰り返さない。
        bool wasFull = Session.IsOverheated(hand);
        if (!Session.RecordCut(hand, judgeTime)) return;
        View?.SetLastHand(hand);
        if (!wasFull && Session.IsOverheated(hand))
        {
            Emit(HeatCue.Full, hand, songTime);
            Overheated?.Invoke(hand);
        }
    }

    public void Tick(double currentSongTime, double currentJudgeTime)
    {
        if (ended || Session == null || !HeatRules.Finite(currentSongTime)
            || !HeatRules.Finite(currentJudgeTime)) return;
        int right = Session.HeatOf(HeatHand.Right), left = Session.HeatOf(HeatHand.Left);
        Session.Advance(currentJudgeTime);
        judgeTime = Session.Time;
        songTime = currentSongTime;
        NotifyCooling(HeatHand.Right, right);
        NotifyCooling(HeatHand.Left, left);
    }

    void NotifyCooling(HeatHand hand, int before)
    {
        int h = hand == HeatHand.Left ? 1 : 0;
        // フレームが飛んで複数段冷えた場合も各段を通知し、見た目と音だけ90msずつずらす。
        for (int level = before - 1; level >= Session.HeatOf(hand); level--)
        {
            double at = Math.Max(songTime, nextCoolingCue[h]);
            nextCoolingCue[h] = at + .09;
            Emit(HeatCue.Cool, hand, at);
            Cooled?.Invoke(hand, level);
        }
    }

    // 手動駆動の試走も本編と同じ表示を使う。熱や採点の状態は進めない。
    public void DrawFeedback()
    {
        if (ended || Session == null || !HeatRules.Finite(songTime)) return;
        View?.Draw(Session, songTime);
        Sfx?.Tick(songTime);
    }

    void LateUpdate() => DrawFeedback();

    public void EndPlay()
    {
        if (ended) return;
        ended = true;
        if (spawner != null)
        {
            spawner.OnNoteSpawned -= Observe;
            spawner.OnChartReset -= EndPlay;
        }
        foreach (var note in new List<CuttableNote>(observed)) Forget(note);
        foreach (var judge in judges)
            if (judge != null && judge.Heat == this) judge.Heat = null;
        View?.Dispose();
        Sfx?.Dispose();
        View = null; Sfx = null;
        rejected.Clear();
        Overheated = null; Cooled = null; Rejected = null;
        spawner = null;
    }

    void OnDisable() => EndPlay();
    void OnDestroy() => EndPlay();
}
