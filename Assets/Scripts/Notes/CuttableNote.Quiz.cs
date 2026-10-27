// 勉強の裏譜面「Q」の見えないノーツと、当たりの大きさの倍率。NoteSpawner が出現のたびに設定し、ResetForSpawn で戻す。
public partial class CuttableNote
{
    // 見えないノーツ(NoteData.hidden)。本体・矢印・着地の枠・床の印・ロングの数字・同時押しの線・破片・切った光を出さない
    // (Veil 1 で合図ごと隠し、GameplayCutFeedback もこの印を見て描かない)。判定・採点・音はふつうのノーツと同じ。
    // 見た目は QuizMode の玉と的が受け持つ。
    public bool IsHidden { get; internal set; }

    // 当たりの大きさの倍率(NoteData.hitScale、0・省略は 1)。SaberCutJudge と CameraImuJudgment がノーツの当たり半径に掛ける。
    public float HitScale { get; internal set; } = 1f;

    internal void ResetQuiz()
    {
        IsHidden = false;
        HitScale = 1f;
    }
}
