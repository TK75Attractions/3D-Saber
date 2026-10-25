using System.Collections.Generic;
using UnityEngine;

// 剣に属する全Rendererの描画順を一時的に変える。
// 共用Materialは変更せず複製し、元の材質の色などはRefreshで追従する。
// 自分のUpdateは持たず、入力側とMemoryVeilModeから更新する。
[DisallowMultipleComponent]
public sealed class SaberMemoryVeilOrder : MonoBehaviour
{
    sealed class Saved
    {
        public int Layer;
        public int Order;
        public Material[] Sources;
        public Material[] Copies;
    }

    readonly Dictionary<Renderer, Saved> saved = new Dictionary<Renderer, Saved>();
    readonly List<Renderer> renderers = new List<Renderer>();
    public bool Above { get; private set; }

    public static bool IsAbove(Component owner)
    {
        var order = owner != null ? owner.GetComponent<SaberMemoryVeilOrder>() : null;
        return order != null && order.Above;
    }

    public static void Set(Component owner, bool above)
    {
        if (owner == null) return;
        var order = owner.GetComponent<SaberMemoryVeilOrder>();
        if (order == null)
        {
            if (!above) return;
            order = owner.gameObject.AddComponent<SaberMemoryVeilOrder>();
            order.hideFlags = HideFlags.DontSave;
        }
        order.Above = above;
        if (above) order.Refresh();
        else order.Restore();
    }

    // 子の刃・縁・残光が後から生成されたときも、その場で適用する。
    public static void RefreshFor(Component part)
    {
        if (part == null) return;
        var order = part.GetComponentInParent<SaberMemoryVeilOrder>(true);
        if (order != null) order.Refresh();
    }

    public void Refresh()
    {
        if (!Above) return;
        renderers.Clear();
        GetComponentsInChildren<Renderer>(true, renderers);
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            if (!saved.TryGetValue(renderer, out var state))
            {
                state = new Saved { Layer = renderer.sortingLayerID, Order = renderer.sortingOrder };
                saved.Add(renderer, state);
            }

            var assigned = renderer.sharedMaterials;
            if (!Same(assigned, state.Copies))
            {
                // 一部の材質だけが差し替わった場合、残った複製を元の材質へ戻してから作り直す。
                Unwrap(assigned, state);
                ReleaseCopies(state);
                state.Sources = assigned;
                state.Copies = new Material[assigned.Length];
                for (int i = 0; i < assigned.Length; i++)
                {
                    if (assigned[i] == null) continue;
                    state.Copies[i] = new Material(assigned[i])
                    {
                        name = assigned[i].name + " / MemoryVeil",
                        hideFlags = HideFlags.DontSave
                    };
                }
                renderer.sharedMaterials = state.Copies;
            }

            for (int i = 0; i < state.Copies.Length; i++)
            {
                var source = state.Sources[i];
                var copy = state.Copies[i];
                if (source == null || copy == null) continue;
                if (copy.shader != source.shader) copy.shader = source.shader;
                copy.CopyPropertiesFromMaterial(source);
                copy.renderQueue = MemoryVeilLayers.AboveRenderQueue;
                if (copy.HasProperty("_ZWrite")) copy.SetFloat("_ZWrite", 0f);
            }

            renderer.sortingLayerID = 0;
            renderer.sortingOrder = MemoryVeilLayers.AboveSortingOrder;
        }
    }

    void Restore()
    {
        foreach (var pair in saved)
        {
            var renderer = pair.Key;
            var state = pair.Value;
            if (renderer != null)
            {
                var assigned = renderer.sharedMaterials;
                Unwrap(assigned, state);
                renderer.sharedMaterials = assigned;
                renderer.sortingLayerID = state.Layer;
                renderer.sortingOrder = state.Order;
            }
            ReleaseCopies(state);
        }
        saved.Clear();
        renderers.Clear();
    }

    static bool Same(Material[] a, Material[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static void Unwrap(Material[] assigned, Saved state)
    {
        if (state.Copies == null || state.Sources == null) return;
        for (int i = 0; i < assigned.Length; i++)
        {
            if (assigned[i] == null) continue;
            for (int j = 0; j < state.Copies.Length; j++)
            {
                if (assigned[i] != state.Copies[j]) continue;
                assigned[i] = state.Sources[j];
                break;
            }
        }
    }

    static void ReleaseCopies(Saved state)
    {
        if (state.Copies == null) return;
        foreach (var material in state.Copies)
            if (material != null) UISkinKit.SafeDestroy(material);
        state.Copies = null;
    }

    void OnDestroy()
    {
        Above = false;
        Restore();
    }
}
