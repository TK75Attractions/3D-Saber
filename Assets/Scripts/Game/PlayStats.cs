using System;
using System.Collections.Generic;
using UnityEngine;

// 実績(2026-10-29)の判定に使う、1回の演奏の記録。AchievementTracker が本編の曲ごとに作る(判定調整・練習では作らない)。
// ・ノーツの表は流している譜面(音源の長さで切り詰めたあとの CurrentChart)から作る。種類(色・矢印・ロング・金)と裏の印
//   (mirror・fall・vanish の hide・veil・wind・overtake・Q の試験用紙の間・最後のサビ)は譜面データから決め、コードに焼かない。
// ・結果はノーツ1つにつき最後の1つ。タップ・矢印は判定の段、ロングは最後まで切れたら PERFECT、残りが MISS になったら MISS。
// ・譜面の時刻 = time + offsetMs(人ごとの判定のずらしは入れない)。同時 = 1ms 以内(FinalNoteGroup.Tolerance)。
// ・連続 PERFECT は判定の並び(ロングの途中の回も数える。PERFECT 以外と MISS で 0 に戻す)。
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
        // 最後の結果(まだなら null)。
        public JudgmentTier? Result;
        internal bool Matched;

        public bool Red => Color == "red";
        public bool Blue => Color == "blue";
        public bool Cut => Result.HasValue && Result.Value != JudgmentTier.Miss;
        public bool Perfect => Result == JudgmentTier.Perfect;
        // BAD も MISS も無い(結果が無いものは含めない)。
        public bool Clean => Result.HasValue && Result.Value != JudgmentTier.Bad && Result.Value != JudgmentTier.Miss;
    }

    readonly List<Note> notes = new List<Note>();
    // HitTime の順に並べた添字(出たノーツを探す)。
    readonly List<int> byHitTime = new List<int>();
    readonly List<int> firstGroup = new List<int>(), finalGroup = new List<int>();
    int streak;

    // 出たノーツと結び付ける時刻・位置の許し。同じ式で求めるので、ふつうは差が 0。
    public const double MatchSeconds = 1e-6;
    public const float MatchDistance = 1e-3f;

    public string RunId { get; private set; } = "";
    public ChartData Chart { get; private set; }
    public IReadOnlyList<Note> Notes => notes;
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

    // hitOffsetSeconds は NoteSpawner.TotalOffsetSeconds(譜面の offsetMs と判定のずらし)、secondsPerLongCut は NoteSpawner.secondsPerLongCut。
    // stage は StagePerformanceTimeline.Load(曲, 難易度)(裏は stage_<難易度>.json が先)。null なら最後のサビは無い。
    public static PlayStats FromChart(ChartData chart, double hitOffsetSeconds, float secondsPerLongCut,
        StagePerformanceTimeline stage, string runId = null)
    {
        var stats = new PlayStats { RunId = runId ?? "", Chart = chart };
        if (chart?.notes == null) return stats;
        double offset = chart.offsetMs / 1000.0;
        float scale = chart.coordScale;
        bool fall = FallRules.Enabled(chart.fall);
        bool reverse = ReverseRules.Enabled(chart.reverse);
        var papers = chart.quiz != null ? chart.quiz.papers : null;
        bool chorus = TryLastChorus(stage, out double chorusStart, out double chorusEnd);
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
            };
            // 鏡は NoteSpawner.UsesReverse と同じ(鏡に見えるタップだけ)。
            note.Mirror = reverse && data.mirror && !data.hidden && !data.IsLong && !data.HasWind && !data.HasOvertake
                && !note.Fall && !LongSlideRules.Enabled(data) && vanish == VanishMode.None;
            note.Quiz = InPaper(papers, data.time);
            note.Chorus = chorus && note.Time >= chorusStart && note.Time < chorusEnd;
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
        }
        return stats;
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

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

// 本編の曲の間、出たノーツ・判定・コンボの節目を聞いて PlayStats に入れる(AchievementTracker が曲ごとに作り、場面を出たら外す)。
// 譜面が置き直された(NoteSpawner.OnChartReset)・回が変わったときは、次のノーツが出る前に記録を作り直す。
// ノーツごとの購読(OnJudged・OnMiss)は持っておき、作り直す・外すときに外す(使い回しのノーツは ResetForSpawn でも外れる)。
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

    struct Watched
    {
        public CuttableNote Note;
        public Action<CuttableNote, JudgmentTier, Vector3, Vector3> Judged;
        public Action<CuttableNote> Missed;
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
        Stats = PlayStats.FromChart(spawner.CurrentChart, spawner.TotalOffsetSeconds, spawner.secondsPerLongCut, stage, runId);
        Stats.Complete = complete;
        Stale = false;
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
            Judged = (cut, tier, point, velocity) => stats.Record(k, tier),
            Missed = missed => stats.Record(k, JudgmentTier.Miss),
        };
        note.OnJudged += item.Judged;
        note.OnMiss += item.Missed;
        watched.Add(item);
    }

    void Judged(JudgmentTier tier, int award) => Count(tier);

    void LongCut(JudgmentTier tier, int award, bool wrongFlick) => Count(tier);

    void Count(JudgmentTier tier)
    {
        judgedSinceReset++;
        if (!Stale) Stats.Judgment(tier);
    }

    // コンボの節目のくじ(GameplayFeedbackPresenter.LastMilestoneChoice は OnMilestone の直前に決まる)。
    void Milestone(int combo)
    {
        if (!Stale && feedback != null && feedback.LastMilestoneChoice.Jackpot) Stats.Jackpots++;
    }
}
