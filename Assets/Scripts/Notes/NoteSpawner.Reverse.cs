// Manic Sonic の裏譜面 R のノーツの見た目(鏡門の鏡像)を毎コマ当てる。
// 判定の根元(transform)は最初から本当の位置のまま。見た目は残り時間だけの関数なので、コマ飛び・停止・再開でも同じ時刻なら同じ姿。
// プールのキー(Key)にも UsesReverse を含め、見た目の子を持つ鏡のノーツをほかのノーツへ持ち越さない。
public partial class NoteSpawner
{
    public bool HasReverse => ReverseRules.Enabled(chart?.reverse);

    // 鏡はタップ(矢印を含む)の mirror だけ。ロング・見えないノーツ・ほかの飛び方(F・W・P・流星・V)には使わない
    // (読み込みで mirror を外してあるが、ここでも外す)。
    public bool UsesReverse(NoteData data) => HasReverse && data != null && data.mirror && !data.hidden && !data.IsLong
        && !data.HasWind && !data.HasOvertake && !UsesFall(data) && !LongSlideRules.Enabled(data)
        && VanishRules.Parse(data.vanish) == VanishMode.None;

    // 出現時の準備(SpawnOne の EnsureWindVisual の後、cutFeedback.Track より前。Track は出現時の BodyRenderer を覚えるため)。
    void PrepareReverseNote(CuttableNote note, NoteData data, double remaining)
    {
        note.ConfigureReverse(data.mirror);
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
        note.SetReverse(note.IsMirror ? ReverseRules.MirrorOffsetX(note.LandingPosition.x, remaining) : 0f);
    }
}
