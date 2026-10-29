using System;
using System.Collections.Generic;
using System.Globalization;

// 実績の条件(AchievementCatalog)が見る、1回の演奏の結果の写し(2026-10-29)。結果が決まったとき(GameSession.RunRecording)に
// AchievementTracker が GameSession の Final*・PlayStats・その回の情報(AchievementRunInfo)・記録(AchievementStore・RankingSync)から作る。
// テストは値を直接入れて作る。
public sealed class AchievementContext
{
    public string SongId = "", Difficulty = "";
    public int Score, MaxCombo, Hit, Miss, Perfect, Great, Good, Bad, CutCount;
    public float Accuracy;
    // 照準の裏譜面(D)の回か。D はノーツを使う実績の対象外。
    public bool AimMode;
    public int AimDamage;
    // 通算の順位(GameSession.FinalDailyRanking。このモードの表、2台合算)。
    public bool RankingAvailable;
    public int DailyRank, TotalPlays, TiedPlays;
    // 演奏中の記録。null は記録が無い回(ノーツ・コンボの節目・X斬りなどを使う実績は取れない)。
    public PlayStats Stats;
    // 曲を始めた・終えた時刻(台の現地時刻)と、終えた時刻(UTC の Unix ミリ秒。解除の記録と、となりの台の回と比べる)。
    public DateTime StartedLocal, EndedLocal;
    public long EndedUnixMs;
    public string Station = "", Namespace = "", RunId = "";
    // 同じ来場者の練習を最後まで終えたあとの、最初の1曲か。
    public bool PracticedBefore;
    // 練習の矢印とロングが両方 ok・総仕上げが最初の型の1周以内だったか。
    public bool PracticePairBefore, PracticeAllFirstLoop;
    // タイトルの開始ノーツを斬り上げたか。TitleHand と同じ寿命。
    public bool TitleUpward;
    // この選曲で、その曲の裏を開く最後の切断より前に出したヒントの数。
    public int HintsBeforeOpen;
    // 選曲にかかった秒(残り時間の数え方)。判定調整から戻った選曲・分からないときは null(時間切れの自動開始は 100 秒)。
    public double? SelectionSeconds;
    // このモードで、今日(台の現地の日付)の clear の解除の数(2台合わせて、この回の分を入れる前)。分からなければ -1。
    public int ClearsToday = -1;
    // 同期の記録(この台と相手の台の回。この回はまだ入っていない)。
    public IReadOnlyList<RankingPlay> Plays = Array.Empty<RankingPlay>();
    // この回の clear の番号(○人目)。clear を解除したあとに入れる(キリ番・双子の素数・背番号)。
    public int ClearNumber;
    // 選曲で、この回の曲のジャケットを切って裏譜面を出したか(その選曲で開いたもの。判定調整から戻って解除し直した分は入らない)。
    public bool OpenedSecretHere;
    // タイトルの開始ノーツを切った手(同じ来場者の最初の本編の曲だけ。キーボード・マウスと、ほかの曲は Any)。
    public SaberHand TitleHand = SaberHand.Any;
    // 選曲に出る全曲(最後のピース。分からなければ空)。
    public IReadOnlyCollection<string> ListedSongs = Array.Empty<string>();
    // この回でほかに解除した実績と番号(初物・大収穫)。AchievementTracker.Record がほかを解除したあとに入れる。
    public IReadOnlyList<AchievementUnlock> Unlocked = Array.Empty<AchievementUnlock>();

    public bool Secret => RushESecretState.IsSecretDifficulty(Difficulty);
    public bool IsHard => string.Equals(Difficulty, AchievementCatalog.HardDifficulty, StringComparison.OrdinalIgnoreCase);
    public PlayRank Rank => PlayRankHelper.FromScore(Score);
    // ScoreManager.IsFullCombo・IsAllPerfect と同じ(避はトゲに当たると対象外)。
    public bool FullCombo => Hit > 0 && Bad == 0 && Miss == 0 && AimDamage == 0;
    public bool AllPerfect => FullCombo && Perfect == Hit;
    // ノーツを使う実績を判定できるか(最初から記録していて、照準の裏譜面でない)。
    public bool NoteStats => Stats != null && Stats.Complete && !AimMode;

    // その裏譜面(曲と難易度の文字)の回か。
    public bool Is(SecretChart chart) =>
        chart != null && SongId == chart.SongId && string.Equals(Difficulty, chart.Difficulty, StringComparison.OrdinalIgnoreCase);

    // 曲を始めてから終わるまでの間に、台の時計が hour:minute(の1分間)にかかったか。
    public bool Spans(int hour, int minute)
    {
        DateTime start = StartedLocal <= EndedLocal ? StartedLocal : EndedLocal;
        // 始めた時刻が入っていない・おかしいときも、1日より前は見ない(曲は数分)。
        if (start < EndedLocal.AddDays(-1)) start = EndedLocal.AddDays(-1);
        for (DateTime day = start.Date; day <= EndedLocal.Date; day = day.AddDays(1))
        {
            DateTime from = day.AddHours(hour).AddMinutes(minute), to = from.AddMinutes(1);
            if (start < to && EndedLocal >= from) return true;
        }
        return false;
    }

    // このモードで、となりの台(台名が自分と違う)が同じ曲(難易度は問わない)を、この回の終わりの前後 seconds 秒以内に終えていたか。
    // 届いている相手の回だけを見るので、取るのは結果があとに決まった方(終わりの演出の長さで、曲を先に終えた方があとになることがある)。
    public bool NeighborFinishedSameSong(double seconds)
    {
        if (Plays == null || string.IsNullOrEmpty(SongId)) return false;
        long window = (long)Math.Round(seconds * 1000.0);
        foreach (var play in Plays)
        {
            if (play == null || play.id == RunId || (play.ns ?? "") != (Namespace ?? "") || play.song != SongId) continue;
            if ((play.st ?? "") == (Station ?? "")) continue;
            if (play.at > 0 && Math.Abs(EndedUnixMs - play.at) <= window) return true;
        }
        return false;
    }

    // となりの回: このモードで台が自分と違い、終えた時刻がこの回の終わりの前後 seconds 秒以内の回のうち、差がいちばん小さい1回(無ければ null)。
    public RankingPlay Neighbor(double seconds)
    {
        if (Plays == null) return null;
        long window = (long)Math.Round(seconds * 1000.0), best = long.MaxValue;
        RankingPlay found = null;
        foreach (var play in Plays)
        {
            if (play == null || play.id == RunId || (play.ns ?? "") != (Namespace ?? "") || (play.st ?? "") == (Station ?? "") || play.at <= 0)
                continue;
            long gap = Math.Abs(EndedUnixMs - play.at);
            if (gap <= window && gap < best) { best = gap; found = play; }
        }
        return found;
    }

    // このモードで、今日(この回を終えた日。RankingPlay.date と同じ yyyy/MM/dd)に2台で終えた回(この回は入れない)の曲が、
    // 選曲に出る全曲のうちこの回の曲だけを欠いているか(難易度は問わない)。
    public bool CompletesTodaysSongs()
    {
        if (ListedSongs == null || Plays == null || string.IsNullOrEmpty(SongId)) return false;
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var played = new HashSet<string>(StringComparer.Ordinal);
        foreach (var play in Plays)
            if (play != null && play.id != RunId && (play.ns ?? "") == (Namespace ?? "") && play.date == today && play.song != null)
                played.Add(play.song);
        if (played.Contains(SongId)) return false;
        bool listed = false;
        foreach (string song in ListedSongs)
        {
            if (song == SongId) listed = true;
            else if (!played.Contains(song)) return false;
        }
        return listed;
    }

    // この台・このモードの直前の回が、指定の回数だけ同じ曲。時刻が同じなら回 ID の順。
    public bool PreviousSameSong(int previousCount, double seconds)
    {
        if (previousCount < 1 || Plays == null || string.IsNullOrEmpty(SongId)
            || string.IsNullOrEmpty(Station) || string.IsNullOrEmpty(RunId) || EndedUnixMs <= 0) return false;
        var previous = new List<RankingPlay>();
        foreach (var play in Plays)
        {
            if (play == null || string.IsNullOrEmpty(play.id) || play.id == RunId || play.at <= 0
                || (play.ns ?? "") != (Namespace ?? "") || play.st != Station) continue;
            if (play.at > EndedUnixMs || (play.at == EndedUnixMs && string.CompareOrdinal(play.id, RunId) >= 0)) continue;
            previous.Add(play);
        }
        previous.Sort((a, b) =>
        {
            int order = b.at.CompareTo(a.at);
            return order != 0 ? order : string.CompareOrdinal(b.id, a.id);
        });
        long window = (long)Math.Round(seconds * 1000.0);
        int count = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var play in previous)
        {
            if (!ids.Add(play.id)) continue;
            if (play.song != SongId || EndedUnixMs - play.at > window) return false;
            if (++count == previousCount) return true;
        }
        return false;
    }

    // 最後のピースの裏譜面版。曲と難易度が表に載っている組だけを数える。
    public bool CompletesTodaysSecrets()
    {
        var current = RushESecretState.ForDifficulty(Difficulty);
        if (current == null || !Is(current) || Plays == null) return false;
        string today = EndedLocal.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var play in Plays)
        {
            if (play == null || play.id == RunId || (play.ns ?? "") != (Namespace ?? "") || play.date != today) continue;
            var chart = RushESecretState.For(play.song);
            if (chart != null && string.Equals(chart.Difficulty, play.diff, StringComparison.OrdinalIgnoreCase))
                played.Add(chart.Difficulty);
        }
        if (played.Contains(current.Difficulty)) return false;
        foreach (var chart in RushESecretState.Charts)
            if (chart != current && !played.Contains(chart.Difficulty)) return false;
        return true;
    }

    // 赤・青(金は除く)がどちらも min 個以上で、色ごとに PERFECT の数 ÷ その色の数 ≥ rate。
    public bool BothHandsPerfectRate(int min, double rate)
    {
        if (!NoteStats) return false;
        int red = Stats.Count(n => n.Red), blue = Stats.Count(n => n.Blue);
        if (red < Math.Max(1, min) || blue < Math.Max(1, min)) return false;
        int redPerfect = Stats.Count(n => n.Red && n.Perfect), bluePerfect = Stats.Count(n => n.Blue && n.Perfect);
        // 割合の境目(6割ちょうど)を丸めで落とさない。
        return redPerfect + 1e-9 >= rate * red && bluePerfect + 1e-9 >= rate * blue;
    }
}
