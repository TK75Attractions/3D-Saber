using System.Collections.Generic;
using UnityEngine;

// F 専用の描画先。判定の根元・着地枠・床の印を動かさず、本体と矢印を落とす。
// 切断の破片・ひび・同時線は共通の VisualTransform / BodyRenderer を通して従来どおり扱う。
public partial class CuttableNote
{
    Transform fallVisual;
    MeshRenderer fallBody;

    internal void EnsureFallVisual()
    {
        if (fallVisual == null)
        {
            var source = GetComponent<MeshRenderer>();
            var sourceMesh = GetComponent<MeshFilter>();
            var go = new GameObject("FallVisual", typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            fallVisual = go.transform;
            fallVisual.SetParent(transform, false);
            fallBody = go.GetComponent<MeshRenderer>();
            if (sourceMesh != null) go.GetComponent<MeshFilter>().sharedMesh = sourceMesh.sharedMesh;
            if (source != null)
            {
                // メッシュと材質の所有権は従来のまま。描画先だけを移す。
                fallBody.sharedMaterials = source.sharedMaterials;
                fallBody.enabled = source.enabled;
                fallBody.shadowCastingMode = source.shadowCastingMode;
                fallBody.receiveShadows = source.receiveShadows;
                fallBody.lightProbeUsage = source.lightProbeUsage;
                fallBody.reflectionProbeUsage = source.reflectionProbeUsage;
                fallBody.probeAnchor = source.probeAnchor;
                fallBody.motionVectorGenerationMode = source.motionVectorGenerationMode;
                fallBody.renderingLayerMask = source.renderingLayerMask;
                fallBody.sortingLayerID = source.sortingLayerID;
                fallBody.sortingOrder = source.sortingOrder;
                fallBody.forceRenderingOff = source.forceRenderingOff;
                var properties = new MaterialPropertyBlock();
                source.GetPropertyBlock(properties);
                fallBody.SetPropertyBlock(properties);
                if (Visuals != null) Visuals.MoveBodyRenderer(source, fallBody);
                source.enabled = false;
            }
            else fallBody.enabled = false;
        }

        // 再利用時に追加された縁やひびも拾う。着地枠は兄弟オブジェクトなので含まれない。
        var children = new List<Transform>();
        foreach (Transform child in transform)
            if (child != fallVisual && child.GetComponentInChildren<Renderer>(true) != null)
                children.Add(child);
        foreach (var child in children) child.SetParent(fallVisual, false);

        fallVisual.localPosition = Vector3.zero;
        fallVisual.gameObject.SetActive(true);
        // F はノーツの近くに文字を出さない。ロングの進み具合は既存の床の印で示す。
        if (countLabel != null) countLabel.gameObject.SetActive(false);
    }

    internal void SetFallOffset(float height)
    {
        if (fallVisual == null) return;
        fallVisual.localPosition = height == 0f
            ? Vector3.zero : transform.InverseTransformVector(Vector3.up * height);
        // ミス済みの本体を着地点へ積み重ねない。採点・回収時刻は従来どおり。
        fallVisual.gameObject.SetActive(!IsMissed && !IsCut);
        if (countLabel != null) countLabel.gameObject.SetActive(false);
    }
}
