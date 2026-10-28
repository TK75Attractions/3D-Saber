using System;
using System.Collections.Generic;
using UnityEngine;

// 実績(2026-10-29)の判定に使う、1回の演奏の記録。AchievementTracker が本編の曲ごとに作る(判定調整・練習では作らない)。
// ・ノーツの表は流している譜面(音源の長さで切り詰めたあとの CurrentChart)から作る。種類(色・矢印・ロング・金)と裏の印
//   (mirror・fall・vanish の hide・veil・wind・overtake・Q の試験用紙の間・最後のサビ)は譜面データから決め、コードに焼かない。
// ・結果はノーツ1つにつき最後の1つ。タップ・矢印は判定の段、ロングは最後まで切れたら PERFECT、残りが MISS になったら MISS。
// ・譜面の時刻 = time + offsetMs(人ごとの判定のずらしは入れない)。同時 = 1ms 以内(FinalNoteGroup.Tolerance)。
// ・連続 PERFECT は判定の並び(ロングの途中の回も数える。PERFECT 以外と MISS で 0 に戻す)。
// ・第2弾(同日): ノーツごとの誤差・結果が出た時刻・切った手・向き違いの振り・反対の剣のしびれ、組(1ms 以内)、最後の組の前後の HUD の点、
//   消え方・背景の食・切る回数。止まり(S)・回転(R)・区間の名前(校歌の転調)は譜面と stage から求める。
public sealed class PlayStats
{
    public sealed class Note
    {
        public int Index;
        // 譜面の時刻(秒)と、ロングの長さを足した終わりの時刻。
        public double Time, EndTime;
        // 出たノーツ(CuttableNote.HitTime。判定のずらし込み)と着地点。出たノーツと結び付けるだけに使う。
        public double HitTime;
        public float X, Y;
        public string Color = "";
        public bool Arrow, Long, Gold;
        public bool Mirror, Fall, Hide, Veiled, Wind, Overtake, Quiz, Chorus;
        // 切る回数(ロングは NoteData.count)・消え方(V)・背景の食(AstralOrbit の背景だけ。EvaluateEclipse(譜面の時刻)、ほかは 0)・最後の組か。
        public int Cuts = 1;
        public VanishMode Vanish;
        public float Eclipse;
        public bool Final;
        // 最後の結果(まだなら null)。
        public JudgmentTier? Result;
        // 最後の結果の誤差(ミリ秒、負=早め。ScoreManager.LastErrorMs。タップ・矢印だけで、ロング・MISS は null)・結果が出た台の現地時刻
        // (MISS は null)・切った手(MISS・キーボードなどは Any)・切ったときに反対の手の剣がしびれていたか(T の闘牛)。
        public double? ErrorMs;
        public DateTime? JudgedAt;
        public SaberHand Hand = SaberHand.Any;
        public bool OtherStunned;
        // 結果が出る前に、向きの合わない振りの知らせ(CuttableNote.OnWrongDirection)が出たか。
        public bool WrongSwing;
        internal bool Matched;

        public bool Red => Color == "red";
        public bool Blue => Color == "blue";
        public bool Cut => Result.HasValue && Result.Value != JudgmentTier.Miss;
        public bool Perfect => Result == JudgmentTier.Perfect;
        // PERFECT・GREAT・GOOD。
        public bool Success => Result == JudgmentTier.Perfect || Result == JudgmentTier.Great || Result == JudgmentTier.Good;
        // BAD も MISS も無い(結果が無いものは含めない)。
        public bool Clean => Result.HasValue && Result.Value != JudgmentTier.Bad && Result.Value != JudgmentTier.Miss;
    }

    readonly List<Note> notes = new List<Note>();
    // HitTime の順に並べた添字(出たノーツを探す)。
    readonly List<int> byHitTime = new List<int>();
    readonly List<int> firstGroup = new List<int>(), finalGroup = new List<int>();
    // 組(譜面の時刻が組の最初のノーツから 1ms 以内)。譜面の時刻の順で、組の中も時刻の順。
    readonly List<List<int>> groups = new List<List<int>>();
    int streak;

    // 出たノーツと結び付ける時刻・位置の許し。同じ式で求めるので、ふつうは差が 0。
    public const double MatchSeconds = 1e-6;
    public const float MatchDistance = 1e-3f;

    public string RunId { get; private set; } = "";
    public ChartData Chart { get; private set; }
    // 演出の区間(区間の名前を見る)と、譜面の offsetMs(秒。譜面の時刻 = time + これ。速さの表・回転は time の時計)。
    public StagePerformanceTimeline Stage { get; private set; }
    public double OffsetSeconds { get; private set; }
    public IReadOnlyList<Note> Notes => notes;
    public IReadOnlyList<IReadOnlyList<int>> Groups => groups;
    // 最初のノーツが出る前から見ていたか。途中から見始めた回は、ノーツを使う実績の対象外にする(取りこぼしで誤って出さない)。
    public bool Complete { get; set; } = true;
    public int MaxPerfectStreak { get; private set; }
    // 曲の終わりに写す値(PlayStatsRecorder.Snapshot)。
    public int CrossCuts { get; set; }
    public int FinalCombo { get; set; }
    public int VoltageMax { get; set; }
    public int Jackpots { get; set; }
    public int Stuns { get; set; }
    public bool HasBull { get; set; }
    // 曲が鳴り始めた時刻と、曲が終わった時刻(終わりの演出の前。台の現地時刻)。分からなければ null。
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    // いちばん早いノーツの組と、いちばん遅く終わるノーツの組(どちらも 1ms 以内は全部)。
    public IReadOnlyList<int> FirstGroup => firstGroup;
    public IReadOnlyList<int> FinalGroup => finalGroup;
    // 最後の組の最初の判定(ロングは1回目の切り)の直前と、最後の判定の直後の HUD の点(ScoreManager.ScoreWithCombo)。まだなら null。
    public int? FinalScoreBefore { get; private set; }
    public int? FinalScoreAfter { get; private set; }

    // hitOffsetSeconds は NoteSpawner.TotalOffsetSeconds(譜面の offsetMs と判定のずらし)、secondsPerLongCut は NoteSpawner.secondsPerLongCut。
    // stage は StagePerformanceTimeline.Load(曲, 難易度)(裏は stage_<難易度>.json が先)。null なら最後のサビは無い。
    // theme は背景(本編と同じ StageThemeCatalog.ForSong)。省略すると stage の stageTheme。食は AstralOrbit の背景だけ。
    public static PlayStats FromChart(ChartData chart, double hitOffsetSeconds, float secondsPerLongCut,
        StagePerformanceTimeline stage, string runId = null, StageTheme? theme = null)
    {
        var stats = new PlayStats { RunId = runId ?? "", Chart = chart, Stage = stage };
        if (chart?.notes == null) return stats;
        double offset = chart.offsetMs / 1000.0;
        stats.OffsetSeconds = offset;
        float scale = chart.coordScale;
        bool fall = FallRules.Enabled(chart.fall);
        bool reverse = ReverseRules.Enabled(chart.reverse);
        var papers = chart.quiz != null ? chart.quiz.papers : null;
        bool chorus = TryLastChorus(stage, out double chorusStart, out double chorusEnd);
        bool eclipse = stage != null && (theme ?? ThemeOf(stage)) == StageTheme.AstralOrbit;
        for (int i = 0; i < chart.notes.Count; i++)
        {
            var data = chart.notes[i];
            if (data == null) continue;
            var vanish = VanishRules.Parse(data.vanish);
            var note = new Note
            {
                Index = i,
                Time = data.TimeSeconds + offset,
                EndTime = data.TimeSeconds + offset + data.LingerSeconds(secondsPerLongCut),
                HitTime = data.TimeSeconds + hitOffsetSeconds,
                X = data.x * scale,
                Y = data.y * scale,
                Color = data.color ?? "",
                Arrow = data.IsDirection,
                Long = data.count > 1,
                Gold = string.Equals(data.color, "gold", StringComparison.OrdinalIgnoreCase),
                Fall = fall && data.fall,
                Hide = vanish == VanishMode.Hide,
                Veiled = data.veil > 0f,
                Wind = data.HasWind,
                Overtake = data.HasOvertake,
                Cuts = Math.Max(1, data.count),
                Vanish = vanish,
            };
            // 鏡は NoteSpawner.UsesReverse と同じ(鏡に見えるタップだけ)。
            note.Mirror = reverse && data.mirror && !data.hidden && !data.IsLong && !data.HasWind && !data.HasOvertake
                && !note.Fall && !LongSlideRules.Enabled(data) && vanish == VanishMode.None;
            note.Quiz = InPaper(papers, data.time);
            note.Chorus = chorus && note.Time >= chorusStart && note.Time < chorusEnd;
            note.Eclipse = eclipse ? stage.EvaluateEclipse(note.Time) : 0f;
            stats.notes.Add(note);
        }
        for (int k = 0; k < stats.notes.Count; k++) stats.byHitTime.Add(k);
        stats.byHitTime.Sort((a, b) =>
        {
            int c = stats.notes[a].HitTime.CompareTo(stats.notes[b].HitTime);
            return c != 0 ? c : a.CompareTo(b);
        });
        if (stats.notes.Count > 0)
        {
            double first = double.PositiveInfinity;
            foreach (var note in stats.notes) first = Math.Min(first, note.Time);
            for (int k = 0; k < stats.notes.Count; k++)
                if (stats.notes[k].Time - first <= FinalNoteGroup.Tolerance + 1e-9) stats.firstGroup.Add(k);
            // 最後の組は本編の締めの演出と同じ選び方(FinalNoteGroup。譜面の時刻+ロングの長さ)。
            var final = new FinalNoteGroup(chart, offset, secondsPerLongCut);
            var position = new Dictionary<int, int>();
            for (int k = 0; k < stats.notes.Count; k++) position[stats.notes[k].Index] = k;
            foreach (int index in final.Indices)
                if (position.TryGetValue(index, out int k)) stats.finalGroup.Add(k);
            foreach (int k in stats.finalGroup) stats.notes[k].Final = true;
            foreach (int k in stats.TimeOrder())
            {
                var last = stats.groups.Count > 0 ? stats.groups[stats.groups.Count - 1] : null;
                if (last != null && stats.notes[k].Time - stats.notes[last[0]].Time <= FinalNoteGroup.Tolerance + 1e-9) last.Add(k);
                else stats.groups.Add(new List<int> { k });
            }
        }
        return stats;
    }

    static StageTheme ThemeOf(StagePerformanceTimeline stage) =>
        Enum.TryParse(stage.stageTheme, true, out StageTheme theme) && StageThemeCatalog.IsDefined(theme) ? theme : StageTheme.ObsidianRelay;

    // ノーツの添字を譜面の時刻の順に(同じ時刻は譜面の順)。
    List<int> TimeOrder()
    {
        var order = new List<int>(notes.Count);
        for (int k = 0; k < notes.Count; k++) order.Add(k);
        order.Sort((a, b) =>
        {
            int c = notes[a].Time.CompareTo(notes[b].Time);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    // stage の強い区間(intensity ≥ StagePerformanceTimeline.StrongIntensity)のうち、始まりがいちばん遅い区間 [start, end)。
    public static bool TryLastChorus(StagePerformanceTimeline stage, out double start, out double end)
    {
        start = end = 0;
        bool found = false;
        if (stage?.sections == null) return false;
        foreach (var section in stage.sections)
        {
            if (section == null || !Finite(section.startSeconds) || !Finite(section.endSeconds) || !Finite(section.intensity)
                || section.startSeconds < 0 || section.endSeconds <= section.startSeconds
                || section.intensity < StagePerformanceTimeline.StrongIntensity) continue;
            if (found && (section.startSeconds < start || (section.startSeconds == start && section.endSeconds <= end))) continue;
            start = section.startSeconds;
            end = section.endSeconds;
            found = true;
        }
        return found;
    }

    // Q の試験用紙の時間帯 [startMs, endMs)(ノーツの time と同じ時計)にあるか。
    static bool InPaper(List<QuizPaperData> papers, float timeMs)
    {
        if (papers == null) return false;
        foreach (var paper in papers)
            if (paper != null && timeMs >= paper.startMs && timeMs < paper.endMs) return true;
        return false;
    }

    // 出たノーツ(HitTime と着地点)に当たる譜面のノーツを探し、使ったことにする。見つからなければ -1。
    // 同じ時刻のノーツは着地点で見分け、合わなければ、まだ使っていない同じ時刻のものを使う。
    public int Match(double hitTime, float x, float y)
    {
        int lo = 0, hi = byHitTime.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (notes[byHitTime[mid]].HitTime < hitTime - MatchSeconds) lo = mid + 1; else hi = mid;
        }
        int fallback = -1;
        for (int k = lo; k < byHitTime.Count; k++)
        {
            var note = notes[byHitTime[k]];
            if (note.HitTime > hitTime + MatchSeconds) break;
            if (note.Matched) continue;
            if (Math.Abs(note.X - x) <= MatchDistance && Math.Abs(note.Y - y) <= MatchDistance)
            {
                note.Matched = true;
                return byHitTime[k];
            }
            if (fallback < 0) fallback = byHitTime[k];
        }
        if (fallback >= 0) notes[fallback].Matched = true;
        return fallback;
    }

    // ノーツの結果を入れる(あとの結果で上書きする)。k は Notes の添字。
    public void Record(int k, JudgmentTier tier)
    {
        if (k >= 0 && k < notes.Count) notes[k].Result = tier;
    }

    // 結果と、そのときの誤差・時刻・切った手・反対の手のしびれ(PlayStatsRecorder。あとの結果で全部上書きする)。
    public void Record(int k, JudgmentTier tier, double? errorMs, DateTime? judgedAt, SaberHand hand, bool otherStunned)
    {
        if (k < 0 || k >= notes.Count) return;
        var note = notes[k];
        note.Result = tier;
        note.ErrorMs = errorMs;
        note.JudgedAt = judgedAt;
        note.Hand = hand;
        note.OtherStunned = otherStunned;
    }

    // 向きの合わない振りの知らせ(結果が出たあとのものは数えない)。
    public void MarkWrongSwing(int k)
    {
        if (k >= 0 && k < notes.Count && !notes[k].Result.HasValue) notes[k].WrongSwing = true;
    }

    // 最後の組の判定1つ(before・after はその判定の直前・直後の HUD の点)。最初の判定の直前と、最後の判定の直後を残す。
    public void FinalJudgment(int before, int after)
    {
        if (!FinalScoreBefore.HasValue) FinalScoreBefore = before;
        FinalScoreAfter = after;
    }

    // 判定の並び(ScoreManager.OnJudgment と、ロングの途中の OnLongCut)。
    public void Judgment(JudgmentTier tier)
    {
        streak = tier == JudgmentTier.Perfect ? streak + 1 : 0;
        if (streak > MaxPerfectStreak) MaxPerfectStreak = streak;
    }

    // 条件に合うノーツの数。
    public int Count(Func<Note, bool> filter)
    {
        int count = 0;
        foreach (var note in notes) if (filter(note)) count++;
        return count;
    }

    // 条件に合うノーツのうち、MISS 以外の結果になった数。
    public int CutCount(Func<Note, bool> filter)
    {
        int count = 0;
        foreach (var note in notes) if (filter(note) && note.Cut) count++;
        return count;
    }

    // 条件に合うノーツが min 個以上あり、どれも MISS 以外(BAD はよい)。結果の無いノーツがあれば false。
    public bool AllCut(Func<Note, bool> filter, int min)
    {
        int count = 0;
        foreach (var note in notes)
        {
            if (!filter(note)) continue;
            if (!note.Cut) return false;
            count++;
        }
        return count >= Math.Max(1, min);
    }

    // 条件に合うノーツが min 個以上あり、どれも BAD・MISS でない。
    public bool AllClean(Func<Note, bool> filter, int min)
    {
        int count = 0;
        foreach (var note in notes)
        {
            if (!filter(note)) continue;
            if (!note.Clean) return false;
            count++;
        }
        return count >= Math.Max(1, min);
    }

    // 組(FirstGroup・FinalGroup)が空でなく、どれも PERFECT。
    public bool AllPerfect(IReadOnlyList<int> group)
    {
        if (group == null || group.Count == 0) return false;
        foreach (int k in group)
            if (k < 0 || k >= notes.Count || !notes[k].Perfect) return false;
        return true;
    }

    // 条件に合うノーツが min 個以上あり、どれも PERFECT。
    public bool AllPerfect(Func<Note, bool> filter, int min)
    {
        int count = 0;
        foreach (var note in notes)
        {
            if (!filter(note)) continue;
            if (!note.Perfect) return false;
            count++;
        }
        return count >= Math.Max(1, min);
    }

    // 並び(組・窓)が空でなく、どれも BAD・MISS でない。
    public bool AllClean(IReadOnlyList<int> group)
    {
        if (group == null || group.Count == 0) return false;
        foreach (int k in group)
            if (k < 0 || k >= notes.Count || !notes[k].Clean) return false;
        return true;
    }

    // ---- 組と場面(第2弾) ----

    // 譜面の時刻 time より後(inclusive なら time ちょうども)の最初の組。無ければ null。
    public IReadOnlyList<int> GroupFrom(double time, bool inclusive)
    {
        foreach (var group in groups)
        {
            double at = notes[group[0]].Time;
            if (inclusive ? at >= time - 1e-9 : at > time + 1e-9) return group;
        }
        return null;
    }

    // 譜面の時刻 time にいちばん近い組(差 maxSeconds 以内。同じ差なら早い方)。無ければ null。
    public IReadOnlyList<int> NearestGroup(double time, double maxSeconds)
    {
        IReadOnlyList<int> best = null;
        double gap = double.PositiveInfinity;
        foreach (var group in groups)
        {
            double d = Math.Abs(notes[group[0]].Time - time);
            if (d < gap) { gap = d; best = group; }
        }
        return gap <= maxSeconds + 1e-9 ? best : null;
    }

    // 流れる速さ(chart.scroll。NoteSpawner と同じく、降る譜面では使わない)が minSeconds 以上 0 のままの区間それぞれの、
    // 止まりが終わったあと最初の組(ノーツをまとめて)。そういう区間が無いか、どれかの区間のあとに組が無ければ
    // (音源で切り詰めた譜面など。場面がそろっていない)空。
    public List<int> FirstGroupsAfterStops(double minSeconds)
    {
        var result = new List<int>();
        if (Chart == null || FallRules.Enabled(Chart.fall)) return result;
        var map = ScrollSpeedMap.Build(Chart.scroll);
        if (map == null) return result;
        foreach (var stop in map.Stops())
        {
            if (double.IsInfinity(stop.End) || stop.End - stop.Start < minSeconds - 1e-6) continue;
            var group = GroupFrom(stop.End + OffsetSeconds, true);
            if (group == null || group.Count == 0) return new List<int>();
            AddGroup(result, group);
        }
        return result;
    }

    // 最初の回転(chart.reverse.turns)の始まりの時刻にある組(無ければいちばん近い組で、差 nearSeconds 以内)と、
    // 回転が終わったあとの最初の afterGroups 組。回転が無いか、始まりの組・あとの組がそろわなければ(音源で切り詰めた譜面など)空。
    public List<int> AroundFirstTurn(double nearSeconds, int afterGroups)
    {
        var result = new List<int>();
        var reverse = Chart != null ? Chart.reverse : null;
        if (!ReverseRules.Enabled(reverse) || reverse.turns == null) return result;
        ReverseTurnData first = null;
        foreach (var turn in reverse.turns)
            if (turn != null && Finite(turn.time) && Finite(turn.endTime) && (first == null || turn.time < first.time)) first = turn;
        if (first == null) return result;
        var start = NearestGroup(first.time / 1000.0 + OffsetSeconds, nearSeconds);
        if (start == null || start.Count == 0) return result;
        AddGroup(result, start);
        double end = Math.Max(first.time, first.endTime) / 1000.0 + OffsetSeconds;
        int taken = 0;
        foreach (var group in groups)
        {
            if (taken >= afterGroups) break;
            if (notes[group[0]].Time < end - 1e-9 || result.Contains(group[0])) continue;
            AddGroup(result, group);
            taken++;
        }
        return taken < afterGroups ? new List<int>() : result;
    }

    // stage の区間のうち名前(label)が prefix で始まるもの(始まりがいちばん早い区間)の、始まりの leadSeconds 前より後の最初の組。
    public IReadOnlyList<int> GroupAfterSection(string prefix, double leadSeconds)
    {
        if (Stage?.sections == null || string.IsNullOrEmpty(prefix)) return null;
        double start = double.PositiveInfinity;
        foreach (var section in Stage.sections)
            if (section != null && section.label != null && section.label.StartsWith(prefix, StringComparison.Ordinal)
                && Finite(section.startSeconds) && section.startSeconds < start) start = section.startSeconds;
        return double.IsInfinity(start) ? null : GroupFrom(start - leadSeconds, false);
    }

    // 各ノーツの譜面の時刻 t を頭にした [t, t+seconds) に入るノーツがいちばん多い窓(同じ数なら早い方)のノーツ。
    public List<int> DensestWindow(double seconds)
    {
        var order = TimeOrder();
        int from = -1, count = 0;
        for (int a = 0, b = 0; a < order.Count; a++)
        {
            if (b < a) b = a;
            while (b < order.Count && notes[order[b]].Time < notes[order[a]].Time + seconds) b++;
            if (b - a > count) { count = b - a; from = a; }
        }
        return from < 0 ? new List<int>() : order.GetRange(from, count);
    }

    // 2番目からの組それぞれで、それより前の全ノーツの終わり(ロングは終わりの時刻)の最大から、その組の時刻までの間を測り、
    // いちばん長い間(同じなら早い方)のあとの組。rest にその間(組が1つ以下なら null で -∞)。
    public IReadOnlyList<int> AfterLongestRest(out double rest)
    {
        rest = double.NegativeInfinity;
        IReadOnlyList<int> best = null;
        double end = double.NegativeInfinity;
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                double gap = notes[groups[i][0]].Time - end;
                if (gap > rest) { rest = gap; best = groups[i]; }
            }
            foreach (int k in groups[i]) end = Math.Max(end, notes[k].EndTime);
        }
        return best;
    }

    // 譜面の順で、1個だけの組の赤・青のタップ(金・矢印・ロングでない)が、赤青交互にどれも成功で続いたいちばん長い数。
    public int LongestRedBlueRelay()
    {
        int best = 0, run = 0;
        string last = null;
        foreach (var group in groups)
        {
            var note = group.Count == 1 ? notes[group[0]] : null;
            if (note != null && (note.Red || note.Blue) && !note.Gold && !note.Arrow && !note.Long && note.Success)
            {
                run = run > 0 && note.Color != last ? run + 1 : 1;
                last = note.Color;
                if (run > best) best = run;
            }
            else { run = 0; last = null; }
        }
        return best;
    }

    // ずれ方の癖: 誤差のある判定(タップ・矢印)が minJudged 個以上、そのうち PERFECT 以外が minOff 個以上で、
    // PERFECT 以外の誤差がすべて sign の向き(負は早め、正は遅め。0 はどちらでもない)。
    public bool OffBeatsLean(int sign, int minJudged, int minOff)
    {
        int judged = 0, off = 0;
        foreach (var note in notes)
        {
            if (!note.ErrorMs.HasValue || !note.Cut) continue;
            judged++;
            if (note.Perfect) continue;
            off++;
            if (sign < 0 ? !(note.ErrorMs.Value < 0) : !(note.ErrorMs.Value > 0)) return false;
        }
        return judged >= Math.Max(1, minJudged) && off >= Math.Max(1, minOff);
    }

    static void AddGroup(List<int> result, IReadOnlyList<int> group)
    {
        if (group == null) return;
        foreach (int k in group) if (!result.Contains(k)) result.Add(k);
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

// 本編の曲の間、出たノーツ・判定・コンボの節目を聞いて PlayStats に入れる(AchievementTracker が曲ごとに作り、場面を出たら外す)。
// 譜面が置き直された(NoteSpawner.OnChartReset)・回が変わったときは、次のノーツが出る前に記録を作り直す。
// ノーツごとの購読(OnJudged・OnMiss・OnWrongDirection、最後の組は OnPartialCut も)は持っておき、作り直す・外すときに外す
// (使い回しのノーツは ResetForSpawn でも外れる)。ノーツの OnJudged は ScoreManager が採点して OnJudgment を出したあとに来るので、
// そこで読む誤差(LastErrorMs)と HUD の点(ScoreWithCombo)はその判定のもの。ロングの途中の切りも ScoreManager が先に数える
// (ScoreManager は GamePlayManager が譜面を渡したすぐあとに結び付け、記録はそのあとで結び付く)。
public sealed class PlayStatsRecorder
{
    readonly GamePlayManager manager;
    readonly NoteSpawner spawner;
    readonly ScoreManager score;
    readonly GameplayFeedbackPresenter feedback;
    readonly List<Watched> watched = new List<Watched>();
    bool bound;
    // 譜面が置き直されてから聞いた判定の数(作り直した記録が、最初から見ていたかを決める)。
    int judgedSinceReset;
    // 直近の判定(OnJudgment・OnLongCut)の直前と直後の HUD の点。
    int scoreBefore, scoreAfter;

    struct Watched
    {
        public CuttableNote Note;
        public Action<CuttableNote, JudgmentTier, Vector3, Vector3> Judged;
        public Action<CuttableNote> Missed, Wrong;
        public Action<CuttableNote, int, int> Partial;
    }

    public PlayStats Stats { get; private set; }
    // 譜面が置き直され、まだ作り直していない。
    public bool Stale { get; private set; }

    PlayStatsRecorder(GamePlayManager owner)
    {
        manager = owner;
        spawner = owner.noteSpawner;
        score = owner.scoreManager;
        feedback = UnityEngine.Object.FindFirstObjectByType<GameplayFeedbackPresenter>();
    }

    // 曲の準備(GamePlayManager が譜面を NoteSpawner に渡し、採点を結び付けたあと)ができていれば結び付ける。まだなら null。
    public static PlayStatsRecorder TryBind(GamePlayManager manager, string runId)
    {
        if (manager == null || manager.noteSpawner == null || manager.scoreManager == null || manager.noteSpawner.CurrentChart == null)
            return null;
        var recorder = new PlayStatsRecorder(manager);
        recorder.Rebuild(runId);
        recorder.Hook();
        return recorder;
    }

    public bool BoundTo(GamePlayManager owner) => bound && owner == manager;

    // 今の譜面とその回で記録を作り直す(前の結果・連続 PERFECT・くじの数は持ち越さない)。もう出た・判定したノーツがあれば、
    // 途中から見始めた回(ノーツを使う実績は出さない)。
    public void Rebuild(string runId) =>
        Rebuild(runId, spawner.NextIndex == 0 && spawner.AliveCount == 0 && score.JudgedNoteCount == 0);

    void Rebuild(string runId, bool complete)
    {
        Unwatch();
        var stage = StagePerformanceTimeline.Load(GameSession.SelectedSongId, GameSession.SelectedDifficulty);
        Stats = PlayStats.FromChart(spawner.CurrentChart, spawner.TotalOffsetSeconds, spawner.secondsPerLongCut, stage, runId,
            StageThemeCatalog.ForSong(GameSession.SelectedSongId));
        Stats.Complete = complete;
        Stale = false;
        scoreBefore = scoreAfter = score.ScoreWithCombo;
    }

    void Hook()
    {
        bound = true;
        spawner.OnNoteSpawned += Spawned;
        spawner.OnChartReset += ChartReset;
        foreach (var note in spawner.LiveNotes) Spawned(note);
        score.OnJudgment += Judged;
        score.OnLongCut += LongCut;
        if (feedback != null) feedback.OnMilestone += Milestone;
    }

    public void Unbind()
    {
        if (!bound) return;
        bound = false;
        Unwatch();
        if (spawner != null) { spawner.OnNoteSpawned -= Spawned; spawner.OnChartReset -= ChartReset; }
        if (score != null) { score.OnJudgment -= Judged; score.OnLongCut -= LongCut; }
        if (feedback != null) feedback.OnMilestone -= Milestone;
    }

    void Unwatch()
    {
        foreach (var item in watched)
        {
            if ((object)item.Note == null) continue;
            item.Note.OnJudged -= item.Judged;
            item.Note.OnMiss -= item.Missed;
            item.Note.OnWrongDirection -= item.Wrong;
            if (item.Partial != null) item.Note.OnPartialCut -= item.Partial;
        }
        watched.Clear();
    }

    // SetChart の始め(まだ前の譜面のまま)。出ていたノーツの購読を外し、次のノーツが出る前に作り直す。
    void ChartReset()
    {
        Unwatch();
        Stale = true;
        judgedSinceReset = 0;
    }

    // 毎コマ。置き直された譜面が入ったら作り直し、曲が鳴り始めた時刻と、曲が終わった時刻(止めたコマ)を1回だけ覚える。
    public void Tick()
    {
        if (!bound) return;
        if (Stale && spawner != null && spawner.CurrentChart != null)
            Rebuild(GameSession.AchievementRunId, judgedSinceReset == 0 && spawner.AliveCount == 0);
        var song = manager != null ? manager.songPlayer : null;
        if (song == null) return;
        if (!Stats.StartedAt.HasValue) { if (song.IsPlaying) Stats.StartedAt = DateTime.Now; }
        else if (!Stats.EndedAt.HasValue && !song.IsScheduled) Stats.EndedAt = DateTime.Now;
    }

    // 結果が決まったとき(GameSession.RunRecording)に、曲の終わりの値を写す。
    public PlayStats Snapshot()
    {
        if (score != null)
        {
            Stats.CrossCuts = score.CrossCutCount;
            Stats.FinalCombo = score.Combo;
        }
        if (manager != null)
        {
            var excitement = manager.GetComponent<PlayExcitement>();
            Stats.VoltageMax = excitement != null && excitement.Meter != null ? excitement.Meter.MaxLevelReached : 0;
            var bull = manager.BullMode;
            Stats.HasBull = bull != null;
            Stats.Stuns = bull != null ? bull.StunCount : 0;
        }
        return Stats;
    }

    void Spawned(CuttableNote note)
    {
        if (note == null || note.IsDecoy) return;
        // 置き直された譜面の最初のノーツ(このノーツだけが出ている)なら、ここで作り直してから数える。
        if (Stale) Rebuild(GameSession.AchievementRunId, judgedSinceReset == 0 && spawner.AliveCount <= 1);
        var stats = Stats;
        int k = stats.Match(note.HitTime, note.LandingPosition.x, note.LandingPosition.y);
        if (k < 0) return;
        var item = new Watched
        {
            Note = note,
            Judged = (cut, tier, point, velocity) => NoteJudged(stats, k, cut, tier),
            Missed = missed => NoteMissed(stats, k),
            Wrong = wrong => stats.MarkWrongSwing(k),
        };
        // 最後の組のロングは、途中の切りも土壇場の一太刀の判定に数える(最後の1回は OnJudged で)。
        if (stats.Notes[k].Final)
            item.Partial = (cut, index, total) => { if (index < total - 1) stats.FinalJudgment(scoreBefore, scoreAfter); };
        note.OnJudged += item.Judged;
        note.OnMiss += item.Missed;
        note.OnWrongDirection += item.Wrong;
        if (item.Partial != null) note.OnPartialCut += item.Partial;
        watched.Add(item);
    }

    // ノーツの結果。誤差はタップ・矢印の判定だけ(ロング・MISS では ScoreManager が無効にしている)。
    void NoteJudged(PlayStats stats, int k, CuttableNote cut, JudgmentTier tier)
    {
        bool hit = tier != JudgmentTier.Miss;
        var hand = hit && cut != null ? cut.LastCutterHand : SaberHand.Any;
        stats.Record(k, tier, hit && score.LastErrorValid ? score.LastErrorMs : (double?)null, hit ? DateTime.Now : (DateTime?)null,
            hand, hit && OtherHandStunned(cut, hand));
        if (stats.Notes[k].Final) stats.FinalJudgment(scoreBefore, scoreAfter);
    }

    // 1回も切れずに期限が切れた。MISS の採点(OnJudgment)はこのあとに来るが、HUD の点は変わらない。
    void NoteMissed(PlayStats stats, int k)
    {
        stats.Record(k, JudgmentTier.Miss, null, null, SaberHand.Any, false);
        int now = score.ScoreWithCombo;
        if (stats.Notes[k].Final) stats.FinalJudgment(now, now);
    }

    // 切った手と反対の剣がしびれていたか(T の闘牛。切った曲時計で見る。時計が無ければ今のしびれ)。
    bool OtherHandStunned(CuttableNote cut, SaberHand hand)
    {
        if ((hand != SaberHand.Left && hand != SaberHand.Right) || manager == null) return false;
        var bull = manager.BullMode;
        if (bull == null || bull.Session == null) return false;
        var other = hand == SaberHand.Left ? SaberHand.Right : SaberHand.Left;
        return cut != null && cut.LastCutSongTime.HasValue ? bull.Session.IsStunned(other, cut.LastCutSongTime.Value) : bull.IsStunned(other);
    }

    void Judged(JudgmentTier tier, int award) => Count(tier);

    void LongCut(JudgmentTier tier, int award, bool wrongFlick) => Count(tier);

    void Count(JudgmentTier tier)
    {
        judgedSinceReset++;
        scoreBefore = scoreAfter;
        scoreAfter = score.ScoreWithCombo;
        if (!Stale) Stats.Judgment(tier);
    }

    // コンボの節目のくじ(GameplayFeedbackPresenter.LastMilestoneChoice は OnMilestone の直前に決まる)。
    void Milestone(int combo)
    {
        if (!Stale && feedback != null && feedback.LastMilestoneChoice.Jackpot) Stats.Jackpots++;
    }
}
