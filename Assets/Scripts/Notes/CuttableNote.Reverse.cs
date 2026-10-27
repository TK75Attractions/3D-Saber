using System.Collections.Generic;
using UnityEngine;

// Manic Sonic の裏譜面 R の描画先(F の FallVisual・W の WindVisual と同じ作り)。判定の根元(transform)・着地の枠・床の印は動かさない。
//   ReverseVisual: 本体の描画先と、芯・縁・光の板・矢印・ひびを移した子。鏡門のノーツは門を通るまで x だけ負の拡縮にして、
//     本当の位置の左右の鏡像に見せる(矢印も鏡)。本体の MeshRenderer は子へ移し、切断のメッシュ・光の保護・当たりの矩形は
//     その子の本体の立方体を使う。
// 2026-10-28: 逆再生(割れた2片・破片・割れ目の光)はユーザーの「見た目が悪い」で外した。鏡のノーツだけがこの子を使う。
// プールは R の鏡のノーツとほかのノーツで分ける(NoteSpawner.Key)。出現のたびに ResetReverse で、ずれと負の拡縮を必ず戻す。
public partial class CuttableNote
{
    Transform reverseVisual;
    MeshRenderer reverseBody;

    // この出現が R の見た目(鏡)を使うか。
    public bool HasReverse { get; private set; }
    // 鏡門のノーツ(門を通るまで左右の鏡像の側に見える)。
    public bool IsMirror { get; private set; }

    // 本体の描画先を子へ移す(1回だけ)。再利用時に足された縁・矢印・ひびも毎回拾う。着地の枠は根元の兄弟なので含まれない。
    internal void EnsureReverseVisual()
    {
        if (reverseVisual == null)
        {
            var source = GetComponent<MeshRenderer>();
            var sourceMesh = GetComponent<MeshFilter>();
            var go = new GameObject("ReverseVisual", typeof(MeshFilter), typeof(MeshRenderer));
            go.layer = gameObject.layer;
            reverseVisual = go.transform;
            reverseVisual.SetParent(transform, false);
            reverseBody = go.GetComponent<MeshRenderer>();
            var bodyFilter = go.GetComponent<MeshFilter>();
            if (sourceMesh != null) bodyFilter.sharedMesh = sourceMesh.sharedMesh;
            if (source != null)
            {
                // メッシュと材質の所有者は従来のまま。描画先だけを子へ移す。
                CopyReverseRenderer(source, reverseBody);
                reverseBody.enabled = source.enabled;
                var properties = new MaterialPropertyBlock();
                source.GetPropertyBlock(properties);
                reverseBody.SetPropertyBlock(properties);
                var visuals = Visuals != null ? Visuals : GetComponent<NoteVisuals>();
                if (visuals != null) visuals.MoveBodyRenderer(source, reverseBody);
                source.enabled = false;
            }
            else reverseBody.enabled = false;
        }

        var children = new List<Transform>();
        foreach (Transform child in transform)
            if (child != reverseVisual && child.GetComponentInChildren<Renderer>(true) != null)
                children.Add(child);
        foreach (var child in children) child.SetParent(reverseVisual, false);
        reverseVisual.gameObject.SetActive(true);
    }

    static void CopyReverseRenderer(MeshRenderer source, MeshRenderer target)
    {
        target.sharedMaterials = source.sharedMaterials;
        target.shadowCastingMode = source.shadowCastingMode;
        target.receiveShadows = source.receiveShadows;
        target.lightProbeUsage = source.lightProbeUsage;
        target.reflectionProbeUsage = source.reflectionProbeUsage;
        target.probeAnchor = source.probeAnchor;
        target.motionVectorGenerationMode = source.motionVectorGenerationMode;
        target.renderingLayerMask = source.renderingLayerMask;
        target.sortingLayerID = source.sortingLayerID;
        target.sortingOrder = source.sortingOrder;
    }

    // 出現時に1回(NoteSpawner.PrepareReverseNote)。鏡は出現の時点で決め、飛行中に表を読み直さない。
    internal void ConfigureReverse(bool mirror)
    {
        EnsureReverseVisual();
        HasReverse = true;
        IsMirror = mirror;
    }

    // 毎コマ(NoteSpawner.ApplyReverse)。mirrorOffsetX・mirrorScaleX は鏡の見た目の横のずれ(ワールド)と拡縮。
    internal void SetReverse(float mirrorOffsetX, float mirrorScaleX)
    {
        if (!HasReverse || reverseVisual == null) return;
        // 鏡: 見た目の子だけを本当の x の鏡像へ。拡縮・傾きのある出現中でも、ずれはワールドの横方向にそろえる。
        reverseVisual.localPosition = mirrorOffsetX == 0f ? Vector3.zero
            : transform.InverseTransformVector(new Vector3(mirrorOffsetX, 0f, 0f));
        reverseVisual.localScale = new Vector3(mirrorScaleX, 1f, 1f);
    }

    // 出現のたびに(ResetForSpawn)。ずれと負の拡縮を戻す。子の作りはそのまま再利用する。
    internal void ResetReverse()
    {
        HasReverse = false;
        IsMirror = false;
        if (reverseVisual == null) return;
        reverseVisual.localPosition = Vector3.zero;
        reverseVisual.localScale = Vector3.one;
    }
}
