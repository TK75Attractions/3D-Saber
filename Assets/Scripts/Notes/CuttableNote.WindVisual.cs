using System.Collections.Generic;
using UnityEngine;

// 判定の根元を動かさず、本体・矢印・ひびだけを横へ揺らす。
public partial class CuttableNote
{
    Transform windVisual;
    MeshRenderer windBody;

    // R の描画先(CuttableNote.Reverse)は F・W の次。プールを分けるので、F・W と同じノーツに並ぶことはない。
    public Transform VisualTransform => fallVisual != null ? fallVisual : windVisual != null ? windVisual
        : reverseVisual != null ? reverseVisual : transform;
    public MeshRenderer BodyRenderer => fallVisual != null ? fallBody : windVisual != null ? windBody
        : reverseVisual != null ? reverseBody : GetComponent<MeshRenderer>();

    // 判定・採点へ渡した座標は保存し、描画するときだけ同じ相対位置へ移す。
    public Vector3 VisualPoint(Vector3 point) => point + (VisualTransform.position - transform.position);

    internal void EnsureWindVisual()
    {
        if (windVisual == null && WindSway != 0f)
        {
            var source = GetComponent<MeshRenderer>();
            var sourceMesh = GetComponent<MeshFilter>();
            var go = new GameObject("WindVisual", typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            windVisual = go.transform;
            windVisual.SetParent(transform, false);
            windBody = go.GetComponent<MeshRenderer>();
            if (sourceMesh != null) go.GetComponent<MeshFilter>().sharedMesh = sourceMesh.sharedMesh;
            if (source != null)
            {
                // 材質とメッシュの所有者は従来のまま。描画先だけを子へ移す。
                windBody.sharedMaterials = source.sharedMaterials;
                windBody.enabled = source.enabled;
                windBody.shadowCastingMode = source.shadowCastingMode;
                windBody.receiveShadows = source.receiveShadows;
                windBody.lightProbeUsage = source.lightProbeUsage;
                windBody.reflectionProbeUsage = source.reflectionProbeUsage;
                windBody.probeAnchor = source.probeAnchor;
                windBody.motionVectorGenerationMode = source.motionVectorGenerationMode;
                windBody.renderingLayerMask = source.renderingLayerMask;
                windBody.sortingLayerID = source.sortingLayerID;
                windBody.sortingOrder = source.sortingOrder;
                windBody.forceRenderingOff = source.forceRenderingOff;
                var properties = new MaterialPropertyBlock();
                source.GetPropertyBlock(properties);
                windBody.SetPropertyBlock(properties);
                var visuals = Visuals != null ? Visuals : GetComponent<NoteVisuals>();
                if (visuals != null) visuals.MoveBodyRenderer(source, windBody);
                source.enabled = false;
            }
            else windBody.enabled = false;

            // 着地枠は根元の兄弟なので含まれない。コライダーは根元に残す。
            var children = new List<Transform>();
            foreach (Transform child in transform)
                if (child != windVisual && child.GetComponentInChildren<Renderer>(true) != null)
                    children.Add(child);
            foreach (var child in children) child.SetParent(windVisual, false);
        }

        // 数字は拡縮しない別オブジェクトのまま、見た目の中心へ追従させる。
        if (countLabel != null)
        {
            var follower = countLabel.GetComponent<FollowTransformWorldOffset>();
            if (follower != null) follower.target = VisualTransform;
            countLabel.transform.position = VisualTransform.position + LongNoteCountStyle.WorldOffset;
        }
    }

    internal void SetWindSway(Vector3 worldOffset)
    {
        if (windVisual == null) return;
        // 拡縮・傾きのある出現中でも、揺れの幅と向きはワールドの横方向にそろえる。
        windVisual.localPosition = worldOffset.Equals(Vector3.zero)
            ? Vector3.zero : transform.InverseTransformVector(worldOffset);
        if (countLabel != null)
            countLabel.transform.position = windVisual.position + LongNoteCountStyle.WorldOffset;
    }
}
