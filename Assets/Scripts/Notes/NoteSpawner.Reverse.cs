// Manic Sonic の裏譜面 R のノーツの見た目(逆再生の2片・破片・割れ目の光、鏡門の鏡像)を毎コマ当てる。
// 判定の根元(transform)は最初から本当の位置のまま。見た目はどれも残り時間だけの関数なので、コマ飛び・停止・再開でも同じ時刻なら同じ姿。
// プールのキー(Key)にも UsesReverse を含め、2片の子を持つノーツをほかの曲へ持ち越さない。
public partial class NoteSpawner
{
    public bool HasReverse => ReverseRules.Enabled(chart?.reverse);

    // 2片と鏡はタップ(矢印を含む)だけ。ロング(割らない)・見えないノーツ・ほかの飛び方(F・W・P・流星・V)には使わない。
    public bool UsesReverse(NoteData data) => HasReverse && data != null && !data.hidden && !data.IsLong
        && !data.HasWind && !data.HasOvertake && !UsesFall(data) && !LongSlideRules.Enabled(data)
        && VanishRules.Parse(data.vanish) == VanishMode.None;

    // 出現時の準備(SpawnOne の EnsureWindVisual の後、cutFeedback.Track より前。Track は出現時の BodyRenderer を覚えるため)。
    // ordinal はノーツの番号(矢印の無いノーツの割れ目の向きを順に変える)。
    void PrepareReverseNote(CuttableNote note, NoteData data, int ordinal, double remaining)
    {
        var pieces = ReverseRules.PiecesFor(chart.reverse, data.time);
        note.ConfigureReverse(ReverseRules.CrackIndex(note.RequiredDirection, ordinal),
            pieces.Count, pieces.Gap, pieces.Spin, data.mirror, ordinal);
        ApplyReverse(note, remaining);
    }

    // UpdateLive と RefreshJudgmentWindows の両方から呼ぶ。
    void UpdateReverseNote(CuttableNote note, double songTime)
    {
        if (note == null || !note.HasReverse) return;
        ApplyReverse(note, note.HitTime - songTime);
    }

    void ApplyReverse(CuttableNote note, double remaining)
    {
        bool reduced = DisplaySettings.ReducedEffects;
        float gap = ReverseRules.Gap(remaining, approachTime, ReverseRules.EffectiveGap(note.ReverseGap, reduced));
        // F3 では破片を出さない。
        float reach = reduced || note.ReverseShardCount <= 0 ? -1f : ReverseRules.ShardReach(remaining, approachTime);
        float offset = note.IsMirror ? ReverseRules.MirrorOffsetX(note.LandingPosition.x, remaining) : 0f;
        float scale = note.IsMirror ? ReverseRules.MirrorScaleX(remaining) : 1f;
        note.SetReverse(gap, reach, offset, scale, ReverseRules.SeamFlash(remaining, reduced));
    }
}
