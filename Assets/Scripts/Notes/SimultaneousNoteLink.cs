using UnityEngine;

// 同時押しノーツの間に張る細い白線ガイド(プロセカの「同時線」相当)。
// NoteSpawner が同時刻(±SimultaneousEpsilonSeconds)のノーツペアを検出して生成する。
// 毎フレーム両ノーツの現在位置へ追従し、どちらかが消えた(カット/ミス/非アクティブ)瞬間に自分も消える。
// 見た目は小節線(GameStageSkin.BarLineAlpha=0.12)より薄い白: 標準機能として常時有効。
public class SimultaneousNoteLink : MonoBehaviour
{
    // 小節線より控えめにする既定値(α 0.095 < 0.12、幅は小節線の太さ0.02より少し太い程度)
    public const float DefaultAlpha = 0.095f;
    public const float DefaultWidth = 0.04f;

    public CuttableNote noteA;
    public CuttableNote noteB;

    private LineRenderer line;
    private Material ownedMaterial;

    public LineRenderer Line => line;

    public static SimultaneousNoteLink Create(CuttableNote a, CuttableNote b, Transform parent)
    {
        var go = new GameObject("SimulLink");
        if (parent != null) go.transform.SetParent(parent, false);
        var link = go.AddComponent<SimultaneousNoteLink>();
        link.noteA = a;
        link.noteB = b;
        link.BuildLine();
        link.Refresh();
        return link;
    }

    private void BuildLine()
    {
        line = gameObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = DefaultWidth;
        line.endWidth = DefaultWidth;
        line.numCapVertices = 2;
        line.alignment = LineAlignment.View;

        var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        ownedMaterial = new Material(sh);
        Color c = new Color(1f, 1f, 1f, DefaultAlpha);
        // 半透明設定(URP Unlit)。対応プロパティが無いシェーダでは色のみ。
        if (ownedMaterial.HasProperty("_Surface")) ownedMaterial.SetFloat("_Surface", 1f);
        if (ownedMaterial.HasProperty("_SrcBlend")) ownedMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (ownedMaterial.HasProperty("_DstBlend")) ownedMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (ownedMaterial.HasProperty("_ZWrite")) ownedMaterial.SetFloat("_ZWrite", 0f);
        ownedMaterial.renderQueue = 3000;
        if (ownedMaterial.HasProperty("_BaseColor")) ownedMaterial.SetColor("_BaseColor", c);
        else ownedMaterial.color = c;
        line.sharedMaterial = ownedMaterial;
        line.startColor = c;
        line.endColor = c;
    }

    void LateUpdate()
    {
        // ノーツの移動(NoteSpawner.UpdateLive)が終わった後に追従させる
        if (!Refresh())
        {
            Destroy(gameObject);
        }
    }

    void OnDestroy()
    {
        if (ownedMaterial != null)
        {
            if (Application.isPlaying) Destroy(ownedMaterial);
            else DestroyImmediate(ownedMaterial);
            ownedMaterial = null;
        }
    }

    // 端点をノーツ現在位置へ更新する。ペアが両方生存していれば true(テストから直接呼べる)。
    public bool Refresh()
    {
        if (!IsAlive(noteA) || !IsAlive(noteB)) return false;
        if (line != null)
        {
            line.SetPosition(0, noteA.transform.position);
            line.SetPosition(1, noteB.transform.position);
        }
        return true;
    }

    private static bool IsAlive(CuttableNote n)
    {
        return n != null && !n.IsCut && !n.IsMissed && n.gameObject.activeInHierarchy;
    }
}
