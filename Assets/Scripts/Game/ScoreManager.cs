using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public SongPlayer songPlayer;
    public int comboBonusPerStep = 10;

    public int Score { get; private set; }
    public int Combo { get; private set; }
    public int MaxCombo { get; private set; }
    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public int PerfectCount { get; private set; }
    public int GreatCount { get; private set; }
    public int GoodCount { get; private set; }
    public int BadCount { get; private set; }
    public JudgmentTier LastTier { get; private set; } = JudgmentTier.Miss;
    public bool LastWasWrongFlick { get; private set; }
    // 直近判定のカットを行った手(ノーツ経由のカットのみ。Miss や直接 RegisterHit では Any)。
    // HapticFeedback が「切った手のデバイスだけ振動」させるのに使う。
    public SaberHand LastCutHand { get; private set; } = SaberHand.Any;
    // 直近判定が金ノーツのカットか。JudgmentSfx が通常カット音を重ねない判定に使う。
    public bool LastCutWasGold { get; private set; }
    // 直近判定の時間誤差(ミリ秒、負=早い/正=遅い)。タップ/フリックのみ有効。
    // ロング(完了率ベース)と Miss では LastErrorValid=false になる。HUD の EARLY/LATE 表示用。
    public double LastErrorMs { get; private set; }
    public bool LastErrorValid { get; private set; }

    public event System.Action<JudgmentTier, int> OnJudgment; // (tier, awarded score this judgment)
    public event System.Action<JudgmentTier, int, bool> OnJudgmentEx; // (tier, awarded, wasWrongFlick)

    private NoteSpawner bound;

    public void Bind(NoteSpawner spawner)
    {
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
        HitCount = 0;
        MissCount = 0;
        PerfectCount = 0;
        GreatCount = 0;
        GoodCount = 0;
        BadCount = 0;
        LastTier = JudgmentTier.Miss;
    }

    private void HandleSpawned(CuttableNote note)
    {
        note.OnCut += HandleCut;
    }

    private void HandleCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        LastCutHand = note.LastCutterHand;
        LastCutWasGold = note.IsGold;
        JudgmentTier tier;
        if (note.RequiredCutCount > 1)
        {
            // ロングノーツは完了率ベースのみで tier を決める。
            // 「切りきれたら Perfect、ダメだった分だけ降格」のシンプル仕様。
            float ratio = (float)note.CutsAchieved / Mathf.Max(1, note.RequiredCutCount);
            tier = TierByCompletionRatio(ratio);
            LastErrorValid = false;
        }
        else
        {
            // タップ／フリックは従来通り時間誤差ベース。
            double songTime = songPlayer != null ? songPlayer.SongTime : 0;
            double error = songTime - note.HitTime;
            tier = JudgmentTierHelper.Classify(error);
            LastErrorMs = error * 1000.0;
            LastErrorValid = true;
        }

        bool wrongDir = note.RequiredDirection != CutDirection.None && !note.LastCutCorrectDirection;
        if (wrongDir)
        {
            tier = DowngradeTier(tier);
        }
        LastWasWrongFlick = wrongDir;
        RegisterHit(tier);
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

    // 完了率（0..1）→ tier。完了率のみで決定するシンプル版。
    // 100% = Perfect、85%以上 = Great、60%以上 = Good、30%以上 = Bad、未満 = Miss。
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
        RegisterMiss();
    }

    public void RegisterHit(JudgmentTier tier)
    {
        LastTier = tier;
        if (tier == JudgmentTier.Miss)
        {
            RegisterMiss();
            return;
        }
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
        int comboMultiplier = Mathf.Max(0, Combo - 1);
        int award = JudgmentTierHelper.BasePoints(tier) + comboBonusPerStep * comboMultiplier;
        Score += award;
        switch (tier)
        {
            case JudgmentTier.Perfect: PerfectCount++; break;
            case JudgmentTier.Great: GreatCount++; break;
            case JudgmentTier.Good: GoodCount++; break;
            case JudgmentTier.Bad: BadCount++; break;
        }
        OnJudgment?.Invoke(tier, award);
        OnJudgmentEx?.Invoke(tier, award, LastWasWrongFlick);
    }

    public void RegisterMiss()
    {
        MissCount++;
        Combo = 0;
        LastTier = JudgmentTier.Miss;
        LastErrorValid = false;
        LastCutHand = SaberHand.Any;
        LastCutWasGold = false;
        OnJudgment?.Invoke(JudgmentTier.Miss, 0);
        OnJudgmentEx?.Invoke(JudgmentTier.Miss, 0, false);
    }
}
