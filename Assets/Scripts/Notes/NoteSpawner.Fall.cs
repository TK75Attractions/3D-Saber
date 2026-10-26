using UnityEngine;

// F の処理を風の位置・姿勢の計算から分離する。
// プールのキーにも F を含め、描画用の子を通常譜面や W へ持ち越さない。
public partial class NoteSpawner
{
    FallPetals fallPetals;

    public bool HasFall => FallRules.Enabled(chart?.fall);
    public int FallPetalCount => fallPetals != null ? fallPetals.PetalCount : 0;
    public bool FallLineVisible => fallPetals != null && fallPetals.LineVisible;

    void PrepareFallNote(CuttableNote note, double remaining)
    {
        note.EnsureFallVisual();
        UpdateFallNote(note, remaining);
    }

    void UpdateFallNote(CuttableNote note, double remaining)
    {
        // SaberCutJudge が読む根元は常に着地点。早判定・入力遅延・コマ飛びでも動かさない。
        // 見た目だけが等速で落ち、HitTime ちょうどに根元と一致する。
        note.transform.position = new Vector3(note.LandingPosition.x, note.LandingPosition.y, judgeZ);
        float offset = note.CutsAchieved > 0 ? 0f
            : FallRules.Offset(chart.fall.height, remaining, approachTime);
        note.SetFallOffset(offset);
    }

    // 停止中の照明変更と終了時の復帰は、花びらの位置や寿命を進めず反映する。
    public void RefreshFallPetalBrightness()
    {
        if (fallPetals != null) fallPetals.RefreshDarkBrightness();
    }

    void TickFallPetals(double songTime)
    {
        if (!HasFall)
        {
            if (fallPetals != null) fallPetals.Clear();
            return;
        }
        if (fallPetals == null) fallPetals = FallPetals.Create(transform);
        fallPetals.Tick(this, songTime);
    }
}
