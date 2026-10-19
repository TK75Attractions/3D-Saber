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
    public int ComboBonus => IsFinalized ? finalComboBonus : (int)((long)ComboScore * MaxCombo / Mathf.Max(1, ComboUnits));
    // ランクと HUD 用。曲の途中でも「今の最大コンボで終わったときの点」。
    public int ScoreWithCombo => IsFinalized ? Score : Score + ComboBonus;
    // 満コンボ(=判定の数。ロングは切る回数)。譜面が分からないときは判定済みの数。
    public int ComboUnits { get { RefreshTotals(); return Mathf.Max(chartComboUnits, HitCount + MissCount); } }
    // 判定し終えたノーツの数(ロングも1つ)。曲の終わりの確認に使う。
    public int JudgedNoteCount { get; private set; }
    public bool IsFinalized { get; private set; }
    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public int PerfectCount { get; private set; }
    public int GreatCount { get; private set; }
    public int GoodCount { get; private set; }
    public int BadCount { get; private set; }
    // 空譜面を達成扱いにしない。Good はコンボ継続、Bad / Miss は達成対象外。
    public bool IsFullCombo => HitCount > 0 && BadCount == 0 && MissCount == 0;
    public bool IsAllPerfect => IsFullCombo && PerfectCount == HitCount;
    public JudgmentTier LastTier { get; private set; } = JudgmentTier.Miss;
    public bool LastWasWrongFlick { get; private set; }
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

    public event System.Action<JudgmentTier, int> OnJudgment; // (tier, awarded score this judgment)
    public event System.Action<JudgmentTier, int, bool> OnJudgmentEx; // (tier, awarded, wasWrongFlick)
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
        JudgedNoteCount = 0;
        earnedPoints = 0;
        judgedWeight = 0;
        finalComboBonus = 0;
        totalsChart = null;
        totalsNoteCount = -1;
        LastTier = JudgmentTier.Miss;
        ClearLastCut();
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
        RefreshTotals();
        long full = (long)Mathf.Max(chartWeight, judgedWeight) * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect);
        int notes = full > 0 ? (int)(NoteScore * earnedPoints / full) : 0;
        Score = notes + (IsFinalized ? finalComboBonus : 0);
    }

    private void ClearLastCut()
    {
        LastWasWrongFlick = false;
        LastErrorValid = false;
        LastErrorMs = 0;
        LastCutHand = SaberHand.Any;
        LastCutWasGold = false;
        LastCutDirection = CutDirection.None;
        LastCutCount = 1;
        LastCutTimedOut = false;
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
    // ロングは時刻ではなく切れたかどうかで採点し、その回の向きが違えば1段下げる。
    private void HandlePartialCut(CuttableNote note, int cutIndex, int total)
    {
        if (IsFinalized || note == null || total <= 1 || cutIndex >= total - 1) return;
        bool wrongDir = note.RequiredDirection != CutDirection.None && !note.LatestCutCorrectDirection;
        JudgmentTier tier = wrongDir ? DowngradeTier(JudgmentTier.Perfect) : JudgmentTier.Perfect;
        LastErrorValid = false;
        int before = Score;
        AddHit(tier, CutWeight(note.IsGold));
        OnLongCut?.Invoke(tier, Score - before, wrongDir);
    }

    private void HandleCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (IsFinalized || note == null) return;
        LastCutHand = note.LastCutterHand;
        LastCutWasGold = note.IsGold;
        LastCutDirection = note.RequiredDirection;
        LastCutCount = note.RequiredCutCount;
        LastCutTimedOut = note.IsMissed;
        int weight = CutWeight(note.IsGold);
        JudgmentTier tier;
        bool wrongDir;
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
            // 最後の1回。途中の回と同じく、切れたら Perfect でその回の向きが違えば1段下げる。
            tier = JudgmentTier.Perfect;
            wrongDir = note.RequiredDirection != CutDirection.None && !note.LatestCutCorrectDirection;
        }
        else
        {
            // タップ／フリックは従来通り時間誤差ベース。
            double songTime = note.LastCutSongTime ?? (songPlayer != null ? songPlayer.SongTime : 0);
            double error = songTime - note.HitTime;
            tier = JudgmentTierHelper.Classify(error);
            LastErrorMs = error * 1000.0;
            LastErrorValid = true;
            wrongDir = note.RequiredDirection != CutDirection.None && !note.LastCutCorrectDirection;
        }

        if (wrongDir)
        {
            tier = DowngradeTier(tier);
        }
        LastWasWrongFlick = wrongDir;
        RegisterHitWithContext(tier, weight);
        note.NotifyJudgment(tier, point, velocity);
    }

    public static JudgmentTier DowngradeTier(JudgmentTier t)
    {
        switch (t)
        {
            case JudgmentTier.Perfect: return JudgmentTier.Great;
            case JudgmentTier.Great: return JudgmentTier.Good;
            case JudgmentTier.Good: return JudgmentTier.Bad;
            case JudgmentTier.Bad: return JudgmentTier.Miss;
            default: return JudgmentTier.Miss;
        }
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
        OnJudgmentEx?.Invoke(tier, award, LastWasWrongFlick);
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
