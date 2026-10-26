using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public partial class NoteVisuals
{
    public float Veil { get; private set; }

    struct VeilMaterialState
    {
        public Material material;
        public float alpha;
    }

    struct VeilRendererState
    {
        public MeshRenderer renderer;
        public bool opaque;
        public bool enabled;
        public ShadowCastingMode shadows;
    }

    // 通常譜面では確保しない。初めて見えにくくする時だけ、復元用の値を保存する。
    VeilMaterialState[] veilMaterials;
    VeilRendererState[] veilRenderers;
    List<float> simultaneousVeils;
    float originalBodyZWrite;
    MeshRenderer contrastOutlineRenderer;

    public void SetVeil(float veil)
    {
        Initialize();
        veil = NoteVeil.Clamp(veil);
        if (Veil == veil) return;
        if (veilMaterials == null) CacheVeilState();
        Veil = veil;

        float visibility = 1f - veil;
        foreach (var state in veilMaterials)
        {
            if (state.material == null) continue;
            // 投影モードの alpha 1 も、最後に見える量を掛ける。
            float alpha = state.material == runtimeBodyMat
                ? (DisplaySettings.ProjectorMode ? 1f : .85f)
                : state.alpha;
            NoteVeil.SetAlpha(state.material, alpha * visibility);
        }
        foreach (var state in veilRenderers)
        {
            if (state.renderer == null) continue;
            bool hidden = veil >= 1f || (state.opaque && veil >= MemoryVeilLayers.HideCuesAtVeil);
            state.renderer.enabled = state.enabled && !hidden;
            state.renderer.shadowCastingMode = veil > 0f ? ShadowCastingMode.Off : state.shadows;
        }

        // 薄い面の深度や影だけで、後ろのノーツ・光をくり抜かない。
        if (runtimeBodyMat != null && runtimeBodyMat.HasProperty(NoteVeil.ZWriteId))
            runtimeBodyMat.SetFloat(NoteVeil.ZWriteId, veil > 0f ? 0f : originalBodyZWrite);
        ApplyContrastVeil();
        lastAppliedStrength = -1f;
        ApplyCurrentEmission();
        if (bodyDimmed) ApplyEmissionStrength(runtimeBodyMat, .2f);
    }

    // 風の描画用の子へ本体を移したあとも、消える・見えにくい設定の復元先をそろえる。
    internal void MoveBodyRenderer(MeshRenderer before, MeshRenderer after)
    {
        if (veilRenderers == null) return;
        for (int i = 0; i < veilRenderers.Length; i++)
        {
            var state = veilRenderers[i];
            if (state.renderer != before) continue;
            state.renderer = after;
            veilRenderers[i] = state;
        }
    }

    void CacheVeilState()
    {
        veilMaterials = new VeilMaterialState[ownedSubMaterials.Count + 1];
        int index = 0;
        if (runtimeBodyMat != null)
        {
            veilMaterials[index++] = new VeilMaterialState {
                material = runtimeBodyMat, alpha = ReadBaseColor(runtimeBodyMat).a
            };
            if (runtimeBodyMat.HasProperty(NoteVeil.ZWriteId))
                originalBodyZWrite = runtimeBodyMat.GetFloat(NoteVeil.ZWriteId);
        }
        foreach (var material in ownedSubMaterials)
        {
            if (!NoteVeil.IsAlphaBlended(material)) continue;
            veilMaterials[index++] = new VeilMaterialState {
                material = material, alpha = ReadBaseColor(material).a
            };
        }

        // Initialize の結合描画が終わった後に集める。矢印・ひび・タイミング枠は各所有者が扱う。
        var renderers = GetComponentsInChildren<MeshRenderer>(true);
        veilRenderers = new VeilRendererState[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            var material = renderer.sharedMaterial;
            if (material == null || (material != runtimeBodyMat && !ownedSubMaterials.Contains(material))) continue;
            veilRenderers[i] = new VeilRendererState {
                renderer = renderer,
                opaque = material != runtimeBodyMat && !NoteVeil.IsAlphaBlended(material),
                enabled = renderer.enabled,
                shadows = renderer.shadowCastingMode
            };
        }
    }

    void ApplyContrastVeil()
    {
        if (contrastOutlineMaterial == null) return;
        NoteVeil.SetAlpha(contrastOutlineMaterial, ContrastOutlineColor.a * (1f - Veil));
        if (contrastOutlineRenderer != null) contrastOutlineRenderer.enabled = Veil < 1f;
    }
}
