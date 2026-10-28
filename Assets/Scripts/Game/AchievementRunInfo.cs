using System;
using System.Collections.Generic;
using UnityEngine;

// 実績の判定に使う、その回の外の情報(2026-10-29)。GameSession には足さず、ここに持つ(担当 B)。
// ・選曲にかかった秒(即決): 選曲で曲を決めた瞬間に、選曲の残り時間の数え方で覚える。判定調整から戻った選曲は数えない。
// ・練習(基本に忠実): 同じ来場者の練習(TutorialController)が最後まで終わったか(スキップ・時間切れ・振る範囲だけの回は数えない)。
// どちらも次に始めた本編の曲(BeginRun)が使い切る。来場者が替わったら(タイトル)消す。AchievementTracker が書く。
public static class AchievementRunInfo
{
    // 選曲の残り時間の始まり(SongSelectCountdown の既定。新しい選曲はこの秒から数える)。
    public static readonly double SelectionStartSeconds = new SongSelectCountdown().Remaining;

    // 次に始める曲の、選曲にかかった秒(null は数えない回)と、その前に練習を終えていたか。
    public static double? PendingSelectionSeconds { get; private set; }
    public static bool PracticePending { get; private set; }

    // 今の曲の回(BeginRun で決める)。
    public static string RunId { get; private set; }
    public static double? SelectionSeconds { get; private set; }
    public static bool PracticedBefore { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    public static void Clear()
    {
        BeginVisitor();
        RunId = null;
        SelectionSeconds = null;
        PracticedBefore = false;
    }

    // 来場者が替わった(タイトル)。前の人の選曲・練習を持ち越さない。
    public static void BeginVisitor()
    {
        PendingSelectionSeconds = null;
        PracticePending = false;
    }

    // 選曲で曲を決めた。remaining は選曲の残り時間、calibrationReturn は判定調整から戻った選曲か。
    public static void SongChosen(double? remaining, bool calibrationReturn)
    {
        PendingSelectionSeconds = calibrationReturn || !remaining.HasValue ? (double?)null : SelectionElapsed(remaining.Value);
    }

    // 残り時間から、選曲にかかった秒。時間切れの自動開始は残り 0 なので、始まりの秒(100)になる。
    public static double SelectionElapsed(double remaining) =>
        double.IsNaN(remaining) || double.IsInfinity(remaining) ? SelectionStartSeconds : Math.Max(0, SelectionStartSeconds - remaining);

    // 練習が終わった(TutorialController.IsFinished のとき1回)。
    public static void PracticeFinished(bool rangeOnly, string finishReason, IEnumerable<TutorialController.TutorialStepRecord> records)
    {
        if (CountsAsPractice(rangeOnly, finishReason, records)) PracticePending = true;
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

    // 本編の曲を始めた(AchievementTracker が曲の記録を結び付けたとき)。覚えていた選曲・練習を、この回のものにして使い切る。
    public static void BeginRun(string runId)
    {
        RunId = runId;
        SelectionSeconds = PendingSelectionSeconds;
        PracticedBefore = PracticePending;
        PendingSelectionSeconds = null;
        PracticePending = false;
    }

    // この回の選曲の秒と練習(別の回なら、どちらも無し)。
    public static bool ForRun(string runId, out double? selectionSeconds, out bool practicedBefore)
    {
        bool same = !string.IsNullOrEmpty(runId) && runId == RunId;
        selectionSeconds = same ? SelectionSeconds : null;
        practicedBefore = same && PracticedBefore;
        return same;
    }
}
