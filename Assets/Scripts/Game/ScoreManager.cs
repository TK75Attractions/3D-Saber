using System.Collections.Generic;
using UnityEngine;

// 満点は全曲 100000(2026-10-20 ユーザー指定)。ノーツで 90000、最大コンボの割合で 10000。
// 各ノーツの点は譜面から自動で決まる: 1回切るごとの重みは普通 1・金 2、ロングは切る回数ぶん数える。
// ノーツの点 = 取った重み×判定の基礎点(Perfect 300 / Great 200 / Good 100 / Bad 50)÷(全体の重み×300)×90000 の切り捨て。
// ロングは1回切るごとに判定1つ・コンボ +1。1回でも残すと残りの回を MISS にしてコンボを切る(切った回の点は残す)。
public class ScoreManager : MonoBehaviour
{
    public SongPlayer songPlayer;
    public const int MaxScore = 100000;
    public const int NoteScore = 90000;
    public const int ComboScore = MaxScore - NoteScore;

    public int Score { get; private set; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    // コンボ点(最大コンボ ÷ 満コンボ × 10000 の切り捨て)。曲の終わりの FinalizeScore で一度だけ Score に足す。
    // 照準の裏譜面(避)にコンボ点は無い。
    public int ComboBonus => AimScoring ? 0 : IsFinalized ? finalComboBonus : (int)((long)ComboScore * MaxCombo / Mathf.Max(1, ComboUnits));
    // ランクと HUD 用。曲の途中でも「今の最大コンボで終わったときの点」。
    public int ScoreWithCombo => IsFinalized ? Score : Score + ComboBonus;
    // 満コンボ(=判定の数。ロングは切る回数)。譜面が分からないときは判定済みの数。避は的の数。
    public int ComboUnits { get { if (AimScoring) return Mathf.Max(aimTargetCount, HitCount + MissCount); RefreshTotals(); return Mathf.Max(chartComboUnits, HitCount + MissCount); } }

    // ---- 照準の裏譜面(Phobia の「避」、2026-10-24) ----
    // ノーツの代わりに AimDodgeMode が的とトゲを知らせる。点は 50000 から始まり、トゲに当たると 1000 減り(0 より下げない)、
    // 的は全部で最大 50000(AimDodgeRules)。的の BULL / INNER / OUTER / MISS は PERFECT / GREAT / GOOD / MISS の数に入れ、
    // 記録・実績・順位の形を通常の譜面とそろえる。コンボは外さなかった的の続き(トゲに当たっても切れる)。
    public bool AimScoring { get; private set; }
    public int AimDamageCount { get; private set; }
    public int AimDartPoints { get; private set; }
    public int AimDartTotal { get; private set; }
    // トゲに当たった(減った点)。HUD の「DAMAGE」の表示に使う。
    public event System.Action<int> OnAimDamage;
    int aimTargetCount;
    // 判定し終えたノーツの数(ロングも1つ)。曲の終わりの確認に使う。
    public int JudgedNoteCount { get; private set; }
    public bool IsFinalized { get; private set; }
    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public int PerfectCount { get; private set; }
    public int GreatCount { get; private set; }
    public int GoodCount { get; private set; }
    public int BadCount { get; private set; }
    // 実際にノーツを切った回数(判定の良し悪しと別)。ロングの途中の切りも数え、期限切れは数えない。
    // 1回も切っていない回を記録に残さないために使う(仕様面の点検 1)。点数・判定・コンボには関係しない。
    public int CutCount { get; private set; }
    // 空譜面を達成扱いにしない。Good はコンボ継続、Bad / Miss は達成対象外。避はトゲに1回でも当たると対象外。
    public bool IsFullCombo => HitCount > 0 && BadCount == 0 && MissCount == 0 && AimDamageCount == 0;
    public bool IsAllPerfect => IsFullCombo && PerfectCount == HitCount;
    public JudgmentTier LastTier { get; private set; } = JudgmentTier.Miss;
    // 直近判定のカットを行った手(ノーツ経由のカットのみ。Miss や直接 RegisterHit では Any)。
    // HapticFeedback が「切った手のデバイスだけ振動」させるのに使う。
    public SaberHand LastCutHand { get; private set; } = SaberHand.Any;
    // 直近判定が金ノーツのカットか。JudgmentSfx が通常カット音を重ねない判定に使う。
    public bool LastCutWasGold { get; private set; }
    // 効果音が判定の良し悪しとノーツ種別を区別するための情報。
    public CutDirection LastCutDirection { get; private set; } = CutDirection.None;
    public int LastCutCount { get; private set; } = 1;
    public bool LastCutTimedOut { get; private set; }
    // 直近判定の時間誤差(ミリ秒、負=早い/正=遅い)。タップ/フリックのみ有効。
    // ロング(切れたかどうかで採点)と Miss では LastErrorValid=false になる。HUD の EARLY/LATE 表示用。
    public double LastErrorMs { get; private set; }
    public bool LastErrorValid { get; private set; }
    // 左右同時の「X 斬り」(派手さカタログ 斬3、2026-10-20)。譜面上同時の、違う手のノーツを、両方 Great 以上で、
    // 切った時刻の差 CrossCutWindowSeconds 以内に切ったとき、2つ目の判定で立てる(普通のノーツだけ。金・ロングは除く)。
    // カタログの 35ms はカメラの1コマ(約33ms)の差で外れるので 50ms にした。点数・コンボには関係しない。
    public const double CrossCutWindowSeconds = .05;
    public bool LastCutWasCross { get; private set; }
    public int CrossCutCount { get; private set; }
    // (1つ目の切った位置, 2つ目の切った位置)。2つ目の OnJudgment より先に知らせる。
    public event System.Action<Vector3, Vector3> OnCrossCut;
    struct CrossCandidate { public bool valid; public SaberHand hand; public double hitTime, cutTime; public Vector3 point; }
    CrossCandidate crossCandidate;

    public event System.Action<JudgmentTier, int> OnJudgment; // (tier, awarded score this judgment)
    // (tier, awarded, wasWrongFlick)。wasWrongFlick は向き違いの1段下げの印(HUD の ! FLICK)だったが、
    // 2026-10-22 から向きの合わない振りでは切らないので、いつも false。
    public event System.Action<JudgmentTier, int, bool> OnJudgmentEx;
    // ロングの途中の1回(tier, awarded, wasWrongFlick)。コンボと判定文字の表示用。
    // 効果音・光・振動はノーツの終わり(OnJudgment)だけに出す。
    public event System.Action<JudgmentTier, int, bool> OnLongCut;

    private NoteSpawner bound;
    private readonly HashSet<CuttableNote> observedNotes = new HashSet<CuttableNote>();
    // 重み×基礎点の合計と、判定済み(MISS 含む)の重み。
    private long earnedPoints;
    private int judgedWeight, finalComboBonus;
    // 譜面全体の重みと満コンボ。譜面が差し替わったり伸びたりしたら数え直す。
    private ChartData totalsChart;
    private int totalsNoteCount = -1, chartWeight, chartComboUnits;

    public void Bind(NoteSpawner spawner)
    {
        foreach (var note in new List<CuttableNote>(observedNotes)) DetachNote(note);
        if (bound != null)
        {
            bound.OnNoteSpawned -= HandleSpawned;
            bound.OnNoteMissed -= HandleMissed;
        }
        bound = spawner;
        if (bound != null)
        {
            bound.OnNoteSpawned += HandleSpawned;
            bound.OnNoteMissed += HandleMissed;
            // 生成後にUIや採点コンポーネントが再接続されても、表示中のノーツを採点する。
            foreach (var note in bound.LiveNotes) HandleSpawned(note);
        }
    }

    void OnDestroy()
    {
        Bind(null);
    }

    public void Reset()
    {
        Score = 0;
        Combo = 0;
        MaxCombo = 0;
        IsFinalized = false;
        HitCount = 0;
        MissCount = 0;
        PerfectCount = 0;
        GreatCount = 0;
        GoodCount = 0;
        BadCount = 0;
        CutCount = 0;
        JudgedNoteCount = 0;
        earnedPoints = 0;
        judgedWeight = 0;
        finalComboBonus = 0;
        totalsChart = null;
        totalsNoteCount = -1;
        LastTier = JudgmentTier.Miss;
        crossCandidate = default;
        CrossCutCount = 0;
        AimScoring = false;
        AimDamageCount = AimDartPoints = AimDartTotal = aimTargetCount = 0;
        ClearLastCut();
    }

    // 避の採点を始める(点は 50000 から)。targetCount は的の数(満コンボ)。
    public void BeginAimScoring(int targetCount)
    {
        Reset();
        AimScoring = true;
        aimTargetCount = Mathf.Max(0, targetCount);
        AimDartTotal = aimTargetCount > 0 ? AimDodgeRules.DartsTotal : 0;
        Score = AimDodgeRules.Score(0, 0);
    }

    // 的1つの結果。tier は Perfect=HIT / Miss(2026-10-26 精度の段をやめた)。points はその的で取った点。
    public void RegisterAimDart(JudgmentTier tier, int points, SaberHand hand)
    {
        if (IsFinalized || !AimScoring) return;
        ClearLastCut();
        LastCutHand = hand;
        LastTier = tier;
        JudgedNoteCount++;
        if (tier == JudgmentTier.Miss)
        {
            MissCount++;
            Combo = 0;
        }
        else
        {
            HitCount++;
            CutCount++;
            Combo++;
            if (Combo > MaxCombo) MaxCombo = Combo;
            if (tier == JudgmentTier.Perfect) PerfectCount++;
            else if (tier == JudgmentTier.Great) GreatCount++;
            else GoodCount++;
            AimDartPoints += Mathf.Max(0, points);
        }
        int before = Score;
        Score = AimDodgeRules.Score(AimDamageCount, AimDartPoints);
        OnJudgment?.Invoke(tier, Score - before);
        OnJudgmentEx?.Invoke(tier, Score - before, false);
    }

    // トゲに当たった。コンボを切り、点を 1000 減らす(0 より下げない)。
    public void RegisterAimDamage()
    {
        if (IsFinalized || !AimScoring) return;
        AimDamageCount++;
        Combo = 0;
        int before = Score;
        Score = AimDodgeRules.Score(AimDamageCount, AimDartPoints);
        OnAimDamage?.Invoke(before - Score);
    }

    // 1回切るごとの重み。金は普通の2倍。
    public static int CutWeight(bool gold) => gold ? 2 : 1;

    // 譜面全体の重み(満点の配分の分母)と満コンボ。ロングは切る回数ぶん数える。
    public static void CountUnits(ChartData chart, out int weight, out int comboUnits)
    {
        weight = comboUnits = 0;
        if (chart == null || chart.notes == null) return;
        foreach (var note in chart.notes)
        {
            if (note == null) continue;
            int cuts = Mathf.Max(1, note.count);
            comboUnits += cuts;
            weight += cuts * CutWeight(string.Equals(note.color, "gold", System.StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshTotals()
    {
        var chart = bound != null ? bound.CurrentChart : null;
        int count = chart != null && chart.notes != null ? chart.notes.Count : 0;
        if (ReferenceEquals(chart, totalsChart) && count == totalsNoteCount) return;
        totalsChart = chart;
        totalsNoteCount = count;
        CountUnits(chart, out chartWeight, out chartComboUnits);
    }

    // ノーツの点は切り捨てなので、全部 Perfect のときだけちょうど 90000 になる。
    // 譜面が分からない(テスト・練習)ときや譜面より多く判定したときは、判定済みの重みを分母にする。
    private void Recalculate()
    {
        if (AimScoring) { Score = AimDodgeRules.Score(AimDamageCount, AimDartPoints); return; }
        RefreshTotals();
        long full = (long)Mathf.Max(chartWeight, judgedWeight) * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect);
        int notes = full > 0 ? (int)(NoteScore * earnedPoints / full) : 0;
        Score = notes + (IsFinalized ? finalComboBonus : 0);
    }

    private void ClearLastCut()
    {
        LastErrorValid = false;
        LastErrorMs = 0;
        LastCutHand = SaberHand.Any;
        LastCutWasGold = false;
        LastCutDirection = CutDirection.None;
        LastCutCount = 1;
        LastCutTimedOut = false;
        LastCutWasCross = false;
    }

    private void HandleSpawned(CuttableNote note)
    {
        if (note == null || note.IsFinalized || !observedNotes.Add(note)) return;
        note.OnCut += HandleCut;
        note.OnPartialCut += HandlePartialCut;
        note.OnRetired += DetachNote;
    }

    private void DetachNote(CuttableNote note)
    {
        observedNotes.Remove(note);
        if (note == null) return;
        note.OnCut -= HandleCut;
        note.OnPartialCut -= HandlePartialCut;
        note.OnRetired -= DetachNote;
    }

    // ロングの途中の1回を、判定1つ・コンボ +1 として数える。最後の1回は OnCut でノーツの判定として数える。
    // ロングは時刻ではなく切れたかどうかで採点する(向きの合わない振りはそもそも切れない)。
    private void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        if (IsFinalized || note == null || total <= 1 || cutIndex >= total - 1) return;
        CutCount++;
        LastErrorValid = false;
        int before = Score;
        AddHit(JudgmentTier.Perfect, CutWeight(note.IsGold));
        OnLongCut?.Invoke(JudgmentTier.Perfect, Score - before, false);
    }

    private void HandleCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (IsFinalized || note == null) return;
        LastCutHand = note.LastCutterHand;
        LastCutWasGold = note.IsGold;
        LastCutDirection = note.RequiredDirection;
        LastCutCount = note.RequiredCutCount;
        LastCutTimedOut = note.IsMissed;
        LastCutWasCross = false;
        int weight = CutWeight(note.IsGold);
        JudgmentTier tier;
        if (note.RequiredCutCount > 1)
        {
            LastErrorValid = false;
            if (note.IsMissed)
            {
                // 途中まで切ったロングの期限切れ。切った回は採点済みなので、残りの回を MISS にしてコンボを切る。
                RegisterMiss(note.RequiredCutCount - note.CutsAchieved, weight);
                note.NotifyJudgment(JudgmentTier.Miss, point, velocity);
                return;
            }
            // 最後の1回。途中の回と同じく、切れたら Perfect。
            CutCount++;
            tier = JudgmentTier.Perfect;
        }
        else
        {
            // タップ／フリックは時間誤差だけで決める(向きの合わない振りはそもそも切れないので、向きで下げない)。
            CutCount++;
            double songTime = note.LastCutSongTime ?? (songPlayer != null ? songPlayer.SongTime : 0);
            double error = songTime - note.HitTime;
            tier = JudgmentTierHelper.Classify(error);
            LastErrorMs = error * 1000.0;
            LastErrorValid = true;
        }

        if (note.RequiredCutCount == 1) CheckCrossCut(note, tier, point);
        RegisterHitWithContext(tier, weight);
        note.NotifyJudgment(tier, point, velocity);
    }

    // 1つ目の候補を覚え、2つ目が条件に合えば X 斬りとして知らせる。1つの候補は1回しか使わない。
    void CheckCrossCut(CuttableNote note, JudgmentTier tier, Vector3 point)
    {
        bool hand = LastCutHand == SaberHand.Left || LastCutHand == SaberHand.Right;
        if (note.IsGold || !hand || (tier != JudgmentTier.Perfect && tier != JudgmentTier.Great)) return;
        double cutTime = note.LastCutSongTime ?? (songPlayer != null ? songPlayer.SongTime : 0);
        var previous = crossCandidate;
        if (previous.valid && previous.hand != LastCutHand
            && System.Math.Abs(previous.hitTime - note.HitTime) <= NoteSpawner.SimultaneousEpsilonSeconds
            && System.Math.Abs(previous.cutTime - cutTime) <= CrossCutWindowSeconds)
        {
            crossCandidate = default;
            LastCutWasCross = true;
            CrossCutCount++;
            OnCrossCut?.Invoke(previous.point, point);
            return;
        }
        crossCandidate = new CrossCandidate { valid = true, hand = LastCutHand, hitTime = note.HitTime, cutTime = cutTime, point = point };
    }

    // 完了率（0..1）→ tier。100% = Perfect、85%以上 = Great、60%以上 = Good、30%以上 = Bad、未満 = Miss。
    // 2026-10-20 からロングは1回ごとに採点するので、採点には使わない(カメラ判定の記録用)。
    public static JudgmentTier TierByCompletionRatio(float ratio)
    {
        if (ratio >= 1.0f) return JudgmentTier.Perfect;
        if (ratio >= 0.85f) return JudgmentTier.Great;
        if (ratio >= 0.60f) return JudgmentTier.Good;
        if (ratio >= 0.30f) return JudgmentTier.Bad;
        return JudgmentTier.Miss;
    }

    // 旧 API。完了率と base tier を組み合わせる古い仕様。今は完了率のみで決定するため未使用だが、
    // 既存テストとの後方互換のため残しておく。
    public static JudgmentTier ScaleTierByCompletionRatio(JudgmentTier baseTier, float ratio)
    {
        JudgmentTier byRatio = TierByCompletionRatio(ratio);
        return ((int)byRatio) > ((int)baseTier) ? byRatio : baseTier;
    }

    private static JudgmentTier WorseOrEqual(JudgmentTier t, JudgmentTier minWorseLevel)
    {
        return ((int)t) < ((int)minWorseLevel) ? minWorseLevel : t;
    }

    private void HandleMissed(CuttableNote note)
    {
        // 部分達成のロングはMarkMiss内のOnCutで残りの回をMISS済み。
        // 続く期限切れ通知を別のMISSとして数えると、判定数とコンボを二重に更新してしまう。
        if (note != null && note.CutsAchieved > 0) return;
        // 1回も切れなかったロングは、切る回数ぶんの MISS。
        if (note != null) RegisterMiss(note.RequiredCutCount, CutWeight(note.IsGold));
        else RegisterMiss();
    }

    public void RegisterHit(JudgmentTier tier)
    {
        if (IsFinalized) return;
        if ((int)tier >= (int)JudgmentTier.Perfect && (int)tier < (int)JudgmentTier.Miss) CutCount++;
        ClearLastCut();
        RegisterHitWithContext(tier, 1);
    }

    private void RegisterHitWithContext(JudgmentTier tier, int weight)
    {
        if (IsFinalized) return;
        if ((int)tier < (int)JudgmentTier.Perfect || (int)tier > (int)JudgmentTier.Miss) return;
        LastTier = tier;
        if (tier == JudgmentTier.Miss)
        {
            RegisterMiss(1, weight);
            return;
        }
        int before = Score;
        AddHit(tier, weight);
        JudgedNoteCount++;
        int award = Score - before;
        OnJudgment?.Invoke(tier, award);
        OnJudgmentEx?.Invoke(tier, award, false);
    }

    // 1回分(タップは1つ、ロングは1カット)の判定を数える。通知は呼び出し側が出す。
    private void AddHit(JudgmentTier tier, int weight)
    {
        HitCount++;
        // Bad 以下でコンボが切れる仕様。Bad はヒット扱いで得点は入るが、コンボは0に戻す。
        if (tier == JudgmentTier.Bad)
        {
            Combo = 0;
        }
        else
        {
            Combo++;
            if (Combo > MaxCombo) MaxCombo = Combo;
        }
        switch (tier)
        {
            case JudgmentTier.Perfect: PerfectCount++; break;
            case JudgmentTier.Great: GreatCount++; break;
            case JudgmentTier.Good: GoodCount++; break;
            case JudgmentTier.Bad: BadCount++; break;
        }
        earnedPoints += (long)weight * JudgmentTierHelper.BasePoints(tier);
        judgedWeight += weight;
        Recalculate();
    }

    public void RegisterMiss() => RegisterMiss(1, 1);

    // cuts 回分の MISS をまとめて1回の通知で数え、コンボを切る(ロングの残りの回など)。
    private void RegisterMiss(int cuts, int weight)
    {
        if (IsFinalized) return;
        cuts = Mathf.Max(1, cuts);
        MissCount += cuts;
        judgedWeight += cuts * weight;
        Combo = 0;
        LastTier = JudgmentTier.Miss;
        JudgedNoteCount++;
        ClearLastCut();
        Recalculate();
        OnJudgment?.Invoke(JudgmentTier.Miss, 0);
        OnJudgmentEx?.Invoke(JudgmentTier.Miss, 0, false);
    }

    // 曲終了時にコンボ点を一度だけ精算する。判定イベントは発行しない。
    public int FinalizeScore()
    {
        if (IsFinalized) return 0;
        finalComboBonus = ComboBonus;
        IsFinalized = true;
        Recalculate();
        return finalComboBonus;
    }
}
