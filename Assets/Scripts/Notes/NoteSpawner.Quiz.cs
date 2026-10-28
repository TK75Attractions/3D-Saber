using UnityEngine;

// 勉強の裏譜面 Q の「斬り分け」のノーツ(第3版)。発射点(NoteData.launch)と隠した向き(NoteData.sideArrow)。
// 発射点: 判定の根元・着地の枠・床の印・当たりは着地点のまま、見た目の子(CuttableNote.Launch)だけを札の位置から着地点へまっすぐ近づける。
//   出る瞬間(根元が spawnZ)に、カメラと札(fromX, fromY, judgeZ)を結ぶ線が z = spawnZ と交わる点に本体が見え(画面では札の上)、
//   届く時刻(根元が judgeZ)にちょうど着地点に重なる。ずれは根元の奥行きだけの関数なので、接近の段(0.8 / 1.0 / 2.0 秒)が
//   変わっても出る瞬間は札の上、届く時刻は変わらない。コマ飛び・停止・再開でも同じ時刻なら同じ姿。
//   プールのキー(Key)にも UsesLaunch を含め、見た目の子を持つノーツをほかのノーツへ持ち越さない。
// 隠した向き: 判定は本当の向き(RequiredDirection)のまま。本体には左右の両矢印を出し(NoteArrowMaterials.SetSideArrow)、
//   床の印も中立の横線にする(FloorTimingGuide)。違う向きに振られたら矢印が本当の向きへ戻って光る(NoteArrowMaterials.Flash)。
public partial class NoteSpawner
{
    // 札の位置を見るカメラ。null なら Camera.main(QuizMode が自分のカメラを渡す。テストも自分のカメラを渡す)。
    public Camera LaunchView { get; set; }

    // 発射点はタップ(矢印を含む)だけ。ロング・見えないノーツ・ほかの飛び方(F・W・P・R・流星)には使わない。
    public bool UsesLaunch(NoteData data) => data != null && data.launch && !data.hidden && !data.IsLong
        && !data.HasWind && !data.HasOvertake && !UsesFall(data) && !UsesReverse(data) && !LongSlideRules.Enabled(data);

    // 出現時の準備(SpawnOne の EnsureWindVisual の後、cutFeedback.Track より前。Track は出現時の BodyRenderer を覚えるため)。
    // 奥の点は出現の時点のカメラで決める。
    void PrepareLaunchNote(CuttableNote note, NoteData data)
    {
        float scale = chart != null ? chart.coordScale : 1f;
        var label = new Vector2(data.fromX * scale, data.fromY * scale);
        var camera = LaunchView != null ? LaunchView : Camera.main;
        Vector3? eye = camera != null ? camera.transform.position : (Vector3?)null;
        note.ConfigureLaunch(QuizRules.LaunchFar(eye, label, judgeZ, spawnZ));
        UpdateLaunchNote(note);
    }

    // 根元の奥行きを決めたあと(UpdateLive)に、見た目の子のずれを当てる。届いたあとは 0(破片・ひびは着地点から)。
    void UpdateLaunchNote(CuttableNote note)
    {
        if (note == null || !note.HasLaunch) return;
        note.SetLaunchOffset(QuizRules.LaunchOffset(note.LaunchFar, note.LandingPosition,
            note.transform.position.z, judgeZ, spawnZ));
    }

    // 矢印を作った・結び直したあと(SpawnOne)。使い回しの矢印も、出現のたびに両矢印か本当の矢印かを決め直す。
    void ApplySideArrow(CuttableNote note)
    {
        if (note != null && note.Arrow != null) note.Arrow.SetSideArrow(note.HidesSideArrow);
    }
}
