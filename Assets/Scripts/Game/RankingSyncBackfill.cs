using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// 同期していない間の回も合わせる(2026-10-29 ユーザー「完全に同期しちゃって欲しい。つまり同期してない間のものも加えるようにする」)。
// 同期の記録(RankingSyncLog)に入るのは、同期が動いている起動で遊び終わった回だけ(RankingSync.RecordLocal)。同期が入る前の版・
// -rankingSync off で遊んだ回は、その台の通算の表(DailyRankingStore)にだけ残り、相手の台へ届かなかった
// (台A に同期が入った 10/28 14:53 より前の台A の回。アンダルシア Hard は台A で32回、台B で15回)。
// RankingSync が動き出すとき(Activate)に1回、この台の通算の表を名前空間×曲×難易度ごとに読み、記録に無い回をこの台の回として作る。
// RankingSync はそれを記録の後ろに足すだけ(表にはもうある)。記録が変われば要約が変わり、相手の台へは全部の記録の束で届く
// (通信の形は版 3 のまま。受ける台は今の版のままで合わせられる)。
// ・回の正本は通算の表(上位5件の表 HighScoreStore にしか無い回は足さない。通算の表だけを消したときに、上位5件から戻さない)。
// ・通算の表に残っているのは回の ID と点だけ。同じ回の上位5件の記録(ID が同じもの。ID の無い古い記録は点が同じもの)があれば、
//   ランク・精度・日付・時刻はそこから。無ければ、ランクは点から(結果画面と同じ)、精度は点の割合(精度はどの画面にも出していない)。
//   時刻は表の並び(足した順 = 遊び終わった順)から: 1つ前の回(記録にある回はその時刻)の 1 ミリ秒あと(表を数え始めた日の 0 時から)。
//   上位5件に日付があれば、その日の 0 時より前にはしない。日付は時刻の日(上位5件に日付があればそれ)。
// ・その回に取った実績(AchievementStore)も入れる(相手の台の「○人目」の数にも入る)。
// ・読めない表は飛ばす(上書きしない)。満点が 100000 になる前の表(v1)は読まない。
public static class RankingSyncBackfill
{
    // 調べる1つの表。難易度は遊ぶときと同じ書き方(Hard・T など)にする(相手の台の実績の条件が難易度の名前を比べるため)。
    public readonly struct Chart
    {
        public readonly string Ns, Song, Difficulty;
        public Chart(string ns, string song, string difficulty) { Ns = ns ?? ""; Song = song; Difficulty = difficulty; }
    }

    // この台の全部の表: StreamingAssets/Songs の曲 × Easy・Normal・Hard とその曲の裏譜面 × ふだんの表・運営モードの表。
    // 今は選曲に出していない曲も調べる(出していたときの回も合わせる)。テストが作る仮の曲(__ で始まる)は調べない。
    public static List<Chart> StationCharts()
    {
        var charts = new List<Chart>();
        string root = Path.Combine(Application.streamingAssetsPath, "Songs");
        string[] directories;
        try { directories = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch (Exception) { return charts; }
        Array.Sort(directories, StringComparer.Ordinal);
        foreach (string ns in new[] { OperatorMode.OrdinaryNamespace, OperatorMode.FestivalNamespace })
            foreach (string directory in directories)
            {
                string song = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(song) || song.StartsWith("__", StringComparison.Ordinal)) continue;
                foreach (string difficulty in SongSelectController.StandardDifficulties) charts.Add(new Chart(ns, song, difficulty));
                var secret = RushESecretState.For(song);
                if (secret != null) charts.Add(new Chart(ns, song, secret.Difficulty));
            }
        return charts;
    }

    // charts の通算の表にあって記録に無い回を、この台(station)の回として表の順に作る。find は記録にある回(無ければ null)、
    // waiting は表へ足す待ちにある相手の回か(その回は相手の台の回なので作らない)。PlayerPrefs は読むだけ。メインスレッドで。
    public static List<RankingPlay> Collect(IEnumerable<Chart> charts, Func<string, RankingPlay> find, Func<string, bool> waiting, string station)
    {
        var plays = new List<RankingPlay>();
        if (charts == null) return plays;
        foreach (var chart in charts)
        {
            if (string.IsNullOrWhiteSpace(chart.Song) || string.IsNullOrWhiteSpace(chart.Difficulty)) continue;
            string json = PlayerPrefs.GetString(DailyRankingStore.Key(chart.Ns, chart.Song, chart.Difficulty), "");
            if (string.IsNullOrEmpty(json) || !DailyRankingStore.TryRead(json, "", out var table)) continue;
            // 上位5件の表が読めなければ、空の表として点から作る。
            HighScoreStore.TryLoad(chart.Ns, chart.Song, chart.Difficulty, out var top);
            var byRun = new Dictionary<string, HighScoreEntry>(StringComparer.Ordinal);
            var withoutRun = new List<HighScoreEntry>();
            foreach (var entry in top.entries)
            {
                if (string.IsNullOrEmpty(entry.runId)) withoutRun.Add(entry);
                else if (!byRun.ContainsKey(entry.runId)) byRun.Add(entry.runId, entry);
            }
            long previous = DayStart(table.day, "yyyy-MM-dd") - 1;
            foreach (var entry in table.entries)
            {
                var logged = find?.Invoke(entry.runId);
                if (logged != null) { previous = Math.Max(previous, logged.at); continue; }
                // 記録から外した回(RankingRemovals)は、この台の回としても作らない。
                if ((waiting != null && waiting(entry.runId)) || !RankingSyncLog.IsValidId(entry.runId) || RankingRemovals.IsRemoved(entry.runId)) continue;
                if (!byRun.TryGetValue(entry.runId, out var best) || best.score != entry.score)
                {
                    best = null;
                    int index = withoutRun.FindIndex(e => e.score == entry.score);
                    if (index >= 0) { best = withoutRun[index]; withoutRun.RemoveAt(index); }
                }
                long at = best != null && best.at > 0 ? best.at : Math.Max(previous + 1, best != null ? DayStart(best.date, "yyyy/MM/dd") : 0);
                if (at <= 0) continue;
                previous = Math.Max(previous, at);
                var play = Build(chart, entry, best, at, station);
                // 上位5件の記録の形が違えば(古い記録のランク・日付)、点から作り直す。
                if (!RankingSyncLog.IsValid(play)) play = Build(chart, entry, null, at, station);
                if (RankingSyncLog.IsValid(play)) plays.Add(play);
            }
        }
        return plays;
    }

    static RankingPlay Build(Chart chart, DailyRankingStore.Entry entry, HighScoreEntry best, long at, string station)
    {
        string rank = best != null ? best.rank : PlayRankHelper.Label(PlayRankHelper.FromScore(entry.score));
        float accuracy = best != null ? best.accuracy : PlayRankHelper.ScoreFraction(entry.score);
        var play = RankingSyncLog.NewPlay(entry.runId, chart.Song, chart.Difficulty, entry.score, rank, accuracy,
            DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime, station, chart.Ns, AchievementStore.UnlockedIn(chart.Ns, entry.runId));
        play.at = at;
        if (best != null && !string.IsNullOrEmpty(best.date)) play.date = best.date;
        return play;
    }

    // その日のこの台の 0 時(UTC の Unix ミリ秒)。読めなければ 0。
    static long DayStart(string day, string format) =>
        DateTime.TryParseExact(day, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local)).ToUnixTimeMilliseconds() : 0;
}
