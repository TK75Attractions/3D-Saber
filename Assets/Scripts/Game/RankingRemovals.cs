using System;
using System.Collections.Generic;
using UnityEngine;

// 記録から外す回(2026-10-30 ユーザー「今のeldordo ハードと青葉騒のハードの1位の記録は私がテストでやったものなので消して」「運営モードの方ね」)。
// どちらも台B で遊んだ運営モード(fes)の回。
// ・2台の同期の記録(RankingSync.Log.v1)には残す(相手の台と要約を同じに保ち、古い版の台と全部の記録を送り合い続けないため。
//   古い版の台がこの回を送ってきても「もう知っている回」で終わる)。
// ・表と実績には入れない: 本番のビルドの起動時(最初の場面を読む前)に1回、通算の順位・上位5件・曲ごとの達成・実績の解除の記録から除く
//   (何度起動しても同じ。もう無ければ何も書かない)。RankingSync は、この回を表と実績の数へ足さず(起動時の照合・届いた回・足し直し・
//   同期していない間の回)、実績の条件に渡す記録(KnownPlays)にも入れない(はじめての人・一番乗りを、次の来場者が取れるように)。
// ・Editor とテストでは起動時の掃除をしない(本物の保存値に触れない)。表と実績へ足さない決まりは同じ。
// 台A も、この版をビルドして起動すれば同じように消える(それまでは台A の表に残る)。
public static class RankingRemovals
{
    public sealed class Run
    {
        public readonly string Ns, Song, Difficulty, RunId;
        // その回の点(曲ごとの達成から、その回のランクの分を引く)と、フルコンボ・AP だったか。
        public readonly int Score;
        public readonly bool FullCombo, AllPerfect;

        public Run(string ns, string song, string difficulty, string runId, int score, bool fullCombo = false, bool allPerfect = false)
        {
            Ns = ns ?? ""; Song = song; Difficulty = difficulty; RunId = runId;
            Score = score; FullCombo = fullCombo; AllPerfect = allPerfect;
        }
    }

    // 2026-10-30 の昼、台B のユーザーのテスト。ElDorado Hard は運営モードの表で唯一の1件(87783・S)、青葉騒 Hard は1位(84457・S。
    // ほかの3件は残す)。どちらもフルコンボではない(曲ごとの達成の fc は 0 のまま)。
    static readonly Run[] runs =
    {
        new Run(OperatorMode.FestivalNamespace, "ElDorado", "Hard", "5d387961e3d64f5d90c7d9bbc759ec15", 87783),
        new Run(OperatorMode.FestivalNamespace, "Aobasou", "Hard", "e891b56bb17a4d55804fb9022ab821e2", 84457),
    };
    static readonly HashSet<string> ids = BuildIds();

    public static IReadOnlyList<Run> Runs => runs;
    // この起動の始めに除いたものの数(表・曲ごとの達成・実績の解除の合計。RankingSync が運営の記録 events.log に書く)。
    public static int CleanedAtStartup { get; private set; }

    static HashSet<string> BuildIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in runs) set.Add(run.RunId);
        return set;
    }

    // 回の ID で引く(ID は回ごとに別の GUID)。
    public static bool IsRemoved(string runId) => runId != null && ids.Contains(runId);

    // 記録から外した回を除いた一覧(外す回が無ければ、渡したまま)。
    public static IReadOnlyList<RankingPlay> Without(IReadOnlyList<RankingPlay> plays)
    {
        if (plays == null) return plays;
        bool any = false;
        foreach (var play in plays)
            if (play != null && IsRemoved(play.id)) { any = true; break; }
        if (!any) return plays;
        var kept = new List<RankingPlay>(plays.Count);
        foreach (var play in plays)
            if (play == null || !IsRemoved(play.id)) kept.Add(play);
        return kept.AsReadOnly();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void CleanAtStartup()
    {
        CleanedAtStartup = 0;
        if (Application.isEditor || Array.IndexOf(Environment.GetCommandLineArgs(), RankingSync.TestRunArgument) >= 0) return;
        try
        {
            CleanedAtStartup = Clean();
            if (CleanedAtStartup > 0) Debug.Log("RankingRemovals: 記録から外す回を、表と実績から除きました(" + CleanedAtStartup + " か所)。");
        }
        catch (Exception exception)
        {
            // 除けなくてもゲームは今までどおり動く(次の起動でまた試す)。
            Debug.LogWarning("RankingRemovals: 記録から外す回を除けませんでした。" + exception.GetType().Name + ": " + exception.Message);
        }
    }

    // 通算の順位・上位5件・曲ごとの達成・実績の解除の記録から外す回を除き、変えたらディスクまで保存する。除いたものの数を返す
    // (もう無ければ 0 で、何も書かない)。読めない記録は上書きしない(それぞれの店の決まりのまま)。テストは鍵を差し替えてから呼ぶ。
    public static int Clean()
    {
        int cleaned = 0;
        foreach (var run in runs)
        {
            if (DailyRankingStore.RemoveRun(run.Ns, run.Song, run.Difficulty, run.RunId)) cleaned++;
            if (HighScoreStore.RemoveRun(run.Ns, run.Song, run.Difficulty, run.RunId)) cleaned++;
            if (SongAchievementStore.RemoveRun(run.Ns, run.Song, run.Difficulty, run.RunId, run.Score, run.FullCombo, run.AllPerfect)) cleaned++;
        }
        cleaned += AchievementStore.RemoveRuns(IsRemoved);
        if (cleaned > 0) PlayerPrefs.Save();
        return cleaned;
    }
}
