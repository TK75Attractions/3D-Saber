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

    public event System.Action<JudgmentTier, int> OnJudgment; // (tier, awarded score this judgment)

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
        RegisterHit(JudgmentTierHelper.Classify(error));
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
    }

    public void RegisterMiss()
    {
        MissCount++;
        Combo = 0;
        LastTier = JudgmentTier.Miss;
        OnJudgment?.Invoke(JudgmentTier.Miss, 0);
    }
}
