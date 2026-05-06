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
        double songTime = songPlayer != null ? songPlayer.SongTime : 0;
        double error = songTime - note.HitTime;
        JudgmentTier tier = JudgmentTierHelper.Classify(error);

        // ロングノーツ：達成数 / 必要数 で tier に上限をかける
        if (note.RequiredCutCount > 1)
        {
            float ratio = (float)note.CutsAchieved / Mathf.Max(1, note.RequiredCutCount);
            tier = ScaleTierByCompletionRatio(tier, ratio);
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

    // 完了率（0..1）に応じて tier の上限（=最良値）を制限する。
    // 100% → そのまま、75%以上 → Great まで、50%以上 → Good まで、25%以上 → Bad、未満 → Miss。
    public static JudgmentTier ScaleTierByCompletionRatio(JudgmentTier baseTier, float ratio)
    {
        if (ratio >= 1f) return baseTier;
        if (ratio >= 0.75f) return WorseOrEqual(baseTier, JudgmentTier.Great);
        if (ratio >= 0.5f) return WorseOrEqual(baseTier, JudgmentTier.Good);
        if (ratio >= 0.25f) return JudgmentTier.Bad;
        return JudgmentTier.Miss;
    }

    private static JudgmentTier WorseOrEqual(JudgmentTier t, JudgmentTier minWorseLevel)
    {
        // tier 列挙は Perfect=0..Miss=4 の順なので「悪い方=数値大」
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
        Combo++;
        if (Combo > MaxCombo) MaxCombo = Combo;
        int award = JudgmentTierHelper.BasePoints(tier) + comboBonusPerStep * (Combo - 1);
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
        OnJudgment?.Invoke(JudgmentTier.Miss, 0);
        OnJudgmentEx?.Invoke(JudgmentTier.Miss, 0, false);
    }
}
