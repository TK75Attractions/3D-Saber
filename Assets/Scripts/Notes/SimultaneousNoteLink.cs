using UnityEngine;

// 同時押しノーツの間に張る明るい白線ガイド(プロセカの「同時線」相当)。
// NoteSpawner が同時刻(±SimultaneousEpsilonSeconds)のノーツペアを検出して生成する。
// 毎フレーム両ノーツの現在位置へ追従し、どちらかが消えた(カット/ミス/非アクティブ)瞬間に自分も消える。
// 小節線より太く明るい白線と、本体の控えめな発光で同時押しを知らせる。
[ExecuteAlways] // 編集中のプレビューでも破棄・非表示時に発光と材質を解放する。
public class SimultaneousNoteLink : MonoBehaviour
{
    public const float DefaultAlpha = 0.32f;
    public const float DefaultWidth = 0.13f;

    public CuttableNote noteA;
    public CuttableNote noteB;

    uint versionA, versionB;
    private LineRenderer line;
    private Material ownedMaterial;
    private NoteVisuals visualsA, visualsB;
    private bool glowApplied;
    float pairVeil;

    public LineRenderer Line => line;

    public static SimultaneousNoteLink Create(CuttableNote a, CuttableNote b, Transform parent)
    {
        if (!IsAlive(a) || !IsAlive(b) || a == b || a.HidesCues || b.HidesCues) return null;
        var go = new GameObject("SimulLink");
        if (parent != null) go.transform.SetParent(parent, false);
        var link = go.AddComponent<SimultaneousNoteLink>();
        link.noteA = a;
        link.noteB = b;
        link.versionA = a.SpawnVersion; link.versionB = b.SpawnVersion;
        link.visualsA = a.GetComponent<NoteVisuals>();
        link.visualsB = b.GetComponent<NoteVisuals>();
        link.pairVeil = Mathf.Max(a.Veil, b.Veil);
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

        var sh = Resources.Load<Shader>("Effects/NoteGuide") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        ownedMaterial = new Material(sh);
        Color c = new Color(1f, 1f, 1f, DefaultAlpha * NoteVeil.CueAlpha(pairVeil));
        // 半透明設定(URP Unlit)。対応プロパティが無いシェーダでは色のみ。
        if (ownedMaterial.HasProperty("_Surface")) ownedMaterial.SetFloat("_Surface", 1f);
        if (ownedMaterial.HasProperty("_SrcBlend")) ownedMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (ownedMaterial.HasProperty("_DstBlend")) ownedMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (ownedMaterial.HasProperty("_ZWrite")) ownedMaterial.SetFloat("_ZWrite", 0f);
        ownedMaterial.renderQueue = 3000;
        // 専用シェーダーで頂点色をそのまま使い、透明度を二重乗算しない。
        if (ownedMaterial.HasProperty("_BaseColor")) ownedMaterial.SetColor("_BaseColor", Color.white);
        else ownedMaterial.color = Color.white;
        line.sharedMaterial = ownedMaterial;
        line.startColor = c;
        line.endColor = c;
    }

    void LateUpdate()
    {
        // ノーツの移動(NoteSpawner.UpdateLive)が終わった後に追従させる
        if (!Refresh())
        {
            if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject);
        }
    }

    void OnDestroy()
    {
        ReleaseNoteGlow();
        if (ownedMaterial != null)
        {
            if (Application.isPlaying) Destroy(ownedMaterial);
            else DestroyImmediate(ownedMaterial);
            ownedMaterial = null;
        }
    }

    void OnDisable()
    {
        // エディタープレビューはコンポーネントだけ無効にしてRefreshを手動で呼ぶ。
        if (!gameObject.activeInHierarchy) ReleaseNoteGlow();
    }

    private void ApplyNoteGlow()
    {
        if (glowApplied || !gameObject.activeInHierarchy) return;
        if (visualsA != null) visualsA.AddSimultaneousLink(pairVeil);
        if (visualsB != null) visualsB.AddSimultaneousLink(pairVeil);
        glowApplied = true;
    }

    private void ReleaseNoteGlow()
    {
        if (!glowApplied) return;
        glowApplied = false;
        // 再利用後のノーツには、前の組のリンク解除を持ち越さない。
        if (noteA != null && noteA.SpawnVersion == versionA && visualsA != null) visualsA.RemoveSimultaneousLink(pairVeil);
        if (noteB != null && noteB.SpawnVersion == versionB && visualsB != null) visualsB.RemoveSimultaneousLink(pairVeil);
    }

    // 端点をノーツ現在位置へ更新する。ペアが両方生存していれば true(テストから直接呼べる)。
    public bool Refresh()
    {
        if (!IsAlive(noteA) || !IsAlive(noteB) || noteA.SpawnVersion != versionA || noteB.SpawnVersion != versionB)
        {
            if (line != null) line.enabled = false;
            ReleaseNoteGlow();
            return false;
        }
        float nextVeil = Mathf.Max(noteA.Veil, noteB.Veil);
        if (nextVeil != pairVeil)
        {
            ReleaseNoteGlow();
            pairVeil = nextVeil;
            if (line != null)
            {
                var color = new Color(1f, 1f, 1f, DefaultAlpha * NoteVeil.CueAlpha(pairVeil));
                line.startColor = line.endColor = color;
            }
        }
        if (pairVeil >= MemoryVeilLayers.HideCuesAtVeil)
        {
            if (line != null) line.enabled = false;
            ReleaseNoteGlow();
            return true;
        }
        ApplyNoteGlow();
        if (line != null)
        {
            Color linkColor = new Color(1f, 1f, 1f, DefaultAlpha * NoteVeil.CueAlpha(pairVeil));
            line.startColor = DarkRules.Rgb(linkColor, noteA.Brightness);
            line.endColor = DarkRules.Rgb(linkColor, noteB.Brightness);
            Vector3 a = noteA.VisualTransform.position, b = noteB.VisualTransform.position;
            Vector3 direction = b - a;
            float distance = direction.magnitude;
            float trimA = EdgeDistance(noteA, direction.normalized);
            float trimB = EdgeDistance(noteB, direction.normalized);
            line.enabled = distance > trimA + trimB + .01f;
            // 本体の矢印・残数の上を横切らず、輪郭間だけを結ぶ。
            line.SetPosition(0, a + direction.normalized * Mathf.Min(trimA, distance * .5f));
            line.SetPosition(1, b - direction.normalized * Mathf.Min(trimB, distance * .5f));
        }
        return true;
    }

    static float EdgeDistance(CuttableNote note, Vector3 direction)
    {
        Vector3 size = note.transform.lossyScale;
        float x = Mathf.Abs(direction.x) > .0001f ? Mathf.Abs(size.x) * .52f / Mathf.Abs(direction.x) : float.PositiveInfinity;
        float y = Mathf.Abs(direction.y) > .0001f ? Mathf.Abs(size.y) * .52f / Mathf.Abs(direction.y) : float.PositiveInfinity;
        return Mathf.Min(x, y, .8f);
    }

    private static bool IsAlive(CuttableNote n)
    {
        return n != null && !n.IsCut && !n.IsMissed && !n.IsFinalized && n.gameObject.activeInHierarchy;
    }
}
