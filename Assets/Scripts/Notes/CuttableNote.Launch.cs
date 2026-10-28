using System.Collections.Generic;
using UnityEngine;

// 勉強の裏譜面 Q の発射点(NoteData.launch、第3版の「斬り分け」)の描画先。F の FallVisual・R の ReverseVisual と同じ作り。
// 判定の根元(transform)・着地の枠・床の印・当たりは着地点のまま。本体・矢印・ひびを移した子だけを、札の位置から
// 着地点へまっすぐ近づける(ずれは NoteSpawner.Quiz が根元の奥行きから毎コマ決める。届く時刻に 0)。
// 切った破片・ひび・切った光は共通の VisualTransform / BodyRenderer を通して今までどおり(届く時刻には子が根元に重なる)。
// プールは発射点のノーツとほかのノーツで分ける(NoteSpawner.Key)。出現のたびに ResetLaunch でずれを戻す。
public partial class CuttableNote
{
    Transform launchVisual;
    MeshRenderer launchBody;

    // この出現が発射点の見た目を使うか。
    public bool HasLaunch { get; private set; }
    // 出る瞬間に本体が見える奥の点(z = spawnZ の面の xy。カメラと札を結ぶ線の上)。
    public Vector2 LaunchFar { get; private set; }

    // 出現時に1回(NoteSpawner.PrepareLaunchNote)。奥の点は出現の時点で決め、飛行中に読み直さない。
    internal void ConfigureLaunch(Vector2 far)
    {
        EnsureLaunchVisual();
        HasLaunch = true;
        LaunchFar = far;
    }

    // 本体の描画先を子へ移す(1回だけ)。再利用時に足された縁・矢印・ひびも毎回拾う。着地の枠は根元の兄弟なので含まれない。
    void EnsureLaunchVisual()
    {
        if (launchVisual == null)
        {
            var source = GetComponent<MeshRenderer>();
            var sourceMesh = GetComponent<MeshFilter>();
            var go = new GameObject("LaunchVisual", typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            launchVisual = go.transform;
            launchVisual.SetParent(transform, false);
            launchBody = go.GetComponent<MeshRenderer>();
            if (sourceMesh != null) go.GetComponent<MeshFilter>().sharedMesh = sourceMesh.sharedMesh;
            if (source != null)
            {
                // メッシュと材質の所有者は従来のまま。描画先だけを子へ移す。
                launchBody.sharedMaterials = source.sharedMaterials;
                launchBody.enabled = source.enabled;
                launchBody.shadowCastingMode = source.shadowCastingMode;
                launchBody.receiveShadows = source.receiveShadows;
                launchBody.lightProbeUsage = source.lightProbeUsage;
                launchBody.reflectionProbeUsage = source.reflectionProbeUsage;
                launchBody.probeAnchor = source.probeAnchor;
                launchBody.motionVectorGenerationMode = source.motionVectorGenerationMode;
                launchBody.renderingLayerMask = source.renderingLayerMask;
                launchBody.sortingLayerID = source.sortingLayerID;
                launchBody.sortingOrder = source.sortingOrder;
                launchBody.forceRenderingOff = source.forceRenderingOff;
                var properties = new MaterialPropertyBlock();
                source.GetPropertyBlock(properties);
                launchBody.SetPropertyBlock(properties);
                var visuals = Visuals != null ? Visuals : GetComponent<NoteVisuals>();
                if (visuals != null) visuals.MoveBodyRenderer(source, launchBody);
                source.enabled = false;
            }
            else launchBody.enabled = false;
        }

        var children = new List<Transform>();
        foreach (Transform child in transform)
            if (child != launchVisual && child.GetComponentInChildren<Renderer>(true) != null)
                children.Add(child);
        foreach (var child in children) child.SetParent(launchVisual, false);
        launchVisual.gameObject.SetActive(true);
    }

    // 毎コマ(NoteSpawner.UpdateLaunchNote)。offset は見た目のずれ(ワールドの xy)。拡縮のある根元でもワールドの向きにそろえる。
    internal void SetLaunchOffset(Vector2 offset)
    {
        if (!HasLaunch || launchVisual == null) return;
        launchVisual.localPosition = offset == Vector2.zero ? Vector3.zero
            : transform.InverseTransformVector(new Vector3(offset.x, offset.y, 0f));
    }

    // 出現のたびに(ResetForSpawn → ResetQuiz)。ずれを戻す。子の作りはそのまま再利用する。
    internal void ResetLaunch()
    {
        HasLaunch = false;
        LaunchFar = Vector2.zero;
        if (launchVisual != null) launchVisual.localPosition = Vector3.zero;
    }
}
