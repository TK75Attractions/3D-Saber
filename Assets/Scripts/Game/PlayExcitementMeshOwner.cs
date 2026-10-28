using UnityEngine;

// PlayExcitementMesh を置いた GameObject が壊れたら(親ごと壊れたときも)、そのメッシュと材質も一緒に壊す。
// 2026-10-28 夜の全件テスト: 最後の大きい光(DopamineFlourish の FinalStageLight)は舞台(StageFeverShow)の下に置くので、
// 床だけを壊すと、持ち主が別のメッシュと材質が残っていた(FavoriteEffectsPlayTests の後片付けの確認)。
// 持ち主の Dispose が先に来ても後に来てもよい(壊れた物は null として扱う)。EditMode のテストでも壊すよう ExecuteAlways。
[ExecuteAlways]
public sealed class PlayExcitementMeshOwner : MonoBehaviour
{
    internal Mesh Mesh;
    internal Material Material;

    void OnDestroy()
    {
        UISkinKit.SafeDestroy(Mesh);
        UISkinKit.SafeDestroy(Material);
        Mesh = null; Material = null;
    }
}
