using System.Collections.Generic;
using UnityEngine;

// 方向矢印が作った材質だけを所有する。本体や別ノーツの共有材質には触れない。
public sealed class NoteArrowMaterials : MonoBehaviour
{
    readonly List<Material> owned = new List<Material>();

    internal void Register(Material material)
    {
        owned.Add(material);
    }

    void OnDestroy()
    {
        // 切断・ミス回収・譜面リセット・選曲の再生成・シーン退出で共通して解放する。
        foreach (var material in owned)
        {
            if (material == null) continue;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }
        owned.Clear();
    }
}
