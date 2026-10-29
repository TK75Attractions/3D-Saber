using System;
using System.Collections.Generic;
using UnityEngine;

// 実績の判定に使う、その回の外の情報(2026-10-29)。AchievementTracker が書く。
// 選曲の秒・裏の扉・ヒントは次に始める曲だけ。タイトルの手と向き・練習の結果は、最初の記録された曲まで持つ。
// 来場者が替わったら全部消す。判定調整の往復ではタイトル・練習を消さず、選曲のヒントは新しい画面で数え直す。
public static partial class AchievementRunInfo
{
    public static readonly double SelectionStartSeconds = new SongSelectCountdown().Remaining;
    public static readonly AchievementSelectionHistory Selection = new AchievementSelectionHistory();
    public static bool Backpick { get; private set; }

    public static double? PendingSelectionSeconds { get; private set; }
    public static bool PracticePending { get; private set; }
    public static bool PracticePairPending { get; private set; }
    public static bool PracticeAllFirstLoopPending { get; private set; }
    public static bool PendingOpenedSecret { get; private set; }
    public static int PendingHintsBeforeOpen { get; private set; }
    public static SaberHand PendingTitleHand { get; private set; } = SaberHand.Any;
    public static bool PendingTitleUpward { get; private set; }

    public static string RunId { get; private set; }
    public static double? SelectionSeconds { get; private set; }
    public static bool PracticedBefore { get; private set; }
    public static bool PracticePairBefore { get; private set; }
    public static bool PracticeAllFirstLoop { get; private set; }
    public static bool OpenedSecret { get; private set; }
    public static int HintsBeforeOpen { get; private set; }
    public static SaberHand TitleHand { get; private set; } = SaberHand.Any;
    public static bool TitleUpward { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void Clear() => BeginVisitor();

    // 来場者が替わった(タイトル)。前の人の回の写しも返さない。
    public static void BeginVisitor()
    {
        PendingSelectionSeconds = SelectionSeconds = null;
        PracticePending = PracticedBefore = false;
        PracticePairPending = PracticePairBefore = false;
        PracticeAllFirstLoopPending = PracticeAllFirstLoop = false;
        PendingOpenedSecret = OpenedSecret = false;
        PendingHintsBeforeOpen = HintsBeforeOpen = 0;
        PendingTitleHand = TitleHand = SaberHand.Any;
        PendingTitleUpward = TitleUpward = false;
        RunId = null;
        Selection.Clear();
        Backpick = false;
    }

    // openedSecret と hintsBeforeOpen は、この選曲で新しく開いた分だけを Tracker が渡す。
    // 判定調整から戻った選曲でも、戻ったあとで新しくヒントを読んで開いた分は数える。
    public static void SongChosen(double? remaining, bool calibrationReturn, bool openedSecret = false, int hintsBeforeOpen = 0)
    {
        PendingSelectionSeconds = calibrationReturn || !remaining.HasValue ? (double?)null : SelectionElapsed(remaining.Value);
        PendingOpenedSecret = openedSecret;
        PendingHintsBeforeOpen = openedSecret ? Math.Max(0, hintsBeforeOpen) : 0;
    }

    public static void TitleCut(SaberHand hand) => TitleCut(hand, Vector3.zero);

    // タイトルの開始ノーツを切った手と向き。キーボードは Any。
    public static void TitleCut(SaberHand hand, Vector3 velocity)
    {
        PendingTitleHand = hand;
        PendingTitleUpward = (hand == SaberHand.Left || hand == SaberHand.Right)
            && !float.IsNaN(velocity.x) && !float.IsNaN(velocity.y)
            && !float.IsInfinity(velocity.x) && !float.IsInfinity(velocity.y)
            && velocity.y > 0 && velocity.y >= Math.Abs(velocity.x);
    }

    public static double SelectionElapsed(double remaining) =>
        double.IsNaN(remaining) || double.IsInfinity(remaining) ? SelectionStartSeconds : Math.Max(0, SelectionStartSeconds - remaining);

    // 同じ練習の中で両方の段が ok だったかを見る。別々の練習の片方ずつを足し合わせない。
    public static void PracticeFinished(bool rangeOnly, string finishReason, IEnumerable<TutorialController.TutorialStepRecord> records)
    {
        var list = records != null ? new List<TutorialController.TutorialStepRecord>(records) : null;
        bool practiced = CountsAsPractice(rangeOnly, finishReason, list);
        PracticePending |= practiced;
        if (rangeOnly || list == null) return;
        bool arrow = false, longNote = false, firstLoop = false;
        foreach (var record in list)
        {
            if (record.Reason != "ok") continue;
            if (record.Id == "arrowEasy") arrow = true;
            if (record.Id == "long") longNote = true;
            if (record.Id == TutorialProgram.AllStepId && record.FirstLoop) firstLoop = true;
        }
        PracticePairPending |= arrow && longNote;
        PracticeAllFirstLoopPending |= practiced && firstLoop;
    }

    // 最後まで終えた練習か(振る範囲だけの回・スキップ・どれかの練習が時間切れの回は数えない)。
    public static bool CountsAsPractice(bool rangeOnly, string finishReason, IEnumerable<TutorialController.TutorialStepRecord> records)
    {
        if (rangeOnly || finishReason != "done") return false;
        if (records != null)
            foreach (var record in records)
                if (record.Reason == "timeout" || record.Reason == "skipped") return false;
        return true;
    }

    // 選曲の情報は使い切る。練習とタイトルは、記録されない回・始め直しでは使い切らない。
    public static void BeginRun(string runId)
    {
        RunId = runId;
        Backpick = Selection.Consume();
        SelectionSeconds = PendingSelectionSeconds;
        PracticedBefore = PracticePending;
        PracticePairBefore = PracticePairPending;
        PracticeAllFirstLoop = PracticeAllFirstLoopPending;
        OpenedSecret = PendingOpenedSecret;
        HintsBeforeOpen = PendingHintsBeforeOpen;
        TitleHand = PendingTitleHand;
        TitleUpward = PendingTitleUpward;
        PendingSelectionSeconds = null;
        PendingOpenedSecret = false;
        PendingHintsBeforeOpen = 0;
    }

    public static void RunRecorded(string runId)
    {
        if (string.IsNullOrEmpty(runId) || runId != RunId) return;
        PendingTitleHand = SaberHand.Any;
        PendingTitleUpward = false;
        PracticePending = PracticePairPending = PracticeAllFirstLoopPending = false;
    }

    public static bool ForRun(string runId, out double? selectionSeconds, out bool practicedBefore) =>
        ForRun(runId, out selectionSeconds, out practicedBefore, out _, out _);

    public static bool ForRun(string runId, out double? selectionSeconds, out bool practicedBefore, out bool openedSecret, out SaberHand titleHand)
    {
        bool same = !string.IsNullOrEmpty(runId) && runId == RunId;
        selectionSeconds = same ? SelectionSeconds : null;
        practicedBefore = same && PracticedBefore;
        openedSecret = same && OpenedSecret;
        titleHand = same ? TitleHand : SaberHand.Any;
        return same;
    }
}
