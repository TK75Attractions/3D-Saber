using System.Collections.Generic;
using UnityEngine;

// 熱の進行は GamePlayManager の判定直前の Tick だけが担当する。
// IMU の遅れて確定したカットも、T と同じ現在の判定時計で受理する。
// LastCutSongTime は採点用のまま残し、熱の時計を過去へ戻さない。
public sealed class HeatMode : MonoBehaviour
{
    // 説明はカウントインだけ。ノーツの近くには文字を出さない。
    public const string RuleCaption = "熱い剣は休ませよう。金はどちらの手でも";

    public HeatSession Session { get; private set; }

    readonly HashSet<CuttableNote> observed = new HashSet<CuttableNote>();
    readonly SaberCutJudge[] judges = new SaberCutJudge[2];
    NoteSpawner spawner;
    HeatView view;
    double judgeTime = double.NaN;
    double songTime;
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
            mode.view = HeatView.Create(go.transform, mode.judges, mode.Session.Max);
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
        if (note.IsHeat) view?.Observe(note);
    }

    void Forget(CuttableNote note)
    {
        observed.Remove(note);
        if (note == null) return;
        note.OnPartialCut -= Cut;
        note.OnRetired -= Forget;
        view?.Forget(note);
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

    public bool Allows(SaberCutJudge judge, CuttableNote note)
    {
        if (ended || note == null || !observed.Contains(note)) return true;
        if (!HeatRules.Finite(judgeTime)) return false;
        return Session.CanCut(HeatHandOf(CutterHand(judge, note)), note.IsHeat);
    }

    void Cut(CuttableNote note, int cutIndex, int total)
    {
        if (ended || !HeatRules.Finite(judgeTime)) return;
        var hand = HeatHandOf(note.LastCutterHand);
        if (hand == HeatHand.Any)
            hand = Session.ChooseHand(HeatHandOf(note.RequiredHand));
        // OnPartialCut は通常ノーツとロングの全カットに一度ずつ届く。
        // ミス・ロングの期限切れはここへ来ない。
        Session.RecordCut(hand, judgeTime);
    }

    public void Tick(double currentSongTime, double currentJudgeTime)
    {
        if (ended || Session == null || !HeatRules.Finite(currentSongTime)
            || !HeatRules.Finite(currentJudgeTime)) return;
        int right = Session.HeatOf(HeatHand.Right), left = Session.HeatOf(HeatHand.Left);
        Session.Advance(currentJudgeTime);
        judgeTime = Session.Time;
        songTime = currentSongTime;
        if (Session.HeatOf(HeatHand.Right) < right) view?.Cooled(HeatHand.Right, songTime);
        if (Session.HeatOf(HeatHand.Left) < left) view?.Cooled(HeatHand.Left, songTime);
    }

    // 刃に追従する描画だけ。停止中も熱と演出の曲時計は進めない。
    void LateUpdate()
    {
        if (!ended && Session != null) view?.Draw(Session, songTime);
    }

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
        view?.Dispose();
        view = null;
        spawner = null;
    }

    void OnDisable() => EndPlay();
    void OnDestroy() => EndPlay();
}
