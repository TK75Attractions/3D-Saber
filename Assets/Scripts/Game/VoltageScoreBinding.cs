using System;

// ロング途中と最後のカットを、ScoreManager と同じ回数で受け取る。
public sealed class VoltageScoreBinding : IDisposable
{
    readonly ScoreManager score;
    readonly VoltageMeter meter;
    readonly Func<bool> accepting;
    bool disposed;

    public VoltageScoreBinding(ScoreManager score, VoltageMeter meter, Func<bool> accepting = null)
    {
        this.score = score ?? throw new ArgumentNullException(nameof(score));
        this.meter = meter ?? throw new ArgumentNullException(nameof(meter));
        this.accepting = accepting;
        meter.SynchronizeCombo(score.Combo);
        score.OnJudgment += Judgment;
        score.OnLongCut += LongCut;
        score.OnAimDamage += AimDamage;
    }

    void Judgment(JudgmentTier tier, int awarded)
    {
        if (accepting != null && !accepting()) return;
        meter.ObserveCombo(score.Combo, tier == JudgmentTier.Bad || tier == JudgmentTier.Miss);
    }

    void LongCut(JudgmentTier tier, int awarded, bool wrongFlick) => Judgment(tier, awarded);

    void AimDamage(int damage)
    {
        if (accepting != null && !accepting()) return;
        meter.ObserveCombo(score.Combo, true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (score == null) return;
        score.OnJudgment -= Judgment;
        score.OnLongCut -= LongCut;
        score.OnAimDamage -= AimDamage;
    }
}
