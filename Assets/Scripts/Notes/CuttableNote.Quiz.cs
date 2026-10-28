// 勉強の裏譜面「Q」のノーツの印。NoteSpawner が出現のたびに設定し、ResetForSpawn(ResetQuiz)で戻す。
public partial class CuttableNote
{
    // 見えないノーツ(NoteData.hidden)。本体・矢印・着地の枠・床の印・ロングの数字・同時押しの線・破片・切った光を出さない
    // (Veil 1 で合図ごと隠し、GameplayCutFeedback もこの印を見て描かない)。判定・採点・音はふつうのノーツと同じ。
    // Q の第2版で使った。第3版の Q では使わない(コードは残す)。
    public bool IsHidden { get; internal set; }

    // 当たりの大きさの倍率(NoteData.hitScale、0・省略は 1)。SaberCutJudge と CameraImuJudgment がノーツの当たり半径に掛ける。
    public float HitScale { get; internal set; } = 1f;

    // 飾りの金(QuizDecoyData。NoteSpawner.Decoy だけが立てる)。見た目は本物の金と同じで、SaberCutJudge の候補にも入る。
    // 受付の時間に刃が通ったら「違う答えを切った」: 割れずに暗くなり(HitDecoy)、NoteSpawner.OnDecoyHit から ScoreManager が
    // MISS にする(コンボが切れる。2026-10-28 ユーザー「違うやつ切ったらミスになるようにして」)。採点の対象のノーツではないので、
    // 満点の分母・判定したノーツの数には入らない。出現のたびに ResetForSpawn で外れ、NoteSpawner.Decoy が立て直す。
    public bool IsDecoy { get; internal set; }
    // 飾りの譜面の見えにくさ(薄れていくときの元の値)。
    internal float DecoyVeil;
    // 間違えて切られた飾り(もう一度は数えない)。
    public bool DecoyHit { get; private set; }
    public event System.Action<CuttableNote> OnDecoyHit;

    internal void HitDecoy()
    {
        if (!IsDecoy || DecoyHit) return;
        DecoyHit = true;
        if (Visuals != null) Visuals.DimAfterMiss();
        OnDecoyHit?.Invoke(this);
    }

    // 隠した向き(NoteData.sideArrow "hidden")。本当の向き(RequiredDirection)は判定にだけ使い、本体には左右の両矢印を出す。
    // 違う向きに振られたら(NoteArrowMaterials が OnWrongDirection を受けて)本当の矢印に戻し、床の印も本当の向きにする。
    public bool SideArrowHidden { get; internal set; }
    public bool SideArrowRevealed { get; private set; }
    public bool HidesSideArrow => SideArrowHidden && !SideArrowRevealed;
    // 本体に今、左右の両矢印が出ているか(テスト用)。
    public bool ShowsSideArrow => Arrow != null && Arrow.SideShown;

    internal void RevealSideArrow()
    {
        if (SideArrowHidden) SideArrowRevealed = true;
    }

    internal void ResetQuiz()
    {
        IsHidden = false;
        HitScale = 1f;
        IsDecoy = false;
        DecoyVeil = 0f;
        DecoyHit = false;
        OnDecoyHit = null;
        SideArrowHidden = SideArrowRevealed = false;
        ResetLaunch();
    }
}
